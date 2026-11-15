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
DEFAULT_TEMP = 0.02


def center_crop(im: Image.Image, frac: float) -> Image.Image:
    """Keep the central `frac` of the shorter side, as a square (the wearer looks at the middle)."""
    if frac >= 1.0:
        return im
    w, h = im.size
    s = int(min(w, h) * frac)
    left, top = (w - s) // 2, (h - s) // 2
    return im.crop((left, top, left + s, top + s))


class Recognizer:
    def __init__(self, backbone: Backbone, refs_dir: Path = REFS, topk: int = DEFAULT_TOPK, temp: float = DEFAULT_TEMP) -> None:
        self.backbone = backbone
        self.refs_dir = refs_dir
        self.topk = topk
        self.temp = temp
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

    def class_sims(self, embs: np.ndarray) -> tuple[list[str], np.ndarray]:
        """(classes, sims[N, C]): mean of the top-k cosine similarities per class."""
        classes = [c for c in CLASSES if c in self.refs]
        sims = np.zeros((len(embs), len(classes)), np.float32)
        for j, c in enumerate(classes):
            s = embs @ self.refs[c].T  # (N, R)
            k = min(self.topk, s.shape[1])
            sims[:, j] = np.sort(s, axis=1)[:, -k:].mean(axis=1)
        return classes, sims

    def calibrate(self, sims: np.ndarray, temp: float | None = None) -> np.ndarray:
        t = temp or self.temp
        z = sims / t
        z = z - z.max(axis=1, keepdims=True)
        p = np.exp(z)
        return p / p.sum(axis=1, keepdims=True)

    def score(self, images: list[Image.Image]) -> list[list[dict]]:
        """Per image: top 3 [{label, score}], best first."""
        classes, sims = self.class_sims(self.embed(images))
        probs = self.calibrate(sims)
        out = []
        for row in probs:
            order = np.argsort(-row)[:3]
            out.append([{"label": classes[i], "score": round(float(row[i]), 4)} for i in order])
        return out
