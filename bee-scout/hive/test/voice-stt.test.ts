import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import { KEYTERMS, checkWhisper, cleanTranscript, pcmToWav, setSttFetch, sttLimits, sttStatus, transcribe } from "../src/stt";

// Every HTTP call goes through this mock: ElevenLabs (api.elevenlabs.io) and the whisper sidecar (WHISPER_URL).
type Handler = (req: Request) => Response | Promise<Response>;
interface Seen { url: string; method: string; headers: Headers; body: Uint8Array; form: FormData | null }

const WHISPER = "http://whisper.test:9/transcribe";
const LIMITS = { ...sttLimits };
let seen: Seen[] = [];

function mockHttp(handlers: { eleven?: Handler; whisper?: Handler; health?: Handler }) {
  seen = [];
  setSttFetch((async (input: RequestInfo | URL, init?: RequestInit) => {
    const req = new Request(input as any, init);
    const body = new Uint8Array(await req.clone().arrayBuffer());
    const form = (req.headers.get("content-type") ?? "").includes("multipart/form-data") ? await req.clone().formData() : null;
    seen.push({ url: req.url, method: req.method, headers: req.headers, body, form });
    const u = new URL(req.url);
    const h = u.hostname.endsWith("elevenlabs.io") ? handlers.eleven : u.pathname === "/health" ? handlers.health : handlers.whisper;
    if (!h) throw new TypeError(`unmocked fetch: ${req.url}`);
    return h(req);
  }) as typeof fetch);
}

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
const hang = (req: Request) =>
  new Promise<Response>((_, reject) => req.signal.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError"))));
const elevenOk = (text: string) => () => json({ language_code: "en", language_probability: 0.99, text, words: [] });
const whisperOk = (text: string) => () => json({ text, ms: 300, audio_s: 1, model: "base.en", device: "cpu" });

const pcm = (seconds: number) => {
  const a = new Uint8Array(Math.round(seconds * 16000) * 2);
  for (let i = 0; i < a.length; i += 2) a[i] = (i * 7) & 0xff;
  return a;
};

beforeEach(() => {
  process.env.WHISPER_URL = WHISPER;
  delete process.env.ELEVENLABS_API_KEY;
  delete process.env.STT_PROVIDER;
  delete process.env.ELEVENLABS_STT_MODEL;
  mockHttp({}); // fail closed: nothing reaches the network
});

afterEach(() => {
  Object.assign(sttLimits, LIMITS);
  delete process.env.ELEVENLABS_API_KEY;
  delete process.env.STT_PROVIDER;
  delete process.env.WHISPER_URL;
  setSttFetch(null);
});

describe("wav header", () => {
  test("44-byte RIFF/WAVE PCM16 mono 16 kHz header + the samples", () => {
    const data = new Uint8Array([1, 2, 3, 4, 5, 6]);
    const wav = pcmToWav(data);
    const v = new DataView(wav.buffer);
    const ascii = (at: number, n: number) => String.fromCharCode(...wav.subarray(at, at + n));
    expect(wav.length).toBe(50);
    expect(ascii(0, 4)).toBe("RIFF");
    expect(v.getUint32(4, true)).toBe(42);
    expect(ascii(8, 4)).toBe("WAVE");
    expect(ascii(12, 4)).toBe("fmt ");
    expect(v.getUint32(16, true)).toBe(16);
    expect(v.getUint16(20, true)).toBe(1); // PCM
    expect(v.getUint16(22, true)).toBe(1); // mono
    expect(v.getUint32(24, true)).toBe(16000);
    expect(v.getUint32(28, true)).toBe(32000);
    expect(v.getUint16(32, true)).toBe(2);
    expect(v.getUint16(34, true)).toBe(16);
    expect(ascii(36, 4)).toBe("data");
    expect(v.getUint32(40, true)).toBe(6);
    expect([...wav.subarray(44)]).toEqual([1, 2, 3, 4, 5, 6]);
  });
});

describe("provider selection", () => {
  test("no ElevenLabs key: whisper only, raw PCM body", async () => {
    mockHttp({ whisper: whisperOk("What if a fire starts?") });
    const audio = pcm(1.2);
    const r = await transcribe(audio);
    expect(r).toMatchObject({ text: "What if a fire starts?", provider: "whisper" });
    expect(r.error).toBeUndefined();
    expect(seen.length).toBe(1);
    expect(seen[0]!.url).toBe(WHISPER);
    expect(seen[0]!.method).toBe("POST");
    expect(seen[0]!.headers.get("content-type")).toBe("application/octet-stream");
    expect(seen[0]!.body).toEqual(audio);
    expect(sttStatus()).toMatchObject({ elevenlabs: false, whisper: true, provider: "whisper" });
  });

  test("key set: ElevenLabs first, scribe_v3 multipart with a WAV file, keyterms and English", async () => {
    process.env.ELEVENLABS_API_KEY = "el_test_not_real";
    mockHttp({ eleven: elevenOk("Put it out!"), whisper: whisperOk("wrong provider") });
    const r = await transcribe(pcm(0.8));
    expect(r).toMatchObject({ text: "Put it out!", provider: "elevenlabs" });
    expect(seen.length).toBe(1);
    const req = seen[0]!;
    expect(req.url).toContain("/v1/speech-to-text");
    expect(req.headers.get("xi-api-key")).toBe("el_test_not_real");
    const form = req.form!;
    expect(form.get("model_id")).toBe("scribe_v3");
    expect(form.get("language_code")).toBe("en");
    expect(form.get("tag_audio_events")).toBe("false");
    expect(form.getAll("keyterms")).toEqual(KEYTERMS);
    const file = form.get("file") as File;
    const bytes = new Uint8Array(await file.arrayBuffer());
    expect(String.fromCharCode(...bytes.subarray(0, 4))).toBe("RIFF");
    expect(bytes.length).toBe(44 + 0.8 * 16000 * 2);
    expect(sttStatus()).toMatchObject({ elevenlabs: true, provider: "elevenlabs" });
  });

  test("ELEVENLABS_STT_MODEL overrides the model", async () => {
    process.env.ELEVENLABS_API_KEY = "el_test_not_real";
    process.env.ELEVENLABS_STT_MODEL = "scribe_v2";
    mockHttp({ eleven: elevenOk("ok") });
    await transcribe(pcm(0.5));
    expect(seen[0]!.form!.get("model_id")).toBe("scribe_v2");
  });

  test("STT_PROVIDER=whisper ignores the key; =elevenlabs never falls back", async () => {
    process.env.ELEVENLABS_API_KEY = "el_test_not_real";
    process.env.STT_PROVIDER = "whisper";
    mockHttp({ eleven: elevenOk("wrong"), whisper: whisperOk("from whisper") });
    expect(await transcribe(pcm(0.5))).toMatchObject({ text: "from whisper", provider: "whisper" });
    expect(sttStatus().provider).toBe("whisper");

    process.env.STT_PROVIDER = "elevenlabs";
    mockHttp({ eleven: () => json({ detail: "boom" }, 500), whisper: whisperOk("must not be used") });
    const r = await transcribe(pcm(0.5));
    expect(r.text).toBe("");
    expect(r.provider).toBe("none");
    expect(r.error).toContain("elevenlabs:");
    expect(seen.every((s) => !s.url.startsWith(WHISPER))).toBe(true);
  });

  test("STT_PROVIDER=elevenlabs without a key fails cleanly", async () => {
    process.env.STT_PROVIDER = "elevenlabs";
    const r = await transcribe(pcm(0.5));
    expect(r).toMatchObject({ text: "", provider: "none" });
    expect(r.error).toContain("ELEVENLABS_API_KEY");
  });
});

describe("fallback (auto)", () => {
  test("ElevenLabs error -> whisper at once", async () => {
    process.env.ELEVENLABS_API_KEY = "el_test_not_real";
    mockHttp({ eleven: () => json({ detail: "quota" }, 401), whisper: whisperOk("what if it rains") });
    const t0 = performance.now();
    const r = await transcribe(pcm(1));
    expect(r).toMatchObject({ text: "what if it rains", provider: "whisper" });
    expect(performance.now() - t0).toBeLessThan(sttLimits.hedgeMs);
  });

  test("slow ElevenLabs -> whisper starts after hedgeMs and wins; the slow call is aborted", async () => {
    process.env.ELEVENLABS_API_KEY = "el_test_not_real";
    sttLimits.hedgeMs = 60;
    let aborted = false;
    mockHttp({
      eleven: (req) => {
        req.signal.addEventListener("abort", () => (aborted = true));
        return hang(req);
      },
      whisper: whisperOk("put it out"),
    });
    const t0 = performance.now();
    const r = await transcribe(pcm(1), { timeoutMs: 3000 });
    expect(r).toMatchObject({ text: "put it out", provider: "whisper" });
    expect(performance.now() - t0).toBeLessThan(1000);
    await Bun.sleep(10);
    expect(aborted).toBe(true);
  });

  test("ElevenLabs answers before the hedge: whisper is never called", async () => {
    process.env.ELEVENLABS_API_KEY = "el_test_not_real";
    mockHttp({ eleven: elevenOk("hello"), whisper: whisperOk("no") });
    await transcribe(pcm(1));
    expect(seen.map((s) => new URL(s.url).hostname)).toEqual(["api.elevenlabs.io"]);
  });

  test("both down -> empty text, both errors listed", async () => {
    process.env.ELEVENLABS_API_KEY = "el_test_not_real";
    mockHttp({
      eleven: () => json({ detail: "x" }, 503),
      whisper: () => {
        throw new TypeError("connection refused");
      },
    });
    const r = await transcribe(pcm(1));
    expect(r).toMatchObject({ text: "", provider: "none" });
    expect(r.error).toContain("elevenlabs:");
    expect(r.error).toContain("whisper: connection refused");
    expect(sttStatus().whisper).toBe(false);
  });

  test("everything hangs -> empty text on time", async () => {
    process.env.ELEVENLABS_API_KEY = "el_test_not_real";
    sttLimits.hedgeMs = 50;
    mockHttp({ eleven: hang, whisper: hang });
    const t0 = performance.now();
    const r = await transcribe(pcm(1), { timeoutMs: 250 });
    const took = performance.now() - t0;
    expect(took).toBeGreaterThanOrEqual(240);
    expect(took).toBeLessThan(1000);
    expect(r).toMatchObject({ text: "", provider: "none" });
    expect(r.error).toBe("elevenlabs: timeout; whisper: timeout");
  });

  test("whisper bad JSON / HTTP error -> empty text, no throw", async () => {
    mockHttp({ whisper: () => new Response("<html>oops</html>", { status: 200 }) });
    expect((await transcribe(pcm(0.5))).text).toBe("");
    mockHttp({ whisper: () => json({ error: "x", text: "" }, 500) });
    const r = await transcribe(pcm(0.5));
    expect(r.error).toBe("whisper: HTTP 500");
  });
});

describe("input handling", () => {
  test("empty, too short and non-bytes input: no HTTP at all", async () => {
    mockHttp({ whisper: whisperOk("no") });
    expect(await transcribe(new Uint8Array(0))).toMatchObject({ text: "", provider: "none", error: "no_audio" });
    expect(await transcribe(new Uint8Array(3000))).toMatchObject({ text: "", error: "too_short" });
    expect((await transcribe(undefined as unknown as Uint8Array)).error).toBe("no_audio");
    expect((await transcribe("hello" as unknown as Uint8Array)).error).toBe("no_audio");
    expect(seen.length).toBe(0);
  });

  test("odd byte counts are trimmed to whole samples; long audio is capped", async () => {
    mockHttp({ whisper: whisperOk("x") });
    await transcribe(new Uint8Array(32001));
    expect(seen[0]!.body.length).toBe(32000);
    sttLimits.maxSeconds = 1;
    await transcribe(pcm(3));
    expect(seen[1]!.body.length).toBe(32000);
  });

  test("audio-event tags and whitespace are cleaned", async () => {
    mockHttp({ whisper: whisperOk("  (laughter) what if [music]  a fire\nstarts ") });
    expect((await transcribe(pcm(1))).text).toBe("what if a fire starts");
    expect(cleanTranscript(42)).toBe("");
  });
});

describe("status", () => {
  test("checkWhisper hits /health on the whisper host and feeds sttStatus", async () => {
    mockHttp({ health: () => json({ ok: true, model: "base.en" }) });
    expect(await checkWhisper()).toBe(true);
    expect(seen[0]!.url).toBe("http://whisper.test:9/health");
    expect(sttStatus().whisper).toBe(true);
    mockHttp({ health: () => json({ ok: false }, 503) });
    expect(await checkWhisper()).toBe(false);
    expect(sttStatus().whisper).toBe(false);
  });

  test("sttStatus refreshes a stale health check in the background", async () => {
    mockHttp({ health: () => json({ ok: true }) }); // setSttFetch resets the health cache
    expect(sttStatus().whisper).toBe(false); // unknown yet: the check runs in the background
    await Bun.sleep(20);
    expect(sttStatus().whisper).toBe(true);
    expect(seen.filter((s) => s.url.endsWith("/health")).length).toBe(1);
  });
});
