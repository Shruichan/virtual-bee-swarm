"""Draw the two card pictures that have no good free photo: a plain aerosol can with a red X, and a blue bowl
of pebbles seen from above. Original drawings (no license strings attached).

    uv run python -m detect.card_art        # -> detect/card_images/bug_spray.png, pebble_dish.png
"""

from __future__ import annotations

import math
import random
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

OUT = Path(__file__).with_name("card_images")
S = 1600  # square canvas; make_cards cover-crops


def aerosol_can() -> Image.Image:
    im = Image.new("RGB", (S, S), (236, 242, 247))
    d = ImageDraw.Draw(im)
    # soft floor shadow
    sh = Image.new("L", (S, S), 0)
    ImageDraw.Draw(sh).ellipse((480, 1380, 1120, 1480), fill=110)
    im.paste((180, 186, 192), (0, 0), sh.filter(ImageFilter.GaussianBlur(30)))
    x0, x1, top, bot = 560, 1040, 420, 1430
    # body: horizontal shading for a metal cylinder
    for x in range(x0, x1):
        t = (x - x0) / (x1 - x0)
        v = 0.55 + 0.45 * math.cos((t - 0.32) * math.pi * 1.1)
        c = tuple(int(c * v) for c in (214, 222, 230))
        d.line((x, top + 40, x, bot - 30), fill=c)
    d.ellipse((x0, bot - 60, x1, bot), fill=(150, 156, 162))
    d.ellipse((x0, top + 10, x1, top + 70), fill=(190, 198, 206))
    # green label band (no brand)
    for x in range(x0, x1):
        t = (x - x0) / (x1 - x0)
        v = 0.6 + 0.4 * math.cos((t - 0.32) * math.pi * 1.1)
        d.line((x, 760, x, 1150), fill=(int(40 * v), int(150 * v), int(80 * v)))
    # shoulder, cap, nozzle
    d.polygon([(x0, top + 40), (x1, top + 40), (920, top - 60), (680, top - 60)], fill=(176, 184, 192))
    d.rounded_rectangle((690, top - 220, 910, top - 50), radius=30, fill=(245, 245, 245), outline=(160, 160, 160), width=6)
    d.rectangle((775, top - 270, 830, top - 215), fill=(60, 60, 60))
    d.rectangle((830, top - 258, 880, top - 236), fill=(60, 60, 60))
    # spray mist
    rng = random.Random(4)
    for _ in range(260):
        a = rng.uniform(-0.45, 0.45)
        r = rng.uniform(40, 420)
        cx, cy = 890 + r * math.cos(a), top - 247 + r * math.sin(a) * 0.9
        rr = rng.uniform(4, 14)
        d.ellipse((cx - rr, cy - rr, cx + rr, cy + rr), fill=(200, 210, 220))
    # big red X
    w = 110
    for (a, b) in [((300, 300), (1300, 1300)), ((1300, 300), (300, 1300))]:
        d.line((a, b), fill=(226, 30, 30), width=w)
    for (cx, cy) in [(300, 300), (1300, 1300), (1300, 300), (300, 1300)]:
        d.ellipse((cx - w / 2, cy - w / 2, cx + w / 2, cy + w / 2), fill=(226, 30, 30))
    return im


def pebble_bowl() -> Image.Image:
    im = Image.new("RGB", (S, S), (222, 205, 176))  # wooden table tone
    d = ImageDraw.Draw(im)
    rng = random.Random(11)
    for y in range(0, S, 6):  # faint wood grain
        v = int(8 * math.sin(y / 37.0) + rng.uniform(-4, 4))
        d.line((0, y, S, y), fill=(222 + v, 205 + v, 176 + v), width=6)
    cx = cy = S // 2
    sh = Image.new("L", (S, S), 0)
    ImageDraw.Draw(sh).ellipse((cx - 700, cy - 680, cx + 740, cy + 760), fill=140)
    im.paste((120, 100, 80), (0, 0), sh.filter(ImageFilter.GaussianBlur(40)))
    d.ellipse((cx - 700, cy - 700, cx + 700, cy + 700), fill=(28, 96, 196))  # rim
    d.ellipse((cx - 610, cy - 610, cx + 610, cy + 610), fill=(52, 128, 220))  # inner wall
    d.ellipse((cx - 540, cy - 540, cx + 540, cy + 540), fill=(110, 170, 210))  # water
    pebbles = []
    for _ in range(4000):
        r = rng.uniform(34, 70)
        a, rad = rng.uniform(0, 2 * math.pi), math.sqrt(rng.random()) * (520 - r)
        x, y = cx + rad * math.cos(a), cy + rad * math.sin(a)
        if all((x - px) ** 2 + (y - py) ** 2 > (r + pr - 6) ** 2 for px, py, pr in pebbles):
            pebbles.append((x, y, r))
        if len(pebbles) > 150:
            break
    for x, y, r in pebbles:
        g = rng.randint(105, 190)
        tint = rng.choice([(0, 0, 0), (12, 6, 0), (-6, 0, 8)])
        c = tuple(max(0, min(255, g + t)) for t in tint)
        e = rng.uniform(0.75, 1.0)
        d.ellipse((x - r, y - r * e, x + r, y + r * e), fill=c, outline=(70, 70, 70), width=3)
        d.ellipse((x - r * 0.45, y - r * e * 0.6, x - r * 0.05, y - r * e * 0.25), fill=tuple(min(255, v + 45) for v in c))
    for _ in range(40):  # water glints between pebbles
        x, y = cx + rng.uniform(-480, 480), cy + rng.uniform(-480, 480)
        if (x - cx) ** 2 + (y - cy) ** 2 < 500 ** 2:
            d.ellipse((x - 10, y - 4, x + 10, y + 4), fill=(235, 248, 255))
    return im


def main() -> None:
    OUT.mkdir(exist_ok=True)
    aerosol_can().save(OUT / "bug_spray.png", optimize=True)
    pebble_bowl().save(OUT / "pebble_dish.png", optimize=True)
    print(OUT / "bug_spray.png", OUT / "pebble_dish.png")


if __name__ == "__main__":
    main()
