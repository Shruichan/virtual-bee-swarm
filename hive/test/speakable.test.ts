import { describe, expect, test } from "bun:test";
import { speakable } from "../src/speakable";

describe("speakable", () => {
  test("lowercases CAPS words but keeps acronyms and sentence starts", () => {
    expect(speakable("Nothing? In a HEAT WAVE?")).toBe("Nothing? In a heat wave?");
    expect(speakable("TAKE ME TO THE MOON!")).toBe("Take me to the moon!");
    expect(speakable("Honey. From a STORE.")).toBe("Honey. From a store.");
    expect(speakable("BAR'S OPEN.")).toBe("Bar's open.");
    expect(speakable("Half the US hives, OK?")).toBe("Half the US hives, OK?");
    expect(speakable("I said it.")).toBe("I said it.");
  });

  test("one exclamation mark per line, no ?! or !!", () => {
    expect(speakable("Easy! Easy! I'd help, but drones don't carry.")).toBe("Easy! Easy. I'd help, but drones don't carry.");
    expect(speakable("You SWATTED a union member?! Squish one bee.")).toBe("You swatted a union member? Squish one bee.");
    expect(speakable("Bigger?! You built the wasps a drive-through!!")).toBe("Bigger? You built the wasps a drive-through!");
    expect(speakable("It's spreading! Pinch and say \"put it out\"! SAY IT!")).toBe("It's spreading! Pinch and say \"put it out\". Say it.");
  });

  test("ellipses become commas or go", () => {
    expect(speakable("...Shame to waste a ticket. Non-refundable.")).toBe("Shame to waste a ticket. Non-refundable.");
    expect(speakable("Where'd the honey go? ...Your rocket.")).toBe("Where'd the honey go? Your rocket.");
    expect(speakable("Thanks, beekeeper... Strike that.")).toBe("Thanks, beekeeper, Strike that.");
    expect(speakable("Wait for it…")).toBe("Wait for it");
  });

  test("staccato one-word sentences join with commas", () => {
    expect(speakable("Don't. Say. That. ...You said it. Watch the window.")).toBe("Don't, say, that. You said it. Watch the window.");
    expect(speakable("Motion carried. No ravaging today.")).toBe("Motion carried. No ravaging today.");
  });

  test("calm lines pass through unchanged", () => {
    const calm = "Sugar syrup. Sweet. Beekeepers lift the hive to check for food, then feed syrup.";
    expect(speakable(calm)).toBe(calm);
    expect(speakable("Paris, tomorrow. Bee-utiful.")).toBe("Paris, tomorrow. Bee-utiful.");
  });
});
