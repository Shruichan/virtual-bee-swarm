"""Clipboard: Beeswax's spreadsheet. Standalone prop stands upright facing the player, pivot = bottom edge center.
add_clipboard() is reused by beeswax.py for the one he holds."""
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "clipboard"
BUDGET = 3000

W, H = 0.09, 0.12  # board size (meters)
COLS, ROWS = 4, 7  # spreadsheet grid


def add_clipboard(mb, m, w=W, h=H, detail=True, seed=7):
    """Clipboard in local XY (x right, y up), reading from local +Z, centered on the origin; placed by m."""
    t = 0.005
    mb.poly("wood", C.rounded_rect(w, h, min(w, h) * 0.08, 2), t, m)
    pw, ph = w - 0.012, h - 0.026
    py = -0.006
    pz = t / 2 + 0.0004
    mb.box("white", m @ C.M((0, py, pz), scale=(pw, ph, 0.0008)))
    top = py + ph / 2
    hdr = ph / (ROWS + 1.5)
    mb.box("amber", m @ C.M((0, top - hdr / 2, pz + 0.0005), scale=(pw, hdr, 0.0006)))
    lz = pz + 0.0006
    lw = max(0.0007, w * 0.009)
    for i in range(1, COLS):
        x = -pw / 2 + pw * i / COLS
        mb.box("charcoal", m @ C.M((x, py, lz), scale=(lw, ph, 0.0005)))
    row_h = (ph - hdr) / ROWS
    for j in range(1, ROWS + 1):
        y = top - hdr - (j - 1) * row_h
        if j > 1:
            mb.box("charcoal", m @ C.M((0, y, lz), scale=(pw, lw, 0.0005)))
    if detail:
        rnd = random.Random(seed)
        cw = pw / COLS
        for j in range(ROWS):
            yc = top - hdr - (j + 0.5) * row_h
            for i in range(COLS - 1):
                if rnd.random() < 0.7:
                    bw = cw * rnd.uniform(0.35, 0.7)
                    xc = -pw / 2 + cw * (i + 0.5)
                    mb.box("ink", m @ C.M((xc - (cw * 0.7 - bw) / 2 + 0.001, yc, lz), scale=(bw, row_h * 0.3, 0.0004)))
            # last column = grievances filed: a fire-coloured tick mark
            xc = -pw / 2 + cw * (COLS - 0.5)
            mb.box("fire", m @ C.M((xc, yc, lz), (0, 0, 35), scale=(cw * 0.35, row_h * 0.22, 0.0004)))
    # clip
    cy = h / 2 - 0.004
    mb.poly("steel", C.rounded_rect(w * 0.42, 0.016, 0.004, 2), 0.004, m @ C.M((0, cy - 0.003, t / 2 + 0.002)),
            bevel=(0.0008, 1))
    mb.torus("steel_dark", w * 0.11, 0.0022, 12, 6, m @ C.M((0, cy + 0.004, t / 2 + 0.003), (90, 0, 0),
                                                              (1.0, 0.55, 1.0)))


def build():
    mb = C.MB("Clipboard", mat="pal")
    add_clipboard(mb, C.M((0, 0, H / 2), (90, 0, 0)))
    mb.to_object(origin=(0, 0, 0))


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom edge center)", hero_az=-20)
