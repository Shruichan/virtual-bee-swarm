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
  type ChoiceRequest,
  type DirectorOutput,
  type FxName,
  type Intent,
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
import { beatName, createState, grievances, recordChoice, remember, rememberHeard, saveScore, scoreText, type GroupState } from "./state";
import { speechVerdict, voiceKeywords } from "./speech";
import { boardHtml } from "./board";
import { monitorHtml } from "./monitor";
import type * as AskModule from "./ask";

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
  /** ask.ts: ASK BEESWAX (map/field questions skip the router; router answers are vetted). Absent = off. */
  ask?: typeof AskModule;
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

// ---------------------------------------------------------------- custom what-ifs: the last local check before the relay
// The router (Claude) writes the Orbis prompt for an unplanned what-if and is told to keep it safe. This is the Hive's own
// final check, so nothing unvetted ever reaches Reactor (moderation can end the billed session). Anything it rejects is
// treated as a refusal: Beeswax says "Grievance filed. Not that one." Over-refusing is fine; a refusal is a joke too.

export const CUSTOM_PROMPT_MAX = 600;

const URL_RX =
  /\b(?:https?|ftp|file|data|javascript|mailto):|\bwww\.|\b[a-z0-9-]+\.(?:com|net|org|io|dev|ai|co|gg|xyz|app|ly|me|tv|info|biz|us|uk|ru|cn|link|site|online|sh)\b/i;

// Whole words (with the listed endings), case-insensitive.
const DENY_WORDS = [
  // violence
  "kill(?:s|ed|er|ers|ing)?", "murder\\w*", "slaughter\\w*", "massacre\\w*", "behead\\w*", "decapitat\\w*", "dismember\\w*",
  "stab(?:s|bed|bing)?", "shoot(?:s|ing|er|ers)?", "shotguns?", "guns?", "gunfire", "gunm[ae]n", "rifles?", "pistols?",
  "bomb(?:s|ed|ing|er|ers)?", "grenades?", "explosives?", "terror\\w*", "weapons?", "blood(?:y|ied|shed|bath)?", "gore", "gory",
  "corpses?", "tortur\\w*", "wars?", "warfare", "genocid\\w*", "strangl\\w*", "assault\\w*", "execution\\w*",
  // harm to people, self-harm
  "suicid\\w*", "self[- ]?harm\\w*", "abus(?:e|ed|es|er|ers|ing)", "kidnap\\w*", "hostages?", "rap(?:e|ed|es|ist|ists|ing)",
  // sexual
  "sex", "sexy", "sexual\\w*", "nud(?:e|es|ity)", "naked", "porn\\w*", "nsfw", "erotic\\w*", "orgasm\\w*", "genital\\w*",
  "penis\\w*", "vagina\\w*", "breasts?", "boobs?", "nipples?", "fetish\\w*", "horny", "lingerie", "strip(?:per|pers|tease)",
  // hate
  "nazi\\w*", "hitler", "swastika\\w*", "kkk", "slurs?", "racis(?:t|ts|m)", "lynch\\w*", "supremac\\w*", "ethnic cleansing",
];
const DENY_RX = new RegExp(`\\b(?:${DENY_WORDS.join("|")})\\b`, "i");
// "people burning", "the children are hurt": harm to people, whichever order the words come in.
const PEOPLE = "people|persons?|humans?|man|men|woman|women|child|children|kids?|bab(?:y|ies)|someone|somebody|everyone|players?";
const HARM =
  "burn\\w*|die|dies|died|dying|dead|death|hurt\\w*|injur\\w*|attack\\w*|sting\\w*|stung|wound\\w*|bleed\\w*|drown\\w*|trapped|crush\\w*|scream\\w*";
const PEOPLE_HARM_RX = new RegExp(`\\b(?:${PEOPLE})\\b[^.!?]{0,40}\\b(?:${HARM})\\b|\\b(?:${HARM})\\b[^.!?]{0,40}\\b(?:${PEOPLE})\\b`, "i");
// Bee-world phrases that contain a denied word but are fine to show.
const ALLOWED_PHRASES = /\b(?:killer bees?|murder hornets?|seed bombs?)\b/gi;

export type CustomCheck = { ok: true; prompt: string } | { ok: false; reason: string };

/** The final local safety check on a custom Orbis prompt: <= 600 chars, no URLs, no denied words. */
export function checkCustomPrompt(raw: unknown): CustomCheck {
  if (typeof raw !== "string") return { ok: false, reason: "no prompt" };
  const prompt = raw
    .replace(/[\u00ad\u200b-\u200f\u2060\ufeff]/g, "") // invisible: deleted, so "bl\u200bood" can't split a word
    .replace(/[\u0000-\u001f\u007f-\u009f\u2028\u2029]/g, " ")
    .replace(/\s+/g, " ")
    .trim();
  if (!prompt) return { ok: false, reason: "empty prompt" };
  if (prompt.length > CUSTOM_PROMPT_MAX) return { ok: false, reason: `too long (${prompt.length} > ${CUSTOM_PROMPT_MAX} chars)` };
  const plain = prompt.normalize("NFKC");
  if (URL_RX.test(plain)) return { ok: false, reason: "contains a URL" };
  const words = plain.replace(ALLOWED_PHRASES, "bees");
  const hit = words.match(DENY_RX)?.[0] ?? words.match(PEOPLE_HARM_RX)?.[0];
  if (hit) return { ok: false, reason: `denylist: "${hit.toLowerCase()}"` };
  return { ok: true, prompt };
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
  try {
    const ask = (await importSibling("ask")) as typeof AskModule;
    if (typeof ask.answer !== "function") throw new Error("no answer() export");
    deps.ask = ask;
    const warmRest = deps.prewarm;
    deps.prewarm = async () => {
      await Promise.allSettled([warmRest?.(), ask.prewarm()]);
    };
  } catch (err) {
    log("WARNING: src/ask.ts failed to load; ASK BEESWAX questions go through the router:", err);
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

/** A route result inside the contract. For a custom what-if, custom_prompt has passed checkCustomPrompt (or is null). */
interface Routed extends RouteResultLike { blocked?: string }

/** What a typed or spoken utterance became (POST /board/custom answers with this). */
interface Understood { route: Routed; response: UtteranceResponse }

const isWhatIfScene = (s: Scene) => s.startsWith("whatif_");
/** The hook's forecast is a moment, not a place the window goes back to: never the base scene. */
const isForecastScene = (s: Scene) => s.startsWith("forecast_");
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
  /** Claude prompt-cache warm-up (deps.prewarm: router + director). The cache lasts 1 h: re-warm this long after the last
   *  warm-up finished (default 50 min), and on group.reset when the last one is older than prewarmStaleMs (default 45 min). */
  prewarmEveryMs?: number;
  prewarmStaleMs?: number;
  /** Warm up as soon as the Hive starts (default true). */
  prewarmOnStart?: boolean;
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
  const prewarmEveryMs = Math.max(1, opts.prewarmEveryMs ?? 50 * 60_000);
  const prewarmStaleMs = opts.prewarmStaleMs ?? 45 * 60_000;
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
      onChoiceRequest: (station, choice) => choiceRequest(station, choice),
      onFxForce: (name) => fxForce(name),
      onWhatIfForce: (m) => whatIfForce(m),
      onListenStart: (ws, mode) => listenStart(ws, mode),
      onMic: (ws, pcm) => micFrame(ws, pcm),
      onListenEnd: (ws, cancel) => finishListen(ws, cancel ? "cancel" : "end"),
      onClientClose: (ws) => finishListen(ws, "closed"),
      onLog: (role, text) => chipAsked(role, text),
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
  // ORBIS_WANT=live in the host's .env makes live Orbis the default (needs REACTOR_API_KEY); the Board can still switch.
  const envWant = process.env.ORBIS_WANT;
  let orbisWant: NonNullable<OrbisRequest["want"]> =
    envWant === "live" || envWant === "off" || envWant === "clips" ? envWant : "clips";
  let relayState: OrbisState = "off";
  let relaySince = 0; // the relay's `since`, so an older reply never overwrites a newer one
  let relayScene: Scene | null = null; // the scene whose prompt the relay was last given
  let relayPrompt: string | null = null; // ...and that prompt (custom what-ifs all share the scene whatif_custom)
  let relayUp: boolean | null = null;
  let relayChain: Promise<unknown> = Promise.resolve(); // POSTs go out one at a time, in order
  let published: OrbisStatus | null = null;
  let customClip: string | null = null; // whatif_custom has no recorded clip: Unity keeps the clip that was showing

  const relayRunning = () => relayState === "priming" || relayState === "ready";
  const clipOf = (scene: Scene) => deps.scenes[scene]?.clip || `${scene}.mp4`;
  const fallbackClip = (scene: Scene) => (scene === "whatif_custom" ? (customClip ?? clipOf(state.base_scene)) : clipOf(scene));
  /** What the relay shows for a scene: the vetted custom prompt for whatif_custom, else hive/lines/orbis_scenes.json. */
  const promptFor = (scene: Scene): string | null =>
    (scene === "whatif_custom" ? state.custom_prompt : null) || deps.scenes[scene]?.prompt || null;

  function orbisStatus(): OrbisStatus {
    return {
      type: "orbis",
      state: relayUrl ? relayState : "off",
      stage: SCENES.indexOf(state.scene),
      scene: state.scene,
      fallback_clip: fallbackClip(state.scene),
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
    if (!relayRunning()) {
      relayScene = null;
      relayPrompt = null;
    } else if (relayScene === null && SCENES.includes(s.scene as Scene)) {
      // After a Hive restart the relay may still be live: remember what it shows so the same prompt isn't re-sent.
      relayScene = s.scene as Scene;
      relayPrompt = typeof s.prompt === "string" ? s.prompt : null;
    }
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
    if (!relayUrl || !relayRunning()) return;
    const prompt = promptFor(scene);
    if (relayScene === scene && relayPrompt === prompt) return;
    if (!prompt) return observe(`no Orbis prompt for scene ${scene}`);
    relayScene = scene;
    relayPrompt = prompt;
    void queueRelay(() => relay("/orbis/stage", { prompt, scene }));
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
      const prompt = promptFor(scene);
      const seed = deps.scenes[scene]?.seed ?? deps.scenes.meadow?.seed;
      const now = await relay("/orbis/status");
      const running = now?.state === "priming" || now?.state === "ready";
      relayScene = scene;
      relayPrompt = prompt;
      if (!running) {
        await relay("/orbis/start", prompt ? { prompt, ...(seed === undefined ? {} : { seed }), scene } : { scene });
      } else if (prompt && now?.prompt !== prompt) {
        await relay("/orbis/stage", { prompt, scene });
      } else {
        observe(`orbis relay already ${String(now?.state)} on ${scene}`);
      }
    });
  }

  /** The one place the window changes: fx for the fire, the relay morph, and orbis{} for everyone. */
  function setScene(next: Scene, why: string): void {
    const prev = state.scene;
    if (next === "whatif_custom" && prev !== "whatif_custom") customClip = fallbackClip(prev);
    state.scene = next;
    if (!isWhatIfScene(next) && !isForecastScene(next) && next !== "fire_out") state.base_scene = next;
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
    // v2.1: with a scene (or stage) this is a scene switch only. `want` is optional there, and when a sender still adds
    // it (its possibly stale idea of the mode) it is ignored, so a Quest or Board never starts or stops the billed stream
    // by accident. Only a bare mode request (the Board's LIVE/CLIPS/OFF) changes the mode.
    const changeWant = !scene && msg.want !== undefined;
    if (!scene && !changeWant) return bus.send(from, { type: "error", message: "orbis.request needs want (live|clips|off) or a scene/stage" });
    if (changeWant) orbisWant = msg.want!;
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

  // v3.1: the run clock starts with the first station (smoker), like Unity's boarding countdown; the intro
  // (hook, meet) is off the clock. A Board jump past smoker starts it at whichever station beat comes first.
  const ON_CLOCK = new Set<BeatName>(["smoker", "feed", "wasps", "hornet", "heat", "whatif"]);

  function beatEntered(n: number, prev: number): void {
    if (n === prev) return; // a re-sent beat.set (Unity reconnect)
    const name = BEATS[n];
    const now = Date.now();
    if (name === "hook") {
      // A new run: clear the clock and the card. The clock itself starts at the first station beat.
      state.clock_start = null;
      state.saved_ms = null;
      state.score = null;
      variantTurn.clear(); // a new run starts on the base lines again (a group.reset clears it too)
      setScene("meadow", "hook");
    } else if (state.clock_start === null && name && ON_CLOCK.has(name)) {
      state.clock_start = now;
      observe(`score clock started at ${name}`);
    }
    if (name === "escape") {
      // v2.1: the score card. Frozen the first time the escape beat starts; re-sent if Unity comes back to escape.
      const first = state.score === null;
      const score = saveScore(state, now);
      if (score) {
        if (first) {
          log(`score: ${score.text} (grievances: ${score.grievances})`);
          observe(`score: ${score.text}`);
        }
        bus.broadcast(score);
      } else {
        observe("no score: the clock never started (no station beat before escape)");
      }
      setScene("full_bloom", "escape");
    } else if (name === "t0") {
      setScene("moon", "t0");
    } else if (name === "map") {
      setScene("full_bloom", "map");
    }
  }

  let lastChoice = { key: "", at: 0 };

  /** A station choice: Unity's choice.made, or the Board's choice.request when no Quest is connected (path "operator"). */
  function choiceMade(station: Station, choice: Choice, questSays: boolean | null, path: "quest" | "operator" = "quest"): void {
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
    const via = path === "operator" ? ", operator: no Quest, applied by the Hive" : "";
    log(`choice ${key}: ${correct ? "correct" : "wrong"} (${solved}/4 solved, ${grievances(state)} grievances${via})`);
    observe(`choice ${station}: ${choice} ${correct ? "CORRECT" : "wrong"} (${solved}/4 solved${via})`);
  }

  // ------------------------------------------------------------ v2.1 operator ops (Beat Board)

  /** choice.request: Unity applies it (hexagon, swarm, line) and reports choice.made as usual. No Quest: the Hive plays
   *  the outcome itself (line + fx + window + memory), so a rehearsal without the headset still works. */
  function choiceRequest(station: Station, choice: Choice): void {
    if (bus.count("quest") > 0) {
      const msg: ChoiceRequest = { type: "choice.request", station, choice };
      bus.broadcast(msg, ["quest"]);
      log(`operator choice.request ${station}:${choice} -> quest`);
      observe(`operator choice.request ${station}: ${choice} -> quest (path operator)`);
      return;
    }
    log(`operator choice.request ${station}:${choice}: no Quest, the Hive applies it`);
    choiceMade(station, choice, null, "operator");
  }

  /** fx.force: straight to Unity (board/monitor get the usual copy). */
  function fxForce(name: FxName): void {
    bus.fx(name);
    log(`operator fx ${name}`);
    observe(`operator fx ${name} (path operator)`);
  }

  /** whatif.force: the operator twin of a voice what-if or "put it out", through the same act() path. */
  function whatIfForce(m: { topic: WhatIfTopic | null; undo: boolean }): void {
    const r: Routed = m.undo
      ? { intent: "undo", topic: null, answer: null, ms: 0, path: "keyword" }
      : { intent: "what_if", topic: m.topic, answer: null, ms: 0, path: "keyword" };
    const what = m.undo ? "undo" : `what-if ${m.topic}`;
    log(`operator ${what}`);
    observe(`operator ${what} (path operator, window ${state.scene})`);
    const field = beatName(state) === "field";
    act(r, (reply) => sayReply(reply, "room"), field, ttsAvailable());
  }

  function groupReset(): void {
    dropJobs();
    for (const [id, l] of listens) {
      clearTimeout(l.watchdog);
      listens.delete(id);
    }
    lastChoice = { key: "", at: 0 };
    customClip = null;
    variantTurn.clear();
    bus.fx("embers_stop");
    publishOrbis(true); // reset() already put the window back on the meadow (and cleared the custom prompt and score)
    steerRelay(state.scene);
    // A new group is about to talk: make sure Claude's prompt cache (1 h) is still warm.
    if (lastWarm === null || Date.now() - lastWarm > prewarmStaleMs) void warmUp("group reset");
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

  async function safeRoute(text: string, ctx: { beat: BeatName; recent: string[] }): Promise<Routed> {
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
    const out: Routed = {
      intent,
      topic: intent === "what_if" ? topic : null,
      answer,
      ms: typeof r?.ms === "number" && Number.isFinite(r.ms) ? r.ms : Date.now() - t0,
      path: r?.path === "claude" ? "claude" : "keyword",
      error: r?.error,
    };
    // v2.1 custom what-if: Claude's prompt only goes on if it passes the Hive's own check; otherwise it is a refusal.
    if (out.topic === "custom") {
      const raw = r?.custom_prompt;
      const check = checkCustomPrompt(raw);
      out.custom_prompt = check.ok ? check.prompt : null;
      if (!check.ok) out.blocked = typeof raw === "string" ? `blocked by the Hive: ${check.reason}` : "the router refused";
    }
    return out;
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
      // ASK BEESWAX: a press with nothing in it gets "Didn't catch that" instead of silence (no Claude call).
      if (deps.ask && source === "quest" && stt.error !== "no_audio" && deps.ask.ASK_BEATS.includes(beatName(state)) && bus.count("quest") > 0)
        sayReply({ line_id: null, text: deps.ask.cannedText("ask_not_heard") }, "room");
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
   * `operator`: the Board typed it (POST /board/custom): it acts in any beat and Beeswax answers in the room.
   */
  async function understand(
    text: string,
    source: VoiceSource,
    t0: number,
    group: string,
    earReply?: (r: UtteranceResponse) => void,
    operator = false,
  ): Promise<Understood | null> {
    const recent = state.memory.heard.slice(-5);
    rememberHeard(state, text);
    const beat = beatName(state);
    if (deps.ask?.wants(text, beat)) return askTurn(text, source, t0, group, beat, earReply, operator);
    const r = await safeRoute(text, { beat, recent });
    deps.ask?.fixRouted(text, r); // canned/vetted answers; a lost question says "ask me again", not a random ack
    latency(operator ? "board.route" : "voice.route", r.ms);
    const intent: Intent = { type: "intent", name: r.intent, topic: r.topic, text, path: r.path, ms: Math.round(r.ms) };
    if (r.topic === "custom") intent.custom_prompt = r.custom_prompt ?? null;
    bus.broadcast(intent);
    // The answer rides along for the operator: with TTS down it is never spoken (Beeswax acks instead).
    const custom = r.topic !== "custom" ? "" : r.custom_prompt ? ` prompt: "${r.custom_prompt.slice(0, 200)}"` : ` REFUSED (${r.blocked ?? "no prompt"})`;
    observe(
      `${operator ? "operator text (path operator)" : `voice ${source}`}: "${text.slice(0, 80)}" -> ${r.intent}${r.topic ? `/${r.topic}` : ""} via ${r.path} ` +
        `${Math.round(r.ms)} ms${r.error ? ` (${r.error})` : ""}${r.answer ? ` answer: "${r.answer}"` : ""}${custom}`,
    );
    if (r.blocked) log(`custom what-if refused: ${r.blocked}`);
    if (group !== state.group_id) return null; // group reset mid-utterance

    const response: UtteranceResponse = { intent: r.intent, topic: r.topic, line_id: null, audio_url: null, caption: "" };
    if (!operator && !VOICE_BEATS.includes(beat)) {
      observe(`voice: not acting in beat ${beat}`);
      if (source === "glasses" && hasLine("ear_not_now")) Object.assign(response, { line_id: "ear_not_now", caption: lineText("ear_not_now") });
      earReply?.(response);
      return { route: r, response };
    }
    const field = beat === "field";
    const toRoom = !field && (operator || bus.count("quest") > 0);
    const deliver = (reply: Reply) => {
      if (toRoom) return sayReply(reply, "room");
      Object.assign(response, earVersion(reply));
      if (earReply) earReply(response);
      else bus.say("ear", response.line_id, { audio_url: response.audio_url, caption: response.caption }, false); // board/monitor copy
    };
    // A live answer needs our TTS in the room; the miniapp can read a caption aloud in Mentra's own voice.
    act(r, deliver, field, toRoom ? ttsAvailable() : true);
    latency(operator ? "board.total" : "voice.total", Date.now() - t0);
    return { route: r, response };
  }

  /** What-if, undo, ask, ack: say the line first, then change the window (the line covers the 2-4 s morph). */
  function act(r: RouteResultLike, deliver: (reply: Reply) => void, field: boolean, canSpeakAnswer: boolean): void {
    switch (r.intent) {
      case "what_if": {
        const topic = r.topic!;
        state.memory.whatifs.push(topic);
        if (topic === "custom") return customWhatIf(r.custom_prompt ?? null, deliver, field);
        // The base line first; a repeat in the same run gets its alternates in turn ("...Again?!").
        const id = `room_whatif_${topic}`;
        deliver(hasLine(id) ? lineReply(variant(id)) : ackReply());
        if (!field) setScene(`whatif_${topic}` as Scene, `what if: ${topic}`);
        return;
      }
      case "undo": {
        const cur = state.scene;
        if (cur === "whatif_fire") {
          deliver(lineReply(variant("room_whatif_out")));
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

  /** A pre-rendered line, else the fallback line, else a canned acknowledgement. */
  const lineReply = (id: string, fallback?: string): Reply => {
    const use = hasLine(id) ? id : fallback && hasLine(fallback) ? fallback : null;
    return use ? { line_id: use, text: lineText(use) } : ackReply();
  };

  const variantTurn = new Map<string, number>();
  /** A line or one of its alternates (lines tagged "alt:<id>"), in turn: the base line first, so a second fire or custom
   *  what-if in the same run gets a new joke. Reset on group.reset and on the hook beat. */
  function variant(id: string): string {
    const pool = [id, ...Object.values(deps.lines).filter((l) => l.tags?.includes(`alt:${id}`)).map((l) => l.id)].filter(hasLine);
    if (pool.length <= 1) return id;
    const n = variantTurn.get(id) ?? 0;
    variantTurn.set(id, n + 1);
    return pool[n % pool.length]!;
  }

  /**
   * An unplanned what-if (v2.1). Live stream: the cover line ("Hold on. I'm picturing it."), then the relay morphs to
   * Claude's prompt (scene whatif_custom; undo goes back). Clips: no recorded clip can show it, so the window keeps its
   * clip and Beeswax says the clips line instead (it is written to stand alone: "I'm picturing it. The window isn't.").
   * No prompt (Claude refused, or the Hive's check blocked it): the refusal line, and the window stays as it is.
   */
  function customWhatIf(raw: string | null, deliver: (reply: Reply) => void, field: boolean): void {
    // Checked again right here, the last step before the relay: nothing unvetted ever reaches Reactor.
    const check = raw ? checkCustomPrompt(raw) : null;
    if (!check?.ok) {
      if (check) observe(`custom what-if refused: ${check.reason}`);
      deliver(lineReply("room_whatif_refuse", "room_ack_grievance"));
      return;
    }
    if (!field && relayUrl && relayRunning()) {
      deliver(lineReply(variant("room_whatif_custom"), "room_ack_allow"));
      state.custom_prompt = check.prompt;
      setScene("whatif_custom", "custom what-if");
      observe(`custom what-if on the live window: "${check.prompt.slice(0, 200)}"`);
      return;
    }
    deliver(lineReply("room_whatif_custom_clips", "room_ack_tabled"));
    observe(`custom what-if: no live stream (clips), the window keeps its clip (prompt: "${check.prompt.slice(0, 120)}")`);
  }

  let lastAck = "";
  function ackReply(): Reply {
    const pool = deps.ackLines.filter((id) => hasLine(id) && id !== lastAck);
    const id = pool.length ? pool[Math.floor(Math.random() * pool.length)]! : hasLine(lastAck) ? lastAck : null;
    if (!id) return { line_id: null, text: "" };
    lastAck = id;
    return { line_id: id, text: lineText(id) };
  }

  /** ASK BEESWAX card: a poked chip arrives as the Quest log line "ask.chip <question>" and is answered like a spoken
   *  question (canned match, 0 ms, audio from memory), so the chips have Beeswax's voice without new clips. */
  function chipAsked(role: Role, text: string): void {
    if (role !== "quest" || !deps.ask || !text.startsWith("ask.chip ")) return;
    const q = cleanText(text.slice(9), 120);
    if (!q || !deps.ask.ASK_BEATS.includes(beatName(state))) return;
    bus.broadcast({ type: "transcript", text: q, final: true, source: "quest", ms: 0, stt: "chip" });
    void understand(q, "quest", Date.now(), state.group_id).catch((e) => log("ask.chip failed:", e));
  }

  /** ASK BEESWAX (map/field): ask.ts answers instead of the router; the first live answer of a group gets the filler. */
  async function askTurn(
    text: string,
    source: VoiceSource,
    t0: number,
    group: string,
    beat: BeatName,
    earReply: ((r: UtteranceResponse) => void) | undefined,
    operator: boolean,
  ): Promise<Understood | null> {
    const ask = deps.ask!;
    const toRoom = beat !== "field" && (operator || bus.count("quest") > 0);
    const onLive = () => toRoom && hasLine(ask.FILLER_LINE) && bus.say("room", ask.FILLER_LINE);
    const a = await ask.answer(text, { beat, group, run: state.memory }, { onLive });
    latency("ask.route", a.ms);
    const name: IntentName = a.kind === "ack" ? "ack" : a.kind === "echo" || a.kind === "not_heard" ? "unknown" : "ask";
    const route: Routed = { intent: name, topic: null, answer: a.text || null, ms: a.ms, path: a.path === "claude" ? "claude" : "keyword", error: a.error };
    bus.broadcast({ type: "intent", name, topic: null, text, path: route.path, ms: Math.round(a.ms) });
    observe(`ask ${operator ? "operator" : source}: "${text.slice(0, 80)}" -> ${a.kind}${a.ask_id ? `/${a.ask_id}` : ""} via ${a.path} ${a.ms} ms${a.error ? ` (${a.error})` : ""}${a.text ? ` "${a.text}"` : ""}`);
    const response: UtteranceResponse = { intent: name, topic: null, line_id: null, audio_url: null, caption: "" };
    if (group !== state.group_id) return null;
    if (a.kind !== "echo") {
      const reply: Reply = a.line_id ? lineReply(a.line_id, "room_ack_grievance") : a.text ? { line_id: null, text: a.text } : ackReply();
      if (toRoom) sayReply(reply, "room");
      else {
        Object.assign(response, earVersion(reply));
        if (earReply) earReply(response);
        else bus.say("ear", response.line_id, { audio_url: response.audio_url, caption: response.caption }, false);
      }
    }
    latency("ask.total", Date.now() - t0);
    return { route, response };
  }

  /** Live text with ask.ts loaded: its cached audio, or TTS once the first chunk is in (1.2 s at most), else the caption
   *  alone (Unity shows it and flaps his mouth), never a stream that ends silent. */
  async function speakPrimed(text: string): Promise<void> {
    const group = state.group_id;
    const audio = await deps.ask!.prepareSpeech(text, (t) => deps.ttsPcm(t)).catch(() => null);
    if (group !== state.group_id) {
      // reset while waiting: start the prepared stream and drop it at once, so its finally closes the TTS stream
      if (audio) void (async () => { for await (const _ of audio) break; })().catch(() => {});
      return;
    }
    if (!audio) {
      observe(`tts: no audio in ${deps.ask!.askLimits.firstChunkMs} ms, caption only: "${text.slice(0, 60)}"`);
      return bus.say("room", null, { caption: text });
    }
    const tts_id = shortId("t");
    bus.say("room", null, { tts_id, caption: text });
    void bus.enqueueRoomTts(tts_id, text, deps.ttsSampleRate, () => audio);
  }

  /** Play a reply from Beeswax on the Quest: a pre-rendered line, or live TTS (caption only when TTS is down). */
  function sayReply(reply: Reply, channel: "room"): void {
    if (reply.line_id) return bus.say(channel, reply.line_id);
    if (!reply.text) return;
    if (!ttsAvailable()) return bus.say(channel, null, { caption: reply.text });
    if (deps.ask) return void speakPrimed(reply.text);
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

  // ------------------------------------------------------------ Claude prompt-cache warm-up (router + director)
  // The cache lasts 1 h. Re-warm prewarmEveryMs after the last warm-up FINISHED (measured from the end, so the modules'
  // own "warmed in the last 50 min" guard never turns our re-warm into a no-op), and on group.reset when it is stale.

  let lastWarm: number | null = null; // when the last warm-up finished
  let warming: Promise<void> | null = null;
  let warmTimer: ReturnType<typeof setTimeout> | null = null;
  let stopped = false;

  function scheduleWarm(): void {
    if (warmTimer) clearTimeout(warmTimer);
    warmTimer = stopped || !deps.prewarm ? null : setTimeout(() => void warmUp("timer"), prewarmEveryMs);
  }

  function warmUp(why: string): Promise<void> {
    const prewarm = deps.prewarm;
    if (!prewarm || stopped) return Promise.resolve();
    if (warming) return warming;
    if (warmTimer) clearTimeout(warmTimer);
    warmTimer = null;
    const t0 = Date.now();
    warming = (async () => {
      try {
        await withTimeout(Promise.resolve(prewarm()), 60_000, "prewarm"); // a sync throw lands in the catch too
        log(`warm-up (${why}) done in ${Date.now() - t0} ms`);
      } catch (err) {
        log(`warm-up (${why}) failed:`, err instanceof Error ? err.message : err);
      } finally {
        lastWarm = Date.now();
        warming = null;
        scheduleWarm();
      }
    })();
    return warming;
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
      orbis: {
        state: orbisStatus().state,
        stage: SCENES.indexOf(state.scene),
        scene: state.scene,
        want: orbisWant,
        relay: relayUrl ? relayUp : null,
        // v2.1: the vetted prompt while a custom what-if is on the window
        ...(state.scene === "whatif_custom" && state.custom_prompt ? { custom_prompt: state.custom_prompt } : {}),
      },
      solved: state.memory.solved,
      // v2.1: the score card ({elapsed_s, text, grievances}) once the escape beat started, else null
      score: state.score ? { elapsed_s: state.score.elapsed_s, text: state.score.text, grievances: state.score.grievances } : null,
      score_text: scoreText(state),
      grievances: grievances(state),
      warmed_s_ago: lastWarm === null ? null : Math.round((Date.now() - lastWarm) / 1000),
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
    const reply: UtteranceResponse = r?.response ?? { intent: "unknown", topic: null, line_id: null, audio_url: null, caption: "" };
    return c.json(reply);
  });

  /** v2.1 Board: a typed what-if (or anything else), routed exactly as if the player had said it. Board token only. */
  app.post("/board/custom", async (c) => {
    if (!authed(c)) return c.json({ error: "bad token" }, 401);
    const body = await readJson(c);
    const text = cleanText(body?.text, 300);
    if (!text) return c.json({ error: "text required" }, 400);
    const t0 = Date.now();
    log(`operator text: "${text.slice(0, 80)}"`);
    bus.broadcast({ type: "transcript", text, final: true, source: "quest", ms: 0, stt: "board" });
    const ear = (reply: UtteranceResponse) => {
      if (reply.line_id || reply.audio_url || reply.caption) bus.say("ear", reply.line_id, { audio_url: reply.audio_url, caption: reply.caption });
    };
    const r = await understand(text, "quest", t0, state.group_id, ear, true);
    if (!r) return c.json({ ok: false, error: "the group was reset while routing" }, 409);
    const { route } = r;
    return c.json({
      ok: true,
      intent: route.intent,
      topic: route.topic,
      path: route.path,
      ms: Math.round(route.ms),
      ...(route.topic === "custom" ? { custom_prompt: route.custom_prompt ?? null, blocked: route.blocked ?? null } : {}),
      ...(route.answer ? { answer: route.answer } : {}),
      ...(route.error ? { error: route.error } : {}),
    });
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
  if (opts.prewarmOnStart === false) scheduleWarm();
  else void warmUp("start");
  // ASK BEESWAX's canned answers are spoken from memory: render them once (about 750 characters, no new clips).
  if (deps.ask && ttsAvailable() && process.env.ASK_WARM_AUDIO !== "0") void deps.ask.warmAudio((t) => deps.ttsPcm(t));
  // The relay changes state on its own too (first frame, failure, idle stop), so poll it.
  const orbisPoll = relayUrl ? setInterval(() => void relay("/orbis/status"), opts.orbisPollMs ?? 3000) : null;

  return {
    server,
    port: server.port!,
    url: `http://127.0.0.1:${server.port}`,
    state,
    bus,
    async stop() {
      stopped = true;
      if (warmTimer) clearTimeout(warmTimer);
      warmTimer = null;
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
  // startHive warms Claude's prompt cache now, every 50 min, and on group.reset when it is older than 45 min.

  const shutdown = () => {
    log("shutting down");
    void hive.stop().finally(() => process.exit(0));
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}
