"""Measure the card matcher: synthetic composites of each reference into real glasses frames, false positives on
card-free frames, and ms/frame.

    uv run python -m train.verify_matcher --refs detect/card_images --frames train/data/s1 [--out out/verify]

--frames: a folder of glasses frames (searched recursively for *.jpg; e.g. ffmpeg -vf fps=2 of the s1 mkvs).
Composite: the reference warped to a width of 90..380 px (in a 1280x720 frame) with random tilt (corner jitter
+-10 %, rotation +-12 deg), brightness/contrast, Gaussian blur 0/3/5, then the whole frame JPEG q 40-80.
A hit = the right label with box IoU >= 0.3 against the pasted outline.
"""

from __future__ import annotations

import argparse
import random
import time
from pathlib import Path

import cv2
import numpy as np

from train.matcher import LABELS, CardMatcher, Vote, find_refs

SIZES = (90, 140, 200, 280, 380)


def iou(a, b) -> float:
    ax0, ay0, ax1, ay1 = a
    bx0, by0, bx1, by1 = b
    iw, ih = max(0, min(ax1, bx1) - max(ax0, bx0)), max(0, min(ay1, by1) - max(ay0, by0))
    inter = iw * ih
    return inter / max(1e-9, (ax1 - ax0) * (ay1 - ay0) + (bx1 - bx0) * (by1 - by0) - inter)


def composite(bg: np.ndarray, ref: np.ndarray, width: int, rng: random.Random) -> tuple[np.ndarray, tuple]:
    H, W = bg.shape[:2]
    h0, w0 = ref.shape[:2]
    hh = width * h0 / w0
    cx = rng.uniform(width / 2 + 10, W - width / 2 - 10)
    cy = rng.uniform(min(H / 2, hh / 2 + 10), max(H / 2, H - hh / 2 - 10))
    base = np.float32([[-width / 2, -hh / 2], [width / 2, -hh / 2], [width / 2, hh / 2], [-width / 2, hh / 2]])
    base += np.float32([[rng.uniform(-0.1, 0.1) * width, rng.uniform(-0.1, 0.1) * hh] for _ in range(4)])
    a = np.radians(rng.uniform(-12, 12))
    R = np.float32([[np.cos(a), -np.sin(a)], [np.sin(a), np.cos(a)]])
    dst = base @ R.T + np.float32([cx, cy])
    src = np.float32([[0, 0], [w0, 0], [w0, h0], [0, h0]])
    M = cv2.getPerspectiveTransform(src, dst)
    im = ref.astype(np.float32) * rng.uniform(0.7, 1.15) + rng.uniform(-25, 20)
    im = np.clip(im, 0, 255).astype(np.uint8)
    warped = cv2.warpPerspective(im, M, (W, H), flags=cv2.INTER_AREA)
    mask = cv2.warpPerspective(np.full((h0, w0), 255, np.uint8), M, (W, H), flags=cv2.INTER_LINEAR)
    out = (bg * (1 - mask[..., None] / 255.0) + warped * (mask[..., None] / 255.0)).astype(np.uint8)
    k = rng.choice([0, 3, 5])
    if k:
        out = cv2.GaussianBlur(out, (k, k), 0)
    ok, enc = cv2.imencode(".jpg", out, [cv2.IMWRITE_JPEG_QUALITY, rng.randint(40, 80)])
    out = cv2.imdecode(enc, cv2.IMREAD_COLOR)
    x0, y0 = np.clip(dst.min(0), 0, [W, H])
    x1, y1 = np.clip(dst.max(0), 0, [W, H])
    return out, (x0 / W, y0 / H, x1 / W, y1 / H)


def draw(bgr: np.ndarray, boxes: list[dict], path: Path) -> None:
    H, W = bgr.shape[:2]
    im = bgr.copy()
    for b in boxes:
        q = (np.array(b["quad"]) * [W, H]).astype(np.int32)
        cv2.polylines(im, [q], True, (0, 255, 0), 2)
        cv2.putText(im, f"{b['label']} {b['inliers']}", (int(b["x"] * W), max(15, int(b["y"] * H) - 5)), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)
    cv2.imwrite(str(path), im)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--refs", type=Path, default=Path("detect/card_images"))
    ap.add_argument("--frames", type=Path, default=Path("train/data/s1"))
    ap.add_argument("--out", type=Path, default=Path("out/verify_matcher"))
    ap.add_argument("--trials", type=int, default=10, help="composites per label per size")
    ap.add_argument("--fp", type=int, default=300, help="card-free frames for the false-positive test")
    ap.add_argument("--frame-w", type=int, default=1280)
    ap.add_argument("--nfeatures", type=int, default=2500)
    ap.add_argument("--min-inliers", type=int, default=12)
    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    rng = random.Random(7)
    frames = sorted(args.frames.rglob("*.jpg"))
    m = CardMatcher(args.refs, frame_w=args.frame_w, nfeatures=args.nfeatures, min_inliers=args.min_inliers)
    refs = {lb: cv2.imread(str(p)) for lb, p in find_refs(args.refs).items()}
    times = []

    def run(bgr):
        rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
        t0 = time.perf_counter()
        b = m.detect(rgb)
        times.append((time.perf_counter() - t0) * 1000)
        return b

    print(f"refs: {m.counts}   frames available: {len(frames)}")
    print(f"{'label':14s}" + "".join(f"{s:>7d}" for s in SIZES) + "   wrong")
    saved = 0
    tot_hit = {s: 0 for s in SIZES}
    for lb in LABELS:
        if lb not in refs:
            continue
        row, wrong = [], 0
        for s in SIZES:
            hit = 0
            for t in range(args.trials):
                bg = cv2.resize(cv2.imread(str(rng.choice(frames))), (1280, 720))
                img, gt = composite(bg, refs[lb], s, rng)
                boxes = run(img)
                ok = any(b["label"] == lb and iou(gt, (b["x"], b["y"], b["x"] + b["w"], b["y"] + b["h"])) >= 0.3 for b in boxes)
                wrong += sum(b["label"] != lb for b in boxes)
                hit += ok
                if t == 0 and s in (90, 200) and saved < 12:
                    draw(img, boxes, args.out / f"{lb}_{s}_{'hit' if ok else 'miss'}.jpg")
                    saved += 1
            row.append(hit / args.trials)
            tot_hit[s] += hit
        print(f"{lb:14s}" + "".join(f"{r * 100:6.0f}%" for r in row) + f"   {wrong}")
    n_lab = len(refs) * args.trials
    print(f"{'all':14s}" + "".join(f"{tot_hit[s] / n_lab * 100:6.0f}%" for s in SIZES))

    # false positives: evenly spaced card-free frames, in time order (so the vote sees a real sequence)
    step = max(1, len(frames) // args.fp)
    fp_frames = frames[::step][: args.fp]
    vote = Vote()
    raw_fp, voted_fp, n_fp = 0, 0, 0
    for i, f in enumerate(fp_frames):
        bgr = cv2.resize(cv2.imread(str(f)), (1280, 720))
        boxes = run(bgr)
        vb = vote.apply(boxes, i * 500.0)  # 2 fps spacing
        n_fp += 1
        if boxes:
            raw_fp += 1
            draw(bgr, boxes, args.out / f"fp_{f.parent.name}_{f.stem}.jpg")
            print(f"  FP {f}: {[(b['label'], b['inliers']) for b in boxes]}")
        voted_fp += any(b["score"] >= 0.6 for b in vb)
    print(f"false positives: {raw_fp}/{n_fp} frames raw, {voted_fp}/{n_fp} after the 3-of-5 vote")
    t = np.array(times)
    print(f"ms/frame: mean {t.mean():.1f}  p50 {np.median(t):.1f}  p95 {np.percentile(t, 95):.1f}  (n={len(t)})")


if __name__ == "__main__":
    main()
