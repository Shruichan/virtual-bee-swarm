"""Syrup bottle: oval squeeze bottle labelled SUGAR SYRUP (the RIGHT choice at the feeding station). White bottle,
sky-blue label band, leaf-green flip cap. Pivot = base. Objects: Bottle, Cap (pivot = its base), Label."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "syrup_bottle"
BUDGET = 3000

RX, FLAT = 0.035, 0.64  # half width, depth/width ratio (oval body)
LABEL_Z = (0.034, 0.106)


def build():
    root = C.empty("SyrupBottle", size=0.06)
    oval = C.M(scale=(1, FLAT, 1))
    bo = C.MB("Bottle", smooth_angle=50)
    prof = [(0, 0), (0.031, 0), (RX, 0.006), (RX, 0.125), (0.03, 0.142), (0.018, 0.151), (0.0165, 0.156),
            (0, 0.156)]
    bo.lathe("plastic", prof, seg=24, m=oval)
    bo.to_object(origin=(0, 0, 0), parent=root)

    cap = C.MB("Cap", smooth_angle=50)
    cap.lathe("leaf", [(0.0185, 0.154), (0.0195, 0.156), (0.0195, 0.172), (0.016, 0.176), (0.007, 0.178),
                       (0.0045, 0.196), (0.0, 0.198)], seg=16, lfn=None)
    cap.sphere("white", C.M((0, 0, 0.197), scale=0.004), seg=8, rings=5)
    cap.box("leaf_dark", C.M((0, -0.019, 0.17), scale=(0.012, 0.004, 0.006)), bevel=(0.0012, 1))  # flip tab
    cap.to_object(origin=(0, 0, 0.154), parent=root)

    lb = C.MB("Label", smooth_angle=50)
    rx, ry = RX + 0.0006, (RX + 0.0006) * FLAT
    lb.lathe("sky", [(rx, LABEL_Z[0]), (rx, LABEL_Z[1])], seg=24, cap_bottom=False, cap_top=False,
             m=C.M(scale=(1, FLAT, 1)))
    wrap = C.ellipse_wrap(rx, ry, 0.0)
    lb.flat("cream", C.rounded_rect(0.054, 0.05, 0.006, 3), lfn=lambda v: wrap(C.Vector((v.x, v.y + 0.07, 0.0006))))
    lb.text("charcoal", "SUGAR", 0.0155, 0.0, lfn=lambda v: wrap(C.Vector((v.x, v.y + 0.0815, 0.0011))), resolution=2)
    lb.text("charcoal", "SYRUP", 0.0155, 0.0, lfn=lambda v: wrap(C.Vector((v.x, v.y + 0.0615, 0.0011))), resolution=2)
    # a little white sugar cube icon under the words
    for i, x in enumerate((-0.006, 0.006)):
        lb.flat("white", C.rounded_rect(0.008, 0.008, 0.0015, 1),
                lfn=lambda v, x=x: wrap(C.Vector((v.x + x, v.y + 0.0495, 0.0011))))
    lb.to_object(origin=(0, 0, LABEL_Z[0]), parent=root)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom center)", hero_az=-20)
