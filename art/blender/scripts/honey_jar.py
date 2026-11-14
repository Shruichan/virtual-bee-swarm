"""Honey jar: store-bought honey (the WRONG choice at the feeding station). Glass jar full of honey, red ribbed lid,
cream label reading HONEY. Pivot = base. Objects: Jar, Lid (pivot = its base, unscrew it), Label."""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "honey_jar"
BUDGET = 3000

LABEL_Z = (0.024, 0.066)
LABEL_R = 0.0398


def build():
    root = C.empty("HoneyJar", size=0.06)
    jar = C.MB("Jar", smooth_angle=50)
    prof = [(0, 0), (0.032, 0), (0.036, 0.004), (0.038, 0.015), (0.039, 0.06), (0.036, 0.078), (0.029, 0.088),
            (0.027, 0.092), (0.027, 0.098), (0.0, 0.098)]
    jar.lathe(lambda c, n: "honey_dark" if c.z < 0.008 else ("amber" if c.z < 0.079 else "honey"), prof, seg=24)
    jar.to_object(origin=(0, 0, 0), parent=root)

    lid = C.MB("Lid", smooth_angle=40)

    def ribs(v):  # alternate verts in/out = grip ribs
        a = math.atan2(v.y, v.x)
        k = 1.0 + (0.045 if math.cos(24 * a) > 0 else 0.0) * (1 if 0.0965 < v.z < 0.1145 else 0)
        return C.Vector((v.x * k, v.y * k, v.z))
    lid.lathe("red", [(0.0285, 0.094), (0.031, 0.096), (0.031, 0.114), (0.029, 0.117), (0, 0.118)], seg=24, lfn=ribs)
    lid.to_object(origin=(0, 0, 0.094), parent=root)

    lb = C.MB("Label", smooth_angle=50)
    lb.lathe("cream", [(LABEL_R, LABEL_Z[0]), (LABEL_R, LABEL_Z[1])], seg=24, cap_bottom=False, cap_top=False)
    for z in (LABEL_Z[0] + 0.002, LABEL_Z[1] - 0.002):
        lb.lathe("fire", [(LABEL_R + 0.0004, z - 0.0016), (LABEL_R + 0.0004, z + 0.0016)], seg=24, cap_bottom=False,
                 cap_top=False)
    wrap = C.cyl_wrap(LABEL_R, 0.0, 0.0)
    lb.text("charcoal", "HONEY", 0.019, 0.0, lfn=lambda v: wrap(C.Vector((v.x, v.y + 0.048, v.z + 0.0008))),
            resolution=2)
    lb.text("honey_dark", "STORE BRAND", 0.0062, 0.0, lfn=lambda v: wrap(C.Vector((v.x, v.y + 0.035, v.z + 0.0008))),
            resolution=2)
    # price sticker on the side
    lb.flat("white", C.circle_pts(1, 10, rx=0.009, ry=0.007),
            lfn=lambda v: C.cyl_wrap(LABEL_R, 0.0, 70)(C.Vector((v.x, v.y + 0.045, 0.0009))))
    lb.text("red", "$3.99", 0.0055, 0.0, lfn=lambda v: C.cyl_wrap(LABEL_R, 0.0, 70)(C.Vector((v.x, v.y + 0.045, 0.0013))),
            resolution=2)
    lb.to_object(origin=(0, 0, LABEL_Z[0]), parent=root)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom center)", hero_az=-20)
