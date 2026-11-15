"""Check the rendered station cards with the marker detector, the way the glasses would see them.

    uv run python -m detect.verify_cards                      # needs out/cards (make_cards) and data/frames

1. Each card PNG straight (downscaled to 1280 px wide): the right tag id must come back.
2. Photo-like: each card composited into real s1 glasses frames at 1.0-3.5 m (f = 385 px, 1280x720), random
   tilt up to 40 deg, light gain, blur, motion blur, noise, JPEG. Prints % detected per distance.
Writes a few example composites to out/cards/check_*.jpg.
"""

from __future__ import annotations

import argparse
import json
import random
import time
from pathlib import Path

import cv2
import numpy as np

from .bench_markers import F_PX, composite, load_bgs
from .markers import TAG_IDS, MarkerDetector

ROOT = Path(__file__).resolve().parent.parent
CARD_W_M = 0.2794  # US Letter landscape width


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cards", type=Path, default=ROOT / "out" / "cards")
    ap.add_argument("--config", type=Path, default=Path(__file__).with_name("cards.json"))
    ap.add_argument("--frames", type=Path, default=ROOT / "data" / "frames")
    ap.add_argument("--trials", type=int, default=60)
    ap.add_argument("--dists", default="1.0,1.5,2.0,2.5,3.0,3.5")
    ap.add_argument("--max-tilt", type=float, default=40)
    args = ap.parse_args()
    cfg = json.loads(args.config.read_text())
    det = MarkerDetector(family=cfg.get("family", "DICT_APRILTAG_25h9"))
    bgs = load_bgs(args.frames, 60, seed=3)
    dists = [float(d) for d in args.dists.split(",")]
    rng = random.Random(7)
    totals = {d: [0, 0] for d in dists}
    ms = []
    for card in cfg["cards"]:
        lab = card["label"]
        img = cv2.imread(str(args.cards / f"{lab}.png"))
        small = cv2.resize(img, (1280, int(1280 * img.shape[0] / img.shape[1])), interpolation=cv2.INTER_AREA)
        straight = [b.label for b in det.detect(small)]
        row = []
        for d in dists:
            hit = 0
            for t in range(args.trials):
                frame, _ = composite(bgs[(t + 7 * len(row)) % len(bgs)], img, F_PX * CARD_W_M / d, rng, max_tilt=args.max_tilt)
                t0 = time.perf_counter()
                boxes = det.detect(frame)
                ms.append((time.perf_counter() - t0) * 1000)
                ok = any(b.label == lab for b in boxes)
                hit += ok
                if t == 0 and d in (2.0, 3.0):
                    cv2.imwrite(str(args.cards / f"check_{lab}_{d:.1f}m.jpg"), frame)
            totals[d][0] += hit
            totals[d][1] += args.trials
            row.append(100 * hit / args.trials)
        print(f"{lab:14s} tag {TAG_IDS[lab]}  straight: {'OK' if lab in straight else 'MISS'} {straight}   "
              + "  ".join(f"{d:.1f}m {r:5.1f}%" for d, r in zip(dists, row)), flush=True)
    print("all cards     " + "  ".join(f"{d:.1f}m {100 * h / n:5.1f}%" for d, (h, n) in totals.items())
          + f"   | detect {np.median(ms):.1f} ms median, p95 {np.percentile(ms, 95):.1f}")


if __name__ == "__main__":
    main()
