"""Crowbar: the WRONG choice at the wasps station (BIGGER door: pry the entrance wide and the wasps pour in).
Red-painted hex bar with a gooseneck hook and steel tips, standing upright, hook to the player's right.
Pivot = base (bottom tip). One mesh."""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "crowbar"
BUDGET = 3000

R = 0.0058     # bar radius (hexagonal section)
TOP = 0.215    # where the gooseneck starts
HOOK = 0.026   # gooseneck radius


def build():
    mb = C.MB("Crowbar", smooth_angle=35)
    # chisel end at the bottom, bent a little toward -X (the hook bends toward +X)
    mb.tube("steel_dark", (-0.011, 0, 0.004), (-0.003, 0, 0.026), R * 0.75, R, seg=6)
    mb.sphere("steel_dark", C.M((-0.003, 0, 0.026), scale=R), seg=6, rings=4)
    # straight shaft + the gooseneck (in the XZ plane so it reads from the front)
    pts = [(-0.003, 0, 0.026), (0, 0, 0.05), (0, 0, TOP)]
    n = 7
    for k in range(1, n + 1):
        t = math.pi * k / n
        pts.append((HOOK - HOOK * math.cos(t), 0, TOP + HOOK * 1.15 * math.sin(t)))
    mb.limb("red", pts, R, R, seg=6)
    # claw at the hook end: two short steel prongs with a slot between them
    end = pts[-1]
    for dy in (-0.0035, 0.0035):
        mb.tube("steel_dark", (end[0], dy, end[2]), (end[0] + 0.004, dy, end[2] - 0.026), R * 0.7, R * 0.35, seg=6)
    mb.sphere("steel_dark", C.M(end, scale=R * 1.02), seg=6, rings=4)
    mb.to_object(origin=(0, 0, 0))


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom tip)", hero_az=-20)
