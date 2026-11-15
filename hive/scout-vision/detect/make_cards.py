"""Bee Scout station cards: the station's game-art picture big and centred, its name below, the logo in a corner.

    uv run python -m detect.make_cards        # -> out/cards/screen/<label>.png (1920 x 1080, full-screen viewing)
                                              #    out/cards/print/<label>.png (US Letter landscape, 300 dpi)
                                              #    out/cards/station_cards.pdf (US Letter, one card per page)
                                              #    out/cards/contact_sheet.jpg

Pictures come from detect/card_images (station_art/render_dart.py + compose.py: dartiros' models rendered in one
style). Names, order and pictures are in detect/cards.json. No markers on the cards.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
CREAM = (255, 243, 214)
COMB = (247, 228, 186)
INK = (74, 42, 20)
HONEY = (232, 150, 20)
LOGO = Path("/Users/aditya/Downloads/logo_trans_-_background.png")
FONT_DIRS = ["/System/Library/Fonts/Supplemental", "/System/Library/Fonts", "/Library/Fonts", "/usr/share/fonts/truetype/dejavu"]
FONT_BOLD = ["Arial Rounded Bold.ttf", "SFNSRounded.ttf", "DejaVuSans-Bold.ttf"]


def font(size: int) -> ImageFont.FreeTypeFont:
    for d in FONT_DIRS:
        for n in FONT_BOLD:
            p = Path(d) / n
            if p.exists():
                return ImageFont.truetype(str(p), size)
    return ImageFont.load_default(size)


def hexagon(cx: float, cy: float, r: float) -> list[tuple[float, float]]:
    return [(cx + r * math.cos(math.radians(30 + 60 * k)), cy + r * math.sin(math.radians(30 + 60 * k))) for k in range(6)]


def honeycomb(d: ImageDraw.ImageDraw, x0: float, y0: float, cols: int, rows: int, r: float, width: int) -> None:
    w = math.sqrt(3) * r
    for j in range(rows):
        for i in range(cols):
            d.polygon(hexagon(x0 + i * w + (w / 2 if j % 2 else 0), y0 + j * 1.5 * r, r * 0.92), outline=COMB, width=width)


def logo_badge(size: int, logo: Path) -> Image.Image | None:
    """The logo cropped to a circle with a honey ring (the source PNG has a patterned, not transparent, background)."""
    if not logo.exists():
        return None
    im = Image.open(logo).convert("RGB")
    s = min(im.size)
    im = im.crop(((im.width - s) // 2, (im.height - s) // 2, (im.width + s) // 2, (im.height + s) // 2)).resize((size, size), Image.LANCZOS)
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).ellipse((0, 0, size - 1, size - 1), fill=255)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(im, (0, 0), mask)
    ImageDraw.Draw(out).ellipse((0, 0, size - 1, size - 1), outline=HONEY, width=max(3, size // 28))
    return out


def render_card(card: dict, number: int, W: int, H: int, root: Path, logo: Path) -> Image.Image:
    u = H / 1080  # layout unit: 1 = one pixel on a 1080-tall screen card
    page = Image.new("RGB", (W, H), CREAM)
    d = ImageDraw.Draw(page)
    honeycomb(d, W - 330 * u, -40 * u, 5, 3, 70 * u, max(2, int(5 * u)))
    honeycomb(d, -60 * u, H - 170 * u, 5, 3, 70 * u, max(2, int(5 * u)))
    b = logo_badge(int(130 * u), logo)
    if b is not None:
        page.paste(b, (int(40 * u), int(36 * u)), b)
    d.text((int(190 * u), int(101 * u)), "BEE SCOUT", font=font(int(54 * u)), fill=INK, anchor="lm")
    d.text((W - int(48 * u), int(101 * u)), f"STATION {number}", font=font(int(40 * u)), fill=HONEY, anchor="rm")
    src = Path(card["image"])
    src = src if src.is_absolute() else root / src
    side = int(min(W * 0.9, H - 330 * u))
    art = Image.open(src).convert("RGB").resize((side, side), Image.LANCZOS)  # same cream background as the page
    page.paste(art, ((W - side) // 2, int(140 * u)))
    name = card["name"]
    size = int(120 * u)
    fn = font(size)
    while d.textlength(name, font=fn) > W * 0.9 and size > 40:
        size -= 6
        fn = font(size)
    d.text((W / 2, H - int(100 * u)), name, font=fn, fill=INK, anchor="mm")
    return page


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", type=Path, default=Path(__file__).with_name("cards.json"))
    ap.add_argument("--out", type=Path, default=ROOT / "out" / "cards")
    ap.add_argument("--logo", type=Path, default=LOGO)
    args = ap.parse_args()
    cfg = json.loads(args.config.read_text())
    root = ROOT / cfg.get("image_root", "detect/card_images")
    (args.out / "screen").mkdir(parents=True, exist_ok=True)
    (args.out / "print").mkdir(parents=True, exist_ok=True)
    pages, thumbs = [], []
    for i, card in enumerate(cfg["cards"], 1):
        screen = render_card(card, i, 1920, 1080, root, args.logo)
        screen.save(args.out / "screen" / f"{card['label']}.png")
        page = render_card(card, i, 3300, 2550, root, args.logo)  # US Letter landscape at 300 dpi
        page.save(args.out / "print" / f"{card['label']}.png", dpi=(300, 300))
        pages.append(page)
        thumbs.append(screen.resize((640, 360), Image.LANCZOS))
        print(args.out / "screen" / f"{card['label']}.png")
    pdf = args.out / "station_cards.pdf"
    pages[0].save(pdf, save_all=True, append_images=pages[1:], resolution=300)
    sheet = Image.new("RGB", (1310, 360 * 3 + 40), (255, 255, 255))
    for k, t in enumerate(thumbs):
        sheet.paste(t, (10 + (k % 2) * 650, 10 + (k // 2) * 370))
    sheet.save(args.out / "contact_sheet.jpg", quality=90)
    print(pdf)
    print(args.out / "contact_sheet.jpg")


if __name__ == "__main__":
    main()
