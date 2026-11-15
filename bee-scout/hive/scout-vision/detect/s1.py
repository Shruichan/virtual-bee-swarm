"""Recorded glasses session -> frames with ground truth (which slide the laptop screen showed).

A session dir holds glasses_<wallclock_start_ms>.mkv segments and events.jsonl ({t, e} with e.type == "slide").
Frames are decoded once with ffmpeg at `fps` into <out>/<segment>/%05d.jpg; frame k of a segment is at
wall time start + k / fps. The camera shows a slide ~LAG_MS after the screen does (stream latency).
"""

from __future__ import annotations

import json
import subprocess
from dataclasses import dataclass
from pathlib import Path

LAG_MS = 400
SETTLE_MS = 1000  # background frames: blank slide, at least this long after the change


@dataclass
class Slide:
    label: str | None
    start: float  # wall ms the screen showed it (+ LAG_MS = first visible in the camera)
    end: float


@dataclass
class Frame:
    t: float  # wall ms
    path: Path
    truth: str | None  # slide label visible in the camera at t (None = blank / no slide)
    slide: int  # index into slides, -1 = before the first slide
    since: float  # ms since the slide became visible in the camera


def load_slides(sess: Path) -> list[Slide]:
    ev = [json.loads(line) for line in (sess / "events.jsonl").read_text().splitlines() if line.strip()]
    sl = [e["e"] for e in ev if e["e"].get("type") == "slide"]
    sl.sort(key=lambda e: e["at"])
    out = []
    for i, e in enumerate(sl):
        end = sl[i + 1]["at"] if i + 1 < len(sl) else float("inf")
        out.append(Slide(e["label"], float(e["at"]), float(end)))
    return out


def extract(sess: Path, out: Path, fps: float = 5.0) -> list[tuple[float, Path]]:
    res = []
    for mkv in sorted(sess.glob("glasses_*.mkv")):
        start = float(mkv.stem.split("_")[1])
        d = out / mkv.stem
        if not d.exists() or not any(d.iterdir()):
            d.mkdir(parents=True, exist_ok=True)
            subprocess.run(["ffmpeg", "-v", "error", "-i", str(mkv), "-vf", f"fps={fps:g}", "-q:v", "3", str(d / "%05d.jpg")], check=True)
        for k, f in enumerate(sorted(d.glob("*.jpg"))):
            res.append((start + k * 1000.0 / fps, f))
    return res


def session_frames(sess: Path, out: Path, fps: float = 5.0) -> tuple[list[Slide], list[Frame]]:
    slides = load_slides(sess)
    frames = []
    for t, p in extract(sess, out, fps):
        tv = t - LAG_MS
        idx = -1
        for i, s in enumerate(slides):
            if s.start <= tv < s.end:
                idx = i
                break
        if idx < 0:
            frames.append(Frame(t, p, None, -1, 1e9))
        else:
            frames.append(Frame(t, p, slides[idx].label, idx, tv - slides[idx].start))
    return slides, frames
