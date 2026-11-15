"""A recorded glasses session: data/sessions/<name>/{glasses_<wall_ms>.mkv, events.jsonl}.

events.jsonl: every Hive /scout/events SSE event as {"t": laptop_ms, "e": event}. Video frame wall time =
the segment's filename wall clock + the frame's pts (the recorder starts reading the RTSP relay at that moment).
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator

import cv2
import numpy as np


@dataclass
class Slide:
    label: str | None  # None = blank
    start: float  #      laptop ms the screen showed it
    end: float  #        next slide (or session end)


@dataclass
class Segment:
    path: Path
    start_ms: int


class Session:
    def __init__(self, folder: Path) -> None:
        self.folder = Path(folder)
        self.events = [json.loads(ln) for ln in (self.folder / "events.jsonl").open() if ln.strip()]
        self.segments = sorted(
            (Segment(p, int(re.search(r"(\d{12,})", p.name).group(1))) for p in self.folder.glob("glasses_*.mkv")),
            key=lambda s: s.start_ms,
        )
        raw = [(e["e"].get("at", e["t"]), e["e"]["label"]) for e in self.events if e["e"].get("type") == "slide"]
        end = self.events[-1]["t"] if self.events else 0
        self.slides = [Slide(lab, t, raw[i + 1][0] if i + 1 < len(raw) else end) for i, (t, lab) in enumerate(raw)]
        self.frames = [e for e in self.events if e["e"].get("type") == "frame"]
        self.window = self._window()

    def _window(self) -> tuple[float, float]:
        def read(name: str) -> float | None:
            p = self.folder / name
            try:
                return float(p.read_text().strip())
            except (OSError, ValueError):
                return None

        a = read("countdown_start_ms.txt")
        b = read("countdown_end_ms.txt")
        return (a or 0.0, b or float("inf"))

    def in_window(self, t: float) -> bool:
        return self.window[0] <= t < self.window[1]

    def label_at(self, t: float) -> tuple[str | None, Slide | None]:
        for s in self.slides:
            if s.start <= t < s.end:
                return s.label, s
        return None, None

    def video_frames(self, fps: float | None = None, t0: float = 0, t1: float = float("inf")) -> Iterator[tuple[float, np.ndarray]]:
        """(wall_ms, rgb) for every segment frame in [t0, t1), subsampled to `fps` if given."""
        for seg in self.segments:
            cap = cv2.VideoCapture(str(seg.path))
            last = -1e18
            while True:
                ok = cap.grab()
                if not ok:
                    break
                wall = seg.start_ms + cap.get(cv2.CAP_PROP_POS_MSEC)
                if wall < t0:
                    continue
                if wall >= t1:
                    break
                if fps and wall - last < 1000.0 / fps - 1:
                    continue
                ok, bgr = cap.retrieve()
                if not ok:
                    break
                last = wall
                yield wall, cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
            cap.release()

    def coverage(self) -> list[tuple[float, float]]:
        """Wall-clock spans that have video."""
        out = []
        for seg in self.segments:
            cap = cv2.VideoCapture(str(seg.path))
            n = cap.get(cv2.CAP_PROP_FRAME_COUNT)
            fps = cap.get(cv2.CAP_PROP_FPS) or 15
            cap.release()
            out.append((seg.start_ms, seg.start_ms + n / fps * 1000))
        return out
