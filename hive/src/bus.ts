// The bus: one websocket hub for the Quest, Beat Board, monitor and glasses.
// JSON text frames carry BusMessages; binary frames are [kind u8][payload].
import type { Server, ServerWebSocket, WebSocketHandler } from "bun";
import { timingSafeEqual } from "node:crypto";
import {
  BEATS,
  BINARY_KIND,
  ITEMS,
  type BinaryKind,
  type BusMessage,
  type Channel,
  type Item,
  type OrbisRequest,
  type Role,
  type Say,
} from "./protocol";
import { isValidBeat, reset, type GroupState } from "./state";

export const ROLES: readonly Role[] = ["quest", "board", "monitor", "glasses"];
const OBSERVERS: Role[] = ["board", "monitor"];

// ---------------------------------------------------------------- small shared helpers

let quiet = process.env.HIVE_QUIET === "1";
export function setQuiet(q: boolean): void {
  quiet = q;
}
export function log(...args: unknown[]): void {
  if (quiet) return;
  const d = new Date();
  const t = `${d.toTimeString().slice(0, 8)}.${String(d.getMilliseconds()).padStart(3, "0")}`; // local time
  console.log(t, ...args);
}

/** Constant-time token check. No expected token = open (dev mode). */
export function checkToken(provided: string | null | undefined, expected: string | null): boolean {
  if (!expected) return true;
  if (!provided) return false;
  const a = Buffer.from(provided);
  const b = Buffer.from(expected);
  return a.length === b.length && timingSafeEqual(a, b);
}

export function binaryFrame(kind: BinaryKind, payload: Uint8Array): Uint8Array {
  const out = new Uint8Array(payload.length + 1);
  out[0] = kind;
  out.set(payload, 1);
  return out;
}

const isObj = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);
const str = (v: unknown, max = 500): string => (typeof v === "string" ? v.slice(0, max) : "");

// ---------------------------------------------------------------- bus

export interface ClientData {
  id: string;
  role: Role;
  client: string;
  missed: number; // heartbeats without a pong / message
}
export type Client = ServerWebSocket<ClientData>;
export type SayExtra = Partial<Pick<Say, "tts_id" | "audio_url" | "caption">>;

/** What the bus needs from the rest of the Hive. */
export interface BusHooks {
  lineText(id: string): string;
  onVerdictForce(item: Item): void;
  onPlea(text: string): void;
  onGroupReset?(group_id: string): void;
  onBeat?(n: number): void;
  onHornetCooked?(overshoot: boolean): void;
  onOrbisRequest?(msg: OrbisRequest): void;
}

export interface BusOptions {
  token: string | null;
  host: string;
  heartbeatMs?: number;
}

export class Bus {
  readonly clients = new Set<Client>();
  private seq = 0;
  private heartbeat: ReturnType<typeof setInterval> | null = null;
  private ttsTail: Promise<void> = Promise.resolve();
  private lastSwarm = new Map<number, number>();

  constructor(
    private state: GroupState,
    private hooks: BusHooks,
    private opts: BusOptions,
  ) {}

  start(): void {
    const ms = this.opts.heartbeatMs ?? 10_000;
    this.heartbeat ??= setInterval(() => this.beat(), ms);
  }

  stop(): void {
    if (this.heartbeat) clearInterval(this.heartbeat);
    this.heartbeat = null;
    for (const ws of this.clients) ws.close(1001, "server stopping");
    this.clients.clear();
  }

  /** Ping everyone; drop clients that missed two heartbeats in a row. */
  private beat(): void {
    for (const ws of this.clients) {
      if (ws.data.missed >= 2) {
        log(`bus: dropping dead ${ws.data.id}`);
        this.clients.delete(ws);
        try {
          ws.terminate();
        } catch {}
        this.observe({ type: "log", text: `${ws.data.role} dropped (no heartbeat)` });
        continue;
      }
      ws.data.missed++;
      try {
        ws.ping();
      } catch {}
    }
  }

  /** Handle GET /bus. Returns a Response on rejection, undefined when upgraded. */
  upgrade(req: Request, server: Server<ClientData>): Response | undefined {
    const url = new URL(req.url);
    const role = url.searchParams.get("role") as Role | null;
    if (!role || !ROLES.includes(role)) return new Response("bad role", { status: 400 });
    const token = url.searchParams.get("token") ?? req.headers.get("x-hive-token");
    if (!checkToken(token, this.opts.token)) return new Response("bad token", { status: 401 });
    const data: ClientData = { id: `${role}-${++this.seq}`, role, client: "", missed: 0 };
    return server.upgrade(req, { data }) ? undefined : new Response("websocket upgrade failed", { status: 400 });
  }

  readonly websocket: WebSocketHandler<ClientData> = {
    maxPayloadLength: 1024 * 1024,
    idleTimeout: 60,
    open: (ws) => {
      this.clients.add(ws);
      log(`bus: + ${ws.data.id} (${this.clients.size} clients)`);
      this.observe({ type: "log", text: `${ws.data.role} connected` }, ws);
    },
    close: (ws, code) => {
      if (this.clients.delete(ws)) {
        log(`bus: - ${ws.data.id} code=${code}`);
        this.observe({ type: "log", text: `${ws.data.role} disconnected` });
      }
    },
    pong: (ws) => {
      ws.data.missed = 0;
    },
    message: (ws, raw) => {
      ws.data.missed = 0;
      if (typeof raw !== "string") {
        log(`bus: ignoring ${raw.byteLength}-byte binary frame from ${ws.data.id}`);
        return;
      }
      let msg: unknown;
      try {
        msg = JSON.parse(raw);
      } catch {
        log(`bus: bad JSON from ${ws.data.id}: ${raw.slice(0, 120)}`);
        this.send(ws, { type: "error", message: "bad JSON" });
        return;
      }
      if (!isObj(msg) || typeof msg.type !== "string") {
        this.send(ws, { type: "error", message: "message needs a string 'type'" });
        return;
      }
      try {
        this.route(ws, msg);
      } catch (err) {
        log(`bus: error routing ${msg.type} from ${ws.data.id}:`, err);
      }
    },
    error: (err, ws) => {
      log(`bus: handler error for ${ws.data.id}:`, err);
    },
  };

  // ------------------------------------------------------------ sending

  send(ws: Client, msg: BusMessage): void {
    try {
      ws.send(JSON.stringify(msg));
    } catch (err) {
      log(`bus: send to ${ws.data.id} failed:`, err);
    }
  }

  sendBinary(ws: Client, frame: Uint8Array): void {
    try {
      ws.send(frame);
    } catch (err) {
      log(`bus: binary send to ${ws.data.id} failed:`, err);
    }
  }

  broadcast(msg: BusMessage, roles: readonly Role[] | "all" = "all", except?: Client): number {
    const text = JSON.stringify(msg);
    let n = 0;
    for (const ws of this.clients) {
      if (ws === except || (roles !== "all" && !roles.includes(ws.data.role))) continue;
      try {
        ws.send(text);
        n++;
      } catch {}
    }
    return n;
  }

  broadcastBinary(kind: BinaryKind, payload: Uint8Array, roles: readonly Role[]): number {
    const frame = binaryFrame(kind, payload);
    let n = 0;
    for (const ws of this.clients) {
      if (!roles.includes(ws.data.role)) continue;
      this.sendBinary(ws, frame);
      n++;
    }
    return n;
  }

  /** Board + monitor: logs, latencies, "what fired". */
  observe(msg: BusMessage, except?: Client): void {
    this.broadcast(msg, OBSERVERS, except);
  }

  count(role: Role): number {
    let n = 0;
    for (const ws of this.clients) if (ws.data.role === role) n++;
    return n;
  }

  counts(): Record<Role, number> {
    return { quest: this.count("quest"), board: this.count("board"), monitor: this.count("monitor"), glasses: this.count("glasses") };
  }

  /**
   * Play a line on ONE device (room = Quest, ear = glasses); board/monitor get a copy for captions/logs.
   * deliver=false only tells the observers (the device already played it).
   */
  say(channel: Channel, line_id: string | null, extra: SayExtra = {}, deliver = true): void {
    const msg: Say = {
      type: "say",
      channel,
      line_id,
      tts_id: extra.tts_id ?? null,
      audio_url: extra.audio_url ?? null,
      caption: extra.caption || (line_id ? this.hooks.lineText(line_id) : ""),
    };
    const device: Role = channel === "room" ? "quest" : "glasses";
    this.broadcast(msg, deliver ? [device, ...OBSERVERS] : OBSERVERS);
  }

  /**
   * Stream one live room line to the Quest: tts.start, binary PCM16 chunks, tts.end.
   * Streams are queued so only one plays at a time.
   */
  enqueueRoomTts(tts_id: string, caption: string, sampleRate: number, chunks: () => AsyncIterable<Uint8Array>): Promise<void> {
    const job = () => this.streamTts(tts_id, caption, sampleRate, chunks);
    const run = this.ttsTail.then(job, job);
    this.ttsTail = run.catch(() => {});
    return run;
  }

  private async streamTts(tts_id: string, caption: string, sampleRate: number, chunks: () => AsyncIterable<Uint8Array>): Promise<void> {
    if (this.count("quest") === 0) {
      log(`tts ${tts_id}: no Quest connected, skipping`);
      return;
    }
    const t0 = Date.now();
    this.broadcast({ type: "tts.start", tts_id, sample_rate: sampleRate, caption }, ["quest"]);
    let bytes = 0;
    let carry: Uint8Array | null = null; // keep frames 2-byte aligned for PCM16
    let it: AsyncIterator<Uint8Array> | null = null;
    try {
      it = chunks()[Symbol.asyncIterator]();
      for (;;) {
        const next = await withTimeout(it.next(), 10_000, "tts chunk");
        if (next.done) break;
        let chunk = next.value;
        if (carry) {
          const merged = new Uint8Array(carry.length + chunk.length);
          merged.set(carry);
          merged.set(chunk, carry.length);
          chunk = merged;
          carry = null;
        }
        if (chunk.length % 2 === 1) {
          carry = chunk.slice(-1);
          chunk = chunk.subarray(0, chunk.length - 1);
        }
        if (chunk.length === 0) continue;
        if (bytes === 0) log(`tts ${tts_id}: first audio after ${Date.now() - t0} ms`);
        bytes += chunk.length;
        if (this.broadcastBinary(BINARY_KIND.TTS_PCM16, chunk, ["quest"]) === 0) {
          log(`tts ${tts_id}: Quest went away mid-stream`);
          break;
        }
        if (Date.now() - t0 > 30_000) {
          log(`tts ${tts_id}: stream over 30 s, cutting`);
          break;
        }
      }
    } catch (err) {
      log(`tts ${tts_id}: stream failed:`, err instanceof Error ? err.message : err);
    } finally {
      it?.return?.()?.catch?.(() => {});
      this.broadcast({ type: "tts.end", tts_id }, ["quest"]);
      const secs = bytes / 2 / sampleRate;
      this.observe({ type: "log", text: `tts ${tts_id}: ${secs.toFixed(1)} s of audio in ${Date.now() - t0} ms` });
    }
  }

  // ------------------------------------------------------------ routing

  private setBeat(n: number, origin?: Client): void {
    this.state.beat = n;
    this.broadcast({ type: "beat.set", n, name: BEATS[n]! }, "all", origin);
    log(`beat -> ${n} ${BEATS[n]}${origin ? "" : " (hive applied: no Quest connected)"}`);
    this.hooks.onBeat?.(n);
  }

  private route(ws: Client, m: Record<string, unknown>): void {
    const role = ws.data.role;
    const boardOnly = () => {
      if (role === "board") return true;
      this.send(ws, { type: "error", message: `${m.type} is only accepted from the board` });
      return false;
    };
    const questOnly = () => {
      if (role === "quest") return true;
      this.send(ws, { type: "error", message: `${m.type} is only accepted from the quest` });
      return false;
    };

    switch (m.type) {
      case "hello": {
        // Pages re-send hello every 10 s as a heartbeat; log only the first one.
        if (!ws.data.client) log(`bus: hello from ${ws.data.id} (${str(m.client, 80) || "?"}, v${m.version})`);
        ws.data.client = str(m.client, 80) || "?";
        this.send(ws, {
          type: "welcome",
          group_id: this.state.group_id,
          beat: this.state.beat,
          mode: this.state.mode,
          server_time: Date.now(),
          host: this.opts.host,
        });
        return;
      }

      case "beat.set": {
        if (!questOnly()) return;
        if (!isValidBeat(m.n)) return this.send(ws, { type: "error", message: `bad beat ${String(m.n)}` });
        return this.setBeat(m.n, ws);
      }

      case "beat.request": {
        if (!boardOnly()) return;
        const next = m.next === true;
        const n = next ? Math.min(this.state.beat + 1, BEATS.length - 1) : m.n;
        if (!isValidBeat(n)) return this.send(ws, { type: "error", message: `bad beat ${String(m.n)}` });
        if (this.count("quest") > 0) {
          this.broadcast(next ? { type: "beat.request", next: true } : { type: "beat.request", n }, ["quest"]);
          this.observe({ type: "log", text: `beat.request ${next ? "next" : `${n} ${BEATS[n]}`} -> quest` });
        } else {
          this.setBeat(n); // no Quest: the Hive is the clock (rehearsal, Field Mode)
        }
        return;
      }

      case "mode": {
        if (!boardOnly()) return;
        if (m.mode !== "duo" && m.mode !== "solo") return this.send(ws, { type: "error", message: "mode must be duo|solo" });
        this.state.mode = m.mode;
        this.broadcast({ type: "mode", mode: m.mode });
        return;
      }

      case "group.reset": {
        if (!boardOnly()) return;
        reset(this.state, str(m.group_id, 64) || undefined);
        this.lastSwarm.clear();
        this.hooks.onGroupReset?.(this.state.group_id);
        this.broadcast({ type: "group.reset", group_id: this.state.group_id });
        log(`group reset -> ${this.state.group_id}`);
        return;
      }

      case "hum.set": {
        if (!boardOnly()) return;
        const v = typeof m.value === "number" && Number.isFinite(m.value) ? Math.min(1, Math.max(0, m.value)) : null;
        if (v === null) return this.send(ws, { type: "error", message: "hum.set needs a number value" });
        this.broadcast({ type: "hum.set", value: v }, ["quest"]);
        return;
      }

      case "verdict.force": {
        if (!boardOnly()) return;
        if (!ITEMS.includes(m.item as Item)) return this.send(ws, { type: "error", message: `unknown item ${String(m.item)}` });
        this.hooks.onVerdictForce(m.item as Item);
        return;
      }

      case "say.force": {
        if (!boardOnly()) return;
        const line_id = str(m.line_id, 120);
        if (!line_id || (m.channel !== "ear" && m.channel !== "room")) {
          return this.send(ws, { type: "error", message: "say.force needs line_id and channel ear|room" });
        }
        this.say(m.channel, line_id);
        return;
      }

      case "orbis.request": {
        if (!boardOnly()) return;
        if (m.want !== "live" && m.want !== "clips" && m.want !== "off") {
          return this.send(ws, { type: "error", message: "orbis.request want must be live|clips|off" });
        }
        const msg: OrbisRequest = { type: "orbis.request", want: m.want };
        if (m.stage !== undefined && m.stage !== null) {
          // Unity parses stage into an int and the relay only takes whole scene numbers.
          if (!Number.isInteger(m.stage) || (m.stage as number) < 0) {
            return this.send(ws, { type: "error", message: "orbis.request stage must be a whole number >= 0" });
          }
          msg.stage = m.stage as number;
        }
        this.broadcast(msg, ["quest"]);
        this.hooks.onOrbisRequest?.(msg);
        return;
      }

      case "orbis": {
        this.broadcast(
          {
            type: "orbis",
            state: (["off", "priming", "ready", "failed", "ended"] as const).find((s) => s === m.state) ?? "off",
            stage: typeof m.stage === "number" ? m.stage : 0,
            fallback_clip: str(m.fallback_clip, 200) || null,
          },
          "all",
          ws,
        );
        return;
      }

      case "swarm.arrived": {
        if (!questOnly()) return;
        const socket = typeof m.socket === "number" ? m.socket : 0;
        this.observe({ type: "swarm.arrived", socket });
        const last = this.lastSwarm.get(socket) ?? 0;
        if (Date.now() - last < 3000) return; // one buzz per landing
        this.lastSwarm.set(socket, Date.now());
        this.say("ear", "ear_swarm_arrived");
        return;
      }

      case "hornet.cooked": {
        if (!questOnly()) return;
        this.state.memory.hornet_overshoot = m.overshoot === true;
        this.observe({ type: "hornet.cooked", overshoot: m.overshoot === true });
        this.hooks.onHornetCooked?.(m.overshoot === true);
        return;
      }

      case "say": {
        // Unity owns the timeline, so it may cue ear asides ("HORNET. Behind Goggles.") through the Hive.
        if (!questOnly()) return;
        const channel: Channel = m.channel === "room" ? "room" : "ear";
        const extra = { tts_id: str(m.tts_id, 120) || null, audio_url: str(m.audio_url, 500) || null, caption: str(m.caption) };
        this.say(channel, str(m.line_id, 120) || null, extra, channel === "ear");
        return;
      }

      case "plea": {
        // glasses, or the operator typing it in when transcription fails
        if (role !== "glasses" && role !== "board") return this.send(ws, { type: "error", message: "plea comes from glasses or board" });
        const text = str(m.text).trim();
        if (text) this.hooks.onPlea(text);
        return;
      }

      case "latency": {
        const ms = typeof m.ms === "number" && Number.isFinite(m.ms) ? Math.round(m.ms) : NaN;
        if (!str(m.hop) || Number.isNaN(ms)) return;
        this.observe({ type: "latency", hop: str(m.hop, 80), ms, source: role }, ws);
        return;
      }

      case "log": {
        this.observe({ type: "log", text: str(m.text), source: role }, ws);
        return;
      }

      case "error": {
        log(`bus: client ${ws.data.id} reported error: ${str(m.message)}`);
        this.observe({ type: "log", text: `${role} error: ${str(m.message)}`, source: role }, ws);
        return;
      }

      default:
        this.send(ws, { type: "error", message: `unhandled type ${String(m.type).slice(0, 40)} from ${role}` });
    }
  }
}

export function withTimeout<T>(p: Promise<T>, ms: number, what: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout>;
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new Error(`${what} timed out after ${ms} ms`)), ms);
  });
  return Promise.race([p, timeout]).finally(() => clearTimeout(timer));
}
