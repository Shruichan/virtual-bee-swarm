"""Marker range bench: synthetic tags composited into real glasses frames.

    uv run python -m detect.bench_markers --frames data/frames            # range table, speed, false positives

data/frames = real glasses footage decoded to JPEGs, e.g. detect.s1.extract(Path("data/sessions/s1"), Path("data/frames")).

For each family and tag size (black square side, in pixels at 1280x720), N trials: a random real s1 frame, a tag
on a white card (1-cell quiet zone + extra margin), random 3D tilt (yaw/pitch up to 50 deg, roll up to 30 deg),
random light gain, Gaussian + motion blur, sensor noise, and JPEG q35-60 (stands in for the H.264 stream).
Success = the right id found. Pixel size -> distance: px = f * size / distance, f = 385 px for a 118 deg
horizontal FOV at 1280 px (conservative: if 118 deg is diagonal, f = 441 px and range is 15% longer).
"""

from __future__ import annotations

import argparse
import random
import time
from pathlib import Path

import cv2
import numpy as np

from .markers import MarkerDetector, tag_cells, tag_image

F_PX = 385.0
FAMILIES = ["DICT_APRILTAG_25h9", "DICT_APRILTAG_36h11", "DICT_APRILTAG_16h5", "DICT_4X4_50"]


def rot(yaw: float, pitch: float, roll: float) -> np.ndarray:
    y, p, r = np.radians([yaw, pitch, roll])
    ry = np.array([[np.cos(y), 0, np.sin(y)], [0, 1, 0], [-np.sin(y), 0, np.cos(y)]])
    rx = np.array([[1, 0, 0], [0, np.cos(p), -np.sin(p)], [0, np.sin(p), np.cos(p)]])
    rz = np.array([[np.cos(r), -np.sin(r), 0], [np.sin(r), np.cos(r), 0], [0, 0, 1]])
    return rz @ rx @ ry


def composite(bg: np.ndarray, card: np.ndarray, card_w_px: float, rng: random.Random, max_tilt: float = 50,
              blur: tuple[float, float] = (0.3, 1.2), motion: float = 5) -> tuple[np.ndarray, np.ndarray]:
    """Paste `card` (grayscale or BGR, any aspect) into bg (BGR), card_w_px wide if it faced the camera, with a
    random 3D tilt, light gain, blur, noise and JPEG. Returns (frame BGR, 3x3 homography card px -> frame px)."""
    h, w = bg.shape[:2]
    if card.ndim == 3:
        card = cv2.cvtColor(card, cv2.COLOR_BGR2GRAY)
    ch, cw = card.shape[:2]
    hw, hh = card_w_px / 2, card_w_px * ch / cw / 2
    z = 1000.0
    pts = np.array([[-hw, -hh, 0], [hw, -hh, 0], [hw, hh, 0], [-hw, hh, 0]], float)
    R = rot(rng.uniform(-max_tilt, max_tilt), rng.uniform(-max_tilt, max_tilt), rng.uniform(-30, 30))
    p3 = pts @ R.T + [0, 0, z]
    proj = p3[:, :2] / p3[:, 2:] * z
    span = proj.max(0) - proj.min(0)
    cx = rng.uniform(span[0] / 2 + 2, w - span[0] / 2 - 2)
    cy = rng.uniform(span[1] / 2 + 2, h - span[1] / 2 - 2)
    dst = (proj + [cx, cy]).astype(np.float32)
    # source at ~2x the destination size so the warp does not alias
    sw = max(32, int(card_w_px * 2))
    sh = max(32, int(card_w_px * 2 * ch / cw))
    src_img = cv2.resize(card, (sw, sh), interpolation=cv2.INTER_AREA)
    src = np.array([[0, 0], [sw, 0], [sw, sh], [0, sh]], np.float32)
    # work in a padded ROI around the card (blur, blend and noise only matter there)
    pad = 12
    rx0, ry0 = max(0, int(dst[:, 0].min()) - pad), max(0, int(dst[:, 1].min()) - pad)
    rx1, ry1 = min(w, int(dst[:, 0].max()) + pad + 1), min(h, int(dst[:, 1].max()) + pad + 1)
    M = cv2.getPerspectiveTransform(src, (dst - np.float32([rx0, ry0])).astype(np.float32))
    rw, rh = rx1 - rx0, ry1 - ry0
    warped = cv2.warpPerspective(src_img, M, (rw, rh), flags=cv2.INTER_LINEAR, borderValue=0)
    mask = cv2.warpPerspective(np.full((sh, sw), 255, np.uint8), M, (rw, rh), flags=cv2.INTER_LINEAR, borderValue=0)
    gain = rng.uniform(0.55, 1.05)
    offset = rng.uniform(0, 25)
    card_bgr = np.repeat((warped.astype(np.float32) * gain + offset)[..., None], 3, axis=2)
    a = (mask.astype(np.float32) / 255)[..., None]
    roi = bg[ry0:ry1, rx0:rx1].astype(np.float32) * (1 - a) + card_bgr * a
    sig = rng.uniform(*blur)
    roi = cv2.GaussianBlur(roi, (0, 0), sig)
    if motion and rng.random() < 0.5:
        L = rng.randint(2, int(motion))
        k = np.zeros((L, L), np.float32)
        k[L // 2, :] = 1.0 / L
        k = cv2.warpAffine(k, cv2.getRotationMatrix2D((L / 2 - 0.5, L / 2 - 0.5), rng.uniform(0, 180), 1), (L, L))
        k /= max(k.sum(), 1e-6)
        roi = cv2.filter2D(roi, -1, k)
    roi += np.random.default_rng(rng.randint(0, 1 << 30)).normal(0, 3, roi.shape)
    out = bg.copy()
    out[ry0:ry1, rx0:rx1] = np.clip(roi, 0, 255).astype(np.uint8)
    ok, enc = cv2.imencode(".jpg", out, [cv2.IMWRITE_JPEG_QUALITY, rng.randint(35, 60)])
    H = cv2.getPerspectiveTransform(np.array([[0, 0], [cw, 0], [cw, ch], [0, ch]], np.float32), dst)
    return cv2.imdecode(enc, cv2.IMREAD_COLOR), H


def tag_card(tag_id: int, family: str, margin_cells: float = 1.0) -> tuple[np.ndarray, float]:
    """Tag + 1-cell quiet zone + extra white margin; returns (image, card side / black-square side)."""
    cells = tag_cells(family)
    img = tag_image(tag_id, cells_px=24, family=family, quiet_cells=1)
    m = int(margin_cells * 24)
    img = cv2.copyMakeBorder(img, m, m, m, m, cv2.BORDER_CONSTANT, value=255)
    return img, img.shape[0] / (cells * 24)


def load_bgs(frames_dir: Path, n: int, seed: int = 0) -> list[np.ndarray]:
    paths = sorted(frames_dir.glob("*/*.jpg"))
    random.Random(seed).shuffle(paths)
    return [cv2.imread(str(p)) for p in paths[:n]]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--frames", type=Path, default=Path("data/frames"))
    ap.add_argument("--trials", type=int, default=200)
    ap.add_argument("--sizes", default="10,12,14,16,18,20,24,28,32")
    ap.add_argument("--families", default=",".join(FAMILIES))
    ap.add_argument("--fp-frames", type=int, default=1000)
    args = ap.parse_args()
    sizes = [int(s) for s in args.sizes.split(",")]
    bgs = load_bgs(args.frames, 60)
    print(f"tag side px -> distance for a 10 cm tag at f={F_PX:.0f}: " + ", ".join(f"{s}px={F_PX * 0.10 / s:.1f}m" for s in sizes))
    for fam in args.families.split(","):
        det = MarkerDetector(family=fam, tag_ids={str(i): i for i in range(6)})  # false = any of ids 0-5
        rng = random.Random(1)
        row, ms = [], []
        for s in sizes:
            hit = 0
            for t in range(args.trials):
                tid = t % 6
                card, ratio = tag_card(tid, fam)
                frame, H = composite(bgs[t % len(bgs)], card, s * ratio, rng)
                c = cv2.perspectiveTransform(np.array([[[card.shape[1] / 2, card.shape[0] / 2]]], np.float32), H)[0, 0]
                gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
                t0 = time.perf_counter()
                found = det.raw(gray)
                ms.append((time.perf_counter() - t0) * 1000)
                if any(i == tid and np.linalg.norm(q.mean(0) - c) < s for i, q in found):
                    hit += 1
            row.append(100 * hit / args.trials)
        # false positives on the raw footage (no tags in it)
        fps_any, n = 0, 0
        for p in sorted(args.frames.glob("*/*.jpg"))[: args.fp_frames]:
            g = cv2.imread(str(p), cv2.IMREAD_GRAYSCALE)
            n += 1
            fps_any += any(i < 6 for i, _ in det.raw(g))
        print(f"{fam:22s} " + "  ".join(f"{s}px:{r:5.1f}%" for s, r in zip(sizes, row))
              + f"   | {np.median(ms):.1f} ms/frame median, p95 {np.percentile(ms, 95):.1f} | false frames {fps_any}/{n}", flush=True)


if __name__ == "__main__":
    main()
