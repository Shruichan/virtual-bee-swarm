"""Flowers: three low-poly meadow flowers, side by side, each with its pivot at the base of its stem.
  Flower_Purple   (aster/daisy: 8 purple petals around a honey center, head tipped toward the viewer)
  Flower_Blue     (tulip-ish bell with 5 pointed petals)
  Flower_Lavender (lavender spike)"""
import math
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "flowers"
BUDGET = 3000

SPACING = 0.11


def stem(mb, base, h, bend, r=0.0026):
    bx, by, bz = base
    pts = [(bx, by, bz), (bx, by - bend * 0.15, bz + h * 0.4), (bx, by - bend * 0.55, bz + h * 0.75),
           (bx, by - bend, bz + h)]
    mb.limb("leaf", pts, r, r * 0.85, seg=6)
    return C.Vector(pts[-1])


def leaf(mb, at, yaw, size=0.024, up=35):
    m = C.M(at, (0, -up, yaw), (1, 1, 1))
    mb.sphere("leaf_dark", m @ C.M((size * 0.5, 0, 0), scale=(size * 0.5, size * 0.17, size * 0.07)), seg=8, rings=5)


def daisy(x):
    mb = C.MB("Flower_Purple", smooth_angle=50)
    top = stem(mb, (x, 0, 0), 0.13, 0.02)
    leaf(mb, (x, 0, 0.035), 20)
    leaf(mb, (x, -0.004, 0.06), 200, 0.02)
    head = C.M(top, (55, 0, 0))  # tip the face toward the viewer (-Y) and up
    mb.sphere("honey", head @ C.M((0, 0, 0.004), scale=(0.011, 0.011, 0.007)), seg=10, rings=6)
    for i in range(8):
        a = 360.0 * i / 8
        mb.sphere("purple" if i % 2 else "lavender",
                  head @ C.M(rot=(0, 0, a)) @ C.M((0.019, 0, 0.001), (0, -12, 0), (0.015, 0.0065, 0.0028)), seg=8,
                  rings=4)
    return mb.to_object(origin=(x, 0, 0))


def bell(x):
    mb = C.MB("Flower_Blue", smooth_angle=50)
    top = stem(mb, (x, 0, 0), 0.115, 0.012)
    leaf(mb, (x, 0, 0.03), 140, 0.026, 45)
    leaf(mb, (x, 0, 0.05), -20, 0.022, 40)

    def petals(v):
        a = math.atan2(v.y, v.x)
        k = max(0.0, math.cos(5 * a)) ** 2
        return C.Vector((v.x, v.y, v.z * (1 + 0.45 * k * max(0.0, v.z / 0.03))))
    prof = [(0, 0), (0.008, 0.002), (0.016, 0.011), (0.020, 0.024), (0.022, 0.032), (0.0205, 0.032), (0.0185, 0.024),
            (0.0145, 0.012), (0.007, 0.0045), (0, 0.0035)]
    mb.lathe(lambda c, n: "blue" if n.z < 0.6 or c.z < top.z + 0.006 else "sky", prof, seg=15,
             m=C.M(top + C.Vector((0, 0, -0.002)), (15, 0, 0)), lfn=petals, cap_bottom=False, cap_top=False)
    mb.sphere("honey", C.M(top + C.Vector((0, -0.002, 0.012)), scale=0.0045), seg=8, rings=5)
    return mb.to_object(origin=(x, 0, 0))


def lavender(x):
    mb = C.MB("Flower_Lavender", smooth_angle=50)
    top = stem(mb, (x, 0, 0), 0.16, 0.008, 0.0022)
    leaf(mb, (x, 0, 0.02), 70, 0.03, 60)
    leaf(mb, (x, 0, 0.026), 250, 0.028, 60)
    rnd = random.Random(3)
    for j in range(6):
        z = top.z - 0.055 + j * 0.0105
        rr = 0.0058 - j * 0.0006
        for k in range(3):
            a = 2 * math.pi * k / 3 + j * 0.9
            p = (x + rr * math.cos(a), top.y + rr * math.sin(a), z)
            mb.sphere("purple" if (j + k) % 2 else "lavender", C.M(p, scale=(0.0048, 0.0048, 0.0062)), seg=6, rings=4)
    mb.sphere("lavender", C.M(top + C.Vector((0, 0, 0.008)), scale=(0.004, 0.004, 0.006)), seg=6, rings=4)
    _ = rnd
    return mb.to_object(origin=(x, 0, 0))


def build():
    daisy(-SPACING)
    bell(0.0)
    lavender(SPACING)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base of each stem", hero_az=-15)
