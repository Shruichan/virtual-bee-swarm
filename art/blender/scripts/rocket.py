"""HONEYMOON-1: Beeswax's cartoon rocket (~0.6 m). Cream hull with a honeycomb band, amber nose with a honey-drop
tip, porthole, name painted on the front, four fire-orange fins, a countdown panel. Pivot = base (fins on the table).

Hierarchy (names match the Unity placeholder / PLAN S11.9):
  HONEYMOON-1
    Hull, Flame (pivot = nozzle exit, hidden in previews), Seat, Perch, CountdownDisplay (empties)
"""
import math
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "rocket"
BUDGET = 3000

PROFILE = [(0, 0.062), (0.05, 0.062), (0.066, 0.075), (0.079, 0.105), (0.086, 0.15), (0.088, 0.22), (0.086, 0.30),
           (0.080, 0.36), (0.068, 0.42), (0.050, 0.47), (0.030, 0.51), (0.012, 0.535), (0, 0.540)]
NOSE_Z = 0.43
PORTHOLE_Z = 0.355
NAME_LINES = (("HONEY", 0.296), ("MOON-1", 0.266))
HEX_ROWS = (0.168, 0.190, 0.212)
FIN_ANGLES = (45, 135, 225, 315)  # degrees from the front


def r_at(z):
    for (r0, z0), (r1, z1) in zip(PROFILE, PROFILE[1:]):
        if z0 <= z <= z1 and z1 > z0:
            return r0 + (r1 - r0) * (z - z0) / (z1 - z0)
    return 0.0


def hull_color(c, n):
    if c.z > NOSE_Z:
        return "amber"
    if c.z < 0.085:
        return "charcoal"
    if 0.398 < c.z < 0.414 or 0.128 < c.z < 0.142:
        return "fire"
    return "cream"


def build():
    root = C.empty("HONEYMOON-1", size=0.1)
    h = C.MB("Hull", smooth_angle=40)
    h.lathe(hull_color, PROFILE, seg=20)
    # honey-drop nose tip
    h.sphere("honey", C.M((0, 0, 0.547), scale=(0.017, 0.017, 0.019)), seg=10, rings=6)
    h.cyl("honey", 0.006, 0.022, 6, C.M((0, 0, 0.560)))
    h.sphere("honey", C.M((0, 0, 0.585), scale=0.010), seg=8, rings=5)

    # honeycomb band: flat hex cells hugging the hull
    rnd = random.Random(4)
    n_around = 24
    for row, z in enumerate(HEX_ROWS):
        rr = r_at(z) + 0.0012
        for k in range(n_around):
            phi = 2 * math.pi * (k + 0.5 * (row % 2)) / n_around
            d = C.Vector((math.sin(phi), -math.cos(phi), 0.0))
            m = C.frame(d * rr + C.Vector((0, 0, z)), d, (0, 0, 1))
            h.flat("honey" if rnd.random() < 0.6 else "amber", C.circle_pts(0.0108, 6, 90), m)

    # name, two lines, wrapped round the front
    for text, z in NAME_LINES:
        wrap = C.cyl_wrap(r_at(z) + 0.0008, 0.0)
        h.text("charcoal", text, 0.027, 0.0, lfn=lambda v, z=z, wrap=wrap: wrap(C.Vector((v.x, v.y + z, v.z))),
               resolution=2)

    # porthole
    pr = r_at(PORTHOLE_Z)
    pc = (0, -pr - 0.002, PORTHOLE_Z)
    h.torus("steel", 0.033, 0.0075, 16, 6, C.frame(pc, (0, -1, 0)))
    h.flat("sky", C.circle_pts(0.031, 14), C.frame((0, -pr - 0.0012, PORTHOLE_Z), (0, -1, 0)))
    h.flat("white", C.rounded_rect(0.006, 0.026, 0.002, 1), C.frame((0.008, -pr - 0.0022, PORTHOLE_Z + 0.004),
                                                                  (0, -1, 0)) @ C.M(rot=(0, 0, -35)))

    # fins
    fin = [(0.0, 0.20), (0.035, 0.13), (0.072, 0.035), (0.078, 0.0), (0.040, 0.0), (0.0, 0.07)]
    for deg in FIN_ANGLES:
        phi = math.radians(deg)
        d = C.Vector((math.sin(phi), -math.cos(phi), 0.0))
        t = C.Vector((math.cos(phi), math.sin(phi), 0.0))
        m = C.frame(d * 0.066, -t, (0, 0, 1))
        h.poly(lambda c, n: "charcoal" if math.hypot(c.x, c.y) > 0.128 else "fire", fin, 0.016, m,
               bevel=(0.003, 1))

    # nozzle bell
    h.lathe("steel_dark", [(0.048, 0.032), (0.046, 0.036), (0.036, 0.05), (0.030, 0.063)], seg=16,
            cap_bottom=False, cap_top=False)
    h.flat("charcoal", C.circle_pts(0.036, 16), C.frame((0, 0, 0.046), (0, 0, -1)))

    # countdown panel
    pz = 0.108
    prr = r_at(pz)
    pm = C.frame((0, -prr - 0.003, pz), (0, -1, 0))
    h.poly("honey", C.rounded_rect(0.066, 0.036, 0.006, 2), 0.012, pm, bevel=(0.0015, 1))
    h.flat("charcoal", C.rounded_rect(0.056, 0.026, 0.004, 2), pm @ C.M((0, 0, 0.0062)))
    h.to_object(origin=(0, 0, 0), parent=root)

    # flame (glow, hidden in previews; show it on launch)
    fl = C.MB("Flame", mat="glow", smooth_angle=60)
    fz = 0.032
    fl.lathe(lambda c, n: "ember" if c.z > fz - 0.04 else ("honey" if c.z > fz - 0.1 else "fire"),
             [(0, fz - 0.17), (0.018, fz - 0.11), (0.038, fz - 0.04), (0.042, fz - 0.005), (0.03, fz + 0.012),
              (0, fz + 0.018)], seg=12)
    fl.to_object(origin=(0, 0, fz), parent=root, preview_hide=True)

    C.empty("Seat", (0, -0.035, 0.33), root, size=0.03)
    C.empty("Perch", (0, 0, 0.60), root, size=0.03)
    C.empty("CountdownDisplay", (0, -prr - 0.0095, pz), root, size=0.02)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom of the fins)", hero_az=-25)
