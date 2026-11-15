"""Replay a recorded glasses session through the recognizer + a copy of the scout game rule.

    uv run python replay_eval.py data/sessions/s1                  # diagnosis + before/after table
    uv run python replay_eval.py data/sessions/s1 --all            # whole session, not just the countdown window

"before" = the frames the live worker actually sent (events.jsonl) and the real "found" events; the model rows
decode the recorded video at the worker's cadence (10 fps) and re-run recognition offline (15 crops embedded once,
cached), so pipeline variants (crops, refs, smoothing, thresholds) are compared on the same footage. Session refs
are scored leave-one-slide-out. Object slides the stream never delivered (glasses Wi-Fi drops) are listed apart.

Game rule (hive/src/scout.ts): top-1 is an object, score >= minScore, top1 - top2 >= minMargin, held on every
frame for >= steadyMs; a frame gap > 1500 ms breaks the run. Practice mode: each slide fires at most once.
"""

from __future__ import annotations

import argparse
import statistics
import sys
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from scout_vision.recognizer import BACKGROUND, CLASSES, LABELS, crop_boxes

STALE_MS = 1500


@dataclass
class Rule:
    steady_ms: float = 900
    min_score: float = 0.6
    min_margin: float = 0.12
    per_label_score: dict = field(default_factory=dict)

    def name(self) -> str:
        extra = f" per-label {self.per_label_score}" if self.per_label_score else ""
        return f"steady {self.steady_ms:.0f} / score {self.min_score} / margin {self.min_margin}{extra}"


def fire_times(frames: list[tuple[float, list[dict]]], rule: Rule) -> list[tuple[float, str, float]]:
    """frames: (t_ms, top) in time order -> [(t_fire, label, since)] like ScoutGame.frame (no busy/cooldown)."""
    out, run = [], None
    for t, top in frames:
        if not top:
            run = None
            continue
        best = top[0]
        second = top[1]["score"] if len(top) > 1 else 0.0
        lab = best["label"]
        thr = rule.per_label_score.get(lab, rule.min_score)
        counts = lab in LABELS and best["score"] >= thr and best["score"] - second >= rule.min_margin
        if not counts:
            run = None
            continue
        if run is None or run["label"] != lab or t - run["last"] > STALE_MS:
            run = {"label": lab, "since": t, "last": t, "fired": False}
        else:
            run["last"] = t
        if not run["fired"] and t - run["since"] >= rule.steady_ms:
            out.append((t, lab, run["since"]))
            run["fired"] = True  # the same run cannot fire twice (practice: same slide cannot re-fire)
    return out


LAG_MS = 400  # the camera shows a slide ~0.3 s after the screen does (stream latency, measured below)


def score_slides(sess, fires: list[tuple[float, str, float]], slides, covered=None) -> dict:
    """Per object slide: first correct fire in [start, end + LAG_MS) (the camera lags the screen). Any other
    object fire inside a slide (a blank, or the wrong label) is a false trigger. covered(slide) -> bool limits
    the object count to slides the stream actually delivered."""
    res = {"obj": 0, "hit": 0, "lat": [], "false_blank": 0, "false_wrong": 0, "per": [], "nostream": 0}
    used = set()
    for s in slides:
        if s.label is None:
            continue
        ok = [f for f in fires if s.start <= f[0] < s.end + LAG_MS and f[1] == s.label]
        if covered is not None and not covered(s):
            res["nostream"] += 1
            used.update(ok)
            continue
        res["obj"] += 1
        if ok:
            res["hit"] += 1
            res["lat"].append(ok[0][0] - s.start)
            used.update(ok)
        res["per"].append((s, ok[0][0] - s.start if ok else None))
    for f in fires:
        if f in used:
            continue
        lab, sl = sess.label_at(f[0])
        if sl is None:
            continue
        if sl.label is None:
            res["false_blank"] += 1
        else:
            res["false_wrong"] += 1
    return res


def fmt(res: dict) -> str:
    lat = res["lat"]
    med = f"{statistics.median(lat) / 1000:.2f}s" if lat else "-"
    p90 = f"{np.percentile(lat, 90) / 1000:.2f}s" if lat else "-"
    pct = 100 * res["hit"] / max(1, res["obj"])
    ns = f" (+{res['nostream']} no video)" if res["nostream"] else ""
    return f"{res['hit']:2d}/{res['obj']:<2d}{ns} ({pct:3.0f}%)  median {med:>6s}  p90 {p90:>6s}  false on blanks {res['false_blank']}  wrong label {res['false_wrong']}"


# ------------------------------------------------------------------ offline recognition on the video


def embed_video(sess, bb, t0: float, t1: float, fps: float, mode: str, cache: Path) -> tuple[np.ndarray, np.ndarray]:
    """(times[N], embs[N, n_crops, D]) for video frames in [t0, t1) at `fps`, cached on disk."""
    if cache.exists():
        z = np.load(cache)
        return z["times"], z["embs"]
    times, embs = [], []
    for wall, rgb in sess.video_frames(fps=fps, t0=t0, t1=t1):
        embs.append(bb.embed_crops(rgb, crop_boxes(rgb.shape[1], rgb.shape[0], mode)))
        times.append(wall)
        if len(times) % 500 == 0:
            print(f"  embedded {len(times)} frames", file=sys.stderr, flush=True)
    times, embs = np.array(times), np.stack(embs)
    cache.parent.mkdir(parents=True, exist_ok=True)
    np.savez(cache, times=times, embs=embs)
    return times, embs


def softmax(sims: np.ndarray, temp: float) -> np.ndarray:
    z = sims / temp
    z = z - z.max(axis=-1, keepdims=True)
    p = np.exp(z)
    return p / p.sum(axis=-1, keepdims=True)


def to_top(p: np.ndarray) -> list[dict]:
    o = np.argsort(-p)[:3]
    return [{"label": CLASSES[i], "score": float(p[i])} for i in o]


def session_refs(times, embs, crops, slides, slide_of, pick: dict, lo=1000, hi=3500, bg_every=5):
    """Session calibration refs, as calibrate_from_session.py builds them: (embs, class idx, slide idx)."""
    E, C, S = [], [], []
    in_obj = np.zeros(len(times), bool)
    for k, sl in enumerate(slides):
        if sl.label is None:
            continue
        in_obj[(times >= sl.start) & (times < sl.end + 500)] = True
        for i in np.where((times >= sl.start + lo) & (times < min(sl.start + hi, sl.end)))[0]:
            j = int(np.argmax((embs[i, crops] @ pick[sl.label].T).max(axis=1)))
            E.append(embs[i, crops[j]]); C.append(CLASSES.index(sl.label)); S.append(k)
    bg = CLASSES.index(BACKGROUND)
    for i in np.where(~in_obj)[0][::bg_every]:
        for c in crops:
            E.append(embs[i, c]); C.append(bg); S.append(slide_of[i])
    return np.stack(E), np.array(C), np.array(S)


def recognize(times, embs, idx, crops, refs, slide_of, temp, floor, sref=None, topk=3, ema=0.0):
    """Offline copy of Recognizer.score_frame (best crop wins) + the worker's EMA; session refs are used
    leave-one-slide-out (a slide's own frames are never its references)."""
    W = np.concatenate([refs[c] for c in CLASSES if c in refs])
    Wc = np.concatenate([np.full(len(refs[c]), CLASSES.index(c)) for c in CLASSES if c in refs])
    Ws = np.full(len(W), -2)
    if sref is not None:
        W, Wc, Ws = np.concatenate([W, sref[0]]), np.concatenate([Wc, sref[1]]), np.concatenate([Ws, sref[2]])
    bg = CLASSES.index(BACKGROUND)
    out, prev, pt = [], None, -1e18
    for i in idx:
        keep = Ws != slide_of[i]
        sims, rc = embs[i, crops] @ W[keep].T, Wc[keep]
        cs = np.full((len(crops), len(CLASSES)), -1.0, np.float32)
        for j in range(len(CLASSES)):
            m = rc == j
            if m.any():
                cs[:, j] = np.sort(sims[:, m], axis=1)[:, -min(topk, int(m.sum())):].mean(axis=1)
        cs[:, bg] = np.maximum(cs[:, bg], floor)
        p = softmax(cs, temp)
        o = p.copy(); o[:, bg] = 0
        q = p[int(np.argmax(o.max(axis=1)))]
        if ema and prev is not None and times[i] - pt < STALE_MS:
            q = ema * prev + (1 - ema) * q
        prev, pt = q, times[i]
        out.append((float(times[i]), to_top(q)))
    return out


def stream_latency(times, embs, slides) -> list[float]:
    """Slide shown -> the camera frame changes most (the screen content switching): a recognition-free
    estimate of glasses capture + Wi-Fi + RTMP/RTSP relay latency, 100 ms resolution at 10 fps."""
    out = []
    for sl in slides:
        idx = np.where((times >= sl.start - 300) & (times < sl.start + 2500))[0]
        if len(idx) < 8 or idx[0] == 0:
            continue
        d = 1 - (embs[idx - 1] * embs[idx]).sum(-1).min(axis=1)
        j = int(np.argmax(d))
        if d[j] > 0.08:
            out.append(times[idx[j]] - sl.start)
    return out


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("session")
    ap.add_argument("--all", action="store_true", help="whole session instead of the countdown window")
    ap.add_argument("--fps", type=float, default=10)
    ap.add_argument("--no-session-refs", action="store_true", help="skip the (slow) session-calibration rows")
    a = ap.parse_args()

    from scout_vision.backbones import load_backbone
    from scout_vision.recognizer import Recognizer
    from scout_vision.session import Session

    sess = Session(Path(a.session))
    w0, w1 = (0, float("inf")) if a.all else sess.window
    slides = [s for s in sess.slides if w0 <= s.start < w1]
    print(f"session {sess.folder.name}: window {w0:.0f}..{w1:.0f}, {len(slides)} slides "
          f"({sum(1 for s in slides if s.label)} object), {len(sess.segments)} video segments")
    rules = {"current": Rule(), "proposed": Rule(steady_ms=500)}

    bb = load_backbone("dino")
    web_only = Recognizer(bb, skip_prefixes=("slide_", "glasses_", "live_"))
    with_slides = Recognizer(bb, skip_prefixes=("glasses_", "live_"))
    end = min(w1, max(s.end for s in sess.slides)) if sess.slides else w1
    cache = Path("cache") / f"session_{sess.folder.name}_{int(end)}_{a.fps:g}fps_grid15.npz"
    times, embs = embed_video(sess, bb, 0, end, a.fps, "grid15", cache)
    slide_of = np.full(len(times), -1)
    for k, sl in enumerate(sess.slides):
        slide_of[(times >= sl.start) & (times < sl.end)] = k
    idx = np.where((times >= w0) & (times < w1) & (slide_of >= 0))[0]

    def covered(s) -> bool:  # the stream delivered at least half the slide
        n = ((times >= s.start + 300) & (times < s.end)).sum()
        return n >= 0.5 * (s.end - s.start - 300) / (1000 / a.fps)

    # ---------------- diagnosis
    n_obj = sum(1 for s in slides if s.label)
    n_cov = sum(1 for s in slides if s.label and covered(s))
    lat = stream_latency(times, embs, slides)
    fps = [e["e"]["frame"]["fps"] for e in sess.frames if w0 <= e["t"] < w1]
    print(f"\nDIAGNOSIS\n  object slides with video: {n_cov}/{n_obj} (the rest fell in glasses stream drops)")
    if fps:
        print(f"  live worker fps: median {statistics.median(fps):.1f}, p10 {np.percentile(fps, 10):.1f}")
    if lat:
        print(f"  stream latency (screen -> camera frame): median {statistics.median(lat):.0f} ms, p90 {np.percentile(lat, 90):.0f} ms")

    # ---------------- before: what actually happened
    found = [(e["t"], e["e"]["label"], e["t"]) for e in sess.events if e["e"].get("type") == "found"]
    logged = [(e["t"], e["e"]["frame"]["top"]) for e in sess.frames]
    print("\nBEFORE (live, object slides that had video)")
    print(f"  {'real found events':44s}          {fmt(score_slides(sess, found, slides, covered))}")
    print(f"  {'logged frames + current rule':44s}          {fmt(score_slides(sess, fire_times(logged, rules['current']), slides, covered))}")

    print("\nOFFLINE REPLAY of the recorded video (10 fps)")
    rows = [
        ("A centre crop, web refs (= live worker)", [0], web_only.refs, None, 0.0),
        ("B 15 crops, web refs", list(range(15)), web_only.refs, None, 0.0),
        ("C 15 crops, web + slide-photo refs", list(range(15)), with_slides.refs, None, 0.0),
        ("D C + EMA 0.3 (worker default)", list(range(15)), with_slides.refs, None, 0.3),
    ]
    if not a.no_session_refs:
        sref = session_refs(times, embs, list(range(15)), sess.slides, slide_of, with_slides.refs)
        rows.append(("E C + session-footage refs (leave-1-out)", list(range(15)), with_slides.refs, sref, 0.0))
    for name, crops, refs, sref, ema in rows:
        frames = recognize(times, embs, idx, crops, refs, slide_of, web_only.temp, web_only.bg_floor, sref=sref, ema=ema)
        for rname, rule in rules.items():
            print(f"  {name:44s} {rname:9s}{fmt(score_slides(sess, fire_times(frames, rule), slides, covered))}", flush=True)


if __name__ == "__main__":
    main()
