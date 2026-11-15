"""No-training card matcher: SIFT keypoints of our six exact pictures, matched into each frame with a homography.

    from train.matcher import CardMatcher
    m = CardMatcher("/path/to/card_images")        # <label>.jpg|png per class; precomputes SIFT once
    boxes = m.detect(rgb)                            # [{label, score, x, y, w, h, via, inliers}] best first
    top, scores = scores_from(boxes)                 # ScoutFrame top (3) and scores (6 labels + background)

    uv run python -m train.matcher <ref_dir> <image>...   # print matches for still images (debug)

One FLANN index over all references (each picture at two sizes so far-away cards still find partners), one kNN
query per frame, Lowe ratio test, then per class a RANSAC homography. A match counts when it has enough inliers
and the projected picture outline is a sane convex quad. Score: MIN_INLIERS -> 0.6 (the Hive's minScore),
rising to 0.99 at MIN_INLIERS + 30.
"""

from __future__ import annotations

import sys
import time
from collections import deque
from pathlib import Path

import cv2
import numpy as np

LABELS = ["beehive", "native_flower", "honey_jar", "pebble_dish", "bug_spray", "bee_hotel"]
BACKGROUND = "background"
EXTS = (".jpg", ".jpeg", ".png", ".webp")

REF_SIZES = (720, 360, 180)  # long side of each reference copy, px
REF_FEATS = (1500, 1000, 600)  # strongest SIFT keypoints kept per copy
FRAME_W = 1280  # frames are matched at this width (the glasses send 1280x720)
RATIO = 0.75
RANSAC_PX = 3.0
MIN_INLIERS = 12
MIN_GOOD = 10  # ratio-test survivors a class needs before a homography is tried


def find_refs(ref_dir: Path) -> dict[str, Path]:
    """One image per label: <label>.<jpg|jpeg|png|webp>; when several exist, the newest file wins."""
    out = {}
    for label in LABELS:
        cands = [p for p in ref_dir.glob(f"{label}.*") if p.suffix.lower() in EXTS]
        if cands:
            out[label] = max(cands, key=lambda p: p.stat().st_mtime)
    return out


def _gray(rgb: np.ndarray, clahe) -> np.ndarray:
    g = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)
    return clahe.apply(g) if clahe is not None else g


class CardMatcher:
    def __init__(self, ref_dir: str | Path, frame_w: int = FRAME_W, use_clahe: bool = True, nfeatures: int = 2500,
                 min_inliers: int = MIN_INLIERS, checks: int = 32) -> None:
        self.ref_dir = Path(ref_dir)
        self.frame_w = frame_w
        self.min_inliers = min_inliers
        self.clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8)) if use_clahe else None
        self.sift = cv2.SIFT_create(nfeatures=nfeatures)
        refs = find_refs(self.ref_dir)
        if not refs:
            raise FileNotFoundError(f"no <label>.jpg/png references in {self.ref_dir}")
        self.labels = [lb for lb in LABELS if lb in refs]
        self.files = {lb: str(refs[lb]) for lb in self.labels}
        self.corners: dict[str, np.ndarray] = {}
        pts, descs, owner = [], [], []
        for ci, label in enumerate(self.labels):
            img = cv2.cvtColor(cv2.imread(str(refs[label]), cv2.IMREAD_COLOR), cv2.COLOR_BGR2RGB)
            h0, w0 = img.shape[:2]
            base = REF_SIZES[0] / max(h0, w0)  # all keypoints live in the REF_SIZES[0] coordinate frame
            W, H = w0 * base, h0 * base
            self.corners[label] = np.float32([[0, 0], [W, 0], [W, H], [0, H]]).reshape(-1, 1, 2)
            for size, nf in zip(REF_SIZES, REF_FEATS):
                s = size / max(h0, w0)
                im = cv2.resize(img, (round(w0 * s), round(h0 * s)), interpolation=cv2.INTER_AREA)
                kp, d = cv2.SIFT_create(nfeatures=nf).detectAndCompute(_gray(im, self.clahe), None)
                if d is None:
                    continue
                k = base / s
                pts.append(np.float32([p.pt for p in kp]) * k)
                descs.append(d)
                owner.append(np.full(len(kp), ci, np.int32))
        self.ref_pts = np.concatenate(pts)
        self.ref_desc = np.concatenate(descs).astype(np.float32)
        self.ref_owner = np.concatenate(owner)
        self.flann = cv2.FlannBasedMatcher({"algorithm": 1, "trees": 4}, {"checks": checks})
        self.flann.add([self.ref_desc])
        self.flann.train()
        self.counts = {lb: int((self.ref_owner == i).sum()) for i, lb in enumerate(self.labels)}

    def detect(self, rgb: np.ndarray, debug: dict | None = None) -> list[dict]:
        """rgb HxWx3 uint8 -> accepted matches as ScoutBox dicts (normalized x,y = top-left), best first."""
        H0, W0 = rgb.shape[:2]
        s = min(1.0, self.frame_w / W0)
        img = cv2.resize(rgb, (round(W0 * s), round(H0 * s)), interpolation=cv2.INTER_AREA) if s < 1 else rgb
        h, w = img.shape[:2]
        kp, d = self.sift.detectAndCompute(_gray(img, self.clahe), None)
        if d is None or len(kp) < 2:
            return []
        knn = self.flann.knnMatch(d.astype(np.float32), k=2)
        q_idx, r_idx = [], []
        for m in knn:
            if len(m) == 2 and m[0].distance < RATIO * m[1].distance:
                q_idx.append(m[0].queryIdx)
                r_idx.append(m[0].trainIdx)
        if not q_idx:
            return []
        q_pts = np.float32([kp[i].pt for i in q_idx])
        r_idx = np.asarray(r_idx)
        cls = self.ref_owner[r_idx]
        out = []
        for ci, label in enumerate(self.labels):
            sel = cls == ci
            n = int(sel.sum())
            if debug is not None:
                debug.setdefault("good", {})[label] = n
            if n < MIN_GOOD:
                continue
            src = self.ref_pts[r_idx[sel]].reshape(-1, 1, 2)
            dst = q_pts[sel].reshape(-1, 1, 2)
            M, mask = cv2.findHomography(src, dst, cv2.RANSAC, RANSAC_PX)
            if M is None:
                continue
            # count inliers on distinct frame points (one frame point matched to both ref sizes counts once)
            inl = mask.ravel().astype(bool)
            uniq = len({(round(float(x)), round(float(y))) for x, y in dst[inl].reshape(-1, 2)})
            if debug is not None:
                debug.setdefault("inliers", {})[label] = uniq
            if uniq < self.min_inliers:
                continue
            quad = cv2.perspectiveTransform(self.corners[label], M).reshape(-1, 2)
            if not _sane(quad, w, h):
                continue
            x0, y0 = np.clip(quad.min(axis=0), 0, [w, h])
            x1, y1 = np.clip(quad.max(axis=0), 0, [w, h])
            score = min(0.99, 0.6 + 0.39 * (uniq - self.min_inliers) / 30)
            out.append({"label": label, "score": round(score, 3), "x": round(float(x0 / w), 4), "y": round(float(y0 / h), 4),
                        "w": round(float((x1 - x0) / w), 4), "h": round(float((y1 - y0) / h), 4), "via": "detector",
                        "inliers": uniq, "quad": (quad / [w, h]).round(4).tolist()})
        out.sort(key=lambda b: -b["score"])
        return out


def _sane(quad: np.ndarray, w: int, h: int) -> bool:
    """Convex, not tiny, not absurdly large, sides not too uneven (a real tilt of a flat picture)."""
    if not cv2.isContourConvex(quad.astype(np.float32).reshape(-1, 1, 2)):
        return False
    area = cv2.contourArea(quad.astype(np.float32))
    if area < 30 * 30 or area > 4.0 * w * h:
        return False
    sides = np.linalg.norm(quad - np.roll(quad, -1, axis=0), axis=1)
    if sides.min() < 15 or sides[0] / max(1e-6, sides[2]) > 3 or sides[2] / max(1e-6, sides[0]) > 3:
        return False
    if sides[1] / max(1e-6, sides[3]) > 3 or sides[3] / max(1e-6, sides[1]) > 3:
        return False
    return True


def scores_from(boxes: list[dict]) -> tuple[list[dict], dict[str, float]]:
    """Per-class score = best box score, background = 1 - max; top = best 3."""
    scores = {lb: 0.0 for lb in LABELS}
    for b in boxes:
        if b["label"] in scores:
            scores[b["label"]] = max(scores[b["label"]], float(b["score"]))
    scores[BACKGROUND] = round(1.0 - max(scores.values()), 4)
    order = sorted(scores, key=lambda c: -scores[c])[:3]
    return [{"label": c, "score": round(scores[c], 4)} for c in order], {c: round(v, 4) for c, v in scores.items()}


class Vote:
    """A class must be seen in >= need of the last n frames before its score is reported high; until then its
    score (and box) is capped at `cap`, below the Hive's minScore, so a one-frame fluke never starts a look."""

    def __init__(self, need: int = 3, n: int = 5, cap: float = 0.3) -> None:
        self.need, self.cap = need, cap
        self.hist: deque[set[str]] = deque(maxlen=n)
        self.last_at = 0.0

    def apply(self, boxes: list[dict], at: float) -> list[dict]:
        if at - self.last_at > 1500:
            self.hist.clear()  # stream gap: start over
        self.last_at = at
        self.hist.append({b["label"] for b in boxes})
        out = []
        for b in boxes:
            seen = sum(b["label"] in s for s in self.hist)
            out.append(b if seen >= self.need else {**b, "score": min(b["score"], self.cap)})
        return out


def main() -> None:
    ref_dir, *images = sys.argv[1:]
    t0 = time.perf_counter()
    m = CardMatcher(ref_dir)
    print(f"refs {m.counts} in {(time.perf_counter() - t0) * 1000:.0f} ms")
    for p in images:
        rgb = cv2.cvtColor(cv2.imread(p), cv2.COLOR_BGR2RGB)
        dbg: dict = {}
        t0 = time.perf_counter()
        boxes = m.detect(rgb, dbg)
        print(f"{p}: {(time.perf_counter() - t0) * 1000:.0f} ms  {[(b['label'], b['inliers'], b['score']) for b in boxes]}  {dbg}")


if __name__ == "__main__":
    main()
