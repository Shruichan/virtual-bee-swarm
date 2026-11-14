// Keyword fast path: what the glasses judge SAYS they're holding gives an instant verdict.
import type { Item, Verdict } from "./protocol";

interface Rule { item: Item; verdict: Verdict; words: string[] }

// Modifiers that override anything else said ("fake roses", "purple plastic flower", "bug spray").
const STRONG: Rule[] = [
  { item: "plastic_flower", verdict: "bad", words: ["plastic", "fake"] },
  { item: "spray_bottle", verdict: "dangerous", words: ["spray", "sprayer", "spray bottle"] },
  { item: "bee_hotel", verdict: "good", words: ["bee hotel", "bug hotel", "insect hotel"] },
];

// Otherwise the earliest keyword in the sentence wins ("a rose with a bug on it" = rose).
const PLAIN: Rule[] = [
  { item: "red_rose", verdict: "bad", words: ["rose", "roses"] },
  {
    item: "purple_bee_flower",
    verdict: "good",
    words: ["lavender", "lavenders", "salvia", "salvias", "sage", "aster", "asters", "purple"],
  },
  { item: "clover_or_rosemary", verdict: "good", words: ["clover", "clovers", "rosemary"] },
  { item: "water_dish", verdict: "good", words: ["water", "dish", "dishes"] },
  { item: "spray_bottle", verdict: "dangerous", words: ["bug", "bugs"] },
  { item: "bee_hotel", verdict: "good", words: ["hotel"] },
];

const escape = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const compile = (words: string[]) =>
  new RegExp(`(?<![\\p{L}\\p{N}])(?:${words.map((w) => escape(w).replace(/ /g, "\\s+")).join("|")})(?![\\p{L}\\p{N}])`, "iu");

const STRONG_RX = STRONG.map((r) => ({ ...r, rx: compile(r.words) }));
const PLAIN_RX = PLAIN.map((r) => ({ ...r, rx: compile(r.words) }));

export function speechVerdict(heard: string): { item: Item; verdict: Verdict } | null {
  if (typeof heard !== "string" || !heard.trim()) return null;
  const text = heard.slice(0, 500);
  for (const r of STRONG_RX) if (r.rx.test(text)) return { item: r.item, verdict: r.verdict };
  let best: { item: Item; verdict: Verdict; at: number } | null = null;
  for (const r of PLAIN_RX) {
    const m = r.rx.exec(text);
    if (m && (best === null || m.index < best.at)) best = { item: r.item, verdict: r.verdict, at: m.index };
  }
  return best && { item: best.item, verdict: best.verdict };
}
