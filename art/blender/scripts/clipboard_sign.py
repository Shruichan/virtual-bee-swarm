"""SIGN HERE clipboard: the START button (V14). Four bees hold it still ~45 cm in front of the player's head; poking
it starts the show. Same board and clip as clipboard.py, but the paper is a union form with a big SIGN HERE, a
signature line, a red X and an arrow. Pivot = bottom edge center, faces the player.
Objects: SignHereClipboard (empty) > Board (one mesh), PokePoint (empty on the signature line)."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "clipboard_sign"
BUDGET = 3000

W, H, T = 0.12, 0.16, 0.006
LINE_Y = -0.04  # signature line, relative to the board center


def build():
    root = C.empty("SignHereClipboard", size=0.05)
    mb = C.MB("Board", smooth_angle=40)
    m = C.M((0, 0, H / 2), (90, 0, 0))  # local XY = board face (y up), local +Z = toward the player
    mb.poly("wood", C.rounded_rect(W, H, 0.01, 2), T, m)
    pw, ph = W - 0.014, H - 0.03
    py = -0.008
    pz = T / 2 + 0.0004
    mb.box("white", m @ C.M((0, py, pz), scale=(pw, ph, 0.0008)))
    top = py + ph / 2
    lz = pz + 0.0007
    # header band: LOCAL 20,000
    hdr = 0.016
    mb.box("amber", m @ C.M((0, top - hdr / 2, pz + 0.0005), scale=(pw, hdr, 0.0006)))
    mb.text("charcoal", "LOCAL 20,000", 0.0085, 0.0, m @ C.M((0, top - hdr / 2 - 0.0004, lz + 0.0002)))
    # the words
    mb.text("charcoal", "SIGN", 0.03, 0.0, m @ C.M((0, 0.03, lz)), resolution=3)
    mb.text("charcoal", "HERE", 0.03, 0.0, m @ C.M((0, 0.002, lz)), resolution=3)
    # signature line with a red X at its start
    lw = pw * 0.78
    mb.box("charcoal", m @ C.M((0.004, LINE_Y, lz), scale=(lw, 0.0016, 0.0005)))
    for a in (40, -40):
        mb.box("red", m @ C.M((-lw / 2 + 0.002, LINE_Y + 0.008, lz + 0.0001), (0, 0, a), scale=(0.013, 0.0028, 0.0005)))
    # arrow pointing left at the X
    tip = -lw / 2 + 0.012
    mb.flat("red", [(0.0, 0.0), (0.009, 0.0065), (0.009, 0.0025), (0.022, 0.0025), (0.022, -0.0025),
                    (0.009, -0.0025), (0.009, -0.0065)], m @ C.M((tip, LINE_Y + 0.008, lz + 0.0002)))
    # small print
    for k in range(3):
        mb.box("stone", m @ C.M((0, LINE_Y - 0.012 - 0.0065 * k, lz), scale=(pw * (0.7 - 0.12 * k), 0.0018, 0.0004)))
    # clip
    cy = H / 2 - 0.004
    mb.poly("steel", C.rounded_rect(W * 0.4, 0.018, 0.004, 2), 0.004, m @ C.M((0, cy - 0.003, T / 2 + 0.002)),
            bevel=(0.0008, 1))
    mb.torus("steel_dark", W * 0.1, 0.0024, 12, 6, m @ C.M((0, cy + 0.005, T / 2 + 0.003), (90, 0, 0),
                                                            (1.0, 0.55, 1.0)))
    mb.to_object(origin=(0, 0, 0), parent=root)
    # the poke target: on the signature line, on the paper's surface
    C.empty("PokePoint", origin=(0.004, -(lz + 0.0005), H / 2 + LINE_Y), parent=root, size=0.01)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom edge center)", hero_az=-15)
