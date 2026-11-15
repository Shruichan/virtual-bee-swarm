# Live Bee Scout

Live Bee Scout is a glasses-only scavenger hunt with an AI character in your ear. A visitor wears Mentra Live
glasses and walks a short trail of six printed station cards (a native flower, a honey jar, bug spray, a bee
hotel, a pebble water dish, a beehive). The glasses stream video to a laptop, a vision worker recognizes each
card, and Beeswax the bee reacts in the visitor's ear with pre-recorded lines. The visitor can also just ask
Beeswax a question out loud and get a short spoken answer. Everyone else watches the judge view on a big screen.

This folder is self-contained: everything the scout needs (server, glasses app, vision worker, voice lines,
art, launcher) lives under `bee-scout/`. It does not use or change anything elsewhere in the repo.

## Architecture

```
Mentra Live glasses ──RTMP──> MediaMTX ──RTSP──> vision worker (python, :8242)
   (glasses/ miniapp,                              - OCR of the trail-sign label on each card
    scout mode)                                    - SIFT picture matcher on the six real-photo cards
        ^                                           │ POST /scout/frame
        │ lines, audio, state                       v
        └──────────────────────────────── Hive scout game (bun + hono, :8241)
                                             - scout state machine (hive/src/scout*.ts)
                                             - Beeswax voice lines (hive/lines/scout_*.json + scout_audio/)
                                             - Ask Beeswax: Claude answers, ElevenLabs speaks (scout-ask*.ts)
                                                    │
                                    MediaMTX ──WebRTC──> judge view  /scout/show   (big screen)
                                                         operator    /scout        (dashboard)
```

| Path | What it is |
|---|---|
| `hive/` | Bun/Hono server. Scout game `src/scout*.ts`, Ask Beeswax `src/scout-ask*.ts`, judge view `src/scout-show.ts`, operator view `src/scout-dashboard.ts`, lines `lines/scout_*.json`, audio `lines/scout_audio/`, art `scout-assets/`. The rest of the Hive is the shared server it plugs into. |
| `hive/scout-vision/` | Python vision worker (`worker.py`). Card matcher + OCR in `train/`, card photos in `detect/card_images/`, card names/signs in `detect/cards.json`. |
| `hive/scout-eval/` | Offline eval of the recognizer on recorded sessions. |
| `glasses/` | Mentra miniapp with scout mode (streams video, plays Beeswax, listens for questions). |
| `ops/` | `scout-up.sh` / `scout-down.sh` launcher and the MediaMTX config. |
| `docs/scout-booth.md` | Booth layout, camera geometry, run-of-show. |

## Prerequisites

- macOS (OCR uses Apple Vision through pyobjc).
- `bun`, `uv`, `ffmpeg`, `mediamtx` (all via Homebrew: `brew install oven-sh/bun/bun uv ffmpeg mediamtx`).
- `bee-scout/hive/.env` (git-ignored) with:
  - `ELEVENLABS_API_KEY` and `ELEVENLABS_VOICE_ID` (Beeswax's voice for Ask Beeswax),
  - `HIVE_TOKEN` (any long random string; the launcher creates one if missing),
  - Claude access for Ask Beeswax: `ANTHROPIC_API_KEY` in `.env` or in your shell.
  See `hive/.env.example`. Never commit real keys: the repo is public.
- Mentra Live glasses and the Mentra app on an iPhone.

## Run it

```bash
bee-scout/ops/scout-up.sh --qr     # MediaMTX + Hive + vision worker + glasses dev server, health checks, opens the pages
bee-scout/ops/scout-down.sh        # stops only what scout-up.sh started
```

`scout-up.sh` installs dependencies on first run (`bun install`, `uv sync`; the first `uv sync` downloads torch, and
the vision model downloads on the first worker start, so give it a few minutes), writes the git-ignored
`glasses/src/config.local.ts` with the laptop's LAN IP and token, starts everything that isn't already running,
checks health, and prints the URLs (tokens masked). With `--qr` it opens the glasses dev QR: in the Mentra app go to
Settings > Miniapp Developer Settings > Scan Miniapp QR Code. The Hive starts in practice mode (`SCOUT_PRACTICE=1`).

The vision worker runs as:

```bash
cd bee-scout/hive/scout-vision
SCOUT_CAPTURE=ffmpeg uv run python worker.py --match detect/card_images --ocr --no-markers \
  --hive http://127.0.0.1:8241        # token from the HIVE_TOKEN env var
```

Useful overrides (see the top of `ops/scout-up.sh`): `SCOUT_HIVE_PORT`, `SCOUT_VISION_PORT`, `SCOUT_RTMP_PORT`,
`SCOUT_RTSP_PORT`, `SCOUT_WEBRTC_PORT`, `SCOUT_NO_GLASSES=1`, `SCOUT_NO_VISION=1`, `SCOUT_LAN_IP`, `--no-open`.
Logs go to `ops/logs/`, pid files to `ops/run/` (both git-ignored).

## Printable cards

```bash
cd bee-scout/hive/scout-vision
uv run python -m detect.make_cards                 # -> hive/scout-vision/out/cards/station_cards.pdf
uv run python -m detect.make_cards --pdf ~/Downloads/bee-scout-print-cards.pdf   # extra copy
```

US Letter landscape, one card per page: the real photo on top and a big trail-sign label below it for OCR.
Print at 100% / actual size, matte paper.

## Judge view (`/scout/show?token=...`)

- **Start your adventure** (or Enter / Space) leaves the landing screen and starts a fresh journey.
- **Restart your journey** (click twice to confirm) goes back to the landing screen.
- **M**: Mac sound on/off (play Beeswax's voice on the laptop too).
- **G**: mute/unmute the glasses' audio.
- **B**: show/hide the detection boxes over the video.
- **F**: fullscreen.

## Network lessons

- Use the **iPhone hotspot** for the laptop and the phone. Venue Wi-Fi tends to isolate clients or block RTMP,
  so the glasses can't reach the laptop.
- The glasses app gets the laptop's IP baked in. **A new laptop IP needs a new QR**: rerun
  `bee-scout/ops/scout-up.sh --qr` after switching networks and scan again (or set `SCOUT_LAN_IP`).
- Keep the phone close to the glasses and the laptop; the stream degrades fast on a weak hotspot.

## Tests

```bash
cd bee-scout/hive && bun install && bunx tsc --noEmit && bun test test/scout*.test.ts
cd bee-scout/glasses && bun install && bun run typecheck && bun --no-env-file test test/scout.test.ts
```
