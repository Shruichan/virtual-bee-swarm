"""Per-class reference embeddings + nearest-neighbour scoring.

refs/<class>/*.jpg (web_* from fetch_web_refs.py, live_* from POST /enrol) are embedded once and cached in
cache/<backbone>.npz (keyed by path + mtime). A frame scores each class by the mean cosine similarity of its
top-k nearest references in that class; a softmax with temperature T turns those into calibrated 0..1 scores
that sum to 1, so "score >= minScore and margin >= minMargin" means a clear, unambiguous win.
"""

from __future__ import annotations

import sys
import threading
import time
from pathlib import Path

import numpy as np
from PIL import Image

from .backbones import Backbone

LABELS = ["beehive", "native_flower", "honey_jar", "pebble_dish", "bug_spray", "bee_hotel"]
BACKGROUND = "background"
CLASSES = [*LABELS, BACKGROUND]

ROOT = Path(__file__).resolve().parent.parent
REFS = ROOT / "refs"
CACHE = ROOT / "cache"

DEFAULT_TOPK = 3
# Per-backbone calibration, tuned with eval.py --sweep (similarity scales differ: DINOv2 own-class median ~0.56,
# best other class ~0.15; CLIP 0.68 vs 0.43). bg_floor: background's similarity is at least this, so a frame
# unlike every reference falls to background instead of to the least-unlike object.
CALIBRATION = {"dino": {"temp": 0.04, "bg_floor": 0.2}, "clip": {"temp": 0.03, "bg_floor": 0.0}}
DEFAULT_TEMP = None  # None = CALIBRATION for the backbone
DEFAULT_BG_FLOOR = None


def crop_boxes(w: int, h: int, mode: str = "grid15") -> list[tuple[int, int, int, int]]:
    """Square crops (left, top, right, bottom) the worker scores on every frame; box 0 = the centre square.
    The glasses camera is wide and wearers look slightly down, so a screen or a table-top object fills only a
    small, usually upper, part of the frame: whole-frame scoring only works up close (session s1).
      center  just box 0 (the old behaviour)
      grid7   + six 0.6 x height crops (upper and middle rows)
      grid15  + eight 0.4 x height crops (upper row of five, middle row of three)"""
    boxes = [((w - h) // 2, 0, (w + h) // 2, h)]
    if mode == "center":
        return boxes
    sets = [(0.6, [(cx, cy) for cy in (0.3, 0.6) for cx in (0.35, 0.5, 0.65)])]
    if mode == "grid15":
        sets.append((0.4, [(cx, 0.2) for cx in (0.3, 0.4, 0.5, 0.6, 0.7)] + [(cx, 0.45) for cx in (0.4, 0.5, 0.6)]))
    for frac, pts in sets:
        s = int(h * frac)
        for cx, cy in pts:
            x, y = int(w * cx - s / 2), int(h * cy - s / 2)
            x, y = max(0, min(w - s, x)), max(0, min(h - s, y))
            boxes.append((x, y, x + s, y + s))
    return boxes


def square_box(x: float, y: float, bw: float, bh: float, w: int, h: int, pad: float = 1.15) -> tuple[int, int, int, int]:
    """A normalized box (detector output) -> a padded square pixel crop inside the frame."""
    cx, cy = (x + bw / 2) * w, (y + bh / 2) * h
    s = int(min(max(bw * w, bh * h) * pad, w, h))
    s = max(s, 32)
    x0, y0 = int(min(max(0, cx - s / 2), w - s)), int(min(max(0, cy - s / 2), h - s))
    return (x0, y0, x0 + s, y0 + s)


def center_crop(im: Image.Image, frac: float) -> Image.Image:
    """Keep the central `frac` of the shorter side, as a square (the wearer looks at the middle)."""
    if frac >= 1.0:
        return im
    w, h = im.size
    s = int(min(w, h) * frac)
    left, top = (w - s) // 2, (h - s) // 2
    return im.crop((left, top, left + s, top + s))


class Recognizer:
    def __init__(self, backbone: Backbone, refs_dir: Path = REFS, topk: int = DEFAULT_TOPK, temp: float | None = DEFAULT_TEMP,
                 bg_floor: float | None = DEFAULT_BG_FLOOR, skip_prefixes: tuple[str, ...] = ()) -> None:
        self.skip_prefixes = skip_prefixes  # eval.py skips slide_* / glasses_* refs to keep its test set held out
        cal = CALIBRATION.get(backbone.kind, {"temp": 0.03, "bg_floor": 0.0})
        self.backbone = backbone
        self.bg_floor = cal["bg_floor"] if bg_floor is None else bg_floor
        self.refs_dir = refs_dir
        self.topk = topk
        self.temp = cal["temp"] if temp is None else temp
        self.lock = threading.Lock()  # one model, shared by the worker loop and /enrol
        self.refs: dict[str, np.ndarray] = {}
        self.reload()

    # ------------------------------------------------------------------ references

    def _cache_path(self) -> Path:
        return CACHE / f"{self.backbone.name.replace('/', '_')}.npz"

    def reload(self) -> dict[str, int]:
        t0 = time.time()
        cached: dict[str, tuple[float, np.ndarray]] = {}
        cp = self._cache_path()
        if cp.exists():
            z = np.load(cp, allow_pickle=False)
            for p, m, e in zip(z["paths"], z["mtimes"], z["embs"]):
                cached[str(p)] = (float(m), e)
        paths, mtimes, embs = [], [], []
        todo: list[tuple[int, Path]] = []
        for cls in CLASSES:
            for f in sorted((self.refs_dir / cls).glob("*.jpg")) if (self.refs_dir / cls).exists() else []:
                if f.name.startswith(self.skip_prefixes):
                    continue
                key, m = str(f.relative_to(self.refs_dir)), f.stat().st_mtime
                paths.append(key)
                mtimes.append(m)
                hit = cached.get(key)
                if hit and abs(hit[0] - m) < 1e-3:
                    embs.append(hit[1])
                else:
                    embs.append(None)
                    todo.append((len(embs) - 1, f))
        for i in range(0, len(todo), 32):
            batch = todo[i:i + 32]
            ims = [Image.open(f).convert("RGB") for _, f in batch]
            with self.lock:
                out = self.backbone.embed(ims)
            for (j, _), e in zip(batch, out):
                embs[j] = e
        refs: dict[str, list[np.ndarray]] = {c: [] for c in CLASSES}
        for key, e in zip(paths, embs):
            refs[key.split("/")[0]].append(e)
        self.refs = {c: np.stack(v) for c, v in refs.items() if v}
        if paths:
            CACHE.mkdir(parents=True, exist_ok=True)
            np.savez(cp, paths=np.array(paths), mtimes=np.array(mtimes), embs=np.stack(embs))
        counts = self.counts()
        print(f"[recognizer] {self.backbone.name}: {sum(counts.values())} refs ({len(todo)} new) in {time.time() - t0:.1f}s {counts}", file=sys.stderr, flush=True)
        return counts

    def counts(self) -> dict[str, int]:
        return {c: int(len(self.refs[c])) if c in self.refs else 0 for c in CLASSES}

    # ------------------------------------------------------------------ scoring

    def embed(self, images: list[Image.Image]) -> np.ndarray:
        with self.lock:
            return self.backbone.embed(images)

    def class_sims(self, embs: np.ndarray, bg_floor: float | None = None) -> tuple[list[str], np.ndarray]:
        """(classes, sims[N, C]): mean of the top-k cosine similarities per class (background floored)."""
        classes = [c for c in CLASSES if c in self.refs]
        sims = np.zeros((len(embs), len(classes)), np.float32)
        for j, c in enumerate(classes):
            s = embs @ self.refs[c].T  # (N, R)
            k = min(self.topk, s.shape[1])
            sims[:, j] = np.sort(s, axis=1)[:, -k:].mean(axis=1)
        floor = self.bg_floor if bg_floor is None else bg_floor
        if BACKGROUND in classes:
            j = classes.index(BACKGROUND)
            sims[:, j] = np.maximum(sims[:, j], floor)
        else:  # no background refs yet: the floor alone stands in for it
            classes = [*classes, BACKGROUND]
            sims = np.concatenate([sims, np.full((len(embs), 1), floor, np.float32)], axis=1)
        return classes, sims

    def calibrate(self, sims: np.ndarray, temp: float | None = None) -> np.ndarray:
        t = temp or self.temp
        z = sims / t
        z = z - z.max(axis=1, keepdims=True)
        p = np.exp(z)
        return p / p.sum(axis=1, keepdims=True)

    def score(self, images: list[Image.Image]) -> list[tuple[list[dict], dict[str, float]]]:
        """Per image: (top 3 [{label, score}] best first, {class: score} for all seven classes)."""
        classes, sims = self.class_sims(self.embed(images))
        probs = self.calibrate(sims)
        out = []
        for row in probs:
            order = np.argsort(-row)[:3]
            top = [{"label": classes[i], "score": round(float(row[i]), 4)} for i in order]
            scores = {c: round(float(row[classes.index(c)]), 4) if c in classes else 0.0 for c in CLASSES}
            out.append((top, scores))
        return out

    def score_frame(self, rgb: np.ndarray, mode: str = "grid15", extra: list[tuple[int, int, int, int]] | None = None):
        """One video frame -> (top 3, {class: score}, winning box (x0, y0, x1, y1) in pixels).
        Every crop is scored on its own; the frame takes the distribution of the crop with the strongest object
        score, so a small object anywhere in the scanned area can win while background-only crops do not dilute it."""
        h, w = rgb.shape[:2]
        boxes = crop_boxes(w, h, mode) + list(extra or [])
        with self.lock:
            if hasattr(self.backbone, "embed_crops"):
                embs = self.backbone.embed_crops(rgb, boxes)
            else:
                im = Image.fromarray(rgb)
                embs = self.backbone.embed([im.crop(b) for b in boxes])
        classes, sims = self.class_sims(embs)
        probs = self.calibrate(sims)
        bg = classes.index(BACKGROUND)
        obj = probs.copy()
        obj[:, bg] = 0
        best = int(np.argmax(obj.max(axis=1)))
        row = probs[best]
        order = np.argsort(-row)[:3]
        top = [{"label": classes[i], "score": round(float(row[i]), 4)} for i in order]
        scores = {c: round(float(row[classes.index(c)]), 4) if c in classes else 0.0 for c in CLASSES}
        return top, scores, boxes[best]
