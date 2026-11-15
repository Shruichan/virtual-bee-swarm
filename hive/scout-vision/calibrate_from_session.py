"""Calibrate the refs from a recorded glasses session (data/sessions/<name>: glasses_<wall_ms>.mkv + events.jsonl).

    uv run python calibrate_from_session.py data/sessions/s1              # object + background crops from the footage
    uv run python calibrate_from_session.py data/sessions/s1 --slides     # also the slide photos themselves as refs
    uv run python calibrate_from_session.py --clear                       # remove all glasses_* and slide_* refs
    curl -X POST localhost:8242/reload                                    # hot-apply to the running worker

Object slides: frames 1.0-3.5 s after the slide showed (the camera lags the screen ~0.3 s), ~3 per second; for each
frame the crop that looks most like that label (web + slide refs) is saved as refs/<label>/glasses_<s>_<t>_c<k>.jpg.
Background: frames outside object slides (blank slides, earlier footage), every crop, every Nth frame.
--slides copies data/test/<label>/*.jpg (what /scout/slides shows) to refs/<label>/slide_*.jpg.
eval.py ignores glasses_* and slide_* refs so its held-out numbers stay honest.
"""

from __future__ import annotations

import argparse
import shutil
from pathlib import Path

import numpy as np
from PIL import Image

from scout_vision.backbones import load_backbone
from scout_vision.recognizer import BACKGROUND, CLASSES, LABELS, REFS, ROOT, Recognizer, crop_boxes
from scout_vision.session import Session

TEST = ROOT / "data" / "test"


def clear() -> int:
    n = 0
    for c in CLASSES:
        for p in [*(REFS / c).glob("glasses_*.jpg"), *(REFS / c).glob("slide_*.jpg")]:
            p.unlink()
            n += 1
    return n


def copy_slides() -> int:
    n = 0
    for lab in LABELS:
        (REFS / lab).mkdir(parents=True, exist_ok=True)
        for f in sorted((TEST / lab).glob("*.jpg")):
            shutil.copy2(f, REFS / lab / f"slide_{f.name}")
            n += 1
    return n


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("session", nargs="?")
    ap.add_argument("--slides", action="store_true", help="also enrol the slide photos (data/test) as refs")
    ap.add_argument("--clear", action="store_true", help="remove glasses_* and slide_* refs first")
    ap.add_argument("--crops", default="grid15")
    ap.add_argument("--lo", type=float, default=1000)
    ap.add_argument("--hi", type=float, default=3500)
    ap.add_argument("--obj-fps", type=float, default=3)
    ap.add_argument("--bg-every", type=float, default=2.0, help="seconds between background frames")
    ap.add_argument("--no-background", action="store_true")
    ap.add_argument("--until", type=float, default=None, help="only footage before this wall ms (keep later for testing)")
    a = ap.parse_args()

    if a.clear:
        print(f"removed {clear()} glasses_*/slide_* refs")
    if a.slides:
        print(f"copied {copy_slides()} slide photos")
    if not a.session:
        return
    sess = Session(Path(a.session))
    name = sess.folder.name
    rec = Recognizer(load_backbone("dino"))
    pick_refs = rec.refs
    until = a.until or float("inf")
    saved = {c: 0 for c in CLASSES}
    obj_spans = [(s.start, s.end + 500) for s in sess.slides if s.label]

    for s in sess.slides:
        if s.label is None or s.start >= until:
            continue
        for wall, rgb in sess.video_frames(fps=a.obj_fps, t0=s.start + a.lo, t1=min(s.start + a.hi, s.end, until)):
            boxes = crop_boxes(rgb.shape[1], rgb.shape[0], a.crops)
            e = rec.backbone.embed_crops(rgb, boxes)
            k = int(np.argmax((e @ pick_refs[s.label].T).max(axis=1)))
            im = Image.fromarray(rgb).crop(boxes[k])
            im.thumbnail((448, 448))
            im.save(REFS / s.label / f"glasses_{name}_{int(wall)}_c{k}.jpg", quality=90)
            saved[s.label] += 1

    if not a.no_background:
        (REFS / BACKGROUND).mkdir(parents=True, exist_ok=True)
        for wall, rgb in sess.video_frames(fps=1.0 / a.bg_every, t1=until):
            if any(t0 <= wall < t1 for t0, t1 in obj_spans):
                continue
            full = Image.fromarray(rgb)
            for k, b in enumerate(crop_boxes(rgb.shape[1], rgb.shape[0], a.crops)):
                im = full.crop(b)
                im.thumbnail((448, 448))
                im.save(REFS / BACKGROUND / f"glasses_{name}_{int(wall)}_c{k}.jpg", quality=90)
                saved[BACKGROUND] += 1
    print("saved", saved)


if __name__ == "__main__":
    main()
