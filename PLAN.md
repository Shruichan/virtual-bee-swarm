# Virtual Bee Swarm — Plan & Team Log

> **📣 READ THIS FIRST: this file is how the team talks.**
> It's our communication doc *and* the outline of the game. Every teammate and every AI agent working in this repo should:
>
> 1. **Pull and read this whole file** before starting anything (`git pull --rebase`).
> 2. **Claim** a task in [Who's doing what](#whos-doing-what) before starting it, and update its status when it changes.
> 3. **Log** every plan change, decision, finished piece, blocker, or question in the [Live log](#live-log) at the bottom. Append only, newest last.
> 4. **Push right away.** Make small, frequent commits to this file. It only works if it's current.
> 5. **Never put API keys or secrets here.** The repo is public. Keys stay on Discord and in `.env` files, which git ignores.
>
> Several people and agents push to this file at once. `.gitattributes` gives it a union merge, so a pull never stops on a conflict here. Instead, both sides' lines are kept. If you see duplicated lines after a pull, tidy them up and log that you did. Rules for agents are in [AGENTS.md](AGENTS.md).

---

## At a glance

| | |
|---|---|
| Event | Immerse the Bay 2026 (Stanford XR). Theme: *Take Me To The Moon* |
| Hard deadline | **Hacking ends and Devpost closes Sun Nov 15, 9:00 AM PT.** Judging 10 AM–12 PM, closing ceremony 1:30 PM |
| Team name | "Virtual Bee Swarm" or "Angry Virtual Bee Swarm"? **Finalize by Sat 9:00 AM** |
| Hardware | **One Meta Quest (3 or 3S: which one?)**, **Mentra Live** glasses (camera + mics + speakers, **no display**), and Rome's **iPhone** as the glasses' phone |
| Tracks | Pick 2. Leading candidates: **ManageXR** ("bring AI to the physical world with smart glasses") and **Visko** (living worlds), with **Social Good** as the fallback. The official 2026 text is on the login-gated live dashboard, so someone copy it here |
| Concept | **v3 = best of both:** Adityash's hive escape (4 stations + live Orbis future window + voice what-ifs) + HONEYMOON (Beeswax the union-rep drone, Moon stakes and ending, hornet hum). Glasses get fitted in after the Quest game works |

## ⏰ Do these tonight (everyone)

- **Someone logged into the live dashboard:** copy the 2026 **Tracks & Submit** text, rules, and judging criteria into this file.
- **Check Discord** for a team/repo/equipment form. In 2025 it was due Fri 9:30 PM.
- **Everyone:** click "Join hackathon" on [Devpost](https://immerse-the-bay-2026.devpost.com/) and accept the project invite later. Accept the GitHub repo invite.
- **Glasses owner:** update Mentra Live software tonight on a strong network (~150–640 MB). In MentraOS, set the button mode to **apps**, not camera.
- **Shopping (Sat 7:30–9 AM):** order the mat print tonight for Sat 8 AM pickup. Buy plants that **actually bloom purple in November**: salvia / Mexican bush sage, asters, rosemary in flower. Lavender is usually not blooming now. See the [props list](#physical-props--shopping).

---

## 🗂️ Delegation plan (Sat 2 AM → Sun 9 AM)

**Starting areas, not fixed jobs.** Most of us are doing dev, and who does what will shift as the night goes. Take the next open task from **Who's doing what** when you finish one, and claim it there. **Adityash has the most time**, so he picks up whatever is most behind or blocking. The areas below are just where each person starts, plus who has the final say if two ideas collide:

| Who | Starts on / has the final say on | First thing to do |
|---|---|---|
| **Nam** (Unity lead + headset) | `unity/HoneymoonQuest`: the scene, the hive placement on the table, hand interactions, the beat clock, building to the Quest, on-device testing | Enable Quest developer mode. When the base project lands (~3 AM), pull it, open it in Unity 6000.2.9f1 (6000.3 LTS also works), build to the headset |
| **Adityash** (gameplay + design, floats to whatever's behind) | The 4 stations' feel, swarm behavior, Beeswax's movement + captions, the meadow window, embers, escape/score, the Moon ending, playtests | Read Concept v3 + `pitch/script-and-build-asks.md`, claim the U2 tasks, work in **prefabs** (not the scene) |
| **dartiros** (3D) | Every model: Beeswax, worker bee, hive (+ door), smoker, honey jar, syrup bottle, water bowl, pebble dish, wasps, giant hornet, HONEYMOON-1 rocket, portal frame, Moon + Earth, hexagons | Beeswax + worker bee + hive first (they're on screen the whole time). Specs below |
| **Rome** (+ agents) | Hive backend, voice (Harry), speech-to-text + what-if intents, Orbis (relay, prompts, clips), Unity client scripts + base project, Beat Board, glasses app, SATIE sounds, **pitch** (Devpost, 30 s video, presenting) | Unity license sign-in on pop-os; Reactor key; SATIE sign-in |

**Handoff rules (to avoid wrecking each other's work):**
- **One person at a time edits `Honeymoon.unity`** (Unity scene files merge badly; Nam by default, say in the log before you take it). Everyone else builds **prefabs** in `Assets/Honeymoon/Prefabs/` and the scene editor drops them in.
- **Models (dartiros → Unity):** FBX or GLB, **meters**, Y-up, pivot at the base (Beeswax: center of body), one material each if possible, textures ≤1024 px. Budgets: Beeswax ≤15k tris, worker bee ≤300 tris (there are ~150 of them), hive ≤5k, everything else ≤3k. Put them in `unity/HoneymoonQuest/Assets/Honeymoon/Art/<name>/` named as in the asset list; the placeholder prefab slots are listed in `README-gameplay.md`.
- **No file over 50 MB in git** (GitHub rejects >100 MB). Orbis clips stay small (720p H.264).
- Pull before you start, push small and often, log in PLAN.md when you hand something off.

**Timeline (PT):**
- **Sat 2–3 AM:** base Unity project + placeholder systems land (agents). Nam builds it to the headset. dartiros starts Beeswax/bee/hive. Adityash claims U2 tasks.
- **Sleep in two shifts:** dartiros + Adityash 3:00–7:30 AM, Nam + Rome 4:30–9:00 AM (agents keep working). Nobody skips sleep entirely.
- **Sat 9 AM:** lock team name + 2 tracks (draft; Visko vs Social Good is final at 11). Sponsor tables: Visko (Reactor credits), Attractor (SATIE credits), ManageXR (loaner Quest?).
- **Sat 11 AM:** Orbis gate (live ≥10 fps for 5 min + a scene change within ~4 s).
- **Sat 12 PM — Milestone 1:** on the headset, all 4 stations work with placeholders, Beeswax talks (Harry), and "What if a fire starts?" → fire + embers → "Put it out" works.
- **Sat 6 PM — feature freeze:** real models swapped in, full run with two strangers + one solo run, stopwatch on.
- **Sat 6 PM–1 AM:** polish only (feel, timing, sound, captions). Capture video footage (Quest recording + a phone over-the-shoulder shot).
- **Sun 1–5 AM:** staggered sleep. Rome finishes Devpost + video edit.
- **Sun 7:30 AM:** build lock (keep a sideload APK). **Sun 8:30 AM:** Devpost + video submitted. **9:00:** closed.

## Concept v3 (CURRENT): HONEYMOON — Save the Hive or the Bees Leave for the Moon

Best of both: **Adityash's hive escape** (4 stations, the live Orbis "future window" steered by voice, practical beekeeping, a score, a map of more hives) as the spine, plus **HONEYMOON** (Beeswax the union-rep drone, the "we're leaving for the Moon" stakes and theme twist, the hornet hum, the Moon ending). The full v2 write-up is in git history if you want to borrow more.

**Logline:** Beeswax's hive is falling apart and the union has voted: fix it in four minutes or the whole colony moves to the Moon. You're in the Quest with the hive on your real table. Four things are killing it. Every choice you make shows up live in a window into the meadow's future, and you can *ask* that future questions out loud.

**Beeswax** is a stingerless **drone** ("no stinger, no job, so they made me union rep") and shop steward of **Local 20,000**, for the ~20,000 bee species. Loud, petty, keeps a spreadsheet, talks in union procedure: "Motion carried." "Grievance filed." "Noted for the minutes." "I'll allow it." His rocket, **HONEYMOON-1**, is parked next to the hive, already fueled with honey.

**Why it wins:**
- One-sentence goal (fix four things, the door opens) with real stakes (or they leave for the Moon), and a score: "Hive saved in 2:47."
- **Visko's dream demo:** a live Orbis world that changes direction mid-stream because of what you did and what you *said*, including the fire that spills embers into the room.
- Three lunch-table moments: **the honey mistake** ("store honey can kill a hive?!"), **the fire** ("I said 'what if a fire starts' and the window caught fire and embers came into the room"), and **"TAKE ME TO THE MOON!"** (he goes anyway, finds no flowers, comes back).
- Real beekeeping, true facts, and a hint that there's a lot more game (six more hives).

### The future window (live Visko Orbis)
A glowing portal next to the hive shows a **live Orbis stream of the meadow's future**. Good choices make it settle and bloom; bad ones turn it sick and grey. Grey enough and it starts to look like the Moon ("That's your yard, to a bee, without us."). The player can steer it by voice ("What if a fire starts?" / "Put it out").
- **Reality check:** Orbis prompt changes take **2–4 s** to show up (a morph, not a cut). Beeswax always has a line ready to cover the wait, and **every window state has a recorded fallback clip** that plays if the stream isn't up. Test the "fire" prompt in the Reactor sandbox early (moderation can end a session).

### Demo script (~4:15)

| Time | Beat | What happens |
|---|---|---|
| 0:00–0:15 | Hook | The hive sits on the real table; HONEYMOON-1 is parked next to it with a countdown. Beeswax: "The union voted. Fix my hive in four minutes or we're moving to the Moon." |
| 0:15–0:35 | Meet Beeswax + the future window | Beeswax flies in: "Four things are killing my hive. Fix them and the door opens." The portal opens beside the hive, showing the meadow live. Voiceover (video only): "That window is a live Visko Orbis stream. It shows the future of every choice you make." |
| 0:35–1:05 | **Station 1: Smoker** | Angry bees swarm out. Choices: swat, spray water, smoker. Smoker → the bees calm down, first hexagon lights up. Window: the meadow settles, bees head out to forage. Beeswax: "Smoke covers up our alarm signal and makes us fill up on honey. Also, our alarm smells like bananas. Don't eat one near my house." |
| 1:05–1:45 | **Station 2: Tip the hive, then feed it** | "Is there enough food in there?" The player tips the hive with their hands and it lifts way too easily. "We're starving." Choices: store-bought honey jar or sugar syrup. Honey → the window shifts *mid-stream* and the colony gets sick: "…Grievance filed. Store honey can carry a disease that wipes out hives. Spores last for decades." Syrup → the scene recovers, second hexagon. (For the video, show the honey mistake first.) |
| 1:45–2:20 | **Station 3: Wasps at the door** | Wasps buzz at the entrance. Choices: make the entrance bigger or smaller. Smaller → guard bees block the wasps, third hexagon. Window: the wasps give up. Beeswax: "Smaller door, fewer guards needed to hold it." **If ahead (+30 s):** a giant hornet forces its way in anyway → make a **fist** to ball the swarm around it → "Now HUM!" (the Quest mic hears the hum) → 46 °C → "STOP, you're cooking the girls!" Japanese honeybees really do this. |
| 2:20–2:55 | **Station 4: Heat wave** | The hive glows red. Choices: deep bowl of water, shallow dish with pebbles, or nothing. Pebbles → bees land and drink, fourth hexagon. Beeswax: "We spread water drops in the hive and fan our wings. Bee air conditioning." |
| 2:55–3:20 | **The "what-if" bang** | The player asks out loud: "What if a fire starts?" Beeswax: "Don't. Say. That." The window's meadow catches fire (2–4 s morph), then **3D embers spill out of the portal into the real room** (Unity VFX, instant). "Put it out!" → the stream recovers. This is the clip people remember and Orbis's real selling point: steering a live world with your voice. |
| 3:20–3:40 | **Escape** | All four hexagons glow, the hive door opens, the swarm pours out into the room, and the window grows into a full forest bloom. Score card: "Hive saved in 2:47." Beeswax: "Motion carried. The union votes to stay." |
| 3:40–4:15 | **T-0: Take me to the Moon** | "…I'm still going. Ticket's non-refundable." "TAKE ME TO THE MOON!" HONEYMOON-1 launches with only Beeswax aboard. The window swells into the Moon: grey dust, Earth rising. "…Not one flower. Only planet with flowers." He crashes back onto the real table, dusty. "Same time tomorrow? There are six more hives." A map flashes with locked levels (farmland, city rooftop, desert). |

- **If running long:** skip the hornet hum first. Never cut the honey mistake, the fire, or the Moon.
- **Choices are physical where we can make them:** grab the smoker and squeeze, lift/tip the hive with your hands, pick up the jar. Floating buttons are the fallback.

### Facts (all true, each tied to an action)

- Smoke masks the alarm pheromone (which smells like bananas) and makes bees gorge on honey, so they calm down → **Station 1**.
- Beekeepers "heft" the hive to check food stores. Feed **sugar syrup**, never store-bought honey: it can carry American foulbrood spores, which stay viable for decades → **Station 2**.
- An entrance reducer makes the door easier to defend: fewer guards are needed to hold it → **Station 3**.
- Japanese honeybees kill giant hornets by balling around them and heating them to ~46 °C; the bees survive a few degrees more (Ono et al., *Nature* 1995) → **hornet hum** (if ahead).
- Bees need shallow water with landing spots (they drown in deep water), and they cool the hive by spreading water and fanning their wings → **Station 4**.
- Optional opener stat: US beekeepers lost more than half their managed colonies in one recent year (Auburn-led survey, 2024–25). Hedge every number.

### Glasses (Mentra Live): adapt later
The glasses are a second priority: we build the Quest game first and fit the glasses in after. Options that already work with the backend:
- **A second mic** for the what-if questions (glasses transcription → same voice pipeline).
- **Beeswax in your ear** for asides and hints.
- **Real props:** a second player looks at a REAL prop on the table (honey jar vs. syrup bottle, pebble dish vs. bowl) and that makes the station choice. This is the strongest pitch for the ManageXR track ("AI in the physical world with smart glasses").

### Sponsors
- **Visko Orbis (via Reactor):** the future window, steered live by choices and voice. **Go/no-go Sat 11 AM** for live; recorded clips per window state are the floor either way.
- **Attractor Labs SATIE:** hive mood (angry roar → calm hum), the fire, the wasps, the launch, the Moon's silence.
- **ManageXR:** depends on how far the glasses get. **Do NOT enroll our only Quest in ManageXR** (Meta bug MAN-54 breaks MRUK/scene apps under MDM, and enrollment needs a factory reset).
- Tracks: **Visko** (strongest fit now) + **ManageXR** if the glasses earn it, otherwise **Social Good**. Confirm on the dashboard.

### MUST / SHOULD / COULD / CUT
- **MUST (freeze Sat 6 PM):** hive on the real table, Beeswax + pre-rendered lines + captions, 4 stations with choices + hexagons, the future window (live Orbis **or** recorded clips per state) reacting to each choice, voice what-if (fire + put it out) with an operator button as backup, Unity embers out of the portal, escape + score card, T-0 Moon ending, locked-levels map, Beat Board. **Unity is the only beat clock** (offline timers finish every beat).
- **SHOULD:** hornet hum (pitch-detected), physical choices (squeeze smoker, tip hive, grab jar), SATIE moods, glasses as second mic + ear.
- **COULD:** glasses real-prop choices, more what-ifs ("what if it rains?", "what if we plant clover?"), scrcpy cast, spectator page.
- **CUT:** live 3D generation, controllers.

### Gates
- **Sat 11 AM, Orbis gate:** live stream holds ≥10 fps in the Quest window for 5 min and a prompt change shows within ~4 s → live + Visko track. Otherwise clips + Social Good.
- **Sat 6 PM:** feature freeze, full run with strangers, stopwatch on.
- **Sun 7:30 AM:** build lock (sideload APK kept). **Sun ~8:30 AM:** Devpost + 30 s vertical video submitted.

## Roles

- **M**, 3D modeler: Beeswax, worker bee, hive (tippable) + entrance + hexagon panel, smoker, honey jar + syrup jar, water bowl + pebble dish, wasps + giant hornet, HONEYMOON-1 rocket, portal frame, Moon + Earth, score card, locked-levels map. Also Orbis clip capture and SATIE sounds (no code).
- **U1**, Unity MR systems: passthrough + placing the hive on the real table, hand interactions (squeeze smoker, tip hive, grab jar, fist), station/beat machine, hum detector, network hookup (`unity/BeeswaxClient`).
- **U2**, Unity content + feel: swarm (angry → calm, guards, pour-out, hornet ball), Beeswax controller (fly-to, look-at, mouth from audio, captions), the future window (Orbis texture + clip fallback), embers out of the portal, escape + score, T-0 Moon ending.
- **R**, Rome: Hive backend (voice → speech-to-text → intents, Orbis steering, Beeswax's voice), Orbis relay, Unity client scripts, Beat Board, hosting, glasses app (later), Devpost.

## Who's doing what

*Claim a task: put your name in, set the status (open / claimed / in progress / blocked / done), and add a log line. Keep the blank lines between tasks. They stop two people's edits from colliding.*

- **Plan doc + concept v3 (merge of Adityash's plan + HONEYMOON)**: owner **Rome**: done

- **Hive backend** (`hive/`: event bus, voice → speech-to-text → intents, Orbis steering, Beeswax voice, Beat Board): owner **Rome**: in progress

- **Orbis relay + per-state fallback clips** (`hive/orbis-relay/`): owner **Rome**: in progress

- **Unity client scripts** (`unity/BeeswaxClient/`: bus, mic streaming, Beeswax voice playback, Orbis window, hum detector): owner **Rome**: in progress

- **Beeswax voice (ElevenLabs Voice Design) + pre-rendered lines**: owner **Rome**: claimed (lines need a funny editor)

- **Mentra Live app** (`glasses/`, second priority: second mic, ear lines, real-prop choices later): owner **Rome**: started, paused until the Quest game works

- **Quest Unity project setup** (`unity/HoneymoonQuest`: Unity 6000.2.9f1, opens in 6000.3 LTS too; Meta XR 209 + OpenXR, passthrough, hands, Quest player settings, BeeswaxClient wired): owner **Rome (agent)**: in progress, landing ~3 AM. Placeholder versions of every v3 system (director/beat clock, 4 stations, swarm, Beeswax, window, embers, hornet, escape, Moon, map) come with it, so U1/U2/M replace the placeholders instead of starting from zero

- **Hive placement on the real table**: owner *open (U1)*

- **Station machine (Unity = only clock: stations 1–4, what-if, escape, T-0; offline timers; accepts Beat Board requests)**: owner *open (U1)*

- **Station interactions** (smoker squeeze, tip the hive, grab a jar, door size, water choice; floating buttons as fallback): owner *open (U1)*

- **Hum detector (pitch-based, 80–500 Hz) + 46 °C thermometer + operator slider** (if-ahead beat): owner *open (U1, Rome can supply HumDetector.cs)*

- **Swarm (angry/calm, guards at the door, pour-out, hornet ball; 120–180 instanced bees)**: owner *open (U2)*

- **Beeswax controller (fly-to, look-at, mouth from audio, captions)**: owner *open (U2)*

- **Future window (portal: Orbis texture + clip fallback per state) + embers spilling into the room**: owner *open (U2)*

- **Hexagons + escape + score card + locked-levels map**: owner *open (U2)*

- **T-0 Moon ending (launch, Moon + Earthrise, crash back)**: owner *open (U2)*

- **3D assets (see Roles → M)**: owner *open (M)*

- **SATIE sounds (hive moods, wasps, fire, launch, Moon silence)**: owner *open (M or whoever)*

- **Orbis prompts + clip capture per window state** (calm, forage, sick, recovered, wasps leave, drinking, fire, fire out, full bloom, Moon): owner *open (M + Rome)*

- **Props run** (only if we use real props: honey jar, syrup bottle, pebble dish, bowl, a smoker or stand-in): owner *open*

- **Copy 2026 tracks/rules from the live dashboard into this file**: owner *open: urgent*

- **Pick 2 tracks + final team name (Sat 9:00 AM)**: owner **everyone**

- **Devpost write-up, 30 s vertical video, 2 min walkthrough**: owner **Rome (agent)**: drafts in progress in `pitch/` (devpost.md, video-30s.md, walkthrough-2min.md, tracks.md, judge-qa.md, demo-runbook.md); a human edits and records

## Physical props & shopping

Optional (only if we do physical choices or the glasses real-prop mode): a jar of honey, a squeeze bottle labeled "SUGAR SYRUP", a shallow dish + pebbles, a deep bowl, a bee smoker (or a stand-in). Also: power banks, a travel router or a dedicated hotspot, a union-mediator lanyard for whoever introduces the demo.

---

## Tech plan (verified research, Fri 11 PM)

```
 Quest app (Unity)  ◄──── wss bus ────►  Hive (Bun/TS)  ◄── HTTPS/WS ──  Mentra miniapp (on iPhone) ── BT ── Mentra Live
   beat clock, swarm,                     scan pipeline, Claude,             button, photo, transcription,
   pre-rendered lines                     voice (ElevenLabs), Beat Board      ear audio
          ▲                                       │
          └──── JPEG frames ◄── Orbis relay (Python, laptop) ◄── WebRTC ── Reactor (Visko Orbis)
```

- **Hosts:** `beeswax.drori.dev` (pop-os, always on: use it tonight) and `beeswax-venue.drori.dev` (Rome's laptop at the venue: **primary on demo day**). Clients try venue first, then pop-os.
- **Dev server is LIVE (Sat 12:30 AM):** `https://beeswax.drori.dev/health` · Beat Board `https://beeswax.drori.dev/board?token=TOKEN` · spectator `…/monitor?token=TOKEN` · bus `wss://beeswax.drori.dev/bus?role=quest&token=TOKEN` · Orbis frames `wss://beeswax.drori.dev/orbis/frames?token=TOKEN` (fake test pattern until the Reactor key is in). **Get TOKEN from Rome privately**: never commit it. Unity setup: `unity/BeeswaxClient/README.md`. Running the Hive locally: `cd hive && bun install && bun run start` (tests: `bun --no-env-file test`, so a local `.env` with real keys never gets used by tests).
- **Mentra Live:** no display. The old `@mentra/sdk` / AppServer / ngrok is **retired**: ignore tutorials and AI suggestions that use them. Use a **miniapp** (`@mentra/miniapp`, Bun/TS, beta) that runs on the phone, with `session.transcription`, `camera.takePhoto()` → signed URL (~30 min), `speaker.speak/play/createStream`, buttons/touch, `led`, `fetch`/WebSocket. iPhone means Mentra App 3.3, so `@mentra/miniapp@3.3.0`. Dev: laptop + iPhone on the same network (iPhone hotspot), `bun dev` → scan the QR in the Mentra App. Demo: `mentra-miniapp release`. Docs index: https://docs.mentraglass.com/llms.txt
- **Orbis = Reactor** (`reactor/visko-orbis-stable`): ~$0.58/min while connected, WebRTC, 832×480 @ 18 fps, first frame ~3–4 s, prompt changes morph in 2–4 s, 5 concurrent sessions per account, clips expire in 24 h. No Unity SDK, so a Python `reactor-sdk==1.14.0` relay sends JPEGs to Unity. Tune prompts in the sandbox: https://www.reactor.inc/sandbox?model=visko-orbis-stable. Keep prompts non-violent (moderation ends the session).
- **Quest (U1/U2):** Unity 6 (6000.0.66f2+), Unity OpenXR + Meta XR feature group, `com.meta.xr.sdk.core` / `com.meta.xr.mrutilitykit` / `com.meta.xr.sdk.interaction.ovr` **209.0.0** via Building Blocks + Project Setup Tool. Floor/stage origin (a system recenter must not move the mat). Declare boundaryless. Bees with Burst + `Graphics.RenderMeshInstanced`. Meta XR Audio (`com.meta.xr.sdk.audio` 209.0.0) as the spatializer. **Avoid** Voice SDK, VFX Graph, colocation. Pre-grant mic permission. Check scrcpy casting of passthrough early.
- **Voice:** one ElevenLabs Voice-Designed Beeswax voice for everything. Scripted lines are pre-rendered and shipped inside the Unity build and the miniapp. Live lines (off-list roasts) are streamed.
- **Claude:** Haiku 5.5 or Sonnet 5.5 for scan verdicts (benchmarked on our props, Rome picks), structured JSON output with fixed enums. Unity only ever executes known enums.
- **SATIE:** use it as a sound *designer* through its MCP (library/Freesound pulls are free, takes ~3¢). Import as **mono, Vorbis** clips and spatialize in Unity. Generated takes on a free account may not be OK in a public repo: ask the Attractor reps.
- **Rules (2025, until 2026's are copied in):** 30 s vertical demo video, public repo under **MIT**, paid AI services cited in the README (≤$20 in 2025), Devpost + git snapshotted at 9:01 AM.

### Event contract (Hive ↔ Quest ↔ glasses ↔ Beat Board)

Source of truth: `hive/src/protocol.ts`. If you change it, log it here. **Being extended for concept v3 tonight (Rome):** station events (`station.set`, `choice.made`), voice in (mic audio frames → transcript → intent), `orbis` window states per station, and `fx` events (embers). The glasses scan messages below stay for the later glasses mode.

- **Bus:** `wss://<host>/bus?role=quest|board|monitor&token=…`, JSON text frames + binary frames.
- **Unity owns the beat clock.** The Hive and Beat Board send *requests* that Unity accepts or ignores.
- **Messages:**
  - `beat.set{n}`, `mode{duo|solo}`, `group.reset{group_id}`
  - `scan.started{}`: glasses button pressed. The Quest shows a blank polaroid + flash, and Beeswax whips around.
  - `scan.photo{}` + binary JPEG: the polaroid develops.
  - `scan.verdict{item, verdict, confidence, seen, socket, line_id | tts_id, path: claude|speech|operator, ms}`
  - `swarm.arrived{socket}` (Quest → Hive → glasses ear buzz), `hornet.cooked{overshoot}`
  - `plea{text}` → clipboard
  - `say{channel: ear|room, line_id | tts_id, caption}`. **One line, one device:** room lines play only from Beeswax's body on the Quest, ear lines only in the glasses.
  - `tts.start{id}` + binary PCM16 + `tts.end{id}` (live lines on the Quest)
  - `orbis{state: priming|ready|failed|ended, fallback_clip}`, `latency{hop, ms}`
- **Glasses → Hive (HTTPS):** `POST /glasses/scan {photoUrl, heard?}`, `POST /glasses/plea {text}`, `POST /glasses/event {…}`.
- Presses while a scan is in flight are ignored. Presses outside a scan window get "Not now, I'm monologuing."

## Decisions

- Theme: bee conservation, Beeswax the AI bee, an angry swarm, hornets ("not all wasps are bad"), pre-made assets the AI only *picks*, a scripted short demo that implies larger scope.
- Fri 11 PM: Mentra Live replaces XREAL; Beeswax's voice is an ElevenLabs designed voice; Claude calls use Haiku 5.5 or Sonnet 5.5.
- Fri 11:40 PM (Rome): **concept v3 = best of both**: Adityash's hive escape (4 stations, live Orbis future window, voice what-ifs, score, map) + HONEYMOON (Beeswax the union-rep drone, Moon stakes + ending, hornet hum). Voice comes from the Quest mic or the glasses mic, through Whisper-style speech-to-text. Glasses are fitted in after the Quest game works.
- Equipment: OpenBCI excluded. Keys never go in the repo.

## Open questions

- Which Quest is it: 3 or 3S? (A 3 has better passthrough and depth.)
- 2026 tracks, criteria, and rules (dashboard). Do sponsor prizes count toward the 2-track limit?
- Judging format: pairs or solo? Open hall or booths?
- Orbis: do the Visko keys from Discord work on reactor.inc, and what's the balance?
- SATIE: runtime API or authoring only? Can generated takes go in a public repo?
- Who writes Beeswax's lines? (Rome drafts v1, the funniest person edits.)
- How does the player talk to the Quest: hold a pinch, a controller button, or always listening with Beeswax prompting "ask me anything"?
- Which station choices are physical (squeeze, tip, grab) vs floating buttons?

## Team

| Name | GitHub | Focus |
|---|---|---|
| Rome | [Shruichan](https://github.com/Shruichan) | Backend, voice, Orbis, glasses, pitch (+ agents) |
| dartiros | [realdarter](https://github.com/realdarter) | 3D models |
| Nam Nguyen | [NamNgHH](https://github.com/NamNgHH) | Unity lead + headset |
| Adityash? | [sushirito](https://github.com/sushirito) | Gameplay content + feel, design owner (confirm GitHub) |

## Live log

*Append below, newest last. Format: `- YYYY-MM-DD HH:MM PT · Name (add "agent" if an AI wrote it) · message`. Address someone with "→ Name". Never edit or delete other people's lines.*

- 2026-11-13 21:45 PT · Rome (agent) · Created this doc from the kickoff meeting notes plus Rome's answers: Beeswax does both scout + coach on both devices, commands an angry swarm, theme-park framing, free-form Orbis "show me". Added AGENTS.md (rules for AI agents) and a union merge for this file. Invited NamNgHH and sushirito as collaborators, so accept your invites. → everyone: claim tasks above, and fill in names for "Speaker 1" and "Speaker 4".
- 2026-11-13 23:10 PT · Rome (agent) · **Concept v2 "HONEYMOON" proposed** (6-pitch design panel + judge personas + red team) and the plan rewritten around the real hardware: one Quest, Mentra Live (no display), iPhone. Research notes are in "Tech plan". Rome claimed: Hive backend, Mentra app, Unity client scripts, Orbis relay, voice. → everyone: read Concept v2 and the "Do these tonight" list, then claim a Unity/3D task. → Rome: confirm the concept with the team and log "accepted".
- 2026-11-13 23:45 PT · Rome (agent) · **Concept v3 is the plan: Adityash's hive escape + HONEYMOON, best of both.** Spine = Adityash's 4 stations + live Orbis future window + voice what-if + escape/score/map. Added from HONEYMOON: Beeswax the union-rep drone, "fix it or we move to the Moon" stakes, hornet hum (if ahead), T-0 Moon ending. Fixed one fact line (smaller door = *fewer guards needed*). Tasks rewritten for v3, so claim yours. → Adityash: thank you, check I kept your beats right. Hive backend/voice/Orbis/Unity client scripts are being built now (Rome).
- 2026-11-14 00:40 PT · Rome (agent) · **Backend v1 is in the repo and live** at beeswax.drori.dev (Hive bus + Beat Board + monitor + Orbis relay in fake mode; voice off until the ElevenLabs key). Code: `hive/`, `hive/orbis-relay/`, `unity/BeeswaxClient/` (drop-in C#, compiled clean in Unity 6000.2.9f1), `glasses/` (Mentra miniapp, later). Now adapting all of it to concept v3: stations + `choice.made`, voice what-ifs (Quest/glasses mic → speech-to-text → intents → Orbis scene + embers), per-scene fallback clips. Protocol bumps to v2; I'll log when it lands. → Unity folks: start from `unity/BeeswaxClient/README.md`; ask Rome for the token.
- 2026-11-14 01:00 PT · Rome (agent) · Started the **Unity base project** (`unity/HoneymoonQuest`, Unity 6000.2.9f1, Meta XR 209/OpenXR, Android/Quest settings, placeholder v3 systems, desktop keys for testing without a headset) and the **pitch kit** (`pitch/`). → Unity folks: if you already started your own project, say so here and we'll merge; otherwise wait ~2 h for the base and claim the systems to replace with real art/feel.
- 2026-11-14 01:25 PT · Rome (agent) · **Pitch kit drafted** in `pitch/` (devpost.md, video-30s.md, walkthrough-2min.md, tracks.md, judge-qa.md, demo-runbook.md, demo-card.md, gallery.md), reviewed by a jaded-judge pass and a fact check (fixes: heat ball is ~46–47 °C with 500+ bees, *Japanese* honeybees, so Beeswax says "My cousins in Japan do this"; from the Moon's surface Earth *hangs* over the horizon, it doesn't rise; newest Auburn loss figure is 39.9% for 2025–26 after two record years). **Game asks from the review are in `pitch/script-and-build-asks.md`**: claim them. Biggest ones: the hive lift should feel weightless + "Light, right? That's bad." (U1); "Grievances filed: N" on the score card (U2 + Rome); let the what-if happen while the meadow is still sick (U1); optional fail ending if the timer runs out (U1). One name everywhere: **the meadow window**; the player talks to **Beeswax**, not the window. Rome takes the line edits + Board fire button + off-list what-ifs.
- 2026-11-14 01:50 PT · Rome (agent) · **Claude Code tools for the team** (optional, if you use Claude Code): Unity's official plugin: `claude plugin marketplace add Unity-Technologies/unity-agent-plugin` then `claude plugin install unity@unity-agent-plugin` (skills: create-xr, craft-game-floors for juice/polish, unity-pipeline to compile/Play Mode/screenshot a running Editor, unity-cli). The Unity CLI itself: `curl -fsSL https://unity.com/install.sh | bash`. A reviewed design skill, game-design-core (plain markdown, Apache-2.0): https://github.com/omer-metin/skills-for-antigravity/tree/main/skills/game-design-core → copy into `~/.claude/skills/`.
- 2026-11-14 02:10 PT · Rome (agent) · **Delegation plan added** (top of file): Nam = Unity lead + headset, Adityash = gameplay content/feel + design owner, dartiros = 3D, Rome = backend/voice/Orbis/glasses/pitch. Only Nam edits the scene; everyone else works in prefabs. Beeswax's voice is **Harry** (ElevenLabs) and it's live on beeswax.drori.dev; all lines get pre-rendered once the script polish lands.
- 2026-11-14 02:15 PT · Rome (agent) · Delegation is flexible: the areas are starting points, most of us are on dev, take the next open task when you finish one. Adityash has the most time, so he floats to whatever is behind.
