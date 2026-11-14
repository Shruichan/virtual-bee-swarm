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
| Tracks | Pick **2** by Sat 9:00 AM. Leading candidates: **ManageXR** ("bring AI to the physical world with smart glasses") and **Visko** (living worlds). The full 2026 list is on the event's live dashboard |
| Devices | **Meta Quest 3S** (we own one). **Smart glasses: unknown until the equipment draw.** We ranked XREAL > Snap Spectacles > Quest 2, and the draw is random with one item per team |
| Demo | About 5 minutes, fully scripted, judges wear our headset |

## The pitch

**Beeswax** is a big, funny AI bee who takes you on a ~5-minute **theme-park ride about saving bees**. You talk to him and he changes the world around you. He plants meadows, brings in hives, builds firebreaks, and commands a **swarm of angry bees** against the hornets that prey on them. Along the way you learn why bee populations are declining and what actually helps. Ask him to *show you* anything (real beekeepers at work, the world through a bee's eyes) and Visko's Orbis generates it live.

The same Beeswax runs on two devices:
- **Quest 3S:** mixed reality, where your room turns into a meadow.
- **Smart glasses:** see-through AR in the real world. Beeswax looks at real plants with you ("is this bee-friendly?") and coaches you through real beekeeping steps.

**Judging angles:** education + social good, AI in the physical world, a living world, and humor (*if it's funny, they'll remember us*). Every feature is narrow, but it should *imply* a much bigger park. Think of a Baldur's Gate 3–style illusion of choice that has one real path.

## Demo script v0 (Quest 3S)

The beats on screen are pre-made and scripted. The AI only chooses *which* pre-made beat plays, and "show me" is the only free-form generation. Times are rough.

| Time | Beat | Player says (example) | What happens | Bee lesson |
|---|---|---|---|---|
| 0:00 | Arrival | — | The room fills with a wilted, empty meadow over passthrough. A few tired bees. Beeswax flies in and introduces himself | Bee populations are declining worldwide |
| 0:45 | Plant | "Beeswax, plant wildflowers here" | Bees carry seeds in and flowers bloom where you point. The soundscape comes alive (SATIE) | Bees need diverse flowers, and habitat loss is the big threat |
| 1:30 | Home | "Put a beehive there" | The swarm hauls a hive into place | Most native bees are solitary, and many nest in the ground |
| 2:15 | Threat | "Swarm, attack!" | Hornets arrive. Beeswax's **angry swarm** goes after them, and you can swat too | Invasive hornets prey on honeybees, but not all wasps are bad |
| 3:00 | Fire | "Build a firebreak" | Smoke on the horizon, then a firebreak appears and protects the meadow | Wildfires wipe out forage and nests (California angle) |
| 3:45 | Show me | "Show me how real beekeepers check a hive" / "Show me the world as a bee" | An Orbis window opens and streams a live, generated world | (Visko track moment) |
| 4:30 | Silly + scope | "Build me a bee fun house" | A ridiculous bee fun house drops behind you. Beeswax: "That's all for today! The rest of the park opens soon…" A park map shows locked rides | Implied scope: pollination ride, hive management, tycoon/chaos mode |

**Glasses version** (same Beeswax, lighter): Beeswax floats in your view and you talk to him the same way. **Scout:** look at a real flower or plant and he says whether it's bee-friendly and why. Bring a potted flower or printed photos to the judging table. **Coach:** step-by-step beekeeping tips with Orbis "show me" clips. **QR reference card** (temporary) on the table to anchor content where the device lacks good scene understanding.

## Tech plan

```
 Quest 3S app (Unity)        Glasses app (depends on the draw)
   mic, gaze/hand point        mic, camera frame
          \                        /
           v                      v
        ┌──────────────────────────────┐
        │  Hive backend (keys live     │  speech-to-text → Claude → one command
        │  here, never in the apps)    │  + Beeswax's spoken line
        └──────────────────────────────┘
          |            |             |
        Claude     Visko Orbis   SATIE (Attractor Labs)
```

- **Command set (fixed; the game owns it):** `plant_flowers(where)`, `place_hive(where)`, `build_firebreak(where)`, `swarm_attack(target)`, `spawn_fun_house()`, `bee_fact(topic)`, `show_me(prompt)` → Orbis, and `deflect(line)` for off-topic or unsafe requests (Beeswax answers with a joke). New spawnables get added here *and* in Unity.
- **Claude:** one Messages call per utterance on `claude-opus-5-5` at low effort, returning structured JSON (`action` from an enum, `args`, `say`). Forced `tool_choice` isn't supported on this model, so use structured outputs or `tool_choice: auto` with strict tools. Handle `stop_reason: "refusal"` and use the `fallbacks` parameter. Measure latency, and if it's too slow, the backend owner decides on a faster model.
- **Free-form "show me":** Claude rewrites the request into a safe, bee-themed Orbis prompt and refuses anything inappropriate. Judges *will* try to break it.
- **Speech-to-text and Beeswax's voice:** TBD. The scripted beats can use pre-recorded or pre-generated lines, with live text-to-speech only for free-form replies.
- **Quest app:** Unity 6 + Meta XR SDK (passthrough, scene understanding, hands). Same stack as last year's Grand Prize winner.
- **Glasses app:** stays a thin client until the draw. **XREAL** means Unity, reusing the Quest project. **Spectacles** means Lens Studio (TypeScript) as a small separate app. **Nothing** means a phone AR or Quest-passthrough stand-in.
- **Where the backend runs:** TBD (backend owner). Orbis video should stream straight to the device, not through the backend.
- **Secrets:** API keys live only in the backend's `.env` (ignored by git). Clients never hold keys. Keys are shared on Discord.
- **Sponsor tech:**
  - **Visko Orbis** is a "live model" that generates and streams worlds in real time. Several keys were collected via Discord. *Unknown:* API shape, stream format, latency, and how to show it in Unity. That's a spike task.
  - **Attractor Labs SATIE** is an AI audio director for 3D worlds, with $10 of free credit per account. Use it for the nature soundscape, bee buzz that scales with the swarm, hornet whine, and fire. Their judges may sit on general tracks too. Rome's machine has a SATIE MCP server configured for agents.
  - **ManageXR** is a device-management platform. *Idea:* deploy our build to the Quest and the glasses through ManageXR so their judges see it.

## Assets

Beeswax (big bee: idle, fly, talk, point) · worker bees (swarm/boids) · angry-swarm mode · hornets · wilted → blooming meadow and wildflowers · beehive · firebreak + smoke/fire · bee fun house · Orbis window/portal · park map with locked rides · subtitles for Beeswax's lines · QR reference card · SATIE soundscape. Simple models are quick to make, so check online assets before modeling from scratch. Avoid huge binaries in git.

## Bee facts (verify and cite before the demo)

- Most of the world's leading food crop types benefit from animal pollination (FAO/IPBES). Get the exact figure and source.
- Main threats: habitat loss, pesticides, Varroa mites (honeybees), climate change, and invasive hornets (e.g., the yellow-legged hornet, *Vespa velutina*, which hunts honeybees).
- Most native bees are solitary, and many nest in bare ground, so leaving some bare soil helps.
- Native wildflowers that bloom across the seasons help the most.
- Most wasps are beneficial. Our villain is *invasive hornets*, not all wasps, and Beeswax should say so.

## Who's doing what

*Claim a task: put your name in, set the status (open / claimed / in progress / blocked / done), and add a log line. Keep the blank lines between tasks. They stop two people's edits from colliding.*

- **Plan doc + live log + agent rules**: owner **Rome**: done (keep it current, everyone)

- **Voice + AI backend** (speech-to-text → Claude → command, Orbis + SATIE calls, key handling): owner **Rome**: in progress

- **Smart-glasses research** (compare options, decide the glasses stack once the draw is known): owner *Speaker 1 in the meeting notes, name?*: open

- **SATIE nature audio** (account, credits, soundscape): owner *Speaker 4 in the meeting notes, name?*: open

- **Quest Unity project setup** (Unity 6 + Meta XR SDK, passthrough, scene, build to the 3S): owner *open*

- **Beeswax character** (model, animations, subtitles, voice playback): owner *open*

- **Bee swarm + angry swarm behavior**: owner *open*

- **Hornets + swarm attack beat**: owner *open*

- **Spawnable assets** (wildflowers, hive, firebreak, fun house + bee-delivery animation): owner *open*

- **Orbis spike** (get one live generated world showing in Unity, measure latency): owner *open*

- **Glasses app** (after the draw): owner *open*

- **QR reference card / spatial anchoring**: owner *open*

- **Demo script, pitch, Devpost write-up, demo video**: owner *open*

- **Pick 2 tracks + final team name (Sat 9:00 AM)**: owner **everyone**

## Timeline (PT)

- **Fri night (now → ~2 AM):** repo + Unity project skeleton, backend skeleton, Orbis + SATIE API spikes, asset list, demo script v0 (above).
- **Sat 9:00 AM:** team name + 2 tracks locked. Glasses draw known. Glasses plan decided.
- **Sat morning:** core loop on Quest (voice → command → pre-made spawn), Beeswax in scene, swarm.
- **Sat afternoon:** hornets, firebreak, Orbis window, SATIE audio, glasses app.
- **Sat evening:** integration + full demo run-throughs. **Feature freeze ~11 PM.**
- **Sun 12–7 AM:** polish, record the demo video, write the Devpost entry.
- **Sun ~8 AM: submit**, with a buffer before the 9:00 AM cutoff. Then rehearse for judging at 10.

## Decisions

- Theme: an educational bee-conservation XR game with an AI bee assistant. The firefighting idea was rejected as too common.
- Demo: ~5 minutes, fully scripted, funny, educational, hinting at much bigger scope. Assets are pre-made and the AI picks which ones appear.
- Beeswax is a bee who works on both devices as scout + coach, and he commands an angry bee swarm. Framing: a theme-park ride about bee conservation.
- Orbis: users can ask Beeswax to generate and *show* them things live (with safeguards).
- Equipment: XREAL first, Snap Spectacles second, Quest 2 next. OpenBCI excluded: little stock, hard to make meaningful, and it fits neither sponsor track. Mentra Live, Meta glasses, and Pico 4 were deprioritized.
- Keys never go in the repo.

## Open questions

- Which glasses do we get? → decides the glasses stack.
- Orbis API: stream format, latency, per-key limits, and whether Unity can show it live.
- Speech-to-text and voice choices for Beeswax.
- Where the backend runs at the venue.
- Final 2 tracks and team name.

## Team

| Name | GitHub | Focus |
|---|---|---|
| Rome | [Shruichan](https://github.com/Shruichan) | Voice + AI backend |
| dartiros | [realdarter](https://github.com/realdarter) | |
| Nam Nguyen | [NamNgHH](https://github.com/NamNgHH) | |
| | [sushirito](https://github.com/sushirito) | |

## Live log

*Append below, newest last. Format: `- YYYY-MM-DD HH:MM PT · Name (add "agent" if an AI wrote it) · message`. Address someone with "→ Name". Never edit or delete other people's lines.*

- 2026-11-13 21:45 PT · Rome (agent) · Created this doc from the kickoff meeting notes plus Rome's answers: Beeswax does both scout + coach on both devices, commands an angry swarm, theme-park framing, free-form Orbis "show me". Added AGENTS.md (rules for AI agents) and a union merge for this file. Invited NamNgHH and sushirito as collaborators, so accept your invites. → everyone: claim tasks above, and fill in names for "Speaker 1" and "Speaker 4".
