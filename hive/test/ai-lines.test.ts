import { describe, expect, test } from "bun:test";
import raw from "../lines/lines.json";
import { LINES, PROP_VERDICT, callbackLine, lineIdForText, lineText, verdictLine, wordCount } from "../src/lines";
import { BEATS, ITEMS, VERDICTS, type Item } from "../src/protocol";

type RawLine = { id: string; channel: string; text: string; beat?: string; tags?: string[] };
const entries = raw as RawLine[];
const OFF_LIST: Item[] = ["other_bee_plant", "other_plant", "non_plant_object", "unclear"];

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
      if (l.beat !== undefined) expect(BEATS as readonly string[]).toContain(l.beat);
      if (l.tags !== undefined) for (const t of l.tags) expect(typeof t).toBe("string");
    }
    expect(Object.keys(LINES).length).toBe(entries.length);
  });

  test("has 45-55 lines", () => {
    expect(entries.length).toBeGreaterThanOrEqual(45);
    expect(entries.length).toBeLessThanOrEqual(55);
  });

  test("load-bearing ear lines are at most 8 words", () => {
    const loadBearing = entries.filter((l) => l.tags?.includes("load-bearing"));
    expect(loadBearing.length).toBeGreaterThan(10);
    for (const l of loadBearing) {
      expect(l.channel).toBe("ear");
      expect({ id: l.id, words: wordCount(l.text) <= 8 }).toEqual({ id: l.id, words: true });
    }
    // every ear line that carries game instructions or a verdict is tagged load-bearing
    for (const l of entries.filter((e) => e.channel === "ear" && !e.tags?.some((t) => t === "callback" || t === "aside"))) {
      expect({ id: l.id, tagged: l.tags?.includes("load-bearing") }).toEqual({ id: l.id, tagged: true });
    }
  });

  test("required lines exist with the scripted wording", () => {
    expect(lineText("ear_hornet_warning")).toBe("HORNET. Behind Goggles. YELL!");
    expect(lineText("ear_practice")).toBe("Testing. You smell like coffee.");
    expect(lineText("ear_canned_unclear")).toBe("What IS that? Bring me a FLOWER.");
    expect(lineText("ear_not_now")).toBe("Not now, I'm monologuing.");
    for (const id of [
      "room_meet_1", "room_meet_2", "ear_hold_still", "ear_hold_still_2", "ear_swarm_arrived",
      "ear_callback_rose", "ear_callback_spray", "ear_callback_overshoot", "ear_callback_default",
      "room_hornet_1", "room_hornet_2", "room_t0_1", "room_t0_2", "room_t0_3",
    ]) {
      expect({ id, ok: Boolean(LINES[id]) }).toEqual({ id, ok: true });
    }
    expect(entries.filter((l) => l.tags?.includes("ack")).length).toBe(5);
    expect(entries.filter((l) => l.id.startsWith("room_hornet_")).length).toBeGreaterThanOrEqual(3);
    expect(entries.filter((l) => l.id.startsWith("room_t0_")).length).toBeGreaterThanOrEqual(3);
  });

  test("the opener is at most 60 words and carries the hedged colony-loss fact", () => {
    const opener = entries.find((l) => l.tags?.includes("opener"))!;
    expect(wordCount(opener.text)).toBeLessThanOrEqual(60);
    expect(opener.text).toContain("more than half");
    expect(opener.text).toContain("managed colonies");
  });

  test("only the 4 vetted facts, each tagged, and no fig claims or digits", () => {
    for (const n of [1, 2, 3, 4]) {
      expect(entries.some((l) => l.tags?.includes(`fact:${n}`))).toBe(true);
    }
    for (const l of entries) {
      expect(l.text.toLowerCase()).not.toContain("fig");
      // Flash v2.5 does not normalize numbers, so numbers are written as words
      expect({ id: l.id, digits: /\d/.test(l.text) }).toEqual({ id: l.id, digits: false });
    }
    expect(lineText("room_hornet_2")).toContain("forty-six degrees");
  });
});

describe("verdictLine", () => {
  test("every prop (and person) has a verdict line on both channels", () => {
    for (const item of Object.keys(PROP_VERDICT) as Item[]) {
      for (const channel of ["ear", "room"] as const) {
        for (const verdict of VERDICTS) {
          const id = verdictLine(item, verdict, channel);
          expect({ item, channel, id: id !== null }).toEqual({ item, channel, id: true });
          expect(LINES[id!]!.channel).toBe(channel);
          expect(LINES[id!]!.tags).toContain(`item:${item}`);
        }
      }
    }
  });

  test("off-list items return null so a live line is used", () => {
    for (const item of OFF_LIST) {
      for (const verdict of VERDICTS) {
        expect(verdictLine(item, verdict, "ear")).toBeNull();
        expect(verdictLine(item, verdict, "room")).toBeNull();
      }
    }
  });

  test("covers every item in the protocol", () => {
    for (const item of ITEMS) expect(Boolean(PROP_VERDICT[item]) || OFF_LIST.includes(item)).toBe(true);
  });
});

describe("helpers", () => {
  test("lineText of an unknown id is empty", () => {
    expect(lineText("nope")).toBe("");
  });

  test("lineIdForText finds the pre-rendered twin of a canned line", () => {
    expect(lineIdForText("What IS that?  Bring me a FLOWER.", "ear")).toBe("ear_canned_unclear");
    expect(lineIdForText("what is that? bring me a flower", "room")).toBe("room_canned_unclear");
    expect(lineIdForText("I'll allow it.", "room")).toBe("room_ack_allow");
    expect(lineIdForText("Something new entirely", "ear")).toBeNull();
    expect(lineIdForText("", "ear")).toBeNull();
  });

  test("callbackLine picks by memory", () => {
    expect(callbackLine({ scanned: ["red_rose"], hornet_overshoot: true })).toBe("ear_callback_overshoot");
    expect(callbackLine({ scanned: ["red_rose", "spray_bottle"] })).toBe("ear_callback_spray");
    expect(callbackLine({ scanned: ["purple_bee_flower"], spray_grabbed: true })).toBe("ear_callback_spray");
    expect(callbackLine({ scanned: ["red_rose", "purple_bee_flower"] })).toBe("ear_callback_rose");
    expect(callbackLine({ scanned: ["purple_bee_flower"] })).toBe("ear_callback_default");
    expect(callbackLine({ scanned: [] })).toBe("ear_callback_default");
  });
});
