# stt-whisper

Local speech-to-text for the Hive's voice what-ifs ("What if a fire starts?", "Put it out!"): faster-whisper behind
one HTTP endpoint. `hive/src/stt.ts` uses it as the fallback when ElevenLabs Scribe is slow or down, or as the only
provider when `ELEVENLABS_API_KEY` is unset or `STT_PROVIDER=whisper`.

```
Quest/glasses mic -> Hive (PCM16 16 kHz) -> POST /transcribe (raw PCM16, no header) -> {"text": ...}
```

Python 3.12 via uv. Dependencies: `faster-whisper` (CTranslate2, no PyTorch) and `numpy`.

## Run

```bash
cd hive/stt-whisper
uv sync
uv run python server.py                          # base.en on 127.0.0.1:8242 (the Hive's default WHISPER_URL)
WHISPER_MODEL=small.en uv run python server.py   # more accurate, about 2x slower on CPU
```

The model downloads from Hugging Face on first start (into `~/.cache/huggingface`), then loads once per process.
Start it before the demo: the first start needs the network.

| Env | Default | |
|---|---|---|
| `WHISPER_MODEL` | `base.en` | any faster-whisper size or a local CTranslate2 path (`tiny.en`, `small.en`, `distil-large-v3`, ...) |
| `WHISPER_PORT` / `WHISPER_BIND` | `8242` / `127.0.0.1` | |
| `WHISPER_DEVICE` | `auto` | `cuda` when CTranslate2 sees a GPU, else `cpu` |
| `WHISPER_COMPUTE` | `float16` (cuda) / `int8` (cpu) | |
| `WHISPER_THREADS` | `min(8, cores)` | CPU threads |
| `WHISPER_BEAM` | `1` | greedy is fastest; 5 is Whisper's usual beam |
| `WHISPER_PROMPT` | `Beeswax, smoker, syrup, hive, wasps, hornet, pebbles, fire, put it out.` | biases spelling toward our words |
| `WHISPER_MAX_S` | `30` | longer audio is cut to the first N seconds |

Hive side (`hive/.env`): `WHISPER_URL=http://127.0.0.1:8242/transcribe`, `STT_PROVIDER=auto|elevenlabs|whisper`.

## API

| | |
|---|---|
| `POST /transcribe` | body: raw PCM16 little-endian, 16 kHz, mono. Reply `{"text", "ms", "audio_s", "model", "device"}`. Under 0.1 s, or no speech (VAD), gives `"text": ""`. |
| `GET /health` | `{"ok": true, "model", "device", "compute_type", "load_s"}` |

Decoding: English, `vad_filter` on (silence never reaches the decoder, so no "Thank you." hallucinations on noise),
temperature 0 (no slow re-decodes), no timestamps, the keyterm prompt as `initial_prompt`. One transcription at a
time; a second request waits for the first.

## Measured (2026-11-14, Framework laptop CPU, 16 cores, int8, 8 threads, beam 1)

Test clips are synthesized speech (piper `en_US-lessac-medium`) resampled to 16 kHz mono PCM16:

| clip | base.en | small.en |
|---|---|---|
| "What if a fire starts?" (1.25 s) | 330-470 ms, exact | ~1000 ms, exact |
| "Put it out!" (0.67 s) | ~400 ms, "Put it out." | ~880 ms, same |
| "Why does the smoker calm the bees down?" (2.2 s) | ~500 ms, exact | ~940 ms, exact |
| "What happens if we plant clover?" (1.9 s) | ~480 ms, exact | ~980 ms, exact |
| fire line + pink noise + 0.7 s lead-in (2.45 s) | ~440-510 ms, exact | ~930 ms, exact |
| 1.5 s of pink noise, no speech | 7 ms, "" | 7 ms, "" |

Downloads: `base.en` 141 MB (about 12 s), `small.en` 464 MB (about 34 s). Load + warm-up: 0.7 s (base.en), 2.2 s (small.en).
Not yet tested with real voices in a loud room: try `small.en` there if base.en mishears.

## Keep it running (systemd user service)

```ini
# ~/.config/systemd/user/stt-whisper.service
[Unit]
Description=HONEYMOON stt-whisper
After=network-online.target

[Service]
WorkingDirectory=%h/immerse-the-bay/virtual-bee-swarm/hive/stt-whisper
ExecStart=%h/.local/bin/uv run --frozen python server.py
Environment=WHISPER_MODEL=base.en
Restart=always
RestartSec=2

[Install]
WantedBy=default.target
```

```bash
systemctl --user daemon-reload && systemctl --user enable --now stt-whisper
journalctl --user -u stt-whisper -f
```
