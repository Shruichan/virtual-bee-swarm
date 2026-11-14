// Test helpers: a Hive with mocked director/tts/stt/router (real line, station and scene tables), and a
// websocket client that records everything.
import { setQuiet } from "../src/bus";
import { ACK_LINES, LINES, SCENE_TABLE, STATION_TABLE } from "../src/lines";
import { BINARY_KIND, type BeatName, type BusMessage, type Channel, type Item, type Role, type Verdict } from "../src/protocol";
import {
  startHive,
  type Hive,
  type HiveDeps,
  type HiveOptions,
  type JudgeInputLike,
  type JudgeResultLike,
  type LineInfo,
  type RouteResultLike,
  type SttResultLike,
} from "../src/server";

setQuiet(true);

export const TOKEN = "test-token";

// v1 verdict lines for the glasses scan tests (the v3 catalog only keeps the person deflection).
const SCAN_LINES: LineInfo[] = [
  { id: "ear_verdict_purple_bee_flower", channel: "ear", text: "PURPLE! That glows." },
  { id: "room_verdict_purple_bee_flower", channel: "room", text: "PURPLE. Now THAT glows. Girls... go." },
  { id: "room_verdict_red_rose", channel: "room", text: "We can't see red. Is this a funeral?" },
  { id: "ear_verdict_spray_bottle", channel: "ear", text: "Put. It. Down." },
  { id: "room_verdict_spray_bottle", channel: "room", text: "BUG-B-GONE ULTRA?!" },
  { id: "ear_swarm_arrived", channel: "ear", text: "They're on your flower. Don't panic." },
];

export interface MockDeps extends HiveDeps {
  judgeCalls: JudgeInputLike[];
  ttsCalls: string[];
  sttCalls: Uint8Array[];
  routeCalls: Array<{ text: string; ctx: { beat: BeatName; recent: string[] } }>;
}

/** A tiny stand-in for router.ts: keyword intents, plus "ask" for questions. */
export function mockRoute(text: string): RouteResultLike {
  const t = text.toLowerCase();
  const base = { answer: null, ms: 3, path: "keyword" as const };
  if (/put it out|undo|go back/.test(t)) return { intent: "undo", topic: null, ...base };
  for (const topic of ["fire", "rain", "flowers", "spray", "winter"] as const) {
    if (t.includes(topic)) return { intent: "what_if", topic, ...base };
  }
  if (t.endsWith("?")) return { intent: "ask", topic: null, answer: "We make honey. It's a whole thing.", ms: 40, path: "claude" };
  return { intent: "unknown", topic: null, answer: null, ms: 40, path: "claude" };
}

/** Mock stt: the "audio" is the transcript's UTF-8 bytes, so a test can say anything over the mic. */
export function mockTranscribe(pcm: Uint8Array): SttResultLike {
  return { text: new TextDecoder().decode(pcm).trim(), ms: 12, provider: "mock" };
}

export function mockDeps(over: Partial<HiveDeps> = {}): MockDeps {
  const lines: Record<string, LineInfo> = { ...LINES, ...Object.fromEntries(SCAN_LINES.map((l) => [l.id, l])) };
  const judgeCalls: JudgeInputLike[] = [];
  const ttsCalls: string[] = [];
  const sttCalls: Uint8Array[] = [];
  const routeCalls: MockDeps["routeCalls"] = [];
  const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9]+/g, " ").trim();
  const deps: MockDeps = {
    judgeCalls,
    ttsCalls,
    sttCalls,
    routeCalls,
    judge: async (input): Promise<JudgeResultLike> => {
      judgeCalls.push(input);
      return { item: "purple_bee_flower", verdict: "good", confidence: 0.95, seen: "purple salvia in a pot", line: null, ms: 5, model: "mock", fallback: false };
    },
    ttsAvailable: () => false,
    ttsSampleRate: 16000,
    ttsPcm: async function* (text: string) {
      ttsCalls.push(text);
      yield new Uint8Array([1, 2, 3]); // odd lengths on purpose: the bus must keep frames 2-byte aligned
      yield new Uint8Array([4, 5, 6, 7, 8]);
    },
    ttsMp3: async (text: string) => {
      ttsCalls.push(text);
      return new Uint8Array([0x49, 0x44, 0x33, 1, 2, 3]);
    },
    lines,
    lineText: (id: string) => lines[id]?.text ?? "",
    verdictLine: (item: Item, verdict: Verdict, channel: Channel) => {
      for (const id of [`${channel}_verdict_${item}_${verdict}`, `${channel}_verdict_${item}`]) if (lines[id]) return id;
      return null;
    },
    lineIdForText: (text: string, channel: Channel) =>
      Object.values(lines).find((l) => l.channel === channel && norm(l.text) === norm(text))?.id ?? null,
    transcribe: async (pcm) => {
      sttCalls.push(pcm);
      return mockTranscribe(pcm);
    },
    sttStatus: () => ({ elevenlabs: false, whisper: true, provider: "whisper" }),
    route: async (text, ctx) => {
      routeCalls.push({ text, ctx });
      return mockRoute(text);
    },
    stations: STATION_TABLE,
    scenes: SCENE_TABLE,
    ackLines: ACK_LINES,
    ...over,
  };
  return deps;
}

export function makeHive(over: Partial<HiveDeps> = {}, opts: Partial<Omit<HiveOptions, "deps">> = {}): Hive & { deps: MockDeps } {
  const deps = mockDeps(over);
  const hive = startHive({ deps, port: 0, hostname: "127.0.0.1", token: TOKEN, heartbeatMs: 60_000, host: "test", ...opts });
  return Object.assign(hive, { deps });
}

type Event = { kind: "json"; msg: any } | { kind: "bin"; data: Uint8Array };

export class TestClient {
  events: Event[] = [];
  private waiters: Array<() => void> = [];

  private constructor(readonly ws: WebSocket) {
    ws.binaryType = "arraybuffer";
    ws.onmessage = (e) => {
      if (typeof e.data === "string") this.events.push({ kind: "json", msg: JSON.parse(e.data) });
      else this.events.push({ kind: "bin", data: new Uint8Array(e.data as ArrayBuffer) });
      for (const w of this.waiters.splice(0)) w();
    };
  }

  static async connect(hive: Hive, role: Role, token = TOKEN): Promise<TestClient> {
    const ws = new WebSocket(`ws://127.0.0.1:${hive.port}/bus?role=${role}&token=${token}`);
    await new Promise<void>((resolve, reject) => {
      ws.onopen = () => resolve();
      ws.onerror = () => reject(new Error("ws error"));
    });
    const c = new TestClient(ws);
    await Bun.sleep(5); // let the server register the client
    return c;
  }

  get msgs(): any[] {
    return this.events.filter((e) => e.kind === "json").map((e) => (e as { msg: any }).msg);
  }

  of(type: string): any[] {
    return this.msgs.filter((m) => m.type === type);
  }

  send(msg: BusMessage | Record<string, unknown>): void {
    this.ws.send(JSON.stringify(msg));
  }

  /** One binary MIC_PCM16 frame: [0x03][pcm]. */
  sendMic(pcm: Uint8Array): void {
    const frame = new Uint8Array(pcm.length + 1);
    frame[0] = BINARY_KIND.MIC_PCM16;
    frame.set(pcm, 1);
    this.ws.send(frame);
  }

  /** listen.start, the "audio" (mock stt reads it back as text) in small frames, listen.end. */
  say(text: string, source: "quest" | "glasses" = "quest", frameBytes = 6): void {
    const bytes = new TextEncoder().encode(text.length % 2 ? `${text} ` : text);
    this.send({ type: "listen.start", source, mode: "ptt" });
    for (let i = 0; i < bytes.length; i += frameBytes) this.sendMic(bytes.subarray(i, i + frameBytes));
    this.send({ type: "listen.end", source });
  }

  /** Resolve with the first event (already received or future) matching pred. */
  async waitEvent(pred: (e: Event) => boolean, timeoutMs = 2000): Promise<Event> {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      const hit = this.events.find(pred);
      if (hit) return hit;
      const left = deadline - Date.now();
      if (left <= 0) throw new Error(`timed out; got ${JSON.stringify(this.msgs.map((m) => m.type))}`);
      await new Promise<void>((r) => {
        const t = setTimeout(r, left);
        this.waiters.push(() => {
          clearTimeout(t);
          r();
        });
      });
    }
  }

  async waitFor(type: string, pred: (m: any) => boolean = () => true, timeoutMs = 2000): Promise<any> {
    const e = await this.waitEvent((e) => e.kind === "json" && e.msg.type === type && pred(e.msg), timeoutMs);
    return (e as { msg: any }).msg;
  }

  close(): void {
    this.ws.close();
  }
}

export async function post(hive: Hive, path: string, body: unknown, token: string | null = TOKEN): Promise<{ status: number; json: any }> {
  const res = await fetch(`${hive.url}${path}${token ? `?token=${token}` : ""}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
  return { status: res.status, json: await res.json().catch(() => null) };
}

/** Put the Hive into a beat the way Unity does it. */
export async function setBeat(quest: TestClient, other: TestClient, name: string, n: number): Promise<void> {
  quest.send({ type: "beat.set", n, name });
  await other.waitFor("beat.set", (m) => m.n === n);
}
