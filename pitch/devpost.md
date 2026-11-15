# Devpost submission draft

> Draft v2, Sat Nov 14 ~1 AM. Every `TODO` gets filled or deleted before we submit (target Sun ~8:30 AM, Devpost closes 9:00 AM PT).
> Rule for this file: if it isn't working on Sunday morning, it doesn't go in. No numbers we didn't measure.
> The 2026 rules and tracks are on the login-gated dashboard. TODO: someone paste them into PLAN.md and check this draft against them (2025 wanted a 30 s vertical video, a public MIT repo, and paid AI cited in the README).
> Names in judge-facing copy: **HONEYMOON** and **Beeswax**. "Local 20,000" appears once. The rocket has no name here. The window is always "the meadow window".
> The long build notes moved to `readme-paste.md` (for the repo README). Paid-AI spending cap: see `demo-runbook.md` → Orbis cost guard. It's a rules risk, not Devpost copy.

---

## Title

**HONEYMOON**

Subtitle if Devpost gives us room: *A bee union rep, a meadow in your room, and a rocket*

Team: Angry Virtual Bee Swarm.

## Tagline (≤ 140 characters)

> A bee union rep gives you four minutes to fix his hive in your room. Then he leaves for the Moon anyway.

## What's live (short version)

| What you see | How it works during the demo |
|---|---|
| The floor-standing hive, meadow, and swarm in your room | Live on the headset (passthrough + hand tracking) |
| Beeswax | Scripted character. Pre-recorded lines in a designed voice |
| The meadow window | TODO pick one: live Visko Orbis stream steered by what you do and say / recorded Visko Orbis clips |
| Understanding what you say | When connected, speech-to-text plus keyword or AI intent routing. Offline, the game uses local controls and timers |

Full table at the bottom.

---

## Inspiration

This year's theme is *Take Me To The Moon*, so we made the bees the ones who want to leave: their union voted to move the whole colony there. That gave us the ending before we had a game. No flowers, no bees. That's the Moon.

US beekeepers lost an estimated 55.6% of their managed colonies in 2024–25, the worst year since the survey started in 2010, and the "better" year after that was still 39.9%.

The game is built on Adityash's hive pitch, and we wanted the beekeeping in it to be real. Every station is something beekeepers actually do, and the honey one is a mistake a kind person would make: feeding a hungry hive store-bought honey sounds like the nicest thing you could do, and it can wipe the hive out.

## What it does

A worker bee brings you a clipboard. Sign it and the crew carries a sick hive into the meadow on your floor. Beeswax talks from inside the hive as it arrives, then climbs out and gives you four minutes to fix it.

Beeswax follows a prerecorded script through the ride. On a connected run, Ask Beeswax can answer a short question after the finale. He's a male bee (a drone), so he has no stinger and no job, "so they made me union rep." He's shop steward of Local 20,000 (there are over 20,000 known bee species): *Motion carried. Grievance filed. Noted for the minutes.* His rocket is parked next to the hive, fueled with honey.

Next to the hive is the meadow window, which changes with every choice you make. Order tools from the ticket board and the crew brings them to you. Each fix lights a hexagon:

1. **The bees are furious.** Order the smoker and pump the lever to calm them. Smoke dulls the alarm pheromone, which smells like bananas.
2. **Is there enough food?** You lift the hive and it comes up way too easily (beekeepers call this hefting). Store-bought honey or sugar syrup? Store honey can carry American foulbrood spores, which kill hives and last for decades. Pick it and the meadow gets sick. Recover by ordering syrup and using the roof feeder.
3. **Wasps at the door.** Choose the smaller reducer and fit it into the hive door. A smaller entrance leaves the guard bees less door to defend.
4. **Heat wave.** Set down the shallow pebble dish and pump the water. The pebbles give bees somewhere to land. They spread the water in the hive and fan it. "Bee air conditioning."

Wrong picks trigger a Beeswax response and count as grievances.

Then it's your turn. Ask Beeswax "what if…" and the window shows it. **"What if a fire starts?"** "Don't. Say. That." The meadow catches fire, and embers leave the window and drift into your real room. "Put it out" brings the meadow back. With the Hive connected, the game can recognize both phrases even when the window uses recorded clips. If the Hive is offline, the local controls and timers still run the fire sequence.

Then the hive's healthy, the door opens and the bees head out to work. The headset shows "Hive saved in m:ss." The Beat Board shows the same time and the grievance count. The timer freezes after the fourth fix. Earth cancels the evacuation.

Then: "…Shame to waste a ticket. Non-refundable." **"TAKE ME TO THE MOON!"** Pull the launch lever and the rocket takes off with only Beeswax on board. The window turns into the Moon: grey dust, Earth hanging over the horizon. "...Not one flower." He crash-lands back in the meadow, covered in dust. A map shows more hives you haven't unlocked yet.

TODO (only if it shipped): **Hornet hum.** Fast players get a giant hornet. "My cousins in Japan do this. Never tried it. Make a fist." Fist to ball the swarm around it, then hum; the headset mic hears you and a thermometer climbs toward 46 °C. Too long: "STOP, you're cooking the girls!"

TODO (only if it shipped): **Glasses mode.** A second player in Mentra Live glasses looks at the real honey jar or syrup bottle and presses the button, and that object becomes the choice in the headset.

Every fact is sourced at the bottom.

## How we built it

```
 Quest app (Unity 6)  <── WebSocket bus ──>  Hive server (Bun/TS)  <── HTTPS ──  Mentra miniapp (iPhone) ── Mentra Live
   beat clock, bees, hands,                   speech-to-text, intents,             (TODO: only if glasses landed)
   pre-rendered lines, embers                 Beeswax's voice, Beat Board
          ^                                            |
          └── JPEG frames <── Orbis relay (Python) <── WebRTC ── Reactor (Visko Orbis world model)
```

TODO: check every bullet against what we actually built. Some of this is still the plan as of Saturday 1 AM.

- **Quest app:** Unity 6 + OpenXR + Meta XR SDK v209 on a Quest TODO:3-or-3S. Passthrough puts the hive and meadow in your room. The player orders tools with tickets and uses them by hand. Unity runs the timer and embers locally, so the scripted ride can keep moving without the Hive server.
- **Hive server:** Bun + TypeScript + Hono. One WebSocket bus between the headset, a spectator page and the Beat Board, the web page our operator uses to watch a run and fix it live.
- **Voice:** the Quest mic streams to the Hive, ElevenLabs Scribe turns it into text (local faster-whisper as backup), a keyword check runs first, then Claude picks one intent from a fixed list. Unity only runs what's on the list.
- **The meadow window:** Visko's Orbis world model on Reactor makes the meadow as live video. There's no Unity SDK, so a Python relay turns the WebRTC stream into JPEG frames for the headset and changes the prompt mid-stream. Recorded Orbis clips play if live frames stop for 8 seconds.
- **Beeswax and sound:** one ElevenLabs Voice Design voice, with prerecorded ride dialogue in the app. Sounds from Attractor Labs' SATIE (TODO: which ones), spatialized in Unity.

More detail is in the repo README.

## Challenges we ran into

- **Orbis has no Unity SDK**, so we bridged WebRTC into Unity ourselves. The relay keeps only the newest frame, so a slow headset skips frames instead of falling behind.
- **The window takes 2 to 4 seconds to change.** That's forever when someone just yelled "fire," so Beeswax has a line for every wait and the embers are done in Unity.
- **The fire vs. moderation.** Moderation can end a session over violent prompts, so the fire prompt is as calm as we could write it. TODO: what happened when we tested it.
- **Mentra Live has no display** and the SDK in most tutorials is retired. TODO: if the glasses got cut, say so here.
- **One headset for the whole team.** TODO: how we shared it.
- TODO: the real ones from Saturday.

## Accomplishments that we're proud of

TODO: keep only what's true on Sunday morning.

- TODO (only if live passed the gate): The window never cuts. Orbis morphs from the frame it's showing into the fire, and it starts when you say the words, not on a timer.
- TODO (if true): Unity embers that leave the generated fire and land in your room.
- Every fact in the game is real and has a source. TODO: which fact playtesters remembered, if we asked them.
- Beeswax. TODO: best line a stranger laughed at during the Sat 6 PM run.
- Nothing in the demo needs the network to finish. TODO: confirm we actually tested it with Wi-Fi off.

## What we learned

- Most of our window work was the 2 to 4 seconds between scenes. A bee who complains during the wait was the cheapest fix.
- Keep the AI on a short leash. Claude picks from a list, Unity runs the list, and nothing the model invents can break the demo.
- TODO: a real lesson from Unity MR / hand tracking.
- TODO: a real lesson from Reactor / Orbis (what the 11 AM gate taught us).
- TODO: a real lesson from Mentra, or from working with one headset.

## What's next for HONEYMOON

- **More hives**, each with its own problem: farmland (pesticide drift), a city rooftop (heat and water), a desert. The map at the end is a placeholder. None of them exist yet.
- **A classroom kit**: the app, a printed mat, the real props and a one-page fact sheet. One student plays, the class watches the cast and yells what-ifs. A class set of headsets would get the app through ManageXR (via Meta's Horizon managed app store, ManageXR's documented route for scene-aware apps). We haven't tried it with a class.
- More what-ifs (rain, clover, spray from the next field). TODO: move any that shipped up to "What it does".
- Glasses mode for a second player. TODO: drop this line if it shipped.

## Built with

`unity` `c#` `meta-quest` `meta-xr-sdk` `openxr` `passthrough` `hand-tracking` `mixed-reality` `bun` `typescript` `hono` `websockets` `python` `webrtc` `reactor` `visko-orbis` `claude` `anthropic` `elevenlabs` `whisper` `satie` `attractor-labs` `cloudflare`

TODO, only if used: `mentra-live` `mentraos`

---

## What's live vs pre-built (full)

We want judges to know exactly what they're seeing. Everything below was made this weekend unless the row says otherwise. TODO: fill the last column on Sunday morning.

| Piece | During the demo | Status Sun AM |
|---|---|---|
| Floor-standing hive, passthrough, hand interactions | Live, on the headset | TODO |
| The swarm (angry, calm, guarding, pouring out) | Live simulation on the headset | TODO |
| The four stations and their outcomes | Scripted. Your choice picks which outcome plays | TODO |
| Beeswax's scripted lines | Pre-rendered audio in the app (ElevenLabs designed voice) | TODO |
| Beeswax answering off-script questions | Live: Claude writes a short answer, ElevenLabs speaks it | TODO: shipped? Delete the row if not |
| Understanding what you say | Live: speech-to-text, then keyword match, then Claude picks an intent from a fixed list | TODO |
| The meadow window | Live Orbis stream steered by prompts, **or** recorded Orbis clips if live is down | TODO: live or clips? When were the clips recorded? |
| Embers coming out of the window | Live Unity particles, triggered by the fire intent | TODO |
| Hornet hum | Live pitch detection on the headset mic | TODO: shipped? |
| Sounds | Pre-made with SATIE, played and spatialized live | TODO |
| 3D models (Beeswax, hive, rocket, props, Moon) | Pre-made. TODO: made by us this weekend, or asset store? List any third-party assets | TODO |
| Score card, Moon ending, map | Scripted. The map's hives are locked art, not levels | TODO |
| Glasses mode | Live photo, then Claude, then the station choice | TODO: shipped? |

Pre-existing things we used: Unity, the Meta XR SDK, NativeWebSocket, Hono, the Anthropic, ElevenLabs and Reactor SDKs, faster-whisper. TODO: add any assets or code anyone brought in.

## AI services used

Cited per the event rules. TODO: fill in real costs from each dashboard Sunday morning.

| Service | What it does in HONEYMOON | Paid by | Cost |
|---|---|---|---|
| Anthropic Claude (TODO: Haiku 5.5 / Sonnet 5.5) | Picks what the player meant from a fixed list. TODO if shipped: short answers to off-script questions; names the real prop in a glasses photo | Team | TODO |
| ElevenLabs Voice Design + text-to-speech | Beeswax's voice | Team | TODO |
| ElevenLabs Scribe | Speech-to-text for the player's voice | Team | TODO |
| Whisper (faster-whisper, run locally) | Backup speech-to-text | Free, runs on our laptop | $0 |
| Visko Orbis via Reactor | The meadow window, live and the recorded clips | TODO: sponsor credits? | TODO |
| Attractor Labs SATIE | Sound design | TODO: sponsor credits? | TODO |
| MentraOS (TODO: only if used) | Glasses speech-to-text and photos | Platform | TODO |
| AI coding assistants (TODO: Claude Code, others?) | TODO: what they wrote (see `judge-qa.md` → "Did an AI write your code?") | TODO | TODO |

## Fact sources

Every fact Beeswax says is in this list. Numbers are hedged in the game ("more than half", "about 46 degrees").

1. **Colony losses.** Auburn University, "U.S. Beekeeping Survey reveals highest honey bee colony losses" (June 16, 2025). Survey led by Auburn with the Apiary Inspectors of America and Oregon State; 2,453 beekeepers; an estimated 55.6% of managed colonies lost from April 2024 to April 2025, the highest since the survey began in 2010. https://wire.auburn.edu/content/agriculture/2025/06/2506-beekeepers-survey.php
   The next year: Auburn's 2025–26 results (June 24, 2026) put losses at 39.9%, after record losses two years running (55.1% in 2023–24, 55.6% in 2024–25). https://wire.auburn.edu/content/agriculture/2026/06/honeybeecolony0626.php
2. **Japanese honeybees cook hornets.** Ono, M., Igarashi, T., Ohno, E. & Sasaki, M. "Unusual thermal defence by a honeybee against mass attack by hornets." *Nature* 377, 334–336 (1995). More than 500 Japanese honeybees ball around the hornet and the ball averages about 47 °C, which kills the hornet but not the bees. https://doi.org/10.1038/377334a0
   Follow-up: Sugahara, M., Nishimura, Y. & Sakamoto, F. *Zoological Science* 29(1), 30–36 (2012). Within five minutes the ball reaches 46 °C and 4% CO2. The hornet dies within 10 minutes, and the CO2 lowers the temperature it can survive. https://pubmed.ncbi.nlm.nih.gov/22233493/
   (Our hive is a regular Western honeybee hive, and Western honeybees mostly can't pull this off. TODO, if the hornet ships: Beeswax admits it with "My cousins in Japan do this.")
3. **Alarm pheromone smells like bananas.** Boch, R., Shearer, D. A. & Stone, B. C. "Identification of iso-amyl acetate as an active component in the sting pheromone of the honey bee." *Nature* 195, 1018–1020 (1962). https://doi.org/10.1038/1951018b0
   Plain-language: Bee Culture, "Smells" (Oct 2025): "That banana-like odor comes from isoamyl acetate, a key component of alarm pheromone, which also happens to be found in bananas." https://beeculture.com/smells/
   (The "don't eat a banana near the hive" part is beekeeper folklore. Beeswax says it as a joke, and we don't claim it as a fact.)
4. **Smoke dulls the alarm signal.** Visscher, P. K., Vetter, R. S. & Robinson, G. E. "Alarm pheromone perception in honey bees is decreased by smoke." *Journal of Insect Behavior* 8(1), 11–18 (1995). Smoke on the antennae weakened their response to alarm pheromone components, and to a floral odor too. https://doi.org/10.1007/BF01990966
   Plain-language: Washington State University, Ask Dr. Universe, "Why does smoke make bees calm?" (June 4, 2026), with WSU bee scientist Brandon Hopkins. Covers the alarm signal, the banana smell, and bees gorging on honey. https://askdruniverse.wsu.edu/2026/06/04/smoke-make-bees-calm/
5. **Smoke makes bees fill up on honey.** Free, J. B. "Engorging of honey by worker honeybees when their colony is smoked." *Journal of Apicultural Research* 7(3), 135–138 (1968). https://doi.org/10.1080/00218839.1968.11100203
6. **Don't feed bees outside honey.** Ontario Ministry of Agriculture, "American foulbrood": "Honey from outside sources should not be fed to honey bee colonies," and spores "remain viable on used honey bee equipment for up to 70 years." https://www.ontario.ca/page/american-foulbrood
7. **Supermarket honey specifically.** Western Australia DPIRD, "American foulbrood: declared pest": lists feeding bees supermarket honey under don'ts; "Spores can remain viable for more than 50 years." https://www.dpird.wa.gov.au/businesses/beekeeping/pests-and-diseases-in-honey-bees/american-foulbrood/
8. **Entrance reducers.** Breece, C., Sagili, R. & Wyns, D. "Protecting honey bees from yellowjacket wasps," Oregon State University Extension EM 9211 (2018, reviewed 2024): "Entrance reducers minimize the space guard bees need to defend." https://extension.oregonstate.edu/catalog/em-9211-protecting-honey-bees-yellowjacket-wasps
   Also: Willingham, R., Klopchin, J. & Ellis, J. D., "Robbing Behavior in Honey Bees," UF/IFAS (IN1064): weak colonies have fewer guard bees at the entrance; reduce the entrance of a colony being robbed (by other bees). https://edis.ifas.ufl.edu/publication/IN1064
9. **20,000+ bee species.** USGS FAQ, "How many species of native bees are in the United States?": over 20,000 known bee species worldwide, about 4,000 native to the US. https://www.usgs.gov/faqs/how-many-species-native-bees-are-united-states
10. **Water, landing spots, cooling.** University of Arkansas System Division of Agriculture, "Bees need watering too" (June 15, 2022): bees deposit water droplets around the hive and fanning evaporates them; "Bees can't swim"; give them clean gravel to sit on. https://uaex.uada.edu/media-resources/news/2022/june/06-15-2022-ark-bee-hydration.aspx
11. **Water collectors and evaporative cooling.** Ostwald, M. M., Smith, M. L. & Seeley, T. D. "The behavioral regulation of thirst, water collection and water storage in honey bee colonies." *Journal of Experimental Biology* 219(14), 2156–2165 (2016). https://doi.org/10.1242/jeb.139824
    Plain-language: Science News, "To douse hot hives, honeybee colonies launch water squadrons" (Susan Milius, July 2016): colonies without water couldn't stop the hive reaching up to 44 °C. https://www.sciencenews.org/article/douse-hot-hives-honeybee-colonies-launch-water-squadrons
12. **Hefting.** Lifting or tipping the hive to judge its food stores is standard beekeeping practice. https://theapiarist.org/weighty-matters/

## Links

- Repo (public, MIT): TODO
- 30 s vertical video: TODO
- 2 min walkthrough: TODO
- Team: TODO names + GitHub handles (Rome / Shruichan, dartiros / realdarter, Nam Nguyen / NamNgHH, sushirito, Adityash: TODO handle)
