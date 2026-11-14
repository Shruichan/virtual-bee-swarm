"""Worker bee: ~3.5 cm, one mesh, one material, vertex colours, front = Unity +Z, pivot = body center.
Drawn ~150 times with GPU instancing, so it stays under 300 triangles (it is ~220)."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "worker_bee"
BUDGET = 300

# proportions (meters)
HEAD = dict(loc=(0, -0.0118, 0.0008), radii=(0.0050, 0.0046, 0.0050))
THORAX = dict(loc=(0, -0.0050, 0.0008), radii=(0.0058, 0.0052, 0.0058))
ABDOMEN = dict(loc=(0, 0.0065, -0.0004), radii=(0.0072, 0.0102))  # (cross radius, length radius)
WING_SPAN = 0.0165
WING_DIHEDRAL = 38  # degrees, tips up
# tail (-1) to front (+1) stripes on the abdomen
STRIPES = [(-0.6, "charcoal"), (-0.25, "amber"), (0.1, "charcoal"), (0.45, "amber"), (0.7, "charcoal"), (1.0, "amber")]


def build():
    mb = C.MB("WorkerBee", mat="bee", smooth_angle=70)
    mb.sphere("charcoal", C.M(HEAD["loc"], scale=HEAD["radii"]), seg=6, rings=4)
    for side in (1, -1):  # big white cartoon eyes
        mb.sphere("white", C.M((0.0027 * side, -0.0150, 0.0024), scale=0.0019), seg=5, rings=3)
    mb.sphere("honey", C.M(THORAX["loc"], scale=THORAX["radii"]), seg=6, rings=4)
    r, L = ABDOMEN["radii"]
    m = C.M(ABDOMEN["loc"], (90, 0, 0), (r, r, L))  # poles along Y, local +Z = front
    inv = m.inverted()
    mb.sphere(lambda c, n: C.bands((inv @ c).z, STRIPES), m, seg=8, rings=7)
    tail = ABDOMEN["loc"][1] + L
    mb.lathe("charcoal", [(0.0012, 0.0), (0.0, 0.0028)], seg=4,
             m=C.frame((0, tail - 0.0006, ABDOMEN["loc"][2]), (0, 1, 0)), cap_top=False, smooth=False)
    s = WING_SPAN / 0.0145
    pts = [(0.0, -0.0014), (0.006, -0.0028), (0.013, -0.0005), (0.0145, 0.004), (0.010, 0.0068), (0.004, 0.0048),
           (0.0, 0.0014)]
    for side in (1, -1):
        wp = [(x * s * side, y * s) for x, y in pts]
        mb.poly("sky", wp, 0.0003, C.M((0.0015 * side, -0.0045, 0.0058), (0, -WING_DIHEDRAL * side, 0)))
    mb.to_object(origin=(0, 0, 0))


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="body center", hero_az=-35)
