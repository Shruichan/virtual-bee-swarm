"""Giant hornet: ~0.25 m, menacing but cartoony. Big orange head, heavy V brows, tiny pupils, hooked mandibles,
dark thorax, banded abdomen, big stinger. Faceted (flat shaded) so it reads harder than the round, friendly bees.
Front = Unity +Z, pivot = body center. Objects: Body, Mandibles (pivot = jaw hinge), WingL / WingR."""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "giant_hornet"
BUDGET = 3000

STRIPES = [(-0.62, "hornet_dark"), (-0.36, "hornet"), (-0.06, "hornet_dark"), (0.22, "hornet"), (0.5, "hornet_dark"),
           (1.0, "hornet_dark")]


def taper_back(v):
    if v.z < 0:
        k = 1.0 + 0.42 * v.z
        return C.Vector((v.x * k, v.y * k, v.z))
    return v


def build():
    root = C.empty("GiantHornet", size=0.1)
    b = C.MB("Body", mat="bee", smooth_angle=20)
    head = (0, -0.085, 0.012)
    b.sphere("hornet", C.M(head, scale=(0.048, 0.040, 0.044)), seg=14, rings=9, smooth=False)
    for s in (1, -1):
        b.sphere("white", C.M((0.020 * s, -0.117, 0.024), (0, 0, 22 * s), (0.016, 0.009, 0.012)), seg=10, rings=6,
                 smooth=False)
        b.sphere("charcoal", C.M((0.0175 * s, -0.1255, 0.0215), scale=(0.0045, 0.002, 0.005)), seg=8, rings=5)
        b.box("charcoal", C.M((0.020 * s, -0.1235, 0.0385), (0, -26 * s, 22 * s), (0.032, 0.009, 0.0085)),
              bevel=(0.002, 1))
        b.limb("hornet_dark", [(0.012 * s, -0.110, 0.050), (0.020 * s, -0.126, 0.074), (0.040 * s, -0.150, 0.080)],
               0.0032, 0.0026, seg=6)
    b.sphere(lambda c, n: "hornet" if c.y < -0.058 else "hornet_dark",
             C.M((0, -0.030, 0.016), scale=(0.038, 0.042, 0.035)), seg=12, rings=8, smooth=False)
    b.tube("hornet_dark", (0, 0.004, 0.009), (0, 0.026, 0.004), 0.010, seg=8)
    m = C.M((0, 0.078, -0.004), (90, 0, 0), (0.048, 0.047, 0.072))
    inv = m.inverted()
    b.sphere(lambda c, n: C.bands((inv @ c).z, STRIPES), m, seg=14, rings=10, smooth=False, lfn=taper_back)
    b.lathe("charcoal", [(0.0065, 0), (0, 0.034)], seg=6, m=C.frame((0, 0.145, -0.006), (0, 1, -0.22)),
            cap_top=False, smooth=False)
    for i, y in enumerate((-0.052, -0.034, -0.016)):
        for s in (1, -1):
            spread = (i - 1) * 0.016
            pts = [(0.016 * s, y, -0.006), (0.040 * s, y + spread, -0.020), (0.050 * s, y + spread * 1.5, -0.050),
                   (0.054 * s, y + spread * 1.7, -0.066)]
            b.limb("hornet_dark", pts[:3], 0.0045, 0.0038, seg=6)
            b.limb("hornet", pts[2:], 0.0036, 0.0028, seg=6, end_ball=0.0045, end_color="hornet_dark")
    b.to_object(origin=(0, 0, 0), parent=root)

    md = C.MB("Mandibles", mat="bee", smooth_angle=20)
    hook = [(0.0, 0.0), (0.012, 0.004), (0.016, -0.010), (0.012, -0.024), (0.004, -0.032), (-0.004, -0.030),
            (0.002, -0.024), (0.004, -0.018), (0.0, -0.016), (0.004, -0.010), (0.0, -0.006)]
    for s in (1, -1):
        pts = [(x * s + 0.006 * s, y) for x, y in hook]
        md.poly("charcoal", pts, 0.008, C.M((0, -0.112, -0.010), (-12, 0, 0)), bevel=(0.0015, 1))
    md.to_object(origin=(0, -0.112, -0.010), parent=root)

    for s, name in ((1, "WingL"), (-1, "WingR")):
        w = C.MB(name, mat="bee", smooth_angle=30)
        rp = (0.022 * s, -0.030, 0.045)
        fore = [(0.065 + 0.065 * math.cos(a), 0.021 * math.sin(a)) for a in [2 * math.pi * k / 14 for k in range(14)]]
        hind = [(0.045 + 0.045 * math.cos(a), 0.015 * math.sin(a)) for a in [2 * math.pi * k / 12 for k in range(12)]]
        w.poly("smoke_wing", [(x * s, y) for x, y in fore], 0.002, C.M(rp, (0, -22 * s, 30 * s)))
        w.poly("smoke_wing", [(x * s, y) for x, y in hind], 0.002, C.M((rp[0], rp[1] + 0.012, rp[2] - 0.004),
                                                                        (0, -16 * s, 46 * s)))
        w.to_object(origin=rp, parent=root)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="body center", hero_az=-38)
