"""Live Bee Scout station-card finder: printed AprilTags (tag25h9 ids 0-5, one per label).

    from detect import ScoutTracker, scores_from_boxes
    tracker = ScoutTracker()                       # one per video stream
    boxes = tracker.update(frame_bgr, now_ms)      # list[ScoutBox], stabilized (2-frame confirm, 500 ms hold)
                                                   # (rgb=True for RGB frames, as the vision worker has)
    top, scores = scores_from_boxes(boxes)         # -> ScoutFrame.top / .scores; boxes -> ScoutFrame.boxes
    payload_boxes = [b.to_json() for b in boxes]

    from detect import detect                      # stateless, single frame
    boxes = detect(frame_bgr)

~8 ms/frame on the CPU at 1280x720, no model file (OpenCV cv2.aruco). Pass full-resolution frames: far tags
need every pixel. Cards: make_cards.py (11 cm tag on US Letter).
"""

from __future__ import annotations

import numpy as np

from .markers import TAG_IDS, MarkerDetector
from .types import BACKGROUND, CLASSES, LABELS, ScoutBox

__all__ = ["ScoutBox", "ScoutTracker", "detect", "scores_from_boxes", "TAG_IDS", "LABELS", "CLASSES", "BACKGROUND"]

_markers: MarkerDetector | None = None


def _marker_detector() -> MarkerDetector:
    global _markers
    if _markers is None:
        _markers = MarkerDetector()
    return _markers


def detect(frame_bgr: np.ndarray) -> list[ScoutBox]:
    """Every station-card tag in one frame (no smoothing), biggest first."""
    return _marker_detector().detect(frame_bgr)


def prominence(b: ScoutBox) -> float:
    """Bigger and nearer the centre of view = what the wearer is looking at."""
    dx, dy = b.cx - 0.5, (b.y + b.h / 2) - 0.5
    return b.w * b.h * (1.0 - min(0.8, (dx * dx + dy * dy) ** 0.5))


def scores_from_boxes(boxes: list[ScoutBox], main_score: float = 0.99, others: float = 0.4) -> tuple[list[dict], dict[str, float]]:
    """Boxes -> the game's (top, scores). The most prominent box's label scores `main_score`; every other label
    in view scores at most `others` (the dashboard shows it, the lead stays well clear of the game's 0.12
    margin); background = 1 - main. No boxes -> background 1.0."""
    scores = {c: 0.0 for c in CLASSES}
    if boxes:
        main = max(boxes, key=prominence)
        for b in boxes:
            if b.label in scores and b.label != main.label:
                scores[b.label] = max(scores[b.label], min(others, b.score))
        scores[main.label] = main_score
        scores[BACKGROUND] = round(1.0 - main_score, 4)
    else:
        scores[BACKGROUND] = 1.0
    order = sorted(scores, key=lambda c: -scores[c])[:3]
    return [{"label": c, "score": round(scores[c], 4)} for c in order], {c: round(v, 4) for c, v in scores.items()}


class ScoutTracker:
    """Per-stream smoothing: a tag must be seen in `confirm` of the last `window` frames before it counts (kills
    one-frame false decodes), and stays alive for `hold_ms` after it was last seen (a blurry frame mid-look
    doesn't break the game's 0.9 s steady run). Held boxes keep their last seen position."""

    def __init__(self, confirm: int = 2, window: int = 3, hold_ms: float = 500, detector: MarkerDetector | None = None) -> None:
        self.confirm, self.window, self.hold_ms = confirm, window, hold_ms
        self.markers = detector or _marker_detector()
        self.hist: list[set[str]] = []
        self.last: dict[str, tuple[float, ScoutBox]] = {}

    def update(self, frame: np.ndarray, now_ms: float, rgb: bool = False) -> list[ScoutBox]:
        """frame: full-resolution BGR (or RGB with rgb=True); now_ms: the frame's timestamp."""
        seen = self.markers.detect(frame, rgb=rgb)
        self.hist = (self.hist + [{b.label for b in seen}])[-self.window:]
        for b in seen:
            if b.label in self.last or sum(b.label in h for h in self.hist) >= self.confirm:
                self.last[b.label] = (now_ms, b)
        self.last = {k: v for k, v in self.last.items() if now_ms - v[0] <= self.hold_ms}
        return [b for _, b in self.last.values()]
