// Speech-to-text for the player's voice (what-ifs, "put it out", questions): one short PCM16 utterance -> text.
// Providers: ElevenLabs Scribe (batch, cloud) and the local faster-whisper sidecar (hive/stt-whisper).
// STT_PROVIDER = auto (default: ElevenLabs when ELEVENLABS_API_KEY is set, else whisper) | elevenlabs | whisper.
// auto hedges: the other provider starts when the first one fails or is slow. transcribe() never throws.
import { ElevenLabsClient } from "@elevenlabs/elevenlabs-js";

export interface SttResult {
  text: string;
  ms: number;
  provider: string; // who produced `text`: "elevenlabs" | "whisper" | "none" (nothing worked or no audio)
  error?: string; //   why it is empty: no_audio | too_short | <provider>: <reason>; ...
}

type Provider = "elevenlabs" | "whisper";

export const STT_SAMPLE_RATE = 16000;
export const DEFAULT_TIMEOUT_MS = 6000;
export const DEFAULT_WHISPER_URL = "http://127.0.0.1:8242/transcribe";
export const DEFAULT_ELEVENLABS_MODEL = "scribe_v3"; // latest batch Scribe (ElevenLabs models page, 2026-11)
/** Words the players and Beeswax actually say; both providers are biased toward them. */
export const KEYTERMS = ["Beeswax", "smoker", "syrup", "hive", "wasps", "hornet", "pebbles", "fire", "put it out", "drone", "drones", "banana", "bananas", "sting", "wildfire", "varroa", "mites", "queen", "pollen", "foulbrood"];

const MIN_BYTES = (STT_SAMPLE_RATE / 10) * 2; // 100 ms: ElevenLabs' minimum, and anything shorter is a click
/** Tunables (tests shorten them). hedgeMs: in auto mode, start the fallback if the primary hasn't answered by then. */
export const sttLimits = { hedgeMs: Number(process.env.STT_HEDGE_MS) || 2500, maxSeconds: 30, healthTtlMs: 15_000, healthTimeoutMs: 1500 };

// ---------------------------------------------------------------- config

function env(name: string): string {
  return process.env[name]?.trim() ?? "";
}

function mode(): "auto" | Provider {
  const m = env("STT_PROVIDER").toLowerCase();
  return m === "elevenlabs" || m === "whisper" ? m : "auto";
}

function whisperUrl(): string {
  return env("WHISPER_URL") || DEFAULT_WHISPER_URL;
}

/** Providers in the order they are tried. */
function plan(): Provider[] {
  const m = mode();
  if (m !== "auto") return [m];
  return env("ELEVENLABS_API_KEY") ? ["elevenlabs", "whisper"] : ["whisper"];
}

// ---------------------------------------------------------------- test seams

let fetchImpl: typeof fetch | null = null;
let eleven: { key: string; client: ElevenLabsClient } | null = null;

/** Route every STT HTTP call (ElevenLabs SDK and whisper) through `f` (tests); null = global fetch. */
export function setSttFetch(f: typeof fetch | null): void {
  fetchImpl = f;
  eleven = null;
  whisperHealth.at = 0;
}

const doFetch: typeof fetch = ((input: any, init?: any) => (fetchImpl ?? fetch)(input, init)) as typeof fetch;

function elevenClient(): ElevenLabsClient {
  const key = env("ELEVENLABS_API_KEY");
  if (!key) throw new Error("ELEVENLABS_API_KEY is not set");
  if (eleven?.key !== key) eleven = { key, client: new ElevenLabsClient({ apiKey: key, fetch: doFetch }) };
  return eleven.client;
}

// ---------------------------------------------------------------- audio

/** 44-byte RIFF/WAVE header + the samples: PCM16 LE mono at `sampleRate`. */
export function pcmToWav(pcm: Uint8Array, sampleRate = STT_SAMPLE_RATE): Uint8Array {
  const out = new Uint8Array(44 + pcm.length);
  const v = new DataView(out.buffer);
  const ascii = (at: number, s: string) => {
    for (let i = 0; i < s.length; i++) out[at + i] = s.charCodeAt(i);
  };
  ascii(0, "RIFF");
  v.setUint32(4, 36 + pcm.length, true);
  ascii(8, "WAVE");
  ascii(12, "fmt ");
  v.setUint32(16, 16, true); // fmt chunk size
  v.setUint16(20, 1, true); // PCM
  v.setUint16(22, 1, true); // mono
  v.setUint32(24, sampleRate, true);
  v.setUint32(28, sampleRate * 2, true); // byte rate
  v.setUint16(32, 2, true); // block align
  v.setUint16(34, 16, true); // bits per sample
  ascii(36, "data");
  v.setUint32(40, pcm.length, true);
  out.set(pcm, 44);
  return out;
}

/** Whole samples only, capped to sttLimits.maxSeconds (a stuck push-to-talk must not upload minutes). */
function trimPcm(pcm: Uint8Array): Uint8Array {
  const max = sttLimits.maxSeconds * STT_SAMPLE_RATE * 2;
  const n = Math.min(pcm.length - (pcm.length % 2), max);
  return n === pcm.length ? pcm : pcm.subarray(0, n);
}

/** "(laughter)  what if [music] a fire starts" -> "what if a fire starts". */
export function cleanTranscript(text: unknown): string {
  if (typeof text !== "string") return "";
  return text
    .replace(/\([^)]{0,40}\)|\[[^\]]{0,40}\]/g, " ")
    .replace(/\s+/g, " ")
    .trim()
    .slice(0, 500);
}

// ---------------------------------------------------------------- providers

async function viaElevenLabs(pcm: Uint8Array, signal: AbortSignal, timeoutMs: number): Promise<string> {
  const res = await elevenClient().speechToText.convert(
    {
      modelId: env("ELEVENLABS_STT_MODEL") || DEFAULT_ELEVENLABS_MODEL,
      file: { data: pcmToWav(pcm), filename: "utterance.wav", contentType: "audio/wav" },
      languageCode: env("STT_LANGUAGE") || "en",
      tagAudioEvents: false,
      timestampsGranularity: "none",
      keyterms: KEYTERMS,
    },
    { abortSignal: signal, timeoutInSeconds: Math.max(1, Math.ceil(timeoutMs / 1000)), maxRetries: 0 },
  );
  if (!res || typeof (res as { text?: unknown }).text !== "string") throw new Error("no text in response");
  return (res as { text: string }).text;
}

async function viaWhisper(pcm: Uint8Array, signal: AbortSignal): Promise<string> {
  const res = await doFetch(whisperUrl(), {
    method: "POST",
    headers: { "content-type": "application/octet-stream" },
    body: pcm.slice(), // a copy typed as Uint8Array<ArrayBuffer> (BodyInit rejects a view over ArrayBufferLike)
    signal,
  });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  const body = (await res.json()) as { text?: unknown };
  if (typeof body?.text !== "string") throw new Error("no text in response");
  return body.text;
}

function errText(e: unknown): string {
  if (e instanceof Error) return e.name === "AbortError" ? "aborted" : e.message.split("\n")[0]!.slice(0, 160);
  return String(e).slice(0, 160);
}

// ---------------------------------------------------------------- transcribe

interface Attempt { provider: Provider; ac: AbortController; promise: Promise<string>; outcome?: "ok" | "error" }

/**
 * Transcribe one utterance (raw PCM16 LE, 16 kHz, mono). Never throws: on failure text is "" and `error` says why.
 * opts.timeoutMs (default 6000) bounds the whole call, fallback included.
 */
export async function transcribe(pcm16le16k: Uint8Array, opts: { timeoutMs?: number } = {}): Promise<SttResult> {
  const start = performance.now();
  const elapsed = () => Math.round(performance.now() - start);
  if (!(pcm16le16k instanceof Uint8Array) || pcm16le16k.length === 0) return { text: "", ms: 0, provider: "none", error: "no_audio" };
  if (pcm16le16k.length < MIN_BYTES) return { text: "", ms: 0, provider: "none", error: "too_short" };
  const pcm = trimPcm(pcm16le16k);
  const timeoutMs = opts.timeoutMs && opts.timeoutMs > 0 ? opts.timeoutMs : DEFAULT_TIMEOUT_MS;
  const order = plan();
  const errors: string[] = [];
  const attempts: Attempt[] = [];

  const launch = (provider: Provider): Attempt => {
    const ac = new AbortController();
    const left = Math.max(1, timeoutMs - elapsed());
    const promise = provider === "elevenlabs" ? viaElevenLabs(pcm, ac.signal, left) : viaWhisper(pcm, ac.signal);
    const a: Attempt = { provider, ac, promise };
    promise.then(
      () => (a.outcome = "ok"),
      () => (a.outcome = "error"),
    );
    attempts.push(a);
    return a;
  };

  // The first success wins. The next provider starts when the current one fails or after hedgeMs.
  const result = await new Promise<SttResult>((resolve) => {
    let next = 0;
    let pending = 0;
    let done = false;
    let hedge: ReturnType<typeof setTimeout> | undefined;
    const finish = (r: SttResult) => {
      if (done) return;
      done = true;
      clearTimeout(hedge);
      clearTimeout(deadline);
      resolve(r);
    };
    const deadline = setTimeout(() => {
      for (const a of attempts) if (!a.outcome) errors.push(`${a.provider}: timeout`);
      finish({ text: "", ms: elapsed(), provider: "none", error: errors.join("; ") });
    }, timeoutMs);
    const startNext = () => {
      clearTimeout(hedge);
      if (done || next >= order.length) return;
      const a = launch(order[next++]!);
      pending++;
      if (next < order.length) hedge = setTimeout(startNext, sttLimits.hedgeMs);
      a.promise.then(
        (text) => finish({ text: cleanTranscript(text), ms: elapsed(), provider: a.provider }),
        (e) => {
          pending--;
          if (done) return;
          errors.push(`${a.provider}: ${errText(e)}`);
          if (next < order.length) startNext();
          else if (pending === 0) finish({ text: "", ms: elapsed(), provider: "none", error: errors.join("; ") });
        },
      );
    };
    startNext();
  });

  for (const a of attempts) {
    // Health from what whisper itself did; a loser we cancel (or a call cut by the deadline) says nothing.
    if (a.provider === "whisper" && a.outcome) noteWhisper(a.outcome === "ok");
    a.ac.abort(); // cancel the loser (no-op for the winner, its body is already read)
    a.promise.catch(() => {});
  }
  const tag = result.provider === "none" ? `FAILED ${result.error}` : `${result.provider} ${JSON.stringify(result.text.slice(0, 80))}`;
  console.log(`[stt] ${(pcm.length / 2 / STT_SAMPLE_RATE).toFixed(2)}s audio -> ${result.ms}ms ${tag}`);
  return result;
}

// ---------------------------------------------------------------- status

const whisperHealth = { ok: false, at: 0, inflight: null as Promise<boolean> | null };

function noteWhisper(ok: boolean): void {
  whisperHealth.ok = ok;
  whisperHealth.at = Date.now();
}

/** GET <whisper>/health. Never throws; updates what sttStatus() reports. */
export function checkWhisper(timeoutMs = sttLimits.healthTimeoutMs): Promise<boolean> {
  if (whisperHealth.inflight) return whisperHealth.inflight;
  const run = (async () => {
    try {
      const url = new URL("/health", whisperUrl());
      const res = await doFetch(url, { signal: AbortSignal.timeout(timeoutMs) });
      const body = res.ok ? ((await res.json()) as { ok?: unknown }) : null;
      noteWhisper(body?.ok === true);
    } catch {
      noteWhisper(false);
    }
    return whisperHealth.ok;
  })();
  whisperHealth.inflight = run.finally(() => {
    whisperHealth.inflight = null;
  });
  return whisperHealth.inflight;
}

/**
 * What the Beat Board shows. elevenlabs = a key is set; whisper = the sidecar answered its last health check or
 * transcription (refreshed in the background when stale, so the first call after startup reports false).
 * provider = the provider tried first.
 */
export function sttStatus(): { elevenlabs: boolean; whisper: boolean; provider: string } {
  if (Date.now() - whisperHealth.at > sttLimits.healthTtlMs) void checkWhisper();
  return { elevenlabs: Boolean(env("ELEVENLABS_API_KEY")), whisper: whisperHealth.ok, provider: plan()[0]! };
}
