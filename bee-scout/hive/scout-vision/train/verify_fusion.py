"""Measure OCR + SIFT fusion: synthetic name cards (picture above, bold name below) composited into real glasses
frames, false positives on card-free frames, wrong-class count, ms/frame.

    uv run python -m train.verify_fusion --refs detect/card_images --frames train/data/s1 [--out out/verify_fusion]

Card = US Letter landscape: the reference picture on top, the name in bold dark letters (4.5 cm tall on paper)
below. Widths 90..380 px in a 1280x720 frame put the letters at ~15-60 px.
"""

from __future__ import annotations

import argparse
import random
import time
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

from train.fusion import CardScout
from train.matcher import BACKGROUND, LABELS, find_refs
from train.verify_matcher import composite

NAMES = {"native_flower": "Sunflower", "honey_jar": "Honeycomb", "bug_spray": "Bug spray", "bee_hotel": "Bee hotel",
         "pebble_dish": "Bee water dish", "beehive": "Beehive"}
SIZES = (90, 140, 200, 280, 380)
FONT = "/System/Library/Fonts/Supplemental/Arial Rounded Bold.ttf"


def make_card(ref_bgr: np.ndarray, name: str) -> np.ndarray:
    W, H = 1100, 850  # 27.9 x 21.6 cm
    page = Image.new("RGB", (W, H), (255, 252, 244))
    ref = Image.fromarray(cv2.cvtColor(ref_bgr, cv2.COLOR_BGR2RGB))
    ref.thumbnail((W - 80, 590))
    page.paste(ref, ((W - ref.width) // 2, 30))
    size = int(4.5 / 27.9 * W / 0.72)  # cap height ~ 4.5 cm
    d = ImageDraw.Draw(page)
    f = ImageFont.truetype(FONT, size)
    while d.textlength(name, font=f) > W - 60:
        size -= 6
        f = ImageFont.truetype(FONT, size)
    d.text((W / 2, 30 + 590 + (H - 620) / 2), name, font=f, fill=(35, 28, 20), anchor="mm")
    return cv2.cvtColor(np.asarray(page), cv2.COLOR_RGB2BGR)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--refs", type=Path, default=Path("detect/card_images"))
    ap.add_argument("--frames", type=Path, default=Path("train/data/s1"))
    ap.add_argument("--out", type=Path, default=Path("out/verify_fusion"))
    ap.add_argument("--trials", type=int, default=8)
    ap.add_argument("--fp", type=int, default=300)
    ap.add_argument("--no-ocr", action="store_true")
    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    rng = random.Random(11)
    frames = sorted(args.frames.rglob("*.jpg"))
    scout = CardScout(args.refs, ocr=not args.no_ocr)
    cards = {lb: make_card(cv2.imread(str(p)), NAMES[lb]) for lb, p in find_refs(args.refs).items()}
    cv2.imwrite(str(args.out / "card_native_flower.jpg"), cards["native_flower"])
    times = []

    def ev(bgr):
        rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
        t0 = time.perf_counter()
        hits, texts, sift, dbg = scout.evidence(rgb)
        label, raw, box, reason = scout.decide(hits, sift)
        times.append((time.perf_counter() - t0) * 1000)
        return label, reason, hits, sift

    print(f"{'label':14s}" + "".join(f"{s:>9d}" for s in SIZES) + "   (final correct / OCR read)   wrong")
    tot = {s: [0, 0] for s in SIZES}
    wrong_all = 0
    for lb in LABELS:
        row, wrong = [], 0
        for s in SIZES:
            ok = ocr_ok = 0
            for t in range(args.trials):
                bg = cv2.resize(cv2.imread(str(rng.choice(frames))), (1280, 720))
                img, gt = composite(bg, cards[lb], s, rng)
                label, reason, hits, sift = ev(img)
                ok += label == lb
                ocr_ok += any(h["label"] == lb and h["strong"] for h in hits)
                if label not in (lb, BACKGROUND):
                    wrong += 1
                    cv2.imwrite(str(args.out / f"wrong_{lb}_{s}_{t}.jpg"), img)
                if t == 0 and s in (90, 140):
                    cv2.imwrite(str(args.out / f"{lb}_{s}_{label}_{reason}.jpg"), img)
            row.append(f"{ok * 100 // args.trials:3d}/{ocr_ok * 100 // args.trials:3d}")
            tot[s][0] += ok
            tot[s][1] += ocr_ok
        wrong_all += wrong
        print(f"{lb:14s}" + "".join(f"{r:>9s}" for r in row) + f"   {wrong}")
    n = len(LABELS) * args.trials
    print(f"{'all':14s}" + "".join(f"{tot[s][0] * 100 // n:3d}/{tot[s][1] * 100 // n:3d}".rjust(9) for s in SIZES) + f"   {wrong_all}")
    step = max(1, len(frames) // args.fp)
    fp_raw = fp_voted = 0
    fp_frames = frames[::step][: args.fp]
    for i, f in enumerate(fp_frames):
        bgr = cv2.resize(cv2.imread(str(f)), (1280, 720))
        rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
        t0 = time.perf_counter()
        top, scores, boxes = scout.process(rgb, i * 500.0)
        times.append((time.perf_counter() - t0) * 1000)
        if boxes:
            fp_raw += 1
            print(f"  FP {f}: {boxes}  ocr={[t['text'] for t in scout.ocr.last_texts] if scout.ocr else []}")
        fp_voted += top[0]["label"] != BACKGROUND and top[0]["score"] >= 0.6
    print(f"false positives on {len(fp_frames)} card-free frames: {fp_raw} raw, {fp_voted} after the vote")
    t = np.array(times)
    print(f"ms/frame (OCR + SIFT in parallel): mean {t.mean():.1f}  p50 {np.median(t):.1f}  p95 {np.percentile(t, 95):.1f}")


if __name__ == "__main__":
    main()
