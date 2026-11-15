"""Apply the scout game's steadiness rule to worker --print output and score it against the truth timeline.

    uv run python worker.py --source data/test.mp4 --print --no-api > data/test_frames.jsonl
    uv run python replay_check.py data/test_frames.jsonl data/test_truth.json

Rule (SCOUT_TUNING): the same object label leads, with score >= minScore and margin >= minMargin, on every frame
for >= steadyMs -> one "found". The run must break (another label, or the gate fails) before it can fire again.
Uses each frame's pos_ms (video time) + frame_ms (inference time) as the moment the decision exists.
"""

from __future__ import annotations

import argparse
import json
import statistics

STEADY_MS, MIN_SCORE, MIN_MARGIN = 900, 0.6, 0.12


def founds(frames: list[dict], steady: float, min_score: float, min_margin: float) -> list[tuple[float, str]]:
    out, run_label, run_start, fired = [], None, 0.0, False
    for f in frames:
        t = f["pos_ms"] + f.get("frame_ms", 0)
        top = f["top"]
        best = top[0]
        margin = best["score"] - (top[1]["score"] if len(top) > 1 else 0.0)
        ok = best["label"] != "background" and best["score"] >= min_score and margin >= min_margin
        label = best["label"] if ok else None
        if label != run_label:
            run_label, run_start, fired = label, f["pos_ms"], False
        if label and not fired and t - run_start >= steady:
            out.append((t, label))
            fired = True
    return out


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("frames")
    ap.add_argument("truth")
    ap.add_argument("--steady-ms", type=float, default=STEADY_MS)
    ap.add_argument("--min-score", type=float, default=MIN_SCORE)
    ap.add_argument("--min-margin", type=float, default=MIN_MARGIN)
    a = ap.parse_args()
    frames = [json.loads(ln) for ln in open(a.frames) if ln.startswith("{")]
    frames = [f for f in frames if "pos_ms" in f]
    segs = json.load(open(a.truth))["segments"]
    events = founds(frames, a.steady_ms, a.min_score, a.min_margin)

    def seg_at(t: float) -> dict | None:
        return next((s for s in segs if s["start_ms"] <= t < s["end_ms"]), None)

    hits: dict[int, float] = {}
    false: list[str] = []
    for t, label in events:
        s = seg_at(t)
        i = segs.index(s) if s else -1
        if s and s["label"] == label and i not in hits:
            hits[i] = t - s["start_ms"]
        else:
            false.append(f"  {t / 1000:6.2f}s found {label} during {s['label'] if s else 'end'} ({s['file'] if s else ''})")
    objs = [(i, s) for i, s in enumerate(segs) if s["label"] != "background"]
    fps = len(frames) / max(1e-6, (frames[-1]["pos_ms"] - frames[0]["pos_ms"]) / 1000) if frames else 0
    ms = [f["frame_ms"] for f in frames]
    print(f"frames {len(frames)} (~{fps:.1f} processed/s), inference median {statistics.median(ms):.1f} ms, max {max(ms):.1f} ms")
    print(f"rule: steady {a.steady_ms:.0f} ms, minScore {a.min_score}, minMargin {a.min_margin}")
    for i, s in objs:
        print(f"  {s['start_ms'] / 1000:6.1f}s {s['label']:14s} " + (f"found after {hits[i]:.0f} ms" if i in hits else "MISSED") + f"  ({s['file']})")
    lat = list(hits.values())
    print(f"objects detected {len(hits)}/{len(objs)}")
    if lat:
        print(f"look-to-detect latency: median {statistics.median(lat):.0f} ms, max {max(lat):.0f} ms")
    print(f"false triggers {len(false)}")
    for ln in false:
        print(ln)


if __name__ == "__main__":
    main()
