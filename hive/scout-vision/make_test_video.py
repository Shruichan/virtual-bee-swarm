"""Slideshow test video from held-out photos: object 3 s, background 2 s, 1280x720 15 fps H.264.

    uv run python make_test_video.py                  # data/test.mp4 + data/test_truth.json
    uv run python make_test_video.py --rounds 3       # each label shown 3 times (different photos)

Truth JSON: {"segments": [{"label": "background"|<object>, "start_ms", "end_ms", "file"}], ...}.
"""

from __future__ import annotations

import argparse
import json
import random
import shutil
import subprocess
from pathlib import Path

from PIL import Image

from scout_vision.recognizer import BACKGROUND, LABELS, ROOT

TEST = ROOT / "data" / "test"
W, H = 1280, 720


def letterbox(src: Path, dst: Path) -> None:
    im = Image.open(src).convert("RGB")
    im.thumbnail((W, H))
    canvas = Image.new("RGB", (W, H), (24, 24, 24))
    canvas.paste(im, ((W - im.width) // 2, (H - im.height) // 2))
    canvas.save(dst, quality=92)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--rounds", type=int, default=2)
    ap.add_argument("--obj-s", type=float, default=3.0)
    ap.add_argument("--bg-s", type=float, default=2.0)
    ap.add_argument("--seed", type=int, default=3)
    ap.add_argument("--out", default=str(ROOT / "data" / "test.mp4"))
    a = ap.parse_args()
    rng = random.Random(a.seed)
    pools = {c: sorted((TEST / c).glob("*.jpg")) for c in [*LABELS, BACKGROUND]}
    for c in pools:
        rng.shuffle(pools[c])
    order: list[tuple[str, Path, float]] = []
    bg_i = 0

    def bg() -> None:
        nonlocal bg_i
        order.append((BACKGROUND, pools[BACKGROUND][bg_i % len(pools[BACKGROUND])], a.bg_s))
        bg_i += 1

    bg()
    for r in range(a.rounds):
        labels = LABELS[:]
        rng.shuffle(labels)
        for c in labels:
            order.append((c, pools[c][r % len(pools[c])], a.obj_s))
            bg()

    frames = ROOT / "data" / "video_frames"
    shutil.rmtree(frames, ignore_errors=True)
    frames.mkdir(parents=True)
    lines, segs, t = [], [], 0.0
    for i, (c, src, dur) in enumerate(order):
        dst = frames / f"{i:03d}.jpg"
        letterbox(src, dst)
        lines += [f"file '{dst}'", f"duration {dur}"]
        segs.append({"label": c, "start_ms": round(t * 1000), "end_ms": round((t + dur) * 1000), "file": f"{c}/{src.name}"})
        t += dur
    lines.append(f"file '{frames / f'{len(order) - 1:03d}.jpg'}'")  # concat demuxer quirk: repeat the last file
    listing = frames / "list.txt"
    listing.write_text("\n".join(lines) + "\n")
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", str(listing),
                    "-vf", f"fps=15,scale={W}:{H},format=yuv420p", "-c:v", "libx264", "-preset", "fast", "-crf", "23",
                    "-t", f"{t:.3f}", a.out], check=True)
    truth = Path(a.out).with_name(Path(a.out).stem + "_truth.json")
    truth.write_text(json.dumps({"video": a.out, "fps": 15, "duration_ms": round(t * 1000), "segments": segs}, indent=1))
    print(f"{a.out} ({t:.0f} s, {sum(1 for s in segs if s['label'] != BACKGROUND)} object showings) + {truth}")


if __name__ == "__main__":
    main()
