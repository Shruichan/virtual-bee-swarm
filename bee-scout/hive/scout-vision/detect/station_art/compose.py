"""Blender renders (transparent, render_dart.py) -> one clean square picture per station on the card cream.

    uv run python -m detect.station_art.compose --renders out/station_renders     # -> detect/card_images/<label>.jpg

Each render is cropped to the object, centred with a margin and scaled to 1800 x 1800 px, with a soft contact
shadow under it.
"""
from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[2]
CREAM = (255, 243, 214)
SIZE = 1800
MARGIN = 0.08


def compose(src: Path, dst: Path) -> None:
    im = Image.open(src).convert("RGBA")
    im = im.crop(im.getbbox())
    box = int(SIZE * (1 - 2 * MARGIN))
    s = box / max(im.size)
    im = im.resize((round(im.width * s), round(im.height * s)), Image.LANCZOS)
    out = Image.new("RGBA", (SIZE, SIZE), CREAM + (255,))
    x, y = (SIZE - im.width) // 2, (SIZE - im.height) // 2
    # soft contact shadow: an ellipse under the object's base
    sh = Image.new("L", (SIZE, SIZE), 0)
    w = im.width * 0.8
    ImageDraw.Draw(sh).ellipse((SIZE / 2 - w / 2, y + im.height - 40, SIZE / 2 + w / 2, y + im.height + 40), fill=90)
    out.paste((150, 120, 80, 255), (0, 0), sh.filter(ImageFilter.GaussianBlur(28)))
    out.alpha_composite(im, (x, y))
    out.convert("RGB").save(dst, quality=90)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--renders", type=Path, default=ROOT / "out" / "station_renders")
    ap.add_argument("--out", type=Path, default=ROOT / "detect" / "card_images")
    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    for p in sorted(args.renders.glob("*.png")):
        dst = args.out / f"{p.stem}.jpg"
        compose(p, dst)
        print(dst)


if __name__ == "__main__":
    main()
