"""HONEYMOON local speech-to-text: faster-whisper behind one HTTP endpoint (the Hive's fallback or primary STT).

    uv run python server.py                      # base.en on 127.0.0.1:8242 (cuda if available, else cpu int8)

    POST /transcribe   body = raw PCM16 little-endian, 16 kHz, mono (no header)
                       -> {"text", "ms", "audio_s", "model", "device"}
    GET  /health       -> {"ok": true, "model", "device", "compute_type", "load_s"}

Env: WHISPER_MODEL (base.en), WHISPER_PORT (8242), WHISPER_BIND (127.0.0.1), WHISPER_DEVICE (auto|cpu|cuda),
WHISPER_COMPUTE (default: float16 on cuda, int8 on cpu), WHISPER_THREADS (cpu threads, default min(8, cores)),
WHISPER_BEAM (1), WHISPER_PROMPT (keyterm prompt), WHISPER_MAX_S (30: longer bodies are cut to the first N s).
"""

from __future__ import annotations

import json
import logging
import os
import signal
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

import numpy as np

log = logging.getLogger("stt-whisper")

SAMPLE_RATE = 16000
DEFAULT_PROMPT = "Beeswax, smoker, syrup, hive, wasps, hornet, pebbles, fire, put it out."
MIN_SAMPLES = SAMPLE_RATE // 10  # < 0.1 s is a click, not speech


def _env_int(name: str, default: int) -> int:
    try:
        value = int(os.environ.get(name, "").strip() or default)
        return value if value > 0 else default
    except ValueError:
        log.warning("ignoring bad %s, using %s", name, default)
        return default


class Engine:
    """Loads the model once; transcriptions run one at a time (CTranslate2 already uses every thread it is given)."""

    def __init__(self) -> None:
        from faster_whisper import WhisperModel  # heavy import: after logging is set up

        import ctranslate2

        self.model_name = os.environ.get("WHISPER_MODEL", "").strip() or "base.en"
        want = (os.environ.get("WHISPER_DEVICE", "").strip() or "auto").lower()
        has_cuda = ctranslate2.get_cuda_device_count() > 0
        self.device = "cuda" if (want == "cuda" or (want == "auto" and has_cuda)) else "cpu"
        self.compute_type = os.environ.get("WHISPER_COMPUTE", "").strip() or ("float16" if self.device == "cuda" else "int8")
        self.threads = _env_int("WHISPER_THREADS", min(8, os.cpu_count() or 4))
        self.beam = _env_int("WHISPER_BEAM", 1)
        self.prompt = os.environ.get("WHISPER_PROMPT", "").strip() or DEFAULT_PROMPT
        self.max_samples = _env_int("WHISPER_MAX_S", 30) * SAMPLE_RATE
        self._lock = threading.Lock()

        t0 = time.monotonic()
        log.info("loading %s on %s (%s, %d threads) ...", self.model_name, self.device, self.compute_type, self.threads)
        self.model = WhisperModel(self.model_name, device=self.device, compute_type=self.compute_type, cpu_threads=self.threads)
        # Warm-up decode with VAD off (VAD would drop the silence and skip the decoder): the first call is slow.
        tone = (np.sin(np.arange(SAMPLE_RATE) * 2 * np.pi * 220 / SAMPLE_RATE) * 3000).astype(np.int16)
        self.transcribe(tone.tobytes(), vad=False)
        self.load_s = round(time.monotonic() - t0, 2)
        log.info("ready in %.1fs", self.load_s)

    def transcribe(self, pcm: bytes, vad: bool = True) -> dict[str, Any]:
        t0 = time.monotonic()
        samples = np.frombuffer(pcm[: len(pcm) - len(pcm) % 2], dtype="<i2")[: self.max_samples]
        text = ""
        if samples.size >= MIN_SAMPLES:
            audio = samples.astype(np.float32) / 32768.0
            with self._lock:
                segments, _info = self.model.transcribe(
                    audio,
                    language="en",  # no language-detection pass
                    beam_size=self.beam,
                    temperature=0.0,  # no temperature-fallback re-decodes: latency matters more than rescue
                    initial_prompt=self.prompt,
                    condition_on_previous_text=False,
                    without_timestamps=True,
                    vad_filter=vad,
                    vad_parameters={"min_silence_duration_ms": 300},
                )
                text = " ".join(s.text.strip() for s in segments).strip()  # the generator decodes lazily
        if text.strip(" .,!?").lower() == self.prompt.strip(" .,!?").lower():
            text = ""  # near-silence can echo the prompt back
        return {
            "text": text,
            "ms": round((time.monotonic() - t0) * 1000),
            "audio_s": round(samples.size / SAMPLE_RATE, 2),
            "model": self.model_name,
            "device": self.device,
        }


class Handler(BaseHTTPRequestHandler):
    engine: Engine
    protocol_version = "HTTP/1.1"
    server_version = "stt-whisper"

    def _json(self, status: int, body: dict[str, Any]) -> None:
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self) -> None:  # noqa: N802
        if self.path.split("?")[0] in ("/health", "/healthz"):
            e = self.engine
            self._json(200, {"ok": True, "model": e.model_name, "device": e.device, "compute_type": e.compute_type, "load_s": e.load_s})
        else:
            self._json(404, {"error": "not found"})

    def do_POST(self) -> None:  # noqa: N802
        if self.path.split("?")[0] != "/transcribe":
            self._json(404, {"error": "not found"})
            return
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = -1
        limit = self.engine.max_samples * 2 * 4  # generous: the engine cuts to WHISPER_MAX_S anyway
        if length < 0 or length > limit:
            self.close_connection = True
            self._json(413 if length > limit else 400, {"error": f"body must be 0..{limit} bytes of PCM16"})
            return
        body = self.rfile.read(length) if length else b""
        try:
            result = self.engine.transcribe(body)
        except Exception as exc:  # never take the server down for one request
            log.exception("transcribe failed")
            self._json(500, {"error": str(exc), "text": ""})
            return
        log.info("%.2fs audio -> %d ms: %r", result["audio_s"], result["ms"], result["text"][:80])
        self._json(200, result)

    def do_OPTIONS(self) -> None:  # noqa: N802
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "content-type")
        self.send_header("Content-Length", "0")
        self.end_headers()

    def log_message(self, fmt: str, *args: Any) -> None:  # requests are logged in do_POST
        pass


def main() -> int:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    port = _env_int("WHISPER_PORT", 8242)
    bind = os.environ.get("WHISPER_BIND", "").strip() or "127.0.0.1"
    try:
        Handler.engine = Engine()
    except Exception:
        log.exception("could not load the whisper model")
        return 1
    server = ThreadingHTTPServer((bind, port), Handler)
    server.daemon_threads = True

    def stop(*_: Any) -> None:
        threading.Thread(target=server.shutdown, daemon=True).start()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    log.info("listening on http://%s:%d (POST /transcribe, GET /health)", bind, port)
    server.serve_forever()
    server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
