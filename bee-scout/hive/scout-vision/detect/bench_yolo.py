"""Benchmark detect/yolo.py on the glasses recordings and the station card images.

    uv run python detect/bench_yolo.py --videos data/sessions/s1 --cards detect/card_images --out out/yolo

Prints ms/frame and detections per class; writes a few annotated frames to --out.
"""

from __future__ import annotations

import argparse
import sys
import time
from collections import Counter
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from detect import yolo  # noqa: E402


def sample_frames(video_dir: Path, n: int) -> list[tuple[str, np.ndarray]]:
    vids = sorted(video_dir.glob("*.mkv")) + sorted(video_dir.glob("*.mp4"))
    counts = []
    for v in vids:
        cap = cv2.VideoCapture(str(v))
        counts.append(int(cap.get(cv2.CAP_PROP_FRAME_COUNT)) or 0)
        cap.release()
    total = sum(counts)
    frames = []
    for v, c in zip(vids, counts):
        if c <= 0:
            continue
        k = max(1, round(n * c / max(total, 1)))
        cap = cv2.VideoCapture(str(v))
        for i in np.linspace(0, c - 1, k).astype(int):
            cap.set(cv2.CAP_PROP_POS_FRAMES, int(i))
            ok, f = cap.read()
            if ok:
                frames.append((f"{v.stem}#{i}", f))
        cap.release()
    return frames


def draw(frame: np.ndarray, boxes: list[dict], color=(0, 200, 255)) -> np.ndarray:
    img = frame.copy()
    h, w = img.shape[:2]
    for b in boxes:
        x1, y1 = int(b["x"] * w), int(b["y"] * h)
        x2, y2 = int((b["x"] + b["w"]) * w), int((b["y"] + b["h"]) * h)
        cv2.rectangle(img, (x1, y1), (x2, y2), color, 3)
        cv2.putText(img, f'{b["label"]} {b["score"]:.2f}', (x1 + 4, max(y1 - 8, 20)), cv2.FONT_HERSHEY_SIMPLEX, 0.8, color, 2)
    return img


def run(name: str, frames, fn, out: Path, n_save: int, also_raw: bool) -> None:
    times, per_class, raw_class, hits = [], Counter(), Counter(), []
    for tag, f in frames:
        t0 = time.perf_counter()
        boxes = fn(f)
        times.append((time.perf_counter() - t0) * 1000)
        per_class.update(b["label"] for b in boxes)
        raw = yolo.raw_detect(f) if also_raw else []
        raw_class.update(b["label"] for b in raw)
        hits.append((tag, f, boxes, raw))
    t = np.array(times)
    print(f"\n== {name}: {len(frames)} frames  ms/frame mean {t.mean():.1f}  p50 {np.median(t):.1f}  p95 {np.percentile(t, 95):.1f}")
    print(f"   frames with a mapped box: {sum(1 for h in hits if h[2])}/{len(hits)}")
    print(f"   mapped boxes per class: {dict(per_class.most_common())}")
    if also_raw:
        print(f"   raw COCO boxes per class: {dict(raw_class.most_common(15))}")
    out.mkdir(parents=True, exist_ok=True)
    picks = [h for h in hits if h[2]][: n_save // 2]
    picks += [h for h in hits if not h[2] and h[3]][: n_save - len(picks)]
    for tag, f, boxes, raw in picks:
        img = draw(f, [r for r in raw if r not in boxes], (180, 180, 180)) if raw else f
        img = draw(img, boxes)
        p = out / f"{name}_{tag.replace('#', '_')}.jpg"
        cv2.imwrite(str(p), cv2.resize(img, (img.shape[1] * 720 // img.shape[0], 720)) if img.shape[0] > 720 else img)
        print(f"   wrote {p}  {[(b['label'], b['score']) for b in boxes]}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--videos", type=Path)
    ap.add_argument("--cards", type=Path)
    ap.add_argument("--n", type=int, default=200)
    ap.add_argument("--out", type=Path, default=Path("out/yolo"))
    ap.add_argument("--save", type=int, default=6)
    a = ap.parse_args()

    t0 = time.perf_counter()
    yolo.warmup()
    print(f"model {yolo.MODEL} on {yolo._device()} loaded in {time.perf_counter() - t0:.1f}s")
    sets = []
    if a.videos:
        sets.append(("video", sample_frames(a.videos, a.n)))
    if a.cards:
        cards = [(p.stem, cv2.imread(str(p))) for p in sorted(a.cards.iterdir()) if p.suffix.lower() in {".jpg", ".jpeg", ".png"}]
        sets.append(("cards", [(t, f) for t, f in cards if f is not None]))
    for name, frames in sets:
        run(f"yolo_{name}", frames, yolo.detect, a.out, a.save, True)


if __name__ == "__main__":
    main()
