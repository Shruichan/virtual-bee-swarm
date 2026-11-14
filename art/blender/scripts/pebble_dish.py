"""Pebble dish (the RIGHT choice at the heat-wave station): a shallow terracotta saucer with a thin layer of water and
pebbles poking out as landing spots. Pivot = base. Objects: Dish, Water, Pebbles, LandingSpot1..N (empties on the
pebble tops where bees can land to drink)."""
import math
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "pebble_dish"
BUDGET = 3000

WATER_Z = 0.017


def build():
    root = C.empty("PebbleDish", size=0.06)
    di = C.MB("Dish", smooth_angle=45)
    prof = [(0, 0), (0.085, 0), (0.09, 0.003), (0.1, 0.022), (0.102, 0.026), (0.098, 0.028), (0.091, 0.011),
            (0.0, 0.011)]
    di.lathe(lambda c, n: "cream" if c.z > 0.024 else "terracotta", prof, seg=28)
    di.to_object(origin=(0, 0, 0), parent=root)

    wa = C.MB("Water", smooth_angle=30)
    wa.lathe("water", [(0.0935, WATER_Z), (0, WATER_Z)], seg=28)
    wa.to_object(origin=(0, 0, WATER_Z), parent=root)

    pb = C.MB("Pebbles", smooth_angle=35)
    rnd = random.Random(11)
    spots = [(0.055 * math.cos(math.radians(a)), 0.055 * math.sin(math.radians(a))) for a in range(15, 375, 60)]
    spots += [(0.02 * math.cos(math.radians(a)), 0.02 * math.sin(math.radians(a))) for a in (40, 160, 280)]
    tops = []
    for i, (x, y) in enumerate(spots):
        sx, sy, sz = rnd.uniform(0.015, 0.021), rnd.uniform(0.012, 0.017), rnd.uniform(0.008, 0.011)
        zc = WATER_Z + rnd.uniform(-0.001, 0.002)
        pb.ico(rnd.choice(["stone", "stone_dark", "moon"]),
               C.M((x, y, zc), (rnd.uniform(-10, 10), rnd.uniform(-10, 10), rnd.uniform(0, 180)), (sx, sy, sz)),
               subdiv=2, smooth=True)
        tops.append((x, y, zc + sz))
    pb.to_object(origin=(0, 0, WATER_Z), parent=root)
    for i, p in enumerate(tops):
        C.empty("LandingSpot%d" % (i + 1), p, root, size=0.008)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom center)", hero_az=-25)
