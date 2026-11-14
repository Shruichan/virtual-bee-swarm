"""Portal frame: the meadow window. A flat-top hexagonal honeycomb ring (~0.8 m wide) on two feet, honeycomb cells
on its face, honey dripping from the top, a flower sprig on one corner. Pivot = base. Front = Unity +Z.

Hierarchy (names match the Unity placeholder / PLAN S11.8):
  PortalFrame
    Frame, LivePane (in front, hidden in previews), FallbackPane (same size, 4 mm behind), Glow (soft halo behind),
    Embers (empty at the pane center; its +Z points out of the window toward the player)
Pane UVs: the hexagon shows the centered 65% of a 16:9 frame (u 0.175..0.825, v 0..1), no stretching.
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "portal_frame"
BUDGET = 3000

R_OUT, R_IN, DEPTH = 0.40, 0.33, 0.05  # flat-top hexagon circumradii (width = 2 * R_OUT)
FEET_H = 0.03
CZ = R_OUT * math.sqrt(3) / 2 + FEET_H  # window center height
VIDEO_ASPECT = 16 / 9


def hex_pts(r):
    return C.circle_pts(r, 6, 0.0)  # flat-top


def build():
    root = C.empty("PortalFrame", size=0.15)
    face = C.M((0, 0, CZ), (90, 0, 0))  # local XY = the window plane, local +Z = toward the viewer (-Y)

    f = C.MB("Frame", smooth_angle=40)
    f.ring("amber", hex_pts(R_OUT), hex_pts(R_IN), DEPTH, face, bevel=(0.012, 2))
    # honeycomb cells along the six sides
    rmid = (R_OUT + R_IN) / 2
    cells = 0
    for side in range(6):
        a0, a1 = math.radians(60 * side), math.radians(60 * (side + 1))
        p0 = C.Vector((rmid * math.cos(a0), rmid * math.sin(a0)))
        p1 = C.Vector((rmid * math.cos(a1), rmid * math.sin(a1)))
        for k in range(5):
            t = (k + 0.5) / 5
            p = p0.lerp(p1, t)
            col = ("honey", "ember", "honey", "cream", "honey")[(k + side) % 5]
            f.hexprism(col, 0.0165, 0.008, face @ C.M((p.x, p.y, DEPTH / 2 + 0.002)), cap_back=False)
            cells += 1
    # honey drips over the top inner edge
    top_in = R_IN * math.sqrt(3) / 2
    for x, ln in ((-0.10, 0.05), (0.02, 0.032), (0.12, 0.065)):
        f.sphere("honey", face @ C.M((x, top_in - ln / 2 + 0.006, DEPTH / 2 + 0.003), scale=(0.009, ln / 2, 0.006)),
                 seg=8, rings=6)
        f.sphere("honey", face @ C.M((x, top_in - ln + 0.004, DEPTH / 2 + 0.004), scale=(0.012, 0.012, 0.008)), seg=8,
                 rings=6)
    # a flower sprig on the upper-left corner
    cx, cy = R_OUT * math.cos(math.radians(120)), R_OUT * math.sin(math.radians(120))
    for ang, ln in ((60, 0.07), (150, 0.06), (100, 0.08)):
        a = math.radians(ang)
        f.sphere("leaf", face @ C.M((cx + math.cos(a) * ln * 0.5, cy + math.sin(a) * ln * 0.5, DEPTH / 2 + 0.006),
                                    (0, 0, ang), (ln * 0.5, 0.016, 0.006)), seg=8, rings=5)
    fc = (cx + 0.02, cy + 0.035)
    for i in range(6):
        a = 2 * math.pi * i / 6
        f.sphere("purple", face @ C.M((fc[0] + 0.016 * math.cos(a), fc[1] + 0.016 * math.sin(a), DEPTH / 2 + 0.012),
                                      (0, 0, math.degrees(a)), (0.014, 0.008, 0.004)), seg=8, rings=5)
    f.sphere("honey", face @ C.M((fc[0], fc[1], DEPTH / 2 + 0.015), scale=(0.009, 0.009, 0.005)), seg=8, rings=5)
    # feet
    for s in (1, -1):
        f.box("honey_dark", C.M((0.13 * s, 0, FEET_H / 2 + 0.002), scale=(0.09, 0.16, FEET_H + 0.004)),
              bevel=(0.006, 2))
    f.to_object(origin=(0, 0, 0), parent=root)

    # panes (real UVs for the video), same size
    rp = R_IN + 0.006
    for name, y, mat, hide in (("LivePane", -0.004, "pane_live", True), ("FallbackPane", 0.0, "pane_fallback", False)):
        p = C.MB(name, mat=mat)
        p.flat("white", hex_pts(rp), C.M((0, y, CZ), (90, 0, 0)))
        ob = p.to_object(origin=(0, y, CZ), parent=root, keep_uv=True, preview_hide=hide)
        me = ob.data
        uv = me.uv_layers.new(name="UVMap")
        hgt = math.sqrt(3) * rp
        for poly in me.polygons:
            for li in poly.loop_indices:
                co = me.vertices[me.loops[li].vertex_index].co
                uv.data[li].uv = (0.5 + co.x / (VIDEO_ASPECT * hgt), 0.5 + co.z / hgt)

    g = C.MB("Glow", mat="glow_soft")
    g.flat("honey", hex_pts(R_OUT + 0.07), C.M((0, 0.035, CZ), (90, 0, 0)))
    g.to_object(origin=(0, 0.035, CZ), parent=root, preview_hide=True)
    C.empty("Embers", (0, -0.03, CZ), root, size=0.05)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom center of the feet)", hero_az=-22)
