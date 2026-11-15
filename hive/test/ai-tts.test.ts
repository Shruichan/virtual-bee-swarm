import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import { cachedAudioSeconds, prepareSpeech } from "../src/ask";
import { TTS_SAMPLE_RATE, ttsAvailable, ttsLimits, ttsMp3, ttsPcm } from "../src/tts";

const realFetch = globalThis.fetch;
const savedEnv = { key: process.env.ELEVENLABS_API_KEY, voice: process.env.ELEVENLABS_VOICE_ID };
const savedLimits = { ...ttsLimits };
let calls: { url: string; init: RequestInit | undefined }[] = [];

function mockFetch(respond: (url: string) => Response | Promise<Response>) {
  calls = [];
  globalThis.fetch = (async (input: unknown, init?: RequestInit) => {
    const url = String(input instanceof Request ? input.url : input);
    calls.push({ url, init });
    return respond(url);
  }) as unknown as typeof fetch;
}

function streamOf(chunks: number[][], hangAfter = false): ReadableStream<Uint8Array> {
  let i = 0;
  return new ReadableStream<Uint8Array>({
    pull(controller) {
      if (i < chunks.length) controller.enqueue(new Uint8Array(chunks[i++]!));
      else if (!hangAfter) controller.close();
      else return new Promise(() => {}); // never sends another chunk
    },
  });
}

async function collect(gen: AsyncGenerator<Uint8Array>): Promise<Uint8Array[]> {
  const out: Uint8Array[] = [];
  for await (const c of gen) out.push(c);
  return out;
}

const bytes = (n: number, from = 0) => Array.from({ length: n }, (_, i) => (from + i) & 0xff);

beforeEach(() => {
  process.env.ELEVENLABS_API_KEY = "test-key-not-real";
  process.env.ELEVENLABS_VOICE_ID = "voice123";
  Object.assign(ttsLimits, { firstByteMs: 300, idleMs: 300, totalMs: 3000 });
});

afterEach(() => {
  globalThis.fetch = realFetch;
  Object.assign(ttsLimits, savedLimits);
  for (const [k, v] of [["ELEVENLABS_API_KEY", savedEnv.key], ["ELEVENLABS_VOICE_ID", savedEnv.voice]] as const) {
    if (v === undefined) delete process.env[k];
    else process.env[k] = v;
  }
});

describe("ttsAvailable", () => {
  test("needs both the key and the voice id", () => {
    expect(ttsAvailable()).toBe(true);
    delete process.env.ELEVENLABS_VOICE_ID;
    expect(ttsAvailable()).toBe(false);
    process.env.ELEVENLABS_VOICE_ID = "v";
    process.env.ELEVENLABS_API_KEY = "  ";
    expect(ttsAvailable()).toBe(false);
  });
});

test("speech budget is shared by streaming and whole-file renders", async () => {
  const names = ["ELEVENLABS_TTS_CHAR_BUDGET", "ELEVENLABS_TTS_BUDGET_PERIOD", "ELEVENLABS_TTS_BUDGET_DB"] as const;
  const previous = names.map((name) => process.env[name]);
  process.env.ELEVENLABS_TTS_CHAR_BUDGET = "7";
  process.env.ELEVENLABS_TTS_BUDGET_PERIOD = "test-allocation";
  process.env.ELEVENLABS_TTS_BUDGET_DB = ":memory:";
  try {
    mockFetch(() => new Response(streamOf([bytes(6)]), { status: 200 }));
    expect(await collect(ttsPcm("Bee."))).toHaveLength(1);
    expect(await ttsMp3("Hi.")).toHaveLength(6);
    expect(await collect(ttsPcm("Hi."))).toEqual([]);
    await expect(ttsMp3("Bee.")).rejects.toThrow("speech budget reached");
    expect(calls).toHaveLength(2);
  } finally {
    for (const [index, name] of names.entries()) {
      const value = previous[index];
      if (value === undefined) delete process.env[name];
      else process.env[name] = value;
    }
  }
});

test("one voice: a leftover VBS_LOCAL_TTS_URL is ignored, live lines still use the ElevenLabs voice", async () => {
  const previous = process.env.VBS_LOCAL_TTS_URL;
  process.env.VBS_LOCAL_TTS_URL = "http://127.0.0.1:8243/speak";
  try {
    mockFetch(() => new Response(streamOf([bytes(6)]), { status: 200 }));
    expect(ttsAvailable()).toBe(true);
    expect(await collect(ttsPcm("That's the queen."))).toHaveLength(1);
    expect(await ttsMp3("That's the queen.")).toHaveLength(6);
    expect(calls.map((c) => c.url).every((u) => u.includes("/v1/text-to-speech/voice123"))).toBe(true);
    delete process.env.ELEVENLABS_API_KEY;
    expect(ttsAvailable()).toBe(false); // no key: no voice at all (captions), never the local one
  } finally {
    if (previous === undefined) delete process.env.VBS_LOCAL_TTS_URL;
    else process.env.VBS_LOCAL_TTS_URL = previous;
  }
});

describe("ttsPcm", () => {
  test("streams pcm_16000 from flash v2.5 and re-chunks odd sizes to whole samples", async () => {
    const sizes = [3, 5, 1, 7, 2]; // 18 bytes total
    let at = 0;
    const chunks = sizes.map((n) => bytes(n, (at += n) - n));
    mockFetch(() => new Response(streamOf(chunks), { status: 200, headers: { "content-type": "audio/pcm" } }));
    const out = await collect(ttsPcm("Motion denied."));
    for (const c of out) expect(c.length % 2).toBe(0);
    const joined = Buffer.concat(out.map((c) => Buffer.from(c)));
    expect([...joined]).toEqual(bytes(18));
    expect(TTS_SAMPLE_RATE).toBe(16000);
    const { url, init } = calls[0]!;
    expect(url).toContain("/v1/text-to-speech/voice123/stream");
    expect(url).toContain("output_format=pcm_16000");
    const body = JSON.parse(String(init?.body));
    expect(body.model_id).toBe("eleven_flash_v2_5");
    expect(body.voice_settings).toEqual({ stability: 0.6, similarity_boost: 0.75, style: 0, use_speaker_boost: true, speed: 0.95 });
    expect(body.text).toBe("Motion denied.");
  });

  test("drops a trailing odd byte", async () => {
    mockFetch(() => new Response(streamOf([bytes(5), bytes(4, 5), bytes(8, 9)]), { status: 200 }));
    const out = await collect(ttsPcm("Noted."));
    expect(out.reduce((n, c) => n + c.length, 0)).toBe(16);
    for (const c of out) expect(c.length % 2).toBe(0);
  });

  test("HTTP error ends quietly with no audio", async () => {
    mockFetch(() => new Response(JSON.stringify({ detail: "bad key" }), { status: 401, headers: { "content-type": "application/json" } }));
    expect(await collect(ttsPcm("Noted."))).toEqual([]);
  });

  test("a request that never answers ends after the first-byte timeout", async () => {
    mockFetch(() => new Promise<Response>(() => {}));
    const t0 = performance.now();
    expect(await collect(ttsPcm("Noted."))).toEqual([]);
    expect(performance.now() - t0).toBeLessThan(1500);
  });

  test("a stalled stream plays its first chunk but never caches the partial reply", async () => {
    const text = "Try this reply again.";
    mockFetch(() => new Response(streamOf([bytes(6)], true), { status: 200 }));
    const first = await prepareSpeech(text, ttsPcm);
    expect(first).not.toBeNull();
    const partial = first![Symbol.asyncIterator]();
    expect((await partial.next()).value).toEqual(new Uint8Array(bytes(6)));
    await expect(partial.next()).rejects.toThrow();
    expect(cachedAudioSeconds(text)).toBe(0);

    mockFetch(() => new Response(streamOf([bytes(8)]), { status: 200 }));
    const second = await prepareSpeech(text, ttsPcm);
    expect(second).not.toBeNull();
    const retried = second![Symbol.asyncIterator]();
    expect((await retried.next()).value).toEqual(new Uint8Array(bytes(8)));
    expect((await retried.next()).done).toBe(true);
    expect(calls).toHaveLength(1);
  });

  test("missing env or empty text: no request", async () => {
    mockFetch(() => new Response(streamOf([bytes(4)]), { status: 200 }));
    expect(await collect(ttsPcm("   "))).toEqual([]);
    delete process.env.ELEVENLABS_API_KEY;
    expect(await collect(ttsPcm("Noted."))).toEqual([]);
    expect(calls.length).toBe(0);
  });
});

describe("ttsMp3", () => {
  test("returns the whole mp3 from the non-streaming endpoint", async () => {
    mockFetch(() => new Response(streamOf([[0x49, 0x44, 0x33], bytes(10)]), { status: 200, headers: { "content-type": "audio/mpeg" } }));
    const mp3 = await ttsMp3("I'll allow it.");
    expect(mp3.length).toBe(13);
    expect([...mp3.slice(0, 3)]).toEqual([0x49, 0x44, 0x33]);
    const { url } = calls[0]!;
    expect(url).toContain("/v1/text-to-speech/voice123");
    expect(url).not.toContain("/stream");
    expect(url).toContain("output_format=mp3_44100_64");
  });

  test("rejects when unavailable, on HTTP errors, and on timeout", async () => {
    mockFetch(() => new Response("nope", { status: 500 }));
    await expect(ttsMp3("Noted.")).rejects.toThrow();
    mockFetch(() => new Promise<Response>(() => {}));
    Object.assign(ttsLimits, { totalMs: 200 });
    await expect(ttsMp3("Noted.")).rejects.toThrow();
    delete process.env.ELEVENLABS_VOICE_ID;
    await expect(ttsMp3("Noted.")).rejects.toThrow(/unavailable/);
  });
});
