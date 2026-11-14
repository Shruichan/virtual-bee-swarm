"""Deep bowl of water (the WRONG choice at the heat-wave station: bees drown in deep water). Tall sky-blue bowl,
cream inside, dark deep water. Pivot = base. Objects: Bowl, Water (pivot = water surface center)."""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "deep_bowl"
BUDGET = 3000

WATER_Z = 0.088


def build():
    root = C.empty("DeepBowl", size=0.06)
    bw = C.MB("Bowl", smooth_angle=45)
    prof = [(0, 0), (0.045, 0), (0.05, 0.006), (0.072, 0.05), (0.083, 0.098), (0.086, 0.104), (0.082, 0.107),
            (0.078, 0.1), (0.067, 0.052), (0.045, 0.016), (0, 0.016)]

    def col(c, n):
        radial = (c.x * n.x + c.y * n.y) / max(1e-6, math.hypot(c.x, c.y))
        if c.z > 0.097:
            return "cream"
        if n.z < -0.5 or radial > 0.1:
            return "sky" if not (0.062 < c.z < 0.072) else "white"
        return "cream"
    bw.lathe(col, prof, seg=28)
    bw.to_object(origin=(0, 0, 0), parent=root)

    wa = C.MB("Water", smooth_angle=30)
    wa.lathe("ocean", [(0.0757, WATER_Z), (0, WATER_Z)], seg=28)
    wa.torus("water", 0.04, 0.0012, 24, 4, C.M((0, 0, WATER_Z + 0.0004), scale=(1, 1, 0.5)))  # a ripple
    wa.to_object(origin=(0, 0, WATER_Z), parent=root)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom center)", hero_az=-25)
