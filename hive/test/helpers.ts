// Test helpers: a Hive with mocked director/tts/lines, and a websocket client that records everything.
import { setQuiet } from "../src/bus";
import type { BusMessage, Channel, Item, Role, Verdict } from "../src/protocol";
import { startHive, type Hive, type HiveDeps, type HiveOptions, type JudgeInputLike, type JudgeResultLike, type LineInfo } from "../src/server";

setQuiet(true);

export const TOKEN = "test-token";

const LINE_LIST: LineInfo[] = [
  { id: "ear_practice", channel: "ear", text: "Testing. You smell like coffee." },
  { id: "ear_not_now", channel: "ear", text: "Not now, I'm monologuing." },
  { id: "ear_swarm_arrived", channel: "ear", text: "They're on your flower. Don't panic." },
  { id: "ear_hornet_done", channel: "ear", text: "Not all wasps are bad. Just that guy." },
  { id: "ear_callback_rose", channel: "ear", text: "You brought me a funeral rose first. Still mad." },
  { id: "ear_callback_default", channel: "ear", text: "You found us a real flower." },
  { id: "ear_canned_unclear", channel: "ear", text: "What IS that? Bring me a FLOWER." },
  { id: "room_canned_unclear", channel: "room", text: "What IS that? Bring me a FLOWER." },
  { id: "room_ack_allow", channel: "room", text: "I'll allow it." },
  { id: "ear_verdict_purple_bee_flower", channel: "ear", text: "PURPLE! That glows." },
  { id: "room_verdict_purple_bee_flower", channel: "room", text: "PURPLE. Now THAT glows. Girls... go." },
  { id: "room_verdict_red_rose", channel: "room", text: "We can't see red. Is this a funeral?" },
  { id: "ear_verdict_spray_bottle", channel: "ear", text: "Put. It. Down." },
  { id: "room_verdict_spray_bottle", channel: "room", text: "BUG-B-GONE ULTRA?!" },
];

export interface MockDeps extends HiveDeps {
  judgeCalls: JudgeInputLike[];
  ttsCalls: string[];
}

export function mockDeps(over: Partial<HiveDeps> = {}): MockDeps {
  const lines = Object.fromEntries(LINE_LIST.map((l) => [l.id, l]));
  const judgeCalls: JudgeInputLike[] = [];
  const ttsCalls: string[] = [];
  const deps: MockDeps = {
    judgeCalls,
    ttsCalls,
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
      LINE_LIST.find((l) => l.channel === channel && l.text === text)?.id ?? null,
    callbackLine: (memory) => (memory.first_item === "red_rose" ? "ear_callback_rose" : "ear_callback_default"),
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
