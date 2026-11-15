"""Test video for the worker's marker path: every station card, at several sizes, with blank gaps.

    uv run python -m detect.make_card_video                  # -> out/card_test.mp4 + out/card_test.json (truth)
    uv run python worker.py --source out/card_test.mp4 --print --no-api > out/card_test.jsonl
    uv run python -m detect.make_card_video --check out/card_test.jsonl

Per card: 1.5 s blank (a real glasses frame of the booth table, no card), then the card full-screen, then
composited into real glasses frames with a slight tilt and blur at 400 px, 110 px (~1 m away at f = 385 px)
and 75 px wide (~1.5 m), 2.5 s each. --check replays the worker's JSON lines through the game rule
(top label score >= 0.6, margin >= 0.12, held >= 0.9 s) and prints, per card and size, whether it fired and
whether marker boxes came with it.
"""

from __future__ import annotations

import argparse
import json
import random
from pathlib import Path

import cv2
import numpy as np

from .bench_markers import composite, load_bgs

ROOT = Path(__file__).resolve().parent.parent
FPS = 15
W, H = 1280, 720
SIZES = ["full", 400, 110, 75]
SEG_S = 2.5
GAP_S = 1.5


def build(cards: Path, frames: Path, out: Path) -> None:
    order = ["native_flower", "honey_jar", "bug_spray", "bee_hotel", "pebble_dish", "beehive"]
    bgs = load_bgs(frames, 40, seed=9)
    rng = random.Random(2)
    vw = cv2.VideoWriter(str(out), cv2.VideoWriter_fourcc(*"mp4v"), FPS, (W, H))
    truth, n, k = [], 0, 0  # n = frames written; times come from frame counts so the truth never drifts
    for lab in order:
        card = cv2.imread(str(cards / f"{lab}.png"))
        screen = cv2.imread(str(cards / "screen" / f"{lab}.png"))
        bg = bgs[k % len(bgs)]
        for _ in range(round(GAP_S * FPS)):
            vw.write(bg)
            n += 1
        for size in SIZES:
            seg_n = round(SEG_S * FPS)
            truth.append({"label": lab, "size": size, "start_ms": n * 1000 / FPS, "end_ms": (n + seg_n) * 1000 / FPS})
            for i in range(seg_n):
                if size == "full":
                    fr = cv2.GaussianBlur(cv2.resize(screen, (W, H), interpolation=cv2.INTER_AREA), (0, 0), 0.8)
                else:
                    r = random.Random(k * 1000 + i // 5)  # pose changes every 5 frames: a slightly moving head
                    fr, _ = composite(bgs[(k + i // 15) % len(bgs)], card, float(size), r, max_tilt=25, blur=(0.4, 0.9), motion=3)
                vw.write(fr)
                n += 1
            k += 1
    vw.release()
    out.with_suffix(".json").write_text(json.dumps(truth, indent=1))
    print(out, f"{n / FPS:.0f} s")


def check(lines: Path, truth_p: Path, steady_ms: float = 900, min_score: float = 0.6, margin: float = 0.12) -> None:
    truth = json.loads(truth_p.read_text())
    frames = [json.loads(x) for x in lines.read_text().splitlines() if x.startswith("{")]
    frames = [f for f in frames if "pos_ms" in f]
    ok_all = True
    for seg in truth:
        fs = [f for f in frames if seg["start_ms"] <= f["pos_ms"] < seg["end_ms"]]
        run, fired, boxes = None, None, False
        for f in fs:
            top = f["top"]
            good = top[0]["label"] == seg["label"] and top[0]["score"] >= min_score and top[0]["score"] - top[1]["score"] >= margin
            if good:
                run = f["pos_ms"] if run is None else run
                if fired is None and f["pos_ms"] - run >= steady_ms:
                    fired = f["pos_ms"] - seg["start_ms"]
                boxes |= any(b.get("via") == "marker" and b["label"] == seg["label"] for b in f.get("boxes", []))
            else:
                run = None
        wrong = sorted({f["top"][0]["label"] for f in fs if f["top"][0]["label"] not in (seg["label"], "background") and f["top"][0]["score"] >= min_score})
        ok_all &= fired is not None and boxes
        print(f"{seg['label']:14s} {str(seg['size']):>5s}  frames {len(fs):3d}  "
              + (f"FIRED after {fired / 1000:.2f} s" if fired is not None else "no trigger      ")
              + f"  marker box {'yes' if boxes else 'NO '}" + (f"  other labels: {wrong}" if wrong else ""))
    gaps = [f for f in frames if not any(s["start_ms"] <= f["pos_ms"] < s["end_ms"] + 600 for s in truth)]
    false = [f for f in gaps if f["top"][0]["label"] != "background" and f["top"][0]["score"] >= min_score]
    print(f"blank gaps: {len(false)}/{len(gaps)} frames with an object on top")
    print("ALL CARDS FIRED WITH MARKER BOXES" if ok_all else "SOME CARDS MISSED")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cards", type=Path, default=ROOT / "out" / "cards")
    ap.add_argument("--frames", type=Path, default=ROOT / "data" / "frames")
    ap.add_argument("--out", type=Path, default=ROOT / "out" / "card_test.mp4")
    ap.add_argument("--check", type=Path, help="worker --print output to score against the truth file")
    args = ap.parse_args()
    if args.check:
        check(args.check, args.out.with_suffix(".json"))
    else:
        build(args.cards, args.frames, args.out)


if __name__ == "__main__":
    main()
