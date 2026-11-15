// Beeswax's live voice: ElevenLabs Flash v2.5 with the Voice-Designed voice (ELEVENLABS_VOICE_ID).
// ttsPcm streams PCM16 for room lines on the Quest; ttsMp3 renders a whole mp3 for the glasses.
import { ElevenLabsClient } from "@elevenlabs/elevenlabs-js";

export const TTS_SAMPLE_RATE = 16000;
export const TTS_MODEL = "eleven_flash_v2_5";

/** Timeouts (ms) and the text cap. Mutable so tests can shorten them. */
export const ttsLimits = { firstByteMs: 5000, idleMs: 5000, totalMs: 30_000, maxChars: 500 };

type ConvertFormat = "mp3_44100_64" | "mp3_44100_128" | "pcm_16000";

export function ttsAvailable(): boolean {
  return Boolean(process.env.ELEVENLABS_API_KEY?.trim() && process.env.ELEVENLABS_VOICE_ID?.trim());
}

let cached: { key: string; client: ElevenLabsClient } | null = null;

function env(): { client: ElevenLabsClient; voiceId: string } {
  const key = process.env.ELEVENLABS_API_KEY?.trim();
  const voiceId = process.env.ELEVENLABS_VOICE_ID?.trim();
  if (!key || !voiceId) throw new Error("TTS unavailable: set ELEVENLABS_API_KEY and ELEVENLABS_VOICE_ID");
  if (cached?.key !== key) cached = { key, client: new ElevenLabsClient({ apiKey: key }) };
  return { client: cached.client, voiceId };
}

function clean(text: unknown): string {
  return typeof text === "string" ? text.replace(/\s+/g, " ").trim().slice(0, ttsLimits.maxChars) : "";
}

function errText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

function withTimeout<T>(p: PromiseLike<T>, ms: number, what: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new Error(`${what} timed out after ${ms} ms`)), ms);
  });
  return Promise.race([Promise.resolve(p), timeout]).finally(() => clearTimeout(timer));
}

/** Read a body stream, failing if no chunk arrives for idleMs. Cancels the stream on exit. */
async function* readChunks(stream: ReadableStream<Uint8Array>): AsyncGenerator<Uint8Array> {
  const reader = stream.getReader();
  try {
    while (true) {
      const { done, value } = await withTimeout(reader.read(), ttsLimits.idleMs, "audio stream");
      if (done) return;
      if (value && value.length > 0) yield value;
    }
  } finally {
    reader.cancel().catch(() => {});
  }
}

function concat(parts: Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

/**
 * PCM16 LE mono chunks at TTS_SAMPLE_RATE, every chunk an even number of bytes (whole samples).
 * Never throws: on a missing key, HTTP error or timeout it logs and ends early (possibly with no audio).
 */
export async function* ttsPcm(text: string): AsyncGenerator<Uint8Array> {
  const t = clean(text);
  if (!t) return;
  if (!ttsAvailable()) {
    console.warn("[tts] unavailable (ELEVENLABS_API_KEY / ELEVENLABS_VOICE_ID unset)");
    return;
  }
  const start = performance.now();
  const ac = new AbortController();
  const total = setTimeout(() => ac.abort(), ttsLimits.totalMs);
  let carry: Uint8Array | null = null;
  let bytes = 0;
  try {
    const { client, voiceId } = env();
    const request = client.textToSpeech.stream(
      voiceId,
      { text: t, modelId: TTS_MODEL, outputFormat: "pcm_16000" },
      { abortSignal: ac.signal, maxRetries: 1, timeoutInSeconds: ttsLimits.firstByteMs / 1000 },
    );
    const stream = await withTimeout(request, ttsLimits.firstByteMs, "TTS response");
    for await (const chunk of readChunks(stream)) {
      if (ac.signal.aborted) throw new Error(`TTS exceeded ${ttsLimits.totalMs} ms`);
      const buf: Uint8Array = carry ? concat([carry, chunk]) : chunk;
      const even = buf.length - (buf.length % 2);
      carry = even < buf.length ? buf.slice(even) : null;
      if (even > 0) {
        bytes += even;
        yield buf.subarray(0, even);
      }
    }
    console.log(`[tts] pcm ${bytes} bytes in ${Math.round(performance.now() - start)}ms`);
  } catch (e) {
    console.warn(`[tts] pcm stopped after ${bytes} bytes: ${errText(e)}`);
  } finally {
    clearTimeout(total);
    ac.abort(); // no-op when the body is fully read; cancels the request if the consumer stopped early
  }
}

/** Whole-file render (used by ttsMp3 and the pre-render script). Throws on failure or timeout. */
export async function ttsBytes(
  text: string,
  opts: {
    format?: ConvertFormat;
    model?: string;
    voiceId?: string;
    timeoutMs?: number;
    voiceSettings?: { stability?: number; similarityBoost?: number; style?: number; useSpeakerBoost?: boolean };
  } = {},
): Promise<Uint8Array> {
  const t = clean(text);
  if (!t) throw new Error("TTS: empty text");
  const { client, voiceId } = env();
  const ac = new AbortController();
  const total = setTimeout(() => ac.abort(), opts.timeoutMs ?? ttsLimits.totalMs);
  try {
    const request = client.textToSpeech.convert(
      opts.voiceId ?? voiceId,
      { text: t, modelId: opts.model ?? TTS_MODEL, outputFormat: opts.format ?? "mp3_44100_64", ...(opts.voiceSettings ? { voiceSettings: opts.voiceSettings } : {}) },
      { abortSignal: ac.signal, maxRetries: 1, timeoutInSeconds: (opts.timeoutMs ?? ttsLimits.totalMs) / 1000 },
    );
    const stream = await withTimeout(request, opts.timeoutMs ?? ttsLimits.totalMs, "TTS response");
    const parts: Uint8Array[] = [];
    for await (const chunk of readChunks(stream)) {
      if (ac.signal.aborted) throw new Error("TTS render timed out");
      parts.push(chunk);
    }
    const out = concat(parts);
    if (out.length === 0) throw new Error("TTS returned no audio");
    return out;
  } finally {
    clearTimeout(total);
    ac.abort();
  }
}

/** A whole mp3 of the line (for the glasses' audio_url). Rejects when TTS is unavailable or fails. */
export async function ttsMp3(text: string): Promise<Uint8Array> {
  return ttsBytes(text, { format: "mp3_44100_64" });
}
