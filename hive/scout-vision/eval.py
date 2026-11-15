"""Held-out evaluation: per-label accuracy, confusion matrix, background false positives, ms/frame.

    uv run python eval.py                      # both backbones, default temperature
    uv run python eval.py --backbone clip --sweep   # also sweep the softmax temperature

A background image counts as a false positive when its top label is an object with score >= minScore and a
margin over the runner-up >= minMargin (the scout game's steadiness gate, SCOUT_TUNING).
"""

from __future__ import annotations

import argparse
import time
from pathlib import Path

import numpy as np
from PIL import Image

from scout_vision.backbones import load_backbone
from scout_vision.recognizer import BACKGROUND, CLASSES, DEFAULT_BG_FLOOR, DEFAULT_TEMP, DEFAULT_TOPK, LABELS, ROOT, Recognizer

TEST = ROOT / "data" / "test"
MIN_SCORE, MIN_MARGIN = 0.6, 0.12  # SCOUT_TUNING in hive/src/scout-protocol.ts


def load_test() -> tuple[list[Image.Image], list[str]]:
    ims, ys = [], []
    for c in CLASSES:
        for f in sorted((TEST / c).glob("*.jpg")):
            ims.append(Image.open(f).convert("RGB"))
            ys.append(c)
    return ims, ys


def gate(probs: np.ndarray, classes: list[str], min_score: float, min_margin: float) -> list[str | None]:
    """Label that would trigger (object, score and margin over thresholds), else None."""
    out = []
    for row in probs:
        o = np.argsort(-row)
        best, second = row[o[0]], row[o[1]] if len(o) > 1 else 0.0
        c = classes[o[0]]
        out.append(c if c != BACKGROUND and best >= min_score and best - second >= min_margin else None)
    return out


def report(name: str, classes: list[str], probs: np.ndarray, ys: list[str], min_score: float, min_margin: float, verbose: bool) -> dict:
    pred = [classes[i] for i in probs.argmax(axis=1)]
    trig = gate(probs, classes, min_score, min_margin)
    res: dict = {"per_label": {}, "triggered": {}}
    for c in LABELS:
        idx = [i for i, y in enumerate(ys) if y == c]
        res["per_label"][c] = (sum(pred[i] == c for i in idx), len(idx))
        res["triggered"][c] = (sum(trig[i] == c for i in idx), len(idx))
    bg = [i for i, y in enumerate(ys) if y == BACKGROUND]
    res["bg_fp"] = (sum(trig[i] is not None for i in bg), len(bg))
    res["bg_top_obj"] = (sum(pred[i] != BACKGROUND for i in bg), len(bg))
    obj = [i for i, y in enumerate(ys) if y != BACKGROUND]
    res["wrong_trigger"] = sum(trig[i] is not None and trig[i] != ys[i] for i in obj)
    accs = [a / n for a, n in res["per_label"].values() if n]
    res["mean_acc"] = float(np.mean(accs)) if accs else 0.0
    trs = [a / n for a, n in res["triggered"].values() if n]
    res["mean_trig"] = float(np.mean(trs)) if trs else 0.0
    if verbose:
        print(f"\n== {name}  (minScore {min_score}, minMargin {min_margin})")
        for c in LABELS:
            a, n = res["per_label"][c]
            t, _ = res["triggered"][c]
            print(f"  {c:14s} top-1 {a:2d}/{n:<2d}  would trigger {t:2d}/{n}")
        print(f"  mean top-1 accuracy {res['mean_acc']:.3f}   mean trigger rate {res['mean_trig']:.3f}   wrong-label triggers {res['wrong_trigger']}")
        print(f"  background: top label is an object {res['bg_top_obj'][0]}/{res['bg_top_obj'][1]}, false positive (gated) {res['bg_fp'][0]}/{res['bg_fp'][1]}")
        short = [c[:6] for c in classes]
        print("  confusion (rows = truth, cols = predicted):")
        print("  " + " " * 14 + " ".join(f"{s:>6s}" for s in short))
        for c in classes:
            row = [sum(1 for y, p in zip(ys, pred) if y == c and p == k) for k in classes]
            print(f"  {c:14s}" + " ".join(f"{v:6d}" for v in row))
    return res


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--backbone", default="both", choices=["both", "clip", "dino"])
    ap.add_argument("--temp", type=float, default=DEFAULT_TEMP, help="default: per backbone (recognizer.CALIBRATION)")
    ap.add_argument("--topk", type=int, default=DEFAULT_TOPK)
    ap.add_argument("--bg-floor", type=float, default=DEFAULT_BG_FLOOR)
    ap.add_argument("--sweep", action="store_true")
    ap.add_argument("--min-score", type=float, default=MIN_SCORE)
    ap.add_argument("--min-margin", type=float, default=MIN_MARGIN)
    a = ap.parse_args()
    ims, ys = load_test()
    print(f"test set: {len(ims)} images, " + ", ".join(f"{c} {ys.count(c)}" for c in CLASSES))
    for kind in (["clip", "dino"] if a.backbone == "both" else [a.backbone]):
        bb = load_backbone(kind)
        rec = Recognizer(bb, topk=a.topk, temp=a.temp, bg_floor=a.bg_floor, skip_prefixes=("glasses_", "slide_", "live_"))
        # ms/frame, single images as in the live loop (after warm-up)
        for im in ims[:3]:
            rec.score([im])
        t = []
        for im in ims[:40]:
            t0 = time.perf_counter()
            rec.score([im])
            t.append((time.perf_counter() - t0) * 1000)
        embs = np.concatenate([rec.embed(ims[i:i + 32]) for i in range(0, len(ims), 32)])
        classes, sims = rec.class_sims(embs)
        report(f"{bb.name} on {getattr(bb, 'device', '?')}, top-{a.topk}, T={rec.temp}, bg floor {rec.bg_floor}", classes, rec.calibrate(sims), ys,
               a.min_score, a.min_margin, True)
        print(f"  ms/frame (single image, incl. preprocessing): median {np.median(t):.1f}, p95 {np.percentile(t, 95):.1f}")
        if a.sweep:
            print("  sweep: T, bg floor -> mean trigger rate / background FP / wrong-label triggers / top-1 (best 12 by trigger - 3*FP rate)")
            rows = []
            for floor in [0.0, *np.arange(0.1, 0.86, 0.025)]:
                classes, sims = rec.class_sims(embs, bg_floor=float(floor))
                for T in (0.005, 0.0075, 0.01, 0.0125, 0.015, 0.02, 0.025, 0.03, 0.04, 0.05, 0.06, 0.08):
                    r = report("", classes, rec.calibrate(sims, T), ys, a.min_score, a.min_margin, False)
                    fp = r["bg_fp"][0] / max(1, r["bg_fp"][1])
                    rows.append((r["mean_trig"] - 3 * fp - 0.02 * r["wrong_trigger"], T, floor, r))
            for _, T, floor, r in sorted(rows, key=lambda x: -x[0])[:12]:
                print(f"    T={T:<7} floor={floor:.3f}  trigger {r['mean_trig']:.3f}  bg FP {r['bg_fp'][0]}/{r['bg_fp'][1]}  wrong {r['wrong_trigger']}  top-1 {r['mean_acc']:.3f}")


if __name__ == "__main__":
    main()
