"""Bee Scout station cards: the real photo as large as possible (unmodified), the display name below it.

    uv run python -m detect.make_cards        # -> out/cards/station_cards.pdf (US Letter portrait, 300 dpi, one per page)
                                              #    out/cards/print/<label>.png, out/cards/screen/<label>.png (1920x1080)
                                              #    out/cards/contact_sheet.jpg
    uv run python -m detect.make_cards --pdf ~/Downloads/bee-scout-print-cards.pdf   # also write the PDF there

Names, order, photos and credits are in detect/cards.json. Print at 100% / actual size.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
INK = (59, 38, 22)
FONT_DIRS = ["/System/Library/Fonts/Supplemental", "/System/Library/Fonts", "/Library/Fonts", "/usr/share/fonts/truetype/dejavu"]
FONT_BOLD = ["Arial Bold.ttf", "Arial Rounded Bold.ttf", "DejaVuSans-Bold.ttf"]


def font(size: int) -> ImageFont.FreeTypeFont:
    for d in FONT_DIRS:
        for n in FONT_BOLD:
            p = Path(d) / n
            if p.exists():
                return ImageFont.truetype(str(p), size)
    return ImageFont.load_default(size)


def render_card(photo: Image.Image, name: str, W: int, H: int, margin: int, name_h: int, name_px: int) -> Image.Image:
    """White page; the photo scaled (aspect kept, nothing cropped) into the area above a name band."""
    page = Image.new("RGB", (W, H), (255, 255, 255))
    box_w, box_h = W - 2 * margin, H - 2 * margin - name_h
    s = min(box_w / photo.width, box_h / photo.height)
    im = photo.resize((round(photo.width * s), round(photo.height * s)), Image.LANCZOS)
    x = (W - im.width) // 2
    y = margin + (box_h - im.height) // 2
    page.paste(im, (x, y))
    d = ImageDraw.Draw(page)
    d.text((W / 2, y + im.height + name_h / 2), name, font=font(name_px), fill=INK, anchor="mm")
    return page


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", type=Path, default=Path(__file__).with_name("cards.json"))
    ap.add_argument("--out", type=Path, default=ROOT / "out" / "cards")
    ap.add_argument("--pdf", type=Path, help="extra copy of the print PDF")
    args = ap.parse_args()
    cfg = json.loads(args.config.read_text())
    root = ROOT / cfg.get("image_root", "detect/card_images")
    for sub in ("screen", "print"):
        (args.out / sub).mkdir(parents=True, exist_ok=True)
    pages, thumbs = [], []
    for card in cfg["cards"]:
        photo = Image.open(root / card["image"]).convert("RGB")
        page = render_card(photo, card["name"], 2550, 3300, 150, 420, 150)  # US Letter portrait, 300 dpi
        page.save(args.out / "print" / f"{card['label']}.png", dpi=(300, 300))
        pages.append(page)
        screen = render_card(photo, card["name"], 1920, 1080, 40, 150, 90)
        screen.save(args.out / "screen" / f"{card['label']}.png")
        thumbs.append(page.resize((425, 550), Image.LANCZOS))
    pdf = args.out / "station_cards.pdf"
    pages[0].save(pdf, save_all=True, append_images=pages[1:], resolution=300)
    if args.pdf:
        pages[0].save(args.pdf.expanduser(), save_all=True, append_images=pages[1:], resolution=300)
    sheet = Image.new("RGB", (3 * 435 + 10, 2 * 560 + 10), (200, 200, 200))
    for k, t in enumerate(thumbs):
        sheet.paste(t, (10 + (k % 3) * 435, 10 + (k // 3) * 560))
    sheet.save(args.out / "contact_sheet.jpg", quality=90)
    print(pdf, args.pdf or "", args.out / "contact_sheet.jpg", sep="\n")


if __name__ == "__main__":
    main()
