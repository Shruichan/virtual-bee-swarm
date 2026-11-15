"""Environment art (art pass 2, 2026-11-14): the meadow island the hive stands on, its Moon twin for T-0, vines and
light shafts for the window ring, a star dome, an Earth glow, the island's flowers and a petal for particles.

  blender -b --factory-startup --python art/blender/scripts/environment.py -- --out DIR [--previews]

Writes DIR/environment.fbx (every piece is a top-level mesh, pivot at its origin, Unity Y-up / +Z front exactly like
common.py's art) and DIR/environment_layout.json (where the island's flowers stand), which
Assets/VirtualBeeSwarm/Environment/Editor/EnvironmentKitBuilder.cs turns into the runtime kit.

Colours are LINEAR float vertex colours with the light baked in (soft top-front key + sky fill, like export_dart.py),
so the game's unlit vertex-colour material shows them as is; alpha is the glow strength of the additive pieces.
Palette: Daniel's warm honey / cedar / sage / teal from "Ten New Assets.blend" plus the meadow greens of the window.

Spaces (Blender Z up, -Y = toward the player, +X = the player's right):
  Island, MoonGround, Bloom_*  the hive's centre at the origin (Unity puts them under TableAnchor at the hive's home)
  PortalVines, Shafts          portal_frame.py's space (Unity puts them under the Portal art, identity transform)
  StarsA/B                     a dome around the origin (Unity centres it on the player's head)
  EarthGlow                    unit size: inner radius 0.5 = the Earth's radius when scaled to its diameter
  Petal                        one 2.4 cm petal, pivot at its base (mesh particles)
"""
import argparse
import json
import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector, noise

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import common as C  # noqa: E402

LIGHT = Vector((0.35, -0.55, 0.76)).normalized()  # from the front (-Y), above, a little right (export_dart.py)

# island footprint (hive centre = origin); Unity anchor-local (x, z) = Blender (x, y) + hive home
ISLAND_RX, ISLAND_RY, ISLAND_CY = 0.37, 0.335, 0.012
FLAT_Z = 0.0012          # the turf where props stand: 1.2 mm (contact shadows draw at 2 mm)
MOON_FLAT_Z = 0.0017     # the Moon ground covers the turf while it grows over it
# places where something stands on the table: no mounds, no decorations (x, y, radius)
KEEP_OUT = [
    (0.0, 0.0, 0.0),        # (the hive itself: handled by HIVE_HALF below)
    (0.31, 0.05, 0.095),    # HONEYMOON-1
    (0.24, -0.19, 0.085),   # its crash spot
    (-0.20, -0.08, 0.075),  # smoker spot
    (0.20, -0.10, 0.10),    # pebble dish spot
]
HIVE_HALF = (0.17, 0.215)

# portal_frame.py
P_ROUT, P_RIN, P_DEPTH, P_FEET = 0.40, 0.33, 0.05, 0.03
P_CZ = P_ROUT * math.sqrt(3) / 2 + P_FEET


def srgb(h):
    h = h.lstrip("#")
    return tuple(C.srgb_to_lin(int(h[i:i + 2], 16) / 255.0) for i in (0, 2, 4))


COL = {k: srgb(v) for k, v in dict(
    grass1="#6DB552", grass2="#5FA548", grass3="#7FC05C", grass4="#58A04F", grass5="#8BC963",
    grass_dark="#3F7A45", grass_tip="#A6D977", teal="#39776C",
    clover="#4E9A45", clover_light="#79BE62", clover_flower="#FFF6EC", clover_pink="#F3B7C8",
    soil="#7A5233", stone="#C2B6A4", stone_dark="#978B7B",
    honey="#F0BB58", gold="#DEA43E", cell="#F6CD74", wax="#F4D68A", cedar="#BC8656",
    white="#FFFFFF", cream="#FFF3D6", yellow="#FFD23F", orange="#F8A432",
    purple="#8E6BD8", lavender="#B9A2F2", pink="#FF8FAB", pink_deep="#E2628C", rose="#FFC2D1",
    blue="#5B8DEF", blue_light="#9DBBFF", sun_center="#6B3E1E",
    vine="#4C8A44", vine_dark="#3A6E37", leaf="#5FA84B", leaf_light="#8CCB66",
    moon="#B9B7B1", moon_dark="#7C7B7E", moon_light="#D9D7D1", moon_deep="#626166",
    star="#FFFFFF", star_warm="#FFF1C9", star_blue="#CFE0FF", shaft="#FFE7A3",
    earth_glow="#7FC4FF", earth_glow2="#4C8DFF",
).items()}


def mul(c, s):
    return (c[0] * s, c[1] * s, c[2] * s)


def mix(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


# ------------------------------------------------------------------------------------------------ mesh accumulator
class Acc:
    """One output mesh: vertices + faces, a linear RGBA colour per face corner."""

    def __init__(self, name):
        self.name = name
        self.v = []
        self.f = []  # (indices, [rgba per corner])

    def vert(self, p):
        self.v.append(Vector(p))
        return len(self.v) - 1

    def normal(self, idx):
        n = Vector((0, 0, 0))
        for i in range(len(idx)):
            a, b = self.v[idx[i]], self.v[idx[(i + 1) % len(idx)]]
            n.x += (a.y - b.y) * (a.z + b.z)
            n.y += (a.z - b.z) * (a.x + b.x)
            n.z += (a.x - b.x) * (a.y + b.y)
        return n.normalized() if n.length > 1e-12 else Vector((0, 0, 1))

    def face(self, idx, col, lit=True, ao=None, alpha=1.0, cols=None, light=1.0):
        """col: linear rgb for the whole face (or cols: one rgba per corner, used as is). ao: a multiplier per corner."""
        idx = list(idx)
        if cols is None:
            s = 1.0
            if lit:
                n = self.normal(idx)
                lam = max(0.0, n.dot(LIGHT))
                s = (0.50 + 0.40 * lam + 0.16 * (0.5 + 0.5 * n.z)) * light
            cols = []
            for k in range(len(idx)):
                a = ao[k] if ao is not None else 1.0
                cols.append((min(1.0, col[0] * s * a), min(1.0, col[1] * s * a), min(1.0, col[2] * s * a), alpha))
        self.f.append((idx, cols))

    def double(self, idx, col, **kw):
        self.face(idx, col, **kw)
        kw2 = dict(kw)
        if "ao" in kw2 and kw2["ao"] is not None:
            kw2["ao"] = list(reversed(kw2["ao"]))
        if "cols" in kw2 and kw2["cols"] is not None:
            kw2["cols"] = list(reversed(kw2["cols"]))
        self.face(list(reversed(list(idx))), col, **kw2)

    def tris(self):
        return sum(len(i) - 2 for i, _ in self.f)

    def to_object(self):
        me = bpy.data.meshes.new(self.name)
        me.from_pydata([tuple(p) for p in self.v], [], [tuple(i) for i, _ in self.f])
        me.update()
        attr = me.color_attributes.new("Col", "FLOAT_COLOR", "CORNER")
        data = []
        # from_pydata keeps the face order and each face's corner order
        for poly, (_, cols) in zip(me.polygons, self.f):
            assert poly.loop_total == len(cols)
        for _, cols in self.f:
            for c in cols:
                data.extend(c)
        attr.data.foreach_set("color", data)
        me.color_attributes.active_color = attr
        try:
            me.color_attributes.render_color_index = 0
        except Exception:
            pass
        ob = bpy.data.objects.new(self.name, me)
        bpy.context.scene.collection.objects.link(ob)
        C.REGISTRY.append(ob)
        return ob


# ------------------------------------------------------------------------------------------------ primitives
def ico_points(subdiv):
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=subdiv, radius=1.0)
    bm.verts.ensure_lookup_table()
    pts = [v.co.copy() for v in bm.verts]
    faces = [[v.index for v in f.verts] for f in bm.faces]
    bm.free()
    return pts, faces


ICO = {}


def octa(acc, center, radii, col, rot=0.0, lit=True):
    """8-triangle diamond (cheap blossom/bud)."""
    R = Matrix.Rotation(rot, 3, "Z")
    c = Vector(center)
    px = [acc.vert(c + R @ Vector(v)) for v in ((radii[0], 0, 0), (0, radii[1], 0), (-radii[0], 0, 0), (0, -radii[1], 0))]
    top = acc.vert(c + Vector((0, 0, radii[2])))
    bot = acc.vert(c - Vector((0, 0, radii[2])))
    for k in range(4):
        a, b = px[k], px[(k + 1) % 4]
        acc.face([a, b, top], col, lit=lit)
        acc.face([b, a, bot], col, lit=lit, ao=[0.8, 0.8, 0.7])


def ico(acc, center, radii, col_fn, subdiv=1, jitter=0.0, rnd=None, ao_fn=None, lit=True, alpha=1.0, rot=0.0):
    if subdiv not in ICO:
        ICO[subdiv] = ico_points(subdiv)
    pts, faces = ICO[subdiv]
    R = Matrix.Rotation(rot, 3, "Z")
    base = len(acc.v)
    for p in pts:
        q = Vector((p.x * radii[0], p.y * radii[1], p.z * radii[2]))
        if jitter and rnd:
            q *= 1.0 + rnd.uniform(-jitter, jitter)
        acc.vert(Vector(center) + R @ q)
    for f in faces:
        idx = [base + i for i in f]
        cen = sum((acc.v[i] for i in idx), Vector()) / len(idx)
        col = col_fn(cen - Vector(center)) if callable(col_fn) else col_fn
        ao = [ao_fn(acc.v[i]) for i in idx] if ao_fn else None
        acc.face(idx, col, ao=ao, lit=lit, alpha=alpha)


def prism(acc, pts2d, z0, z1, top_col, side_col, cx=0.0, cy=0.0, bottom=False, ao_side=(0.75, 1.0)):
    n = len(pts2d)
    lo = [acc.vert((cx + x, cy + y, z0)) for x, y in pts2d]
    hi = [acc.vert((cx + x, cy + y, z1)) for x, y in pts2d]
    acc.face(hi, top_col)
    if bottom:
        acc.face(list(reversed(lo)), side_col)
    for i in range(n):
        j = (i + 1) % n
        acc.face([lo[i], lo[j], hi[j], hi[i]], side_col, ao=[ao_side[0], ao_side[0], ao_side[1], ao_side[1]])


def frame_of(d):
    """Two unit vectors perpendicular to d."""
    d = Vector(d).normalized()
    up = Vector((0, 0, 1)) if abs(d.z) < 0.95 else Vector((0, 1, 0))
    a = d.cross(up).normalized()
    b = a.cross(d).normalized()
    return a, b


def tube(acc, path, radii, col, seg=6, col_end=None, cap=True):
    """A tube along path (list of points), radius per point; colour fades to col_end along it."""
    rings = []
    n = len(path)
    prev_a = None
    for i, p in enumerate(path):
        d = (path[min(i + 1, n - 1)] - path[max(i - 1, 0)])
        if d.length < 1e-9:
            d = Vector((0, 0, 1))
        a, b = frame_of(d)
        if prev_a is not None and a.dot(prev_a) < 0:
            a, b = -a, -b
        prev_a = a
        r = radii[i]
        rings.append([acc.vert(p + (a * math.cos(2 * math.pi * k / seg) + b * math.sin(2 * math.pi * k / seg)) * r)
                      for k in range(seg)])
    for i in range(n - 1):
        t = i / max(1, n - 2)
        c = mix(col, col_end, t) if col_end else col
        for k in range(seg):
            k2 = (k + 1) % seg
            acc.face([rings[i][k], rings[i + 1][k], rings[i + 1][k2], rings[i][k2]], c)
    if cap:
        tip = acc.vert(path[-1] + (path[-1] - path[-2]).normalized() * radii[-1])
        c = col_end or col
        for k in range(seg):
            acc.face([rings[-1][k], tip, rings[-1][(k + 1) % seg]], c)


def leaf(acc, base, direction, normal, length, width, col, tip_col=None, double=True, fold=0.25):
    """A pointed leaf: base -> tip along direction, its face toward normal, folded along the vein."""
    d = Vector(direction).normalized()
    nrm = Vector(normal)
    nrm = (nrm - d * nrm.dot(d)).normalized()
    side = d.cross(nrm).normalized()
    base = Vector(base)
    tip = base + d * length
    pts = []
    for t, w in ((0.3, 0.85), (0.62, 1.0)):
        c = base + d * (length * t)
        pts.append((c + side * (width * 0.5 * w) - nrm * (width * fold * 0.5 * w),
                    c - side * (width * 0.5 * w) - nrm * (width * fold * 0.5 * w), c))
    vb = acc.vert(base)
    vt = acc.vert(tip)
    l1, r1, m1 = (acc.vert(p) for p in pts[0])
    l2, r2, m2 = (acc.vert(p) for p in pts[1])
    tc = tip_col or col
    ao_b, ao_t = 0.8, 1.0
    faces = [([vb, l1, m1], [ao_b, 0.95, 0.9]), ([vb, m1, r1], [ao_b, 0.9, 0.95]),
             ([m1, l1, l2, m2], [0.9, 0.95, 1.0, 0.95]), ([m1, m2, r2, r1], [0.9, 0.95, 1.0, 0.95]),
             ([m2, l2, vt], [0.95, 1.0, 1.05]), ([m2, vt, r2], [0.95, 1.05, 1.0])]
    for k, (idx, ao) in enumerate(faces):
        c = col if k < 2 else mix(col, tc, 0.5 if k < 4 else 1.0)
        if double:
            acc.double(idx, c, ao=ao)
        else:
            acc.face(idx, c, ao=ao)


def petal(acc, base, direction, normal, length, width, col, tip_col=None, cup=0.2):
    """A cheap two-sided petal: base, two cupped sides, tip (4 tris)."""
    d = Vector(direction).normalized()
    nrm = Vector(normal)
    nrm = (nrm - d * nrm.dot(d)).normalized()
    side = d.cross(nrm).normalized()
    base = Vector(base)
    m = base + d * (length * 0.55) + nrm * (width * cup)
    vb, vt = acc.vert(base), acc.vert(base + d * length)
    vl, vr = acc.vert(m + side * (width * 0.5)), acc.vert(m - side * (width * 0.5))
    tc = tip_col or col
    acc.double([vb, vl, vt], None, cols=_pc([col, mix(col, tc, 0.5), tc], acc, [vb, vl, vt]))
    acc.double([vb, vt, vr], None, cols=_pc([col, tc, mix(col, tc, 0.5)], acc, [vb, vt, vr]))


def _pc(cols, acc, idx):
    n = acc.normal(idx)
    s = 0.62 + 0.3 * max(0.0, n.dot(LIGHT)) + 0.12 * (0.5 + 0.5 * n.z)
    s = max(s, 0.62 + 0.3 * max(0.0, -n.dot(LIGHT)) + 0.12 * (0.5 - 0.5 * n.z))  # both sides lit alike
    out = []
    for k, c in enumerate(cols):
        a = 0.86 if k == 0 else 1.0
        out.append((min(1, c[0] * s * a), min(1, c[1] * s * a), min(1, c[2] * s * a), 1.0))
    return out


def petal_ring(acc, center, normal, n, length, width, col, tip_col=None, cup=0.25, rot=0.0, double=True):
    """n petals around center, opening toward normal (cupped up by `cup`)."""
    nrm = Vector(normal).normalized()
    a, b = frame_of(nrm)
    for i in range(n):
        ang = rot + 2 * math.pi * i / n
        d = (a * math.cos(ang) + b * math.sin(ang)) * (1 - cup) + nrm * cup
        petal(acc, Vector(center), d, nrm, length, width, col, tip_col)


def disc_fan(acc, center, normal, r, n, col, ao_center=1.05, ao_edge=0.85, lift=0.0):
    nrm = Vector(normal).normalized()
    a, b = frame_of(nrm)
    c = acc.vert(Vector(center) + nrm * lift)
    ring = [acc.vert(Vector(center) + (a * math.cos(2 * math.pi * k / n) + b * math.sin(2 * math.pi * k / n)) * r)
            for k in range(n)]
    for k in range(n):
        acc.face([c, ring[(k + 1) % n], ring[k]], col, ao=[ao_center, ao_edge, ao_edge])


# ------------------------------------------------------------------------------------------------ island
def outline_r(theta, rx=ISLAND_RX, ry=ISLAND_RY, seed=0.0):
    """Lumpy ellipse radius at angle theta (radians, 0 = +X)."""
    base = 1.0 / math.sqrt((math.cos(theta) / rx) ** 2 + (math.sin(theta) / ry) ** 2)
    w = 1.0 + 0.035 * math.sin(3 * theta + 1.3 + seed) + 0.025 * math.sin(5 * theta + 0.4 + seed) \
        + 0.018 * math.sin(9 * theta + 2.2 + seed) + 0.01 * math.sin(14 * theta + seed)
    return base * w


def keep_out_weight(x, y, extra=0.0):
    """1 where something stands (no mound, no decoration), 0 far from it."""
    w = 0.0
    hx, hy = HIVE_HALF
    dx, dy = max(0.0, abs(x) - hx), max(0.0, abs(y - 0.0) - hy)
    d = math.hypot(dx, dy)
    w = max(w, 1.0 - min(1.0, d / (0.05 + extra)))
    for (kx, ky, kr) in KEEP_OUT[1:]:
        d = math.hypot(x - kx, y - ky)
        if d < kr + extra:
            w = max(w, 1.0)
        elif d < kr + extra + 0.05:
            w = max(w, 1.0 - (d - kr - extra) / 0.05)
    # the honeycomb path in front of the door
    if abs(x) < 0.06 and -0.36 < y < -0.2:
        w = max(w, 1.0)
    return w


def blocked(x, y, margin=0.0):
    return keep_out_weight(x, y, margin) > 0.5


def turf_height(t, theta, x, y, flat=FLAT_Z, mound_scale=1.0):
    if t <= 0.74:
        return flat
    u = min(1.0, (t - 0.74) / 0.26)
    prof = math.sin(math.pi * min(1.0, u * 1.08)) ** 0.9 if u < 0.925 else 0.0
    m = 0.0035 + 0.0045 * max(0.0, math.sin(2 * theta + 0.7)) + 0.004 * max(0.0, math.sin(theta)) \
        + 0.003 * max(0.0, noise.noise(Vector((math.cos(theta) * 3, math.sin(theta) * 3, 0.7))))
    m *= mound_scale * (1.0 - keep_out_weight(x, y))
    edge = flat * (1.0 - u)
    return edge + m * prof


def build_ground(name, flat, palette_fn, rx, ry, seed, rings=(0, 0.16, 0.32, 0.47, 0.6, 0.71, 0.8, 0.87, 0.93, 0.975, 1.0),
                 seg=44, mound_scale=1.0, height_fn=None):
    acc = Acc(name)
    rnd = random.Random(seed)
    grid = []
    for ti, t in enumerate(rings):
        row = []
        for s in range(seg if t > 0 else 1):
            th = 2 * math.pi * s / seg
            r = outline_r(th, rx, ry, seed) * t
            x, y = r * math.cos(th), r * math.sin(th) + ISLAND_CY
            if 0 < t < 1:
                x += rnd.uniform(-0.006, 0.006) * t
                y += rnd.uniform(-0.006, 0.006) * t
            z = height_fn(t, th, x, y) if height_fn else turf_height(t, th, x, y, flat, mound_scale)
            row.append((acc.vert((x, y, z)), t))
        grid.append(row)

    def ao_of(t):
        return 1.03 - 0.10 * t * t - (0.22 * (t - 0.93) / 0.07 if t > 0.93 else 0.0)

    # centre fan
    c, _ = grid[0][0]
    for s in range(seg):
        a, ta = grid[1][s]
        b, tb = grid[1][(s + 1) % seg]
        cen = (acc.v[c] + acc.v[a] + acc.v[b]) / 3
        acc.face([c, a, b], palette_fn(cen, 0.08, rnd), ao=[ao_of(0), ao_of(ta), ao_of(tb)])
    for ti in range(1, len(rings) - 1):
        r0, r1 = grid[ti], grid[ti + 1]
        for s in range(seg):
            a0, _ = r0[s]
            a1, _ = r0[(s + 1) % seg]
            b0, _ = r1[s]
            b1, _ = r1[(s + 1) % seg]
            if (s + ti) % 2:
                tris = ([a0, b0, a1], [a1, b0, b1])
            else:
                tris = ([a0, b0, b1], [a0, b1, a1])
            tc = (rings[ti] + rings[ti + 1]) / 2
            for tri in tris:
                cen = sum((acc.v[i] for i in tri), Vector()) / 3
                aos = [ao_of(rings[ti]) if i in (a0, a1) else ao_of(rings[ti + 1]) for i in tri]
                acc.face(tri, palette_fn(cen, tc, rnd), ao=aos)
    return acc


def turf_color(cen, t, rnd):
    n = noise.noise(Vector((cen.x * 9.0, cen.y * 9.0, 0.3)))
    keys = ["grass1", "grass2", "grass3", "grass4", "grass5"]
    k = keys[int((n * 0.5 + 0.5) * 4.99 + rnd.uniform(-0.6, 0.6)) % 5]
    col = COL[k]
    if t > 0.955:
        col = mix(col, COL["grass_dark"], 0.55)
    return col


def build_island(layout):
    acc = build_ground("Island", FLAT_Z, turf_color, ISLAND_RX, ISLAND_RY, 3)
    rnd = random.Random(11)

    def ground_z(x, y):
        th = math.atan2(y - ISLAND_CY, x)
        t = math.hypot(x, y - ISLAND_CY) / outline_r(th, seed=3)
        return turf_height(min(1.0, t), th, x, y), t

    # --- fringe: grass blades along the outline (3-sided, leaning out), sparse blades on the rim band
    n_fringe = 150
    for i in range(n_fringe):
        th = 2 * math.pi * (i + rnd.uniform(-0.35, 0.35)) / n_fringe
        t = rnd.uniform(0.95, 1.02)
        r = outline_r(th, seed=3) * t
        x, y = r * math.cos(th), r * math.sin(th) + ISLAND_CY
        if blocked(x, y, -0.03):
            continue
        blade(acc, rnd, x, y, max(0.0, ground_z(x, y)[0]) if t < 1 else 0.0, th, rnd.uniform(0.012, 0.026), out=0.55)
    for i in range(70):
        th = rnd.uniform(0, 2 * math.pi)
        t = rnd.uniform(0.76, 0.94)
        r = outline_r(th, seed=3) * t
        x, y = r * math.cos(th), r * math.sin(th) + ISLAND_CY
        if blocked(x, y, 0.01):
            continue
        blade(acc, rnd, x, y, ground_z(x, y)[0], th, rnd.uniform(0.014, 0.03), out=0.25)

    # --- clover clumps on the rim band
    clumps = []
    tries = 0
    while len(clumps) < 9 and tries < 400:
        tries += 1
        th = rnd.uniform(0, 2 * math.pi)
        t = rnd.uniform(0.78, 0.9)
        r = outline_r(th, seed=3) * t
        x, y = r * math.cos(th), r * math.sin(th) + ISLAND_CY
        if blocked(x, y, 0.025) or any(math.hypot(x - a, y - b) < 0.09 for a, b in clumps):
            continue
        clumps.append((x, y))
    for (cx, cy) in clumps:
        for k in range(rnd.randint(6, 9)):
            x, y = cx + rnd.uniform(-0.035, 0.035), cy + rnd.uniform(-0.028, 0.028)
            z = ground_z(x, y)[0]
            trefoil(acc, rnd, Vector((x, y, z)), rnd.uniform(0.006, 0.014), rnd.uniform(0.010, 0.014))
        if rnd.random() < 0.8:
            x, y = cx + rnd.uniform(-0.02, 0.02), cy + rnd.uniform(-0.02, 0.02)
            clover_flower(acc, rnd, Vector((x, y, ground_z(x, y)[0])), rnd.uniform(0.018, 0.028))

    # --- pebbles
    placed = 0
    tries = 0
    while placed < 6 and tries < 300:
        tries += 1
        th = rnd.uniform(0, 2 * math.pi)
        t = rnd.uniform(0.8, 0.97)
        r = outline_r(th, seed=3) * t
        x, y = r * math.cos(th), r * math.sin(th) + ISLAND_CY
        if blocked(x, y, 0.01):
            continue
        s = rnd.uniform(0.007, 0.013)
        z = ground_z(x, y)[0]

        def stone_col(d, rr=rnd.random()):
            return COL["stone"] if d.z > -0.2 * s and rr > 0.3 else COL["stone_dark"]
        ico(acc, (x, y, z + s * 0.25), (s * 1.25, s, s * 0.6), stone_col, subdiv=1, jitter=0.12, rnd=rnd,
            rot=rnd.uniform(0, 3))
        placed += 1

    # --- the honeycomb path from the door toward the player (Daniel's comb golds)
    hexes = [(0.0, -0.238), (0.024, -0.268), (-0.006, -0.298), (0.016, -0.33)]
    for i, (x, y) in enumerate(hexes):
        r = 0.0165 - 0.0012 * i
        pts = C.circle_pts(r, 6, 0.0)
        top = COL["honey"] if i % 2 == 0 else COL["cell"]
        prism(acc, pts, FLAT_Z - 0.001, FLAT_Z + 0.0026, top, COL["gold"], cx=x, cy=y)
        inner = C.circle_pts(r * 0.62, 6, 0.0)
        prism(acc, inner, FLAT_Z + 0.0026, FLAT_Z + 0.0034, COL["wax"], COL["gold"], cx=x, cy=y)

    # --- flower spots (instanced blooms in Unity), in little clusters: regular ones + full-bloom extras
    spots = []
    kinds = ["Lupine", "Daisy", "Cosmos", "Cornflower", "Daisy", "Cosmos", "Lupine", "Cornflower"]
    centres = []
    tries = 0
    while len(centres) < 9 and tries < 3000:
        tries += 1
        th = rnd.uniform(0, 2 * math.pi)
        t = rnd.uniform(0.8, 0.92)
        r = outline_r(th, seed=3) * t
        x, y = r * math.cos(th), r * math.sin(th) + ISLAND_CY
        if blocked(x, y, 0.035) or any(math.hypot(x - a, y - b) < 0.13 for a, b, _ in centres):
            continue
        centres.append((x, y, th))
    for ci, (cx, cy, th) in enumerate(centres):
        front = cy < -0.12 and abs(cx) < 0.22  # in front of the hive: short flowers only (keep the door visible)
        n = 3 if ci % 3 else 2
        for j in range(n):
            for _ in range(40):
                x, y = cx + rnd.uniform(-0.03, 0.03), cy + rnd.uniform(-0.03, 0.03)
                if not blocked(x, y, 0.02) and all(math.hypot(x - s_["x"], y - s_["y"]) > 0.028 for s_ in spots):
                    break
            else:
                continue
            kind = kinds[(ci * 2 + j) % len(kinds)]
            if front and kind == "Lupine":
                kind = "Daisy"
            extra = j == n - 1 and ci % 2 == 0
            if extra and not front:
                kind = "Sunflower" if ci % 4 == 0 else kind
            spots.append(dict(kind=kind, x=round(x, 4), y=round(y, 4), z=round(ground_z(x, y)[0], 4),
                              yaw=round(rnd.uniform(-35, 35), 1), scale=round(rnd.uniform(0.85, 1.15), 3),
                              extra=extra))
    layout["blooms"] = spots
    layout["island"] = dict(rx=ISLAND_RX, ry=ISLAND_RY, cy=ISLAND_CY, flat_z=FLAT_Z, moon_flat_z=MOON_FLAT_Z)
    return acc


def blade(acc, rnd, x, y, z, th, h, out=0.4):
    """A 3-sided grass blade leaning outward (radially) by `out`."""
    w = rnd.uniform(0.0022, 0.0035)
    lean = Vector((math.cos(th) * out + rnd.uniform(-0.25, 0.25), math.sin(th) * out + rnd.uniform(-0.25, 0.25), 1.0))
    lean.normalize()
    tip = Vector((x, y, z)) + lean * h
    base = [acc.vert((x + w * math.cos(a), y + w * math.sin(a), z - 0.0005))
            for a in (rnd.uniform(0, 1), rnd.uniform(0, 1) + 2.1, rnd.uniform(0, 1) + 4.2)]
    vt = acc.vert(tip)
    gcol = COL[rnd.choice(["grass1", "grass3", "grass5", "grass2"])]
    tipc = mix(gcol, COL["grass_tip"], 0.6)
    for k in range(3):
        a, b = base[k], base[(k + 1) % 3]
        n = acc.normal([a, b, vt])
        lam = max(0.0, n.dot(LIGHT))
        s = 0.5 + 0.4 * lam + 0.16 * (0.5 + 0.5 * n.z)
        dark = mul(mix(gcol, COL["teal"], 0.35), 0.7 * s)
        acc.face([a, b, vt], None, cols=[dark + (1.0,), dark + (1.0,), mul(tipc, s * 1.05) + (1.0,)])


def trefoil(acc, rnd, base, height, size):
    top = base + Vector((rnd.uniform(-0.002, 0.002), rnd.uniform(-0.002, 0.002), height))
    tube(acc, [base, top], [0.0009, 0.0007], COL["clover"], seg=3, cap=False)
    rot = rnd.uniform(0, 2 * math.pi)
    for i in range(3):
        a = rot + 2 * math.pi * i / 3
        d = Vector((math.cos(a), math.sin(a), 0.25)).normalized()
        heart(acc, top, d, size, COL["clover"], COL["clover_light"])


def heart(acc, base, d, size, col, col2):
    """A clover leaflet: a rounded heart fan seen from above (one-sided, faces up)."""
    side = Vector((0, 0, 1)).cross(d).normalized()
    outline = [(0.0, 0.0), (0.3, -0.38), (0.68, -0.5), (0.98, -0.3), (0.86, 0.0), (0.98, 0.3), (0.68, 0.5), (0.3, 0.38)]
    cen = acc.vert(base + d * (size * 0.52) + Vector((0, 0, size * 0.12)))
    ring = [acc.vert(base + d * (u * size) + side * (v * size) + Vector((0, 0, size * 0.05 * u))) for u, v in outline]
    n = len(ring)
    for k in range(n):
        tri = [cen, ring[k], ring[(k + 1) % n]]
        if acc.normal(tri).z < 0:
            tri = [cen, ring[(k + 1) % n], ring[k]]
        acc.face(tri, col2 if k in (2, 3, 4, 5) else col, ao=[1.05, 0.85 if k == 0 else 1.0, 0.85 if (k + 1) % n == 0 else 1.0])


def clover_flower(acc, rnd, base, h):
    top = base + Vector((rnd.uniform(-0.003, 0.003), rnd.uniform(-0.003, 0.003), h))
    tube(acc, [base, top], [0.0009, 0.0008], COL["clover"], seg=3, cap=False)
    s = rnd.uniform(0.0055, 0.0075)

    def fc(d):
        return COL["clover_flower"] if d.z > -0.3 * s else COL["clover_pink"]
    ico(acc, top + Vector((0, 0, s * 0.6)), (s, s, s * 0.9), fc, subdiv=1, jitter=0.1, rnd=rnd)


# ------------------------------------------------------------------------------------------------ blooms
def stem(acc, h, bend=(0.0, 0.0), r=0.0022, col=None):
    pts = [Vector((bend[0] * (k / 4) ** 2, bend[1] * (k / 4) ** 2, h * k / 4)) for k in range(5)]
    tube(acc, pts, [r, r * 0.95, r * 0.9, r * 0.85, r * 0.8], col or COL["vine"], seg=4, col_end=COL["leaf"],
         cap=False)
    return pts[-1]


def basal_leaves(acc, n, length, rnd, col=None):
    for i in range(n):
        a = 2 * math.pi * i / n + rnd.uniform(-0.4, 0.4)
        d = Vector((math.cos(a), math.sin(a), 0.75)).normalized()
        petal(acc, Vector((0, 0, 0.004)), d, Vector((0, 0, 1)), length, length * 0.38, col or COL["leaf"],
              COL["leaf_light"], cup=0.15)


def bloom_daisy():
    acc = Acc("Bloom_Daisy")
    rnd = random.Random(21)
    head = stem(acc, 0.05, (0.006, 0.0))
    basal_leaves(acc, 3, 0.032, rnd)
    nrm = Vector((0.15, -0.45, 1.0)).normalized()  # faces up and a bit toward the player
    petal_ring(acc, head, nrm, 11, 0.024, 0.0085, COL["white"], COL["cream"], cup=0.18)
    disc_fan(acc, head + nrm * 0.0025, nrm, 0.009, 8, COL["yellow"], lift=0.004)
    return acc


def bloom_cosmos():
    acc = Acc("Bloom_Cosmos")
    rnd = random.Random(22)
    head = stem(acc, 0.064, (-0.008, 0.004))
    basal_leaves(acc, 3, 0.03, rnd)
    nrm = Vector((-0.1, -0.5, 1.0)).normalized()
    petal_ring(acc, head, nrm, 8, 0.028, 0.016, COL["pink"], COL["rose"], cup=0.25)
    disc_fan(acc, head + nrm * 0.002, nrm, 0.008, 8, COL["orange"], lift=0.004)
    return acc


def bloom_cornflower():
    acc = Acc("Bloom_Cornflower")
    rnd = random.Random(23)
    head = stem(acc, 0.058, (0.004, -0.004))
    basal_leaves(acc, 2, 0.03, rnd)
    nrm = Vector((0.0, -0.3, 1.0)).normalized()
    for ring_i, (n, ln, cup, col) in enumerate(((9, 0.015, 0.35, COL["blue"]), (6, 0.011, 0.65, COL["blue_light"]))):
        a, b = frame_of(nrm)
        for i in range(n):
            ang = 2 * math.pi * (i + 0.5 * ring_i) / n
            d = (a * math.cos(ang) + b * math.sin(ang)) * (1 - cup) + nrm * cup
            petal(acc, head, d, nrm, ln * 1.45, 0.008, col, COL["blue_light"], cup=0.3)
    octa(acc, head + nrm * 0.004, (0.0055, 0.0055, 0.005), COL["purple"])
    return acc


def bloom_lupine():
    acc = Acc("Bloom_Lupine")
    h = 0.095
    stem(acc, h, (0.0, 0.0), r=0.0026)
    for i in range(3):  # palmate basal leaves
        a = 2 * math.pi * i / 3 + 0.4
        c = Vector((math.cos(a) * 0.01, math.sin(a) * 0.01, 0.012))
        for j in range(4):
            aa = a + (j - 1.5) * 0.5
            d = Vector((math.cos(aa), math.sin(aa), 0.35)).normalized()
            petal(acc, c, d, Vector((0, 0, 1)), 0.018, 0.0055, COL["leaf"], COL["leaf_light"], cup=0.1)
    tiers = 8
    for k in range(tiers):
        t = k / (tiers - 1)
        z = 0.048 + t * (h - 0.044)
        rr = 0.011 * (1 - 0.55 * t)
        col = mix(COL["purple"], COL["lavender"], t)
        for j in range(3):
            a = 2 * math.pi * j / 3 + k * 1.0
            p = Vector((math.cos(a) * rr, math.sin(a) * rr, z))
            s_ = 0.0066 * (1 - 0.45 * t)
            octa(acc, p, (s_, s_ * 0.85, s_ * 0.8), col, rot=a)
    octa(acc, Vector((0, 0, h + 0.004)), (0.0028, 0.0028, 0.0045), COL["lavender"])
    return acc


def bloom_sunflower():
    acc = Acc("Bloom_Sunflower")
    rnd = random.Random(25)
    head = stem(acc, 0.1, (0.0, -0.01), r=0.003)
    for zz, a in ((0.04, 0.3), (0.07, 2.6)):
        d = Vector((math.cos(a), math.sin(a), 0.4)).normalized()
        leaf(acc, Vector((0, -0.01 * (zz / 0.12) ** 2, zz)), d, Vector((0, 0, 1)), 0.03, 0.02, COL["leaf"],
             COL["leaf_light"])
    nrm = Vector((0.0, -1.0, 0.75)).normalized()  # turned to the player
    petal_ring(acc, head, nrm, 12, 0.03, 0.012, COL["yellow"], COL["orange"], cup=0.12)
    disc_fan(acc, head + nrm * 0.002, nrm, 0.017, 10, COL["sun_center"], lift=0.004, ao_center=1.2)
    return acc


# ------------------------------------------------------------------------------------------------ moon ground
CRATERS = [  # x, y, radius, rim height (on the rim band, away from what stands on the table)
    (-0.27, 0.16, 0.055, 0.007), (0.06, 0.29, 0.045, 0.006), (-0.31, -0.12, 0.04, 0.005),
    (0.33, -0.05, 0.03, 0.004), (-0.12, 0.3, 0.03, 0.004), (0.2, 0.25, 0.035, 0.005),
]
DECAL_CRATERS = [(-0.08, -0.16, 0.035), (0.12, 0.16, 0.03), (-0.12, 0.05, 0.025), (0.06, -0.06, 0.02)]


def moon_height(t, th, x, y):
    z = turf_height(t, th, x, y, MOON_FLAT_Z, 0.7)
    for cx, cy, cr, ch in CRATERS:
        d = math.hypot(x - cx, y - cy) / cr
        w = 1.0 - keep_out_weight(x, y)
        if d < 1.0:
            z += w * (-0.6 * ch * (1 - d * d) + ch * d ** 4)
        elif d < 1.6:
            z += w * ch * (1 - (d - 1.0) / 0.6) ** 2
    return max(MOON_FLAT_Z * (1.0 - t) if t >= 1 else MOON_FLAT_Z * 0.5, z)


def moon_color(cen, t, rnd):
    n = noise.noise(Vector((cen.x * 4.0, cen.y * 4.0, 1.7)))
    col = COL["moon"] if n > -0.25 else COL["moon_light"]
    if n > 0.35:
        col = mix(COL["moon"], COL["moon_dark"], 0.35)
    if rnd.random() < 0.12:
        col = mix(col, COL["moon_light"], 0.6)
    if t > 0.955:
        col = mix(col, COL["moon_dark"], 0.45)
    return col


def crater(acc, x, y, z, r, h, rnd):
    """A raised rim ring (lit outside, shadowed inside) around a dark bowl."""
    n = 12
    rot = rnd.uniform(0, 1)
    outer = [acc.vert((x + r * 1.45 * math.cos(2 * math.pi * (k + rot) / n), y + r * 1.45 * math.sin(2 * math.pi * (k + rot) / n), z - 0.0004)) for k in range(n)]
    top = [acc.vert((x + r * 1.08 * math.cos(2 * math.pi * (k + rot) / n), y + r * 1.08 * math.sin(2 * math.pi * (k + rot) / n), z + h * rnd.uniform(0.8, 1.2))) for k in range(n)]
    inner = [acc.vert((x + r * 0.78 * math.cos(2 * math.pi * (k + rot) / n), y + r * 0.78 * math.sin(2 * math.pi * (k + rot) / n), z + h * 0.15)) for k in range(n)]
    c = acc.vert((x, y, z + h * 0.05))
    for k in range(n):
        k2 = (k + 1) % n
        acc.face([outer[k], outer[k2], top[k2], top[k]], COL["moon_light"], ao=[0.85, 0.85, 1.05, 1.05])
        acc.face([top[k], top[k2], inner[k2], inner[k]], COL["moon_dark"], ao=[1.0, 1.0, 0.8, 0.8])
        acc.face([inner[k], inner[k2], c], COL["moon_deep"], ao=[0.9, 0.9, 0.75])


def build_moon_ground():
    acc = build_ground("MoonGround", MOON_FLAT_Z, moon_color, ISLAND_RX * 1.05, ISLAND_RY * 1.05, 7,
                       rings=(0, 0.16, 0.32, 0.47, 0.6, 0.71, 0.8, 0.87, 0.93, 0.975, 1.0), seg=44,
                       height_fn=lambda t, th, x, y: turf_height(t, th, x, y, MOON_FLAT_Z, 0.8))
    rnd = random.Random(31)
    for cx, cy, cr, ch in CRATERS:
        crater(acc, cx, cy, MOON_FLAT_Z + 0.002, cr * 0.75, ch * 1.3, rnd)
    for cx, cy, cr in DECAL_CRATERS:  # flat ones where things stand: a dark bowl and a light rim, 0.4 mm proud
        crater(acc, cx, cy, MOON_FLAT_Z + 0.0002, cr * 0.8, 0.0004, rnd)
    placed, tries = 0, 0
    while placed < 12 and tries < 400:
        tries += 1
        th = rnd.uniform(0, 2 * math.pi)
        t = rnd.uniform(0.82, 1.0)
        r = outline_r(th, ISLAND_RX * 1.05, ISLAND_RY * 1.05, 7) * t
        x, y = r * math.cos(th), r * math.sin(th) + ISLAND_CY
        if blocked(x, y, 0.0):
            continue
        s = rnd.uniform(0.01, 0.022)

        def rc(d, rr=rnd.random()):
            return COL["moon_light"] if d.z > 0 else COL["moon_dark"]
        ico(acc, (x, y, s * 0.2), (s * 1.2, s, s * 0.7), rc, subdiv=1, jitter=0.15, rnd=rnd, rot=rnd.uniform(0, 3))
        placed += 1
    return acc


# ------------------------------------------------------------------------------------------------ stars, glow, petal
def build_stars(name, n, seed, size_range, bright):
    acc = Acc(name)
    rnd = random.Random(seed)
    placed = 0
    golden = math.pi * (3 - math.sqrt(5))
    i = 0
    while placed < n and i < n * 6:
        i += 1
        z = rnd.uniform(math.sin(math.radians(4)), math.sin(math.radians(80)))
        a = rnd.uniform(0, 2 * math.pi) if not bright else (i * golden) % (2 * math.pi)
        rr = math.sqrt(1 - z * z)
        d = Vector((rr * math.cos(a), rr * math.sin(a), z))
        R = rnd.uniform(2.1, 2.8)
        p = d * R
        s = rnd.uniform(*size_range)
        col = COL[rnd.choice(["star", "star", "star_warm", "star_blue"])]
        side, up = frame_of(-d)
        c = acc.vert(p)
        if bright:  # 4-point sparkle, bright centre fading to the tips
            pts = []
            for k in range(8):
                ang = math.pi * k / 4
                L = s if k % 2 == 0 else s * 0.28
                pts.append(acc.vert(p + (side * math.cos(ang) + up * math.sin(ang)) * L))
            for k in range(8):
                acc.face([c, pts[k], pts[(k + 1) % 8]], None,
                         cols=[col + (1.0,), mul(col, 0.6) + (0.0,), mul(col, 0.6) + (0.0,)])
        else:  # small diamond
            pts = [acc.vert(p + (side * math.cos(math.pi * k / 2) + up * math.sin(math.pi * k / 2)) * s)
                   for k in range(4)]
            for k in range(4):
                acc.face([c, pts[k], pts[(k + 1) % 4]], None,
                         cols=[col + (0.95,), mul(col, 0.5) + (0.15,), mul(col, 0.5) + (0.15,)])
        placed += 1
    return acc


def build_earth_glow():
    """Soft additive halo around a unit-diameter Earth (radius 0.5), in the XZ plane, both sides."""
    acc = Acc("EarthGlow")
    n = 40
    radii = [(0.40, COL["earth_glow"], 0.0), (0.5, COL["earth_glow"], 0.95), (0.6, COL["earth_glow2"], 0.45),
             (0.78, COL["earth_glow2"], 0.12), (1.0, COL["earth_glow2"], 0.0)]
    rings = []
    for r, col, al in radii:
        rings.append([(acc.vert((r * math.cos(2 * math.pi * k / n), 0.0, r * math.sin(2 * math.pi * k / n))),
                       col + (al,)) for k in range(n)])
    for i in range(len(rings) - 1):
        for k in range(n):
            k2 = (k + 1) % n
            a, ca = rings[i][k]
            b, cb = rings[i][k2]
            c, cc = rings[i + 1][k2]
            d, cd = rings[i + 1][k]
            acc.face([a, b, c, d], None, cols=[ca, cb, cc, cd])
    return acc


def build_petal():
    acc = Acc("Petal")
    leaf(acc, Vector((0, 0, 0)), Vector((0, -1, 0.15)), Vector((0, 0, 1)), 0.024, 0.014, COL["pink"], COL["rose"],
         double=True, fold=-0.3)
    return acc


# ------------------------------------------------------------------------------------------------ portal vines + shafts
def hex_r(a, R):
    """Flat-top hexagon boundary radius at angle a (radians)."""
    m = (a % (math.pi / 3)) - math.pi / 6
    return R * math.cos(math.pi / 6) / math.cos(m)


def portal_point(a, r, y):
    return Vector((r * math.cos(a), y, P_CZ + r * math.sin(a)))


def build_vines():
    acc = Acc("PortalVines")
    rnd = random.Random(41)
    front = -P_DEPTH / 2 - 0.012  # just in front of the frame's face (its honeycomb cells stand 1 cm proud)
    rmid = (P_ROUT + P_RIN) / 2
    vines = [  # (start angle, end angle, phase): degrees, CCW seen from the front, 0 = right
        (258.0, 112.0, 0.0),
        (284.0, 395.0, 1.7),
        (205.0, 150.0, 3.1),  # a short second strand around the left corner
    ]
    flower_cols = [("pink", "rose"), ("white", "cream"), ("purple", "lavender"), ("yellow", "orange"),
                   ("pink", "rose"), ("blue", "blue_light")]
    fi = 0
    for vi, (a0, a1, ph) in enumerate(vines):
        steps = max(8, int(abs(a1 - a0) / 2.6))
        path = []
        for k in range(steps + 1):
            t = k / steps
            a = math.radians(a0 + (a1 - a0) * t)
            wob = 0.022 * math.sin(t * 15.0 + ph) + 0.008 * math.sin(t * 37.0 + ph * 2)
            r = hex_r(a, rmid) + wob
            y = front - 0.004 * (0.5 + 0.5 * math.sin(t * 15.0 + ph + 1.2))
            path.append(portal_point(a, r, y))
        r0 = 0.013 if vi < 2 else 0.009
        radii = [r0 * (1.0 - 0.6 * (k / steps)) for k in range(steps + 1)]
        tube(acc, path, radii, COL["vine_dark"], seg=5, col_end=COL["vine"])
        # leaves along it, alternating sides, growing toward the tip
        k = 2
        side = 1
        while k < steps - 1:
            p = path[k]
            d = (path[k + 1] - path[k - 1]).normalized()
            outward = Vector((p.x, 0.0, p.z - P_CZ)).normalized()
            ld = (d * 0.35 + outward * side * 0.9 + Vector((0, -0.35, 0))).normalized()
            size = 0.036 + 0.016 * rnd.random()
            leaf(acc, p, ld, Vector((0, -1, 0)), size, size * 0.5, COL["leaf"], COL["leaf_light"], fold=0.3)
            if rnd.random() < 0.45 and k + 2 < steps:
                fc, ft = flower_cols[fi % len(flower_cols)]
                fi += 1
                fp = p + Vector((0, -0.01, 0)) + outward * (-side) * 0.016
                nrm = Vector((rnd.uniform(-0.3, 0.3), -1.0, rnd.uniform(-0.2, 0.3))).normalized()
                petal_ring(acc, fp, nrm, 5, 0.024, 0.017, COL[fc], COL[ft], cup=0.2, rot=rnd.uniform(0, 3))
                disc_fan(acc, fp + nrm * 0.003, nrm, 0.0065, 6, COL["yellow"], lift=0.003)
            side = -side
            k += rnd.randint(2, 3)
        # a curl at the tip
        p, d = path[-1], (path[-1] - path[-2]).normalized()
        curl = []
        for j in range(10):
            ang = j * 0.6
            rr = 0.012 * (1 - j / 12)
            n2 = Vector((0, -1, 0)).cross(d).normalized()
            curl.append(p + d * (math.sin(ang) * rr) + n2 * ((1 - math.cos(ang)) * rr) + d * 0.004 * j)
        tube(acc, curl, [radii[-1] * (1 - j / 12) for j in range(10)], COL["vine"], seg=4)
    # two hanging tendrils with a bud from the bottom corners
    for a, ln in ((math.radians(238), 0.07), (math.radians(302), 0.055)):
        p0 = portal_point(a, hex_r(a, P_ROUT) - 0.01, front)
        pts = [p0 + Vector((0.008 * math.sin(j * 1.3), -0.003 * j / 6, -ln * j / 6)) for j in range(7)]
        tube(acc, pts, [0.0035 * (1 - j / 8) for j in range(7)], COL["vine"], seg=4)
        petal_ring(acc, pts[-1], Vector((0, -0.4, -1)), 5, 0.018, 0.013, COL["pink"], COL["rose"], cup=0.45)
    return acc


def build_shafts():
    """Four soft god rays from the pane, out of the window and down toward the table (additive, alpha = strength)."""
    acc = Acc("Shafts")
    rnd = random.Random(51)
    starts = [(-0.13, 0.07), (0.02, 0.13), (0.15, 0.03), (-0.03, -0.06)]
    col = COL["shaft"]
    for i, (sx, sz) in enumerate(starts):
        yaw = math.radians(rnd.uniform(-10, 10) + (sx * 40))
        pitch = math.radians(rnd.uniform(26, 36))
        d = Vector((math.sin(yaw) * math.cos(pitch), -math.cos(yaw) * math.cos(pitch), -math.sin(pitch)))
        o = Vector((sx, -0.01, P_CZ + sz))
        L = rnd.uniform(0.75, 1.0)
        w0, w1 = 0.05 + 0.02 * rnd.random(), 0.15 + 0.05 * rnd.random()
        peak = 0.2 + 0.08 * rnd.random()
        a, b = frame_of(d)
        for axis in (a, b):  # two crossed ribbons
            rows = [(0.0, 0.0, w0), (0.12, peak, w0 + (w1 - w0) * 0.12), (0.55, peak * 0.55, w0 + (w1 - w0) * 0.55),
                    (1.0, 0.0, w1)]
            grid = []
            for t, al, w in rows:
                c = o + d * (L * t)
                grid.append([(acc.vert(c - axis * (w * 0.5)), 0.0), (acc.vert(c), al), (acc.vert(c + axis * (w * 0.5)), 0.0)])
            for r in range(len(rows) - 1):
                for j in range(2):
                    v00, a00 = grid[r][j]
                    v01, a01 = grid[r][j + 1]
                    v11, a11 = grid[r + 1][j + 1]
                    v10, a10 = grid[r + 1][j]
                    acc.face([v00, v01, v11, v10], None,
                               cols=[col + (a00,), col + (a01,), col + (a11,), col + (a10,)])
    return acc


# ------------------------------------------------------------------------------------------------ preview + export
def preview(path, objs, cam_loc, cam_look, lens=50, size=(1200, 800), ortho=None):
    sc = bpy.context.scene
    sc.render.engine = "BLENDER_WORKBENCH"
    sh = sc.display.shading
    sh.light = "FLAT"
    sh.color_type = "VERTEX"
    sh.background_type = "VIEWPORT"
    sh.background_color = (0.62, 0.5, 0.38)  # a wooden table-ish backdrop
    sc.view_settings.view_transform = "Standard"
    sc.render.resolution_x, sc.render.resolution_y = size
    sc.render.film_transparent = False
    for ob in bpy.data.objects:
        ob.hide_render = ob not in objs and ob.type == "MESH"
    cam = bpy.data.objects.get("PreviewCam")
    if cam is None:
        cam = bpy.data.objects.new("PreviewCam", bpy.data.cameras.new("PreviewCam"))
        sc.collection.objects.link(cam)
    cam.data.lens = lens
    cam.data.clip_start = 0.01
    if ortho:
        cam.data.type = "ORTHO"
        cam.data.ortho_scale = ortho
    else:
        cam.data.type = "PERSP"
    cam.location = Vector(cam_loc)
    cam.rotation_euler = (Vector(cam_look) - Vector(cam_loc)).to_track_quat("-Z", "Y").to_euler()
    sc.camera = cam
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--previews", action="store_true")
    a = p.parse_args(argv)
    out = os.path.abspath(a.out)
    os.makedirs(out, exist_ok=True)
    C.reset()
    layout = {}
    accs = [build_island(layout), bloom_daisy(), bloom_cosmos(), bloom_cornflower(), bloom_lupine(), bloom_sunflower(),
            build_moon_ground(), build_stars("StarsA", 70, 61, (0.03, 0.05), True),
            build_stars("StarsB", 170, 62, (0.009, 0.016), False), build_earth_glow(), build_petal(), build_vines(),
            build_shafts()]
    stats = {}
    objs = {}
    for acc in accs:
        objs[acc.name] = acc.to_object()
        stats[acc.name] = acc.tris()
        print("[environment] %-18s %5d tris" % (acc.name, acc.tris()), flush=True)
    layout["tris"] = stats
    with open(os.path.join(out, "environment_layout.json"), "w") as fh:
        json.dump(layout, fh, indent=1)

    if a.previews:
        pv = os.path.join(out, "previews")
        os.makedirs(pv, exist_ok=True)
        # island with its blooms placed (copies for the preview only)
        tmp = []
        for s in layout["blooms"]:
            src = objs["Bloom_" + s["kind"]]
            ob = src.copy()
            bpy.context.scene.collection.objects.link(ob)
            ob.location = (s["x"], s["y"], s["z"])
            ob.rotation_euler = (0, 0, math.radians(-s["yaw"]))
            ob.scale = (s["scale"],) * 3
            tmp.append(ob)
        isl = [objs["Island"]] + tmp
        preview(os.path.join(pv, "island_pov.png"), isl, (0.0, -0.95, 0.62), (0.0, 0.05, 0.0), lens=40)
        preview(os.path.join(pv, "island_top.png"), isl, (0.0, 0.0, 1.2), (0.0, 0.0, 0.0), ortho=0.9)
        preview(os.path.join(pv, "island_low.png"), isl, (0.45, -0.55, 0.12), (0.0, 0.05, 0.02), lens=35)
        preview(os.path.join(pv, "moon_pov.png"), [objs["MoonGround"]], (0.0, -0.95, 0.62), (0.0, 0.05, 0.0), lens=40)
        preview(os.path.join(pv, "vines.png"), [objs["PortalVines"]], (0.0, -1.6, P_CZ), (0.0, 0.0, P_CZ), lens=50)
        preview(os.path.join(pv, "blooms.png"), [objs[n] for n in ("Bloom_Daisy", "Bloom_Cosmos", "Bloom_Cornflower",
                                                                   "Bloom_Lupine", "Bloom_Sunflower")],
                (0.0, -0.35, 0.12), (0.0, 0.0, 0.05), lens=50)
        for ob in tmp:
            bpy.data.objects.remove(ob, do_unlink=True)
        # spread the blooms out for the sheet: put them back where they belong afterwards
    # export (pre-converted to Y-up like common.export; every node unrotated, unscaled)
    for ob in bpy.data.objects:
        if ob.type == "CAMERA":
            bpy.data.objects.remove(ob, do_unlink=True)
    C._fbx_space(True)
    try:
        bpy.ops.export_scene.fbx(
            filepath=os.path.join(out, "environment.fbx"), use_selection=False, object_types={"MESH"},
            global_scale=1.0, apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z",
            axis_up="Y", bake_space_transform=False, use_mesh_modifiers=True, mesh_smooth_type="OFF",
            colors_type="LINEAR", prioritize_active_color=True, use_tspace=False, add_leaf_bones=False,
            bake_anim=False, path_mode="STRIP", embed_textures=False, use_custom_props=False)
    finally:
        C._fbx_space(False)
    print("[environment] DONE %d meshes, %d tris -> %s" % (len(accs), sum(stats.values()), out), flush=True)


if __name__ == "__main__":
    main()
