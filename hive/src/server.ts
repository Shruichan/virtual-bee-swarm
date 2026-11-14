// The Hive: HTTP (Hono) + the websocket bus (Bun.serve) + the scan pipeline.
// Run: bun run src/server.ts   (env: PORT, HIVE_TOKEN, DIRECTOR_TIMEOUT_MS, PUBLIC_BASE_URL, ...)
import type { Server } from "bun";
import { Hono, type Context } from "hono";
import { cors } from "hono/cors";
import { hostname } from "node:os";
import sharp from "sharp";
import {
  BEATS,
  BINARY_KIND,
  ITEMS,
  PROTOCOL_VERSION,
  SCAN_BEATS,
  SOCKET_OF,
  VERDICTS,
  type BeatName,
  type Channel,
  type DirectorOutput,
  type Item,
  type OrbisRequest,
  type OrbisState,
  type OrbisStatus,
  type PressResponse,
  type Role,
  type ScanPath,
  type ScanResponse,
  type Verdict,
} from "./protocol";
import { Bus, checkToken, log, withTimeout, type ClientData } from "./bus";
import { beatName, createState, remember, type GroupState } from "./state";
import { speechVerdict } from "./speech";
import { boardHtml } from "./board";
import { monitorHtml } from "./monitor";

// ---------------------------------------------------------------- injected dependencies
// director.ts / tts.ts / lines.ts belong to the AI builder. They are injected so tests can mock
// them and so a broken AI module degrades the Hive (speech + operator paths) instead of killing it.

export interface LineInfo { id: string; channel: Channel; text: string; beat?: string; tags?: string[] }
export interface JudgeInputLike {
  photo?: Uint8Array;
  mime?: string;
  heard?: string;
  beat: BeatName;
  memory: { scanned: Item[] };
}
export interface JudgeResultLike extends DirectorOutput { ms: number; model: string; fallback: boolean }

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
  /** Optional (lines.ts extras): a pre-rendered line whose text matches, so canned text needs no live TTS. */
  lineIdForText?(text: string, channel: Channel): string | null;
  /** Optional (lines.ts extras): the splashdown ear callback for what this group did. */
  callbackLine?(memory: GroupState["memory"]): string;
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

/** Load the real AI modules; any that fail to load are replaced by safe stubs (logged loudly). */
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
  };
  try {
    const lines = await import("./lines");
    deps.lines = lines.LINES;
    deps.verdictLine = lines.verdictLine;
    deps.lineText = lines.lineText;
    deps.lineIdForText = lines.lineIdForText;
    deps.callbackLine = lines.callbackLine;
  } catch (err) {
    log("WARNING: src/lines.ts failed to load; no pre-rendered lines:", err);
  }
  try {
    const director = await import("./director");
    deps.judge = director.judge;
    deps.prewarm = director.prewarm;
  } catch (err) {
    log("WARNING: src/director.ts failed to load; Claude path disabled (speech/operator only):", err);
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
  return deps;
}

// ---------------------------------------------------------------- scan vocabulary helpers

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
  const hasLine = (id: string) => Boolean(deps.lines[id]);
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
    if (!line_id || !hasLine(line_id) || earCuesPlayed.has(`${state.group_id}:${cue}`)) return;
    earCuesPlayed.add(`${state.group_id}:${cue}`);
    bus.say("ear", line_id);
  };

  const bus: Bus = new Bus(
    state,
    {
      lineText,
      onVerdictForce: (item) => forceVerdict(item),
      onPlea: (text) => plea(text),
      onGroupReset: () => dropJobs(),
      onOrbisRequest: (msg) => void relayOrbis(msg),
      onBeat: (n) => {
        // Only the Hive knows what this group did, so it plays the splashdown callback in the ear.
        if (BEATS[n] !== "splashdown") return;
        try {
          earOnce("callback", deps.callbackLine?.(state.memory));
        } catch (err) {
          log("callbackLine threw:", err);
        }
      },
      onHornetCooked: () => earOnce("hornet_done", "ear_hornet_done"),
    },
    { token, host: opts.host ?? hostname(), heartbeatMs: opts.heartbeatMs },
  );

  const scanRoles = (job: ScanJob): Role[] => (job.beat === "field" ? OBSERVERS : ["quest", ...OBSERVERS]);
  const observe = (text: string) => bus.observe({ type: "log", text });

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

  // ------------------------------------------------------------ press / scan / verdict

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

    // A live line (off-list roast) plays on ONE device: Beeswax on the Quest if it's there, else the ear.
    // Canned text that matches a pre-rendered line plays that line instead of going through TTS.
    if (d.line) {
      const canSpeak = ttsAvailable();
      if (questLive && !roomLine) {
        roomLine = lineIdFor(d.line, "room");
        roomCaption = roomLine ? lineText(roomLine) : d.line;
        if (!roomLine && canSpeak) tts_id = shortId("t");
      } else if (!questLive && !earLine) {
        earLine = lineIdFor(d.line, "ear");
        earCaption = earLine ? lineText(earLine) : d.line;
        if (!earLine && canSpeak) {
          const id = crypto.randomUUID();
          const text = d.line;
          audio.put(id, Promise.resolve().then(() => deps.ttsMp3(text)));
          audio_url = `${baseUrl || `http://localhost:${server.port}`}/audio/${id}.mp3`;
        }
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
        const text = d.line!;
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

  // ------------------------------------------------------------ Orbis relay (hive/orbis-relay)
  // The relay owns the billed Reactor session. The Hive steers it from the Board's orbis.request and mirrors its
  // state on the bus as orbis{} (the Quest also gets that state straight from the relay's frames socket).

  const relayUrl = opts.orbisRelayUrl?.replace(/\/+$/, "") || null;
  const ORBIS_STATES: readonly OrbisState[] = ["off", "priming", "ready", "failed", "ended"];
  let orbis: OrbisStatus | null = null; // last state mirrored on the bus
  let orbisSince = 0; // the relay's `since` of that state, so an older reply never overwrites a newer one
  let relayUp: boolean | null = null;

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
      mirrorOrbis(json as Record<string, unknown>);
      return json as Record<string, unknown>;
    } catch (err) {
      // Polls only report the first failure, not one every few seconds.
      if (body || relayUp !== false) observe(`orbis relay unreachable: ${err instanceof Error ? err.message : err}`);
      relayUp = false;
      return null;
    }
  }

  function mirrorOrbis(s: Record<string, unknown>): void {
    const state = ORBIS_STATES.find((x) => x === s.state);
    if (!state) return;
    const since = typeof s.since === "number" ? s.since : 0;
    if (since && since < orbisSince) return;
    orbisSince = Math.max(orbisSince, since);
    const next: OrbisStatus = {
      type: "orbis",
      state,
      stage: typeof s.stage === "number" ? s.stage : 0,
      fallback_clip: typeof s.fallback_clip === "string" && s.fallback_clip ? s.fallback_clip : null,
    };
    if (orbis && orbis.state === next.state && orbis.stage === next.stage && orbis.fallback_clip === next.fallback_clip) return;
    orbis = next;
    bus.broadcast(next);
  }

  /**
   * live: start the stream, or morph a running one to the requested stage (/orbis/stage, 2-4 s): /orbis/start on a
   * running stream resets it (3-4 s with no frames). clips/off: stop, since Reactor bills while connected.
   */
  async function relayOrbis(msg: OrbisRequest): Promise<void> {
    if (!relayUrl) return;
    if (msg.want !== "live") {
      await relay("/orbis/stop", {});
      return;
    }
    const now = await relay("/orbis/status");
    const running = now?.state === "priming" || now?.state === "ready";
    if (!running) await relay("/orbis/start", msg.stage !== undefined ? { stage: msg.stage } : {});
    else if (msg.stage !== undefined && msg.stage !== now?.stage) await relay("/orbis/stage", { stage: msg.stage });
    else observe(`orbis relay already ${String(now?.state)} at stage ${String(now?.stage)}`);
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
      orbis: orbis && { state: orbis.state, stage: orbis.stage },
      tts: ttsAvailable(),
      lines: Object.keys(deps.lines).length,
      uptime_s: Math.round((Date.now() - startedAt) / 1000),
    }),
  );

  app.get("/board", (c) => (authed(c) ? c.html(boardHtml()) : c.text("Add ?token=<HIVE_TOKEN> to the URL.", 401)));
  app.get("/monitor", (c) => (authed(c) ? c.html(monitorHtml()) : c.text("Add ?token=<HIVE_TOKEN> to the URL.", 401)));
  app.get("/lines.json", (c) => (authed(c) ? c.json(deps.lines) : c.json({ error: "bad token" }, 401)));

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
    idleTimeout: 60, // /glasses/scan and /audio can legitimately wait ~15 s
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
    orbisRelayUrl: process.env.ORBIS_RELAY_URL || null,
    host: process.env.HIVE_HOST || undefined,
  });
  log(`hive v${PROTOCOL_VERSION} on :${hive.port}  lines=${Object.keys(deps.lines).length} tts=${deps.ttsAvailable()}`);
  log(`board:   /board?token=$HIVE_TOKEN    monitor: /monitor?token=$HIVE_TOKEN`);
  void deps.prewarm?.().catch(() => {});

  const shutdown = () => {
    log("shutting down");
    void hive.stop().finally(() => process.exit(0));
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}
