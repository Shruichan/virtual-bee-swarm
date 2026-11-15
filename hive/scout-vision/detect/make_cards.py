"""Printable Bee Scout station cards: big object photo + name + an AprilTag "station badge".

    uv run python -m detect.make_cards                       # -> out/cards/<label>.png (Letter, 300 dpi),
                                                             #    out/cards/screen/<label>.png (1920x1080),
                                                             #    out/cards/station_cards.pdf (one card per page)
    uv run python -m detect.make_cards --config my.json --out out/cards2

US Letter landscape at 300 dpi. Print at 100% ("actual size", not "fit to page") so the tag's black square
comes out at tag_cm (cards.json). The badge's white field is the tag's quiet zone: keep it white, don't
laminate with a glossy film if you can avoid it (glare hides the tag), and don't trim into it.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

from .markers import FAMILY, TAG_IDS, tag_cells

ROOT = Path(__file__).resolve().parent.parent
DPI = 300
PAGE = (3300, 2550)  # 11 x 8.5 in landscape
MARGIN = 110
INK = (43, 33, 24)
HONEY = (242, 183, 5)
HONEY_DARK = (196, 128, 0)
CREAM = (255, 248, 231)
QUIET_CELLS = 1.0  # white quiet zone around the tag's black square, in tag cells (the spec minimum; the honey frame beyond it is light too)

FONT_DIRS = ["/System/Library/Fonts/Supplemental", "/System/Library/Fonts", "/Library/Fonts", "/usr/share/fonts/truetype/dejavu"]
FONT_BOLD = ["Arial Rounded Bold.ttf", "SFNSRounded.ttf", "DejaVuSans-Bold.ttf"]
FONT_REG = ["Avenir Next.ttc", "Helvetica.ttc", "DejaVuSans.ttf"]


def font(names: list[str], size: int) -> ImageFont.FreeTypeFont:
    for d in FONT_DIRS:
        for n in names:
            p = Path(d) / n
            if p.exists():
                return ImageFont.truetype(str(p), size)
    return ImageFont.load_default(size)


def cm(v: float) -> int:
    return int(round(v / 2.54 * DPI))


def tag_pil(tag_id: int, side_px: int, family: str) -> Image.Image:
    d = cv2.aruco.getPredefinedDictionary(getattr(cv2.aruco, family))
    n = tag_cells(family)
    cell = max(1, side_px // n)
    img = cv2.aruco.generateImageMarker(d, tag_id, n * cell)  # exact multiple: crisp cell edges
    return Image.fromarray(img).convert("RGB")


def cover(im: Image.Image, w: int, h: int) -> Image.Image:
    s = max(w / im.width, h / im.height)
    im = im.resize((math.ceil(im.width * s), math.ceil(im.height * s)), Image.LANCZOS)
    l, t = (im.width - w) // 2, (im.height - h) // 2
    return im.crop((l, t, l + w, t + h))


def hexagon(cx: float, cy: float, r: float, rot: float = 30) -> list[tuple[float, float]]:
    return [(cx + r * math.cos(math.radians(rot + 60 * k)), cy + r * math.sin(math.radians(rot + 60 * k))) for k in range(6)]


def honeycomb(draw: ImageDraw.ImageDraw, x0: int, y0: int, cols: int, rows: int, r: int, color) -> None:
    w = math.sqrt(3) * r
    for j in range(rows):
        for i in range(cols):
            cx = x0 + i * w + (w / 2 if j % 2 else 0)
            cy = y0 + j * 1.5 * r
            draw.polygon(hexagon(cx, cy, r * 0.92), outline=color, width=6)


def render_card(card: dict, cfg: dict, number: int) -> tuple[Image.Image, int]:
    family = cfg.get("family", FAMILY)
    label = card["label"]
    tag_id = card.get("tag_id", TAG_IDS[label])
    n = tag_cells(family)
    tag_px = cm(float(cfg.get("tag_cm", 9.0)))
    tag_px -= tag_px % n
    cell = tag_px // n
    white = tag_px + int(2 * QUIET_CELLS * cell)  # tag + quiet zone
    frame = 46  # honey frame thickness
    badge_w = white + 2 * frame
    tab_h = 150  # "SCOUT STATION" tab above the white field

    W, H = PAGE
    page = Image.new("RGB", PAGE, CREAM)
    d = ImageDraw.Draw(page)
    honeycomb(d, -40, H - 330, 9, 4, 95, (247, 226, 170))
    honeycomb(d, W - 760, -60, 6, 3, 95, (247, 226, 170))

    # right column: the badge, vertically centred under its tab
    bx1 = W - MARGIN
    bx0 = bx1 - badge_w
    body_h = white + 2 * frame
    by0 = (H - (body_h + tab_h + 210)) // 2 + tab_h
    by1 = by0 + body_h
    # tab: a stretched hexagon ribbon
    tw = badge_w * 0.78
    tcx = (bx0 + bx1) / 2
    ty0, ty1 = by0 - tab_h - 10, by0 + 20
    tab = [(tcx - tw / 2, ty0), (tcx + tw / 2, ty0), (tcx + tw / 2 + 60, (ty0 + ty1) / 2), (tcx + tw / 2, ty1),
           (tcx - tw / 2, ty1), (tcx - tw / 2 - 60, (ty0 + ty1) / 2)]
    d.polygon(tab, fill=INK)
    ft = font(FONT_BOLD, 84)
    txt = f"SCOUT STATION {number}"
    tl = d.textlength(txt, font=ft)
    d.text((tcx - tl / 2, (ty0 + ty1) / 2 - 10), txt, font=ft, fill=HONEY, anchor="lm")
    d.rounded_rectangle((bx0, by0, bx1, by1), radius=70, fill=HONEY, outline=INK, width=10)
    d.rectangle((bx0 + frame, by0 + frame, bx1 - frame, by1 - frame), fill=(255, 255, 255))
    page.paste(tag_pil(tag_id, tag_px, family), (bx0 + frame + (white - tag_px) // 2, by0 + frame + (white - tag_px) // 2))
    # small hexagon "studs" on the badge's frame corners (decoration outside the white field)
    for (sx, sy) in [(bx0 + frame / 2, by0 + frame / 2), (bx1 - frame / 2, by0 + frame / 2),
                     (bx0 + frame / 2, by1 - frame / 2), (bx1 - frame / 2, by1 - frame / 2)]:
        d.polygon(hexagon(sx, sy, 34), fill=HONEY_DARK, outline=INK, width=5)
    fc = font(FONT_BOLD, 70)
    cap = "Scout, look here!"
    d.text(((bx0 + bx1) / 2, by1 + 95), cap, font=fc, fill=INK, anchor="mm")

    # left column: header, photo, name
    lx0, lx1 = MARGIN, bx0 - 90
    fh = font(FONT_BOLD, 74)
    d.text((lx0, MARGIN + 30), "BEE SCOUT", font=fh, fill=HONEY_DARK, anchor="lm")
    py0 = MARGIN + 110
    name_h = 300
    py1 = H - MARGIN - name_h - 40
    src = Path(card["image"])
    if not src.is_absolute():
        root = Path(cfg.get("image_root", "."))
        src = (root if root.is_absolute() else ROOT / root) / src
    photo = cover(Image.open(src).convert("RGB"), lx1 - lx0, py1 - py0)
    mask = Image.new("L", photo.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, photo.width - 1, photo.height - 1), radius=60, fill=255)
    page.paste(photo, (lx0, py0), mask)
    d.rounded_rectangle((lx0, py0, lx1, py1), radius=60, outline=INK, width=10)
    name = card["name"]
    size = 210
    fn = font(FONT_BOLD, size)
    while d.textlength(name, font=fn) > lx1 - lx0 and size > 90:
        size -= 10
        fn = font(FONT_BOLD, size)
    d.text((lx0, py1 + 40 + name_h / 2 - 30), name, font=fn, fill=INK, anchor="lm")
    if card.get("credit"):
        d.text((lx0, H - MARGIN + 30), card["credit"], font=font(FONT_REG, 34), fill=(120, 105, 90), anchor="lm")
    return page, tag_px


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", type=Path, default=Path(__file__).with_name("cards.json"))
    ap.add_argument("--out", type=Path, default=ROOT / "out" / "cards")
    args = ap.parse_args()
    cfg = json.loads(args.config.read_text())
    args.out.mkdir(parents=True, exist_ok=True)
    pages = []
    for i, card in enumerate(cfg["cards"], 1):
        page, tag_px = render_card(card, cfg, i)
        p = args.out / f"{card['label']}.png"
        page.save(p, dpi=(DPI, DPI))
        pages.append(page)
        screen = Image.new("RGB", (1920, 1080), CREAM)  # full-screen viewing: the card letterboxed
        fit = page.resize((round(1080 * PAGE[0] / PAGE[1]), 1080), Image.LANCZOS)
        screen.paste(fit, ((1920 - fit.width) // 2, 0))
        (args.out / "screen").mkdir(exist_ok=True)
        screen.save(args.out / "screen" / f"{card['label']}.png")
        print(f"{p}  tag id {card.get('tag_id', TAG_IDS[card['label']])}, black square {tag_px / DPI * 2.54:.1f} cm")
    pdf = args.out / "station_cards.pdf"
    pages[0].save(pdf, save_all=True, append_images=pages[1:], resolution=DPI)
    print(pdf)


if __name__ == "__main__":
    main()
