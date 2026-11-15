import { describe, expect, test } from "bun:test";
import { existsSync, statSync } from "node:fs";
import { join } from "node:path";
import demoRaw from "../lines/scout_lines.json";
import boothRaw from "../lines/scout_lines_booth.json";
import { PLACEHOLDER, SCOUT_AUDIO_DIR, SCOUT_MANIFEST_FILE, VOICE_NAMES, lineHash, readManifest } from "../scripts/scout-render";
import { SCOUT_BOOTH_LINES, SCOUT_LABELS, SCOUT_LINES, type ScoutLabel, type ScoutLine } from "../src/scout-protocol";

const demo = demoRaw as ScoutLine[];
const booth = boothRaw as ScoutLine[];
/** What the game loads: both files (booth facts double as demo mode's facts). */
const lines = [...demo, ...booth];
const byId = new Map(lines.map((l) => [l.id, l]));

/** Every id SCOUT_LINES implies, with the kind (and label) it must have. */
const expected: { id: string; kind: ScoutLine["kind"]; label?: ScoutLabel }[] = [
  ...SCOUT_LINES.intro.map((id) => ({ id, kind: "intro" as const })),
  { id: SCOUT_LINES.chime, kind: "chime" },
  ...SCOUT_LABELS.flatMap((label) => [
    { id: SCOUT_LINES.hook(label), kind: "hook" as const, label },
    { id: SCOUT_LINES.fact(label), kind: "fact" as const, label },
  ]),
  ...SCOUT_LINES.no.map((id) => ({ id, kind: "no" as const })),
  { id: SCOUT_LINES.rude, kind: "rude" },
  { id: SCOUT_LINES.silence, kind: "silence" },
  { id: SCOUT_LINES.idle, kind: "idle" },
  ...[1, 2, 3, 4, 5].map((n) => ({ id: SCOUT_LINES.progress(n), kind: "progress" as const })),
  { id: SCOUT_LINES.done, kind: "done" },
  // booth mode (scout_lines_booth.json); each fact has a second part, played right after it
  { id: SCOUT_BOOTH_LINES.intro, kind: "intro" },
  { id: SCOUT_BOOTH_LINES.chimeAsk, kind: "chime" },
  { id: SCOUT_BOOTH_LINES.silenceYes, kind: "silence" },
  { id: SCOUT_BOOTH_LINES.no, kind: "no" },
  { id: SCOUT_BOOTH_LINES.done, kind: "done" },
  ...Object.values(SCOUT_BOOTH_LINES.dir).map((id) => ({ id, kind: "direction" as const })),
  ...SCOUT_LABELS.flatMap((label) => [
    { id: `${SCOUT_LINES.fact(label)}_2`, kind: "fact" as const, label },
    { id: SCOUT_BOOTH_LINES.next(label), kind: "direction" as const, label },
  ]),
];

/** Ride Script v2 SOURCES plus scout_script_v3.json sources (branch scout/content): the only URLs a fact may cite. */
const SOURCE_ALLOWLIST = new Set([
  "https://plants.ces.ncsu.edu/plants/helianthus-annuus/",
  "https://usda.azureedge.us/node/48771",
  "https://pueblo.extension.colostate.edu/?p=4685",
  "https://doi.org/10.1371/journal.pone.0220029",
  "https://biologicaldiversity.org/news/press_releases/2017/bees-03-01-2017.php",
  "https://www.sciencenews.org/article/douse-hot-hives-honeybee-colonies-launch-water-squadrons",
  "https://www.fws.gov/press-release/2017-01/race-against-extinction-rusty-patched-bumble-bee-listed-endangered",
  "https://www.usgs.gov/faqs/how-many-species-native-bees-are-united-states",
  "https://canr.udel.edu/maarec/honey-bee-biology/the-colony-and-its-organization/",
  "https://ucanr.edu/node/154475",
  "https://honeybeesuite.com/festooning-bees-lacework-between-the-frames/",
  "https://www.honeybeesuite.com/why-do-my-bees-turn-nasty-when-i-kill-them/",
  "https://doi.org/10.1038/1951018b0",
  "https://askdruniverse.wsu.edu/2026/06/04/smoke-make-bees-calm/",
  "https://doi.org/10.1007/BF01990966",
  "https://ijbs.com/v09p0313.htm",
  "https://theapiarist.org/the-scent-of-death/",
  "https://uaex.uada.edu/media-resources/news/2026/april/04-16-2026-ark-honey-bee-swarms.aspx",
  "https://theapiarist.org/weighty-matters/",
  "https://www.ontario.ca/page/american-foulbrood",
  "https://ages.at/en/human/disease/pathogens-from-a-to-z/american-foulbrood",
  "https://pir.sa.gov.au/biosecurity/animal_health/animal_species/bees/american_foulbrood",
  "https://extension.oregonstate.edu/catalog/em-9211-protecting-honey-bees-yellowjacket-wasps",
  "https://doi.org/10.1371/journal.pone.0008967",
  "https://uaex.uada.edu/media-resources/news/2022/june/06-15-2022-ark-bee-hydration.aspx",
  "https://extension.oregonstate.edu/catalog/how-do-wildfires-affect-bees",
  "https://www.sciencedaily.com/releases/2019/04/190403094132.htm",
  "https://doi.org/10.3389/fevo.2019.00252",
  "https://doi.org/10.1111/geb.12939",
  "https://www.honeybeesuite.com/do-honey-bees-fly-in-the-rain/",
  "https://pnwhandbooks.org/insect/bee-protection/reduce-bee-poisoning",
  "https://xerces.org/bring-back-the-pollinators",
  "https://www.xerces.org/blog/want-to-save-bees-focus-on-habitat-not-honey-bees",
  "https://xerces.org/pollinator-conservation/nesting",
  "https://wire.auburn.edu/content/agriculture/2025/06/2506-beekeepers-survey.php",
  "https://wire.auburn.edu/content/agriculture/2026/06/honeybeecolony0626.php",
  "https://www.ars.usda.gov/news-events/news/research-news/2025/usda-researchers-find-viruses-from-miticide-resistant-parasitic-mites-are-cause-of-recent-honey-bee-colony-collapses/",
  "https://www.smithsonianmag.com/air-space-magazine/earth-rising-180951474/",
]);

/** Off-voice and AI-tell words (game-writing guide). Whole words, any case. */
const BANNED = [
  "delve", "tapestry", "testament", "journey", "adventure", "amazing", "magical", "buddy", "folks", "friend",
  "vibrant", "bustling", "realm", "unleash", "elevate", "seamless", "captivating", "crucial", "pivotal",
  "embark", "whimsical", "awesome", "incredible", "fascinating", "remember",
];
/** Kid safety: nothing that tells a kid to handle bees, nothing mean. */
const UNSAFE = ["touch", "hold", "grab", "catch", "swat", "squish", "kill", "stupid", "dumb", "idiot", "hate", "shut"];

const words = (t: string) => t.split(/\s+/).filter(Boolean);
const capsWords = (t: string) => words(t).map((w) => w.replace(/[^A-Za-z]/g, "")).filter((w) => /^[A-Z]{2,}$/.test(w));
const HOOK_ENDING = "Would you like to learn more?";

describe("scout_lines.json + scout_lines_booth.json", () => {
  test("has exactly the ids SCOUT_LINES and SCOUT_BOOTH_LINES imply, once each across both files, right kind and label", () => {
    const ids = lines.map((l) => l.id);
    expect(new Set(ids).size).toBe(ids.length);
    expect([...ids].sort()).toEqual(expected.map((e) => e.id).sort());
    for (const e of expected) {
      const l = byId.get(e.id)!;
      expect({ id: l.id, kind: l.kind, label: l.label }).toEqual({ id: e.id, kind: e.kind, label: e.label });
    }
  });

  test("every line is short, digit-free and shouts at most one word", () => {
    for (const l of lines) {
      const why = `${l.id}: ${l.text}`;
      expect(l.text.trim().length > 0, why).toBe(true);
      expect(l.text.length <= 110, why).toBe(true);
      expect(words(l.text).length <= 20, why).toBe(true);
      expect(/\d/.test(l.text), why).toBe(false);
      expect(capsWords(l.text).length <= 1, why).toBe(true);
    }
  });

  test("the chimes say the catchphrase", () => {
    expect(byId.get(SCOUT_LINES.chime)!.text).toBe("Bee fact found!");
    expect(byId.get(SCOUT_BOOTH_LINES.chimeAsk)!.text).toBe("Bee fact found! Would you like to learn more?");
  });

  test("demo-only ids stay in scout_lines.json, booth ids in scout_lines_booth.json", () => {
    const boothIds = new Set(booth.map((l) => l.id));
    for (const label of SCOUT_LABELS) {
      expect(boothIds.has(SCOUT_LINES.fact(label))).toBe(true);
      expect(boothIds.has(SCOUT_LINES.hook(label))).toBe(false);
    }
    expect(booth.find((l) => l.id === SCOUT_BOOTH_LINES.dir.up)!.text).toBe("Lift your chin a little so I can see it.");
  });

  test("hooks are short, name something first, and end with the same offer", () => {
    for (const label of SCOUT_LABELS) {
      const h = byId.get(SCOUT_LINES.hook(label))!.text;
      expect(h.endsWith(` ${HOOK_ENDING}`), h).toBe(true);
      expect(h.length <= 80, h).toBe(true); // heard right after the chime
      expect(h.slice(0, -HOOK_ENDING.length).trim().length >= 10, h).toBe(true);
    }
  });

  test("repeatable no barks start with different words", () => {
    const first = SCOUT_LINES.no.map((id) => words(byId.get(id)!.text)[0]!.replace(/[^A-Za-z']/g, "").toLowerCase());
    expect(new Set(first).size).toBe(first.length);
  });

  test("every fact cites vetted sources (space-separated when a line makes two claims); nothing else carries one", () => {
    for (const l of lines) {
      if (l.kind !== "fact") {
        expect(l.source, l.id).toBeUndefined();
        continue;
      }
      const urls = (l.source ?? "").split(" ").filter(Boolean);
      expect(urls.length > 0, l.id).toBe(true);
      for (const u of urls) expect(SOURCE_ALLOWLIST.has(u), `${l.id}: ${u}`).toBe(true);
    }
  });

  test("no banned or unsafe words, and the ride's sign-off pun at most once", () => {
    for (const l of lines) {
      // "next adventure" is the user's own wording for the booth handoffs
      const banned = l.kind === "direction" ? BANNED.filter((w) => w !== "adventure") : BANNED;
      for (const w of [...banned, ...UNSAFE]) expect(new RegExp(`\\b${w}`, "i").test(l.text), `${l.id} has "${w}"`).toBe(false);
    }
    const beeswax = lines.filter((l) => /mind your own beeswax/i.test(l.text));
    expect(beeswax.length <= 1).toBe(true);
  });
});

describe("scout_audio", () => {
  test.skipIf(!existsSync(SCOUT_MANIFEST_FILE))("every line has a current mp3 with a duration", () => {
    const manifest = readManifest();
    expect(Object.keys(manifest).sort()).toEqual(lines.map((l) => l.id).sort());
    let bytes = 0;
    for (const l of lines) {
      const m = manifest[l.id]!;
      const file = join(SCOUT_AUDIO_DIR, `${l.id}.mp3`);
      expect(existsSync(file), file).toBe(true);
      bytes += statSync(file).size;
      expect(m.ms > 0, l.id).toBe(true);
      expect(["harry", PLACEHOLDER, ...Object.values(VOICE_NAMES)]).toContain(m.voice);
      expect(m.chars, l.id).toBe(l.text.length);
      expect(m.hash, `${l.id} text changed: run bun scripts/scout-render.ts`).toBe(lineHash(l));
    }
    expect(bytes < 2_000_000).toBe(true);
  });
});
