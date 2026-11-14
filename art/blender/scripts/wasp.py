"""Wasp: ~5.5 cm, lemon-yellow and black, narrow waist, stinger, angry brows. Flies level, front = Unity +Z,
pivot = body center. Objects: Body, WingL / WingR (pivot = wing root; flap on Unity Z)."""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "wasp"
BUDGET = 3000

STRIPES = [(-0.55, "charcoal"), (-0.2, "wasp"), (0.05, "charcoal"), (0.35, "wasp"), (0.58, "charcoal"),
           (1.0, "wasp")]


def taper_back(v):
    """Squeeze the back half of a unit sphere (local -Z = tail) into a point."""
    if v.z < 0:
        k = 1.0 + 0.5 * v.z
        return C.Vector((v.x * k, v.y * k, v.z))
    return v


def build():
    root = C.empty("Wasp", size=0.03)
    b = C.MB("Body", mat="bee", smooth_angle=60)
    head = (0, -0.0175, 0.0022)
    b.sphere("wasp", C.M(head, scale=(0.0072, 0.0062, 0.0068)), seg=12, rings=8)
    for s in (1, -1):
        b.sphere("white", C.M((0.0034 * s, -0.0228, 0.0036), (0, 0, 18 * s), (0.0027, 0.0016, 0.0029)), seg=10, rings=6)
        b.sphere("charcoal", C.M((0.0030 * s, -0.0243, 0.0031), scale=(0.0011, 0.0006, 0.0013)), seg=6, rings=4)
        b.box("charcoal", C.M((0.0034 * s, -0.0236, 0.0068), (0, -28 * s, 18 * s), (0.0056, 0.0012, 0.0015)))
        b.poly("charcoal", [((0.0018 + x) * s, y) for x, y in ((0, 0), (0.0026, -0.0006), (0.0012, -0.0040))], 0.0012,
               C.M((0, -0.0225, -0.0028)))  # mandible
        b.limb("charcoal", [(0.0018 * s, -0.0220, 0.0080), (0.0035 * s, -0.0265, 0.0120),
                            (0.0068 * s, -0.0300, 0.0128)], 0.00065, seg=5)
    b.sphere(lambda c, n: "wasp" if (c.z > 0.006 and abs(c.x) > 0.002) else "charcoal",
             C.M((0, -0.0075, 0.0025), scale=(0.0058, 0.0062, 0.0056)), seg=12, rings=8)
    b.tube("charcoal", (0, -0.0018, 0.0016), (0, 0.0028, 0.0006), 0.0016, seg=6)
    m = C.M((0, 0.0145, -0.0004), (90, 0, 0), (0.0068, 0.0068, 0.012))
    inv = m.inverted()
    b.sphere(lambda c, n: C.bands((inv @ c).z, STRIPES), m, seg=12, rings=10, lfn=taper_back)
    b.lathe("charcoal", [(0.0011, 0), (0, 0.0055)], seg=5, m=C.frame((0, 0.0262, -0.0006), (0, 1, -0.15)),
            cap_top=False, smooth=False)
    for i, y in enumerate((-0.0105, -0.0075, -0.0045)):
        for s in (1, -1):
            spread = (i - 1) * 0.003
            b.limb("charcoal", [(0.0028 * s, y, -0.0015), (0.0078 * s, y + spread, -0.0052),
                                (0.0088 * s, y + spread * 1.6, -0.0115)], 0.00085, 0.0007, seg=5,
                   end_ball=0.0011, end_color="wasp")
    b.to_object(origin=(0, 0, 0), parent=root)

    for s, name in ((1, "WingL"), (-1, "WingR")):
        w = C.MB(name, mat="bee", smooth_angle=30)
        rp = (0.0028 * s, -0.0085, 0.0070)
        fore = [(0.011 + 0.011 * math.cos(a), 0.0034 * math.sin(a)) for a in [2 * math.pi * k / 14 for k in range(14)]]
        hind = [(0.0075 + 0.0075 * math.cos(a), 0.0024 * math.sin(a)) for a in [2 * math.pi * k / 12 for k in range(12)]]
        w.poly("wing", [(x * s, y) for x, y in fore], 0.0003, C.M(rp, (0, -18 * s, 32 * s)))
        w.poly("wing", [(x * s, y) for x, y in hind], 0.0003, C.M((rp[0], rp[1] + 0.003, rp[2] - 0.0006),
                                                                  (0, -14 * s, 48 * s)))
        w.to_object(origin=rp, parent=root)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="body center", hero_az=-40)
