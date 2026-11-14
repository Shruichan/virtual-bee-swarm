"""Earth: a small stylized globe (0.2 m across): blue ocean, raised green blobby continents, white ice caps, and a
separate Clouds object you can spin slowly. Pivot = center. Objects: Earth, Clouds (child)."""
import math
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402
from mathutils import noise  # noqa: E402

ITEM = "earth"
BUDGET = 3000

RADIUS = 0.10
LAND_LEVEL = 0.12  # raise to shrink continents
OFFSET = C.Vector((3.1, 7.7, 1.3))  # noise seed position


def land(d):
    return noise.noise(d * 1.7 + OFFSET) + 0.45 * noise.noise(d * 3.6 + OFFSET * 2) > LAND_LEVEL


def ice(d):
    return abs(d.z) > 0.86


def build():
    e = C.MB("Earth", smooth_angle=25)

    def lfn(v):
        d = v.normalized()
        k = 1.0
        if ice(d):
            k = 1.025
        elif land(d):
            k = 1.04
        return d * k

    def col(c, n):
        d = c.normalized()
        if ice(d):
            return "white"
        if land(d):
            return "leaf" if noise.noise(d * 6) > -0.25 else "leaf_dark"
        return "ocean"
    e.ico(col, C.M(scale=RADIUS), subdiv=4, smooth=False, lfn=lfn)
    earth = e.to_object(origin=(0, 0, 0))

    cl = C.MB("Clouds", smooth_angle=40)
    rnd = random.Random(8)
    for i in range(5):
        z = rnd.uniform(-0.6, 0.6)
        a = rnd.uniform(0, 2 * math.pi) if i else -math.pi / 2 + 0.4  # one in front
        d = C.Vector((math.sqrt(1 - z * z) * math.cos(a), math.sqrt(1 - z * z) * math.sin(a), z))
        side = d.cross(C.Vector((0, 0, 1))).normalized()
        for j in range(2):
            p = d * RADIUS * 1.09 + side * (j - 0.5) * 0.022
            cl.ico("white", C.frame(p, d, (0, 0, 1), (0.020 - 0.004 * j, 0.014, 0.008)), subdiv=2, smooth=True)
    cl.to_object(origin=(0, 0, 0), parent=earth)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="center", hero_az=-20)
