"""Spray bottle of plain WATER: the WRONG choice at the smoker station (misting the hive makes the bees angrier).
Blue plastic bottle with a white trigger sprayer and a cream WATER label. Pivot = base.
Objects: SprayBottle (empty) > Bottle, Sprayer (pivot = its base, on the neck), Label, SprayPoint (nozzle tip,
+Z = spray direction)."""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "spray_bottle"
BUDGET = 3000

RX, FLAT = 0.032, 0.72
NECK = 0.142
LABEL_Z = (0.03, 0.098)


def build():
    root = C.empty("SprayBottle", size=0.06)
    oval = C.M(scale=(1, FLAT, 1))
    bo = C.MB("Bottle", smooth_angle=50)
    prof = [(0, 0), (0.028, 0), (RX, 0.005), (RX, 0.108), (0.026, 0.124), (0.0145, 0.134), (0.0145, NECK),
            (0, NECK)]
    bo.lathe("sky", prof, seg=24, m=oval)
    bo.to_object(origin=(0, 0, 0), parent=root)

    sp = C.MB("Sprayer", smooth_angle=35)
    z0 = NECK
    sp.cyl("white", 0.0165, 0.012, seg=16, m=C.M((0, 0, z0 - 0.002)))           # screw collar
    sp.cyl("plastic", 0.009, 0.01, seg=10, m=C.M((0, 0, z0 + 0.01)))             # stem
    sp.box("white", C.M((0, -0.012, z0 + 0.03), scale=(0.024, 0.052, 0.022)), bevel=(0.004, 2))  # head, nose forward
    sp.box("white", C.M((0, 0.008, z0 + 0.024), scale=(0.022, 0.02, 0.026)), bevel=(0.004, 2))   # back hump
    sp.cyl("charcoal", 0.0052, 0.008, seg=10, m=C.frame((0, -0.037, z0 + 0.031), (0, -1, 0)))     # nozzle
    sp.cyl("fire", 0.0026, 0.0012, seg=8, m=C.frame((0, -0.0447, z0 + 0.031), (0, -1, 0)))       # nozzle hole ring
    # trigger: a curved paddle hanging under the nose
    pts = [(0, -0.026, z0 + 0.02), (0, -0.031, z0 + 0.006), (0, -0.029, z0 - 0.012)]
    sp.limb("blue", pts, 0.0045, 0.004, seg=8, end_ball=0.0045)
    sp.to_object(origin=(0, 0, z0 - 0.002), parent=root)

    lb = C.MB("Label", smooth_angle=50)
    rx, ry = RX + 0.0006, (RX + 0.0006) * FLAT
    lb.lathe("cream", [(rx, LABEL_Z[0]), (rx, LABEL_Z[1])], seg=24, cap_bottom=False, cap_top=False,
             m=C.M(scale=(1, FLAT, 1)))
    wrap = C.ellipse_wrap(rx, ry, 0.0)
    lb.text("charcoal", "WATER", 0.0145, 0.0, lfn=lambda v: wrap(C.Vector((v.x, v.y + 0.074, 0.0007))), resolution=2)
    # a water drop icon under the word
    drop = [(0.0, 0.009)] + [(0.0062 * math.cos(math.radians(a)), 0.0062 * math.sin(math.radians(a)))
                             for a in range(-20, -170, -15)] + [(-0.0062 * math.cos(math.radians(20)),
                                                                  0.0062 * math.sin(math.radians(20)))]
    lb.flat("ocean", drop, lfn=lambda v: wrap(C.Vector((v.x, v.y + 0.049, 0.0007))))
    lb.flat("white", C.circle_pts(0.0016, 8), lfn=lambda v: wrap(C.Vector((v.x - 0.002, v.y + 0.047, 0.0009))))
    lb.to_object(origin=(0, 0, LABEL_Z[0]), parent=root)

    C.empty("SprayPoint", origin=(0, -0.045, z0 + 0.031), parent=root, size=0.01)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom center)", hero_az=-35)
