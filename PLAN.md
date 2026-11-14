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
| Concept | **v2 "HONEYMOON" below is PROPOSED (Rome, Fri 11 PM).** Rome confirms it with the team and logs "accepted". Until then the old "theme-park ride" plan is replaced, not deleted: see git history |

## ⏰ Do these tonight (everyone)

- **Someone logged into the live dashboard:** copy the 2026 **Tracks & Submit** text, rules, and judging criteria into this file.
- **Check Discord** for a team/repo/equipment form. In 2025 it was due Fri 9:30 PM.
- **Everyone:** click "Join hackathon" on [Devpost](https://immerse-the-bay-2026.devpost.com/) and accept the project invite later. Accept the GitHub repo invite.
- **Glasses owner:** update Mentra Live software tonight on a strong network (~150–640 MB). In MentraOS, set the button mode to **apps**, not camera.
- **Shopping (Sat 7:30–9 AM):** order the mat print tonight for Sat 8 AM pickup. Buy plants that **actually bloom purple in November**: salvia / Mexican bush sage, asters, rosemary in flower. Lavender is usually not blooming now. See the [props list](#physical-props--shopping).

---

## Concept v2 (PROPOSED): HONEYMOON — The Bees Are Leaving for the Moon

This concept came out of a 6-pitch design panel scored by 4 judge personas (Stanford XR generalist, ManageXR, Visko, a skeptical XR tech lead), then red-teamed. Last year's Grand Prize ("Where's My Boo?") won with a tight, funny, handmade, polished MR story, so this is built to that standard.

**Logline:** The bees built a honey-fueled rocket and are leaving Earth for the Moon. One judge in the **Quest** is *in the bees' world*: their real table is a launch pad. The other wears **Mentra Live** glasses and is *the field agent in ours*, the only one with hands and a camera. Every real bee flower they find is a reason to stay, and the bees in the headset fly to it.

**Beeswax** is a stingerless **drone** ("no stinger, no job, so they made me union rep") and shop steward of **Local 20,000**, for the ~20,000 bee species. He's loud, petty, keeps a spreadsheet, and is *right*: lawns, spray, mites. He speaks in union procedure: "Motion denied." "Noted for the minutes." "Grievance filed." "I'll allow it."

**Why it wins:** a one-sentence goal (stop the launch), a villain who is right, the event theme as the twist, and three moments a judge retells at lunch:
1. **The beeline.** "The other judge looked at a real flower and the bees in my headset flew to it."
2. **The hum.** A giant hornet, a fist, and everyone humming to cook it, which is exactly what Japanese honeybees really do.
3. **"TAKE ME TO THE MOON!"** Beeswax launches alone, lands, says "…not one flower," watches Earth rise, and crashes back.

**Two devices = asymmetric co-op (the honest reason):** only GOGGLES (Quest) can *see* which flower is food (Bee Vision), only GLASSES can *scan* it, and only GLASSES hears the hornet coming. If a judge asks "why not the Quest's own camera?": the headset wearer *is* the bee, the scout has to move, and in a classroom the scout walks the schoolyard while the hive stays on the table.

### The table

- **Earth mat:** printed ~1 m × 0.6 m on foam board, with **4 drop-in pot sockets** (holes cut for identical 4-inch nursery pots, pre-placed) and **2 calibration dots**. The operator does a 10-second two-dot pinch calibration at every reset. **No MRUK, no room scan.**
  - Socket 1: red roses · Socket 2: purple bee flower · Socket 3: clover / rosemary in flower · Socket 4: pebble water dish
- **Side tray:** empty spray bottle labeled "BUG-B-GONE ULTRA", an absurd neon plastic flower with a price tag, a paper-tube bee hotel.
- **HONEYMOON-1** honeycomb rocket on the mat, with a honey-jar rack and a countdown.
- **The window** sits *just behind the far edge of the mat, in the same field of view* (~0.8 m wide, tilted toward the Quest judge). Sign: **"YOUR YARD, TO A BEE"**: no flowers means nothing to eat, which looks like the Moon. Every good scan greens it a stage.

### Demo script (4:00 of play, with headset fitting done before 0:00; T-0 is fixed at 3:50 and never cut)

| Time | Beat | Who does what |
|---|---|---|
| pre | Hook | Teammate in a "union mediator" lanyard: "The bees are leaving Earth. You have four minutes to talk them out of it." Quest on first. GLASSES gets a finger on the button and does one practice press ("Testing. You smell like coffee.") |
| 0:00–0:15 | Meet Beeswax | Swarm pours onto GOGGLES' **open palm**. "…Traitors." Opener ≤60 words, including "more than half the managed colonies lost in one year" |
| 0:15–1:05 | Roses miss + Bee Vision | Mediator hands GLASSES the **roses** first. Scan → "We can't see red. Is this a funeral?" → **Bee Vision auto-fires for 8 s**, then a **poke** on the bee-goggles Beeswax offers: reds go dark, violets glow, UV nectar guides appear on the sockets |
| 1:05–1:40 | **The beeline** | GLASSES scans the **purple flower** → polaroid develops on the rocket → **150 glowing bees beeline onto that socket** → honey jars carried *out* of the rocket → window greens a stage → ear: "They're on your flower. Don't panic." |
| 1:40–2:10 | *(only if ahead)* Off-list roast | GLASSES scans anything (badge, coffee, shoe) → **live Claude roast** in union language. This is the proof the AI is live |
| 2:10–3:00 | **Hornet + the hum** | Ear-only: "HORNET. Behind Goggles. YELL!" → **fist** balls the swarm → "Now HUM!" (pitch-detected on the Quest) → 46 °C → "STOP, you're cooking the girls!" → cartoon puff. Ear: "Not all wasps are bad. Just that guy." |
| 3:00–3:15 | One plea | GLASSES says why the bees should stay → transcribed onto Beeswax's clipboard → "Noted for the minutes." |
| 3:15–3:50 | Buffer | |
| 3:50–4:30 | **T-0: the turn** | One jar left: "my *personal* jar, enough for one drone." "TAKE ME TO THE MOON!" Launches alone → passthrough dims, Unity Moon + Earthrise → "…Not one flower. Only planet with flowers." → crash back onto the real table |
| 4:30–4:50 | Splashdown | Ear callback of what *this* group did ("You brought me a funeral rose first. Still mad.") + a real wildflower seed packet |

- **Solo is the default.** Ask every group "which track are you judging?"
  - **Glasses-first** (ManageXR / AI judges): the judge wears the glasses and scans while a teammate wears the Quest, and the cast monitor shows the bees landing. Then the Quest goes onto the judge for the hum + finale.
  - **Duo script** for pairs. Whoever wears prescription glasses takes the Quest.
- **If running long:** cut the off-list roast first. Never cut Bee Vision, the beeline, the hum, or T-0.

### What's live vs pre-built

- **Live:** passthrough + mat calibration, hand gestures, the hum (on-device pitch), the glasses camera → Claude verdict (incl. off-list roasts), glasses transcription (plea), the swarm landing on the real socket, cross-device events, game memory → callback.
- **Pre-built:** all 3D assets, ~45 Beeswax lines pre-rendered in one voice, verdict lines for every prop, SATIE stems, Orbis clips, the beat machine, the Beat Board.
- **Rules:** live AI changes *what is said and shown*, never *which beat comes next*. Every live step has an operator twin, and the Devpost says which is which.

### Facts (spoken: only these 4, each tied to an action)

1. Bees see violet/blue/UV, not red. Red reads as dark → **Bee Vision**.
2. Neonicotinoid-type sprays impair bees' navigation and homing → **spray tantrum**.
3. Japanese honeybees kill giant hornets by balling around them and heating them to ~46 °C (bees tolerate a few degrees more) → **the hum**. (Ono et al., *Nature* 1995)
4. US beekeepers lost more than half their managed colonies in one recent year (Auburn-led survey, 2024–25) → **opener**.

Fact cards / Devpost only: most bees are solitary and many nest in the ground (bee hotel); pebble water dishes; not all wasps are bad (*fig wasps pollinate figs; some figs can't exist without them*: do **not** say "every fig"). Hedge every number ("about", "more than half"). Someone other than the line writer checks each line against its source.

### Sponsors (each one load-bearing)

- **ManageXR:** AI on smart glasses that perceives the real world and changes what happens, in an education setting (classroom kit: one Quest hive per table, glasses scouts on the schoolyard walk). Privacy defaults: camera only on a button press, photos never saved, roasts objects not people.
  - **Do NOT enroll our only Quest in ManageXR.** Meta's no-fix bug MAN-54 breaks MRUK/scene apps under MDM, and enrollment needs a factory reset. Ask their table for a loaner Quest to show kiosk/relaunch.
- **Visko Orbis (via Reactor):** the window. **Floor = a Unity 3D crater diorama that greens** (no network). Orbis clips or a live stream are a layer on top. **Go/no-go Sat 11 AM.** No go → drop the Visko track for Social Good.
- **Attractor Labs SATIE:** two hive moods (angry roar / calm hum) + the hornet drone + stings. Pitch it only as much as it's really used.

### MUST / SHOULD / COULD / CUT

- **MUST (feature freeze Sat 6 PM):**
  - Quest: mat sockets + two-dot calibration, rocket + jars + countdown (display-only), swarm (haul / beeline / land / U-turn / palm / ball; **120–180 bees, 3–4 cm, emissive**), Beeswax + pre-rendered lines + captions, Bee Vision (poke + auto 8 s), hornet + **pitch-based hum** + hidden operator slider, window floor (Unity diorama) + clips, T-0 finale (Unity Moon + Earth).
  - Glasses: button scan → verdict → line in ear, off-list roast, ear asides, hornet warning, plea.
  - Both: **Unity is the only beat clock** (offline timers finish every beat), Beat Board, both solo variants.
- **SHOULD:** live Orbis layer, SATIE moods, scrcpy cast to a monitor facing the glasses judge, chill-bees mode (distance clamp + volume), recall-check number from playtesters.
- **COULD:** live Orbis "earth if we leave" morph, ManageXR lesson pack, LED verdict colors, QR backup decode, "MISSION 1 OF 12" locked mission board teaser (Almond Road Trip, Varroa, Pesticide Drift, Wildfire Season/firebreak, Hornet Front, CHAOS MODE: HIVE TYCOON), spectator page.
- **CUT:** Quest speech-to-text, push-to-talk, palm-flip gesture, quote-back TTS, double ending, controllers, live 3D generation.

### Gates

- **Fri ~1:30 AM, scan gate (Rome):** 20 glasses presses on the hotspot. If **p90 press → verdict audio ≤ 6 s**, keep photo + Claude. If not, go **speech-first**: the glasses judge says what they hold and the transcript gives an instant verdict, the photo still makes the polaroid, and the operator can force a verdict. (Research: Mentra photos currently take **3.5–13.7 s**.)
- **Sat 11 AM, Orbis gate:** live stream holds ≥10 fps in the Quest window for 5 min → keep live as a layer + Visko track. Otherwise ship clips/diorama + Social Good.
- **Sat 6 PM:** feature freeze. First full run with strangers + one solo run, stopwatch on.
- **Sun 7:30 AM:** build lock (sideload APK kept). **Sun ~8:30 AM:** Devpost + 30 s vertical video submitted.

## Roles

- **M**, 3D modeler: Beeswax, worker bee, rocket, jars, bloom patches, hornet, thermometer, window frame, Moon + Earth, polaroid frame. Also Orbis clip capture and SATIE stems (no code).
- **U1**, Unity MR systems: passthrough + two-dot calibration, gestures (palm, fist, poke), Bee Vision LUT, hum detector, beat machine, network client hookup.
- **U2**, Unity content + feel: swarm states, Beeswax controller (fly-to, look-at, mouth from audio, captions), polaroid develop, beeline, window, T-0 finale.
- **R**, Rome: Hive backend, Mentra Live app, Unity client scripts (`unity/BeeswaxClient`), Claude, voice + pre-rendered lines, Beat Board, Orbis relay, hosting, Devpost.

## Who's doing what

*Claim a task: put your name in, set the status (open / claimed / in progress / blocked / done), and add a log line. Keep the blank lines between tasks. They stop two people's edits from colliding.*

- **Plan doc + concept v2 + research update**: owner **Rome**: done (proposed, needs team OK)

- **Hive backend** (`hive/`: event bus, scan pipeline, Claude verdicts, voice, Beat Board): owner **Rome**: in progress

- **Mentra Live app** (`glasses/`: button scan, ear lines, plea): owner **Rome**: in progress

- **Unity client scripts** (`unity/BeeswaxClient/`: bus, polaroid, room voice, Orbis window, optional hum detector): owner **Rome**: in progress

- **Orbis relay + fallback clips** (`hive/orbis-relay/`): owner **Rome**: claimed

- **Beeswax voice (ElevenLabs Voice Design) + ~45 pre-rendered lines**: owner **Rome**: claimed (line script needs a writer: anyone funny?)

- **Quest Unity project setup** (Unity 6, OpenXR + Meta XR feature group, Meta XR 209.0.0 Building Blocks, passthrough, hand tracking, boundaryless, floor origin): owner *open (U1)*

- **Two-dot mat calibration + socket positions**: owner *open (U1)*

- **Gestures: open palm, fist, poke (no palm-toward-face poses)**: owner *open (U1)*

- **Bee Vision passthrough LUT + UV nectar guides**: owner *open (U1)*

- **Hum detector (pitch-based, 80–500 Hz) + 46 °C thermometer + operator slider**: owner *open (U1, Rome can supply HumDetector.cs)*

- **Beat machine (Unity = only clock, offline timers, accepts Hive/Board requests)**: owner *open (U1)*

- **Swarm (120–180 instanced bees: haul, beeline, land, U-turn, palm, ball)**: owner *open (U2)*

- **Beeswax controller (fly-to, look-at, mouth from audio, captions)**: owner *open (U2)*

- **Polaroid develop + rocket/jars/countdown**: owner *open (U2)*

- **Window (Unity crater diorama floor + Orbis/clip layer)**: owner *open (U2)*

- **T-0 finale (dim, launch, Moon + Earthrise, crash back)**: owner *open (U2)*

- **3D assets (see Roles → M)**: owner *open (M)*

- **SATIE sounds (2 moods, hornet drone, stings, launch, Moon silence)**: owner *open (M or whoever)*

- **Orbis clip capture in the Reactor sandbox**: owner *open (M)*

- **Props run + mat print + seed packets**: owner *open*

- **Copy 2026 tracks/rules from the live dashboard into this file**: owner *open: urgent*

- **Pick 2 tracks + final team name (Sat 9:00 AM)**: owner **everyone**

- **Devpost write-up, 30 s vertical video, 2 min walkthrough**: owner *open*

## Physical props & shopping

2× purple bee flower in bloom (salvia / Mexican bush sage / aster), red roses (potted or cut in a pot), clover or flowering rosemary, pebbles + shallow dish, 4 identical 4-inch nursery pots, paper straws + a can (bee hotel), empty spray bottle + a "BUG-B-GONE ULTRA" label, neon plastic flower, mat print (~1 m × 0.6 m) + foam board + box cutter, ~30 wildflower seed packets + "HONEYMOON: plant me" stickers, union-mediator lanyard, beekeeper hat (optional), power banks, a travel router or a dedicated hotspot.

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
- **Mentra Live:** no display. The old `@mentra/sdk` / AppServer / ngrok is **retired**: ignore tutorials and AI suggestions that use them. Use a **miniapp** (`@mentra/miniapp`, Bun/TS, beta) that runs on the phone, with `session.transcription`, `camera.takePhoto()` → signed URL (~30 min), `speaker.speak/play/createStream`, buttons/touch, `led`, `fetch`/WebSocket. iPhone means Mentra App 3.3, so `@mentra/miniapp@3.3.0`. Dev: laptop + iPhone on the same network (iPhone hotspot), `bun dev` → scan the QR in the Mentra App. Demo: `mentra-miniapp release`. Docs index: https://docs.mentraglass.com/llms.txt
- **Orbis = Reactor** (`reactor/visko-orbis-stable`): ~$0.58/min while connected, WebRTC, 832×480 @ 18 fps, first frame ~3–4 s, prompt changes morph in 2–4 s, 5 concurrent sessions per account, clips expire in 24 h. No Unity SDK, so a Python `reactor-sdk==1.14.0` relay sends JPEGs to Unity. Tune prompts in the sandbox: https://www.reactor.inc/sandbox?model=visko-orbis-stable. Keep prompts non-violent (moderation ends the session).
- **Quest (U1/U2):** Unity 6 (6000.0.66f2+), Unity OpenXR + Meta XR feature group, `com.meta.xr.sdk.core` / `com.meta.xr.mrutilitykit` / `com.meta.xr.sdk.interaction.ovr` **209.0.0** via Building Blocks + Project Setup Tool. Floor/stage origin (a system recenter must not move the mat). Declare boundaryless. Bees with Burst + `Graphics.RenderMeshInstanced`. Meta XR Audio (`com.meta.xr.sdk.audio` 209.0.0) as the spatializer. **Avoid** Voice SDK, VFX Graph, colocation. Pre-grant mic permission. Check scrcpy casting of passthrough early.
- **Voice:** one ElevenLabs Voice-Designed Beeswax voice for everything. Scripted lines are pre-rendered and shipped inside the Unity build and the miniapp. Live lines (off-list roasts) are streamed.
- **Claude:** Haiku 5.5 or Sonnet 5.5 for scan verdicts (benchmarked on our props, Rome picks), structured JSON output with fixed enums. Unity only ever executes known enums.
- **SATIE:** use it as a sound *designer* through its MCP (library/Freesound pulls are free, takes ~3¢). Import as **mono, Vorbis** clips and spatialize in Unity. Generated takes on a free account may not be OK in a public repo: ask the Attractor reps.
- **Rules (2025, until 2026's are copied in):** 30 s vertical demo video, public repo under **MIT**, paid AI services cited in the README (≤$20 in 2025), Devpost + git snapshotted at 9:01 AM.

### Event contract (Hive ↔ Quest ↔ glasses ↔ Beat Board)

Source of truth: `hive/src/protocol.ts`. If you change it, log it here.

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
- Fri 11 PM (Rome, proposed): concept v2 HONEYMOON replaces the theme-park ride, Mentra Live replaces XREAL, Quest speech-to-text is cut, Beeswax's voice is an ElevenLabs designed voice, scan verdicts use Haiku 5.5 or Sonnet 5.5.
- Equipment: OpenBCI excluded. Keys never go in the repo.

## Open questions

- Which Quest is it: 3 or 3S? (A 3 has better passthrough and depth.)
- 2026 tracks, criteria, and rules (dashboard). Do sponsor prizes count toward the 2-track limit?
- Judging format: pairs or solo? Open hall or booths?
- Orbis: do the Visko keys from Discord work on reactor.inc, and what's the balance?
- SATIE: runtime API or authoring only? Can generated takes go in a public repo?
- Who writes Beeswax's ~45 lines? (Rome drafts v1, the funniest person edits.)

## Team

| Name | GitHub | Focus |
|---|---|---|
| Rome | [Shruichan](https://github.com/Shruichan) | Hive backend, glasses app, voice, Unity client |
| dartiros | [realdarter](https://github.com/realdarter) | |
| Nam Nguyen | [NamNgHH](https://github.com/NamNgHH) | |
| | [sushirito](https://github.com/sushirito) | |

## Live log

*Append below, newest last. Format: `- YYYY-MM-DD HH:MM PT · Name (add "agent" if an AI wrote it) · message`. Address someone with "→ Name". Never edit or delete other people's lines.*

- 2026-11-13 21:45 PT · Rome (agent) · Created this doc from the kickoff meeting notes plus Rome's answers: Beeswax does both scout + coach on both devices, commands an angry swarm, theme-park framing, free-form Orbis "show me". Added AGENTS.md (rules for AI agents) and a union merge for this file. Invited NamNgHH and sushirito as collaborators, so accept your invites. → everyone: claim tasks above, and fill in names for "Speaker 1" and "Speaker 4".
- 2026-11-13 23:10 PT · Rome (agent) · **Concept v2 "HONEYMOON" proposed** (6-pitch design panel + judge personas + red team) and the plan rewritten around the real hardware: one Quest, Mentra Live (no display), iPhone. Research notes are in "Tech plan". Rome claimed: Hive backend, Mentra app, Unity client scripts, Orbis relay, voice. → everyone: read Concept v2 and the "Do these tonight" list, then claim a Unity/3D task. → Rome: confirm the concept with the team and log "accepted".
