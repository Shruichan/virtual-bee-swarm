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
import { scoutAssetResponse, scoutShowHtml } from "./scout-show";
import { elevenAvailable, prewarmAsk, registerScoutAskRoutes, ScoutAsk, warmTts, type ScoutAskDeps } from "./scout-ask";
import {
  SCOUT_BACKGROUND,
  SCOUT_BOOTH_LINES,
  SCOUT_BOOTH_JUDGE_ORDER,
  SCOUT_BOOTH_TUNING,
  SCOUT_LABELS,
  SCOUT_LINES,
  SCOUT_TUNING,
  type ScoutAnswer,
  type ScoutBlockReason,
  type ScoutBox,
  type ScoutClass,
  type ScoutEvent,
  type ScoutFrame,
  type ScoutLabel,
  type ScoutLine,
  type ScoutMode,
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
  booth?: Partial<typeof SCOUT_BOOTH_TUNING>;
  random?: () => number;
}

/** A skipped object is offered again after this long, or once everything else is found. */
export const SKIP_REOFFER_MS = 60_000;
/** At most one idle nudge this often. */
export const IDLE_EVERY_MS = 60_000;
/** Practice mode: the pause after a line before the next chime (demo mode uses tuning.gapMs). */
export const PRACTICE_GAP_MS = 1000;
/** Practice mode: looking away from the last find this long lets it fire again without a slide change. */
export const PRACTICE_LOOK_AWAY_MS = 8000;
/** Frames further apart than this break a steady look (the worker stalled). */
export const FRAME_STALE_MS = 1500;

export function estimateMs(text: string): number {
  return Math.round((text.length / 14.4) * 1000 + 300);
}

const LABEL_SET = new Set<string>(SCOUT_LABELS);
const isLabel = (s: unknown): s is ScoutLabel => typeof s === "string" && LABEL_SET.has(s);

export class ScoutGame {
  readonly tuning: typeof SCOUT_TUNING;
  readonly booth: typeof SCOUT_BOOTH_TUNING;
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
  /** Booth: when the last station line (not a direction cue) finishes; the gap counts from here. */
  private mainUntil = 0;
  private offerDeadline = 0;
  private offerLines: string[] = [];
  private lookingSince = 0;
  private lastIdleAt = -Infinity;
  private noTurn = 0;
  private run: { label: ScoutLabel; since: number; firstAt: number; lastAt: number; score: number } | null = null;
  private lastSlide: { label: ScoutClass | null; at: number; recvAt: number } | null = null;
  private mode: ScoutMode = "demo";
  private lastFind: { label: ScoutLabel; at: number } | null = null;
  /** Practice: since when the frames' top-1 has not been the last find, and whether that lasted PRACTICE_LOOK_AWAY_MS. */
  private awaySince: number | null = null;
  private lookedAway = false;
  /** Booth: the station order, the index of the station Beeswax waits for, the last cue, the last sighting. */
  private order: ScoutLabel[] = [...SCOUT_BOOTH_JUDGE_ORDER];
  private station = 0;
  private lastCueAt = -Infinity;
  private lastSeenNext = 0;
  private blockedKey = "";
  private lastSig = "";

  constructor(deps: ScoutGameDeps) {
    this.d = deps;
    this.lines = new Map(deps.lines.map((l) => [l.id, l]));
    this.now = deps.now ?? Date.now;
    this.tuning = { ...SCOUT_TUNING, ...deps.tuning };
    this.booth = { ...SCOUT_BOOTH_TUNING, ...deps.booth };
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
      practice: this.mode === "practice",
      mode: this.mode,
      order: [...this.order],
      next: this.next(),
    };
  }

  /** Booth: the station Beeswax is waiting for (null outside booth mode or after the last one). */
  next(): ScoutLabel | null {
    return this.mode === "booth" ? (this.order[this.station] ?? null) : null;
  }

  /** POST /scout/practice: screen-test mode. A steady look plays the chime and the fact straight away (no offer),
   *  1 s gap, and the same object fires again once the slide changes or after 8 s of looking away. */
  setPractice(on: boolean): void {
    this.setMode(on ? "practice" : "demo");
  }

  /** POST /scout/mode. Booth restarts at the first station (progress cleared, no intro). */
  setMode(mode: ScoutMode, order?: readonly ScoutLabel[]): void {
    const now = this.now();
    // Booth defaults to the judge order (four stations); all six only when an order is passed.
    if (order && order.length) this.order = [...order];
    else if (mode === "booth") this.order = [...SCOUT_BOOTH_JUDGE_ORDER];
    const was = this.mode;
    this.mode = mode;
    if (mode === "booth" || was === "booth") {
      this.found = [];
      this.skipped.clear();
      this.station = 0;
    }
    if (this.phase === "offering" || this.phase === "telling" || (this.phase === "done" && (mode !== was || mode === "booth"))) this.toLooking(now);
    this.lastSeenNext = now;
    this.lastCueAt = -Infinity;
    this.run = null;
    this.changed();
  }

  /** POST /scout/start, /scout/reset: reset, play the intro, then look. */
  start(): void {
    const now = this.now();
    this.found = [];
    this.skipped.clear();
    this.offer = null;
    this.run = null;
    this.station = 0;
    this.startedAt = now;
    this.busyUntil = now; // cut short whatever was "playing"
    this.mainUntil = now;
    this.lastIdleAt = -Infinity;
    this.lastCueAt = -Infinity;
    this.lastSeenNext = now;
    this.phase = "intro";
    if (this.mode === "booth") this.say(SCOUT_BOOTH_LINES.intro);
    else for (const id of SCOUT_LINES.intro) this.say(id);
    this.changed();
  }

  frame(f: ScoutFrame): void {
    const now = this.now();
    this.tick();
    const top = Array.isArray(f.top) ? f.top : [];
    const best = top[0];
    this.current = best ? { label: best.label, score: best.score } : null;
    this.emit({ type: "frame", frame: f });
    this.trackLookAway(best?.label ?? null, now);
    if (this.mode === "booth") return this.boothFrame(f, top, now);
    const second = top[1]?.score ?? 0;
    const byClass = !!best && isLabel(best.label) && best.score >= this.tuning.minScore && best.score - second >= this.tuning.minMargin;
    // The winner: the clear top-1, else the most central AprilTag (a marker box is certain whatever the scores say).
    const markers = (Array.isArray(f.boxes) ? f.boxes : []).filter((b) => b && b.via === "marker" && isLabel(b.label));
    const tag = markers.length ? markers.reduce((a, b) => (centerDist(b) < centerDist(a) ? b : a)) : null;
    const win: { label: ScoutLabel; score: number } | null =
      byClass && best ? { label: best.label as ScoutLabel, score: best.score } : tag ? { label: tag.label as ScoutLabel, score: Math.max(tag.score, f.scores?.[tag.label] ?? 0) } : null;
    if (this.phase !== "looking") {
      this.run = null;
      // Seen clearly while Beeswax was busy (intro / offer / fact): tell the dashboard, once per busy stretch.
      if (win && win.label !== this.offer) this.blocked(win.label, "busy", win.score, `${this.phase}:${this.busyUntil}`);
      return;
    }
    if (!win) {
      this.run = null;
      return;
    }
    const lookMs = this.extendRun(win.label, win.score, f, now);
    if (lookMs < this.steadyFor(markers, win.label)) return;
    const reason: ScoutBlockReason | null = !this.eligible(win.label)
      ? this.found.includes(win.label) ? "already_found" : "skipped"
      : now < this.busyUntil + this.gapMs() ? "cooldown" : null;
    if (reason) this.blocked(win.label, reason, win.score, String(this.run!.since));
    else this.foundIt(win.label, win.score, lookMs, now - this.run!.firstAt);
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

  /** A short button press (or POST /scout/yes): "yes" during an offer, else nothing. */
  button(at?: number, text = "(button)"): boolean {
    this.tick();
    if (this.phase !== "offering") return false;
    this.emit({ type: "heard", text, answer: "yes", at: this.now() });
    this.answer("yes", at);
    return true;
  }

  /** POST /scout/force: found right now (no dwell, no gap) for `label`, default booth's next station. */
  force(label?: ScoutLabel | null): boolean {
    this.tick();
    const l = label ?? this.next() ?? (this.current && isLabel(this.current.label) ? this.current.label : null);
    if (!l) return false;
    if (this.mode === "booth") {
      const i = this.order.indexOf(l);
      if (i < 0) return false;
      this.station = i;
    }
    if (this.phase === "offering" && this.offer === l) return false;
    this.foundIt(l, 1, 0, 0);
    return true;
  }

  /** POST /scout/next (booth): skip the current station. */
  skipStation(): boolean {
    this.tick();
    const l = this.next();
    if (!l) return false;
    const now = this.now();
    if (!this.found.includes(l)) this.skipped.set(l, now);
    this.station++;
    this.toLooking(now);
    if (!this.next()) this.phase = "done";
    this.lastSeenNext = now;
    this.changed();
    return true;
  }

  slide(label: ScoutClass | null, at: number): void {
    this.lastSlide = { label, at, recvAt: this.now() };
    this.emit({ type: "slide", label, at });
  }

  /** Time-driven transitions: lines ending, offer timeout, idle / lost nudges. Safe to call any time. */
  tick(): void {
    const now = this.now();
    const booth = this.mode === "booth";
    if ((this.phase === "intro" || this.phase === "telling") && now >= this.busyUntil) {
      this.toLooking(now);
    } else if (this.phase === "offering" && now >= this.offerDeadline) {
      if (booth) {
        // Booth: silence counts as yes.
        this.emit({ type: "heard", text: "(silence)", answer: "yes", at: now });
        this.say(SCOUT_BOOTH_LINES.silenceYes);
        this.answer("yes");
      } else this.decline("silence");
    } else if (booth) {
      const next = this.next();
      if (
        this.phase === "looking" &&
        next &&
        this.startedAt !== null &&
        now >= this.busyUntil + this.booth.gapMs &&
        now - Math.max(this.lastSeenNext, this.lookingSince, this.busyUntil) >= this.booth.lostMs &&
        now - this.lastCueAt >= this.booth.cueEveryMs
      ) {
        this.lastCueAt = now;
        this.lastSeenNext = now;
        this.sayIf(SCOUT_BOOTH_LINES.dir.lost);
      }
    } else if (
      this.phase === "looking" &&
      this.startedAt !== null &&
      now >= this.busyUntil + this.gapMs() &&
      now - Math.max(this.lookingSince, this.busyUntil) >= this.tuning.idleMs &&
      now - this.lastIdleAt >= IDLE_EVERY_MS
    ) {
      this.lastIdleAt = now;
      this.say(SCOUT_LINES.idle);
    }
    this.changed();
  }

  // ------------------------------------------------------------ internals

  /** Booth: only the expected-next station can trigger; its most central box drives the direction cues. */
  private boothFrame(f: ScoutFrame, top: ScoutFrame["top"], now: number): void {
    const e = this.next();
    if (!e || this.phase !== "looking") {
      this.run = null;
      return;
    }
    const t = this.tuning;
    const boxes = (Array.isArray(f.boxes) ? f.boxes : []).filter((b) => b && b.label === e);
    const box = boxes.length ? boxes.reduce((a, b) => (centerDist(b) < centerDist(a) ? b : a)) : null;
    const eScore = Math.max(top.find((x) => x.label === e)?.score ?? 0, f.scores?.[e] ?? 0);
    const topScore = top[0]?.score ?? 0;
    // Neighbours are in frame at the booth, so the expected object needs no margin, just to be (nearly) the best.
    const byClass = eScore >= t.minScore && eScore >= topScore - t.minMargin;
    const byBox = !!box && (box.via === "marker" || box.score >= t.minScore);
    if (box || byClass) this.lastSeenNext = now;
    const off = box ? this.cueFor(box) : null;

    // Another station seen clearly: say so on the dashboard (once per looking stretch).
    const best = top[0];
    if (best && isLabel(best.label) && best.label !== e && best.score >= t.minScore && best.score - (top[1]?.score ?? 0) >= t.minMargin) {
      this.blocked(best.label, "not_next", best.score, String(this.lookingSince));
    }

    if (off) {
      // Visible but off-center / small / high: guide the wearer instead of triggering on the frame edge.
      this.run = null;
      if (now - this.lastCueAt >= this.booth.cueEveryMs && now >= this.busyUntil + this.booth.gapMs) {
        this.lastCueAt = now;
        this.sayIf(SCOUT_BOOTH_LINES.dir[off]);
      }
      return;
    }
    if (!(byBox || byClass)) {
      this.run = null;
      return;
    }
    const score = byBox && box ? Math.max(box.score, eScore) : eScore;
    const lookMs = this.extendRun(e, score, f, now);
    if (lookMs < this.steadyFor(boxes, e)) return;
    if (now < this.mainUntil + this.booth.gapMs) return this.blocked(e, "cooldown", score, String(this.run!.since));
    this.foundIt(e, score, lookMs, now - this.run!.firstAt);
  }

  /** How long a look must hold: markerSteadyMs when an AprilTag box for `label` backs this frame, else steadyMs. */
  private steadyFor(boxes: readonly ScoutBox[], label: ScoutLabel): number {
    return boxes.some((b) => b.via === "marker" && b.label === label) ? this.tuning.markerSteadyMs : this.tuning.steadyMs;
  }

  /** Which direction cue a box needs, or null when it is centered and big enough. */
  private cueFor(b: ScoutBox): "left" | "right" | "closer" | "up" | null {
    const cx = b.x + b.w / 2;
    const cy = b.y + b.h / 2;
    if (cy < this.booth.topY) return "up"; // the camera aims ~15 deg below the gaze: top edge = lift the head
    if (cx < this.booth.leftX) return "left";
    if (cx > this.booth.rightX) return "right";
    if (b.w < this.booth.minW) return "closer";
    return null;
  }

  private extendRun(label: ScoutLabel, score: number, f: ScoutFrame, now: number): number {
    const at = typeof f.at === "number" && Number.isFinite(f.at) ? f.at : now;
    if (!this.run || this.run.label !== label || now - this.run.lastAt > FRAME_STALE_MS) {
      this.run = { label, since: now, firstAt: at, lastAt: now, score };
    } else {
      this.run.lastAt = now;
      this.run.score = score;
    }
    return now - this.run.since;
  }

  private blocked(label: ScoutLabel, reason: ScoutBlockReason, score: number, episode: string): void {
    const key = `${reason}:${label}:${episode}`;
    if (key === this.blockedKey) return;
    this.blockedKey = key;
    this.emit({ type: "blocked", label, reason, score, at: this.now() });
  }

  private eligible(label: ScoutLabel): boolean {
    if (this.mode === "practice") {
      // Practice: anything goes, except repeating the last find before the slide has changed.
      const last = this.lastFind;
      return !(last && last.label === label && !this.lookedAway && (!this.lastSlide || this.lastSlide.recvAt <= last.at));
    }
    if (this.found.includes(label)) return false;
    const skippedAt = this.skipped.get(label);
    if (skippedAt === undefined) return true;
    if (this.now() - skippedAt >= SKIP_REOFFER_MS) return true;
    return SCOUT_LABELS.every((l) => l === label || this.found.includes(l));
  }

  private gapMs(): number {
    return this.mode === "practice" ? PRACTICE_GAP_MS : this.tuning.gapMs;
  }

  /** Practice: latch "looked away" once the last find hasn't been top-1 for PRACTICE_LOOK_AWAY_MS (props, no slides). */
  private trackLookAway(top: ScoutClass | null, now: number): void {
    const last = this.lastFind;
    if (!last || top === null) return;
    if (top !== last.label) {
      this.awaySince ??= now;
      return;
    }
    if (this.awaySince !== null && now - this.awaySince >= PRACTICE_LOOK_AWAY_MS) this.lookedAway = true;
    this.awaySince = null;
  }

  private foundIt(label: ScoutLabel, score: number, lookMs: number, frameMs: number): void {
    const now = this.now();
    this.run = null;
    this.offer = label;
    this.lastFind = { label, at: now };
    this.awaySince = null;
    this.lookedAway = false;
    this.phase = this.mode === "practice" ? "telling" : "offering";
    this.emit({ type: "found", label, score, at: now, look_ms: Math.round(lookMs) });
    if (this.lastSlide && this.lastSlide.label === label) this.latency("slide_to_found", now - this.lastSlide.at);
    this.latency("frame_to_found", frameMs);
    if (this.mode === "booth") {
      // Booth: one line asks ("Bee fact found! Would you like to learn more?"); silence = yes after 4 s.
      this.offerLines = [SCOUT_BOOTH_LINES.chimeAsk];
      this.say(SCOUT_BOOTH_LINES.chimeAsk);
      this.latency("found_to_say", this.now() - now);
      this.offerDeadline = this.busyUntil + this.booth.offerTimeoutMs;
      this.changed();
      return;
    }
    this.say(SCOUT_LINES.chime);
    if (this.mode === "practice") {
      // Practice: no hook, no offer; the fact straight away. found/progress are left alone.
      this.sayFact(label);
      this.latency("found_to_say", this.now() - now);
      this.changed();
      return;
    }
    this.offerLines = [SCOUT_LINES.chime, SCOUT_LINES.hook(label)];
    this.say(SCOUT_LINES.hook(label));
    this.latency("found_to_say", this.now() - now);
    this.offerDeadline = this.busyUntil + this.tuning.offerTimeoutMs;
    this.changed();
  }

  private answer(answer: ScoutAnswer, at?: number): void {
    const label = this.offer;
    if (!label) return;
    if (this.mode === "booth") return this.boothAnswer(label, answer, at);
    if (answer === "yes") {
      const now = this.now();
      this.skipped.delete(label);
      const repeat = this.found.includes(label); // a second find of the same object
      if (!repeat) this.found.push(label);
      this.phase = "telling";
      this.sayFact(label);
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

  /** Booth: yes -> fact; no / rude -> a short reply. Either way, then the direction to the next station. */
  private boothAnswer(label: ScoutLabel, answer: ScoutAnswer, at?: number): void {
    const now = this.now();
    if (answer === "yes") {
      this.skipped.delete(label);
      if (!this.found.includes(label)) this.found.push(label);
      this.sayFact(label);
      const heardAt = typeof at === "number" && Number.isFinite(at) && Math.abs(now - at) < 30_000 ? Math.min(at, now) : now;
      this.latency("answer_to_fact", this.now() - heardAt);
    } else {
      if (!this.found.includes(label)) this.skipped.set(label, now);
      this.say(answer === "rude" ? SCOUT_BOOTH_LINES.rude : SCOUT_BOOTH_LINES.no);
    }
    const i = this.order.indexOf(label);
    this.station = (i >= 0 ? i : this.station) + 1;
    const next = this.next();
    if (next) {
      this.sayIf(SCOUT_BOOTH_LINES.next(next));
      this.phase = "telling";
    } else {
      this.sayIf(SCOUT_BOOTH_LINES.done);
      this.phase = "done";
      this.offer = null;
    }
    this.lastSeenNext = now;
    this.changed();
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

  /** The mic heard Beeswax's own offer lines (3+ words, all inside one line). */
  private isEcho(text: string): boolean {
    const t = normText(text);
    if (t.split(" ").length < 3 || !this.offer) return false;
    return this.offerLines.some((id) => normText(this.lines.get(id)?.text ?? "").includes(t));
  }

  /** A fact is one or two lines: scout_fact_<label>, then scout_fact_<label>_2 when it exists. */
  private sayFact(label: ScoutLabel): void {
    this.say(SCOUT_LINES.fact(label));
    this.sayIf(`${SCOUT_LINES.fact(label)}_2`);
  }

  /** Optional lines (booth directions): skipped when missing or blank. */
  private sayIf(id: string): void {
    if (this.lines.get(id)?.text) this.say(id);
  }

  private say(id: string): void {
    const line = this.lines.get(id);
    const caption = line?.text ?? "";
    if (!line) log(`scout: no line ${id} in the scout lines files`);
    const now = this.now();
    const ms = this.d.durationMs?.(id) ?? estimateMs(caption);
    this.busyUntil = Math.max(now, this.busyUntil) + ms;
    if (!id.startsWith("scout_dir_")) this.mainUntil = this.busyUntil;
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
    return JSON.stringify([this.phase, this.found, [...this.skipped.keys()], this.offer, this.startedAt, this.mode, this.order, this.station]);
  }

  private changed(): void {
    const s = this.sig();
    if (s === this.lastSig) return;
    this.lastSig = s;
    this.emit({ type: "state", state: this.state() });
  }
}

const centerDist = (b: ScoutBox) => Math.hypot(b.x + b.w / 2 - 0.5, b.y + b.h / 2 - 0.5);

// ---------------------------------------------------------------- files

const LINES_DIR = fileURLToPath(new URL("../lines/", import.meta.url));
const audioDir = () => process.env.SCOUT_AUDIO_DIR || join(LINES_DIR, "scout_audio");

/** scout_lines.json plus scout_lines_booth.json (booth lines; it wins on an id clash). With `path`: that file only. */
export function loadScoutLines(path?: string): ScoutLine[] {
  if (!path) {
    const byId = new Map<string, ScoutLine>();
    for (const f of ["scout_lines.json", "scout_lines_booth.json"]) for (const l of loadScoutLines(join(LINES_DIR, f))) byId.set(l.id, l);
    return [...byId.values()];
  }
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

/** A booth order: distinct scout labels, at least one; null when invalid. */
export function parseOrder(raw: unknown): ScoutLabel[] | null {
  if (!Array.isArray(raw)) return null;
  const o = raw.map((x) => (typeof x === "string" ? x.trim() : x));
  if (!o.length || !o.every(isLabel) || new Set(o).size !== o.length) return null;
  return o as ScoutLabel[];
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
  /** Ask Beeswax (scout-ask.ts): false = off; fakes for tests. On by default when ElevenLabs is configured
   *  (SCOUT_ASK=1 forces it on without, SCOUT_ASK=0 off). */
  ask?: false | Partial<Pick<ScoutAskDeps, "llm" | "tts" | "renderSentence" | "mode">>;
}

const FRAME_EVENT_MS = 200; // ~5 a second on SSE

export function registerScoutRoutes(app: Hono, ctx: ScoutRouteCtx): ScoutGame {
  const listeners = new Set<(e: ScoutEvent) => void>();
  let lastFrameEvent = 0;
  let ask: ScoutAsk | null = null;
  const fanout = (e: ScoutEvent) => {
    ask?.event(e);
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

  // Ask Beeswax: questions in /scout/utterance are answered live in Beeswax's voice (scout-ask.ts).
  const askOn = ctx.ask !== false && process.env.SCOUT_ASK !== "0" && (!!ctx.ask || elevenAvailable() || process.env.SCOUT_ASK === "1");
  if (askOn) {
    ask = new ScoutAsk({
      ...(ctx.ask || {}),
      game,
      base: ctx.base,
      event: (e) => {
        for (const l of listeners) l(e);
        if (e.type === "say") log(`scout: say ${JSON.stringify(e).slice(0, 160)}`);
      },
      say: (s) => ctx.bus.broadcast({ type: "say", channel: "ear", line_id: null, tts_id: s.line_id, audio_url: s.audio_url, caption: s.caption }, ["glasses"]),
    });
    if (!ctx.ask) {
      // warm Claude's connection and a spare ElevenLabs websocket now, and again whenever a card is found
      warmTts();
      void prewarmAsk();
      let warmedAt = Date.now();
      listeners.add((e) => {
        if (e.type !== "found") return;
        warmTts();
        if (Date.now() - warmedAt > 60_000) (warmedAt = Date.now()), void prewarmAsk();
      });
    }
  }

  // Startup mode survives Hive restarts: SCOUT_MODE=demo|practice|booth (SCOUT_PRACTICE=1 = practice),
  // SCOUT_ORDER=native_flower,honey_jar,... the booth order. POST /scout/mode and /scout/practice still switch.
  const envOrder = parseOrder(process.env.SCOUT_ORDER?.split(","));
  const envMode = process.env.SCOUT_MODE;
  if (envMode === "demo" || envMode === "practice" || envMode === "booth") game.setMode(envMode, envOrder ?? undefined);
  else if (process.env.SCOUT_PRACTICE === "1") game.setPractice(true);
  else if (envOrder) game.setMode("demo", envOrder);

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
    if (c.req.method === "OPTIONS" || c.req.path.startsWith("/scout/audio/") || c.req.path.startsWith("/scout/ask/") || c.req.path.startsWith("/scout/assets/")) return next(); // mp3s + artwork: public, no token (like /audio)
    if (!ctx.authed(c)) {
      return c.req.path === "/scout/slides" || c.req.path === "/scout" || c.req.path === "/scout/show" ? c.text("Add ?token=<HIVE_TOKEN> to the URL.", 401) : c.json({ error: "bad token" }, 401);
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
  app.get("/scout/show", (c) => {
    if (!ctx.authed(c)) return c.text("Add ?token=<HIVE_TOKEN> to the URL.", 401);
    return c.html(scoutShowHtml());
  });
  app.get("/scout/assets/:file", (c) => scoutAssetResponse(c.req.param("file")));
  let lastFrameAt: number | null = null; // last POST /scout/frame from the vision worker (watchdog)
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
    let boxes: ScoutBox[] | undefined;
    if (Array.isArray(body.boxes)) {
      const n01 = (v: unknown) => typeof v === "number" && Number.isFinite(v) && v >= -0.5 && v <= 1.5;
      boxes = (body.boxes as Record<string, unknown>[])
        .filter((b) => !!b && isClass(b.label) && typeof b.score === "number" && Number.isFinite(b.score) && n01(b.x) && n01(b.y) && n01(b.w) && n01(b.h))
        .slice(0, 20)
        .map((b) => {
          const out: ScoutBox = { label: b.label as ScoutClass, score: b.score as number, x: b.x as number, y: b.y as number, w: b.w as number, h: b.h as number };
          if (b.via === "detector" || b.via === "marker" || b.via === "refs") out.via = b.via;
          return out;
        });
    }
    const now = Date.now();
    lastFrameAt = now;
    game.frame({
      at: num(body.at, now),
      frame_ms: num(body.frame_ms, 0),
      fps: num(body.fps, 0),
      top: clean,
      ...(scores ? { scores } : {}),
      ...(boxes ? { boxes } : {}),
      source: typeof body.source === "string" ? body.source.slice(0, 200) : "",
    });
    return c.json({ ok: true, phase: game.state().phase });
  });

  app.post("/scout/utterance", async (c) => {
    const body = await readJson(c);
    const text = typeof body?.text === "string" ? body.text.trim() : "";
    if (!text) return c.json({ error: "text required" }, 400);
    // A question goes to Ask Beeswax (it cuts Beeswax off and answers); short yes/no and the rest stay with the game.
    if (ask && ask.route(text, classifyAnswer(text)) === "ask") {
      fanout({ type: "heard", text: text.slice(0, 300), answer: "other", at: Date.now() });
      void ask.ask(text.slice(0, 300));
      return c.json({ ok: true, answer: "other", routed: "ask", phase: game.state().phase });
    }
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

  app.post("/scout/watchdog", (c) => c.json({ frame_age_ms: lastFrameAt === null ? null : Date.now() - lastFrameAt }));

  app.post("/scout/practice", async (c) => {
    const body = await readJson(c);
    if (typeof body?.on !== "boolean") return c.json({ error: "on (boolean) required" }, 400);
    game.setPractice(body.on);
    return c.json(game.state());
  });

  // ---- booth mode and operator keys (scout-protocol.ts: ScoutModeRequest, ScoutForce)
  app.post("/scout/mode", async (c) => {
    const body = await readJson(c);
    const mode = body?.mode;
    if (mode !== "demo" && mode !== "practice" && mode !== "booth") return c.json({ error: "mode must be demo, practice or booth" }, 400);
    let order: ScoutLabel[] | undefined;
    if (body?.order !== undefined) {
      const o = parseOrder(body.order);
      if (!o) return c.json({ error: "order must be distinct scout labels" }, 400);
      order = o;
    }
    game.setMode(mode, order);
    return c.json(game.state());
  });

  app.post("/scout/force", async (c) => {
    const body = (await readJson(c)) ?? {};
    if (body.label !== undefined && body.label !== null && !isLabel(body.label)) return c.json({ error: "label must be a scout label" }, 400);
    const ok = game.force((body.label as ScoutLabel | null | undefined) ?? null);
    return c.json({ ok, ...game.state() }, ok ? 200 : 409);
  });

  app.post("/scout/yes", (c) => {
    const ok = game.button(undefined, "(operator yes)");
    return c.json({ ok, ...game.state() }, ok ? 200 : 409);
  });

  app.post("/scout/next", (c) => {
    const ok = game.skipStation();
    return c.json({ ok, ...game.state() }, ok ? 200 : 409);
  });

  app.post("/scout/reset", (c) => {
    game.start();
    return c.json({ ok: true, ...game.state() });
  });

  app.post("/scout/slide", async (c) => {
    const body = await readJson(c);
    if (!body || !(body.label === null || isClass(body.label))) return c.json({ error: "label required (a class or null)" }, 400);
    game.slide(body.label as ScoutClass | null, num(body.at, Date.now()));
    return c.json({ ok: true });
  });

  if (ask) registerScoutAskRoutes(app, ask); // after the token middleware: /scout/ask/<id>.mp3 is public, the rest needs it

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
