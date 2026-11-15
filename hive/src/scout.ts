// Live Bee Scout: the game (pure ScoutGame, unit-tested with a fake clock) and its HTTP routes.
// The vision worker posts frames; when the wearer looks steadily at a new booth object, Beeswax plays the
// chime + that object's hook in the glasses ear and waits for yes / no. Contract: ./scout-protocol.ts.
import type { Hono, Context } from "hono";
import { cors } from "hono/cors";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import type { Bus } from "./bus";
import { log } from "./bus";
import { scoutDashboardHtml, scoutSlidesHtml } from "./scout-dashboard";
import {
  SCOUT_BACKGROUND,
  SCOUT_LABELS,
  SCOUT_LINES,
  SCOUT_TUNING,
  type ScoutAnswer,
  type ScoutBlockReason,
  type ScoutClass,
  type ScoutEvent,
  type ScoutFrame,
  type ScoutLabel,
  type ScoutLine,
  type ScoutPhase,
  type ScoutState,
} from "./scout-protocol";

// ---------------------------------------------------------------- answers

const YES = ["yes", "yeah", "yep", "yup", "ya", "yea", "sure", "okay", "ok", "please", "tell me", "go on", "go ahead", "more", "uh huh", "definitely", "of course", "absolutely", "why not", "alright", "all right"];
const NO = ["no", "nope", "nah", "not now", "later", "skip", "pass", "no thanks", "no thank you", "not really", "never mind", "nevermind"];
const RUDE = ["shut up", "be quiet", "quiet", "go away", "stop talking", "leave me alone", "buzz off", "i don't care", "i dont care", "don't care", "dont care", "who cares", "boring"];

export const normText = (s: string) =>
  s.toLowerCase().replace(/[’']/g, "'").replace(/[^a-z0-9' ]+/g, " ").replace(/\s+/g, " ").trim();
const hasPhrase = (t: string, phrases: string[]) => phrases.some((p) => ` ${t} `.includes(` ${p} `));

/** rude > no > yes; anything else is "other". */
export function classifyAnswer(text: string): ScoutAnswer {
  const t = normText(text);
  if (!t) return "other";
  if (hasPhrase(t, RUDE)) return "rude";
  if (hasPhrase(t, NO)) return "no";
  if (hasPhrase(t, YES)) return "yes";
  return "other";
}

// ---------------------------------------------------------------- the game

export interface ScoutSay { line_id: string; caption: string; audio_url: string | null }

export interface ScoutGameDeps {
  lines: ScoutLine[];
  now?: () => number;
  /** Sends one line to the glasses (the routes broadcast it on the bus). */
  say?: (s: ScoutSay) => void;
  event?: (e: ScoutEvent) => void;
  /** Absolute mp3 URL for a line id, or null (the glasses then speak the caption). */
  audioUrl?: (id: string) => string | null;
  /** Measured audio length; null = estimate from the caption. */
  durationMs?: (id: string) => number | null;
  tuning?: Partial<typeof SCOUT_TUNING>;
  random?: () => number;
}

/** A skipped object is offered again after this long, or once everything else is found. */
export const SKIP_REOFFER_MS = 60_000;
/** At most one idle nudge this often. */
export const IDLE_EVERY_MS = 60_000;
/** Frames further apart than this break a steady look (the worker stalled). */
export const FRAME_STALE_MS = 1500;

export function estimateMs(text: string): number {
  return Math.round((text.length / 14.4) * 1000 + 300);
}

const LABEL_SET = new Set<string>(SCOUT_LABELS);
const isLabel = (s: unknown): s is ScoutLabel => typeof s === "string" && LABEL_SET.has(s);

export class ScoutGame {
  readonly tuning: typeof SCOUT_TUNING;
  private readonly lines: Map<string, ScoutLine>;
  private readonly now: () => number;
  private readonly d: ScoutGameDeps;

  private phase: ScoutPhase = "looking";
  private found: ScoutLabel[] = [];
  private skipped = new Map<ScoutLabel, number>(); // label -> skipped at
  private offer: ScoutLabel | null = null;
  private current: { label: ScoutClass; score: number } | null = null;
  private startedAt: number | null = null;

  /** When the last line sent is expected to finish playing. */
  busyUntil = 0;
  private offerDeadline = 0;
  private lookingSince = 0;
  private lastIdleAt = -Infinity;
  private noTurn = 0;
  private run: { label: ScoutLabel; since: number; firstAt: number; lastAt: number; score: number } | null = null;
  private lastSlide: { label: ScoutClass | null; at: number; recvAt: number } | null = null;
  private practice = false;
  private lastFind: { label: ScoutLabel; at: number } | null = null;
  private blockedKey = "";
  private lastSig = "";

  constructor(deps: ScoutGameDeps) {
    this.d = deps;
    this.lines = new Map(deps.lines.map((l) => [l.id, l]));
    this.now = deps.now ?? Date.now;
    this.tuning = { ...SCOUT_TUNING, ...deps.tuning };
    this.lastSig = this.sig();
  }

  state(): ScoutState {
    return {
      phase: this.phase,
      found: [...this.found],
      skipped: [...this.skipped.keys()],
      offer: this.offer,
      current: this.current ? { ...this.current } : null,
      started_at: this.startedAt,
      practice: this.practice,
    };
  }

  /** POST /scout/practice: screen-test mode, any object can trigger again once the slide changes. */
  setPractice(on: boolean): void {
    this.practice = on;
    this.changed();
  }

  /** POST /scout/start: reset, play the intro, then look. */
  start(): void {
    const now = this.now();
    this.found = [];
    this.skipped.clear();
    this.offer = null;
    this.run = null;
    this.startedAt = now;
    this.busyUntil = now; // cut short whatever was "playing"
    this.lastIdleAt = -Infinity;
    this.phase = "intro";
    for (const id of SCOUT_LINES.intro) this.say(id);
    this.changed();
  }

  frame(f: ScoutFrame): void {
    const now = this.now();
    this.tick();
    const top = Array.isArray(f.top) ? f.top : [];
    const best = top[0];
    this.current = best ? { label: best.label, score: best.score } : null;
    this.emit({ type: "frame", frame: f });
    const second = top[1]?.score ?? 0;
    const counts = best && isLabel(best.label) && best.score >= this.tuning.minScore && best.score - second >= this.tuning.minMargin;
    if (this.phase !== "looking") {
      this.run = null;
      // Seen clearly while Beeswax was busy (intro / offer / fact): tell the dashboard, once per busy stretch.
      if (counts && best && isLabel(best.label) && best.label !== this.offer) this.blocked(best.label, "busy", best.score, `${this.phase}:${this.busyUntil}`);
      return;
    }
    if (!counts || !best || !isLabel(best.label)) {
      this.run = null;
      return;
    }
    const at = typeof f.at === "number" && Number.isFinite(f.at) ? f.at : now;
    if (!this.run || this.run.label !== best.label || now - this.run.lastAt > FRAME_STALE_MS) {
      this.run = { label: best.label, since: now, firstAt: at, lastAt: now, score: best.score };
    } else {
      this.run.lastAt = now;
      this.run.score = best.score;
    }
    const lookMs = now - this.run.since;
    if (lookMs < this.tuning.steadyMs) return;
    const reason: ScoutBlockReason | null = !this.eligible(best.label)
      ? this.found.includes(best.label) ? "already_found" : "skipped"
      : now < this.busyUntil + this.tuning.gapMs ? "cooldown" : null;
    if (reason) this.blocked(best.label, reason, best.score, String(this.run.since));
    else this.foundIt(best.label, best.score, lookMs, now - this.run.firstAt);
  }

  private blocked(label: ScoutLabel, reason: ScoutBlockReason, score: number, episode: string): void {
    const key = `${reason}:${label}:${episode}`;
    if (key === this.blockedKey) return;
    this.blockedKey = key;
    this.emit({ type: "blocked", label, reason, score, at: this.now() });
  }

  /** A final transcript from the glasses. Returns how it was read. */
  utterance(text: string, at?: number): ScoutAnswer {
    const now = this.now();
    this.tick();
    const raw = String(text ?? "").slice(0, 300);
    if (this.phase !== "offering") {
      this.emit({ type: "heard", text: raw, answer: "other", at: now });
      return "other";
    }
    const answer = this.isEcho(raw) ? "other" : classifyAnswer(raw);
    this.emit({ type: "heard", text: raw, answer, at: now });
    if (answer !== "other") this.answer(answer, at);
    return answer;
  }

  /** A short button press: "yes" during an offer, else nothing. */
  button(at?: number): boolean {
    this.tick();
    if (this.phase !== "offering") return false;
    this.emit({ type: "heard", text: "(button)", answer: "yes", at: this.now() });
    this.answer("yes", at);
    return true;
  }

  slide(label: ScoutClass | null, at: number): void {
    this.lastSlide = { label, at, recvAt: this.now() };
    this.emit({ type: "slide", label, at });
  }

  /** Time-driven transitions: lines ending, offer timeout, idle nudge. Safe to call any time. */
  tick(): void {
    const now = this.now();
    if ((this.phase === "intro" || this.phase === "telling") && now >= this.busyUntil) {
      this.toLooking(now);
    } else if (this.phase === "offering" && now >= this.offerDeadline) {
      this.decline("silence");
    } else if (
      this.phase === "looking" &&
      this.startedAt !== null &&
      now >= this.busyUntil + this.tuning.gapMs &&
      now - Math.max(this.lookingSince, this.busyUntil) >= this.tuning.idleMs &&
      now - this.lastIdleAt >= IDLE_EVERY_MS
    ) {
      this.lastIdleAt = now;
      this.say(SCOUT_LINES.idle);
    }
    this.changed();
  }

  // ------------------------------------------------------------ internals

  private eligible(label: ScoutLabel): boolean {
    if (this.practice) {
      // Practice: anything goes, except repeating the last find before the slide has changed.
      const last = this.lastFind;
      return !(last && last.label === label && (!this.lastSlide || this.lastSlide.recvAt <= last.at));
    }
    if (this.found.includes(label)) return false;
    const skippedAt = this.skipped.get(label);
    if (skippedAt === undefined) return true;
    if (this.now() - skippedAt >= SKIP_REOFFER_MS) return true;
    return SCOUT_LABELS.every((l) => l === label || this.found.includes(l));
  }

  private foundIt(label: ScoutLabel, score: number, lookMs: number, frameMs: number): void {
    const now = this.now();
    this.run = null;
    this.offer = label;
    this.lastFind = { label, at: now };
    this.phase = "offering";
    this.emit({ type: "found", label, score, at: now, look_ms: Math.round(lookMs) });
    if (this.lastSlide && this.lastSlide.label === label) this.latency("slide_to_found", now - this.lastSlide.at);
    this.latency("frame_to_found", frameMs);
    this.say(SCOUT_LINES.chime);
    this.say(SCOUT_LINES.hook(label));
    this.latency("found_to_say", this.now() - now);
    this.offerDeadline = this.busyUntil + this.tuning.offerTimeoutMs;
    this.changed();
  }

  private answer(answer: ScoutAnswer, at?: number): void {
    const label = this.offer;
    if (!label) return;
    if (answer === "yes") {
      const now = this.now();
      this.skipped.delete(label);
      const repeat = this.found.includes(label); // practice mode: a second find of the same object
      if (!repeat) this.found.push(label);
      this.phase = "telling";
      this.say(SCOUT_LINES.fact(label));
      const heardAt = typeof at === "number" && Number.isFinite(at) && Math.abs(now - at) < 30_000 ? Math.min(at, now) : now;
      this.latency("answer_to_fact", this.now() - heardAt);
      const n = this.found.length;
      if (repeat) {
        // no progress line for a repeat
      } else if (n >= SCOUT_LABELS.length) {
        this.say(SCOUT_LINES.done);
        this.phase = "done";
        this.offer = null;
      } else {
        this.say(SCOUT_LINES.progress(n));
      }
      this.changed();
      return;
    }
    this.decline(answer === "rude" ? "rude" : "no");
  }

  private decline(why: "no" | "rude" | "silence"): void {
    const label = this.offer;
    const now = this.now();
    if (label) this.skipped.set(label, now);
    if (why === "rude") this.say(SCOUT_LINES.rude);
    else if (why === "silence") this.say(SCOUT_LINES.silence);
    else this.say(SCOUT_LINES.no[this.noTurn++ % SCOUT_LINES.no.length]!);
    this.toLooking(now);
  }

  private toLooking(now: number): void {
    this.phase = "looking";
    this.offer = null;
    this.run = null;
    this.lookingSince = now;
    this.changed();
  }

  /** The mic heard Beeswax's own chime/hook (3+ words, all inside the line). */
  private isEcho(text: string): boolean {
    const t = normText(text);
    if (t.split(" ").length < 3 || !this.offer) return false;
    const lines = [SCOUT_LINES.chime, SCOUT_LINES.hook(this.offer)].map((id) => normText(this.lines.get(id)?.text ?? ""));
    return lines.some((l) => l.includes(t));
  }

  private say(id: string): void {
    const line = this.lines.get(id);
    const caption = line?.text ?? "";
    if (!line) log(`scout: no line ${id} in scout_lines.json`);
    const now = this.now();
    const ms = this.d.durationMs?.(id) ?? estimateMs(caption);
    this.busyUntil = Math.max(now, this.busyUntil) + ms;
    let audio_url: string | null = null;
    try {
      audio_url = this.d.audioUrl?.(id) ?? null;
    } catch {}
    try {
      this.d.say?.({ line_id: id, caption, audio_url });
    } catch (err) {
      log("scout: say failed:", err);
    }
    this.emit({ type: "say", line_id: id, caption, audio_url, at: now });
  }

  private latency(hop: "slide_to_found" | "frame_to_found" | "found_to_say" | "answer_to_fact", ms: number): void {
    this.emit({ type: "latency", hop, ms: Math.max(0, Math.round(ms)) });
  }

  private emit(e: ScoutEvent): void {
    try {
      this.d.event?.(e);
    } catch (err) {
      log("scout: event handler failed:", err);
    }
  }

  private sig(): string {
    return JSON.stringify([this.phase, this.found, [...this.skipped.keys()], this.offer, this.startedAt, this.practice]);
  }

  private changed(): void {
    const s = this.sig();
    if (s === this.lastSig) return;
    this.lastSig = s;
    this.emit({ type: "state", state: this.state() });
  }
}

// ---------------------------------------------------------------- files

const LINES_DIR = fileURLToPath(new URL("../lines/", import.meta.url));
const audioDir = () => process.env.SCOUT_AUDIO_DIR || join(LINES_DIR, "scout_audio");

export function loadScoutLines(path = join(LINES_DIR, "scout_lines.json")): ScoutLine[] {
  try {
    const raw = JSON.parse(readFileSync(path, "utf8"));
    if (!Array.isArray(raw)) throw new Error("not an array");
    return raw.filter((l) => l && typeof l.id === "string" && typeof l.text === "string");
  } catch (err) {
    log(`scout: can't read ${path}:`, err instanceof Error ? err.message : err);
    return [];
  }
}

/** {id: {ms}} from scout_audio/manifest.json, re-read when it changes on disk (the lines lane regenerates it). */
let manifestCache: { key: string; ms: Record<string, number> } | null = null;
function manifestMs(id: string): number | null {
  const path = join(audioDir(), "manifest.json");
  try {
    const st = Bun.file(path);
    const key = `${path}:${st.lastModified}:${st.size}`;
    if (!manifestCache || manifestCache.key !== key) {
      const raw = JSON.parse(readFileSync(path, "utf8")) as Record<string, { ms?: unknown }>;
      const ms: Record<string, number> = {};
      for (const [k, v] of Object.entries(raw ?? {})) if (typeof v?.ms === "number" && v.ms > 0) ms[k] = v.ms;
      manifestCache = { key, ms };
    }
    return manifestCache.ms[id] ?? null;
  } catch {
    return null;
  }
}

const SAFE_FILE = /^[a-z0-9_]{1,80}\.mp3$/;

// ---------------------------------------------------------------- routes

export interface ScoutRouteCtx {
  bus: Bus;
  /** Token check, same as /glasses/* (?token= or x-hive-token). */
  authed: (c: Context) => boolean;
  /** Public base URL of this Hive, for absolute audio URLs. */
  base: () => string;
  /** Learn the base URL from a request's Host header (server.ts rememberBase). */
  rememberBase?: (c: Context) => void;
  stopped?: () => boolean;
  now?: () => number;
}

const FRAME_EVENT_MS = 200; // ~5 a second on SSE

export function registerScoutRoutes(app: Hono, ctx: ScoutRouteCtx): ScoutGame {
  const listeners = new Set<(e: ScoutEvent) => void>();
  let lastFrameEvent = 0;
  const fanout = (e: ScoutEvent) => {
    if (e.type === "frame") {
      const t = Date.now();
      if (t - lastFrameEvent < FRAME_EVENT_MS) return;
      lastFrameEvent = t;
    } else if (e.type !== "state") {
      log(`scout: ${e.type} ${JSON.stringify(e).slice(0, 160)}`);
    }
    for (const l of listeners) l(e);
  };

  const game = new ScoutGame({
    lines: loadScoutLines(),
    now: ctx.now,
    event: fanout,
    durationMs: manifestMs,
    audioUrl: (id) => (existsSync(join(audioDir(), `${id}.mp3`)) ? `${ctx.base()}/scout/audio/${id}.mp3` : null),
    // line_id stays null (no embedded clip in the glasses); tts_id carries the scout line id so the glasses can
    // light the LED per line even when audio_url is null. Ear lines never use tts_id otherwise.
    say: (s) =>
      ctx.bus.broadcast({ type: "say", channel: "ear", line_id: null, tts_id: s.line_id, audio_url: s.audio_url, caption: s.caption }, ["glasses"]),
  });

  const ticker = setInterval(() => {
    if (ctx.stopped?.()) return clearInterval(ticker);
    game.tick();
  }, 200);
  (ticker as { unref?: () => void }).unref?.();

  const readJson = async (c: Context): Promise<Record<string, unknown> | null> => {
    try {
      const v = await c.req.json();
      return typeof v === "object" && v !== null && !Array.isArray(v) ? v : null;
    } catch {
      return null;
    }
  };
  const num = (v: unknown, dflt: number) => (typeof v === "number" && Number.isFinite(v) ? v : dflt);
  const isClass = (v: unknown): v is ScoutClass => v === SCOUT_BACKGROUND || isLabel(v);

  app.use("/scout/*", cors({ origin: "*", allowHeaders: ["content-type", "x-hive-token"], allowMethods: ["GET", "POST", "OPTIONS"] }));
  app.use("/scout/*", async (c, next) => {
    if (c.req.method === "OPTIONS" || c.req.path.startsWith("/scout/audio/")) return next(); // mp3s: public lines, no token (like /audio)
    if (!ctx.authed(c)) {
      return c.req.path === "/scout/slides" || c.req.path === "/scout" ? c.text("Add ?token=<HIVE_TOKEN> to the URL.", 401) : c.json({ error: "bad token" }, 401);
    }
    ctx.rememberBase?.(c);
    await next();
  });

  app.get("/scout", (c) => {
    if (!ctx.authed(c)) return c.text("Add ?token=<HIVE_TOKEN> to the URL.", 401);
    ctx.rememberBase?.(c);
    return c.html(scoutDashboardHtml());
  });
  app.get("/scout/slides", (c) => c.html(scoutSlidesHtml()));
  app.get("/scout/state", (c) => c.json(game.state()));

  app.post("/scout/frame", async (c) => {
    const body = await readJson(c);
    const top = Array.isArray(body?.top) ? body.top : null;
    if (!body || !top) return c.json({ error: "top required" }, 400);
    const clean = top
      .filter((t): t is { label: ScoutClass; score: number } => !!t && isClass(t.label) && typeof t.score === "number" && Number.isFinite(t.score))
      .slice(0, 3)
      .map((t) => ({ label: t.label, score: t.score }));
    let scores: Partial<Record<ScoutClass, number>> | undefined;
    if (body.scores && typeof body.scores === "object") {
      scores = {};
      for (const [k, v] of Object.entries(body.scores as Record<string, unknown>)) if (isClass(k) && typeof v === "number" && Number.isFinite(v)) scores[k] = v;
    }
    const now = Date.now();
    game.frame({
      at: num(body.at, now),
      frame_ms: num(body.frame_ms, 0),
      fps: num(body.fps, 0),
      top: clean,
      ...(scores ? { scores } : {}),
      source: typeof body.source === "string" ? body.source.slice(0, 200) : "",
    });
    return c.json({ ok: true, phase: game.state().phase });
  });

  app.post("/scout/utterance", async (c) => {
    const body = await readJson(c);
    const text = typeof body?.text === "string" ? body.text.trim() : "";
    if (!text) return c.json({ error: "text required" }, 400);
    const answer = game.utterance(text, typeof body?.at === "number" ? body.at : undefined);
    return c.json({ ok: true, answer, phase: game.state().phase });
  });

  app.post("/scout/button", async (c) => {
    const body = (await readJson(c)) ?? {};
    const yes = game.button(typeof body.at === "number" ? body.at : undefined);
    return c.json({ ok: true, answer: yes ? "yes" : null, phase: game.state().phase });
  });

  app.post("/scout/start", (c) => {
    game.start();
    return c.json({ ok: true, state: game.state() });
  });

  app.post("/scout/practice", async (c) => {
    const body = await readJson(c);
    if (typeof body?.on !== "boolean") return c.json({ error: "on (boolean) required" }, 400);
    game.setPractice(body.on);
    return c.json(game.state());
  });

  app.post("/scout/slide", async (c) => {
    const body = await readJson(c);
    if (!body || !(body.label === null || isClass(body.label))) return c.json({ error: "label required (a class or null)" }, 400);
    game.slide(body.label as ScoutClass | null, num(body.at, Date.now()));
    return c.json({ ok: true });
  });

  app.get("/scout/events", (c) => {
    const enc = new TextEncoder();
    let cleanup = () => {};
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        const write = (s: string) => {
          try {
            controller.enqueue(enc.encode(s));
          } catch {
            cleanup();
          }
        };
        const send = (e: ScoutEvent) => write(`event: ${e.type}\ndata: ${JSON.stringify(e)}\n\n`);
        listeners.add(send);
        const keep = setInterval(() => write(": keepalive\n\n"), 15_000);
        cleanup = () => {
          listeners.delete(send);
          clearInterval(keep);
        };
        write("retry: 2000\n\n");
        send({ type: "state", state: game.state() });
      },
      cancel() {
        cleanup();
      },
    });
    c.req.raw.signal?.addEventListener("abort", () => cleanup());
    return new Response(stream, {
      headers: { "content-type": "text/event-stream", "cache-control": "no-cache", connection: "keep-alive", "access-control-allow-origin": "*" },
    });
  });

  app.get("/scout/audio/:file", (c) => {
    const file = c.req.param("file");
    if (!SAFE_FILE.test(file)) return c.text("bad file name", 400);
    const path = join(audioDir(), file);
    if (!existsSync(path)) return c.text("not found", 404);
    return new Response(Bun.file(path), { headers: { "content-type": "audio/mpeg", "cache-control": "public, max-age=300" } });
  });

  return game;
}
