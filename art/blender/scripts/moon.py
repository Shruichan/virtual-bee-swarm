"""Moon: a faceted grey sphere (0.5 m across) with chunky bowl craters. Pivot = center (it floats). One mesh."""
import math
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402
from mathutils import noise  # noqa: E402

ITEM = "moon"
BUDGET = 3000

RADIUS = 0.25
N_CRATERS = 16
SEED = 2


def craters():
    rnd = random.Random(SEED)
    out = []
    for i in range(N_CRATERS):
        z = rnd.uniform(-1, 1)
        a = rnd.uniform(0, 2 * math.pi)
        r = math.sqrt(1 - z * z)
        c = C.Vector((r * math.cos(a), r * math.sin(a), z))
        if i < 3:  # make sure the front (-Y) shows a few big ones
            c = C.Vector((rnd.uniform(-0.5, 0.5), -1.0, rnd.uniform(-0.5, 0.5))).normalized()
        out.append((c, rnd.uniform(0.16, 0.38)))
    return out


CRATERS = craters()


def disp(d):
    h = 0.012 * noise.noise(d * 3.0)
    for c, a in CRATERS:
        ang = d.angle(c)
        depth = 0.16 * a
        if ang < a:
            h -= depth * (1 - (ang / a) ** 2)
        elif ang < 1.35 * a:
            h += depth * 0.35 * (1 - (ang - a) / (0.35 * a))
    return h


def build():
    mb = C.MB("Moon", smooth_angle=25)

    def lfn(v):
        d = v.normalized()
        return d * (1 + disp(d))

    def col(c, n):
        d = c.normalized()
        for cc, a in CRATERS:
            if d.angle(cc) < 0.8 * a:
                return "moon_dark"
        return "moon"
    mb.ico(col, C.M(scale=RADIUS), subdiv=4, smooth=False, lfn=lfn)
    mb.to_object(origin=(0, 0, 0))


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="center", hero_az=-20)
