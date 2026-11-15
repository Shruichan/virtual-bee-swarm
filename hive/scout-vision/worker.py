"""Live Bee Scout vision worker: newest frame -> class scores -> ScoutFrame JSON, plus the HTTP API on :8242.

    uv run python worker.py --print                                         # rtsp://127.0.0.1:8554/scout
    uv run python worker.py --hive http://127.0.0.1:8241 --token "$HIVE_TOKEN"
    uv run python worker.py --source data/test.mp4 --print                  # a video file, paced in real time
    uv run python worker.py --source webcam:0 --print
    uv run python worker.py --source data/test/beehive --print              # an image folder

API (CORS *):  GET /health   POST /enrol {label, seconds}   POST /clear {label}   POST /reload
               GET /slides.json   GET /img/<label>/<file>
"""

from __future__ import annotations

import argparse
import csv
import json
import os
import queue
import re
import sys
import threading
import time
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse

import numpy as np
import requests
from PIL import Image

from scout_vision.backbones import load_backbone
from scout_vision.recognizer import (BACKGROUND, CLASSES, DEFAULT_BG_FLOOR, DEFAULT_TEMP, DEFAULT_TOPK, LABELS, REFS, ROOT, Recognizer,
                                     center_crop, crop_boxes, square_box)
from scout_vision.sources import Source

TEST = ROOT / "data" / "test"
CREDITS = ROOT / "data" / "web" / "credits.csv"


def load_dotenv() -> None:
    for p in (ROOT / ".env", ROOT.parent / ".env"):
        if p.exists():
            for ln in p.read_text().splitlines():
                m = re.match(r"\s*([A-Z_][A-Z0-9_]*)\s*=\s*(.*)\s*$", ln)
                if m and m.group(1) not in os.environ:
                    os.environ[m.group(1)] = m.group(2).strip().strip('"').strip("'")


class HivePoster:
    """Posts frames to the Hive from its own thread; only the newest pending frame is kept."""

    def __init__(self, base: str, token: str) -> None:
        self.url = base.rstrip("/") + "/scout/frame"
        self.token = token
        self.q: queue.Queue[dict] = queue.Queue(maxsize=1)
        self.session = requests.Session()
        self.last_err = 0.0
        self.errors = 0
        self.sent = 0
        threading.Thread(target=self._run, daemon=True).start()

    def post(self, frame: dict) -> None:
        try:
            self.q.get_nowait()
        except queue.Empty:
            pass
        self.q.put_nowait(frame)

    def _run(self) -> None:
        while True:
            frame = self.q.get()
            try:
                r = self.session.post(self.url, params={"token": self.token}, json=frame, timeout=1.0)
                if r.status_code >= 300:
                    raise RuntimeError(f"HTTP {r.status_code} {r.text[:80]}")
                self.sent += 1
            except Exception as e:  # noqa: BLE001 - the Hive may be down or the route missing: keep going
                self.errors += 1
                if time.time() - self.last_err > 5:
                    msg = re.sub(r"token=[^&\s)'\"]*", "token=***", str(e))[:200]  # never log the token
                    print(f"[hive] POST {self.url} failed ({self.errors} so far): {msg}", file=sys.stderr, flush=True)
                    self.last_err = time.time()


class Worker:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.rec = Recognizer(load_backbone(args.backbone), topk=args.topk, temp=args.temp, bg_floor=args.bg_floor)
        self.source = Source(args.source, loop=args.loop, hold_s=args.hold)
        self.poster = HivePoster(args.hive, args.token) if args.hive else None
        self.times: deque[float] = deque(maxlen=20)
        self.fps = 0.0
        self.last: dict | None = None
        self.ema: dict[str, float] | None = None
        self.ema_at = 0.0
        self.detector = load_detector(args.detector) if args.detector else None

    def frame_image(self, rgb) -> Image.Image:
        return center_crop(Image.fromarray(rgb), self.args.crop)

    def run(self) -> None:
        seq = -1
        min_dt = 1.0 / self.args.fps
        idle_since = time.time()
        while True:
            t_start = time.time()
            f = self.source.latest(after_seq=seq, timeout=2.0)
            if f is None:
                if self.source.ended:
                    print("[worker] source ended", file=sys.stderr, flush=True)
                    return
                if time.time() - idle_since > 10:
                    print(f"[worker] no frames from {self.source.spec} yet", file=sys.stderr, flush=True)
                    idle_since = time.time()
                continue
            idle_since = time.time()
            seq = f.seq
            t0 = time.perf_counter()
            top, scores, boxes = self.recognize(f.rgb, f.at)
            frame_ms = (time.perf_counter() - t0) * 1000
            self.times.append(time.time())
            if len(self.times) >= 2:
                self.fps = (len(self.times) - 1) / max(1e-6, self.times[-1] - self.times[0])
            out = {"at": int(f.at), "frame_ms": round(frame_ms, 1), "fps": round(self.fps, 2), "top": top, "scores": scores, "source": self.source.label}
            if boxes:
                out["boxes"] = boxes
            if f.pos_ms is not None:
                out["pos_ms"] = round(f.pos_ms, 1)  # extra, file sources only (replay_check.py)
            self.last = out
            if self.args.print:
                print(json.dumps(out), flush=True)
            if self.poster:
                self.poster.post(out)
            spare = min_dt - (time.time() - t_start)
            if spare > 0:
                time.sleep(spare)

    def recognize(self, rgb, at: float) -> tuple[list[dict], dict[str, float], list[dict]]:
        """Multi-crop scoring (+ detector proposals as extra crops), then EMA smoothing over frames."""
        h, w = rgb.shape[:2]
        det = []
        if self.detector is not None:
            try:
                det = list(self.detector(rgb) or [])
            except Exception as e:  # noqa: BLE001 - a detector bug must not stop the worker
                print(f"[detector] {e}", file=sys.stderr, flush=True)
        extra = [square_box(d["x"], d["y"], d["w"], d["h"], w, h) for d in det]
        if self.args.crop < 1.0 and self.args.crops == "center":
            rgb_c = np.asarray(self.frame_image(rgb))
            top, scores, box = self.rec.score_frame(rgb_c, "center")
            box = None
        else:
            top, scores, box = self.rec.score_frame(rgb, self.args.crops, extra)
        # EMA over the per-class scores: one odd frame no longer breaks a steady look. Reset after a gap.
        a = self.args.ema
        if a > 0 and self.ema is not None and at - self.ema_at < 1500:
            scores = {c: round(a * self.ema.get(c, 0.0) + (1 - a) * v, 4) for c, v in scores.items()}
            order = sorted(scores, key=lambda c: -scores[c])[:3]
            top = [{"label": c, "score": scores[c]} for c in order]
        self.ema, self.ema_at = scores, at
        out_boxes = [{"label": d.get("label", "background"), "score": round(float(d.get("score", 0)), 3), "x": d["x"], "y": d["y"],
                      "w": d["w"], "h": d["h"], "via": d.get("via", "detector")} for d in det]
        if box is not None and top[0]["label"] != BACKGROUND and top[0]["score"] >= 0.3:
            x0, y0, x1, y1 = box
            out_boxes.append({"label": top[0]["label"], "score": top[0]["score"], "x": round(x0 / w, 4), "y": round(y0 / h, 4),
                              "w": round((x1 - x0) / w, 4), "h": round((y1 - y0) / h, 4), "via": "refs"})
        return top, scores, out_boxes

    # ------------------------------------------------------------------ API actions

    def enrol(self, label: str, seconds: float) -> dict:
        out_dir = REFS / label
        out_dir.mkdir(parents=True, exist_ok=True)
        saved, seq, end = 0, -1, time.time() + seconds
        while time.time() < end:
            f = self.source.latest(after_seq=seq, timeout=1.0)
            if f is not None:
                seq = f.seq
                full = Image.fromarray(f.rgb)
                boxes = crop_boxes(full.width, full.height, self.args.crops)
                if label == BACKGROUND:
                    picks = range(len(boxes))  # every crop of the booth is background
                elif label in self.rec.refs:  # the crop that already looks most like this object
                    if hasattr(self.rec.backbone, "embed_crops"):
                        with self.rec.lock:
                            e = self.rec.backbone.embed_crops(f.rgb, boxes)
                    else:
                        e = self.rec.embed([full.crop(b) for b in boxes])
                    picks = [int(np.argmax((e @ self.rec.refs[label].T).max(axis=1)))]
                else:
                    picks = [0]
                for k in picks:
                    im = full.crop(boxes[k])
                    im.thumbnail((448, 448))
                    im.save(out_dir / f"live_{int(f.at)}_c{k}.jpg", quality=90)
                    saved += 1
            time.sleep(1 / 3)
        counts = self.rec.reload()
        return {"label": label, "saved": saved, "total": counts.get(label, 0)}

    def clear(self, label: str) -> dict:
        n = 0
        for p in (REFS / label).glob("live_*.jpg"):
            p.unlink()
            n += 1
        counts = self.rec.reload()
        return {"label": label, "removed": n, "total": counts.get(label, 0)}

    def health(self) -> dict:
        return {"ok": True, "model": self.rec.backbone.name, "source": self.source.label, "fps": round(self.fps, 2),
                "refs": self.rec.counts(), "connected": self.source.connected, "last": self.last}


def load_detector(spec: str):
    """--detector module:function -> callable(rgb HxWx3 uint8) -> [{x, y, w, h (0..1), score, label?, via?}].
    Each box is also scored by the refs as an extra crop; the detector module lives in its own lane."""
    import importlib

    mod, _, fn = spec.partition(":")
    return getattr(importlib.import_module(mod), fn or "detect")


def credits() -> dict[str, str]:
    out: dict[str, str] = {}
    if CREDITS.exists():
        with CREDITS.open() as f:
            for row in csv.DictReader(f):
                who = row.get("author") or "unknown"
                out[f"{row['label']}/{row['file']}"] = f"{who}, {row.get('license', '')}, Wikimedia Commons"
    return out


def make_handler(worker: Worker):
    credit_map = credits()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *a):  # quiet
            pass

        def _send(self, code: int, body: bytes, ctype: str = "application/json") -> None:
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Access-Control-Allow-Origin", "*")
            self.send_header("Access-Control-Allow-Headers", "*")
            self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
            self.send_header("Cache-Control", "no-store" if ctype == "application/json" else "max-age=3600")
            self.end_headers()
            self.wfile.write(body)

        def _json(self, code: int, obj) -> None:
            self._send(code, json.dumps(obj).encode())

        def _body(self) -> dict:
            n = int(self.headers.get("Content-Length") or 0)
            try:
                return json.loads(self.rfile.read(n) or b"{}")
            except json.JSONDecodeError:
                return {}

        def do_OPTIONS(self) -> None:
            self._send(204, b"")

        def do_GET(self) -> None:
            path = urlparse(self.path).path
            if path == "/health":
                return self._json(200, worker.health())
            if path == "/slides.json":
                host = self.headers.get("Host") or f"127.0.0.1:{worker.args.port}"
                slides = []
                for label in LABELS:
                    for f in sorted((TEST / label).glob("*.jpg")):
                        s = {"label": label, "url": f"http://{host}/img/{label}/{f.name}"}
                        if c := credit_map.get(f"{label}/{f.name}"):
                            s["credit"] = c
                        slides.append(s)
                return self._json(200, slides)
            m = re.fullmatch(r"/img/([a-z_]+)/([A-Za-z0-9_.-]+\.jpg)", path)
            if m and m.group(1) in CLASSES:
                p = TEST / m.group(1) / m.group(2)
                if p.is_file():
                    return self._send(200, p.read_bytes(), "image/jpeg")
            self._json(404, {"error": "not found"})

        def do_POST(self) -> None:
            path = urlparse(self.path).path
            body = self._body()
            if path == "/reload":
                return self._json(200, {"ok": True, "refs": worker.rec.reload()})
            if path in ("/enrol", "/clear"):
                label = body.get("label")
                if label not in CLASSES:
                    return self._json(400, {"error": f"label must be one of {CLASSES}"})
                if path == "/clear":
                    return self._json(200, worker.clear(label))
                try:
                    seconds = min(30.0, max(0.5, float(body.get("seconds", 3))))
                except (TypeError, ValueError):
                    return self._json(400, {"error": "seconds must be a number"})
                return self._json(200, worker.enrol(label, seconds))
            self._json(404, {"error": "not found"})

    return Handler


def main() -> None:
    load_dotenv()
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--source", default=os.environ.get("SCOUT_SOURCE", "rtsp://127.0.0.1:8554/scout"))
    ap.add_argument("--backbone", default=os.environ.get("SCOUT_BACKBONE", "dino"), choices=["clip", "dino"])
    ap.add_argument("--print", action="store_true", help="JSON lines to stdout")
    ap.add_argument("--hive", default=os.environ.get("SCOUT_HIVE_URL"), help="e.g. http://127.0.0.1:8241")
    ap.add_argument("--token", default=os.environ.get("HIVE_TOKEN", ""))
    ap.add_argument("--fps", type=float, default=10.0, help="max processed frames per second")
    ap.add_argument("--crop", type=float, default=float(os.environ.get("SCOUT_CROP", 1.0)), help="central fraction of the frame to look at")
    ap.add_argument("--temp", type=float, default=DEFAULT_TEMP)
    ap.add_argument("--topk", type=int, default=DEFAULT_TOPK)
    ap.add_argument("--bg-floor", type=float, default=DEFAULT_BG_FLOOR)
    ap.add_argument("--crops", default=os.environ.get("SCOUT_CROPS", "center"), choices=["center", "grid7", "grid15"])
    ap.add_argument("--ema", type=float, default=float(os.environ.get("SCOUT_EMA", 0.0)), help="score smoothing, 0 = off")
    ap.add_argument("--detector", default=os.environ.get("SCOUT_DETECTOR"), help="module:function returning boxes")
    ap.add_argument("--loop", action="store_true", help="loop a file/folder source")
    ap.add_argument("--hold", type=float, default=2.0, help="seconds per image (folder source)")
    ap.add_argument("--port", type=int, default=int(os.environ.get("SCOUT_VISION_PORT", 8242)))
    ap.add_argument("--bind", default="0.0.0.0")
    ap.add_argument("--no-api", action="store_true")
    args = ap.parse_args()

    worker = Worker(args)
    if not args.no_api:
        server = ThreadingHTTPServer((args.bind, args.port), make_handler(worker))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        print(f"[api] http://{args.bind}:{args.port}  (/health /enrol /reload /slides.json)", file=sys.stderr, flush=True)
    try:
        worker.run()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
