"""Fly swatter: the WRONG choice at the smoker station (swatting at bees only makes them angrier). Red plastic head
with a grid of holes on a long handle, standing upright facing the player. Pivot = base (bottom of the grip).
One mesh."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "swatter"
BUDGET = 3000

HW, HH, HT = 0.085, 0.078, 0.004  # head width, height, thickness
L = 0.165                         # handle length (base of the grip to the bottom of the head)
GRIP = 0.05


def build():
    mb = C.MB("Swatter", smooth_angle=40)
    # grip + handle
    mb.cyl("charcoal", 0.0068, GRIP, seg=10, m=C.M((0, 0, 0)))
    mb.torus("charcoal", 0.0068, 0.0016, 10, 5, m=C.M((0, 0, GRIP)))
    mb.cyl("red", 0.0042, L - GRIP + 0.006, seg=8, m=C.M((0, 0, GRIP)))
    # neck where the handle meets the head
    front = C.M((0, 0, L + HH / 2), (90, 0, 0))  # local XY = head face (y up), local +Z = toward the player
    mb.poly("red", [(-0.013, -HH / 2 + 0.002), (0.013, -HH / 2 + 0.002), (0.0045, -HH / 2 - 0.016),
                    (-0.0045, -HH / 2 - 0.016)], HT * 1.3, front, bevel=(0.0008, 1))
    # head plate with a raised rim
    mb.poly("red", C.rounded_rect(HW, HH, 0.016, 3), HT, front, bevel=(0.0009, 1))
    mb.ring("fire", C.rounded_rect(HW, HH, 0.016, 3), C.rounded_rect(HW - 0.008, HH - 0.008, 0.012, 3), HT + 0.0016,
            front)
    # the holes: dark squares on both faces
    cols, rows, cell = 6, 5, 0.0118
    s = 0.0068
    for j in range(rows):
        for i in range(cols):
            x = (i - (cols - 1) / 2) * cell
            y = (j - (rows - 1) / 2) * cell + 0.001
            sq = [(-s / 2, -s / 2), (s / 2, -s / 2), (s / 2, s / 2), (-s / 2, s / 2)]
            mb.flat("charcoal", sq, front @ C.M((x, y, HT / 2 + 0.0004)))
            mb.flat("charcoal", sq, front @ C.M((x, y, -HT / 2 - 0.0004), (0, 180, 0)))
    mb.to_object(origin=(0, 0, 0))


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom of the grip)", hero_az=-25)
