// The Hive: HTTP (Hono) + the websocket bus (Bun.serve). It turns Quest choices and the player's voice into
// Beeswax lines, window (Orbis) scenes and Unity fx, and keeps the glasses scan pipeline for the real-prop mode.
// Run: bun run src/server.ts   (env: PORT, HIVE_TOKEN, ORBIS_RELAY_URL, PUBLIC_BASE_URL, STT/ROUTE timeouts, ...)
import type { Server } from "bun";
import { Hono, type Context } from "hono";
import { cors } from "hono/cors";
import { hostname } from "node:os";
import sharp from "sharp";
import {
  BEATS,
  BINARY_KIND,
  CORRECT_CHOICE,
  INTENTS,
  ITEMS,
  PROTOCOL_VERSION,
  SCAN_BEATS,
  SCENES,
  SOCKET_OF,
  VERDICTS,
  VOICE_BEATS,
  WHATIF_TOPICS,
  type BeatName,
  type Channel,
  type Choice,
  type DirectorOutput,
  type FxName,
  type IntentName,
  type Item,
  type OrbisRequest,
  type OrbisState,
  type OrbisStatus,
  type PressResponse,
  type Role,
  type RouterOutput,
  type ScanPath,
  type ScanResponse,
  type Scene,
  type Station,
  type UtteranceResponse,
  type Verdict,
  type WhatIfTopic,
} from "./protocol";
import { Bus, checkToken, log, withTimeout, type Client, type ClientData } from "./bus";
import { beatName, createState, recordChoice, remember, rememberHeard, scoreText, type GroupState } from "./state";
import { speechVerdict, voiceKeywords } from "./speech";
import { boardHtml } from "./board";
import { monitorHtml } from "./monitor";

// ---------------------------------------------------------------- injected dependencies
// director/tts/stt/router are other builders' modules, and lines.ts holds the data tables. They are injected so
// tests can mock them and so a broken module degrades the Hive (keywords + operator paths) instead of killing it.

export interface LineInfo { id: string; channel: Channel; text: string; beat?: string; tags?: string[] }
export interface JudgeInputLike {
  photo?: Uint8Array;
  mime?: string;
  heard?: string;
  beat: BeatName;
  memory: { scanned: Item[] };
}
export interface JudgeResultLike extends DirectorOutput { ms: number; model: string; fallback: boolean }
/** stt.ts SttResult */
export interface SttResultLike { text: string; ms: number; provider: string; error?: string }
/** router.ts RouteResult */
export interface RouteResultLike extends RouterOutput { ms: number; path: "keyword" | "claude"; error?: string }
export interface StationOutcomeLike { correct: boolean; line_id: string; scene: Scene; fx?: FxName }
export interface StationEntryLike { intro_line: string | null; choices: Partial<Record<Choice, StationOutcomeLike>> }
export interface SceneEntryLike { prompt: string; seed: number; clip: string }

export interface HiveDeps {
  judge(input: JudgeInputLike, opts?: { timeoutMs?: number }): Promise<JudgeResultLike>;
  prewarm?(): Promise<void>;
  ttsPcm(text: string): AsyncIterable<Uint8Array>;
  ttsMp3(text: string): Promise<Uint8Array>;
  ttsAvailable(): boolean;
  ttsSampleRate: number;
  verdictLine(item: Item, verdict: Verdict, channel: Channel): string | null;
  lineText(id: string): string;
  lines: Record<string, LineInfo>;
  /** A pre-rendered line whose text matches, so canned text needs no live TTS. */
  lineIdForText?(text: string, channel: Channel): string | null;
  /** stt.ts: never throws; text "" on failure. */
  transcribe(pcm16le16k: Uint8Array, opts?: { timeoutMs?: number }): Promise<SttResultLike>;
  sttStatus?(): { elevenlabs: boolean; whisper: boolean; provider: string };
  /** router.ts: never throws. */
  route(text: string, ctx: { beat: BeatName; recent: string[] }, opts?: { timeoutMs?: number }): Promise<RouteResultLike>;
  /** hive/lines/stations.json and orbis_scenes.json, and the canned acknowledgement line ids. */
  stations: Partial<Record<Station, StationEntryLike>>;
  scenes: Partial<Record<Scene, SceneEntryLike>>;
  ackLines: string[];
}

const OFFLINE_JUDGE: HiveDeps["judge"] = async () => ({
  item: "unclear",
  verdict: "roast",
  confidence: 0,
  seen: "director offline",
  line: null,
  ms: 0,
  model: "none",
  fallback: true,
});

/** Degraded router: keywords only (used when router.ts is missing or blows the Hive's own deadline). */
function keywordRoute(text: string): RouteResultLike {
  const k = voiceKeywords(text);
  return { intent: k.intent, topic: k.topic, answer: null, ms: 0, path: "keyword" };
}

// Imported by URL so the Hive still type-checks and starts while a sibling module is missing or broken.
async function importSibling(name: string): Promise<Record<string, unknown>> {
  return (await import(new URL(`./${name}.ts`, import.meta.url).href)) as Record<string, unknown>;
}
const fn = <T>(v: unknown): T | undefined => (typeof v === "function" ? (v as T) : undefined);

/** Load the real modules; any that fail to load are replaced by safe stubs (logged loudly). */
export async function loadDeps(): Promise<HiveDeps> {
  const deps: HiveDeps = {
    judge: OFFLINE_JUDGE,
    ttsPcm: async function* () {},
    ttsMp3: async () => {
      throw new Error("tts offline");
    },
    ttsAvailable: () => false,
    ttsSampleRate: 16000,
    verdictLine: () => null,
    lineText: () => "",
    lines: {},
    transcribe: async () => ({ text: "", ms: 0, provider: "none", error: "stt offline" }),
    route: async (text) => keywordRoute(text),
    stations: {},
    scenes: {},
    ackLines: [],
  };
  try {
    const lines = await import("./lines");
    deps.lines = lines.LINES;
    deps.verdictLine = lines.verdictLine;
    deps.lineText = lines.lineText;
    deps.lineIdForText = lines.lineIdForText;
    deps.stations = lines.STATION_TABLE;
    deps.scenes = lines.SCENE_TABLE;
    deps.ackLines = lines.ACK_LINES;
  } catch (err) {
    log("WARNING: src/lines.ts failed to load; no pre-rendered lines, stations or scenes:", err);
  }
  try {
    const director = await import("./director");
    deps.judge = director.judge;
    deps.prewarm = director.prewarm;
  } catch (err) {
    log("WARNING: src/director.ts failed to load; Claude scan path disabled (speech/operator only):", err);
  }
  try {
    const tts = await import("./tts");
    deps.ttsPcm = tts.ttsPcm;
    deps.ttsMp3 = tts.ttsMp3;
    deps.ttsAvailable = tts.ttsAvailable;
    deps.ttsSampleRate = tts.TTS_SAMPLE_RATE;
  } catch (err) {
    log("WARNING: src/tts.ts failed to load; no live voice:", err);
  }
  try {
    const stt = await importSibling("stt");
    const transcribe = fn<HiveDeps["transcribe"]>(stt.transcribe);
    if (!transcribe) throw new Error("no transcribe() export");
    deps.transcribe = transcribe;
    deps.sttStatus = fn<NonNullable<HiveDeps["sttStatus"]>>(stt.sttStatus);
  } catch (err) {
    log("WARNING: src/stt.ts failed to load; voice in is OFF (transcripts are empty):", err);
  }
  try {
    const router = await importSibling("router");
    const route = fn<HiveDeps["route"]>(router.route);
    if (!route) throw new Error("no route() export");
    deps.route = route;
    const warmRouter = fn<() => Promise<void>>(router.prewarm);
    const warmDirector = deps.prewarm;
    deps.prewarm = async () => {
      await Promise.allSettled([warmDirector?.(), warmRouter?.()]);
    };
  } catch (err) {
    log("WARNING: src/router.ts failed to load; voice intents use keywords only:", err);
  }
  return deps;
}

// ---------------------------------------------------------------- scan vocabulary helpers (glasses real-prop mode)

const OBSERVERS: Role[] = ["board", "monitor"];

/** The operator's verdict for each item (verdict.force carries only the item). */
export const OPERATOR_VERDICT: Record<Item, Verdict> = {
  red_rose: "bad",
  purple_bee_flower: "good",
  clover_or_rosemary: "good",
  water_dish: "good",
  bee_hotel: "good",
  spray_bottle: "dangerous",
  plastic_flower: "bad",
  other_bee_plant: "good",
  other_plant: "bad",
  non_plant_object: "roast",
  person: "roast",
  unclear: "roast",
};

/** Off-list items have no verdict line, so the operator twin plays a pre-rendered acknowledgement instead. */
const OPERATOR_ACK: Partial<Record<Item, string>> = {
  other_bee_plant: "room_ack_allow",
  other_plant: "room_ack_denied",
  non_plant_object: "room_canned_unclear",
  unclear: "room_canned_unclear",
};

/** A scan verdict with no line of its own says one of these (pre-rendered room acks; the unclear one has an ear twin). */
const VERDICT_ACK: Record<Verdict, string> = {
  good: "room_ack_allow",
  bad: "room_ack_denied",
  dangerous: "room_ack_grievance",
  roast: "room_canned_unclear",
};

const SEEN: Record<Item, string> = {
  red_rose: "red roses",
  purple_bee_flower: "a purple bee flower",
  clover_or_rosemary: "clover or rosemary",
  water_dish: "a pebble water dish",
  bee_hotel: "a bee hotel",
  spray_bottle: "a spray bottle",
  plastic_flower: "a plastic flower",
  other_bee_plant: "a bee-friendly plant",
  other_plant: "a plant",
  non_plant_object: "an object",
  person: "a person",
  unclear: "something unclear",
};

interface Decision {
  item: Item;
  verdict: Verdict;
  confidence: number;
  seen: string;
  line: string | null; // live line text (off-list roasts)
}

/** Coerce whatever the director returned into known enums. */
function sanitize(r: Partial<DirectorOutput> | null | undefined): Decision {
  const item = ITEMS.includes(r?.item as Item) ? (r!.item as Item) : "unclear";
  const verdict = VERDICTS.includes(r?.verdict as Verdict) ? (r!.verdict as Verdict) : "roast";
  const confidence = typeof r?.confidence === "number" && Number.isFinite(r.confidence) ? Math.min(1, Math.max(0, r.confidence)) : 0;
  const seen = typeof r?.seen === "string" && r.seen.trim() ? r.seen.trim().split(/\s+/).slice(0, 8).join(" ") : SEEN[item];
  const line = typeof r?.line === "string" && r.line.trim() ? r.line.trim().slice(0, 300) : null;
  return { item, verdict, confidence, seen, line };
}

interface Photo { jpeg: Uint8Array; width: number; height: number; ms: number }

const MAX_PHOTO_BYTES = 25 * 1024 * 1024;

/** Download the glasses photo and shrink it to <= 1024 px on the long edge (JPEG). */
export async function fetchPhoto(url: string, timeoutMs: number): Promise<Photo> {
  const u = new URL(url);
  if (u.protocol !== "https:" && u.protocol !== "http:") throw new Error(`unsupported photo url scheme ${u.protocol}`);
  const t0 = Date.now();
  const work = async () => {
    const res = await fetch(u, { signal: AbortSignal.timeout(timeoutMs) });
    if (!res.ok) throw new Error(`photo fetch HTTP ${res.status}`);
    if (Number(res.headers.get("content-length") ?? 0) > MAX_PHOTO_BYTES) throw new Error("photo too large");
    const raw = new Uint8Array(await res.arrayBuffer());
    if (raw.length > MAX_PHOTO_BYTES) throw new Error("photo too large");
    const { data, info } = await sharp(raw, { failOn: "none" })
      .rotate() // honor EXIF orientation
      .resize({ width: 1024, height: 1024, fit: "inside", withoutEnlargement: true })
      .jpeg({ quality: 82 })
      .toBuffer({ resolveWithObject: true });
    return { jpeg: new Uint8Array(data), width: info.width, height: info.height, ms: Date.now() - t0 };
  };
  return withTimeout(work(), timeoutMs + 2000, "photo");
}

// ---------------------------------------------------------------- scan jobs

interface ScanJob {
  id: string;
  group_id: string;
  beat: BeatName;
  pressedAt: number;
  origin: "glasses" | "operator";
  started: boolean; //     a /glasses/scan arrived and the pipeline is running
  photoStarted: boolean; // the photo is being (or was) fetched for the polaroid
  httpWaiting: boolean; // a /glasses/scan request is waiting on the answer
  response: ScanResponse | null;
  done: Promise<ScanResponse>;
  finish(r: ScanResponse): void;
}

class AudioCache {
  private map = new Map<string, Promise<Uint8Array>>();
  constructor(private max = 50) {}
  put(id: string, p: Promise<Uint8Array>): void {
    p.catch((err) => log(`audio ${id}: tts failed:`, err instanceof Error ? err.message : err));
    this.map.set(id, p);
    while (this.map.size > this.max) this.map.delete(this.map.keys().next().value!);
  }
  get(id: string): Promise<Uint8Array> | undefined {
    return this.map.get(id);
  }
}

const shortId = (prefix: string) => `${prefix}${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

// ---------------------------------------------------------------- voice

type VoiceSource = "quest" | "glasses";

interface Listen {
  n: number;
  source: VoiceSource;
  mode: "ptt" | "vad";
  startedAt: number;
  chunks: Uint8Array[];
  bytes: number;
  capped: boolean;
  watchdog: ReturnType<typeof setTimeout>;
}

/** What Beeswax says back: a pre-rendered room line, or live text (needs TTS). */
interface Reply { line_id: string | null; text: string }

const isWhatIfScene = (s: Scene) => s.startsWith("whatif_");
const cleanText = (s: unknown, max = 500) => (typeof s === "string" ? s.replace(/\s+/g, " ").trim().slice(0, max) : "");

function concat(parts: Uint8Array[], total: number): Uint8Array {
  const out = new Uint8Array(total);
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

// ---------------------------------------------------------------- the Hive

export interface HiveOptions {
  deps: HiveDeps;
  port?: number; // 0 = random (tests)
  hostname?: string;
  token?: string | null;
  publicBaseUrl?: string | null;
  directorTimeoutMs?: number;
  photoTimeoutMs?: number;
  heartbeatMs?: number;
  scanStaleMs?: number;
  orbisRelayUrl?: string | null;
  /** How often the relay's state is polled and mirrored on the bus as orbis{} (default 3000). */
  orbisPollMs?: number;
  host?: string;
  /** Voice: stt and router deadlines (each gets +1.5 s of grace from the Hive). */
  sttTimeoutMs?: number;
  routeTimeoutMs?: number;
  /** Mic audio kept per utterance (default 15 s of 16 kHz PCM16); a listen with no end is finished after listenWatchdogMs. */
  listenMaxBytes?: number;
  listenWatchdogMs?: number;
  /** "what if a fire starts": embers spill out of the portal this long after the line starts (covers the Orbis morph). */
  embersDelayMs?: number;
}

export interface Hive {
  server: Server<ClientData>;
  port: number;
  url: string;
  state: GroupState;
  bus: Bus;
  stop(): Promise<void>;
}

export function startHive(opts: HiveOptions): Hive {
  const deps = opts.deps;
  const token = opts.token ?? null;
  const directorTimeoutMs = opts.directorTimeoutMs ?? 6000;
  const photoTimeoutMs = opts.photoTimeoutMs ?? 8000;
  const scanStaleMs = opts.scanStaleMs ?? 25_000;
  const sttTimeoutMs = opts.sttTimeoutMs ?? 6000;
  const routeTimeoutMs = opts.routeTimeoutMs ?? 3500;
  const listenMaxBytes = Math.max(2, (opts.listenMaxBytes ?? 15 * 16000 * 2) & ~1);
  const listenWatchdogMs = opts.listenWatchdogMs ?? 20_000;
  const embersDelayMs = opts.embersDelayMs ?? 2500;
  const state = createState();
  const jobs = new Map<string, ScanJob>(); // by scan_id (current + recent, for idempotent retries)
  const audio = new AudioCache(50);
  let baseUrl = opts.publicBaseUrl?.replace(/\/+$/, "") || "";
  const startedAt = Date.now();

  const lineText = (id: string) => {
    try {
      return deps.lineText(id) || "";
    } catch {
      return "";
    }
  };
  const verdictLine = (item: Item, verdict: Verdict, channel: Channel) => {
    try {
      return deps.verdictLine(item, verdict, channel);
    } catch (err) {
      log("verdictLine threw:", err);
      return null;
    }
  };
  const ttsAvailable = () => {
    try {
      return deps.ttsAvailable();
    } catch {
      return false;
    }
  };
  const lineIdFor = (text: string, channel: Channel) => {
    try {
      return deps.lineIdForText?.(text, channel) ?? null;
    } catch {
      return null;
    }
  };
  const hasLine = (id: string | null | undefined): id is string => Boolean(id && deps.lines[id]);
  /** When Claude can't answer: "What IS that?" (pre-rendered via lineIdForText when it exists). */
  const fallbackDecision = (seen = SEEN.unclear): Decision => ({
    item: "unclear",
    verdict: "roast",
    confidence: 0,
    seen,
    line: lineText("room_canned_unclear") || null,
  });
  const earCuesPlayed = new Set<string>(); // ear cues already played this group ("<group>:<cue>")
  const earOnce = (cue: string, line_id: string | null | undefined) => {
    if (!hasLine(line_id) || earCuesPlayed.has(`${state.group_id}:${cue}`)) return;
    earCuesPlayed.add(`${state.group_id}:${cue}`);
    bus.say("ear", line_id);
  };

  const bus: Bus = new Bus(
    state,
    {
      lineText,
      onVerdictForce: (item) => forceVerdict(item),
      onPlea: (text) => plea(text),
      onGroupReset: () => groupReset(),
      onOrbisRequest: (msg, from) => orbisRequest(msg, from),
      onBeat: (n, prev) => beatEntered(n, prev),
      onHornetCooked: () => {
        state.memory.hornet_done = true;
        earOnce("hornet_done", "ear_aside_hornet");
      },
      onChoice: (station, choice, questSays) => choiceMade(station, choice, questSays),
      onListenStart: (ws, mode) => listenStart(ws, mode),
      onMic: (ws, pcm) => micFrame(ws, pcm),
      onListenEnd: (ws, cancel) => finishListen(ws, cancel ? "cancel" : "end"),
      onClientClose: (ws) => finishListen(ws, "closed"),
    },
    { token, host: opts.host ?? hostname(), heartbeatMs: opts.heartbeatMs },
  );

  const scanRoles = (job: ScanJob): Role[] => (job.beat === "field" ? OBSERVERS : ["quest", ...OBSERVERS]);
  const observe = (text: string) => bus.observe({ type: "log", text });
  const latency = (hop: string, ms: number) => bus.observe({ type: "latency", hop, ms: Math.max(0, Math.round(ms)), source: "hive" });

  // ------------------------------------------------------------ press / scan / verdict (glasses real-prop mode)

  function newJob(pressedAt: number, origin: ScanJob["origin"]): ScanJob {
    let finish!: (r: ScanResponse) => void;
    const done = new Promise<ScanResponse>((r) => (finish = r));
    const job: ScanJob = {
      id: shortId("s"),
      group_id: state.group_id,
      beat: beatName(state),
      pressedAt,
      origin,
      started: false,
      photoStarted: false,
      httpWaiting: false,
      response: null,
      done,
      finish,
    };
    jobs.set(job.id, job);
    while (jobs.size > 40) jobs.delete(jobs.keys().next().value!);
    state.scan_in_flight = { scan_id: job.id, pressed_at: pressedAt, started_at: Date.now() };
    bus.broadcast({ type: "scan.started", scan_id: job.id }, scanRoles(job));
    log(`scan ${job.id} started (${origin}, beat ${job.beat})`);
    return job;
  }

  function currentJob(): ScanJob | null {
    const f = state.scan_in_flight;
    if (!f) return null;
    const job = jobs.get(f.scan_id);
    if (job && !job.response && (job.httpWaiting || Date.now() - f.started_at < scanStaleMs)) return job;
    // The glasses pressed but never sent the scan: don't block scanning forever.
    log(`scan ${f.scan_id} expired without a result`);
    state.scan_in_flight = null;
    if (job && !job.response) jobs.delete(job.id);
    return null;
  }

  function dropJobs(): void {
    for (const job of jobs.values()) {
      if (job.response) continue;
      job.response = notNowScan();
      job.finish(job.response);
    }
    jobs.clear();
    earCuesPlayed.clear();
  }

  const notNowScan = (): ScanResponse => ({
    item: "unclear",
    verdict: "roast",
    line_id: "ear_not_now",
    audio_url: null,
    caption: lineText("ear_not_now"),
    path: "operator",
    ms: 0,
  });

  function saneTime(t: unknown, now: number): number {
    return typeof t === "number" && Number.isFinite(t) && now - t > -5000 && now - t < 120_000 ? t : now;
  }

  function press(pressed_at: unknown): PressResponse {
    const now = Date.now();
    const beat = beatName(state);
    if (!SCAN_BEATS.includes(beat) || currentJob()) {
      const line_id = beat === "pre" && hasLine("ear_practice") ? "ear_practice" : "ear_not_now";
      return { allowed: false, scan_id: null, line_id };
    }
    const job = newJob(saneTime(pressed_at, now), "glasses");
    return { allowed: true, scan_id: job.id, line_id: null };
  }

  async function scan(body: Record<string, unknown>): Promise<{ status: number; body: ScanResponse }> {
    const scan_id = typeof body.scan_id === "string" ? body.scan_id : "";
    let job = scan_id ? jobs.get(scan_id) : undefined;
    if (!job) {
      // The press never reached us (or expired): treat this scan as its own press.
      const p = press(body.pressed_at);
      if (!p.allowed || !p.scan_id) return { status: 409, body: notNowScan() };
      job = jobs.get(p.scan_id)!;
      if (scan_id) jobs.set(scan_id, job);
    }
    if (!job.response) job.httpWaiting = true;
    const photoUrl = typeof body.photo_url === "string" ? body.photo_url : "";
    if (!job.started) {
      job.started = true;
      const j = job;
      void runScan(j, body).catch((err) => {
        log(`scan ${j.id} pipeline error:`, err);
        resolve(j, fallbackDecision(), "claude");
      });
    } else if (photoUrl && !job.photoStarted) {
      // Speech-first glasses send the photo after the verdict, only so the Quest polaroid develops.
      void developPhoto(job, photoUrl);
    }
    return { status: 200, body: await job.done };
  }

  /** Fetch the photo and send it to the polaroid (Quest + monitor). Resolves null on failure. */
  function developPhoto(job: ScanJob, url: string): Promise<Photo | null> {
    job.photoStarted = true;
    return fetchPhoto(url, photoTimeoutMs).then(
      (photo) => {
        // A late photo must not overwrite the polaroid of a newer scan.
        const newer = state.scan_in_flight !== null && state.scan_in_flight.scan_id !== job.id;
        if (job.group_id === state.group_id && !newer) {
          // JSON then binary, per socket, so the JPEG always follows its scan.photo. Board skips the bytes.
          const roles = scanRoles(job).filter((r) => r !== "board");
          bus.broadcast({ type: "scan.photo", scan_id: job.id, width: photo.width, height: photo.height }, roles);
          bus.broadcastBinary(BINARY_KIND.SCAN_PHOTO, photo.jpeg, roles);
          observe(`photo ${photo.width}x${photo.height} ${Math.round(photo.jpeg.length / 1024)} KB in ${photo.ms} ms`);
        }
        return photo;
      },
      (err) => {
        log(`scan ${job.id}: photo failed:`, err instanceof Error ? err.message : err);
        observe(`photo failed: ${err instanceof Error ? err.message : err}`);
        return null;
      },
    );
  }

  async function runScan(job: ScanJob, body: Record<string, unknown>): Promise<void> {
    const heard = typeof body.heard === "string" ? body.heard.trim().slice(0, 500) : "";
    const photoUrl = typeof body.photo_url === "string" ? body.photo_url : "";
    const photoP = photoUrl ? developPhoto(job, photoUrl) : Promise.resolve(null);

    if (job.response) return; // operator already decided; the photo still develops
    const fast = heard ? speechVerdict(heard) : null;
    if (fast) {
      resolve(job, { ...fast, confidence: 0.9, seen: SEEN[fast.item], line: null }, "speech");
      return;
    }

    const photo = await photoP;
    if (job.response) return;
    if (!photo && !heard) {
      resolve(job, fallbackDecision("nothing"), "claude");
      return;
    }
    let result: JudgeResultLike | null = null;
    try {
      result = await withTimeout(
        deps.judge(
          {
            photo: photo?.jpeg,
            mime: photo ? "image/jpeg" : undefined,
            heard: heard || undefined,
            beat: job.beat,
            memory: { scanned: [...state.memory.scanned] },
          },
          { timeoutMs: directorTimeoutMs },
        ),
        directorTimeoutMs + 1500,
        "judge",
      );
    } catch (err) {
      log(`scan ${job.id}: judge failed:`, err instanceof Error ? err.message : err);
    }
    const decision = result ? sanitize(result) : fallbackDecision();
    observe(
      result
        ? `judge ${result.model} ${result.ms} ms${result.fallback ? " FALLBACK" : ""}: ${decision.item}/${decision.verdict}`
        : "judge failed: fallback",
    );
    if (job.response) {
      log(`scan ${job.id}: late Claude verdict ${decision.item} discarded (operator was faster)`);
      return;
    }
    resolve(job, decision, "claude");
  }

  /** First decision wins. Picks lines/voice, broadcasts scan.verdict, answers the glasses. */
  function resolve(job: ScanJob, d: Decision, path: ScanPath): ScanResponse {
    if (job.response) return job.response;
    const field = job.beat === "field";
    const questLive = !field && bus.count("quest") > 0;
    let roomLine = field ? null : verdictLine(d.item, d.verdict, "room");
    let earLine = verdictLine(d.item, d.verdict, "ear");
    let roomCaption = roomLine ? lineText(roomLine) : "";
    let earCaption = earLine ? lineText(earLine) : "";
    let tts_id: string | null = null;
    let audio_url: string | null = null;

    // The director and the speech/operator paths give props no live line because props have verdict lines, but the
    // v3 catalog dropped the v1 prop lines: such a verdict says the pre-rendered acknowledgement instead of nothing.
    const line = d.line ?? (roomLine || earLine ? null : lineText(VERDICT_ACK[d.verdict]) || null);

    // A live line (off-list roast) plays on ONE device: Beeswax on the Quest if it's there, else the ear.
    // Canned text that matches a pre-rendered line plays that line instead of going through TTS.
    if (line) {
      const canSpeak = ttsAvailable();
      if (questLive && !roomLine) {
        roomLine = lineIdFor(line, "room");
        roomCaption = roomLine ? lineText(roomLine) : line;
        if (!roomLine && canSpeak) tts_id = shortId("t");
      } else if (!questLive && !earLine) {
        earLine = lineIdFor(line, "ear");
        earCaption = earLine ? lineText(earLine) : line;
        if (!earLine && canSpeak) audio_url = mp3Url(line);
      }
    }

    const ms = Math.max(0, Date.now() - job.pressedAt);
    const sameGroup = job.group_id === state.group_id;
    if (sameGroup) {
      bus.broadcast(
        {
          type: "scan.verdict",
          scan_id: job.id,
          item: d.item,
          verdict: d.verdict,
          confidence: d.confidence,
          seen: d.seen,
          socket: SOCKET_OF[d.item] ?? null,
          line_id: roomLine,
          tts_id,
          caption: roomCaption || earCaption,
          path,
          ms,
        },
        scanRoles(job),
      );
      if (tts_id) {
        const text = line!;
        void bus.enqueueRoomTts(tts_id, text, deps.ttsSampleRate, () => deps.ttsPcm(text));
      }
      if (d.item !== "unclear") remember(state, d.item);
      if (state.scan_in_flight?.scan_id === job.id) state.scan_in_flight = null;
    }
    log(`scan ${job.id}: ${d.item}/${d.verdict} via ${path} in ${ms} ms room=${roomLine ?? tts_id ?? "-"} ear=${earLine ?? audio_url ?? "-"}`);

    const response: ScanResponse = {
      item: d.item,
      verdict: d.verdict,
      line_id: earLine,
      audio_url,
      // The miniapp reads a caption with no clip aloud in Mentra's voice: fine as a last resort (no live voice),
      // never while Beeswax on the Quest is already saying the line.
      caption: earCaption || (questLive && (roomLine || tts_id) ? "" : roomCaption),
      path,
      ms,
    };
    job.response = response;
    job.finish(response);
    // Operator verdict with no glasses request waiting: deliver the ear line over the bus instead.
    if (job.origin === "operator" && !job.httpWaiting && (earLine || audio_url)) {
      bus.say("ear", earLine, { audio_url, caption: earCaption });
    }
    return response;
  }

  function forceVerdict(item: Item): void {
    const job = currentJob() ?? newJob(Date.now(), "operator");
    const line = lineText(OPERATOR_ACK[item] ?? "") || null;
    resolve(job, { item, verdict: OPERATOR_VERDICT[item], confidence: 1, seen: SEEN[item], line }, "operator");
  }

  function plea(raw: string): void {
    const text = raw.trim().slice(0, 300);
    if (!text) return;
    state.memory.pleas.push(text);
    bus.broadcast({ type: "plea", text }, beatName(state) === "field" ? OBSERVERS : ["quest", ...OBSERVERS]);
    log(`plea: ${text}`);
  }

  /** Render a live ear line as an mp3 the glasses fetch from /audio/<id>.mp3. */
  function mp3Url(text: string): string {
    const id = crypto.randomUUID();
    audio.put(id, Promise.resolve().then(() => deps.ttsMp3(text)));
    return `${baseUrl || `http://localhost:${server.port}`}/audio/${id}.mp3`;
  }

  // ------------------------------------------------------------ the future window (Orbis relay: hive/orbis-relay)
  // The Hive owns the scene. The relay owns the billed Reactor session: the Hive starts/stops it on the Board's (or
  // Quest's) orbis.request and morphs it to each scene's prompt (/orbis/stage, 2-4 s). orbis{} on the bus always
  // carries the scene and its fallback clip, so Unity can play the clip whenever live is down.

  const relayUrl = opts.orbisRelayUrl?.replace(/\/+$/, "") || null;
  const ORBIS_STATES: readonly OrbisState[] = ["off", "priming", "ready", "failed", "ended"];
  let orbisWant: OrbisRequest["want"] = "clips";
  let relayState: OrbisState = "off";
  let relaySince = 0; // the relay's `since`, so an older reply never overwrites a newer one
  let relayScene: Scene | null = null; // the scene whose prompt the relay was last given
  let relayUp: boolean | null = null;
  let relayChain: Promise<unknown> = Promise.resolve(); // POSTs go out one at a time, in order
  let published: OrbisStatus | null = null;

  const relayRunning = () => relayState === "priming" || relayState === "ready";
  const clipOf = (scene: Scene) => deps.scenes[scene]?.clip || `${scene}.mp4`;

  function orbisStatus(): OrbisStatus {
    return {
      type: "orbis",
      state: relayUrl ? relayState : "off",
      stage: SCENES.indexOf(state.scene),
      scene: state.scene,
      fallback_clip: clipOf(state.scene),
    };
  }

  function publishOrbis(force: boolean): void {
    const next = orbisStatus();
    if (!force && published && published.state === next.state && published.scene === next.scene) return;
    published = next;
    bus.broadcast(next);
  }

  /** GET (no body) or POST the relay. Mirrors any status it answers with. Never throws. */
  async function relay(path: string, body?: object): Promise<Record<string, unknown> | null> {
    if (!relayUrl) return null;
    try {
      const res = await fetch(`${relayUrl}${path}`, {
        method: body ? "POST" : "GET",
        headers: { "content-type": "application/json", ...(token ? { "x-hive-token": token } : {}) },
        body: body ? JSON.stringify(body) : undefined,
        signal: AbortSignal.timeout(2000),
      });
      if (body) observe(`orbis relay ${path}: HTTP ${res.status}`);
      else if (relayUp === false) observe("orbis relay is back");
      relayUp = true;
      const json: unknown = await res.json().catch(() => null);
      if (!res.ok || typeof json !== "object" || json === null || Array.isArray(json)) return null;
      mirrorRelay(json as Record<string, unknown>);
      return json as Record<string, unknown>;
    } catch (err) {
      // Polls only report the first failure, not one every few seconds.
      if (body || relayUp !== false) observe(`orbis relay unreachable: ${err instanceof Error ? err.message : err}`);
      relayUp = false;
      return null;
    }
  }

  function mirrorRelay(s: Record<string, unknown>): void {
    const st = ORBIS_STATES.find((x) => x === s.state);
    if (!st) return;
    const since = typeof s.since === "number" ? s.since : 0;
    if (since && since < relaySince) return;
    relaySince = Math.max(relaySince, since);
    relayState = st;
    if (!relayRunning()) relayScene = null;
    // After a Hive restart the relay may still be live: remember what it shows so the same prompt isn't re-sent.
    else if (relayScene === null && SCENES.includes(s.scene as Scene)) relayScene = s.scene as Scene;
    publishOrbis(false);
  }

  function queueRelay<T>(job: () => Promise<T>): Promise<T | null> {
    const run = relayChain.then(job, job).catch((err) => {
      log("orbis relay job failed:", err);
      return null;
    });
    relayChain = run;
    return run;
  }

  /** Morph a running stream to this scene's prompt. No stream: nothing (never opens a billed session by itself). */
  function steerRelay(scene: Scene): void {
    if (!relayUrl || !relayRunning() || relayScene === scene) return;
    const sc = deps.scenes[scene];
    if (!sc) return observe(`no Orbis prompt for scene ${scene}`);
    relayScene = scene;
    void queueRelay(() => relay("/orbis/stage", { prompt: sc.prompt, scene }));
  }

  /** live: start the stream on the current scene, or morph a running one. clips/off: stop (Reactor bills while connected). */
  function applyWant(want: OrbisRequest["want"]): void {
    if (!relayUrl) return;
    void queueRelay(async () => {
      if (want !== "live") {
        relayScene = null;
        await relay("/orbis/stop", {});
        return;
      }
      const scene = state.scene;
      const sc = deps.scenes[scene];
      const now = await relay("/orbis/status");
      const running = now?.state === "priming" || now?.state === "ready";
      if (!running) {
        relayScene = scene;
        await relay("/orbis/start", sc ? { prompt: sc.prompt, seed: sc.seed, scene } : { scene });
      } else if (sc && now?.prompt !== sc.prompt) {
        relayScene = scene;
        await relay("/orbis/stage", { prompt: sc.prompt, scene });
      } else {
        relayScene = scene;
        observe(`orbis relay already ${String(now?.state)} on ${scene}`);
      }
    });
  }

  /** The one place the window changes: fx for the fire, the relay morph, and orbis{} for everyone. */
  function setScene(next: Scene, why: string): void {
    const prev = state.scene;
    state.scene = next;
    if (!isWhatIfScene(next) && next !== "fire_out") state.base_scene = next;
    if (prev === "whatif_fire" && next !== "whatif_fire") bus.fx("embers_stop");
    if (next === "whatif_fire" && prev !== "whatif_fire") bus.fx("embers", { delay_ms: embersDelayMs });
    publishOrbis(true);
    steerRelay(next);
    if (next !== prev) {
      log(`window: ${prev} -> ${next} (${why})`);
      observe(`window: ${prev} -> ${next} (${why})`);
    }
  }

  function orbisRequest(msg: OrbisRequest, from: Client): void {
    let scene = msg.scene;
    if (!scene && msg.stage !== undefined) {
      scene = SCENES[msg.stage];
      if (!scene) return bus.send(from, { type: "error", message: `orbis.request stage ${msg.stage} is not a scene (0-${SCENES.length - 1})` });
    }
    // With a scene (or stage) this is a scene switch only: its `want` (the sender's possibly stale idea of the mode)
    // is ignored, so a Quest or Board never starts or stops the billed stream by accident. Only a bare mode request
    // (the Board's LIVE/CLIPS/OFF) changes the mode.
    const changeWant = !scene;
    if (changeWant) orbisWant = msg.want;
    const fwd: OrbisRequest = { type: "orbis.request", want: orbisWant };
    if (scene) {
      fwd.scene = scene;
      fwd.stage = SCENES.indexOf(scene);
    }
    bus.broadcast(fwd, ["quest"], from.data.role === "quest" ? from : undefined);
    observe(`orbis.request from ${from.data.role}: ${orbisWant}${scene ? ` ${scene}` : ""}`);
    if (changeWant && orbisWant !== "live") applyWant(orbisWant);
    if (scene) setScene(scene, `${from.data.role} request`);
    else publishOrbis(true);
    if (changeWant && orbisWant === "live") applyWant(orbisWant);
  }

  // ------------------------------------------------------------ beats, choices, score

  const ON_CLOCK = new Set<BeatName>(["meet", "smoker", "feed", "wasps", "hornet", "heat", "whatif"]);

  function beatEntered(n: number, prev: number): void {
    if (n === prev) return; // a re-sent beat.set (Unity reconnect)
    const name = BEATS[n];
    const now = Date.now();
    if (name === "hook") {
      state.clock_start = now;
      state.saved_ms = null;
      setScene("meadow", "hook");
    } else if (state.clock_start === null && name && ON_CLOCK.has(name)) {
      state.clock_start = now; // the hook was skipped: start the score clock here
      observe(`score clock started at ${name} (no hook beat)`);
    }
    if (name === "escape") {
      if (state.clock_start !== null && state.saved_ms === null) {
        state.saved_ms = now - state.clock_start;
        const score = scoreText(state)!;
        log(`score: ${score}`);
        observe(`score: ${score}`);
      }
      setScene("full_bloom", "escape");
    } else if (name === "t0") {
      setScene("moon", "t0");
    } else if (name === "map") {
      setScene("full_bloom", "map");
    }
  }

  let lastChoice = { key: "", at: 0 };

  function choiceMade(station: Station, choice: Choice, questSays: boolean | null): void {
    const key = `${station}:${choice}`;
    if (key === lastChoice.key && Date.now() - lastChoice.at < 1500) return log(`choice ${key}: duplicate ignored`);
    lastChoice = { key, at: Date.now() };
    const { correct } = recordChoice(state, station, choice);
    // The router's "recent events": without them a question right after a choice gets the before-choosing tease.
    rememberHeard(state, `(event: the player chose ${choice.replace(/_/g, " ")} at the ${station} station, ${correct ? "the right choice" : "a wrong choice"})`);
    if (questSays !== null && questSays !== correct) {
      observe(`quest says ${key} is ${questSays ? "correct" : "wrong"}, but the answer is ${CORRECT_CHOICE[station]}`);
    }
    bus.observe({ type: "choice.made", station, choice, correct });
    const outcome = deps.stations[station]?.choices[choice];
    if (outcome && hasLine(outcome.line_id)) bus.say("room", outcome.line_id);
    else {
      log(`choice ${key}: no line in stations.json, acknowledging`);
      sayReply(ackReply(), "room");
    }
    if (outcome?.fx) bus.fx(outcome.fx);
    if (outcome) setScene(outcome.scene, key);
    const solved = state.memory.solved.length;
    log(`choice ${key}: ${correct ? "correct" : "wrong"} (${solved}/4 solved)`);
    observe(`choice ${station}: ${choice} ${correct ? "CORRECT" : "wrong"} (${solved}/4 solved)`);
  }

  function groupReset(): void {
    dropJobs();
    for (const [id, l] of listens) {
      clearTimeout(l.watchdog);
      listens.delete(id);
    }
    lastChoice = { key: "", at: 0 };
    bus.fx("embers_stop");
    publishOrbis(true); // reset() already put the window back on the meadow
    steerRelay(state.scene);
  }

  // ------------------------------------------------------------ voice in: listen.start, MIC_PCM16, listen.end

  const listens = new Map<string, Listen>(); // by client id
  const strayWarned = new Set<string>();
  let listenSeq = 0;

  function listenStart(ws: Client, mode: "ptt" | "vad"): void {
    const id = ws.data.id;
    const old = listens.get(id);
    if (old) {
      clearTimeout(old.watchdog);
      log(`listen ${id}#${old.n}: restarted, ${old.bytes} bytes dropped`);
    }
    const n = ++listenSeq;
    const watchdog = setTimeout(() => {
      if (listens.get(id)?.n !== n) return;
      log(`listen ${id}#${n}: no listen.end after ${listenWatchdogMs} ms, finishing`);
      finishListen(ws, "watchdog");
    }, listenWatchdogMs);
    listens.set(id, {
      n,
      source: ws.data.role === "glasses" ? "glasses" : "quest",
      mode,
      startedAt: Date.now(),
      chunks: [],
      bytes: 0,
      capped: false,
      watchdog,
    });
    strayWarned.delete(id);
  }

  function micFrame(ws: Client, pcm: Uint8Array): void {
    const l = listens.get(ws.data.id);
    if (!l) {
      if (!strayWarned.has(ws.data.id)) {
        strayWarned.add(ws.data.id);
        log(`listen ${ws.data.id}: mic audio with no open listen.start, dropped`);
      }
      return;
    }
    if (l.capped || pcm.length === 0) return;
    let chunk = pcm;
    if (l.bytes + chunk.length > listenMaxBytes) {
      chunk = chunk.subarray(0, listenMaxBytes - l.bytes);
      l.capped = true;
      observe(`voice: ${l.source} utterance capped at ${(listenMaxBytes / 32000).toFixed(1)} s`);
    }
    if (chunk.length) {
      l.chunks.push(chunk.slice()); // copy: the socket may reuse its buffer
      l.bytes += chunk.length;
    }
  }

  function finishListen(ws: Client, why: "end" | "cancel" | "closed" | "watchdog"): void {
    const l = listens.get(ws.data.id);
    if (!l) {
      if (why === "end" || why === "cancel") log(`listen ${ws.data.id}: listen.end without listen.start`);
      return;
    }
    listens.delete(ws.data.id);
    clearTimeout(l.watchdog);
    if (why === "cancel" || why === "closed") {
      log(`listen ${ws.data.id}#${l.n}: ${why}, ${l.bytes} bytes dropped`);
      if (why === "cancel") observe(`voice: ${l.source} listen cancelled`);
      return;
    }
    const pcm = concat(l.chunks, l.bytes & ~1);
    log(`listen ${ws.data.id}#${l.n}: ${(pcm.length / 32000).toFixed(2)} s of audio (${l.mode}, ${why})`);
    void runVoice(pcm, l.source, ws).catch((err) => log("voice pipeline error:", err));
  }

  async function safeTranscribe(pcm: Uint8Array): Promise<SttResultLike> {
    const t0 = Date.now();
    try {
      const r = await withTimeout(deps.transcribe(pcm, { timeoutMs: sttTimeoutMs }), sttTimeoutMs + 1500, "stt");
      return {
        text: cleanText(r?.text),
        ms: typeof r?.ms === "number" && Number.isFinite(r.ms) ? r.ms : Date.now() - t0,
        provider: cleanText(r?.provider, 40) || "?",
        error: typeof r?.error === "string" ? r.error : undefined,
      };
    } catch (err) {
      return { text: "", ms: Date.now() - t0, provider: "none", error: err instanceof Error ? err.message : String(err) };
    }
  }

  async function safeRoute(text: string, ctx: { beat: BeatName; recent: string[] }): Promise<RouteResultLike> {
    const t0 = Date.now();
    let r: RouteResultLike;
    try {
      r = await withTimeout(deps.route(text, ctx, { timeoutMs: routeTimeoutMs }), routeTimeoutMs + 1500, "route");
    } catch (err) {
      log("route failed, using keywords:", err instanceof Error ? err.message : err);
      r = { ...keywordRoute(text), ms: Date.now() - t0 };
    }
    // Coerce into the contract: topic iff what_if, answer iff ask.
    let intent: IntentName = INTENTS.includes(r?.intent as IntentName) ? (r.intent as IntentName) : "unknown";
    const topic = WHATIF_TOPICS.includes(r?.topic as WhatIfTopic) ? (r.topic as WhatIfTopic) : null;
    if (intent === "what_if" && !topic) intent = "unknown";
    const answer = intent === "ask" && typeof r?.answer === "string" && r.answer.trim() ? r.answer.trim().slice(0, 300) : null;
    return {
      intent,
      topic: intent === "what_if" ? topic : null,
      answer,
      ms: typeof r?.ms === "number" && Number.isFinite(r.ms) ? r.ms : Date.now() - t0,
      path: r?.path === "claude" ? "claude" : "keyword",
      error: r?.error,
    };
  }

  /** Mic audio -> transcript -> intent -> act. Never throws. */
  async function runVoice(pcm: Uint8Array, source: VoiceSource, ws: Client): Promise<void> {
    const t0 = Date.now();
    const group = state.group_id;
    const stt: SttResultLike = pcm.length ? await safeTranscribe(pcm) : { text: "", ms: 0, provider: "none", error: "no_audio" };
    latency("voice.stt", stt.ms);
    bus.broadcast({ type: "transcript", text: stt.text, final: true, source, ms: Math.round(stt.ms), stt: stt.provider });
    if (group !== state.group_id) return;
    if (!stt.text) {
      observe(`voice: heard nothing (${stt.error ?? stt.provider})`);
      return;
    }
    // Glasses talking over the bus get their ear reply over the bus too.
    const r = await understand(stt.text, source, t0, group, (reply) => {
      if (reply.line_id || reply.audio_url || reply.caption) bus.say("ear", reply.line_id, { audio_url: reply.audio_url, caption: reply.caption });
    });
    if (r) log(`voice ${source}: stt ${stt.provider} ${Math.round(stt.ms)} ms, total ${Date.now() - t0} ms (${ws.data.id})`);
  }

  /**
   * Route a final transcript and act on it. Room lines play on the Quest when one is connected (outside the field);
   * otherwise the reply goes to the ear: `earReply` for the bus path, the return value for /glasses/utterance.
   */
  async function understand(
    text: string,
    source: VoiceSource,
    t0: number,
    group: string,
    earReply?: (r: UtteranceResponse) => void,
  ): Promise<UtteranceResponse | null> {
    const recent = state.memory.heard.slice(-5);
    rememberHeard(state, text);
    const beat = beatName(state);
    const r = await safeRoute(text, { beat, recent });
    latency("voice.route", r.ms);
    bus.broadcast({ type: "intent", name: r.intent, topic: r.topic, text, path: r.path, ms: Math.round(r.ms) });
    // The answer rides along for the operator: with TTS down it is never spoken (Beeswax acks instead).
    observe(
      `voice ${source}: "${text.slice(0, 80)}" -> ${r.intent}${r.topic ? `/${r.topic}` : ""} via ${r.path} ${Math.round(r.ms)} ms` +
        `${r.error ? ` (${r.error})` : ""}${r.answer ? ` answer: "${r.answer}"` : ""}`,
    );
    if (group !== state.group_id) return null; // group reset mid-utterance

    const response: UtteranceResponse = { intent: r.intent, topic: r.topic, line_id: null, audio_url: null, caption: "" };
    if (!VOICE_BEATS.includes(beat)) {
      observe(`voice: not acting in beat ${beat}`);
      if (source === "glasses" && hasLine("ear_not_now")) Object.assign(response, { line_id: "ear_not_now", caption: lineText("ear_not_now") });
      earReply?.(response);
      return response;
    }
    const field = beat === "field";
    const toRoom = !field && bus.count("quest") > 0;
    const deliver = (reply: Reply) => {
      if (toRoom) return sayReply(reply, "room");
      Object.assign(response, earVersion(reply));
      if (earReply) earReply(response);
      else bus.say("ear", response.line_id, { audio_url: response.audio_url, caption: response.caption }, false); // board/monitor copy
    };
    // A live answer needs our TTS in the room; the miniapp can read a caption aloud in Mentra's own voice.
    act(r, deliver, field, toRoom ? ttsAvailable() : true);
    latency("voice.total", Date.now() - t0);
    return response;
  }

  /** What-if, undo, ask, ack: say the line first, then change the window (the line covers the 2-4 s morph). */
  function act(r: RouteResultLike, deliver: (reply: Reply) => void, field: boolean, canSpeakAnswer: boolean): void {
    switch (r.intent) {
      case "what_if": {
        const topic = r.topic!;
        state.memory.whatifs.push(topic);
        const id = `room_whatif_${topic}`;
        deliver(hasLine(id) ? { line_id: id, text: lineText(id) } : ackReply());
        if (!field) setScene(`whatif_${topic}` as Scene, `what if: ${topic}`);
        return;
      }
      case "undo": {
        const cur = state.scene;
        if (cur === "whatif_fire") {
          deliver(lineReply("room_whatif_out"));
          if (!field) setScene("fire_out", "put it out");
        } else if (isWhatIfScene(cur) || cur === "fire_out") {
          deliver(lineReply("room_whatif_back"));
          if (!field) setScene(state.base_scene, "undo");
        } else {
          deliver(lineReply("room_whatif_nothing"));
        }
        return;
      }
      case "ask":
        deliver(r.answer && canSpeakAnswer ? { line_id: null, text: r.answer } : ackReply());
        return;
      default:
        deliver(ackReply());
    }
  }

  const lineReply = (id: string): Reply => (hasLine(id) ? { line_id: id, text: lineText(id) } : ackReply());

  let lastAck = "";
  function ackReply(): Reply {
    const pool = deps.ackLines.filter((id) => hasLine(id) && id !== lastAck);
    const id = pool.length ? pool[Math.floor(Math.random() * pool.length)]! : hasLine(lastAck) ? lastAck : null;
    if (!id) return { line_id: null, text: "" };
    lastAck = id;
    return { line_id: id, text: lineText(id) };
  }

  /** Play a reply from Beeswax on the Quest: a pre-rendered line, or live TTS (caption only when TTS is down). */
  function sayReply(reply: Reply, channel: "room"): void {
    if (reply.line_id) return bus.say(channel, reply.line_id);
    if (!reply.text) return;
    if (!ttsAvailable()) return bus.say(channel, null, { caption: reply.text });
    const tts_id = shortId("t");
    bus.say(channel, null, { tts_id, caption: reply.text });
    void bus.enqueueRoomTts(tts_id, reply.text, deps.ttsSampleRate, () => deps.ttsPcm(reply.text));
  }

  /** The same reply for the glasses: an ear twin of the line, else a live mp3, else just the caption (Mentra reads it). */
  function earVersion(reply: Reply): Pick<UtteranceResponse, "line_id" | "audio_url" | "caption"> {
    const text = reply.text || (reply.line_id ? lineText(reply.line_id) : "");
    if (!text) return { line_id: null, audio_url: null, caption: "" };
    const ear = lineIdFor(text, "ear");
    if (ear) return { line_id: ear, audio_url: null, caption: lineText(ear) };
    return { line_id: null, audio_url: ttsAvailable() ? mp3Url(text) : null, caption: text };
  }

  // ------------------------------------------------------------ HTTP

  const app = new Hono();
  const tokenOf = (c: Context) => c.req.query("token") ?? c.req.header("x-hive-token") ?? null;
  const authed = (c: Context) => checkToken(tokenOf(c), token);
  const readJson = async (c: Context): Promise<Record<string, unknown> | null> => {
    try {
      const v = await c.req.json();
      return typeof v === "object" && v !== null && !Array.isArray(v) ? v : null;
    } catch {
      return null;
    }
  };
  const rememberBase = (c: Context) => {
    if (opts.publicBaseUrl) return;
    const host = c.req.header("x-forwarded-host") ?? c.req.header("host");
    if (host) baseUrl = `${c.req.header("x-forwarded-proto") ?? new URL(c.req.url).protocol.replace(":", "")}://${host}`;
  };
  const sttStatus = () => {
    try {
      return deps.sttStatus?.() ?? null;
    } catch {
      return null;
    }
  };

  app.onError((err, c) => {
    log(`http error on ${c.req.method} ${c.req.path}:`, err);
    return c.json({ error: "internal error" }, 500);
  });

  app.use("/glasses/*", cors({ origin: "*", allowHeaders: ["content-type", "x-hive-token"], allowMethods: ["POST", "OPTIONS"] }));
  app.use("/audio/*", cors({ origin: "*" }));
  app.use("/glasses/*", async (c, next) => {
    if (!authed(c)) return c.json({ error: "bad token" }, 401);
    rememberBase(c);
    await next();
  });

  app.get("/", (c) => c.text("Beeswax Hive. See /health, /board?token=..., /monitor?token=..."));

  app.get("/health", (c) =>
    c.json({
      ok: true,
      version: PROTOCOL_VERSION,
      clients: bus.counts(),
      beat: state.beat,
      beat_name: beatName(state),
      group_id: state.group_id,
      mode: state.mode,
      scan_in_flight: state.scan_in_flight !== null,
      orbis: { state: orbisStatus().state, stage: SCENES.indexOf(state.scene), scene: state.scene, want: orbisWant, relay: relayUrl ? relayUp : null },
      solved: state.memory.solved,
      score: scoreText(state),
      listening: listens.size,
      tts: ttsAvailable(),
      stt: sttStatus(),
      lines: Object.keys(deps.lines).length,
      uptime_s: Math.round((Date.now() - startedAt) / 1000),
    }),
  );

  app.get("/board", (c) => (authed(c) ? c.html(boardHtml(deps.stations)) : c.text("Add ?token=<HIVE_TOKEN> to the URL.", 401)));
  app.get("/monitor", (c) => (authed(c) ? c.html(monitorHtml()) : c.text("Add ?token=<HIVE_TOKEN> to the URL.", 401)));
  app.get("/lines.json", (c) => (authed(c) ? c.json(deps.lines) : c.json({ error: "bad token" }, 401)));
  app.get("/stations.json", (c) => (authed(c) ? c.json(deps.stations) : c.json({ error: "bad token" }, 401)));

  app.get("/audio/:file", async (c) => {
    const id = c.req.param("file").replace(/\.mp3$/, "");
    const p = audio.get(id);
    if (!p) return c.text("not found", 404);
    try {
      const mp3 = (await withTimeout(p, 20_000, "tts mp3")) as Uint8Array<ArrayBuffer>;
      return new Response(mp3, { headers: { "content-type": "audio/mpeg", "cache-control": "public, max-age=3600" } });
    } catch {
      return c.text("voice unavailable", 502);
    }
  });

  app.post("/glasses/press", async (c) => {
    const body = (await readJson(c)) ?? {};
    return c.json(press(body.pressed_at));
  });

  app.post("/glasses/scan", async (c) => {
    const body = await readJson(c);
    if (!body) return c.json({ error: "bad JSON" }, 400);
    const r = await scan(body);
    return c.json(r.body, r.status as 200 | 409);
  });

  /** A final transcription from the glasses' own speech-to-text: same intent pipeline as Quest voice. */
  app.post("/glasses/utterance", async (c) => {
    const body = await readJson(c);
    const text = cleanText(body?.text);
    if (!text) return c.json({ error: "text required" }, 400);
    const now = Date.now();
    const at = saneTime(body?.at, now);
    bus.broadcast({ type: "transcript", text, final: true, source: "glasses", ms: now - at, stt: "glasses" });
    const r = await understand(text, "glasses", now, state.group_id);
    latency("utterance.total", Date.now() - at);
    const reply: UtteranceResponse = r ?? { intent: "unknown", topic: null, line_id: null, audio_url: null, caption: "" };
    return c.json(reply);
  });

  app.post("/glasses/plea", async (c) => {
    const body = await readJson(c);
    const text = typeof body?.text === "string" ? body.text.trim() : "";
    if (!text) return c.json({ error: "text required" }, 400);
    plea(text);
    return c.json({ ok: true });
  });

  app.post("/glasses/latency", async (c) => {
    const body = await readJson(c);
    const hop = typeof body?.hop === "string" ? body.hop.slice(0, 80) : "";
    const ms = typeof body?.ms === "number" && Number.isFinite(body.ms) ? Math.round(body.ms) : NaN;
    if (!hop || Number.isNaN(ms)) return c.json({ error: "hop and ms required" }, 400);
    bus.observe({ type: "latency", hop, ms, source: "glasses" });
    return c.json({ ok: true });
  });

  app.post("/glasses/event", async (c) => {
    const body = await readJson(c);
    if (!body) return c.json({ error: "bad JSON" }, 400);
    const text = `glasses event: ${JSON.stringify(body).slice(0, 300)}`;
    log(text);
    bus.observe({ type: "log", text, source: "glasses" });
    return c.json({ ok: true });
  });

  // ------------------------------------------------------------ server

  const server = Bun.serve({
    port: opts.port ?? 8240,
    hostname: opts.hostname ?? "0.0.0.0",
    idleTimeout: 60, // /glasses/scan, /glasses/utterance and /audio can legitimately wait ~15 s
    fetch(req, srv) {
      if (new URL(req.url).pathname === "/bus") return bus.upgrade(req, srv);
      return app.fetch(req);
    },
    websocket: bus.websocket,
    error(err) {
      log("server error:", err);
      return new Response("internal error", { status: 500 });
    },
  });
  bus.start();
  // The relay changes state on its own too (first frame, failure, idle stop), so poll it.
  const orbisPoll = relayUrl ? setInterval(() => void relay("/orbis/status"), opts.orbisPollMs ?? 3000) : null;

  return {
    server,
    port: server.port!,
    url: `http://127.0.0.1:${server.port}`,
    state,
    bus,
    async stop() {
      if (orbisPoll) clearInterval(orbisPoll);
      for (const l of listens.values()) clearTimeout(l.watchdog);
      listens.clear();
      bus.stop();
      await server.stop(true);
    },
  };
}

// ---------------------------------------------------------------- main

if (import.meta.main) {
  process.on("unhandledRejection", (err) => log("unhandled rejection:", err));
  process.on("uncaughtException", (err) => log("uncaught exception:", err));

  const token = process.env.HIVE_TOKEN || null;
  if (!token) log("WARNING: HIVE_TOKEN is unset: the bus, Beat Board and glasses API are OPEN to anyone");
  const deps = await loadDeps();
  const hive = startHive({
    deps,
    port: Number(process.env.PORT) || 8240,
    token,
    publicBaseUrl: process.env.PUBLIC_BASE_URL || null,
    directorTimeoutMs: Number(process.env.DIRECTOR_TIMEOUT_MS) || 6000,
    scanStaleMs: Number(process.env.SCAN_STALE_MS) || 25_000,
    sttTimeoutMs: Number(process.env.STT_TIMEOUT_MS) || 6000,
    routeTimeoutMs: Number(process.env.ROUTE_TIMEOUT_MS) || 3500,
    orbisRelayUrl: process.env.ORBIS_RELAY_URL || null,
    host: process.env.HIVE_HOST || undefined,
  });
  let stt = "?";
  try {
    stt = JSON.stringify(deps.sttStatus?.() ?? null);
  } catch {}
  log(`hive v${PROTOCOL_VERSION} on :${hive.port}  lines=${Object.keys(deps.lines).length} scenes=${Object.keys(deps.scenes).length} tts=${deps.ttsAvailable()} stt=${stt}`);
  log(`board:   /board?token=$HIVE_TOKEN    monitor: /monitor?token=$HIVE_TOKEN`);
  void deps.prewarm?.().catch(() => {});

  const shutdown = () => {
    log("shutting down");
    void hive.stop().finally(() => process.exit(0));
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}
