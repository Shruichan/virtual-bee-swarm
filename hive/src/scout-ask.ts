// Ask Beeswax for Live Bee Scout (branch scout/ask): voice follow-up questions on the glasses.
//
//   glasses final transcript --POST /scout/utterance--> routeUtterance(): echo | game | wake | ask
//   (wake word only: "Hey Beeswax ..." is answered; a bare "Hey Beeswax" gets scout_wake_ack, then the next utterance)
//   ask: game.interrupt() + beginAnswer() -> Claude tokens (streamAnswer) -> sentences -> ElevenLabs stream-input
//        websocket (Beeswax voice, mp3) -> AskAudio, served progressively at GET /scout/ask/<id>.mp3
//   the bus `say` (tts_id scout_ask_<id>) goes out as soon as the first audio bytes exist; endAnswer() when done.
//
// Stable exports for the persona-eval loop (scout/eval): answerText(question, ctx), streamAnswer, setAskClient.
import Anthropic from "@anthropic-ai/sdk";
import type { Context, Hono } from "hono";
import { log } from "./bus";
import { askContextFor, buildAskMessages, cardFacts, SCOUT_ASK_SYSTEM, type AskContext, type AskFact } from "./scout-ask-prompt";
import type { ScoutAnswer, ScoutEvent, ScoutLabel, ScoutState } from "./scout-protocol";
import { TTS_MODEL, ttsBytes } from "./tts";

export type { AskContext, AskFact } from "./scout-ask-prompt";
export { askContextFor, buildAskMessages, SCOUT_ASK_SYSTEM } from "./scout-ask-prompt";

// ---------------------------------------------------------------- routing

const norm = (s: string) =>
  String(s ?? "")
    .toLowerCase()
    .replace(/[’']/g, "'")
    .replace(/[^a-z0-9' ]+/g, " ")
    .replace(/\s+/g, " ")
    .trim();

/** Fillers and wake words in front of a question ("um so beeswax why ..."). */
const LEAD_RX = /^(?:(?:um+|uh+|hm+|so|okay|ok|hey|hi|oh|wait|and|but|well|beeswax|bees wax|bee wax|mister beeswax|mr beeswax)\s+)+/;
/** Starts like a question or a request for more. */
const START_RX =
  /^(?:why|how|what|what's|whats|who|who's|where|where's|when|which|whose|is|are|am|do|does|did|can|could|would|will|should|was|were|have|has|tell me|explain|i wonder|i want to know|i wanna know)\b/;
/** A question or request anywhere ("so the pebbles are for what", "and tell me about honey"). */
const ANY_RX =
  /\b(?:why|how come|how do|how does|how did|how many|how much|how long|how big|what is|what's|what are|what do|what does|what happens|what if|tell me (?:more )?about|tell me why|explain|i wonder)\b|\b(?:for what|what for)$/;

/** Is this transcript a question or a request for more info? Two words minimum ("why though"), three for most. */
export function looksLikeQuestion(text: string): boolean {
  const raw = String(text ?? "");
  const t = norm(raw).replace(LEAD_RX, "");
  const words = t ? t.split(" ").length : 0;
  if (words < 2) return false;
  if (raw.includes("?")) return true;
  if (/^(?:why|how come)\b/.test(t)) return true;
  if (words < 3) return false;
  return START_RX.test(t) || ANY_RX.test(t);
}

const bigrams = (t: string) => {
  const w = t.split(" ");
  const out: string[] = [];
  for (let i = 0; i + 1 < w.length; i++) out.push(`${w[i]} ${w[i + 1]}`);
  return out;
};

// ---- wake word: Beeswax only answers (and only cuts himself off) when he is called by name

const SEP = "[\\s,.!?;:\\-–—]+";
/** "Beeswax" as speech-to-text writes it: beeswax, bees wax, bee's wax, bee wax, beezwax, b's wax, bees whacks... */
const NAME = "b(?:ee|e)?(?:['’]?s|z)?[\\s\\-]*(?:wax|wacks|whacks|wax's)";
/** Filler allowed before the wake phrase ("um, okay, so hey beeswax ..."). */
const LEAD = "(?:(?:um+|uh+|hm+|mm+|ah+|er+|oh+|okay|ok|so|well|and|wait)" + SEP + ")*";
/** The wake phrase: a greeting + the name, only at the START of the utterance (after fillers). The bare name never
 *  wakes him: STT hears his own chime "Bee fact found!" as "Beeswax found!", and "... what Beeswax was saying" is chatter. */
const GREETING = "(?:hey|hay|hi|hello|okay|ok|a|yo)";
export const WAKE_RX = new RegExp(
  `^[\\s,.!?;:\\-–—]*${LEAD}(?:${GREETING}${SEP}${NAME}|(?:hey|hay)${SEP}bees?)(?=$|[\\s,.!?;:\\-–—])`,
  "i",
);
/** An explicit call ("hey beeswax") anywhere: only his tip lines contain one ("Just say Hey Beeswax"). */
const CALL_RX = new RegExp(`\\b${GREETING}${SEP}${NAME}\\b|\\b(?:hey|hay)${SEP}bees?\\b`, "i");
const EDGE_RX = /^[\s,.!?;:\-–—]+|[\s,.;:\-–—]+$/g;
const FILLER_ONLY_RX = /^(?:(?:um+|uh+|hm+|mm+|ah+|er+|oh+|huh|so|well|hey|hi|hello|okay|ok|please|there)\s*)*$/;

/** Finds the wake phrase. `rest` = the utterance without it (what goes to Claude); "" when it was only the call. */
export function parseWake(text: string): { wake: boolean; rest: string } {
  const raw = String(text ?? "");
  const m = WAKE_RX.exec(raw);
  if (!m) return { wake: false, rest: raw.trim() };
  const rest = raw.slice(m.index + m[0].length).replace(EDGE_RX, "").trim();
  return { wake: true, rest: FILLER_ONLY_RX.test(norm(rest)) ? "" : rest };
}

/** His chime as STT writes it ("Beeswax found", "Be fact found", "Bee facts found") -> one spelling, on both sides. */
const echoNorm = (s: string) => norm(s).replace(/\b(?:bee'?s ?wax|bees ?wax|bee ?facts?|be ?facts?|b ?facts?) found\b/g, "bee fact found");

/** The mic heard Beeswax himself (from the glasses or from the Mac's speakers, whenever it played): the transcript
 *  is inside something he said, shares a run of 4+ words with it, or most of its words (60%+) are his. An explicit
 *  "hey beeswax" his line doesn't contain is the wearer, never an echo. Anything starting "beeswax found" is his chime. */
export function isEcho(text: string, captions: readonly string[]): boolean {
  const t = echoNorm(text);
  if (/^(?:bee'?s ?wax|bees ?wax) found\b/.test(norm(text))) return true;
  const ws = t ? t.split(" ") : [];
  const n = ws.length;
  if (n < 2) return false;
  const call = CALL_RX.test(text);
  for (const cap of captions) {
    const c = echoNorm(cap);
    if (!c) continue;
    if (call && !CALL_RX.test(cap)) continue;
    if (` ${c} `.includes(` ${t} `)) return true;
    for (let i = 0; i + 4 <= n; i++) if (` ${c} `.includes(` ${ws.slice(i, i + 4).join(" ")} `)) return true;
    if (n >= 3) {
      const words = new Set(c.split(" "));
      if (ws.filter((w) => words.has(w)).length / n >= 0.6) return true;
    }
  }
  return false;
}

/** echo: Beeswax heard himself. game: not for Ask (yes/no, chatter: the game reads it, the judge screen shows it).
 *  wake: only the name: acknowledge and take the next utterance as the question. ask: answer `question`. */
export type AskRoute =
  | { kind: "echo" }
  | { kind: "game"; text: string }
  | { kind: "wake" }
  | { kind: "ask"; question: string };

/** Never a question, even right after "Hey Beeswax": thanks, okay, never mind. */
const NOT_A_QUESTION_RX =
  /^(?:(?:um+|uh+|hm+|mm+|ah+|er+|oh+|huh|so|well)\s*)*(?:thanks|thank you|ok(?:ay)?|cool|nice|got it|i'?m good|all good|never ?mind|nothing|no questions?|not really|that'?s all|bye|goodbye)?(?: (?:thanks|thank you))?$/;

/** Where a final transcript goes. Wake-word only: without "Hey Beeswax" (or within `awaitingQuestion`, the few
 *  seconds after a bare "Hey Beeswax") nothing is answered. `classify` = the game's yes/no/rude reading. */
export function routeUtterance(
  text: string,
  o: { classify: (t: string) => ScoutAnswer; captions: readonly string[]; playing?: readonly string[]; awaitingQuestion?: boolean },
): AskRoute {
  const { wake, rest } = parseWake(text);
  // `captions`: everything he said in the last ~30 s (the Mac plays it later than the glasses). A bare "Hey Beeswax"
  // is checked only against what is playing now (`playing`): his tips say "Just say Hey Beeswax", and that is exactly
  // when a kid says it back; an echo of a tip comes with its other words ("just say...") and is caught above.
  if (isEcho(text, wake && !rest ? (o.playing ?? o.captions) : o.captions)) return { kind: "echo" };
  if (!wake) {
    const t = norm(text);
    if (o.awaitingQuestion && t && !NOT_A_QUESTION_RX.test(t) && !FILLER_ONLY_RX.test(t)) return { kind: "ask", question: text.trim() };
    return { kind: "game", text: text.trim() };
  }
  if (!rest) return { kind: "wake" };
  // "Hey Beeswax, yes!" during an offer is still the game's yes
  if (o.classify(rest) !== "other" && norm(rest).split(" ").length <= 4 && !looksLikeQuestion(rest)) return { kind: "game", text: rest };
  return { kind: "ask", question: rest };
}

// ---------------------------------------------------------------- spoken text

/** Words in a spoken answer, at most (the prompt asks for 35). */
export const MAX_ANSWER_WORDS = 40;

/** Spoken text only: no emoji, stage directions, quotes or markdown. */
export function cleanSpoken(s: unknown): string {
  if (typeof s !== "string") return "";
  return s
    .replace(/\*[^*]{0,60}\*|\([^)]{0,80}\)|\[[^\]]{0,80}\]/g, " ")
    .replace(/https?:\/\/\S+/g, " ")
    .replace(/\p{Extended_Pictographic}|[\u{FE0F}\u{200D}]/gu, "")
    .replace(/["“”#_*`~<>]/g, "")
    .replace(/\s+/g, " ")
    .trim();
}

/** At most `max` words, cut on a sentence end when there is one. */
export function clipWords(s: string, max = MAX_ANSWER_WORDS): string {
  const words = s.split(" ").filter(Boolean);
  if (words.length <= max) return words.join(" ");
  const sentences = s.split(/(?<=[.!?])\s+/);
  let out = "";
  for (const x of sentences) {
    const next = out ? `${out} ${x}` : x;
    if (next.split(" ").length > max) break;
    out = next;
  }
  if (out) return out;
  const cut = words.slice(0, max).join(" ").replace(/[\s,;:\-–—]+$/, "");
  return /[.!?]$/.test(cut) ? cut : `${cut}.`;
}

/** Splits streamed text into sentences as soon as each one ends (the TTS gets whole sentences). */
export class SentenceSplitter {
  private buf = "";
  push(delta: string): string[] {
    this.buf += delta;
    return this.drain(false);
  }
  end(): string[] {
    return this.drain(true);
  }
  private drain(final: boolean): string[] {
    const out: string[] = [];
    const re = /[.!?]+["'”’)\]]*\s+/g;
    let start = 0;
    for (let m = re.exec(this.buf); m; m = re.exec(this.buf)) {
      const s = this.buf.slice(start, m.index + m[0].length).trim();
      if (s) out.push(s);
      start = m.index + m[0].length;
    }
    this.buf = this.buf.slice(start);
    // a long run with no sentence end: cut at the last comma so the voice can start
    if (!final && this.buf.length > 140) {
      const cut = this.buf.lastIndexOf(", ");
      if (cut > 60) {
        out.push(this.buf.slice(0, cut + 1).trim());
        this.buf = this.buf.slice(cut + 2);
      }
    }
    if (final && this.buf.trim()) {
      out.push(this.buf.trim());
      this.buf = "";
    }
    return out;
  }
}

// ---------------------------------------------------------------- Claude

export const DEFAULT_ASK_MODEL = "claude-haiku-5-5";
export const askModel = () => process.env.SCOUT_ASK_MODEL?.trim() || DEFAULT_ASK_MODEL;
/** Claude must start talking within this (ms), or the answer falls back to the not-sure line. */
export const askLimits = { firstTokenMs: 4000, totalMs: 10_000 };
const MAX_TOKENS = 160;

/** The slice of the Anthropic client Ask Beeswax uses (tests inject a fake). */
export interface AskClient {
  messages: {
    create(
      body: Anthropic.MessageCreateParamsStreaming,
      options?: { signal?: AbortSignal; timeout?: number; maxRetries?: number },
    ): PromiseLike<AsyncIterable<Anthropic.RawMessageStreamEvent>>;
  };
}

let client: AskClient | null = null;

/** Swap the API client (tests, evals); null goes back to a real client from env credentials (or the ant profile). */
export function setAskClient(c: AskClient | null): void {
  client = c;
}

function getClient(): AskClient {
  return (client ??= new Anthropic() as unknown as AskClient);
}

export function askRequest(question: string, ctx: AskContext, model = askModel()): Anthropic.MessageCreateParamsStreaming {
  return {
    model,
    max_tokens: MAX_TOKENS,
    system: [{ type: "text", text: SCOUT_ASK_SYSTEM, cache_control: { type: "ephemeral" } }],
    messages: buildAskMessages(question, ctx),
    // Haiku 5.5: thinking off is allowed at effort low; first token fastest.
    ...(model.startsWith("claude-haiku") ? { thinking: { type: "disabled" as const } } : {}),
    output_config: { effort: "low" },
    stream: true,
  };
}

/** Claude's answer as text deltas, raw (clean them with cleanSpoken). Throws on API errors, refusals, timeouts. */
export async function* streamAnswer(question: string, ctx: AskContext, opts: { signal?: AbortSignal; model?: string } = {}): AsyncGenerator<string> {
  const ac = new AbortController();
  const onAbort = () => ac.abort();
  opts.signal?.addEventListener("abort", onAbort);
  let firstTimer: ReturnType<typeof setTimeout> | undefined = setTimeout(() => ac.abort(), askLimits.firstTokenMs);
  const total = setTimeout(() => ac.abort(), askLimits.totalMs);
  try {
    const stream = await getClient().messages.create(askRequest(question, ctx, opts.model), { signal: ac.signal, maxRetries: 1, timeout: askLimits.totalMs });
    for await (const ev of stream) {
      if (ac.signal.aborted) throw new Error(opts.signal?.aborted ? "aborted" : "Claude timed out");
      if (ev.type === "content_block_delta" && ev.delta.type === "text_delta" && ev.delta.text) {
        if (firstTimer) clearTimeout(firstTimer), (firstTimer = undefined);
        yield ev.delta.text;
      } else if (ev.type === "message_delta" && ev.delta.stop_reason === "refusal") {
        throw new Error("refusal");
      }
    }
    if (ac.signal.aborted) throw new Error(opts.signal?.aborted ? "aborted" : "Claude timed out");
  } finally {
    clearTimeout(firstTimer);
    clearTimeout(total);
    opts.signal?.removeEventListener("abort", onAbort);
  }
}

/** Beeswax's whole answer to `question` (cleaned, at most MAX_ANSWER_WORDS words). Throws on failure. */
export async function answerText(question: string, ctx: AskContext): Promise<string> {
  let raw = "";
  for await (const d of streamAnswer(question, ctx)) raw += d;
  const out = clipWords(cleanSpoken(raw));
  if (!out) throw new Error("empty answer");
  return out;
}

/** Open the HTTPS connection (and cache the system prompt where the model allows) before the first question. */
export async function prewarmAsk(): Promise<void> {
  try {
    const stream = await getClient().messages.create({ ...askRequest("hi", askContextFor(null)), max_tokens: 1 }, { maxRetries: 0, timeout: 10_000 });
    for await (const _ of stream) {
    }
  } catch (e) {
    log(`scout ask: prewarm failed: ${e instanceof Error ? e.message : e}`);
  }
}

/** Said when Claude fails or times out (in Beeswax's voice when TTS works). */
export const ASK_FALLBACK = "Bzzt, my bee brain just buzzed out. Ask me again, or go find another card!";

// ---------------------------------------------------------------- small async helpers

/** An async queue: push/end/fail on one side, for-await on the other. */
export class AsyncQueue<T> implements AsyncIterable<T> {
  private items: T[] = [];
  private waiting: ((r: IteratorResult<T>) => void) | null = null;
  private failWait: ((e: unknown) => void) | null = null;
  private closed = false;
  private error: unknown = null;
  push(v: T): void {
    if (this.closed) return;
    if (this.waiting) {
      const w = this.waiting;
      this.waiting = this.failWait = null;
      w({ value: v, done: false });
    } else this.items.push(v);
  }
  end(): void {
    if (this.closed) return;
    this.closed = true;
    if (this.waiting) {
      const w = this.waiting;
      this.waiting = this.failWait = null;
      w({ value: undefined as never, done: true });
    }
  }
  fail(e: unknown): void {
    if (this.closed) return;
    this.error = e ?? new Error("failed");
    this.closed = true;
    if (this.failWait) {
      const f = this.failWait;
      this.waiting = this.failWait = null;
      f(this.error);
    }
  }
  get isClosed(): boolean {
    return this.closed;
  }
  [Symbol.asyncIterator](): AsyncIterator<T> {
    return {
      next: () => {
        if (this.items.length) return Promise.resolve({ value: this.items.shift()!, done: false });
        if (this.error) return Promise.reject(this.error);
        if (this.closed) return Promise.resolve({ value: undefined as never, done: true });
        return new Promise((resolve, reject) => {
          this.waiting = resolve;
          this.failWait = reject;
        });
      },
      return: () => {
        this.end();
        return Promise.resolve({ value: undefined as never, done: true });
      },
    };
  }
}

function withTimeout<T>(p: Promise<T>, ms: number, what: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  return Promise.race([
    p,
    new Promise<never>((_, reject) => {
      timer = setTimeout(() => reject(new Error(`${what} timed out after ${ms} ms`)), ms);
    }),
  ]).finally(() => clearTimeout(timer));
}

// ---------------------------------------------------------------- ElevenLabs streaming TTS

/** Sentences in, mp3 bytes out (one continuous mp3 stream). Ends early (no throw needed) when `signal` aborts. */
export type StreamTts = (sentences: AsyncIterable<string>, signal: AbortSignal) => AsyncIterable<Uint8Array>;

/** mp3_44100_64: 64 kbit/s = 8 bytes per ms. */
export const MP3_BYTES_PER_MS = 8;
export const ASK_FORMAT = "mp3_44100_64";

export function elevenAvailable(): boolean {
  return Boolean(process.env.ELEVENLABS_API_KEY?.trim() && process.env.ELEVENLABS_VOICE_ID?.trim());
}

interface Conn {
  ws: WebSocket;
  ready: Promise<void>;
  audio: AsyncQueue<Uint8Array>;
  openedAt: number;
  dead: boolean;
}

/** A spare websocket older than this is replaced (the server closes idle ones at inactivity_timeout=180 s). */
const SPARE_MAX_AGE_MS = 150_000;
let spare: Conn | null = null;

function connect(): Conn {
  const key = process.env.ELEVENLABS_API_KEY!.trim();
  const voice = process.env.ELEVENLABS_VOICE_ID!.trim();
  const url =
    `wss://api.elevenlabs.io/v1/text-to-speech/${encodeURIComponent(voice)}/stream-input` +
    `?model_id=${TTS_MODEL}&output_format=${ASK_FORMAT}&auto_mode=true&inactivity_timeout=180`;
  const ws = new WebSocket(url);
  const audio = new AsyncQueue<Uint8Array>();
  const conn: Conn = { ws, audio, openedAt: Date.now(), dead: false, ready: Promise.resolve() };
  conn.ready = new Promise<void>((resolve, reject) => {
    ws.onopen = () => {
      try {
        // BOS: a single space, the key (never logged), default voice settings (same as the pre-rendered lines)
        ws.send(JSON.stringify({ text: " ", xi_api_key: key }));
        resolve();
      } catch (e) {
        reject(e);
      }
    };
    ws.onerror = () => {
      conn.dead = true;
      reject(new Error("ElevenLabs websocket error"));
      audio.fail(new Error("ElevenLabs websocket error"));
    };
  });
  conn.ready.catch(() => {});
  ws.onmessage = (ev) => {
    let m: Record<string, unknown>;
    try {
      m = JSON.parse(typeof ev.data === "string" ? ev.data : new TextDecoder().decode(ev.data as ArrayBuffer));
    } catch {
      return;
    }
    if (typeof m.audio === "string" && m.audio) audio.push(new Uint8Array(Buffer.from(m.audio, "base64")));
    if (m.isFinal === true) audio.end();
    if (m.error || (m.message && !m.audio && m.isFinal !== true && m.alignment === undefined)) audio.fail(new Error(`ElevenLabs: ${String(m.error ?? m.message).slice(0, 200)}`));
  };
  ws.onclose = (ev) => {
    conn.dead = true;
    if (ev.code === 1000 || audio.isClosed) audio.end();
    else audio.fail(new Error(`ElevenLabs websocket closed ${ev.code} ${String(ev.reason ?? "").slice(0, 120)}`));
  };
  return conn;
}

/** Keep one connected websocket ready (saves the TLS + websocket handshake on the next question). */
export function warmTts(): void {
  if (!elevenAvailable()) return;
  if (spare && !spare.dead && Date.now() - spare.openedAt < SPARE_MAX_AGE_MS) return;
  if (spare) closeQuietly(spare.ws);
  try {
    spare = connect();
  } catch (e) {
    spare = null;
    log(`scout ask: tts warm failed: ${e instanceof Error ? e.message : e}`);
  }
}

function takeConn(): Conn {
  let c = spare;
  spare = null;
  if (!c || c.dead || Date.now() - c.openedAt >= SPARE_MAX_AGE_MS) {
    if (c) closeQuietly(c.ws);
    c = connect();
  }
  setTimeout(warmTts, 0); // the next question gets a fresh spare
  return c;
}

function closeQuietly(ws: WebSocket): void {
  try {
    ws.close();
  } catch {}
}

/** ElevenLabs stream-input websocket, Beeswax's voice (ELEVENLABS_VOICE_ID), Flash v2.5, auto_mode, one flush per sentence. */
export const elevenTts: StreamTts = async function* (sentences, signal) {
  if (!elevenAvailable()) throw new Error("TTS unavailable: set ELEVENLABS_API_KEY and ELEVENLABS_VOICE_ID");
  const conn = takeConn();
  const stop = () => {
    closeQuietly(conn.ws);
    conn.audio.end();
  };
  signal.addEventListener("abort", stop);
  try {
    await withTimeout(conn.ready, 4000, "ElevenLabs connect");
    void (async () => {
      for await (const s of sentences) {
        if (signal.aborted || conn.dead) return;
        conn.ws.send(JSON.stringify({ text: `${s} `, flush: true }));
      }
      if (!signal.aborted && !conn.dead) conn.ws.send(JSON.stringify({ text: "" })); // EOS
    })().catch((e) => conn.audio.fail(e));
    const it = conn.audio[Symbol.asyncIterator]();
    while (true) {
      const r = await withTimeout(it.next(), 8000, "ElevenLabs audio");
      if (r.done) return;
      yield r.value;
    }
  } finally {
    signal.removeEventListener("abort", stop);
    closeQuietly(conn.ws);
  }
};

/** Sentence mode: one whole mp3 per sentence (HTTP), for players that can't play a growing file. */
export const renderSentenceMp3 = (text: string): Promise<Uint8Array> => ttsBytes(text, { format: ASK_FORMAT, timeoutMs: 8000 });

// ---------------------------------------------------------------- progressive audio

/** One answer's mp3, growing while ElevenLabs talks. Any number of readers, each from byte 0. */
export class AskAudio {
  readonly chunks: Uint8Array[] = [];
  bytes = 0;
  done = false;
  firstServedAt: number | null = null;
  private wake = new Set<() => void>();

  constructor(
    readonly id: string,
    private onFirstServed?: (at: number) => void,
    private now: () => number = Date.now,
  ) {}

  push(c: Uint8Array): void {
    if (this.done || !c.length) return;
    this.chunks.push(c);
    this.bytes += c.length;
    this.ping();
  }
  finish(): void {
    if (this.done) return;
    this.done = true;
    this.ping();
  }
  private ping(): void {
    const w = [...this.wake];
    this.wake.clear();
    for (const f of w) f();
  }
  private next(): Promise<void> {
    return new Promise((r) => this.wake.add(r));
  }
  /** Audio length so far in ms. */
  get ms(): number {
    return Math.round(this.bytes / MP3_BYTES_PER_MS);
  }

  /** The HTTP response: chunked while growing, a plain file once done. */
  response(): Response {
    const headers: Record<string, string> = { "content-type": "audio/mpeg", "cache-control": "no-store", "accept-ranges": "none" };
    let i = 0;
    let cancelled = false;
    const served = () => {
      if (this.firstServedAt !== null) return;
      this.firstServedAt = this.now();
      this.onFirstServed?.(this.firstServedAt);
    };
    if (this.done) {
      served();
      const all = new Uint8Array(this.bytes);
      let at = 0;
      for (const c of this.chunks) all.set(c, at), (at += c.length);
      return new Response(all, { headers: { ...headers, "content-length": String(all.length) } });
    }
    const stream = new ReadableStream<Uint8Array>({
      pull: async (controller) => {
        while (!cancelled) {
          if (i < this.chunks.length) {
            served();
            controller.enqueue(this.chunks[i++]!);
            return;
          }
          if (this.done) return controller.close();
          await Promise.race([this.next(), new Promise((r) => setTimeout(r, 15_000))]);
          if (!this.done && i >= this.chunks.length && this.now() - (this.firstServedAt ?? this.now()) > 30_000) return controller.close();
        }
      },
      cancel: () => {
        cancelled = true;
      },
    });
    return new Response(stream, { headers });
  }
}

// ---------------------------------------------------------------- the orchestrator

/** The game's Ask Beeswax API (scout/game). Optional so this works before that branch lands. */
export interface AnswerGame {
  state(): ScoutState;
  /** False while the game waits for Start (POST /scout/reset): Ask Beeswax stays silent then. */
  isStarted?(): boolean;
  /** When the last line sent is expected to finish playing (ScoutGame.busyUntil). */
  readonly busyUntil?: number;
  beginAnswer?(): boolean;
  endAnswer?(audioMs?: number): void;
  interrupt?(): void;
}

export interface AskSay {
  line_id: string; // scout_ask_<id>
  caption: string;
  audio_url: string | null;
}

/** progressive: the say goes out at the first audio byte, the mp3 grows while it plays (chunked HTTP).
 *  whole: the say goes out once the answer's mp3 is complete (plain file with content-length; ~0.3 s later).
 *  sentence: one finished mp3 per sentence, one say each (HTTP TTS per sentence). */
export type AskAudioMode = "progressive" | "whole" | "sentence";

export interface ScoutAskDeps {
  game: AnswerGame;
  /** Sends the answer to the glasses (the routes broadcast it on the bus, tts_id = line_id). */
  say: (s: AskSay) => void;
  /** Dashboard events (say, heard). */
  event?: (e: ScoutEvent) => void;
  /** Public base URL for absolute audio URLs. */
  base: () => string;
  now?: () => number;
  /** Claude, as text deltas. Default: streamAnswer. */
  llm?: (question: string, ctx: AskContext, signal: AbortSignal) => AsyncIterable<string>;
  /** Progressive mode TTS. Default: elevenTts. */
  tts?: StreamTts;
  /** Sentence mode TTS. Default: renderSentenceMp3. */
  renderSentence?: (text: string) => Promise<Uint8Array>;
  /** Env SCOUT_ASK_AUDIO=progressive|whole|sentence (default progressive). */
  mode?: AskAudioMode;
  facts?: Record<ScoutLabel, AskFact[]>;
  /** A pre-rendered scout line (the wake ack): its caption and mp3 URL, or null when it doesn't exist. */
  line?: (id: string) => { caption: string; audio_url: string | null; ms?: number | null } | null;
}

/** Timings of one answer, ms after the question reached the Hive. */
export interface AskStats {
  id: string;
  question: string;
  answer: string;
  label: ScoutLabel | null;
  mode: AskAudioMode;
  at: number;
  first_token_ms: number | null;
  first_sentence_ms: number | null;
  first_audio_ms: number | null; //  first TTS byte at the Hive (the say goes out here)
  first_served_ms: number | null; // first byte written to the glasses' GET
  done_ms: number | null;
  audio_ms: number;
  error?: string;
}

/** After a bare "Hey Beeswax", the next utterance is the question if it comes within this long after the ack. */
export const WAKE_WINDOW_MS = 8000;
/** The acknowledgement line (hive/lines), and what the glasses say when it has no mp3 yet. */
export const WAKE_ACK_ID = "scout_wake_ack";
export const WAKE_ACK_FALLBACK = "Yes? I'm all antennae!";
/** Captions this recent count for the echo check. */
const ECHO_TAIL_MS = 2000;
/** Any line he said this recently counts for the echo check, wherever and whenever it played (Mac speakers too). */
const ECHO_RECENT_MS = 30_000; // a line can come back as a transcript this long after it stops playing
/** From the say to the first sound in the ear, roughly (phone fetch + player start). */
const PLAYER_START_MS = 700;
const KEEP_ASKS = 12;
/** Same estimate as scout.ts estimateMs. */
const estimateSpeechMs = (text: string) => Math.round((text.length / 14.4) * 1000 + 300);

export class ScoutAsk {
  readonly mode: AskAudioMode;
  private readonly now: () => number;
  private readonly facts: Record<ScoutLabel, AskFact[]>;
  private lastFound: ScoutLabel | null = null;
  /** Until when an utterance counts as the question (after a bare "Hey Beeswax"). */
  private wakeUntil = 0;
  /** What Beeswax says or will say, and until when it can come back through the mic (echo check). */
  private captions: { text: string; at: number; until: number }[] = [];
  private recent: string[] = [];
  private audios = new Map<string, AskAudio>();
  private stats: AskStats[] = [];
  private current: AbortController | null = null;
  private seq = 0;

  constructor(private d: ScoutAskDeps) {
    this.now = d.now ?? Date.now;
    const env = process.env.SCOUT_ASK_AUDIO;
    this.mode = d.mode ?? (env === "sentence" || env === "whole" ? env : "progressive");
    this.facts = d.facts ?? cardFacts();
  }

  /** Feed every game event: remembers the last found card and what Beeswax said (echo check). */
  event(e: ScoutEvent): void {
    if (e.type === "found") this.lastFound = e.label;
    else if (e.type === "say" && e.caption && !e.line_id.startsWith("scout_ask_") && e.line_id !== WAKE_ACK_ID) {
      // queued lines play after what is ahead of them: the game's busyUntil already includes this one
      const own = Math.max(e.at, this.now()) + estimateSpeechMs(e.caption);
      this.noteSaid(e.caption, Math.max(this.d.game.busyUntil ?? 0, own) + PLAYER_START_MS + ECHO_TAIL_MS);
    } else if (e.type === "stop") this.cutCaptions(); // cut off: the queued lines will never play
    else if (e.type === "state" && e.state.phase === "intro") this.reset(); // game restarted
  }

  /** Forget the last card (game restarted). */
  reset(): void {
    this.lastFound = null;
    this.wakeUntil = 0;
    this.recent = [];
  }

  get label(): ScoutLabel | null {
    return this.lastFound;
  }

  private noteSaid(caption: string, until: number): void {
    this.captions.push({ text: caption, at: this.now(), until });
    if (this.captions.length > 20) this.captions.shift();
  }

  /** Beeswax was cut off: nothing still queued will play, so it can't echo. */
  private cutCaptions(): void {
    const end = this.now() + ECHO_TAIL_MS;
    for (const c of this.captions) c.until = Math.min(c.until, end);
  }

  /** Everything he said in the last ECHO_RECENT_MS, or that may still be playing (the Mac plays lines later). */
  private recentCaptions(): string[] {
    const now = this.now();
    return this.captions.filter((c) => now <= c.until || now - c.at <= ECHO_RECENT_MS).map((c) => c.text);
  }

  /** What may be playing right now (in the glasses). */
  private playingCaptions(): string[] {
    const now = this.now();
    return this.captions.filter((c) => now <= c.until).map((c) => c.text);
  }

  /** Is the next utterance the question (a bare "Hey Beeswax" was just acknowledged)? */
  get awaitingQuestion(): boolean {
    return this.now() <= this.wakeUntil;
  }

  route(text: string, classify: (t: string) => ScoutAnswer): AskRoute {
    return routeUtterance(text, { classify, captions: this.recentCaptions(), playing: this.playingCaptions(), awaitingQuestion: this.awaitingQuestion });
  }

  /** A bare "Hey Beeswax": cut Beeswax off, say the ack, and wait for the question (detections paused meanwhile). */
  wake(): void {
    if (this.d.game.isStarted?.() === false) return; // waiting for Start: silent
    this.current?.abort();
    const g = this.d.game;
    try {
      g.interrupt?.();
      g.beginAnswer?.();
    } catch (e) {
      log("scout ask: wake interrupt failed:", e);
    }
    const line = this.d.line?.(WAKE_ACK_ID) ?? null;
    const caption = line?.caption || WAKE_ACK_FALLBACK;
    const s: AskSay = { line_id: WAKE_ACK_ID, caption, audio_url: line?.audio_url ?? null };
    try {
      this.d.say(s);
    } catch (e) {
      log("scout ask: say failed:", e);
    }
    const ackMs = line?.ms ?? estimateSpeechMs(caption);
    this.cutCaptions();
    this.noteSaid(caption, this.now() + PLAYER_START_MS + ackMs + ECHO_TAIL_MS);
    this.emit({ type: "say", ...s, at: this.now() });
    this.wakeUntil = this.now() + PLAYER_START_MS + ackMs + WAKE_WINDOW_MS;
    try {
      g.endAnswer?.(this.wakeUntil - this.now()); // the game waits for the question, then looks again
    } catch (e) {
      log("scout ask: endAnswer failed:", e);
    }
  }

  audio(id: string): AskAudio | undefined {
    return this.audios.get(id);
  }

  history(): AskStats[] {
    return this.stats.map((s) => ({ ...s }));
  }

  /** Answer a question out loud. Cuts Beeswax off first. Resolves when the answer is fully sent; never rejects. */
  async ask(question: string): Promise<AskStats> {
    const t0 = this.now();
    if (this.d.game.isStarted?.() === false) {
      // waiting for Start (POST /scout/reset): no answers
      return { id: "", question: question.slice(0, 300), answer: "", label: this.lastFound, mode: this.mode, at: t0, first_token_ms: null, first_sentence_ms: null, first_audio_ms: null, first_served_ms: null, done_ms: 0, audio_ms: 0, error: "not started" };
    }
    const ms = () => this.now() - t0;
    this.current?.abort();
    this.wakeUntil = 0;
    const ac = new AbortController();
    this.current = ac;
    const id = `${(t0 % 1e8).toString(36)}${(this.seq++ % 36).toString(36)}`;
    const lineId = `scout_ask_${id}`;
    const label = this.lastFound;
    const st: AskStats = {
      id,
      question: question.slice(0, 300),
      answer: "",
      label,
      mode: this.mode,
      at: t0,
      first_token_ms: null,
      first_sentence_ms: null,
      first_audio_ms: null,
      first_served_ms: null,
      done_ms: null,
      audio_ms: 0,
    };
    this.stats.push(st);
    if (this.stats.length > KEEP_ASKS) this.stats.shift();

    const g = this.d.game;
    this.cutCaptions();
    try {
      g.interrupt?.();
    } catch (e) {
      log("scout ask: interrupt failed:", e);
    }
    try {
      g.beginAnswer?.();
    } catch (e) {
      log("scout ask: beginAnswer failed:", e);
    }

    const ctx = askContextFor(label, this.facts, this.recent);
    const llm = this.d.llm ?? ((q, c, signal) => streamAnswer(q, c, { signal }));
    const sentences = new AsyncQueue<string>();
    let text = "";
    let llmError: string | null = null;

    // Claude -> sentences (cleaned, word-capped)
    const produce = (async () => {
      const split = new SentenceSplitter();
      let words = 0;
      const emit = (raw: string) => {
        const s = cleanSpoken(raw);
        if (!s || words >= MAX_ANSWER_WORDS) return;
        const clipped = clipWords(s, MAX_ANSWER_WORDS - words);
        words += clipped.split(" ").length;
        if (st.first_sentence_ms === null) st.first_sentence_ms = ms();
        text = text ? `${text} ${clipped}` : clipped;
        sentences.push(clipped);
      };
      try {
        for await (const delta of llm(question, ctx, ac.signal)) {
          if (ac.signal.aborted) break;
          if (st.first_token_ms === null) st.first_token_ms = ms();
          for (const s of split.push(delta)) emit(s);
          if (words >= MAX_ANSWER_WORDS) break;
        }
        for (const s of split.end()) emit(s);
      } catch (e) {
        if (!ac.signal.aborted) llmError = e instanceof Error ? e.message : String(e);
      }
      if (!text && !ac.signal.aborted) {
        if (llmError) log(`scout ask ${id}: Claude failed (${llmError}), fallback line`);
        text = ASK_FALLBACK;
        if (st.first_sentence_ms === null) st.first_sentence_ms = ms();
        sentences.push(ASK_FALLBACK);
      }
      sentences.end();
    })();

    let firstSent = false;
    const sendSay = (caption: string, url: string | null, segId = lineId) => {
      firstSent = true;
      const s: AskSay = { line_id: segId, caption, audio_url: url };
      try {
        this.d.say(s);
      } catch (e) {
        log("scout ask: say failed:", e);
      }
      this.noteSaid(caption, this.now() + 15_000); // still streaming: corrected when the answer is done
      this.emit({ type: "say", line_id: segId, caption, audio_url: url, at: this.now() });
    };

    let audioMs = 0;
    let audioStartAt: number | null = null;
    try {
      if (this.mode !== "sentence") {
        const audio = new AskAudio(id, (at) => (st.first_served_ms ??= at - t0), this.now);
        this.audios.set(id, audio);
        this.trim();
        const tts = this.d.tts ?? elevenTts;
        try {
          for await (const chunk of tts(sentences, ac.signal)) {
            if (ac.signal.aborted) break;
            audio.push(chunk);
            if (st.first_audio_ms === null) st.first_audio_ms = ms();
            if (!firstSent && this.mode === "progressive") {
              audioStartAt = this.now();
              sendSay(text, `${this.d.base()}/scout/ask/${id}.mp3`);
            }
          }
        } catch (e) {
          st.error = `tts: ${e instanceof Error ? e.message : e}`;
          log(`scout ask ${id}: ${st.error}`);
        }
        audio.finish();
        audioMs = audio.ms;
        if (this.mode === "whole" && audio.bytes > 0 && !ac.signal.aborted) {
          audioStartAt = this.now();
          sendSay(text, `${this.d.base()}/scout/ask/${id}.mp3`);
        }
      } else {
        // sentence mode: each sentence is its own finished mp3, sent in order, rendered in parallel
        const render = this.d.renderSentence ?? renderSentenceMp3;
        const jobs = new AsyncQueue<{ text: string; mp3: Promise<Uint8Array | null> }>();
        const sender = (async () => {
          let n = 0;
          for await (const job of jobs) {
            const mp3 = await job.mp3;
            if (ac.signal.aborted) return;
            if (!mp3) continue;
            const segId = n === 0 ? id : `${id}_${n + 1}`;
            n++;
            const audio = new AskAudio(segId, (at) => (st.first_served_ms ??= at - t0), this.now);
            audio.push(mp3);
            audio.finish();
            this.audios.set(segId, audio);
            this.trim();
            if (st.first_audio_ms === null) (st.first_audio_ms = ms()), (audioStartAt = this.now());
            audioMs += audio.ms;
            sendSay(job.text, `${this.d.base()}/scout/ask/${segId}.mp3`, `scout_ask_${segId}`);
          }
        })();
        for await (const s of sentences) {
          if (ac.signal.aborted) break;
          jobs.push({
            text: s,
            mp3: render(s).catch((e) => {
              st.error = `tts: ${e instanceof Error ? e.message : e}`;
              log(`scout ask ${id}: ${st.error}`);
              return null;
            }),
          });
        }
        jobs.end();
        await sender;
      }
    } finally {
      await produce;
    }

    st.answer = text;
    st.audio_ms = audioMs;
    if (llmError) st.error = st.error ? `${st.error}; claude: ${llmError}` : `claude: ${llmError}`;
    if (ac.signal.aborted) {
      st.error = st.error ?? "interrupted by a newer question";
      st.done_ms = ms();
      return { ...st };
    }
    if (!firstSent) {
      // no audio at all (TTS down): the glasses speak the caption with the phone's voice
      sendSay(text, null);
      audioMs = estimateSpeechMs(text);
      audioStartAt = this.now();
    } else if (this.mode === "progressive") {
      // dashboard: the full caption (the glasses already got the say with the first sentence)
      this.emit({ type: "say", line_id: lineId, caption: text, audio_url: `${this.d.base()}/scout/ask/${id}.mp3`, at: this.now() });
    }
    this.noteSaid(text, (audioStartAt ?? this.now()) + PLAYER_START_MS + audioMs + ECHO_TAIL_MS);
    st.done_ms = ms();
    this.recent.push(`Kid: ${question.slice(0, 160)}`, `Beeswax: ${text}`);
    if (this.recent.length > 4) this.recent.splice(0, this.recent.length - 4);
    if (this.current === ac) this.current = null;
    const left = Math.max(0, (audioStartAt ?? this.now()) + PLAYER_START_MS + audioMs - this.now());
    try {
      this.d.game.endAnswer?.(left);
    } catch (e) {
      log("scout ask: endAnswer failed:", e);
    }
    log(
      `scout ask ${id}: "${question.slice(0, 80)}" -> "${text.slice(0, 120)}" | token ${st.first_token_ms} ms, sentence ${st.first_sentence_ms} ms, ` +
        `audio ${st.first_audio_ms} ms, served ${st.first_served_ms ?? "-"} ms, done ${st.done_ms} ms, ${audioMs} ms of audio${st.error ? ` (${st.error})` : ""}`,
    );
    return { ...st };
  }

  private trim(): void {
    while (this.audios.size > KEEP_ASKS * 3) this.audios.delete(this.audios.keys().next().value!);
  }

  private emit(e: ScoutEvent): void {
    try {
      this.d.event?.(e);
    } catch (err) {
      log("scout ask: event handler failed:", err);
    }
  }
}

// ---------------------------------------------------------------- routes

const ASK_FILE = /^([a-z0-9_]{1,40})\.mp3$/;

/** GET /scout/ask/<id>.mp3 (public, like /scout/audio), GET /scout/ask-stats and POST /scout/ask {text} (token). */
export function registerScoutAskRoutes(app: Hono, ask: ScoutAsk): void {
  app.get("/scout/ask/:file", (c: Context) => {
    const m = ASK_FILE.exec(c.req.param("file") ?? "");
    if (!m) return c.text("bad file name", 400);
    const audio = ask.audio(m[1]!);
    if (!audio) return c.text("not found", 404);
    return audio.response();
  });
  app.get("/scout/ask-stats", (c) => c.json({ mode: ask.mode, label: ask.label, asks: ask.history() }));
  app.post("/scout/ask", async (c) => {
    let body: unknown = null;
    try {
      body = await c.req.json();
    } catch {}
    const text = typeof (body as { text?: unknown })?.text === "string" ? (body as { text: string }).text.trim() : "";
    if (!text) return c.json({ error: "text required" }, 400);
    const wait = (body as { wait?: unknown }).wait === true;
    const p = ask.ask(text.slice(0, 300));
    if (wait) return c.json(await p);
    return c.json({ ok: true });
  });
}
