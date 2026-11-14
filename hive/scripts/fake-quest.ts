#!/usr/bin/env bun
// Fake Quest: connects to the bus as role=quest, prints every message, and behaves like Unity's
// beat machine (accepts beat.request, reports beat.set, lands the swarm after a verdict).
//
//   bun scripts/fake-quest.ts --url http://localhost:8240 --token $HIVE_TOKEN --beats roses,beeline --interval 5
//
// Flags: --url (http/https/ws/wss base), --token (default $HIVE_TOKEN), --beats a,b,c (names or numbers),
//        --interval seconds between --beats (default 3), --no-swarm, --exit-after seconds.
import { BEATS, BINARY_KIND, PROTOCOL_VERSION } from "../src/protocol";

function args(argv: string[]): Record<string, string | true> {
  const out: Record<string, string | true> = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]!;
    if (!a.startsWith("--")) continue;
    const [k, v] = a.slice(2).split("=", 2) as [string, string | undefined];
    if (v !== undefined) out[k] = v;
    else if (argv[i + 1] && !argv[i + 1]!.startsWith("--")) out[k] = argv[++i]!;
    else out[k] = true;
  }
  return out;
}

const opt = args(process.argv.slice(2));
const base = String(opt.url ?? "http://localhost:8240").replace(/\/+$/, "");
const token = String(opt.token ?? process.env.HIVE_TOKEN ?? "");
const interval = Number(opt.interval ?? 3) * 1000;
const swarm = !opt["no-swarm"];
const plan = typeof opt.beats === "string" ? opt.beats.split(",").map((s) => s.trim()).filter(Boolean) : [];

const wsUrl = `${base.replace(/^http/, "ws")}/bus?role=quest&token=${encodeURIComponent(token)}`;
const t0 = Date.now();
const stamp = () => `${new Date().toTimeString().slice(0, 8)} +${((Date.now() - t0) / 1000).toFixed(2)}s`;
const print = (...a: unknown[]) => console.log(stamp(), ...a);

function beatIndex(s: string): number {
  const n = /^\d+$/.test(s) ? Number(s) : BEATS.indexOf(s as (typeof BEATS)[number]);
  if (n < 0 || n >= BEATS.length) throw new Error(`unknown beat "${s}" (one of ${BEATS.join(", ")})`);
  return n;
}
const planned = plan.map(beatIndex);

let ws: WebSocket | null = null;
let beat = 0;
let backoff = 500;
let planStarted = false;
let tts: { id: string; bytes: number; rate: number; t: number } | null = null;

function send(msg: object): void {
  if (ws?.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(msg));
    print("->", JSON.stringify(msg));
  } else print("(not connected, dropped)", JSON.stringify(msg));
}

function setBeat(n: number): void {
  beat = n;
  send({ type: "beat.set", n, name: BEATS[n] });
}

async function runPlan(): Promise<void> {
  for (const n of planned) {
    setBeat(n);
    await Bun.sleep(interval);
  }
  if (planned.length) print(`beat plan done (now ${beat} ${BEATS[beat]})`);
}

function onText(raw: string): void {
  let m: any;
  try {
    m = JSON.parse(raw);
  } catch {
    return print("<- (bad JSON)", raw.slice(0, 200));
  }
  print("<-", m.type, JSON.stringify(m));
  switch (m.type) {
    case "welcome":
      if (!planStarted) {
        planStarted = true;
        void runPlan();
      }
      break;
    case "beat.request": {
      const n = m.next ? Math.min(beat + 1, BEATS.length - 1) : m.n;
      if (typeof n === "number" && n >= 0 && n < BEATS.length) setBeat(n); // the real Unity may refuse
      break;
    }
    case "scan.verdict":
      if (swarm && m.socket) setTimeout(() => send({ type: "swarm.arrived", socket: m.socket }), 1500);
      break;
    case "tts.start":
      tts = { id: m.tts_id, bytes: 0, rate: m.sample_rate, t: Date.now() };
      break;
    case "tts.end":
      if (tts) print(`   tts ${tts.id}: ${(tts.bytes / 2 / tts.rate).toFixed(2)} s of PCM in ${Date.now() - tts.t} ms`);
      tts = null;
      break;
  }
}

function onBinary(buf: ArrayBuffer): void {
  const b = new Uint8Array(buf);
  const kind = b[0];
  if (kind === BINARY_KIND.TTS_PCM16) {
    if (tts) tts.bytes += b.length - 1;
    return; // too chatty to print every chunk
  }
  const name = kind === BINARY_KIND.SCAN_PHOTO ? "SCAN_PHOTO" : `kind ${kind}`;
  print(`<- binary ${name} ${b.length - 1} bytes${kind === BINARY_KIND.SCAN_PHOTO && b[1] === 0xff && b[2] === 0xd8 ? " (JPEG)" : ""}`);
}

function connect(): void {
  print("connecting", wsUrl.replace(/token=[^&]*/, "token=***"));
  ws = new WebSocket(wsUrl);
  ws.binaryType = "arraybuffer";
  ws.onopen = () => {
    backoff = 500;
    print("connected");
    send({ type: "hello", role: "quest", client: "fake-quest", version: PROTOCOL_VERSION });
    send({ type: "beat.set", n: beat, name: BEATS[beat] }); // Unity reports its beat on (re)connect
  };
  ws.onmessage = (e) => (typeof e.data === "string" ? onText(e.data) : onBinary(e.data as ArrayBuffer));
  ws.onclose = (e) => {
    print(`closed (${e.code}${e.reason ? ` ${e.reason}` : ""}); retry in ${backoff} ms`);
    setTimeout(connect, backoff);
    backoff = Math.min(backoff * 2, 5000);
  };
  ws.onerror = () => print("socket error");
}

if (opt["exit-after"]) setTimeout(() => process.exit(0), Number(opt["exit-after"]) * 1000);
process.on("SIGINT", () => process.exit(0));
connect();
