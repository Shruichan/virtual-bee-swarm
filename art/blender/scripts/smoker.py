"""Smoker: beekeeper's smoker. Steel can with a heat-shield cage, a lid with a bent spout (pointing at the front),
bellows at the back. Pivot = base. Objects: Canister, Lid (pivot = back hinge), Bellows (fixed board + red leather),
BellowsLever (pivot = bottom hinge; rotate on X to squeeze), SmokePoint (empty at the spout tip, +Z out)."""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "smoker"
BUDGET = 3000

R, HC = 0.05, 0.17  # can radius, can height


def build():
    root = C.empty("Smoker", size=0.08)
    cn = C.MB("Canister", smooth_angle=50)
    cn.lathe(lambda c, n: "charcoal" if c.z < 0.012 else "steel",
             [(0, 0), (R - 0.004, 0), (R, 0.006), (R, HC - 0.004), (R - 0.002, HC)], seg=18)
    for z in (0.028, 0.118):
        cn.torus("charcoal", R + 0.008, 0.0032, 18, 6, C.M((0, 0, z)))
    for i in range(12):
        a = 2 * math.pi * i / 12
        if abs(math.degrees(a) - 90) < 35:  # leave the back free for the bellows
            continue
        cn.box("charcoal", C.M(((R + 0.008) * math.cos(a), (R + 0.008) * math.sin(a), 0.073),
                                (0, 0, math.degrees(a)), (0.004, 0.006, 0.092)))
    cn.to_object(origin=(0, 0, 0), parent=root)

    ld = C.MB("Lid", smooth_angle=50)
    ld.lathe("steel", [(R + 0.003, HC - 0.003), (R + 0.003, HC + 0.012), (0.034, HC + 0.04), (0.019, HC + 0.05),
                       (0, HC + 0.05)], seg=18)
    tip = (0, -0.064, HC + 0.088)
    ld.limb("steel", [(0, 0, HC + 0.042), (0, -0.010, HC + 0.068), (0, -0.034, HC + 0.084), tip], 0.017, 0.011,
            seg=12)
    ld.torus("charcoal", 0.0105, 0.0032, 12, 6, C.frame(tip, (0, -1, 0)))
    ld.cyl("steel_dark", 0.006, 0.03, 8, C.frame((-0.015, R + 0.004, HC + 0.008), (1, 0, 0)))  # hinge pin
    ld.to_object(origin=(0, R + 0.004, HC + 0.008), parent=root)
    C.empty("SmokePoint", (0, -0.070, HC + 0.088), root, size=0.02)

    # bellows: fixed board on the can, red leather wedge, hinged lever board
    y0 = R + 0.016
    bw = 0.066
    be = C.MB("Bellows")
    be.box("wood", C.M((0, R + 0.012, 0.092), scale=(0.072, 0.008, 0.13)), bevel=(0.002, 1))
    zig = [(y0, 0.034), (y0, 0.152)]
    for k in range(1, 6):
        zig.append((y0 + 0.008 * k, 0.148 if k % 2 else 0.155))
    zig.append((y0 + 0.046, 0.150))
    side = C.frame((0, 0, 0), (1, 0, 0), (0, 0, 1))  # local X -> world +Y, local Y -> world +Z, extrude along X
    be.poly("red", zig, bw, side)
    be.to_object(origin=(0, R + 0.012, 0.03), parent=root)

    lv = C.MB("BellowsLever")
    hinge = (0, y0 + 0.004, 0.032)
    ang = math.degrees(math.atan2(0.046, 0.125))
    lv.box("wood", C.M((0, hinge[1] + 0.023, 0.032 + 0.0625), (-ang, 0, 0), (0.074, 0.008, 0.135)), bevel=(0.002, 1))
    lv.cyl("steel_dark", 0.0045, 0.074, 8, C.frame((-0.037, hinge[1], hinge[2]), (1, 0, 0)))
    lv.to_object(origin=hinge, parent=root)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom center)", hero_az=-55)
