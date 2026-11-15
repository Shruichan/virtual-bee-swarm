# scout-vision

Live Bee Scout recognizer: glasses video (MediaMTX RTSP) -> newest frame -> DINOv2-small embedding -> nearest
reference photos per class -> `ScoutFrame` JSON (see `hive/src/scout-protocol.ts`) -> Hive `POST /scout/frame`.
HTTP API on :8242 for health, booth enrolment and the slides screen test.

## Setup (Apple Silicon, uses torch MPS)

```bash
cd hive/scout-vision
uv sync
uv run python fetch_web_refs.py          # ~40 Wikimedia Commons photos per label + ~100 background, data/web/ + credits.csv
#   hand-check data/sheets/*.jpg, list junk as "<label>/<file>" lines in data/web/junk.txt, then:
uv run python fetch_web_refs.py --split  # 20 per label -> refs/<label>/web_*, the rest -> data/test/<label>/
```

Models download on first use (DINOv2-small ~90 MB, the default; open_clip ViT-B-32 laion2b ~600 MB for `--backbone clip`).

## Run

```bash
# glasses stream via MediaMTX, posting to the scout Hive
uv run python worker.py --hive http://127.0.0.1:8241 --token "$HIVE_TOKEN"
# just print JSON lines
uv run python worker.py --print
uv run python worker.py --source webcam:0 --print
uv run python worker.py --source data/test.mp4 --print          # file, paced in real time (--loop to repeat)
uv run python worker.py --source data/test/honey_jar --print    # image folder, 2 s per image
```

Options: `--crops grid15|grid7|center` (default grid15: the centre square + 14 zoomed crops, the best crop wins),
`--ema 0.3` (score smoothing, 0 = off), `--detector module:function` (boxes from the detect lane, scored as extra
crops and sent as `boxes`), `--backbone dino|clip`, `--fps 10`, `--temp`, `--topk`, `--port 8242`, `--no-api`.
Env: `SCOUT_CROPS`, `SCOUT_EMA`, `SCOUT_DETECTOR`, `SCOUT_CAPTURE=opencv` (RTSP via OpenCV instead of the ffmpeg pipe).
`HIVE_TOKEN` may also live in `hive/scout-vision/.env` or `hive/.env`.

Screen test calibration: `uv run python calibrate_from_session.py --slides` copies the slide photos into
`refs/<label>/slide_*.jpg`, then `curl -X POST localhost:8242/reload`. `calibrate_from_session.py data/sessions/<s>`
also enrols crops of recorded glasses footage (`glasses_*`), but on session s1 that made things worse (see replay).

## API (port 8242, CORS *)

```bash
curl localhost:8242/health
curl -X POST localhost:8242/enrol -d '{"label":"honey_jar","seconds":4}'   # look at the jar while this runs
curl -X POST localhost:8242/enrol -d '{"label":"background","seconds":6}'  # look around the booth: table, people, walls
curl -X POST localhost:8242/clear -d '{"label":"honey_jar"}'              # drop that label's live_* refs
curl -X POST localhost:8242/reload                                         # re-scan refs/ after editing it by hand
curl localhost:8242/slides.json                                           # held-out photos, served at /img/<label>/<file>
```

Booth calibration: enrol each of the six objects for 3-5 s from a couple of angles, then 6-10 s of background.

## Test

```bash
uv run python eval.py --sweep                       # held-out accuracy, confusion, background FP, ms/frame, both backbones
uv run python make_test_video.py                    # data/test.mp4 + data/test_truth.json
uv run python worker.py --source data/test.mp4 --print --no-api > data/test_frames.jsonl
uv run python replay_check.py data/test_frames.jsonl data/test_truth.json
```

Replay a recorded glasses session (`data/sessions/<s>/glasses_<wall_ms>.mkv` + `events.jsonl` from the Hive SSE)
through the recognizer and a copy of the game rule, before vs after, current vs proposed thresholds:

```bash
uv run python replay_eval.py data/sessions/s1      # countdown window; --all for the whole session
```

RTSP smoke test without the glasses (never publish to the `scout` path; `scouttest` is the test path):

```bash
uv run python worker.py --source rtsp://127.0.0.1:8554/scouttest --print --no-api &
ffmpeg -re -i data/test.mp4 -c copy -f rtsp -rtsp_transport tcp rtsp://127.0.0.1:8554/scouttest
```

## Run it (whole laptop side, one command)

From the repo root (macOS; needs `brew install mediamtx ffmpeg uv` and bun):

```bash
ops/scout-up.sh             # MediaMTX + scout Hive + this worker + glasses dev server, health check, opens /scout and /scout/slides
ops/scout-up.sh --no-open   # same, no browser
ops/scout-up.sh --qr        # also open the glasses dev QR (Mentra app > Settings > Miniapp Developer Settings > Scan)
ops/scout-down.sh           # stop only what scout-up.sh started (ops/run/*.pid)
```

It is safe to rerun: anything whose port is already listening is reported as "already running" and left alone.
First run creates `hive/.env` (random `HIVE_TOKEN`, `PORT=8241`, `PUBLIC_BASE_URL` = this laptop's LAN ip) and
rewrites `glasses/src/config.local.ts` (scoutMode, streamUrl, hosts) for the current ip; both are git-ignored and the
token is never printed. Missing `refs/` are fetched from Wikimedia (minutes), or copied with
`SCOUT_REFS_FROM=<other checkout>/hive/scout-vision`. Logs: `ops/logs/<name>.log`.

Knobs: `SCOUT_NO_GLASSES=1`, `SCOUT_NO_VISION=1`, `SCOUT_PRACTICE=0` (default 1), `SCOUT_LAN_IP=`, and every port
(`SCOUT_HIVE_PORT`, `SCOUT_VISION_PORT`, `SCOUT_RTMP_PORT`, `SCOUT_RTSP_PORT`, `SCOUT_WEBRTC_PORT`, `SCOUT_GLASSES_PORT`, ...;
see the top of `ops/scout-up.sh`). Non-default MediaMTX ports get a generated config in `ops/run/`.
