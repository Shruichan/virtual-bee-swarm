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
  SCOUT_STOP_ID,
  SCOUT_TEACHER_LINE,
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
  /** Tells the glasses to cut the ear now (interrupt()). */
  stop?: () => void;
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
/** Practice mode: the pause after a line before the next chime (demo mode uses tuning.gapMs). */
export const PRACTICE_GAP_MS = 1000;
/** An answer (beginAnswer) that never calls endAnswer gives up after this long. */
export const ANSWER_MAX_MS = 30_000;
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

  /** Silent until the journey starts: POST /scout/reset (the judge view's "Start your adventure"). */
  private phase: ScoutPhase = "waiting";
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
  private anyQTurn = 0;
  /** Practice: the six-badge finale played this journey. */
  private finaleDone = false;
  private noTurn = 0;
  private run: { label: ScoutLabel; since: number; firstAt: number; lastAt: number; score: number } | null = null;
  private lastSlide: { label: ScoutClass | null; at: number; recvAt: number } | null = null;
  private mode: ScoutMode = "demo";
  /** Ask Beeswax: when the answer began, and when its audio ends (null until endAnswer). */
  private answerStartedAt = 0;
  private answerEndsAt: number | null = null;
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
      glasses_muted: this.glassesMuted,
    };
  }

  /** Booth: the station Beeswax is waiting for (null outside booth mode or after the last one). */
  next(): ScoutLabel | null {
    return this.mode === "booth" ? (this.order[this.station] ?? null) : null;
  }

  /** POST /scout/practice: screen-test mode. A steady look plays the chime and the fact straight away (no offer),
   *  1 s gap. The same card fires again after another card, a slide change, or 20 s + 3 s out of view. */
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

  /** POST /scout/reset (the judge view's "Start your adventure"): reset, play the intro, then look.
   *  (POST /scout/start from the glasses no longer calls this; it only returns the state.) */
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
    this.finaleDone = false;
    this.lastCueAt = -Infinity;
    this.lastSeenNext = now;
    this.lastSlide = null;
    this.answerEndsAt = null;
    this.blockedKey = "";
    this.phase = "intro";
    // Every intro line that exists, in order: scout_intro_1.._3, or scout_booth_intro, _2, _3.
    if (this.mode === "booth") this.saySeries(SCOUT_BOOTH_LINES.intro);
    else for (const id of SCOUT_LINES.introAll) this.sayIf(id);
    this.changed();
  }

  frame(f: ScoutFrame): void {
    const now = this.now();
    this.tick();
    const top = Array.isArray(f.top) ? f.top : [];
    const best = top[0];
    this.current = best ? { label: best.label, score: best.score } : null;
    this.emit({ type: "frame", frame: f });
    if (this.phase === "waiting") {
      this.run = null; // silent until the journey starts (POST /scout/reset)
      return;
    }
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
    if (this.phase === "waiting") return false;
    // A bare force while Beeswax is busy (intro, a fact, an answer) is refused, so a double key press can't skip a
    // station; naming the label forces it anyway.
    if (!label && (this.phase === "intro" || this.phase === "telling" || this.phase === "answering")) return false;
    const l = label ?? this.next() ?? (this.current && isLabel(this.current.label) ? this.current.label : null);
    if (!l || this.found.includes(l)) return false; // no rediscovery, not even forced
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
    if (!l || this.phase === "waiting") return false;
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

  // ------------------------------------------------------------ Ask Beeswax API (branch scout/ask). Keep stable.

  /** An answer starts: phase "answering", frames ignored, any open offer dropped (not counted as skipped).
   *  Does NOT cut audio: call interrupt() first to stop the current line. Returns false only when an answer is
   *  already running (one at a time); the caller should then not speak. */
  beginAnswer(): boolean {
    this.tick();
    if (this.phase === "answering" || this.phase === "waiting") return false;
    this.phase = "answering";
    this.offer = null;
    this.run = null;
    this.answerStartedAt = this.now();
    this.answerEndsAt = null;
    this.changed();
    return true;
  }

  /** The answer is done being sent. `audioMs` = how much of it is still to play in the ear (0 = finished);
   *  the game stays in "answering" until then, then back to looking (the usual gap applies). No-op otherwise. */
  endAnswer(audioMs = 0): void {
    if (this.phase !== "answering") return;
    const now = this.now();
    const end = now + Math.max(0, Number.isFinite(audioMs) ? audioMs : 0);
    this.answerEndsAt = end;
    this.busyUntil = Math.max(this.busyUntil, end);
    this.mainUntil = Math.max(this.mainUntil, end);
    this.tick();
  }

  /** Cut Beeswax off now: busy-until = now, emits {type:"stop"} and tells the glasses to drop the ear's line and queue.
   *  The phase is untouched (telling/intro then end on the next tick; an offer keeps its timeout from now). */
  interrupt(): void {
    const now = this.now();
    this.busyUntil = now;
    this.mainUntil = now;
    if (this.phase === "offering") this.offerDeadline = Math.min(this.offerDeadline, now + this.offerTimeout());
    try {
      this.d.stop?.();
    } catch (err) {
      log("scout: stop failed:", err);
    }
    this.emit({ type: "stop", at: now });
    this.tick();
  }

  /** POST /scout/teacher: "Ask your teacher for another assignment!" (queued after whatever is playing). */
  /** Glasses mute: the routes stop broadcasting `say` to the glasses while this is true (say events, timing and
   *  busy-until are unchanged). Muting sends the glasses stop so the ear goes quiet now. */
  glassesMuted = false;
  setGlassesMuted(muted: boolean): void {
    const was = this.glassesMuted;
    this.glassesMuted = muted;
    if (muted && !was) {
      try {
        this.d.stop?.();
      } catch (err) {
        log("scout: stop failed:", err);
      }
    }
    this.changed();
  }

  /** POST /scout/teacher: "Ask your teacher for another assignment!" (queued after whatever is playing).
   *  False (silent) while waiting for the journey to start. */
  teacher(): boolean {
    this.tick();
    if (this.phase === "waiting") return false;
    this.say(SCOUT_TEACHER_LINE);
    this.changed();
    return true;
  }

  /** Has the journey started (POST /scout/reset)? While waiting the game, Ask Beeswax and the teacher line are silent. */
  isStarted(): boolean {
    return this.phase !== "waiting";
  }

  /** POST /scout/stop (the judge view's "Restart your journey"): cut the ear, clear badges and progress, and wait
   *  silently for the next POST /scout/reset. */
  stopJourney(): void {
    this.interrupt();
    const now = this.now();
    this.found = [];
    this.skipped.clear();
    this.offer = null;
    this.run = null;
    this.station = 0;
    this.startedAt = null;
    this.finaleDone = false;
    this.answerEndsAt = null;
    this.lastSlide = null;
    this.blockedKey = "";
    this.busyUntil = now;
    this.mainUntil = now;
    this.phase = "waiting";
    this.changed();
  }

  /** Time-driven transitions: lines ending, offer timeout, idle / lost nudges. Safe to call any time. */
  tick(): void {
    const now = this.now();
    const booth = this.mode === "booth";
    if (this.phase === "answering") {
      const done = this.answerEndsAt !== null ? now >= this.answerEndsAt && now >= this.busyUntil : now - this.answerStartedAt >= ANSWER_MAX_MS;
      if (done) {
        if (this.answerEndsAt === null) log("scout: answer never ended, back to looking");
        this.answerEndsAt = null;
        this.toLooking(now);
      }
    } else if ((this.phase === "intro" || this.phase === "telling") && now >= this.busyUntil) {
      this.toLooking(now);
    } else if (this.phase === "offering" && now >= this.offerDeadline) {
      this.decline("silence"); // demo only: practice and booth never offer
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
    }
    // (No idle nudge any more: scout_idle is never said.)
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
    // No rediscovery: once a card's fact has played this journey it never fires again (until POST /scout/reset).
    if (this.found.includes(label)) return false;
    if (this.mode === "practice") return true;
    const skippedAt = this.skipped.get(label);
    if (skippedAt === undefined) return true;
    if (this.now() - skippedAt >= SKIP_REOFFER_MS) return true;
    return SCOUT_LABELS.every((l) => l === label || this.found.includes(l));
  }

  private offerTimeout(): number {
    return this.mode === "booth" ? this.booth.offerTimeoutMs : this.tuning.offerTimeoutMs;
  }

  private gapMs(): number {
    return this.mode === "practice" ? PRACTICE_GAP_MS : this.tuning.gapMs;
  }

  private foundIt(label: ScoutLabel, score: number, lookMs: number, frameMs: number): void {
    const now = this.now();
    this.run = null;
    this.offer = label;
    this.phase = this.mode === "demo" ? "offering" : "telling";
    this.emit({ type: "found", label, score, at: now, look_ms: Math.round(lookMs) });
    if (this.lastSlide && this.lastSlide.label === label) this.latency("slide_to_found", now - this.lastSlide.at);
    this.latency("frame_to_found", frameMs);
    this.say(SCOUT_LINES.chime);
    if (this.mode !== "demo") {
      // Practice and booth: no hook, no offer. "Bee fact found!", the fact (+ _2), then "any questions?"
      // (Ask Beeswax takes it from there through beginAnswer/endAnswer).
      this.sayFact(label);
      this.latency("found_to_say", this.now() - now);
      const isNew = !this.found.includes(label); // a badge per distinct card; a rediscovery replays the fact only
      if (isNew) this.found.push(label);
      this.sayAnyQ();
      if (this.mode === "booth") {
        // On to the next station: its direction line, or the finale after the last one.
        const i = this.order.indexOf(label);
        this.station = (i >= 0 ? i : this.station) + 1;
        const next = this.next();
        if (next) this.sayIf(SCOUT_BOOTH_LINES.next(next));
        else {
          this.saySeries(SCOUT_BOOTH_LINES.done);
          this.phase = "done";
          this.offer = null;
        }
        this.lastSeenNext = now;
      } else if (isNew && this.found.length === SCOUT_LABELS.length && !this.finaleDone) {
        // Practice: all six badges, first time this journey: the finale (practice keeps going afterwards).
        this.finaleDone = true;
        this.saySeries(SCOUT_LINES.done);
      }
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
        this.saySeries(SCOUT_LINES.done);
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

  /** The mic heard Beeswax's own offer lines (3+ words, all inside one line). */
  private isEcho(text: string): boolean {
    const t = normText(text);
    if (t.split(" ").length < 3 || !this.offer) return false;
    return this.offerLines.some((id) => normText(this.lines.get(id)?.text ?? "").includes(t));
  }

  /** A fact is one or two lines: scout_fact_<label>, then scout_fact_<label>_2 when it exists. */
  /** `base`, then `base_2`, `base_3`: every one that exists (multi-line intro and finale). */
  private saySeries(base: string): void {
    for (const id of [base, `${base}_2`, `${base}_3`]) this.sayIf(id);
  }

  /** One of scout_any_q_1..3 (taking turns among those that exist), after a fact in practice and booth. */
  private sayAnyQ(): void {
    const ids = SCOUT_LINES.anyQ.filter((id) => this.lines.get(id)?.text);
    if (ids.length) this.say(ids[this.anyQTurn++ % ids.length]!);
  }

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
    return JSON.stringify([this.phase, this.found, [...this.skipped.keys()], this.offer, this.startedAt, this.mode, this.order, this.station, this.glassesMuted]);
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

  /** One line to the glasses' ear, unless the glasses are muted (POST /scout/glasses-mute). */
  const toGlasses = (s: { line_id: string; caption: string; audio_url: string | null }) => {
    if (game.glassesMuted) return;
    ctx.bus.broadcast({ type: "say", channel: "ear", line_id: null, tts_id: s.line_id, audio_url: s.audio_url, caption: s.caption }, ["glasses"]);
  };
  const game: ScoutGame = new ScoutGame({
    lines: loadScoutLines(),
    now: ctx.now,
    event: fanout,
    durationMs: manifestMs,
    audioUrl: (id) => (existsSync(join(audioDir(), `${id}.mp3`)) ? `${ctx.base()}/scout/audio/${id}.mp3` : null),
    // line_id stays null (no embedded clip in the glasses); tts_id carries the scout line id so the glasses can
    // light the LED per line even when audio_url is null. Ear lines never use tts_id otherwise.
    stop: () =>
      ctx.bus.broadcast({ type: "say", channel: "ear", line_id: null, tts_id: SCOUT_STOP_ID, audio_url: null, caption: "" }, ["glasses"]),
    say: (s) => toGlasses(s),
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
      say: (s) => toGlasses(s), // muted -> not sent (its say event still reaches the dashboards)
      // the wake ack (scout_wake_ack) comes from the scout lines files like any other line
      line: (id) => {
        const l = loadScoutLines().find((x) => x.id === id);
        if (!l?.text) return null;
        const has = existsSync(join(audioDir(), `${id}.mp3`));
        return { caption: l.text, audio_url: has ? `${ctx.base()}/scout/audio/${id}.mp3` : null, ms: manifestMs(id) };
      },
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
  if (process.env.SCOUT_GLASSES_MUTED === "1") game.setGlassesMuted(true);
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
    // Wake word only: "Hey Beeswax, <question>" goes to Ask Beeswax (it cuts Beeswax off and answers), a bare
    // "Hey Beeswax" gets the ack and the next utterance is the question; everything else stays with the game
    // (yes / no) and shows on the judge screen as heard.
    let forGame = text;
    if (ask && game.isStarted()) {
      // (while waiting for "Start your adventure", Ask Beeswax stays silent too)
      const r = ask.route(text, classifyAnswer);
      if (r.kind === "ask" || r.kind === "wake") {
        fanout({ type: "heard", text: text.slice(0, 300), answer: "other", at: Date.now(), ...(r.kind === "ask" ? { question: true } : {}) });
        if (r.kind === "wake") ask.wake();
        else void ask.ask(r.question.slice(0, 300));
        return c.json({ ok: true, answer: "other", routed: r.kind, phase: game.state().phase });
      }
      if (r.kind === "game") forGame = r.text || text;
    }
    const answer = game.utterance(forGame, typeof body?.at === "number" ? body.at : undefined);
    return c.json({ ok: true, answer, phase: game.state().phase });
  });

  app.post("/scout/button", async (c) => {
    const body = (await readJson(c)) ?? {};
    const yes = game.button(typeof body.at === "number" ? body.at : undefined);
    return c.json({ ok: true, answer: yes ? "yes" : null, phase: game.state().phase });
  });

  // The glasses POST this when their bus connects. It no longer starts (or restarts) the journey: the judge view's
  // "Start your adventure" does, through POST /scout/reset. Kept so older glasses builds get a clean 200.
  app.post("/scout/start", (c) => c.json({ ok: true, state: game.state() }));

  app.post("/scout/stop", (c) => {
    game.stopJourney();
    return c.json({ ok: true, ...game.state() });
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
    game.interrupt(); // cut whatever is playing, then start over (practice state included)
    game.start();
    return c.json({ ok: true, ...game.state() });
  });

  app.post("/scout/glasses-mute", async (c) => {
    const body = await readJson(c);
    if (typeof body?.muted !== "boolean") return c.json({ error: "muted (boolean) required" }, 400);
    game.setGlassesMuted(body.muted);
    return c.json(game.state());
  });

  app.post("/scout/teacher", (c) => {
    const ok = game.teacher();
    return c.json({ ok, ...game.state() }, ok ? 200 : 409);
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
