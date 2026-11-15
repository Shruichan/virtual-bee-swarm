// Beeswax line catalog (hive/lines/lines.json), the station outcome table (stations.json) and the
// future-window scenes (orbis_scenes.json), plus the rules for picking a pre-rendered line.
// Ids: room_* play from Beeswax on the Quest, ear_* in the glasses.
import raw from "../lines/lines.json";
import rawStations from "../lines/stations.json";
import rawScenes from "../lines/orbis_scenes.json";
import {
  CHOICES,
  FX,
  SCENES,
  STATIONS,
  WHATIF_TOPICS,
  type Channel,
  type Choice,
  type FxName,
  type Item,
  type Scene,
  type Station,
  type Verdict,
  type WhatIfTopic,
} from "./protocol";

export interface Line { id: string; channel: "ear" | "room"; text: string; beat?: string; tags?: string[] }

/** The fixed verdict for each glasses-scan prop (and person), so the outcome is deterministic whatever the model says. */
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

const isObj = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);

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

// ---------------------------------------------------------------- stations.json

export interface StationOutcome { correct: boolean; line_id: string; scene: Scene; fx?: FxName }
export interface StationEntry { intro_line: string | null; choices: Partial<Record<Choice, StationOutcome>> }

function loadStations(data: unknown): Partial<Record<Station, StationEntry>> {
  const out: Partial<Record<Station, StationEntry>> = {};
  if (!isObj(data)) {
    console.error("[lines] stations.json must be an object");
    return out;
  }
  for (const station of STATIONS) {
    const s = data[station];
    if (!isObj(s) || !isObj(s.choices)) {
      console.warn(`[lines] stations.json: no entry for ${station}`);
      continue;
    }
    const entry: StationEntry = { intro_line: typeof s.intro_line === "string" ? s.intro_line : null, choices: {} };
    for (const choice of CHOICES[station] as readonly Choice[]) {
      const c = s.choices[choice];
      if (!isObj(c) || typeof c.line_id !== "string" || !SCENES.includes(c.scene as Scene)) {
        console.warn(`[lines] stations.json: bad or missing ${station}.${choice}`);
        continue;
      }
      const outcome: StationOutcome = { correct: c.correct === true, line_id: c.line_id, scene: c.scene as Scene };
      if (c.fx !== undefined) {
        if (FX.includes(c.fx as FxName)) outcome.fx = c.fx as FxName;
        else console.warn(`[lines] stations.json: unknown fx ${String(c.fx)} on ${station}.${choice}`);
      }
      entry.choices[choice] = outcome;
    }
    out[station] = entry;
  }
  return out;
}

export const STATION_TABLE = loadStations(rawStations);

// ---------------------------------------------------------------- orbis_scenes.json

export interface SceneEntry { prompt: string; seed: number; clip: string }

function loadScenes(data: unknown): Partial<Record<Scene, SceneEntry>> {
  const out: Partial<Record<Scene, SceneEntry>> = {};
  if (!isObj(data)) {
    console.error("[lines] orbis_scenes.json must be an object");
    return out;
  }
  for (const scene of SCENES) {
    const s = data[scene];
    if (!isObj(s) || typeof s.prompt !== "string" || !s.prompt.trim()) {
      console.warn(`[lines] orbis_scenes.json: no prompt for ${scene}`);
      continue;
    }
    out[scene] = {
      prompt: s.prompt.trim(),
      seed: typeof s.seed === "number" && Number.isInteger(s.seed) && s.seed >= 0 ? s.seed : 20000,
      clip: typeof s.clip === "string" && s.clip ? s.clip : `${scene}.mp4`,
    };
  }
  return out;
}

export const SCENE_TABLE = loadScenes(rawScenes);

// ---------------------------------------------------------------- picking lines

/** Canned acknowledgements for voice the router didn't understand (and "ack" intents). */
export const ACK_LINES: string[] = Object.values(LINES)
  .filter((l) => l.channel === "room" && l.tags?.includes("ack"))
  .map((l) => l.id);

/** The room line Beeswax says over the window's what-if morph. */
export function whatIfLine(topic: WhatIfTopic): string | null {
  const id = `room_whatif_${topic}`;
  return WHATIF_TOPICS.includes(topic) && LINES[id] ? id : null;
}

/** "Put it out" relief, "back to how it was", and "undo what?". */
export const UNDO_LINES = { fire: "room_whatif_out", back: "room_whatif_back", nothing: "room_whatif_nothing" } as const;

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

export function wordCount(text: string): number {
  return text.trim().split(/\s+/).filter(Boolean).length;
}
