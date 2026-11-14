# README paste-in (for a human to copy into the repo README)

> Agents only write in `pitch/`, so this is a draft. A human copies the part below the line into `README.md`, keeps the existing "plan lives in PLAN.md" line, fixes the TODOs, and commits.
> It replaces the old README's "Setup: TBD" and its out-of-date AI table (scan verdicts, stings, no Scribe, no coding assistants).
> Never paste keys, the Hive token, or `.env` contents into the README. The repo is public.

---

# HONEYMOON

A bee union rep gives you four minutes to fix his hive on your real table. Then he leaves for the Moon anyway.

Mixed reality game for Meta Quest, made at [Immerse the Bay 2026](https://immerse-the-bay-2026.devpost.com/) (Stanford XR, Nov 13–15, 2026; theme *Take Me To The Moon*) by team TODO:final team name. Devpost: TODO link.

**The plan, task claims and team chat live in [PLAN.md](PLAN.md).** AI agents: see [AGENTS.md](AGENTS.md).

## Team

TODO: names + GitHub handles (Rome / Shruichan, dartiros / realdarter, Nam Nguyen / NamNgHH, sushirito, Adityash / TODO).

## How it works

```
 Quest app (Unity 6)  <── WebSocket bus ──>  Hive server (Bun/TS)  <── HTTPS ──  Mentra miniapp (iPhone) ── Mentra Live
   beat clock, bees, hands,                   speech-to-text, intents,             (later / optional)
   pre-rendered lines, embers                 Beeswax's voice, Beat Board
          ^                                            |
          └── JPEG frames <── Orbis relay (Python) <── WebRTC ── Reactor (Visko Orbis world model)
```

- **Quest app** (`unity/`). Unity 6 with OpenXR and the Meta XR SDK (v209). Passthrough puts the hive on the real table, hand tracking does the choices. TODO: which choices are physical and which are floating buttons. The bees are instanced meshes (Burst + `Graphics.RenderMeshInstanced`). TODO: real bee count and frame rate. Embers are Unity particles, so they're instant even though the window takes a few seconds to change. **Unity is the only clock:** every beat has an offline timer, and if the Hive is down, `BeeswaxStations` plays each choice's line, window clip and effect locally, so the demo always finishes.
- **Hive server** (`hive/`). Bun + TypeScript + Hono. One WebSocket bus between the headset, the operator's Beat Board, a spectator page and the glasses. The Beat Board is a web page the operator uses to watch a run and nudge it (force the window to clips, play a line, jump a beat, reset for the next group). Message contract: `hive/src/protocol.ts`, mirrored in `unity/BeeswaxClient/Runtime/BeeswaxProtocol.cs`.
- **Voice.** The Quest mic streams 16 kHz PCM16 to the Hive while the player talks. ElevenLabs Scribe does speech-to-text, with a local faster-whisper server (`hive/stt-whisper/`) as the backup when Scribe is slow or down. A keyword match runs first ("fire", "put it out"). If it misses, Claude (Haiku 5.5 by default, `ROUTER_MODEL`) turns the sentence into one intent from a fixed list (`what_if`, `undo`, `ask`, `ack`, `unknown`) plus a what-if topic. Unity only runs things from that list. TODO: confirm whether `ask` (a short Claude-written answer in Beeswax's voice, spoken live by ElevenLabs) is on for the demo.
- **Beeswax's voice.** One ElevenLabs Voice Design voice (`hive/scripts/voice-design.ts`). Every scripted line (`hive/lines/lines.json`) is pre-rendered (`hive/scripts/prerender.ts`) and shipped inside the app, so the script never waits on the network.
- **The meadow window** (`hive/orbis-relay/`). Visko's Orbis world model, served by Reactor as `reactor/visko-orbis-stable`, generates the meadow as real-time video (832×480 at about 18 fps). There's no Unity SDK, so a Python relay (`reactor-sdk` 1.14.0) takes the WebRTC stream and sends the newest frame as a JPEG over a WebSocket to the headset. Station choices and what-ifs change the prompt mid-stream, and the meadow morphs into the new scene in 2 to 4 seconds. The headset falls back to a recorded clip for the current scene if no live frame arrives for 8 seconds. TODO: true only once real Orbis clips replace the test-pattern placeholders in `hive/orbis-relay/clips/`.
- **Sound.** Attractor Labs' SATIE, imported as mono clips and spatialized in Unity with Meta XR Audio. TODO: which sounds came from SATIE.
- **Glasses** (`glasses/`). A Mentra Live miniapp (Bun/TS) for a second player. TODO: what it does in the final demo, or "not used in the demo".
- **Hosting.** The Hive and relay run on a laptop at the venue (`beeswax-venue.drori.dev`), with an always-on home server (`beeswax.drori.dev`) as the backup. The headset tries the venue host first and switches after 3 failed connects.

## Setup

Requirements: Bun, uv (Python 3.12), Unity TODO:exact version with Android build support, a Meta Quest TODO:3-or-3S in developer mode.

Keys go in `hive/.env` (git ignores it). Variable names, no values: `ANTHROPIC_API_KEY`, `ELEVENLABS_API_KEY`, `ELEVENLABS_VOICE_ID`, `REACTOR_API_KEY`, `HIVE_TOKEN`. TODO Rome: check this list against the real `.env` names.

**Hive server**
```bash
cd hive
bun install
bun run start          # http://localhost:PORT/health, Beat Board at /board?token=…
bun run test           # tests never read your .env
```

**Orbis relay** (works with no key: it falls back to a moving test pattern)
```bash
cd hive/orbis-relay
uv sync
uv run python relay.py --fake                 # test pattern, no Reactor, no credits
uv run --env-file ../.env python relay.py     # live Orbis (bills about $0.58/min while connected)
```

**Backup speech-to-text** (start it once with network; the model downloads on first run)
```bash
cd hive/stt-whisper
uv sync
uv run python server.py
```

**Quest app**: TODO (U1): how to open the Unity project, which scene to build, and how to sideload the APK. The networking scripts and their setup are in [`unity/BeeswaxClient/README.md`](unity/BeeswaxClient/README.md).

**Glasses (optional)**: see [`glasses/README.md`](glasses/README.md).

## AI services used

Cited per the event rules. Costs are filled in before submission.

| Service | What it does in HONEYMOON | Paid by | Cost |
|---|---|---|---|
| Anthropic Claude (Haiku 5.5 by default) | Picks what the player meant from a fixed list of intents. TODO if on: short answers to off-script questions; names the real prop in a glasses photo | Team | TODO |
| ElevenLabs Voice Design + text-to-speech | Beeswax's voice: pre-rendered lines (and live answers, if `ask` is on) | Team | TODO |
| ElevenLabs Scribe | Speech-to-text for the player's voice | Team | TODO |
| faster-whisper (Whisper, run locally) | Backup speech-to-text | Free, runs on our laptop | $0 |
| Visko Orbis via Reactor | The meadow window: live stream and recorded fallback clips | TODO: team or sponsor credits | TODO |
| Attractor Labs SATIE | Sound design (hive moods, wasps, fire, launch, the Moon) | TODO: team or sponsor credits | TODO |
| MentraOS | Glasses speech-to-text and photos. TODO: delete the row if the glasses weren't used | Platform | TODO |
| AI coding assistants (Claude Code; TODO: others?) | TODO: what they wrote (server, relay, Unity client scripts, docs?) and what people did | TODO | TODO |

## Facts

Every bee fact in the game has a source, listed on our Devpost (TODO: link) and in [`pitch/devpost.md`](pitch/devpost.md) → Fact sources.

## License

MIT, see [LICENSE](LICENSE).
