#!/usr/bin/env bun
// Fake glasses: press + scan through the glasses HTTP API, print timings, and (optionally) listen on the
// bus as role=glasses for ear lines. Never plays audio: it only downloads audio_url and prints its size.
//
//   bun scripts/fake-glasses.ts --url http://localhost:8240 --token $HIVE_TOKEN --heard "lavender"
//   bun scripts/fake-glasses.ts --photo-url https://.../photo.jpg --count 20 --gap 2     # scan gate: p50/p90
//   bun scripts/fake-glasses.ts --plea "my grandma keeps bees"
//   bun scripts/fake-glasses.ts --utterance "what if a fire starts"                     # second mic (v3 voice)
//
// Flags: --url, --token (default $HIVE_TOKEN), --heard, --photo-url, --count N (default 1), --gap seconds,
//        --plea text, --utterance text (POST /glasses/utterance; repeat with a | separator: "a|b"),
//        --no-listen (skip the bus), --linger seconds to keep listening after (default 3).
import { PROTOCOL_VERSION, type PressResponse, type ScanResponse, type UtteranceResponse } from "../src/protocol";

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
const heard = typeof opt.heard === "string" ? opt.heard : undefined;
const photoUrl = typeof opt["photo-url"] === "string" ? opt["photo-url"] : undefined;
const count = Math.max(0, Number(opt.count ?? (heard || photoUrl ? 1 : 0)));
const gap = Number(opt.gap ?? 2) * 1000;
const linger = Number(opt.linger ?? 3) * 1000;
const utterances = typeof opt.utterance === "string" ? opt.utterance.split("|").map((u) => u.trim()).filter(Boolean) : [];

const t0 = Date.now();
const stamp = () => `${new Date().toTimeString().slice(0, 8)} +${((Date.now() - t0) / 1000).toFixed(2)}s`;
const print = (...a: unknown[]) => console.log(stamp(), ...a);

async function post<T>(path: string, body: unknown, timeoutMs = 30_000): Promise<{ status: number; json: T | null; ms: number }> {
  const t = Date.now();
  const res = await fetch(`${base}${path}?token=${encodeURIComponent(token)}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
    signal: AbortSignal.timeout(timeoutMs),
  });
  const json = (await res.json().catch(() => null)) as T | null;
  return { status: res.status, json, ms: Date.now() - t };
}

let ws: WebSocket | null = null;
function listen(): Promise<void> {
  const url = `${base.replace(/^http/, "ws")}/bus?role=glasses&token=${encodeURIComponent(token)}`;
  return new Promise((resolve) => {
    ws = new WebSocket(url);
    const done = setTimeout(resolve, 3000);
    ws.onopen = () => {
      ws!.send(JSON.stringify({ type: "hello", role: "glasses", client: "fake-glasses", version: PROTOCOL_VERSION }));
      print("bus connected (role=glasses)");
      clearTimeout(done);
      resolve();
    };
    ws.onmessage = (e) => print("<- bus", typeof e.data === "string" ? e.data : `binary ${(e.data as ArrayBuffer).byteLength} bytes`);
    ws.onclose = (e) => print(`bus closed (${e.code})`);
    ws.onerror = () => print("bus error");
  });
}

async function scanOnce(i: number): Promise<number | null> {
  const pressed_at = Date.now();
  const press = await post<PressResponse>("/glasses/press", { pressed_at, button: "main", press_type: "short" });
  print(`#${i} press ${press.status} in ${press.ms} ms:`, JSON.stringify(press.json));
  if (!press.json?.allowed) return null;
  const scan = await post<ScanResponse>("/glasses/scan", { scan_id: press.json.scan_id, photo_url: photoUrl, heard, pressed_at });
  const total = Date.now() - pressed_at;
  print(`#${i} scan ${scan.status} in ${scan.ms} ms (press->answer ${total} ms, hive says ${scan.json?.ms} ms):`, JSON.stringify(scan.json));
  if (scan.json?.audio_url) {
    const t = Date.now();
    try {
      const res = await fetch(scan.json.audio_url, { signal: AbortSignal.timeout(20_000) });
      const bytes = (await res.arrayBuffer()).byteLength;
      print(`#${i} audio ${res.status} ${bytes} bytes in ${Date.now() - t} ms (not played) -> press->audio ${Date.now() - pressed_at} ms`);
    } catch (err) {
      print(`#${i} audio fetch failed:`, err instanceof Error ? err.message : err);
    }
  }
  return total;
}

const pct = (xs: number[], p: number) => [...xs].sort((a, b) => a - b)[Math.min(xs.length - 1, Math.floor((p / 100) * xs.length))];

async function main(): Promise<void> {
  if (!opt["no-listen"]) await listen();
  const times: number[] = [];
  for (let i = 1; i <= count; i++) {
    try {
      const t = await scanOnce(i);
      if (t !== null) times.push(t);
    } catch (err) {
      print(`#${i} failed:`, err instanceof Error ? err.message : err);
    }
    if (i < count) await Bun.sleep(gap);
  }
  if (times.length > 1) print(`press->answer over ${times.length} scans: p50 ${pct(times, 50)} ms, p90 ${pct(times, 90)} ms, max ${Math.max(...times)} ms`);
  if (typeof opt.plea === "string") {
    const r = await post("/glasses/plea", { text: opt.plea });
    print(`plea ${r.status} in ${r.ms} ms`, JSON.stringify(r.json));
  }
  for (const [i, text] of utterances.entries()) {
    try {
      const r = await post<UtteranceResponse>("/glasses/utterance", { text, at: Date.now() });
      print(`utterance ${r.status} in ${r.ms} ms:`, JSON.stringify(r.json));
      if (r.json?.audio_url) print(`   ear audio at ${r.json.audio_url} (not played)`);
    } catch (err) {
      print("utterance failed:", err instanceof Error ? err.message : err);
    }
    if (i < utterances.length - 1) await Bun.sleep(gap);
  }
  const idle = count === 0 && typeof opt.plea !== "string" && utterances.length === 0;
  if (idle) print("nothing to do: pass --heard, --photo-url, --plea or --utterance (listening only)");
  if (ws && linger > 0) await Bun.sleep(idle ? 1e9 : linger);
  ws?.close();
  process.exit(0);
}

process.on("SIGINT", () => process.exit(0));
void main();
