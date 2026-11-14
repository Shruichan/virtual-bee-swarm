"""Entrance reducer: the RIGHT choice at the wasps station (SMALLER door: fewer guards needed). A standalone,
chunkier copy of the wooden cleat in hive.py (hive.fbx has its own EntranceReducer object on the landing board),
with two charcoal arrows pointing at the notch. Pivot = base (bottom center). One mesh."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "reducer"
BUDGET = 3000

L, HH, DD, NOTCH = 0.20, 0.03, 0.03, 0.05


def build():
    mb = C.MB("Reducer", smooth_angle=30)
    for s in (1, -1):
        cx = s * (NOTCH / 2 + (L - NOTCH) / 4)
        mb.box("cream", C.M((cx, 0, HH / 2), scale=((L - NOTCH) / 2, DD, HH)), bevel=(0.003, 1))
    mb.box("wood", C.M((0, 0, HH * 0.75), scale=(NOTCH + 0.006, DD * 0.96, HH / 2)), bevel=(0.003, 1))
    # arrows on the front face pointing in at the notch ("smaller")
    front = C.M((0, -DD / 2 - 0.0005, 0), (90, 0, 0))
    for s in (1, -1):
        x0 = s * (NOTCH / 2 + 0.018)
        tip = s * (NOTCH / 2 + 0.006)
        mb.flat("charcoal", [(x0, HH / 2 - 0.004), (x0, HH / 2 + 0.004), (x0 + s * 0.022, HH / 2 + 0.004),
                             (x0 + s * 0.022, HH / 2 - 0.004)], front)
        mb.flat("charcoal", [(x0 + 0.0005 * s, HH / 2 - 0.009), (x0 + 0.0005 * s, HH / 2 + 0.009), (tip, HH / 2)],
                front)
    mb.to_object(origin=(0, 0, 0))


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom center)", hero_az=-25)
