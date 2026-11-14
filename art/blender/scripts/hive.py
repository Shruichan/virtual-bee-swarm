"""Hive: a stacked Langstroth hive (~0.44 m tall) for the real table. Front (entrance) = Unity +Z, pivot = base.

Hierarchy (names match the Unity placeholder / PLAN S11.6):
  Hive
    LiftRoot (tip/heft the hive by rotating this)
      Stand, BoxLower (brood box + "LOCAL 20,000" plaque), BoxUpper (open top: frames + comb inside),
      Lid (pivot = back-bottom edge: rotate on X to open, or move up to lift),
      Door (pivot at the entrance; scale.x = entrance width) > DoorSlot,
      DoorPoint, SwarmCenter, HeatGlow (hidden in previews), Hexagons > HexSlot1..4,
      EntranceReducer (separate wooden cleat resting on the landing board, pivot = its base)
NOTE: the Unity placeholder scene has its door facing -Z; if the slot loader keeps that, rotate the prefab 180.
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "hive"
BUDGET = 5000

W, D = 0.34, 0.40  # box footprint: width (X) x depth (Y)
STAND_TOP = 0.057
LOWER_H, UPPER_H = 0.18, 0.14
WALL = 0.018
LID_H = 0.05
ENTRANCE = (0.22, 0.025)  # width, height
COLORS = dict(lower="honey", upper="cream", stand="wood_dark", lid="amber", lid_top="steel", trim="wood_dark")


def build():
    root = C.empty("Hive", size=0.1)
    lift = C.empty("LiftRoot", (0, 0, 0), root, size=0.08)
    z0, z1 = STAND_TOP, STAND_TOP + LOWER_H
    z_top = z1 + UPPER_H

    # ---------------------------------------------------------------- stand + landing board
    st = C.MB("Stand")
    for sx in (1, -1):
        for sy in (1, -1):
            st.box(COLORS["stand"], C.M((sx * 0.15, sy * 0.17, 0.0175), scale=(0.04, 0.04, 0.035)), bevel=(0.004, 1))
    st.box(COLORS["stand"], C.M((0, 0, 0.046), scale=(W + 0.03, D + 0.03, 0.022)), bevel=(0.004, 1))
    st.box("wood", C.M((0, -(D / 2 + 0.015) - 0.035, 0.046), scale=(0.30, 0.07, 0.022)), bevel=(0.004, 1))
    st.to_object(origin=(0, 0, 0), parent=lift)

    # ---------------------------------------------------------------- lower (brood) box
    lo = C.MB("BoxLower")
    lo.box(COLORS["lower"], C.M((0, 0, z0 + LOWER_H / 2), scale=(W, D, LOWER_H)), bevel=(0.006, 1))
    hz = z0 + LOWER_H * 0.62
    for s in (1, -1):
        lo.box(COLORS["trim"], C.M((s * (W / 2 + 0.0015), 0, hz), scale=(0.006, 0.11, 0.024)), bevel=(0.002, 1))
    lo.box(COLORS["trim"], C.M((0, D / 2 + 0.0015, hz), scale=(0.11, 0.006, 0.024)), bevel=(0.002, 1))
    # union hall plaque: LOCAL 20,000
    pm = C.M((0, -D / 2 - 0.0015, z0 + LOWER_H * 0.58), (90, 0, 0))
    lo.poly("cream", C.rounded_rect(0.19, 0.058, 0.008, 2), 0.004, pm, bevel=(0.001, 1))
    lo.hexprism("amber", 0.016, 0.003, pm @ C.M((-0.07, 0, 0.003)))
    lo.text("charcoal", "LOCAL", 0.03, 0.0, pm @ C.M((0.016, 0.012, 0.0024)), resolution=2)
    lo.text("charcoal", "20,000", 0.03, 0.0, pm @ C.M((0.016, -0.013, 0.0024)), resolution=2)
    lo.to_object(origin=(0, 0, z0), parent=lift)

    # ---------------------------------------------------------------- upper box: open top, frames + comb inside
    up = C.MB("BoxUpper")
    zc = z1 + UPPER_H / 2
    up.box(COLORS["upper"], C.M((0, -D / 2 + WALL / 2, zc), scale=(W, WALL, UPPER_H)), bevel=(0.005, 1))
    up.box(COLORS["upper"], C.M((0, D / 2 - WALL / 2, zc), scale=(W, WALL, UPPER_H)), bevel=(0.005, 1))
    for s in (1, -1):
        up.box(COLORS["upper"], C.M((s * (W / 2 - WALL / 2), 0, zc), scale=(WALL, D - 2 * WALL + 0.004, UPPER_H)),
               bevel=(0.005, 1))
        up.box(COLORS["trim"], C.M((s * (W / 2 + 0.0015), 0, z1 + UPPER_H * 0.6), scale=(0.006, 0.11, 0.022)),
               bevel=(0.002, 1))
    up.box("charcoal", C.M((0, 0, z1 + 0.004), scale=(W - 2 * WALL, D - 2 * WALL, 0.008)))
    n_frames = 8
    pitch = (W - 2 * WALL) / n_frames
    for i in range(n_frames):
        x = -W / 2 + WALL + pitch * (i + 0.5)
        up.box("wood", C.M((x, 0, z_top - 0.007), scale=(0.022, D - 2 * WALL + 0.012, 0.010)), bevel=(0.002, 1))
        up.box("honey" if i % 2 else "honey_dark", C.M((x, 0, z1 + (UPPER_H - 0.012) / 2 + 0.004),
                                                       scale=(0.02, D - 2 * WALL - 0.012, UPPER_H - 0.022)))
    # four hexagon sockets for the progress tiles
    hex_z = z1 + UPPER_H * 0.52
    hex_x = [-0.075, -0.025, 0.025, 0.075]
    for x in hex_x:
        up.hexprism("charcoal", 0.0245, 0.003, C.M((x, -D / 2 - 0.001, hex_z), (90, 0, 0)), bevel=(0.0008, 1))
    # honey drips sneaking out from under the lid
    for x, ln in ((-0.125, 0.045), (0.095, 0.03), (0.128, 0.055)):
        up.sphere("honey", C.M((x, -D / 2 - 0.003, z_top - ln / 2), scale=(0.0075, 0.0045, ln / 2)), seg=8, rings=6)
        up.sphere("honey", C.M((x, -D / 2 - 0.0035, z_top - ln + 0.002), scale=(0.0095, 0.006, 0.0095)), seg=8, rings=6)
    up.to_object(origin=(0, 0, z1), parent=lift)

    # ---------------------------------------------------------------- lid (inner cover + telescoping cover)
    ld = C.MB("Lid")
    ld.box("wood", C.M((0, 0, z_top + 0.006), scale=(W + 0.004, D + 0.004, 0.012)), bevel=(0.002, 1))
    ld.box(COLORS["lid"], C.M((0, 0, z_top + 0.012 + LID_H / 2), scale=(W + 0.04, D + 0.04, LID_H)),
           bevel=(0.006, 1))
    ld.box(COLORS["lid_top"], C.M((0, 0, z_top + 0.012 + LID_H + 0.003), scale=(W + 0.05, D + 0.05, 0.008)),
           bevel=(0.003, 1))
    ld.to_object(origin=(0, D / 2 + 0.02, z_top), parent=lift)

    # ---------------------------------------------------------------- entrance: Door pivot (scale.x = width)
    door_p = (0, -D / 2 - 0.001, z0 + ENTRANCE[1] / 2)
    door = C.empty("Door", door_p, lift, size=0.03)
    ds = C.MB("DoorSlot")
    ds.box("charcoal", C.M(door_p, scale=(ENTRANCE[0], 0.008, ENTRANCE[1])))
    ds.to_object(origin=door_p, parent=door)
    C.empty("DoorPoint", (0, -D / 2 - 0.02, z0 + ENTRANCE[1] / 2), lift)
    C.empty("SwarmCenter", (0, 0, z0 + (LOWER_H + UPPER_H) / 2), lift)
    hx = C.empty("Hexagons", (0, -D / 2 - 0.003, hex_z), lift)
    for i, x in enumerate(hex_x):
        C.empty("HexSlot%d" % (i + 1), (x, -D / 2 - 0.003, hex_z), hx, size=0.01)

    # ---------------------------------------------------------------- heat glow shell (hidden in previews)
    hg = C.MB("HeatGlow", mat="glow_soft")
    hgz = z0 + (LOWER_H + UPPER_H) / 2
    hg.box("fire", C.M((0, 0, hgz), scale=(W + 0.04, D + 0.04, LOWER_H + UPPER_H + 0.03)), bevel=(0.02, 2))
    hg.to_object(origin=(0, 0, hgz), parent=lift, preview_hide=True)

    # ---------------------------------------------------------------- entrance reducer (wooden cleat with a notch)
    er = C.MB("EntranceReducer")
    L, hh, dd, notch = 0.30, 0.02, 0.02, 0.06
    ery = -D / 2 - 0.05
    for s in (1, -1):
        cx = s * (notch / 2 + (L - notch) / 4)
        er.box("cream", C.M((cx, ery, z0 + hh / 2), scale=((L - notch) / 2, dd, hh)), bevel=(0.002, 1))
    er.box("cream", C.M((0, ery, z0 + hh * 0.75), scale=(notch + 0.004, dd, hh / 2)), bevel=(0.002, 1))
    er.to_object(origin=(0, ery, z0), parent=lift)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom center)", hero_az=-32)
