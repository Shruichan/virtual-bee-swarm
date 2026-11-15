"""Printed label for the bug-spray bottle, in the style of dartiros' water.png (512 px square, header band).

    uv run python -m detect.station_art.make_labels      # -> detect/station_art/bug_spray_label.png
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
BOLD = ["/System/Library/Fonts/Supplemental/Arial Rounded Bold.ttf", "/System/Library/Fonts/SFNSRounded.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"]


def font(size):
    for p in BOLD:
        if Path(p).exists():
            return ImageFont.truetype(p, size)
    return ImageFont.load_default(size)


def bug_spray_label(path: Path) -> None:
    S = 512
    im = Image.new("RGB", (S, S), (255, 248, 230))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((8, 8, S - 8, S - 8), radius=26, outline=(200, 40, 30), width=10)
    d.rounded_rectangle((8, 8, S - 8, 80), radius=26, fill=(200, 40, 30))
    d.rectangle((8, 50, S - 8, 80), fill=(200, 40, 30))
    d.text((S / 2, 46), "DANGER", font=font(38), fill=(255, 248, 230), anchor="mm")
    d.text((S / 2, 160), "BUG", font=font(104), fill=(43, 33, 24), anchor="mm")
    d.text((S / 2, 262), "SPRAY", font=font(84), fill=(43, 33, 24), anchor="mm")
    # a little bee with a red slash: "kills bees too"
    cx, cy = S / 2, 395
    d.ellipse((cx - 62, cy - 38, cx + 62, cy + 38), fill=(255, 200, 40), outline=(43, 33, 24), width=6)
    for x in (-22, 10):
        d.rectangle((cx + x, cy - 36, cx + x + 16, cy + 36), fill=(43, 33, 24))
    d.ellipse((cx - 40, cy - 86, cx + 4, cy - 34), fill=(225, 240, 255), outline=(43, 33, 24), width=5)
    d.ellipse((cx + 0, cy - 86, cx + 44, cy - 34), fill=(225, 240, 255), outline=(43, 33, 24), width=5)
    d.ellipse((cx - 98, cy - 92, cx + 98, cy + 92), outline=(220, 30, 30), width=16)
    d.line((cx - 68, cy + 68, cx + 68, cy - 68), fill=(220, 30, 30), width=16)
    im.save(path)


if __name__ == "__main__":
    bug_spray_label(HERE / "bug_spray_label.png")
    print(HERE / "bug_spray_label.png")
