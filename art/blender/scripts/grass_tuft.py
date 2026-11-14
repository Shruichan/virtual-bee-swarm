"""Grass tuft: nine bent blades (two greens) around a small clump. One mesh, pivot = base. ~8 cm tall."""
import math
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "grass_tuft"
BUDGET = 3000

N_BLADES = 9
SEED = 5


def build():
    mb = C.MB("GrassTuft", smooth_angle=35)
    rnd = random.Random(SEED)
    mb.sphere("leaf_dark", C.M((0, 0, 0.002), scale=(0.018, 0.018, 0.007)), seg=10, rings=5)
    for i in range(N_BLADES):
        a = 360.0 * i / N_BLADES + rnd.uniform(-12, 12)
        h = rnd.uniform(0.05, 0.09)
        w = rnd.uniform(0.008, 0.012)
        lean = rnd.uniform(8, 28)
        out = [(-w / 2, 0), (w / 2, 0), (w * 0.42, h * 0.33), (w * 0.3, h * 0.66), (0.0, h), (-w * 0.3, h * 0.66),
               (-w * 0.42, h * 0.33)]

        def bend(v, h=h):
            t = max(0.0, v.y / h)
            return C.Vector((v.x, v.y, v.z - 0.35 * h * t * t))  # tip curls outward
        ra = math.radians(a)
        base = (-0.008 * math.sin(ra), 0.008 * math.cos(ra), 0.0)
        mb.poly("leaf" if i % 2 else "leaf_dark", out, 0.0018, C.M(base, (90 - lean, 0, a)), lfn=bend, smooth=True)
    mb.to_object(origin=(0, 0, 0))


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base", hero_az=-20)
