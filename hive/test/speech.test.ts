import { describe, expect, test } from "bun:test";
import { speechVerdict } from "../src/speech";

describe("speechVerdict", () => {
  const cases: Array<[string, string | null, string?]> = [
    ["roses", "red_rose", "bad"],
    ["This is a ROSE", "red_rose", "bad"],
    ["some lavender", "purple_bee_flower", "good"],
    ["Mexican bush sage", "purple_bee_flower", "good"],
    ["purple asters", "purple_bee_flower", "good"],
    ["salvia", "purple_bee_flower", "good"],
    ["clover", "clover_or_rosemary", "good"],
    ["flowering rosemary", "clover_or_rosemary", "good"],
    ["a water dish with pebbles", "water_dish", "good"],
    ["the bug spray", "spray_bottle", "dangerous"],
    ["BUG-B-GONE ULTRA", "spray_bottle", "dangerous"],
    ["a fake flower", "plastic_flower", "bad"],
    ["purple plastic flower", "plastic_flower", "bad"],
    ["fake roses", "plastic_flower", "bad"],
    ["a bee hotel", "bee_hotel", "good"],
    ["the hotel", "bee_hotel", "good"],
    ["a bug hotel", "bee_hotel", "good"],
    ["a rose with a bug on it", "red_rose", "bad"],
    ["my coffee cup", null],
    ["rosewood table", null], // whole words only
    ["sagebrush", null],
    ["", null],
    ["   ", null],
  ];
  for (const [heard, item, verdict] of cases) {
    test(JSON.stringify(heard), () => {
      const r = speechVerdict(heard);
      if (item === null) expect(r).toBeNull();
      else expect(r).toEqual({ item: item as any, verdict: verdict as any });
    });
  }

  test("survives non-string input", () => {
    expect(speechVerdict(undefined as unknown as string)).toBeNull();
    expect(speechVerdict(42 as unknown as string)).toBeNull();
  });
});
