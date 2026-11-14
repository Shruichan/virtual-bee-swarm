"""Beeswax: the hero bee. A stingerless drone and union shop steward of Local 20,000: big head, sleepy grumpy
lids, buck teeth, fluffy collar, lanyard + badge, clipboard in his right hand, other hand on his hip.

~0.35 m tall, front = Unity +Z, pivot = body center (the chest). Hierarchy (names match the Unity placeholder):
  Beeswax (root)
    Body (empty; BeeswaxController scales it while talking)
      Torso, Head, Eyes, Mouth (pivot = top edge, scale Y to open/close), WingL / WingR (pivot = wing root,
      rotate on Unity Z to flap), Lanyard, Clipboard
      HandR, HandL, CaptionAnchor (empties)
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402
from clipboard import add_clipboard  # noqa: E402
from mathutils import Vector  # noqa: E402

ITEM = "beeswax"
BUDGET = 15000

# ---- proportions (meters). Tweak these to restyle him.
CHEST = dict(loc=(0, 0, 0), radii=(0.058, 0.052, 0.062))
HEAD = dict(loc=(0, -0.012, 0.120), radii=(0.074, 0.066, 0.070))
ABDOMEN = dict(loc=(0, 0.060, -0.062), radii=(0.068, 0.064, 0.088), axis_tilt=38)  # tilt = degrees back-down
EYE = dict(x=0.029, y=-0.068, z=0.134, radii=(0.025, 0.016, 0.030), splay=14)
LID_DROOP = 12  # degrees; inner corners lower = grumpy
MOUTH = dict(top_z=0.099, width=0.048, height=0.026)
WING = dict(root=(0.032, 0.036, 0.050), span=0.165, chord=0.068, sweep=22, dihedral=40)
STRIPES = [(-0.72, "charcoal"), (-0.45, "amber"), (-0.15, "charcoal"), (0.15, "amber"), (0.45, "charcoal"),
           (1.0, "amber")]  # tail (-1) .. front (+1)


def head_decal(origin_xz, out=(0, -1, 0)):
    """lfn that wraps flat local (x, y, height) art onto the head's front."""
    ox, oz = origin_xz
    return C.ellipsoid_decal(HEAD["loc"], HEAD["radii"], (ox, HEAD["loc"][1], oz), (1, 0, 0), (0, 0, 1), out)


def build():
    root = C.empty("Beeswax", (0, 0, 0), size=0.1)
    body = C.empty("Body", (0, 0, 0), root, size=0.08)

    # ------------------------------------------------------------------ torso: chest, collar, abdomen, limbs
    t = C.MB("Torso", mat="bee", smooth_angle=60)
    t.sphere("amber", C.M(CHEST["loc"], scale=CHEST["radii"]), seg=20, rings=12)
    for i in range(11):  # fluffy collar = ring of puffs
        a = 2 * math.pi * i / 11
        t.sphere("cream", C.M((0.036 * math.cos(a), -0.004 + 0.034 * math.sin(a), 0.052), scale=(0.017, 0.017, 0.013)),
                 seg=8, rings=5)
    tilt = ABDOMEN["axis_tilt"]
    m = C.M(ABDOMEN["loc"], (tilt, 0, 0), ABDOMEN["radii"])  # local +Z points up-front (toward the chest)
    inv = m.inverted()
    t.sphere(lambda c, n: C.bands((inv @ c).z, STRIPES), m, seg=20, rings=16)
    # right arm (character's right = -X) holds the clipboard, left hand on the hip
    hand_r = (-0.096, -0.070, -0.040)
    hand_l = (0.074, 0.028, -0.058)
    t.limb("charcoal", [(-0.050, -0.010, 0.018), (-0.086, -0.026, -0.016), hand_r], 0.0095, 0.0085)
    t.limb("charcoal", [(0.050, -0.010, 0.018), (0.090, 0.006, -0.012), hand_l], 0.0095, 0.0085)
    for p in (hand_r, hand_l):
        t.sphere("honey", C.M(p, scale=0.0145), seg=10, rings=6)
    for s in (1, -1):
        t.limb("charcoal", [(0.028 * s, 0.0, -0.064), (0.036 * s, -0.018, -0.100), (0.038 * s, -0.024, -0.118)],
               0.0085, 0.008)
        t.sphere("charcoal", C.M((0.039 * s, -0.034, -0.124), scale=(0.015, 0.021, 0.0105)), seg=10, rings=6)
        t.limb("charcoal", [(0.048 * s, 0.030, -0.066), (0.066 * s, 0.020, -0.092)], 0.006, 0.005,
               end_ball=0.008)  # little middle legs
    torso = t.to_object(origin=(0, 0, 0), parent=body)

    # ------------------------------------------------------------------ head: head ball, lids, antennae
    h = C.MB("Head", mat="bee", smooth_angle=60)
    h.sphere("honey", C.M(HEAD["loc"], scale=HEAD["radii"]), seg=24, rings=14)
    for s in (1, -1):
        eye_c = Vector((EYE["x"] * s, EYE["y"], EYE["z"]))
        er = Vector(EYE["radii"]) * 1.13
        n = Vector((-s * math.sin(math.radians(LID_DROOP)), 0.0, math.cos(math.radians(LID_DROOP))))
        h.half_dome("amber", C.M(eye_c, (0, 0, EYE["splay"] * s), er), eye_c + Vector((0, 0, 0.003)), n,
                    seg=16, rings=10)
        h.limb("charcoal", [(0.022 * s, -0.030, 0.178), (0.036 * s, -0.046, 0.214), (0.060 * s, -0.070, 0.232)],
               0.0038, 0.0034, end_ball=0.011, end_color="honey")
    head = h.to_object(origin=HEAD["loc"], parent=body)

    # ------------------------------------------------------------------ eyes (whites, pupils, shine)
    e = C.MB("Eyes", mat="bee", smooth_angle=70)
    for s in (1, -1):
        e.sphere("white", C.M((EYE["x"] * s, EYE["y"], EYE["z"]), (0, 0, EYE["splay"] * s), EYE["radii"]), seg=16,
                 rings=10)
        px, pz = EYE["x"] * s - 0.003 * s, EYE["z"] - 0.006
        e.sphere("charcoal", C.M((px, EYE["y"] - 0.0145, pz), (0, 0, EYE["splay"] * s), (0.0115, 0.005, 0.0135)),
                 seg=12, rings=8)
        e.sphere("white", C.M((px - 0.004, EYE["y"] - 0.0195, pz + 0.005), scale=0.0034), seg=8, rings=5)
    eyes = e.to_object(origin=(0, EYE["y"], EYE["z"]), parent=body)

    # ------------------------------------------------------------------ mouth (D-shaped, buck teeth, tongue)
    mo = C.MB("Mouth", mat="bee", smooth_angle=60)
    w, ht = MOUTH["width"], MOUTH["height"]
    dec = head_decal((0.0, MOUTH["top_z"]))
    outline = [(w / 2 * math.cos(math.pi * i / 12), -ht * math.sin(math.pi * i / 12)) for i in range(13)]
    mo.poly("charcoal", outline, 0.004, lfn=dec)
    for s in (1, -1):
        mo.poly("white", C.rounded_rect(0.0085, 0.0095, 0.0022, 2), 0.003,
                lfn=lambda v, s=s: dec(Vector((v.x + 0.0048 * s, v.y - 0.0052, v.z + 0.0024))))
    mo.poly("pink", C.circle_pts(1, 12, rx=0.0105, ry=0.0052), 0.003,
            lfn=lambda v: dec(Vector((v.x, v.y - ht + 0.0075, v.z + 0.0016))))
    mouth = mo.to_object(origin=dec(Vector((0, 0, 0))), parent=body)

    # ------------------------------------------------------------------ wings (pivot = root, flap on Unity Z)
    for s, name in ((1, "WingL"), (-1, "WingR")):
        wg = C.MB(name, mat="bee", smooth_angle=30)
        span, chord = WING["span"], WING["chord"]
        fore = [(span / 2 + span / 2 * math.cos(a), chord / 2 * math.sin(a))
                for a in [2 * math.pi * i / 20 for i in range(20)]]
        hind = [(span * 0.32 + span * 0.32 * math.cos(a), chord * 0.36 * math.sin(a))
                for a in [2 * math.pi * i / 16 for i in range(16)]]
        root_pt = Vector(WING["root"]) * Vector((s, 1, 1))
        for pts, sweep, off, col in ((fore, WING["sweep"], 0.0, "wing"), (hind, WING["sweep"] + 22, 0.008, "sky")):
            mp = [(x * s, y) for x, y in pts]
            wg.poly(col, mp, 0.003, C.M(root_pt + Vector((0, off, -0.004 if off else 0)),
                                            (0, -WING["dihedral"] * s, sweep * s)), smooth=False)
        wg.to_object(origin=root_pt, parent=body)

    # ------------------------------------------------------------------ lanyard + badge
    ly = C.MB("Lanyard", mat="bee", smooth_angle=50)
    for s in (1, -1):
        ly.limb("fire", [(0.030 * s, -0.046, 0.060), (0.020 * s, -0.054, 0.032), (0.007 * s, -0.058, 0.010),
                         (0.0, -0.059, 0.004)], 0.0024, seg=6)
    badge_m = C.M((0, -0.0545, -0.016), (105, 0, 0))
    ly.poly("cream", C.rounded_rect(0.031, 0.041, 0.004, 2), 0.0028, badge_m, bevel=(0.0006, 1))
    ly.hexprism("amber", 0.0068, 0.0016, badge_m @ C.M((0, 0.0075, 0.0018)))
    for i, bw in enumerate((0.021, 0.015)):
        ly.box("charcoal", badge_m @ C.M((0, -0.006 - i * 0.0062, 0.0017), scale=(bw, 0.0028, 0.0008)))
    ly.box("fire", badge_m @ C.M((0, 0.0175, 0.0016), scale=(0.031, 0.004, 0.0006)))
    ly.box("steel", badge_m @ C.M((0, 0.0215, 0.0002), scale=(0.008, 0.006, 0.003)), bevel=(0.0008, 1))
    ly.to_object(origin=(0, -0.058, 0.004), parent=body)

    # ------------------------------------------------------------------ clipboard in the right hand
    cb = C.MB("Clipboard", mat="bee", smooth_angle=40)
    cb_center = (-0.070, -0.084, -0.052)
    add_clipboard(cb, C.M(cb_center, (65, 0, -15)), w=0.058, h=0.076, detail=False)
    cb.to_object(origin=cb_center, parent=body)

    C.empty("HandR", hand_r, body, size=0.02)
    C.empty("HandL", hand_l, body, size=0.02)
    C.empty("CaptionAnchor", (0, 0, 0.27), root, size=0.03)
    _ = (torso, head, eyes, mouth)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="body center (chest)", hero_az=-28)
