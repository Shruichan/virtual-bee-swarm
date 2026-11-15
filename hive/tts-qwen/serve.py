"""Loopback-only synthesis for Beeswax's approved local voice."""

import argparse
import io
import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import soundfile
import torch
from qwen_tts import Qwen3TTSModel


MODEL = "Qwen/Qwen3-TTS-12Hz-0.6B-Base"
REFERENCE = Path(__file__).with_name("beeswax-reference.wav")
TRANSCRIPT = "Delivery for you. One hive, slightly used. Sign here. Off the record? The movers were being dramatic."
MAX_BODY = 4096
MAX_TEXT = 500


def serve(port, checkpoint):
    model = Qwen3TTSModel.from_pretrained(
        checkpoint, device_map="cuda:0", dtype=torch.bfloat16, attn_implementation="sdpa",
    )
    prompt = model.create_voice_clone_prompt(ref_audio=str(REFERENCE), ref_text=TRANSCRIPT)
    ffmpeg = os.environ.get("FFMPEG", "ffmpeg")
    subprocess.run([ffmpeg, "-version"], check=True, stdout=subprocess.DEVNULL)
    gpu = threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path != "/health":
                self.send_error(404)
                return
            self.send_response(204)
            self.end_headers()

        def do_POST(self):
            if self.path != "/speak":
                self.send_error(404)
                return
            try:
                length = int(self.headers.get("Content-Length", "0"))
                if not 0 < length <= MAX_BODY:
                    raise ValueError("invalid request size")
                payload = json.loads(self.rfile.read(length))
                text, encoding = payload["text"], payload["format"]
                if not isinstance(text, str) or not text.strip() or len(text) > MAX_TEXT:
                    raise ValueError("invalid text")
                if encoding not in ("pcm", "mp3"):
                    raise ValueError("invalid format")
            except (KeyError, ValueError, TypeError):
                self.send_error(400)
                return
            if not gpu.acquire(blocking=False):
                self.send_error(503, "Voice is busy")
                return
            try:
                waves, rate = model.generate_voice_clone(
                    text=text.strip(), language="English", voice_clone_prompt=prompt,
                    max_new_tokens=min(600, max(160, len(text) * 4)),
                )
                wave = io.BytesIO()
                soundfile.write(wave, waves[0], rate, format="WAV")
                codec = ["-ar", "16000", "-f", "s16le"] if encoding == "pcm" else ["-f", "mp3"]
                body = subprocess.run(
                    [ffmpeg, "-v", "error", "-i", "pipe:0", "-af", "atempo=1.1", "-ac", "1", *codec, "pipe:1"],
                    input=wave.getvalue(), capture_output=True, check=True, timeout=10,
                ).stdout
                if not body:
                    raise RuntimeError("Voice returned no audio")
            except Exception as exc:
                self.log_error("Synthesis failed: %s", exc)
                self.send_error(502)
                return
            finally:
                gpu.release()
            try:
                self.send_response(200)
                self.send_header("Content-Type", "application/octet-stream" if encoding == "pcm" else "audio/mpeg")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

    with ThreadingHTTPServer(("127.0.0.1", port), Handler) as server:
        print(f"Beeswax voice ready on 127.0.0.1:{port}", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8243)
    parser.add_argument("--checkpoint", default=MODEL)
    args = parser.parse_args()
    serve(args.port, args.checkpoint)
