"""Rain cloud: the fire brigade (SHOULD, V18). A crew of 6 bees flies it out of the window and parks it over the
table, then Unity rains particles from RainPoint. Puffy white cloud with a flat grey underside and two determined
eyes; a few drops hang under it (hide Drops when it isn't raining). Pivot = cloud center (it floats).
Objects: RainCloud (empty) > Cloud, Drops, RainPoint (bottom center, +Z = front; rain falls along -Y)."""
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "rain_cloud"
BUDGET = 3000

BOTTOM = -0.032  # flat underside height (Blender Z)
PUFFS = [  # (x, y, z, r): y > 0 is behind
    (0.0, 0.0, 0.012, 0.066),
    (-0.072, 0.004, -0.004, 0.05),
    (0.07, 0.002, -0.002, 0.054),
    (-0.03, 0.006, 0.05, 0.048),
    (0.032, 0.004, 0.046, 0.046),
    (0.0, 0.035, 0.022, 0.05),
    (-0.11, 0.01, -0.012, 0.03),
    (0.112, 0.008, -0.012, 0.032),
]


def build():
    root = C.empty("RainCloud", size=0.08)
    cl = C.MB("Cloud", smooth_angle=70)

    def flat_bottom(v):
        return C.Vector((v.x, v.y, max(v.z, BOTTOM)))

    for x, y, z, r in PUFFS:
        cl.sphere(lambda c, n: "cloud_dark" if c.z < BOTTOM + 0.006 else "cloud", C.M((x, y, z), scale=r),
                  seg=14, rings=8, vfn=flat_bottom)
    # eyes on the front puff (front = Blender -Y)
    for s in (-1, 1):
        ex, ez = s * 0.02, 0.026
        dec = C.ellipsoid_decal((0.0, 0.0, 0.012), (0.066, 0.066, 0.066), (ex, -0.08, ez), (1, 0, 0), (0, 0, 1),
                                (0, -1, 0))
        cl.flat("charcoal", C.circle_pts(1, 10, rx=0.0075, ry=0.0105), lfn=lambda v: dec(C.Vector((v.x, v.y, 0.0008))))
        cl.flat("white", C.circle_pts(0.0028, 8), lfn=lambda v: dec(C.Vector((v.x - 0.0022, v.y + 0.004, 0.0014))))
        # determined brow
        bx = [(-0.011, 0.016), (0.011, 0.016), (0.011, 0.0205), (-0.011, 0.0205)]
        brow = C.M((0, 0, 0), (0, 0, 16 * s))
        cl.flat("cloud_dark", [tuple((brow @ C.Vector((px, py, 0)))[:2]) for px, py in bx],
                lfn=lambda v: dec(C.Vector((v.x, v.y, 0.0012))))
    cl.to_object(origin=(0, 0, 0), parent=root)

    dr = C.MB("Drops", smooth_angle=60)
    rnd = random.Random(3)
    for i in range(7):
        x = -0.09 + 0.03 * i + rnd.uniform(-0.006, 0.006)
        y = rnd.uniform(-0.03, 0.03)
        z = BOTTOM - 0.018 - rnd.uniform(0.0, 0.03)
        dr.sphere("water", C.M((x, y, z), scale=(0.0055, 0.0055, 0.0062)), seg=8, rings=5)
        dr.cyl("water", 0.0052, 0.009, seg=8, r2=0.0, m=C.M((x, y, z + 0.002)), caps=False)
    dr.to_object(origin=(0, 0, BOTTOM - 0.02), parent=root)

    C.empty("RainPoint", origin=(0, 0, BOTTOM), parent=root, size=0.02)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="cloud center", hero_az=-25)
