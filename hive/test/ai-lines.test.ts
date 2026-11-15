import { describe, expect, test } from "bun:test";
import raw from "../lines/lines.json";
import rawScenes from "../lines/orbis_scenes.json";
import rawStations from "../lines/stations.json";
import {
  ACK_LINES,
  LINES,
  PROP_VERDICT,
  SCENE_TABLE,
  STATION_TABLE,
  UNDO_LINES,
  lineIdForText,
  lineText,
  verdictLine,
  whatIfLine,
  wordCount,
} from "../src/lines";
import { BEATS, CHOICES, CORRECT_CHOICE, FX, SCENES, STATIONS, WHATIF_TOPICS, type Item } from "../src/protocol";

type RawLine = { id: string; channel: string; text: string; beat?: string; tags?: string[] };
const entries = raw as RawLine[];

describe("lines.json", () => {
  test("is a list of well-formed lines with unique ids", () => {
    expect(Array.isArray(raw)).toBe(true);
    const ids = new Set<string>();
    for (const l of entries) {
      expect(typeof l.id).toBe("string");
      expect(ids.has(l.id)).toBe(false);
      ids.add(l.id);
      expect(["ear", "room"]).toContain(l.channel);
      expect(l.id.startsWith(`${l.channel}_`)).toBe(true);
      expect(l.text.trim().length).toBeGreaterThan(0);
      if (l.beat !== undefined) expect({ id: l.id, beat: (BEATS as readonly string[]).includes(l.beat) }).toEqual({ id: l.id, beat: true });
      if (l.tags !== undefined) for (const t of l.tags) expect(typeof t).toBe("string");
    }
    expect(Object.keys(LINES).length).toBe(entries.length);
  });

  test("has 55-135 lines", () => {
    expect(entries.length).toBeGreaterThanOrEqual(55);
    expect(entries.length).toBeLessThanOrEqual(135); // 110 + the hand-play/voice-action lines (room_pal_*) + throw reactions (2026-11-15)
  });

  test("load-bearing lines are at most 15 words, ear lines at most 10, and nothing runs past 30", () => {
    const loadBearing = entries.filter((l) => l.tags?.includes("load-bearing"));
    expect(loadBearing.length).toBeGreaterThan(15);
    for (const l of loadBearing) expect({ id: l.id, short: wordCount(l.text) <= 15 }).toEqual({ id: l.id, short: true });
    for (const l of entries.filter((e) => e.channel === "ear")) expect({ id: l.id, short: wordCount(l.text) <= 10 }).toEqual({ id: l.id, short: true });
    for (const l of entries) expect({ id: l.id, words: wordCount(l.text) <= 30 }).toEqual({ id: l.id, words: true });
  });

  test("numbers are spelled out (Flash v2.5 does not normalize digits) and the scripted wording is there", () => {
    for (const l of entries) expect({ id: l.id, digits: /\d/.test(l.text) }).toEqual({ id: l.id, digits: false });
    expect(lineText("room_hook_1")).toBe("Save our hive, or the swarms take Earth. That rocket is our backup plan.");
    expect(lineText("room_meet_1")).toContain("Local Twenty Thousand");
    expect(lineText("room_meet_2")).toBe("Four problems. Four minutes. Or you're on that rocket.");
    expect(lineText("room_meet_2")).toContain("Four minutes");
    expect(lineText("room_feed_tip")).toBe("We're starving.");
    expect(lineText("room_hornet_1")).toContain("FIST");
    expect(lineText("room_hornet_2")).toBe("Now HUM!");
    expect(lineText("room_hornet_4")).toBe("STOP, you're cooking the girls!");
    expect(lineText("room_whatif_fire")).toStartWith("Don't. Say. That.");
    expect(lineText("room_escape")).toBe("Motion carried. No ravaging today. Cancel the evacuation.");
    expect(lineText("room_t0_1")).toBe("...Shame to waste a ticket. Non-refundable.");
    expect(lineText("room_t0_2")).toBe("TAKE ME TO THE MOON!");
    expect(lineText("room_t0_3")).toBe("...Not one flower. We've only found them on Earth.");
    expect(lineText("room_map")).toStartWith("Same time tomorrow? Other hives have it worse.");
    const all = entries.map((l) => l.text).join(" ");
    for (const phrase of ["Motion carried.", "Grievance filed.", "Noted for the minutes.", "I'll allow it."]) expect(all).toContain(phrase);
  });

  test("the true facts are where the script puts them", () => {
    expect(lineText("room_smoker_smoker")).toContain("alarm");
    expect(lineText("room_smoker_smoker")).toContain("bananas");
    expect(lineText("room_smoker_smoker")).toContain("fill up on honey");
    expect(lineText("room_feed_store_honey")).toContain("foulbrood");
    expect(lineText("room_feed_store_honey")).toContain("decades");
    expect(lineText("room_feed_sugar_syrup")).toContain("lift the hive");
    expect(lineText("room_wasps_smaller_door")).toContain("fewer guards");
    expect(lineText("room_heat_pebble_dish")).toContain("fan our wings");
    expect(lineText("room_heat_deep_bowl")).toContain("drown");
    expect(lineText("room_hornet_3")).toContain("forty-six degrees");
    expect(lineText("room_meet_stat")).toContain("more than half");
    for (const fact of ["smoke", "foulbrood", "heft", "reducer", "water", "cooling", "hornet", "colonies"]) {
      expect({ fact, tagged: entries.some((l) => l.tags?.includes(`fact:${fact}`)) }).toEqual({ fact, tagged: true });
    }
  });

  test("every station has an intro, every choice an outcome line, every what-if topic a line, plus the undo lines", () => {
    for (const station of STATIONS) {
      expect(LINES[`room_${station}_intro`]?.channel).toBe("room");
      for (const choice of CHOICES[station]) expect({ id: `room_${station}_${choice}`, ok: Boolean(LINES[`room_${station}_${choice}`]) }).toEqual({ id: `room_${station}_${choice}`, ok: true });
    }
    for (const topic of WHATIF_TOPICS) expect(whatIfLine(topic)).toBe(`room_whatif_${topic}`);
    for (const id of Object.values(UNDO_LINES)) expect(LINES[id]?.channel).toBe("room");
  });

  test("6-8 short room acknowledgements for voice nobody understood", () => {
    expect(ACK_LINES.length).toBeGreaterThanOrEqual(6);
    expect(ACK_LINES.length).toBeLessThanOrEqual(8);
    for (const id of ACK_LINES) {
      expect(LINES[id]!.channel).toBe("room");
      expect(wordCount(LINES[id]!.text)).toBeLessThanOrEqual(7);
    }
    expect(ACK_LINES).toContain("room_ack_minutes");
  });

  test("the glasses keep their ear lines", () => {
    for (const id of ["ear_practice", "ear_not_now", "ear_no_signal", "ear_scan_failed", "ear_hold_still", "ear_hold_still_2", "ear_canned_unclear"]) {
      expect({ id, ok: LINES[id]?.channel === "ear" }).toEqual({ id, ok: true });
    }
    expect(lineText("ear_not_now")).toBe("Not now, I'm monologuing.");
    expect(entries.filter((l) => l.id.startsWith("ear_hint_")).length).toBeGreaterThanOrEqual(4);
    expect(entries.filter((l) => l.id.startsWith("ear_aside_")).length).toBeGreaterThanOrEqual(1);
  });

  test("the lines the scan director and the Hive borrow exist", () => {
    // director.ts: FALLBACK_LINE, PERSON_LINE and the off-list canned lines; server.ts: the scan fallback twin
    for (const id of ["ear_canned_unclear", "room_canned_unclear", "room_verdict_person", "room_ack_allow", "room_ack_denied", "room_ack_grievance"]) {
      expect({ id, ok: Boolean(LINES[id]) }).toEqual({ id, ok: true });
    }
    expect(lineText("ear_canned_unclear")).toBe(lineText("room_canned_unclear"));
  });
});

describe("stations.json", () => {
  test("covers every station and every CHOICES entry; correct matches CORRECT_CHOICE; scenes, lines and fx exist", () => {
    expect(Object.keys(rawStations).sort()).toEqual([...STATIONS].sort());
    for (const station of STATIONS) {
      const entry = STATION_TABLE[station]!;
      expect(entry.intro_line).toBe(`room_${station}_intro`);
      expect(Object.keys((rawStations as Record<string, { choices: object }>)[station]!.choices).sort()).toEqual([...CHOICES[station]].sort());
      for (const choice of CHOICES[station]) {
        const o = (entry.choices as Record<string, { correct: boolean; line_id: string; scene: string; fx?: string }>)[choice]!;
        expect({ station, choice, correct: o.correct }).toEqual({ station, choice, correct: CORRECT_CHOICE[station] === choice });
        expect(LINES[o.line_id]?.channel).toBe("room");
        expect(LINES[o.line_id]!.tags).toContain(`choice:${choice}`);
        expect(SCENES as readonly string[]).toContain(o.scene);
        if (o.fx !== undefined) expect(FX as readonly string[]).toContain(o.fx);
      }
    }
  });

  test("a correct choice heals the window, a wrong one hurts it", () => {
    const healed = new Set(["smoker_settle", "feed_recover", "wasps_leave", "heat_drink"]);
    for (const station of STATIONS) {
      for (const [choice, o] of Object.entries(STATION_TABLE[station]!.choices)) {
        expect({ choice, healed: healed.has(o!.scene) }).toEqual({ choice, healed: o!.correct });
      }
    }
  });
});

describe("orbis_scenes.json", () => {
  const scenes = rawScenes as Record<string, { prompt: string; seed: number; clip: string }>;
  const CAMERA = "Wide shot, eye-level, static camera, deep depth of field.";

  test("has every SCENES entry and nothing else, each with clip <scene>.mp4 and one shared seed", () => {
    expect(Object.keys(scenes).sort()).toEqual([...SCENES].sort());
    const seeds = new Set(Object.values(scenes).map((s) => s.seed));
    expect(seeds.size).toBe(1);
    for (const scene of SCENES) {
      expect(scenes[scene]!.clip).toBe(`${scene}.mp4`);
      expect(SCENE_TABLE[scene]!.prompt).toBe(scenes[scene]!.prompt);
    }
  });

  test("prompts follow Reactor's guide: 1-3 sentences, under 100 words, same camera, same hive, no negations, non-violent", () => {
    for (const scene of SCENES) {
      const p = scenes[scene]!.prompt;
      const sentences = p.split(/(?<=[.!?])\s+/).filter(Boolean);
      expect({ scene, sentences: sentences.length >= 1 && sentences.length <= 3 }).toEqual({ scene, sentences: true });
      expect({ scene, words: wordCount(p) < 100 }).toEqual({ scene, words: true });
      expect({ scene, camera: p.endsWith(CAMERA) }).toEqual({ scene, camera: true });
      expect({ scene, hive: p.includes("white wooden beehive on a low stand") }).toEqual({ scene, hive: true });
      expect({ scene, negation: /\b(no|not|never|without|none|nothing|nobody|cannot)\b|n't\b/i.test(p) }).toEqual({ scene, negation: false });
      expect({ scene, violent: /\b(kill|killed|dead|death|dying|die|blood|attack|burned|burning|burns|destroy|hurt|injur)/i.test(p) }).toEqual({
        scene,
        violent: false,
      });
    }
  });
});

describe("verdictLine (glasses scan mode)", () => {
  test("the person deflection exists; v1 props no longer have verdict lines (the scan falls back to a live line)", () => {
    expect(verdictLine("person", "roast", "room")).toBe("room_verdict_person");
    for (const item of Object.keys(PROP_VERDICT) as Item[]) {
      if (item === "person") continue;
      expect(verdictLine(item, "good", "room")).toBeNull();
      expect(verdictLine(item, "good", "ear")).toBeNull();
    }
  });
});

describe("helpers", () => {
  test("lineText of an unknown id is empty", () => {
    expect(lineText("nope")).toBe("");
  });

  test("lineIdForText finds the pre-rendered twin of a canned line", () => {
    expect(lineIdForText("What IS that?  Not on the agenda.", "ear")).toBe("ear_canned_unclear");
    expect(lineIdForText("what is that? not on the agenda", "room")).toBe("room_canned_unclear");
    expect(lineIdForText("I'll allow it.", "room")).toBe("room_ack_allow");
    expect(lineIdForText("Something new entirely", "ear")).toBeNull();
    expect(lineIdForText("", "ear")).toBeNull();
  });
});
