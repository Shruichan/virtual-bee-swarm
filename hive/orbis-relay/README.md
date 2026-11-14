# orbis-relay

Turns the Visko Orbis world model (served by Reactor as `reactor/visko-orbis-stable`, over WebRTC) into
**newest-only JPEG frames on a WebSocket** for the Quest window, because there is no Unity SDK.
It also records the **fallback clips** the window plays when the live stream is off or failing.

```
Reactor (WebRTC, 832x480 @ 18 fps) -> relay.py (reactor-sdk 1.14.0) -> ws /orbis/frames (JPEG, <=640 wide, <=12 fps) -> Unity
```

Python 3.12 via uv. Dependencies: `reactor-sdk==1.14.0` (wheel only, glibc 2.34+), `aiohttp`, `pillow`.
numpy is not needed: the relay takes raw BGRA frames from the SDK and encodes them with Pillow off the event loop.

## Run

```bash
cd hive/orbis-relay
uv sync
uv run python relay.py --fake                      # test pattern, no Reactor, no credits
uv run --env-file ../.env python relay.py          # live when REACTOR_API_KEY is set (else it falls back to fake and says so)
```

Fake mode serves a moving honeycomb with the stage number, scene id and a wall clock (to eyeball latency),
with a 1 s "GPU wait" and a 1.5 s first-frame delay so client state handling gets exercised.

| Env | Default | |
|---|---|---|
| `REACTOR_API_KEY` | (unset = fake) | read from the environment only, never written anywhere |
| `HIVE_TOKEN` | (unset = open) | required as `?token=` or header `x-hive-token` on everything except `/healthz` |
| `ORBIS_PORT` / `ORBIS_BIND` | `8241` / `127.0.0.1` | bind `0.0.0.0` only if Unity connects to the laptop directly |
| `ORBIS_FAKE` | `0` | `1` forces fake mode (same as `--fake`) |
| `ORBIS_IDLE_S` | `90` | stop the session after this long with no frame clients |
| `ORBIS_MAX_S` | `900` | hard cap per session; Reactor also gets `max_session_duration_seconds = MAX_S + 60` as a backstop |
| `ORBIS_MAX_W` / `ORBIS_FPS` / `ORBIS_JPEG_Q` | `640` / `12` / `70` | output frames |
| `ORBIS_PRIME_S` / `ORBIS_STALL_S` | `45` / `15` | start -> first frame deadline; ready-but-silent -> reconnect |
| `ORBIS_CONNECT_TIMEOUT_S` | `90` | connect + GPU assignment |
| `ORBIS_SCENES` | `scenes.json` | stage prompts |

## API

POSTs return immediately (the work runs in order in the background). Add `?wait=1` to block until it settles.

| | |
|---|---|
| `POST /orbis/warm` | connect early (billing starts once the session is ready; the idle stop still applies) |
| `POST /orbis/start {prompt?, seed?, stage?, passthrough?}` | connect if needed, audio off (SATIE does sound), seed, prompt, start. No prompt = the stage's scene from `scenes.json`. Restarting a running stream does `reset` first (a seed only applies to a fresh run). |
| `POST /orbis/stage {prompt?, stage?, reset?, passthrough?}` | change the prompt mid-stream. No prompt = the scene's `morph` prompt (2-4 s morph); scenes without one (moon_0, earthrise) and `reset: true` do `reset -> set_prompt -> start` (~3-4 s with no new frames). Empty body = next stage. **With no stream running it only remembers the stage; it never opens a billed session.** |
| `POST /orbis/stop` | end the session (Reactor bills per second while connected) |
| `GET /orbis/status` | `{type:"orbis", state, stage, since, fps, fallback_clip, ...}` plus `src_fps, connected, generating, prompt, seed, error, clients, session_s` |
| `WS /orbis/frames` | binary frames, each one complete JPEG (starts `FF D8`). Newest only: a slow client skips frames, it never queues them. Add `?status=1` to also get the status JSON as text frames on every change. |
| `GET /orbis/frame.jpg` | latest frame (204 when none) |
| `GET /healthz` | `ok`, no token |

States (`OrbisState` in `hive/src/protocol.ts`): `off` -> `priming` (connecting, waiting for a GPU, or waiting for the first frame) -> `ready` (frames flowing) -> `ended` (stop, idle, hard cap) or `failed` (clients fall back to clips; `error` says why).
`fallback_clip` is the clip file for the current stage (`clips/<scene id>.mp4`).

Robustness: a dropped connection is resumed on the **same** session with `reconnect()` inside Reactor's 30 s window; a stream that stalls while connected gets one reconnect cycle; no frames within `ORBIS_PRIME_S` after that, a rejected start, or an unrecoverable session (moderation, expiry) ends in `failed` and the session is disconnected so it stops billing. A rejected stage prompt keeps the old scene streaming. Orbis reports a rejected command only as a `command_error` broadcast, so each command races its reply against that broadcast and fails at once instead of waiting out the 15 s timeout. On SIGINT/SIGTERM the session is ended before exit. No error path crashes the server.

## Clips

```bash
uv run python record_clips.py --dry-run           # plan + cost estimate (~$1.50 for all 5 at 12 s)
uv run --env-file ../.env python record_clips.py  # all scenes, asks before spending (--yes to skip; refuses without --yes when stdin is not a terminal)
uv run python record_clips.py --only stage_1 --seconds 10 --pingpong
uv run python record_clips.py --fake              # placeholder clips (test pattern), no credits
```

One Reactor session for all scenes: for each, `reset`, `set_seed`, `set_prompt`, `start`, wait for the first frame, run N s,
`request_clip(N)` + download (fragmented MP4), then remux to `clips/<id>.mp4` with `ffmpeg -c copy` when it is H.264
(re-encode to H.264 otherwise, or with `--reencode`). Every frame is also captured locally, so a failed SDK recording
still gives a clip. `--pingpong` adds `<id>_pingpong.mp4` (forward + reversed) for seamless looping.
`clips/manifest.json` lists each clip's stage, seed, prompt, size and origin. `clips/` is git-ignored; Reactor deletes its copies after 24 h.

Scenes live in `scenes.json` (stages: `moon_0` grey cratered yard, `stage_1` one purple patch, `stage_2`, `stage_3` full bloom, `earthrise`).
Prompts follow Reactor's prompt guide: who/what/where + camera, 1-3 sentences, positive wording (no negations), non-violent
(moderation ends the session). Cameras are static so clips loop and a long live run does not drift. Tune them in the
sandbox (https://www.reactor.inc/sandbox?model=visko-orbis-stable) before spending on clips.

## Test

```bash
uv run python test_relay.py                       # live code path against a mock reactor_sdk (no network),
                                                  # plus a fake-mode HTTP/WS regression test (run it under `timeout`: that bug was a hang)
uv run python relay.py --fake &  uv run python smoke.py --start --stage --seconds 8
uv run python smoke.py --url https://<host> --seconds 300   # Sat 11 AM gate: >= 10 fps for 5 min (watch only)
```

## Notes

- Reactor limits: 5 concurrent sessions and 10 new sessions/min per account (burst 3). The relay never auto-creates a new session after a failure; the next `/orbis/start` does.
- The first chunk of a run has 0 frames: expect ~3-4 s from `start` to the first picture.
- Quality drifts on long runs; the 15-minute cap keeps runs short.
