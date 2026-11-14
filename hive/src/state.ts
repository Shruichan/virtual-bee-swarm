// Group state: one judging group at a time. The Quest owns the beat; the Hive mirrors it.
import {
  BEATS,
  CORRECT_CHOICE,
  type BeatName,
  type Choice,
  type Item,
  type Mode,
  type Scene,
  type Score,
  type Station,
  type WhatIfTopic,
} from "./protocol";

export interface Memory {
  // v3 game
  choices: Partial<Record<Station, Choice[]>>; // every choice made at each station, in order
  wrong: Array<{ station: Station; choice: Choice }>;
  solved: Station[]; // stations whose correct choice was made, in order
  whatifs: WhatIfTopic[];
  hornet_done: boolean;
  hornet_overshoot: boolean;
  heard: string[]; // recent transcripts (router context), newest last
  // glasses real-prop mode (later)
  scanned: Item[];
  first_item?: Item;
  pleas: string[];
}

export interface ScanInFlight {
  scan_id: string;
  pressed_at: number; // glasses clock (falls back to server clock)
  started_at: number; // server clock
}

export interface GroupState {
  group_id: string;
  beat: number;
  mode: Mode;
  memory: Memory;
  scan_in_flight: ScanInFlight | null;
  /** The future window: what it shows now, and the last scene that wasn't a what-if (undo goes back there). */
  scene: Scene;
  base_scene: Scene;
  /** Score clock: when the hook beat started, and hook -> escape once the hive is saved. */
  clock_start: number | null;
  saved_ms: number | null;
  /** v2.1: the score card, frozen when the escape beat starts (null until then). */
  score: Score | null;
  /** v2.1: the vetted Orbis prompt of the last custom what-if (shown while scene is whatif_custom). */
  custom_prompt: string | null;
}

export function newGroupId(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  const rnd = Math.random().toString(36).slice(2, 5);
  return `g${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}-${rnd}`;
}

function emptyMemory(): Memory {
  return {
    choices: {},
    wrong: [],
    solved: [],
    whatifs: [],
    hornet_done: false,
    hornet_overshoot: false,
    heard: [],
    scanned: [],
    pleas: [],
  };
}

export function createState(): GroupState {
  return {
    group_id: newGroupId(),
    beat: 0,
    mode: "solo",
    memory: emptyMemory(),
    scan_in_flight: null,
    scene: "meadow",
    base_scene: "meadow",
    clock_start: null,
    saved_ms: null,
    score: null,
    custom_prompt: null,
  };
}

/** New group: fresh id, memory, window and clock. Beat and mode are kept (the Quest reports its own beat). */
export function reset(state: GroupState, group_id?: string): GroupState {
  state.group_id = group_id?.trim() || newGroupId();
  state.memory = emptyMemory();
  state.scan_in_flight = null;
  state.scene = "meadow";
  state.base_scene = "meadow";
  state.clock_start = null;
  state.saved_ms = null;
  state.score = null;
  state.custom_prompt = null;
  return state;
}

export function beatName(state: GroupState): BeatName {
  return BEATS[state.beat] ?? "pre";
}

export function isValidBeat(n: unknown): n is number {
  return typeof n === "number" && Number.isInteger(n) && n >= 0 && n < BEATS.length;
}

/** Glasses scan memory. */
export function remember(state: GroupState, item: Item): void {
  const m = state.memory;
  m.scanned.push(item);
  m.first_item ??= item;
}

/** Record a station choice. Returns whether it was correct, and whether it was the first correct one there. */
export function recordChoice(state: GroupState, station: Station, choice: Choice): { correct: boolean; solvedNow: boolean } {
  const m = state.memory;
  (m.choices[station] ??= []).push(choice);
  const correct = CORRECT_CHOICE[station] === choice;
  if (!correct) {
    m.wrong.push({ station, choice });
    return { correct, solvedNow: false };
  }
  const solvedNow = !m.solved.includes(station);
  if (solvedNow) m.solved.push(station);
  return { correct, solvedNow };
}

export function rememberHeard(state: GroupState, text: string, keep = 6): void {
  const h = state.memory.heard;
  h.push(text);
  if (h.length > keep) h.splice(0, h.length - keep);
}

/** "2:47" */
export function formatClock(ms: number): string {
  const s = Math.max(0, Math.round(ms / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

export function scoreText(state: GroupState): string | null {
  return state.saved_ms === null ? null : `Hive saved in ${formatClock(state.saved_ms)}`;
}

/** Wrong choices this group: "Grievances filed: N" on the score card. */
export function grievances(state: GroupState): number {
  return state.memory.wrong.length;
}

/** Stop the score clock (once per group) and freeze the score card. Null if the clock never started. */
export function saveScore(state: GroupState, now = Date.now()): Score | null {
  if (state.score) return state.score;
  if (state.clock_start === null) return null;
  state.saved_ms ??= Math.max(0, now - state.clock_start);
  state.score = {
    type: "score",
    elapsed_s: Math.max(0, Math.round(state.saved_ms / 1000)),
    text: scoreText(state)!,
    grievances: grievances(state),
  };
  return state.score;
}
