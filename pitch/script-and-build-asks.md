# Script and build asks from the pitch review

> The pitch files now avoid claiming any of these. Each one makes a pitch line true (or a better one possible). Owners are guesses, so claim them in PLAN.md. Lines live in `hive/lines/lines.json`; changing a line means re-rendering it in Beeswax's voice.

## Script (the line editor)

| Line id (tonight) | Now | Ask | Why |
|---|---|---|---|
| `room_meet_window` | "See that window? That's the meadow's future. Every choice you make shows up in there. Live." | "See that window? That's the meadow. Every choice you make shows up in there." (Keep "Live." only if the gate passes.) | One name for it: the meadow window. "Future" doesn't fit a fire what-if. |
| `room_whatif_prompt` | "Your turn. Ask the window something. Start with 'what if.'" | "Your turn. Ask me 'what if,' and the window shows it." | The player talks to Beeswax, everywhere. |
| `room_hornet_1` / `room_hornet_3` | "Japanese honeybees ball up and cook hornets. About forty-six degrees. Louder!" | Before the fist: "My cousins in Japan do this. Never tried it. Make a fist." | Our hive is a Western honeybee hive, and they mostly can't do the heat ball. A bee-literate judge will catch it. |
| map line | "Same time tomorrow? There are six more hives." | "Same time tomorrow? Other hives have it worse." (or anything without "six") | The map shows three hives. Say six only if six are on it. |
| new, station 2 | | Player or Beeswax: "Where'd the honey go?" "...Rocket fuel. Next question." | Ties the starving hive to the rocket fueled with honey. |
| new, station 2 lift | | Right after the lift: "Light, right? That's bad." | No haptics, so the line has to tell them. |
| wrong picks | swat, spray, store honey, bigger door, deep bowl, nothing all have lines | Punch each one up so people pick wrong on purpose. "Water? Now they're wet AND angry." is the bar. | Wrong picks should be worth seeing. |
| new, refusal | | "Grievance filed. Not that one." | For a what-if moderation would reject (see below). |

## Build (Unity / Hive)

1. **Hive lift has no weight (U1).** The hive should pop up with no resistance, faster than you expect, then the "Light, right?" line. Pitch now says "it comes up way too easily."
2. **"Grievances filed: N" on the score card (U2 + Rome).** The Hive already records wrong picks per group (`state.memory.wrong` in `hive/src/state.ts`). Cheapest way to make choices count.
3. **Make "sick or healthy" true, or leave it out (U1).** Right now the what-if comes after all four stations are fixed, so every meadow is the same healthy one when it burns. Option: allow the what-if right after the honey mistake while the meadow is still sick, or keep a sick patch until the end. Until then the pitch doesn't claim it.
4. **Off-list what-ifs (Rome, only if time).** A what-if nobody planned for still works: Claude writes a safe Orbis prompt, and anything moderation would reject gets the refusal line. This is the single thing that would most help the Visko pitch. Don't pitch it until it works.
5. **Optional fail ending (U1).** If the four-minute timer runs out, the colony leaves. Only then can the tagline go back to "Fail, and the colony moves to the Moon."
6. **Moon shot (U2/M).** From the Moon's surface, Earth doesn't rise; it hangs in roughly the same spot in the sky. Show Earth over the horizon, or frame the shot from orbit. (The clip/scene is called `earthrise` / "Earth rising" in places.)
7. **Board beat jumps (Rome/U1).** The 2-minute judge cut needs Board → station 2 → what-if → T-0. Confirm `beat.set` jumps work and Unity starts each beat cleanly.
