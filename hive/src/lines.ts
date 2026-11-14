// Beeswax line catalog (hive/lines/lines.json) and the rules for picking a pre-rendered line.
// Ids: <channel>_verdict_<item> for scan verdicts; ear_* play in the glasses, room_* from Beeswax on the Quest.
import raw from "../lines/lines.json";
import type { Channel, Item, Verdict } from "./protocol";

export interface Line { id: string; channel: "ear" | "room"; text: string; beat?: string; tags?: string[] }

/** The fixed verdict for each prop (and person), so the beeline is deterministic whatever the model says. */
export const PROP_VERDICT: Partial<Record<Item, Verdict>> = {
  red_rose: "bad",
  purple_bee_flower: "good",
  clover_or_rosemary: "good",
  water_dish: "good",
  bee_hotel: "good",
  spray_bottle: "dangerous",
  plastic_flower: "bad",
  person: "roast",
};

function load(data: unknown): Record<string, Line> {
  const out: Record<string, Line> = {};
  if (!Array.isArray(data)) {
    console.error("[lines] lines.json must be an array");
    return out;
  }
  for (const e of data) {
    const { id, channel, text, beat, tags } = (e ?? {}) as Record<string, unknown>;
    if (typeof id !== "string" || (channel !== "ear" && channel !== "room") || typeof text !== "string" || !text.trim()) {
      console.warn("[lines] skipping bad entry", JSON.stringify(e));
      continue;
    }
    if (out[id]) {
      console.warn(`[lines] duplicate id ${id}, keeping the first`);
      continue;
    }
    const line: Line = { id, channel, text };
    if (typeof beat === "string") line.beat = beat;
    if (Array.isArray(tags)) line.tags = tags.filter((t): t is string => typeof t === "string");
    out[id] = line;
  }
  return out;
}

export const LINES: Record<string, Line> = load(raw);

/** Pre-rendered verdict line id for this item on this channel, or null for off-list items (use a live line). */
export function verdictLine(item: Item, verdict: Verdict, channel: Channel): string | null {
  for (const id of [`${channel}_verdict_${item}_${verdict}`, `${channel}_verdict_${item}`]) {
    if (LINES[id]) return id;
  }
  return null;
}

/** Caption text for a line id ("" if unknown). */
export function lineText(id: string): string {
  return LINES[id]?.text ?? "";
}

const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9]+/g, " ").trim();

/** A pre-rendered line whose text matches (e.g. the director's canned fallback), so no live TTS is needed. */
export function lineIdForText(text: string, channel: Channel): string | null {
  const want = norm(text);
  if (!want) return null;
  for (const line of Object.values(LINES)) {
    if (line.channel === channel && norm(line.text) === want) return line.id;
  }
  return null;
}

/** Splashdown callback, keyed by what this group did. Accepts the state.ts Memory shape. */
export function callbackLine(memory: {
  scanned: readonly Item[];
  first_item?: Item;
  spray_grabbed?: boolean;
  hornet_overshoot?: boolean;
}): string {
  if (memory.hornet_overshoot) return "ear_callback_overshoot";
  if (memory.spray_grabbed || memory.scanned.includes("spray_bottle")) return "ear_callback_spray";
  if ((memory.first_item ?? memory.scanned[0]) === "red_rose") return "ear_callback_rose";
  return "ear_callback_default";
}

export function wordCount(text: string): number {
  return text.trim().split(/\s+/).filter(Boolean).length;
}
