# Tracks: which two, and what we say for each

> The 2026 track list is on the login-gated dashboard and we don't have it yet. Every track name below is a guess from 2025 and our sponsor notes. TODO: replace with the real names and wording, and answer: do sponsor prizes count toward the 2-track limit?
> PLAN.md says pick tracks by Sat 9 AM, but the Orbis gate is Sat 11 AM. Treat the 9 AM pick as provisional and lock it after the gate. TODO: check whether tracks have to be declared anywhere before the Devpost submission.
> Judge-facing names: HONEYMOON and Beeswax. The window is "the meadow window".

## Decision

**Orbis gate (Sat 11 AM):** passes if the live stream holds 10+ fps in the Quest window for 5 minutes and a prompt change shows up within about 4 seconds.
**Glasses "landed":** by the Sat 6 PM freeze, a second player can reliably pick a station choice by looking at a real prop with Mentra Live.

| Orbis gate | Glasses | Enter |
|---|---|---|
| Pass | Landed | **Visko + ManageXR** |
| Pass | Not landed | **Visko + Social Good** |
| Fail | Landed | **ManageXR + Social Good** |
| Fail | Not landed | **Social Good + Creative/Gaming** (or Best use of AI, if that track exists and its wording fits better) |

If sponsor prizes don't count toward the limit: enter Visko either way (with honest "recorded clips" framing if the gate failed) and use the two main slots on Social Good + Creative/Gaming.

---

## Visko (living worlds)

Pick if the gate passes. **One sentence:** "You change a living world by talking to it." Orbis generates the meadow window next to the hive as real-time video (832×480, about 18 fps on Reactor's GPUs). Our relay holds one session for the whole run and sends the frames to the headset. Each station choice and each spoken what-if sends Orbis a new scene prompt mid-stream: the meadow calms down after the smoker, goes grey and sick after the store honey, catches fire when you say "what if a fire starts," recovers when you say "put it out," and turns into the Moon at the end. The window never cuts. Orbis morphs from the frame it's showing into the fire, and it starts when you say the words, not on a timer. Our recorded clips are the backup, and they cut. If the Beat Board's LIVE/CLIPS toggle works at the table, show the judge both. TODO: confirm which scenes actually stream live and which are clip-only.

What would make this track ours (TODO, don't pitch until it works): a what-if nobody planned for still works. Claude writes a safe Orbis prompt from what the player said, and anything moderation would reject gets a Beeswax refusal ("Grievance filed. Not that one."). See `script-and-build-asks.md`.

## ManageXR (AI in the physical world with smart glasses)

Pick **only if the glasses land.** **One sentence:** "The AI looks at real objects through a pair of glasses and turns them into choices in someone else's mixed reality." A second player wears Mentra Live (camera, mics and speakers, no display), looks at a real jar of honey or a real bottle of sugar syrup on the table, and presses the button. Claude names the object from the photo, and the Hive turns it into that station's choice in the Quest: the window reacts, Beeswax reacts in the headset, and he also talks in the glasses wearer's ear. The classroom version is where ManageXR fits: one student in a headset, a few in glasses at the same table, a teacher on the Beat Board, and a class set of headsets kept up to date through ManageXR (scene-aware apps go through Meta's Horizon managed app store, which is ManageXR's documented route for them). **If the glasses don't land, we don't enter this track.** If we're in it anyway (say, sponsor prizes are separate), the honest line is: "The glasses app is in the repo under `glasses/`. Here's what it does today: TODO. In the demo, the AI hears you through the headset mic instead." Don't call a half-working feature done.

Only if they ask why our demo headset isn't enrolled: see `judge-qa.md` → questions we hope they don't ask. Don't bring it up.

## Social Good / Education

Fallback partner for either Visko or ManageXR, and the main pick if the gate fails. **One sentence:** "A four-minute game where every choice is a real beekeeping practice, and the honey one is a mistake a kind person would make." US beekeepers lost an estimated 55.6% of their managed colonies in 2024–25, the worst year since the survey started in 2010, and the "better" year after that was still 39.9%. HONEYMOON teaches by letting you get it wrong: feeding a hungry hive store-bought honey feels kind, and it can spread a disease whose spores last for decades. Every fact Beeswax says is tied to an action and has a source on our Devpost (smoke and the alarm pheromone, hefting the hive, sugar syrup, entrance reducers, water with landing spots). The next step is a classroom kit: the app, a printed mat, the real props, a one-page fact sheet, and more hives as lessons (farmland and pesticide drift, a city rooftop, a desert). It's open source under MIT. We haven't tested it with students, and we'll say that if asked. TODO: add anything real from the Sat 6 PM stranger run (how many people picked the honey, if we counted).

## Best use of AI

Only if this track exists. **One sentence:** "Talking to a world is the AI part, and the model only ever picks from a list, so it can't break the demo." Four AI pieces, each doing one job. Speech-to-text (ElevenLabs Scribe, with local Whisper as a backup that starts if the first one is slow). Claude, which turns a sentence into one intent from a fixed list after a cheap keyword check. Visko Orbis, generating the window's world in real time. ElevenLabs, which designed Beeswax's voice. The design choice we'd defend: the model never writes game logic. Unity only runs things from the list, every scripted line is pre-rendered, Beeswax covers the 2 to 4 second waits, and every AI piece has a non-AI fallback (keyword match, operator button, recorded clips). TODO, one clause max and only if it shipped: Claude also writes short answers to off-script questions.

## Creative / Gaming

Second pick if the gate fails and the glasses don't land. **One sentence:** "A beehive on your real table, run by a petty bee union rep who's leaving for the Moon." It has a clear goal (fix four things, the hive's healthy, the door opens and the bees head out to work) and a timer. TODO, only if true by Sunday: a score you want to beat ("Grievances filed: N" for wrong picks), and choices you make with your hands (squeeze the smoker, lift the hive and feel it come up way too easily, grab a jar). Beeswax carries the comedy ("Motion carried." "Grievance filed." "Ticket's non-refundable."), and the theme has a twist: *Take Me To The Moon* is what the bees threaten. No flowers, no bees. That's the Moon. The two moments people repeat afterward (TODO: confirm at the Sat 6 PM run) are the embers coming out of the window into the room, and Beeswax launching anyway, finding "not one flower," and crash-landing back on the table. The map at the end says there's more game here. TODO: add the hornet hum if it shipped (make a fist, then hum, and the bees cook the hornet).
