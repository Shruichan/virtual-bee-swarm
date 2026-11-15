// Ask Beeswax for Live Bee Scout (branch scout/ask): voice follow-up questions on the glasses.
//
//   glasses final transcript --POST /scout/utterance--> routeUtterance(): echo | game (yes/no) | ask
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
  /\b(?:why|how come|how do|how does|how did|how many|how much|how long|how big|what is|what's|what are|what do|what does|what happens|what if|tell me (?:more )?about|tell me why|explain|i wonder)\b/;

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

/** The mic heard Beeswax himself: the transcript sits inside something he said recently. */
export function isEcho(text: string, captions: readonly string[]): boolean {
  const t = norm(text);
  const n = t ? t.split(" ").length : 0;
  if (n < 2) return false;
  for (const cap of captions) {
    const c = norm(cap);
    if (!c) continue;
    if (` ${c} `.includes(` ${t} `)) return true;
    if (n >= 4) {
      // STT rarely hears it word for word: most of its word pairs in order is enough
      const have = new Set(bigrams(c));
      const pairs = bigrams(t);
      if (pairs.filter((p) => have.has(p)).length / pairs.length >= 0.85) return true;
    }
  }
  return false;
}

export type AskRoute = "echo" | "game" | "ask";

/** Where a final transcript goes. `answer` = the game's own yes/no/rude reading (classifyAnswer). Short yes/no
 *  answers stay with the game, Beeswax's own voice is dropped, questions go to Ask Beeswax, the rest to the game. */
export function routeUtterance(text: string, o: { answer: ScoutAnswer; captions: readonly string[] }): AskRoute {
  if (isEcho(text, o.captions)) return "echo";
  const words = norm(text).replace(LEAD_RX, "").split(" ").filter(Boolean).length;
  if (o.answer !== "other" && words <= 4) return "game";
  return looksLikeQuestion(text) ? "ask" : "game";
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

/** Captions this recent count for the echo check. */
const ECHO_WINDOW_MS = 25_000;
/** From the say to the first sound in the ear, roughly (phone fetch + player start). */
const PLAYER_START_MS = 700;
const KEEP_ASKS = 12;

export class ScoutAsk {
  readonly mode: AskAudioMode;
  private readonly now: () => number;
  private readonly facts: Record<ScoutLabel, AskFact[]>;
  private lastFound: ScoutLabel | null = null;
  private captions: { text: string; at: number }[] = [];
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
    else if (e.type === "say" && e.caption && !e.line_id.startsWith("scout_ask_")) this.noteSaid(e.caption, e.at);
    else if (e.type === "state" && e.state.phase === "intro") this.reset(); // game restarted
  }

  /** Forget the last card (game restarted). */
  reset(): void {
    this.lastFound = null;
    this.recent = [];
  }

  get label(): ScoutLabel | null {
    return this.lastFound;
  }

  private noteSaid(caption: string, at = this.now()): void {
    this.captions.push({ text: caption, at });
    if (this.captions.length > 20) this.captions.shift();
  }

  private recentCaptions(): string[] {
    const now = this.now();
    return this.captions.filter((c) => now - c.at <= ECHO_WINDOW_MS).map((c) => c.text);
  }

  route(text: string, answer: ScoutAnswer): AskRoute {
    return routeUtterance(text, { answer, captions: this.recentCaptions() });
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
    const ms = () => this.now() - t0;
    this.current?.abort();
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
      this.noteSaid(caption);
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
      audioMs = Math.round((text.length / 14.4) * 1000 + 300);
      audioStartAt = this.now();
    } else if (this.mode === "progressive") {
      // dashboard: the full caption (the glasses already got the say with the first sentence)
      this.emit({ type: "say", line_id: lineId, caption: text, audio_url: `${this.d.base()}/scout/ask/${id}.mp3`, at: this.now() });
      this.noteSaid(text);
    }
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
