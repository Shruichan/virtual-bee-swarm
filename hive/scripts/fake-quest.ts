#!/usr/bin/env bun
// Fake Quest: connects to the bus as role=quest, prints every message, and behaves like Unity's beat machine
// (accepts beat.request, reports beat.set). It can also play a station run and talk into the mic.
//
//   bun scripts/fake-quest.ts --url http://localhost:8240 --token $HIVE_TOKEN \
//     --beats hook,meet,smoker --choices smoker:swat,smoker:smoker,feed:store_honey,feed:sugar_syrup \
//     --say-wav what-if-fire.wav --interval 3
//
//   # the whole scripted demo, in order (beats, choices and utterances interleaved):
//   bun scripts/fake-quest.ts --interval 1 --steps "hook,meet,smoker,smoker:swat,smoker:smoker,feed,feed:store_honey,\
//     feed:sugar_syrup,wasps,wasps:smaller_door,heat,heat:pebble_dish,whatif,say:fire.wav,say:putout.wav,escape,t0,map"
//
// Flags: --url (http/https/ws/wss base), --token (default $HIVE_TOKEN), --beats a,b,c (names or numbers),
//        --choices station:choice,... (choice.made, each `correct` from the contract), --interval seconds between
//        steps (default 3), --say-wav file (any PCM/float WAV; sent as listen.start + 100 ms MIC_PCM16 frames at
//        16 kHz mono + listen.end, paced in real time unless --fast), --cancel (end the listen with cancel:true),
//        --steps a,b,c (instead of --beats/--choices/--say-wav: run these in order; a step is a beat name or number,
//        station:choice, say:<wav> (waits for the Hive's intent, or an empty transcript, up to 15 s) or wait:<seconds>),
//        --no-swarm, --exit-after seconds. Never plays audio.
import { readFileSync } from "node:fs";
import { BEATS, BINARY_KIND, CHOICES, CORRECT_CHOICE, PROTOCOL_VERSION, STATIONS, type Station } from "../src/protocol";

export type Step =
  | { kind: "beat"; n: number }
  | { kind: "choice"; station: Station; choice: string }
  | { kind: "say"; file: string }
  | { kind: "wait"; seconds: number };

/** "hook,smoker:swat,say:fire.wav,wait:2" -> steps. Throws with a readable message on a bad entry. */
export function parseSteps(spec: string): Step[] {
  return spec
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean)
    .map((s): Step => {
      if (s.startsWith("say:")) {
        const file = s.slice(4).trim();
        if (!file) throw new Error(`bad step "${s}": say:<wav file>`);
        return { kind: "say", file };
      }
      if (s.startsWith("wait:")) {
        const seconds = Number(s.slice(5));
        if (!Number.isFinite(seconds) || seconds < 0) throw new Error(`bad step "${s}": wait:<seconds>`);
        return { kind: "wait", seconds };
      }
      if (s.includes(":")) {
        const [station, choice] = s.split(":", 2) as [Station, string];
        if (!STATIONS.includes(station) || !(CHOICES[station] as readonly string[]).includes(choice)) {
          throw new Error(`bad step "${s}": station:choice, e.g. ${STATIONS.map((st) => `${st}:${CHOICES[st][0]}`).join(",")}`);
        }
        return { kind: "choice", station, choice };
      }
      const n = /^\d+$/.test(s) ? Number(s) : BEATS.indexOf(s as (typeof BEATS)[number]);
      if (n < 0 || n >= BEATS.length) throw new Error(`bad step "${s}": a beat (one of ${BEATS.join(", ")}), station:choice, say:<wav> or wait:<s>`);
      return { kind: "beat", n };
    });
}

// ---------------------------------------------------------------- WAV -> 16 kHz mono PCM16

export interface Wav { sampleRate: number; channels: number; samples: Float32Array /* mono, -1..1 */ }

/** Parse a RIFF WAVE file (PCM 8/16/24/32-bit or IEEE float 32/64, any channel count) and mix it down to mono. */
export function parseWav(buf: Uint8Array): Wav {
  const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  const tag = (at: number) => String.fromCharCode(buf[at]!, buf[at + 1]!, buf[at + 2]!, buf[at + 3]!);
  if (buf.length < 12 || tag(0) !== "RIFF" || tag(8) !== "WAVE") throw new Error("not a RIFF/WAVE file");
  let fmt: { format: number; channels: number; rate: number; bits: number } | null = null;
  let data: Uint8Array | null = null;
  for (let at = 12; at + 8 <= buf.length; ) {
    const id = tag(at);
    const size = dv.getUint32(at + 4, true);
    const body = at + 8;
    if (id === "fmt ") {
      let format = dv.getUint16(body, true);
      if (format === 0xfffe && size >= 26) format = dv.getUint16(body + 24, true); // WAVE_FORMAT_EXTENSIBLE subformat
      fmt = { format, channels: dv.getUint16(body + 2, true), rate: dv.getUint32(body + 4, true), bits: dv.getUint16(body + 14, true) };
    } else if (id === "data") {
      data = buf.subarray(body, Math.min(buf.length, body + size));
    }
    at = body + size + (size % 2); // chunks are word-aligned
  }
  if (!fmt || !data) throw new Error("WAV has no fmt or data chunk");
  const { format, channels, rate, bits } = fmt;
  if (channels < 1 || rate < 1) throw new Error("WAV fmt chunk is broken");
  const bytes = bits / 8;
  const frames = Math.floor(data.length / (bytes * channels));
  const ddv = new DataView(data.buffer, data.byteOffset, data.byteLength);
  const read = (off: number): number => {
    if (format === 3 && bits === 32) return ddv.getFloat32(off, true);
    if (format === 3 && bits === 64) return ddv.getFloat64(off, true);
    if (format !== 1) throw new Error(`unsupported WAV format ${format} (${bits}-bit)`);
    if (bits === 8) return (ddv.getUint8(off) - 128) / 128;
    if (bits === 16) return ddv.getInt16(off, true) / 32768;
    if (bits === 24) return ((ddv.getUint8(off) | (ddv.getUint8(off + 1) << 8) | (ddv.getInt8(off + 2) << 16)) as number) / 8388608;
    if (bits === 32) return ddv.getInt32(off, true) / 2147483648;
    throw new Error(`unsupported PCM bit depth ${bits}`);
  };
  const samples = new Float32Array(frames);
  for (let i = 0; i < frames; i++) {
    let sum = 0;
    for (let c = 0; c < channels; c++) sum += read((i * channels + c) * bytes);
    samples[i] = sum / channels;
  }
  return { sampleRate: rate, channels, samples };
}

/** Linear-interpolation resample to `rate` and convert to PCM16 little-endian. */
export function toPcm16(wav: Wav, rate = 16000): Uint8Array {
  const n = Math.max(0, Math.round((wav.samples.length * rate) / wav.sampleRate));
  const out = new Uint8Array(n * 2);
  const dv = new DataView(out.buffer);
  const step = wav.sampleRate / rate;
  for (let i = 0; i < n; i++) {
    const pos = i * step;
    const j = Math.floor(pos);
    const a = wav.samples[j] ?? 0;
    const b = wav.samples[j + 1] ?? a;
    const v = Math.max(-1, Math.min(1, a + (b - a) * (pos - j)));
    dv.setInt16(i * 2, Math.round(v * 32767), true);
  }
  return out;
}

// ---------------------------------------------------------------- the fake Quest

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

function main(): void {
  const opt = args(process.argv.slice(2));
  const base = String(opt.url ?? "http://localhost:8240").replace(/\/+$/, "");
  const token = String(opt.token ?? process.env.HIVE_TOKEN ?? "");
  const interval = Number(opt.interval ?? 3) * 1000;
  const fast = Boolean(opt.fast);

  const wsUrl = `${base.replace(/^http/, "ws")}/bus?role=quest&token=${encodeURIComponent(token)}`;
  const t0 = Date.now();
  const stamp = () => `${new Date().toTimeString().slice(0, 8)} +${((Date.now() - t0) / 1000).toFixed(2)}s`;
  const print = (...a: unknown[]) => console.log(stamp(), ...a);

  const beatIndex = (s: string): number => {
    const n = /^\d+$/.test(s) ? Number(s) : BEATS.indexOf(s as (typeof BEATS)[number]);
    if (n < 0 || n >= BEATS.length) throw new Error(`unknown beat "${s}" (one of ${BEATS.join(", ")})`);
    return n;
  };
  const list = (v: string | true | undefined) => (typeof v === "string" ? v.split(",").map((s) => s.trim()).filter(Boolean) : []);
  const beats = list(opt.beats).map(beatIndex);
  const choices = list(opt.choices).map((s) => {
    const [station, choice] = s.split(":") as [Station, string];
    if (!STATIONS.includes(station) || !(CHOICES[station] as readonly string[]).includes(choice)) {
      throw new Error(`bad --choices entry "${s}": use station:choice, e.g. ${STATIONS.map((st) => `${st}:${CHOICES[st][0]}`).join(",")}`);
    }
    return { station, choice };
  });
  const loadWav = (file: string): Uint8Array => {
    const wav = parseWav(new Uint8Array(readFileSync(file)));
    const out = toPcm16(wav);
    print(`wav ${file}: ${wav.sampleRate} Hz x${wav.channels} -> ${(out.length / 32000).toFixed(2)} s at 16 kHz mono`);
    return out;
  };
  let pcm: Uint8Array | null = null;
  if (typeof opt["say-wav"] === "string") pcm = loadWav(opt["say-wav"]);
  const steps = typeof opt.steps === "string" ? parseSteps(opt.steps) : [];
  const stepAudio = new Map<string, Uint8Array>(); // decoded up front: a bad file fails before connecting
  for (const s of steps) if (s.kind === "say" && !stepAudio.has(s.file)) stepAudio.set(s.file, loadWav(s.file));
  /** Resolved by the Hive's answer to the utterance in flight: an intent, or an empty transcript (heard nothing). */
  let heardBack: ((what: string) => void) | null = null;

  let ws: WebSocket | null = null;
  let beat = 0;
  let backoff = 500;
  let planStarted = false;
  let tts: { id: string; bytes: number; rate: number; t: number } | null = null;
  let listenEndedAt = 0;

  const open = () => ws?.readyState === WebSocket.OPEN;
  function send(msg: object, quiet = false): void {
    if (open()) {
      ws!.send(JSON.stringify(msg));
      if (!quiet) print("->", JSON.stringify(msg));
    } else print("(not connected, dropped)", JSON.stringify(msg));
  }
  function setBeat(n: number): void {
    beat = n;
    send({ type: "beat.set", n, name: BEATS[n] });
  }

  async function speak(audio: Uint8Array): Promise<void> {
    const FRAME = 3200; // 100 ms of 16 kHz PCM16
    send({ type: "listen.start", source: "quest", mode: "ptt" });
    const start = Date.now();
    for (let at = 0, i = 0; at < audio.length; at += FRAME, i++) {
      if (!open()) return print("socket closed mid-utterance");
      const chunk = audio.subarray(at, at + FRAME);
      const frame = new Uint8Array(chunk.length + 1);
      frame[0] = BINARY_KIND.MIC_PCM16;
      frame.set(chunk, 1);
      ws!.send(frame);
      if (!fast) await Bun.sleep(Math.max(0, start + (i + 1) * 100 - Date.now()));
    }
    print(`   sent ${(audio.length / 32000).toFixed(2)} s of mic audio in ${Math.ceil(audio.length / FRAME)} frames`);
    listenEndedAt = Date.now();
    send(opt.cancel ? { type: "listen.end", source: "quest", cancel: true } : { type: "listen.end", source: "quest" });
  }

  /** Speak and wait (<= 15 s) until the Hive has transcribed and routed it, so the next step can't overtake it. */
  async function speakAndWait(audio: Uint8Array): Promise<void> {
    listenEndedAt = 0; // only answers after this utterance's listen.end count
    const answered = new Promise<string>((resolve) => {
      const timer = setTimeout(() => resolve("no answer within 15 s"), 15_000);
      heardBack = (what) => {
        clearTimeout(timer);
        resolve(what);
      };
    });
    await speak(audio);
    print(`   utterance done: ${await answered}`);
    heardBack = null;
  }

  async function runPlan(): Promise<void> {
    for (const s of steps) {
      if (s.kind === "beat") setBeat(s.n);
      else if (s.kind === "choice") send({ type: "choice.made", station: s.station, choice: s.choice, correct: CORRECT_CHOICE[s.station] === s.choice });
      else if (s.kind === "say") await speakAndWait(stepAudio.get(s.file)!);
      else await Bun.sleep(s.seconds * 1000);
      if (s.kind !== "wait") await Bun.sleep(interval);
    }
    for (const n of beats) {
      setBeat(n);
      await Bun.sleep(interval);
    }
    for (const { station, choice } of choices) {
      send({ type: "choice.made", station, choice, correct: CORRECT_CHOICE[station] === choice });
      await Bun.sleep(interval);
    }
    if (pcm) await speak(pcm);
    if (steps.length || beats.length || choices.length || pcm) print(`plan done (beat ${beat} ${BEATS[beat]})`);
  }

  function onText(raw: string): void {
    let m: any;
    try {
      m = JSON.parse(raw);
    } catch {
      return print("<- (bad JSON)", raw.slice(0, 200));
    }
    const since = listenEndedAt && (m.type === "transcript" || m.type === "intent") ? `  (+${Date.now() - listenEndedAt} ms after listen.end)` : "";
    print("<-", m.type, JSON.stringify(m) + since);
    if (heardBack && listenEndedAt && (m.type === "intent" || (m.type === "transcript" && !m.text))) {
      heardBack(m.type === "intent" ? `intent ${m.name}${m.topic ? `/${m.topic}` : ""}` : "heard nothing");
    }
    switch (m.type) {
      case "welcome":
        if (!planStarted) {
          planStarted = true;
          void runPlan().catch((err) => print("plan failed:", err));
        }
        break;
      case "beat.request": {
        const n = m.next ? Math.min(beat + 1, BEATS.length - 1) : m.n;
        if (typeof n === "number" && n >= 0 && n < BEATS.length) setBeat(n); // the real Unity may refuse
        break;
      }
      case "scan.verdict":
        if (!opt["no-swarm"] && m.socket) setTimeout(() => send({ type: "swarm.arrived", socket: m.socket }), 1500);
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
}

if (import.meta.main) main();
