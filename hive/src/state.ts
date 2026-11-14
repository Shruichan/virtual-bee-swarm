// Group state: one judging group at a time. The Quest owns the beat; the Hive mirrors it.
import { BEATS, type BeatName, type Item, type Mode } from "./protocol";

export interface Memory {
  scanned: Item[];
  first_item?: Item;
  spray_grabbed: boolean;
  hornet_overshoot: boolean;
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
}

export function newGroupId(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  const rnd = Math.random().toString(36).slice(2, 5);
  return `g${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}-${rnd}`;
}

function emptyMemory(): Memory {
  return { scanned: [], spray_grabbed: false, hornet_overshoot: false, pleas: [] };
}

export function createState(): GroupState {
  return { group_id: newGroupId(), beat: 0, mode: "solo", memory: emptyMemory(), scan_in_flight: null };
}

/** New group: fresh id and memory. Beat and mode are kept (the Quest reports its own beat). */
export function reset(state: GroupState, group_id?: string): GroupState {
  state.group_id = group_id?.trim() || newGroupId();
  state.memory = emptyMemory();
  state.scan_in_flight = null;
  return state;
}

export function beatName(state: GroupState): BeatName {
  return BEATS[state.beat] ?? "pre";
}

export function isValidBeat(n: unknown): n is number {
  return typeof n === "number" && Number.isInteger(n) && n >= 0 && n < BEATS.length;
}

export function remember(state: GroupState, item: Item): void {
  const m = state.memory;
  m.scanned.push(item);
  m.first_item ??= item;
  if (item === "spray_bottle") m.spray_grabbed = true;
}
