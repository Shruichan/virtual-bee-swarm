"""Bee Scout station cards: the real photo (unmodified) on top, a big trail-sign label below it for OCR.

    uv run python -m detect.make_cards        # -> out/cards/station_cards.pdf (US Letter landscape, 300 dpi, one per page)
                                              #    out/cards/print/<label>.png, out/cards/screen/<label>.png (1920x1080)
                                              #    out/cards/contact_sheet.jpg
    uv run python -m detect.make_cards --pdf ~/Downloads/bee-scout-print-cards.pdf   # also write the PDF there

Names, signs, order, photos and credits are in detect/cards.json. Print at 100% / actual size: the sign's capital
letters are then 4.2 cm tall (DIN Condensed Bold, near-black on a plain light panel), readable from ~1 m.
Landscape, because a one-line HONEYCOMB at 4 cm cap height does not fit the width of a portrait Letter page.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
INK = (59, 38, 22)
FONT_DIRS = ["/System/Library/Fonts/Supplemental", "/System/Library/Fonts", "/Library/Fonts", "/usr/share/fonts/truetype/dejavu"]
FONT_BOLD = ["DIN Condensed Bold.ttf", "Arial Narrow Bold.ttf", "DejaVuSansCondensed-Bold.ttf", "DejaVuSans-Bold.ttf"]
SIGN_INK = (12, 12, 12)
PANEL = (250, 248, 242)
DPI = 300


def font(size: int) -> ImageFont.FreeTypeFont:
    for d in FONT_DIRS:
        for n in FONT_BOLD:
            p = Path(d) / n
            if p.exists():
                return ImageFont.truetype(str(p), size)
    return ImageFont.load_default(size)


def sign_font(cap_px: int) -> ImageFont.FreeTypeFont:
    """The font size whose capital letters are cap_px tall."""
    probe = font(200)
    b = probe.getbbox("H")
    return font(round(200 * cap_px / (b[3] - b[1])))


def render_card(photo: Image.Image, sign: str, W: int, H: int, margin: int, photo_frac: float, cap_px: int) -> Image.Image:
    """White page: the photo scaled (aspect kept, nothing cropped) into the top photo_frac, then a plain light panel
    with the sign in capitals cap_px tall (shrunk only if it would not fit the width)."""
    page = Image.new("RGB", (W, H), (255, 255, 255))
    box_w, box_h = W - 2 * margin, int(H * photo_frac) - margin
    s = min(box_w / photo.width, box_h / photo.height)
    im = photo.resize((round(photo.width * s), round(photo.height * s)), Image.LANCZOS)
    page.paste(im, ((W - im.width) // 2, margin + (box_h - im.height) // 2))
    d = ImageDraw.Draw(page)
    py0, py1 = int(H * photo_frac) + margin // 2, H - margin // 2
    d.rectangle((margin // 2, py0, W - margin // 2, py1), fill=PANEL)
    fn = sign_font(cap_px)
    while d.textlength(sign, font=fn) > W - 2 * margin and cap_px > 20:
        cap_px -= 4
        fn = sign_font(cap_px)
    b = fn.getbbox("H")
    cy = (py0 + py1) / 2
    d.text((W / 2, cy - (b[1] + b[3]) / 2), sign, font=fn, fill=SIGN_INK, anchor="mt")
    return page, cap_px


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
        sign = card.get("sign", card["name"].upper())
        page, cap = render_card(photo, sign, 3300, 2550, 100, 0.70, round(4.2 / 2.54 * DPI))  # Letter landscape
        page.save(args.out / "print" / f"{card['label']}.png", dpi=(DPI, DPI))
        pages.append(page)
        screen, scap = render_card(photo, sign, 1920, 1080, 30, 0.72, 170)
        screen.save(args.out / "screen" / f"{card['label']}.png")
        thumbs.append(page.resize((550, 425), Image.LANCZOS))
        print(f"{sign:11s} print cap {cap / DPI * 2.54:.1f} cm, screen cap {scap} px")
    pdf = args.out / "station_cards.pdf"
    pages[0].save(pdf, save_all=True, append_images=pages[1:], resolution=300)
    if args.pdf:
        pages[0].save(args.pdf.expanduser(), save_all=True, append_images=pages[1:], resolution=300)
    sheet = Image.new("RGB", (2 * 560 + 10, 3 * 435 + 10), (200, 200, 200))
    for k, t in enumerate(thumbs):
        sheet.paste(t, (10 + (k % 2) * 560, 10 + (k // 2) * 435))
    sheet.save(args.out / "contact_sheet.jpg", quality=90)
    print(pdf, args.pdf or "", args.out / "contact_sheet.jpg", sep="\n")


if __name__ == "__main__":
    main()
