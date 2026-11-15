"""Bloom kit: the low-poly bee flowers, grass and vine pieces that cover the real room at the ESCAPE.

    blender -b --factory-startup --python art/blender/scripts/bloom_flowers.py -- --out DIR [--no-render]

Needs Blender 5.3: the grass tuft reuses Daniel's "Grass Tuft - Proposed" blades from "Virtual Bee Swarm Art.blend"
(file version 503); without the .blend it builds blades in the same style. The flowers follow his "Flowers - Proposed"
look (pastel heads, gold centres, thin stems, flat pointed leaves) at ~1/8 of the triangles, so thousands can be drawn.

Writes DIR/bloom_<item>.fbx (one mesh each), DIR/bloom_kit.blend, DIR/manifest.json, DIR/contact.png.

Every mesh is one object with one material "VC" (Unity: Art/_Shared/VBS_DartVC), zero rotation, unit scale, and
  * colour = LINEAR vertex colour with a soft sky light baked in (like export_dart.py), for VirtualBeeSwarm/Unlit
    with _UseVertexColor = 1; no textures, no UVs
  * vertex colour ALPHA = sway weight, 0 at the base and 1 at the tip (plants: (height / top) ^ 1.6; vine leaf:
    distance from the pivot / length; blossom: distance from its centre / radius). It is not opacity
  * flowers and grass: Unity Y-up, pivot at the stem base on the floor (stems reach 6 mm below it)
  * vine leaf and blossom: the front faces Unity +Z (out of the wall), the leaf tip points +Y, pivot where it joins
    the vine: place with Quaternion.LookRotation(wallNormal, alongTheVine)
  * flat parts (leaves, petals, blades) carry their own back faces 0.3 mm behind, so Cull Back (or Off) is fine
Axes as in common.py: built Z-up with the front at -Y; the FBX lands Y-up with the front on Unity +Z.
"""
import argparse
import json
import math
import os
import random
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
ART_BLEND = os.path.normpath(os.path.join(HERE, "..", "Virtual Bee Swarm Art.blend"))
Z = Vector((0.0, 0.0, 1.0))
BUDGET = {"flower": 120, "grass": 60, "leaf": 8, "blossom": 30}

# sRGB. First block: Daniel's meadow materials in the .blend ("Flowers - Proposed", "Grass Tuft - Proposed").
COLORS = {
    "stem": "#4D7852",        # Grass shadow
    "leaf": "#659554",        # Leaf green
    "spring": "#86BF6C",      # Spring green
    "meadow": "#529955",      # Meadow green
    "blade": "#6CAF61",       # Leaf green.001
    "pollen": "#F7AA36",      # Pollen
    "honey": "#FFBF43",       # Honey gold
    "lilac": "#AA91CE",       # Soft lilac
    # added for the bloom
    "lilac_dark": "#8A6CC4",
    "sage": "#7FA08A",        # grey-green foliage: lavender, sage, poppy
    "salvia": "#6C62D6",
    "salvia_light": "#9C94EE",
    "phacelia": "#8496E6",
    "phacelia_light": "#B3BDF6",
    "clover": "#D98CC0",
    "clover_light": "#F0B8DA",
    "clover_dark": "#B26A9E",
    "poppy": "#F98E1D",
    "poppy_light": "#FFB637",
    "sun_petal": "#FFC83A",
    "disk": "#6E4426",
    "leaf_back": "#8CB87A",
    "vine_leaf": "#4F8E46",
    "blossom": "#C9A2EE",
    "blossom_back": "#A986D6",
}


def log(*a):
    print("[bloom]", *a, flush=True)


def hex_rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4))


def srgb_to_lin(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def lin(key):
    return tuple(srgb_to_lin(c) for c in hex_rgb(COLORS[key]))


# ------------------------------------------------------------------------------------------------ baked light
SIDE = Vector((0.45, -0.75, 0.0)).normalized()     # weak, so a randomly turned instance still looks right
WALL_LIGHT = Vector((0.3, -0.8, 0.5)).normalized()  # vine pieces: from the room, above, a little right


def shade_plant(n, z):
    s = 0.70 + 0.24 * n.z + 0.12 * n.dot(SIDE)
    ao = 0.68 + 0.32 * min(1.0, max(0.0, z / 0.10))  # darker where it meets the floor
    return max(0.3, min(1.0, s)) * ao


def shade_wall(n, z):
    s = 0.60 + 0.40 * max(0.0, n.dot(WALL_LIGHT)) + 0.06 * n.z
    return max(0.35, min(1.0, s))


# ------------------------------------------------------------------------------------------------ mesh building
class Part:
    """One output mesh: vertices, faces with a colour key each. kind: flower | grass | leaf | blossom."""

    def __init__(self, name, kind):
        self.name, self.kind = name, kind
        self.v = []
        self.f = []
        self.landing = None  # where a bee lands (Blender space), flowers only

    def add(self, p):
        self.v.append(Vector(p))
        return len(self.v) - 1

    def face(self, idx, key):
        if key not in COLORS:
            raise KeyError(key)
        self.f.append((tuple(idx), key))

    def tri_out(self, a, b, c, key, away_from):
        """Triangle a, b, c wound to face away from the point away_from."""
        va, vb, vc = self.v[a], self.v[b], self.v[c]
        n = (vb - va).cross(vc - va)
        if n.dot((va + vb + vc) / 3 - Vector(away_from)) < 0:
            b, c = c, b
        self.face((a, b, c), key)

    def sheet(self, pts, tris, key, back_key=None, eps=0.0003):
        """Open surface with its own back faces: tris index pts with the FRONT winding."""
        pts = [Vector(p) for p in pts]
        n = Vector()
        for a, b, c in tris:
            n += (pts[b] - pts[a]).cross(pts[c] - pts[a])
        n.normalize()
        front = [self.add(p) for p in pts]
        back = [self.add(p - n * eps) for p in pts]
        for a, b, c in tris:
            self.face((front[a], front[b], front[c]), key)
            self.face((back[a], back[c], back[b]), back_key or key)

    def tris(self):
        return sum(len(f) - 2 for f, _ in self.f)


def frame_m(origin, x_dir, z_hint):
    """Matrix with local X along x_dir and local Z (a leaf's front) as close to z_hint as possible."""
    x = Vector(x_dir).normalized()
    z = Vector(z_hint)
    z = (z - x * z.dot(x)).normalized()
    y = z.cross(x)
    o = Vector(origin)
    return Matrix(((x.x, y.x, z.x, o.x), (x.y, y.y, z.y, o.y), (x.z, y.z, z.z, o.z), (0, 0, 0, 1)))


def sph(az, el):
    """Unit vector: azimuth from +X toward +Y, elevation above the floor (degrees)."""
    a, e = math.radians(az), math.radians(el)
    return Vector((math.cos(e) * math.cos(a), math.cos(e) * math.sin(a), math.sin(e)))


def tube(part, pts, radii, sides, key, phase=0.0, twist=0.0, cap_start=False):
    """Tube along pts (parallel-transported rings); a radius of 0 is a pointed tip. key: str or f(segment)."""
    pts = [Vector(p) for p in pts]
    n = len(pts)
    tang = [(pts[min(n - 1, i + 1)] - pts[max(0, i - 1)]).normalized() for i in range(n)]
    ref = Vector((1, 0, 0)) if abs(tang[0].x) < 0.9 else Vector((0, 1, 0))
    u = (ref - tang[0] * ref.dot(tang[0])).normalized()
    rings = []
    for i in range(n):
        t = tang[i]
        u = (u - t * u.dot(t)).normalized()
        v = t.cross(u)
        if radii[i] <= 0:
            rings.append([part.add(pts[i])])
            continue
        ring = []
        for k in range(sides):
            a = 2 * math.pi * k / sides + phase + i * twist
            ring.append(part.add(pts[i] + radii[i] * (math.cos(a) * u + math.sin(a) * v)))
        rings.append(ring)

    def axis_pt(i, idx):
        g = sum((part.v[j] for j in idx), Vector()) / len(idx)
        a, b = pts[i], pts[i + 1]
        ab = b - a
        t = max(0.0, min(1.0, (g - a).dot(ab) / max(1e-12, ab.length_squared)))
        return a + ab * t

    for i in range(n - 1):
        A, B = rings[i], rings[i + 1]
        kk = key(i) if callable(key) else key
        if len(B) == 1:
            for k in range(sides):
                tri = (A[k], A[(k + 1) % sides], B[0])
                part.tri_out(*tri, kk, axis_pt(i, tri))
            continue
        for k in range(sides):
            k1 = (k + 1) % sides
            for tri in ((A[k], A[k1], B[k]), (A[k1], B[k1], B[k])):
                part.tri_out(*tri, kk, axis_pt(i, tri))
    if cap_start and len(rings[0]) > 2:
        r0 = rings[0]
        inside = pts[0] + tang[0] * 0.01
        kk = key(0) if callable(key) else key
        for k in range(1, sides - 1):
            part.tri_out(r0[0], r0[k], r0[k + 1], kk, inside)


def along(pts, z):
    """Point on the polyline pts at height z."""
    pts = [Vector(p) for p in pts]
    for a, b in zip(pts, pts[1:]):
        if a.z <= z <= b.z:
            t = (z - a.z) / max(1e-9, b.z - a.z)
            return a.lerp(b, t)
    return pts[-1].copy()


def _fan(outline):
    return [(0.0, 0.0, 0.0)] + [(x, y, 0.0) for x, y in outline], [(0, i, i + 1) for i in range(1, len(outline))]


# leaf outlines: x along the leaf (0 base .. 1 tip), y across (-0.5 .. 0.5), z toward the front (in leaf lengths)
LEAF_SHAPES = {
    "diamond": _fan([(0.45, -0.5), (1.0, 0.0), (0.45, 0.5)]),                                   # 2 tris
    "round": _fan([(0.28, -0.46), (0.72, -0.42), (1.0, 0.0), (0.72, 0.42), (0.28, 0.46)]),     # 4 tris
    "heart": _fan([(0.3, -0.3), (0.88, -0.5), (0.8, 0.0), (0.88, 0.5), (0.3, 0.3)]),           # 4 tris
    "vine": ([(0, 0, 0), (0.32, -0.5, 0), (1, 0, 0), (0.32, 0.5, 0), (0.4, 0, 0.13)],          # 4 tris, raised rib
             [(0, 1, 4), (4, 1, 2), (0, 4, 3), (4, 2, 3)]),
}


def leaf(part, m, length, width, key, back_key=None, shape="diamond", cup=0.25, droop=0.12):
    """A double-sided leaf/petal in frame m (x = along, z = front). cup lifts the edges, droop bends the tip back."""
    pts, tris = LEAF_SHAPES[shape]
    out = []
    for x, y, z in pts:
        zz = z * length + cup * abs(y) * width - droop * x * x * length
        out.append(m @ Vector((x * length, y * width, zz)))
    part.sheet(out, tris, key, back_key or key)


ICO_V = [(-1, 1.618, 0), (1, 1.618, 0), (-1, -1.618, 0), (1, -1.618, 0), (0, -1, 1.618), (0, 1, 1.618),
         (0, -1, -1.618), (0, 1, -1.618), (1.618, 0, -1), (1.618, 0, 1), (-1.618, 0, -1), (-1.618, 0, 1)]
ICO_F = [(0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6),
         (7, 1, 8), (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10),
         (8, 6, 7), (9, 8, 1)]


def ico(part, c, r, key, scale=(1, 1, 1), jitter=0.0, poke=0.0, seed=0):
    """Icosahedron ball (20 tris), or with poke > 0 every face raised to a point (60 tris).
    key(face_index, sub_index, outward_dir) -> colour key."""
    rnd = random.Random(seed)
    c = Vector(c)
    dirs = [Vector(p).normalized() * (1 + rnd.uniform(-jitter, jitter)) for p in ICO_V]

    def pos(d):
        return c + Vector((d.x * r * scale[0], d.y * r * scale[1], d.z * r * scale[2]))
    ids = [part.add(pos(d)) for d in dirs]
    for fi, (a, b, cc) in enumerate(ICO_F):
        g = (dirs[a] + dirs[b] + dirs[cc]) / 3
        if poke <= 0:
            part.tri_out(ids[a], ids[b], ids[cc], key(fi, 0, g.normalized()), c)
            continue
        tip = part.add(pos(g.normalized() * (g.length + poke)))
        for si, (x, y) in enumerate(((a, b), (b, cc), (cc, a))):
            part.tri_out(ids[x], ids[y], tip, key(fi, si, g.normalized()), c)


def curl_path(start, d, steps, phi0, dphi, l0, shrink):
    """Scorpioid curl (phacelia): walk from start, the heading turning from up (phi=0) over toward d and back."""
    pts = [Vector(start)]
    pos, phi, step = Vector(start), math.radians(phi0), l0
    for _ in range(steps):
        pos = pos + step * (math.sin(phi) * d + math.cos(phi) * Z)
        pts.append(pos.copy())
        phi += math.radians(dphi)
        step *= shrink
    return pts


# ------------------------------------------------------------------------------------------------ the items
STEM_SINK = -0.006


def build_lavender():
    """Lavender: three spikes of lilac whorls on straight stems, two narrow grey-green leaves."""
    p = Part("Bloom_Lavender", "flower")
    spikes = [((0.0, 0.0), 0.0, 0.0, 0.24, 0.095, 1.0),
              ((0.005, 0.004), 14.0, 40.0, 0.195, 0.08, 0.9),
              ((-0.005, 0.003), 16.0, 215.0, 0.175, 0.074, 0.85)]
    for (bx, by), lean, az, sl, kl, rs in spikes:
        d = sph(az, 90 - lean)
        b = Vector((bx, by, STEM_SINK))
        top = b + d * sl
        tube(p, [b, top], [0.0034, 0.0029], 3, "stem")
        ks = [top + d * (kl * t) for t in (0, 0.22, 0.44, 0.66, 0.86, 1.0)]
        rr = [0.0092, 0.0114, 0.01, 0.0106, 0.0078, 0.0]
        tube(p, ks, [r * rs for r in rr], 3, lambda i: "lilac" if i % 2 else "lilac_dark", twist=math.pi / 3,
             cap_start=True)
        if p.landing is None:
            p.landing = ks[-1] + d * 0.004
    leaf(p, frame_m((0.002, 0.0, 0.004), sph(100, 58), Z), 0.10, 0.012, "sage", shape="diamond", droop=0.25)
    leaf(p, frame_m((-0.002, 0.0, 0.006), sph(300, 52), Z), 0.085, 0.011, "sage", shape="diamond", droop=0.25)
    return p


def build_salvia():
    """Salvia (Cleveland sage): blue-violet pom-pom whorls stacked up a stem, two grey-green sage leaves."""
    p = Part("Bloom_Salvia", "flower")
    pts = [(0, 0, STEM_SINK), (0.004, 0, 0.13), (0.006, -0.003, 0.26), (0.006, -0.004, 0.36), (0.006, -0.004, 0.405)]
    tube(p, pts, [0.0045, 0.004, 0.0036, 0.003, 0.0], 3, "stem")

    def key(fi, si, d):
        return "salvia_light" if (fi % 3 == 0 or d.z > 0.75) else "salvia"
    for i, (z, r) in enumerate(((0.2, 0.03), (0.278, 0.026), (0.35, 0.02))):
        ico(p, along(pts, z), r, key, scale=(1, 1, 0.8), jitter=0.14, seed=7 + i)
    p.landing = along(pts, 0.35) + Vector((0, 0, 0.02 * 0.8))
    leaf(p, frame_m(along(pts, 0.05), sph(20, 28), Z), 0.075, 0.032, "sage", shape="round", droop=0.2)
    leaf(p, frame_m(along(pts, 0.095), sph(205, 32), Z), 0.068, 0.028, "sage", shape="round", droop=0.2)
    return p


def build_phacelia():
    """Phacelia ("bee's friend"): lavender-blue flowers on two curled fiddleneck sprays, two leaves."""
    p = Part("Bloom_Phacelia", "flower")
    stem = [(0, 0, STEM_SINK), (0.004, 0.002, 0.12), (0.01, 0.0, 0.22), (0.016, -0.002, 0.275)]
    tube(p, stem, [0.004, 0.0036, 0.0032, 0.0032], 3, "stem")

    def bumps(n, r0):
        return [r0 * (1.0 if i % 2 else 0.78) * (1 - 0.35 * i / n) for i in range(n)] + [0.0]

    def key(i):
        return "phacelia_light" if i % 2 else "phacelia"
    c1 = curl_path(stem[-1], Vector((1, 0.15, 0)).normalized(), 6, 15, 54, 0.028, 0.8)
    tube(p, c1, bumps(6, 0.0118), 3, key, twist=math.pi / 3)
    p.landing = max(c1, key=lambda v: v.z) + Vector((0, 0, 0.008))
    fork = along(stem, 0.2)
    b_end = fork + Vector((-0.02, 0.008, 0.045))
    tube(p, [fork, b_end], [0.003, 0.0028], 3, "stem")
    c2 = curl_path(b_end, Vector((-1, 0.35, 0)).normalized(), 5, 10, 58, 0.023, 0.8)
    tube(p, c2, bumps(5, 0.0102), 3, key, twist=math.pi / 3)
    leaf(p, frame_m(along(stem, 0.05), sph(250, 30), Z), 0.075, 0.026, "leaf", "leaf_back", shape="diamond")
    leaf(p, frame_m(along(stem, 0.11), sph(60, 35), Z), 0.065, 0.022, "leaf", "leaf_back", shape="diamond")
    return p


def build_clover():
    """Red clover: a pink pom-pom head of florets and one trefoil leaf on its own stalk."""
    p = Part("Bloom_Clover", "flower")
    tube(p, [(0, 0, STEM_SINK), (0.004, 0, 0.07), (0.007, -0.003, 0.13)], [0.003, 0.0028, 0.0026], 3, "stem")
    head = Vector((0.007, -0.003, 0.152))

    def key(fi, si, d):
        if d.z < -0.45:
            return "clover_dark"
        return "clover_light" if (si == 0 and d.z > -0.1) else "clover"
    ico(p, head, 0.0235, key, scale=(1, 1, 1.1), poke=0.36, jitter=0.06, seed=3)
    p.landing = head + Vector((0, 0, 0.0235 * 1.1 * 1.3))
    pet = [(-0.002, 0.002, STEM_SINK), (-0.012, 0.008, 0.04), (-0.022, 0.016, 0.07)]
    tube(p, pet, [0.0022, 0.002, 0.0018], 3, "stem")
    for a in (35, 155, 275):
        leaf(p, frame_m(pet[-1], sph(a, 8), Z), 0.033, 0.03, "leaf", "leaf_back", shape="heart", cup=0.18, droop=0.1)
    return p


def build_poppy():
    """California poppy: an open orange four-petal cup with a gold heart, a closed bud, grey-green leaves."""
    p = Part("Bloom_Poppy", "flower")
    stem = [(0, 0, STEM_SINK), (0.003, 0, 0.09), (0.008, -0.002, 0.18), (0.01, -0.004, 0.235)]
    tube(p, stem, [0.0035, 0.0032, 0.003, 0.003], 3, "stem")
    c = Vector(stem[-1])
    sides = 8
    prof = [(0.006, 0.0), (0.025, 0.017), (0.041, 0.038)]

    def ring_pt(j, k, inner):
        r, z = prof[j]
        a = 2 * math.pi * k / sides
        if j == 2:
            r, z = (r, z + 0.003) if k % 2 == 0 else (r * 0.8, z - 0.006)
        elif j == 1:
            r *= 1.05 if k % 2 == 0 else 0.95
        if inner and j > 0:
            r -= 0.0014
            z += 0.001
        return c + Vector((r * math.cos(a), r * math.sin(a), z))
    for inner in (False, True):
        rings = [[p.add(ring_pt(j, k, inner)) for k in range(sides)] for j in range(3)]
        for j in range(2):
            key = ("poppy_light" if j == 0 else "poppy") if inner else "poppy"
            for k in range(sides):
                k1 = (k + 1) % sides
                for tri in ((rings[j][k], rings[j][k1], rings[j + 1][k]), (rings[j][k1], rings[j + 1][k1], rings[j + 1][k])):
                    # outer faces look away from the cup's axis, inner faces toward it
                    g = sum((p.v[t] for t in tri), Vector()) / 3
                    ax = Vector((c.x, c.y, g.z))
                    away = ax if not inner else g + (g - ax)
                    p.tri_out(*tri, key, away)
    # gold stamens
    sr = [p.add(c + Vector((0.0068 * math.cos(a), 0.0068 * math.sin(a), 0.005)))
          for a in (math.pi * k / 2 + math.pi / 4 for k in range(4))]
    apex = p.add(c + Vector((0, 0, 0.014)))
    for k in range(4):
        p.tri_out(sr[k], sr[(k + 1) % 4], apex, "pollen", c)
    p.landing = c + Vector((0, 0, 0.016))
    # a closed bud on a second stem
    bstem = [(-0.003, 0.002, STEM_SINK), (-0.012, 0.007, 0.08), (-0.02, 0.012, 0.15)]
    tube(p, bstem, [0.0028, 0.0026, 0.0025], 3, "stem")
    t = (Vector(bstem[-1]) - Vector(bstem[-2])).normalized()
    b0 = Vector(bstem[-1])
    tube(p, [b0, b0 + t * 0.012, b0 + t * 0.032], [0.0034, 0.0068, 0.0], 3, lambda i: "sage" if i == 0 else "poppy")
    leaf(p, frame_m(along(stem, 0.04), sph(300, 30), Z), 0.07, 0.02, "sage", shape="diamond", droop=0.2)
    leaf(p, frame_m(along(stem, 0.07), sph(120, 34), Z), 0.06, 0.018, "sage", shape="diamond", droop=0.2)
    return p


def build_sunflower():
    """Small sunflower: twelve gold petals around a brown disc, head tipped to the front, two broad leaves."""
    p = Part("Bloom_Sunflower", "flower")
    stem = [(0, 0, STEM_SINK), (0.002, 0, 0.16), (0.004, -0.006, 0.32), (0.004, -0.016, 0.43)]
    tube(p, stem, [0.0055, 0.005, 0.0045, 0.004], 3, "stem")
    hc = Vector((0.004, -0.021, 0.446))
    tilt = math.radians(35)
    nh = Vector((0, -math.sin(tilt), math.cos(tilt)))
    u = Vector((1, 0, 0))
    v = nh.cross(u)
    rim = [p.add(hc + 0.027 * (math.cos(a) * u + math.sin(a) * v)) for a in (2 * math.pi * k / 8 for k in range(8))]
    front, back = p.add(hc + nh * 0.008), p.add(hc - nh * 0.013)
    for k in range(8):
        p.tri_out(rim[k], rim[(k + 1) % 8], front, "disk", hc - nh * 0.02)
        p.tri_out(rim[k], rim[(k + 1) % 8], back, "stem", hc + nh * 0.02)
    for k in range(12):
        a = 2 * math.pi * (k + 0.5) / 12
        radial = math.cos(a) * u + math.sin(a) * v
        d = (radial * math.cos(math.radians(14)) + nh * math.sin(math.radians(14))).normalized()
        leaf(p, frame_m(hc + radial * 0.019, d, nh), 0.048, 0.019, "sun_petal" if k % 2 else "honey", shape="diamond",
             cup=0.2, droop=0.08)
    p.landing = hc + nh * 0.01
    leaf(p, frame_m(along(stem, 0.12), sph(25, 22), Z), 0.095, 0.06, "leaf", "leaf_back", shape="round", droop=0.25)
    leaf(p, frame_m(along(stem, 0.2), sph(210, 26), Z), 0.085, 0.054, "leaf", "leaf_back", shape="round", droop=0.25)
    return p


GRASS_SCALE = 2.2  # Daniel's tufts are tabletop size (6-9 cm); floor tufts 13-17 cm
GRASS_MATS = {"Meadow green": "meadow", "Leaf green.001": "blade", "Spring green": "spring"}


def build_grass():
    """Grass tuft: Daniel's "Fan grass" (four blades) plus two blades of his "Tall grass", turned 40 degrees."""
    p = Part("Bloom_Grass", "grass")
    blades = []
    src = None
    if os.path.exists(ART_BLEND):
        with bpy.data.libraries.load(ART_BLEND, link=False) as (data_from, data_to):
            data_to.objects = [n for n in ("Fan grass", "Tall grass") if n in data_from.objects]
        src = {o.name: o for o in data_to.objects if o is not None}
    if src and "Fan grass" in src and "Tall grass" in src:
        rot = Matrix.Rotation(math.radians(40), 3, "Z")
        for name, polys, m in (("Fan grass", None, Matrix.Identity(3)), ("Tall grass", range(6), rot)):
            me = src[name].data
            mats = [s.material.name if s.material else "" for s in src[name].material_slots]
            for pi, poly in enumerate(me.polygons):
                if polys is not None and pi not in polys:
                    continue
                key = GRASS_MATS.get(mats[poly.material_index] if poly.material_index < len(mats) else "", "meadow")
                blades.append(([m @ (me.vertices[i].co * GRASS_SCALE) for i in poly.vertices], key))
        for o in src.values():
            me = o.data
            bpy.data.objects.remove(o, do_unlink=True)
            if me.users == 0:
                bpy.data.meshes.remove(me)
        log("grass: reused Daniel's Fan grass + 2 Tall grass blades")
    else:
        log("grass: art .blend not found, building blades in Daniel's style")
        rnd = random.Random(5)
        for i in range(6):
            a = math.radians(60 * i + rnd.uniform(-10, 10))
            h, w, lean = rnd.uniform(0.11, 0.16), rnd.uniform(0.02, 0.03), rnd.uniform(0.45, 0.8)
            out = Vector((math.cos(a), math.sin(a), 0))
            side = Vector((-math.sin(a), math.cos(a), 0))
            key = ("meadow", "blade", "spring")[i % 3]
            rows = [(0.0, w / 2), (0.4, w * 0.45), (0.75, w * 0.25)]
            pts = []
            for t, hw in rows:
                c = out * (0.012 + lean * h * t * t) + Z * (h * t * (1 - 0.25 * t))
                pts.append((c - side * hw, c + side * hw))
            tip = out * (0.012 + lean * h) + Z * (h * 0.75)
            for (a0, a1), (b0, b1) in zip(pts, pts[1:]):
                blades.append(([a0, a1, b1, b0], key))
            blades.append(([pts[-1][0], pts[-1][1], tip], key))
    for verts, key in blades:
        tris = [(0, 1, 2)] if len(verts) == 3 else [(0, 1, 2), (0, 2, 3)]
        p.sheet(verts, tris, key)
    return p


def build_vine_leaf():
    """Vine leaf: pointed, with a raised midrib, tip up (+Z here, Unity +Y), front out of the wall (-Y, Unity +Z)."""
    p = Part("Bloom_VineLeaf", "leaf")
    m = frame_m((0, 0, 0), Vector((0, -0.28, 1)), Vector((0, -1, 0)))
    leaf(p, m, 0.075, 0.052, "vine_leaf", "leaf_back", shape="vine", cup=0.0, droop=0.16)
    return p


def build_vine_blossom():
    """Vine blossom: a cupped five-petal lilac star with a gold centre, facing out of the wall (-Y, Unity +Z)."""
    p = Part("Bloom_VineBlossom", "blossom")
    R, base_y, cup = 0.03, -0.004, 0.01
    ring = []
    for i in range(10):
        a = math.radians(90 + 36 * i)
        r = R if i % 2 == 0 else 0.42 * R
        ring.append(Vector((r * math.cos(a), base_y - cup * (r / R) ** 2, r * math.sin(a))))
    c = Vector((0, base_y, 0))
    tris = [(0, 1 + i, 1 + (i + 1) % 10) for i in range(10)]
    p.sheet([c] + ring, tris, "blossom", "blossom_back")
    cr = [p.add(Vector((0.0058 * math.cos(a), base_y - 0.002, 0.0058 * math.sin(a))))
          for a in (math.radians(90 + 72 * k) for k in range(5))]
    apex = p.add(Vector((0, base_y - 0.009, 0)))
    for k in range(5):
        p.tri_out(cr[k], cr[(k + 1) % 5], apex, "pollen", Vector((0, 0.01, 0)))
    return p


ITEMS = [  # file stem, builder, short description for the manifest
    ("bloom_lavender", build_lavender, "lavender: three lilac spikes"),
    ("bloom_salvia", build_salvia, "salvia (Cleveland sage): blue-violet whorls up a stem"),
    ("bloom_phacelia", build_phacelia, "phacelia: two curled lavender-blue sprays"),
    ("bloom_clover", build_clover, "red clover: pink head + trefoil"),
    ("bloom_poppy", build_poppy, "California poppy: orange cup + bud"),
    ("bloom_sunflower", build_sunflower, "small sunflower: gold petals, brown disc, head tipped to +Z"),
    ("bloom_grass", build_grass, "grass tuft (Daniel's blades)"),
    ("bloom_vine_leaf", build_vine_leaf, "vine leaf: front +Z, tip +Y, pivot at the stalk"),
    ("bloom_vine_blossom", build_vine_blossom, "vine blossom: front +Z, pivot at the back centre"),
]


# ------------------------------------------------------------------------------------------------ objects
def vc_material(name="VC"):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    try:
        m.use_nodes = True
    except Exception:
        pass
    nt = m.node_tree
    nt.nodes.clear()
    attr = nt.nodes.new("ShaderNodeVertexColor")
    attr.layer_name = "Col"
    em = nt.nodes.new("ShaderNodeEmission")
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    nt.links.new(attr.outputs["Color"], em.inputs["Color"])
    nt.links.new(em.outputs["Emission"], out.inputs["Surface"])
    return m


def flat_material(name, rgb_hex, strength=1.0):
    m = bpy.data.materials.new(name)
    try:
        m.use_nodes = True
    except Exception:
        pass
    nt = m.node_tree
    nt.nodes.clear()
    em = nt.nodes.new("ShaderNodeEmission")
    em.inputs["Color"].default_value = tuple(srgb_to_lin(c) for c in hex_rgb(rgb_hex)) + (1.0,)
    em.inputs["Strength"].default_value = strength
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    nt.links.new(em.outputs["Emission"], out.inputs["Surface"])
    return m


def to_object(part, mat):
    me = bpy.data.meshes.new(part.name)
    me.from_pydata([tuple(v) for v in part.v], [], [list(f) for f, _ in part.f])
    me.update()
    assert len(me.polygons) == len(part.f), part.name
    plant = part.kind in ("flower", "grass")
    top = max(v.z for v in part.v)

    def dist(v):  # vine pieces: from the pivot (leaf) or from the flower's centre line (blossom, so its heart is 0)
        return Vector((v.x, v.z)).length if part.kind == "blossom" else v.length
    reach = max(dist(v) for v in part.v)
    attr = me.color_attributes.new("Col", "FLOAT_COLOR", "CORNER")
    data = [0.0] * (len(me.loops) * 4)
    for poly, (_, key) in zip(me.polygons, part.f):
        base = lin(key)
        n = poly.normal
        for li in poly.loop_indices:
            co = me.vertices[me.loops[li].vertex_index].co
            if plant:
                s = shade_plant(n, co.z)
                a = max(0.0, min(1.0, co.z / top)) ** 1.6
            else:
                s = shade_wall(n, co.z)
                a = max(0.0, min(1.0, dist(co) / reach))
            data[li * 4:li * 4 + 4] = [min(1.0, base[0] * s), min(1.0, base[1] * s), min(1.0, base[2] * s), a]
    attr.data.foreach_set("color", data)
    me.color_attributes.active_color = attr
    try:
        me.color_attributes.render_color_index = me.color_attributes.active_color_index
    except Exception:
        pass
    me.materials.append(mat)
    ob = bpy.data.objects.new(part.name, me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def export_fbx(ob, path):
    """Y-up mesh data and an identity node in the FBX, as common._fbx_space does: the mesh is turned to Y-up and the
    object gets +90 X, which the exporter's own -90 X axis conversion cancels."""
    conv = Matrix.Rotation(math.radians(-90), 4, "X")
    ob.data.transform(conv)
    ob.data.update()
    ob.matrix_basis = conv.inverted()
    bpy.context.view_layer.update()
    for o in bpy.context.scene.objects:
        o.select_set(o is ob)
    bpy.context.view_layer.objects.active = ob
    try:
        bpy.ops.export_scene.fbx(
            filepath=path, use_selection=True, object_types={"MESH"}, global_scale=1.0, apply_unit_scale=True,
            apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y", bake_space_transform=False,
            use_mesh_modifiers=True, mesh_smooth_type="OFF", colors_type="LINEAR", prioritize_active_color=True,
            use_tspace=False, add_leaf_bones=False, bake_anim=False, path_mode="AUTO", embed_textures=False,
            use_custom_props=False)
    finally:
        ob.data.transform(conv.inverted())
        ob.data.update()
        ob.matrix_basis = Matrix.Identity(4)
        bpy.context.view_layer.update()


def to_unity(v):
    return [round(-v.x, 4), round(v.z, 4), round(-v.y, 4)]


# ------------------------------------------------------------------------------------------------ contact sheet
def _cam(name, loc, target, lens=35.0, ortho=None):
    cd = bpy.data.cameras.new(name)
    if ortho:
        cd.type = "ORTHO"
        cd.ortho_scale = ortho
    else:
        cd.lens = lens
    cd.clip_start, cd.clip_end = 0.01, 50
    cam = bpy.data.objects.new(name, cd)
    bpy.context.scene.collection.objects.link(cam)
    cam.location = loc
    cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    return cam


def _inst(coll, ob, loc, yaw=0.0, scale=1.0, rot=None):
    o = bpy.data.objects.new(ob.name + "_i", ob.data)
    coll.objects.link(o)
    o.location = loc
    if rot is not None:
        o.matrix_world = Matrix.Translation(Vector(loc)) @ rot.to_4x4() @ Matrix.Scale(scale, 4)
    else:
        o.rotation_euler = (0, 0, math.radians(yaw))
        o.scale = (scale, scale, scale)
    return o


def _plane(coll, name, mat, verts):
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], [list(range(len(verts)))])
    me.materials.append(mat)
    o = bpy.data.objects.new(name, me)
    coll.objects.link(o)
    return o


def _text(coll, body, loc, size, mat, rot=(math.radians(75), 0, 0)):
    cu = bpy.data.curves.new("T", "FONT")
    cu.body = body
    cu.size = size
    cu.align_x = "CENTER"
    cu.materials.append(mat)
    o = bpy.data.objects.new("T", cu)
    coll.objects.link(o)
    o.location = loc
    o.rotation_euler = rot
    return o


def _wall_rot(normal, along):
    """Rotation taking the kit's wall pieces (front -Y, tip +Z) to face `normal` with the tip along `along`."""
    f = Vector(normal).normalized()
    up = Vector(along)
    up = (up - f * up.dot(f)).normalized()
    y = -f
    x = y.cross(up)
    return Matrix((x, y, up)).transposed()


def _vine(coll, kit, mat, path, normals, rnd, every=0.055):
    """Preview only: a stem tube along path with alternating leaves and a few blossoms (what the code lane does)."""
    part = Part("PreviewVine", "leaf")
    tube(part, path, [0.0065] * len(path), 5, "stem")
    ob = to_object(part, mat)
    bpy.context.scene.collection.objects.unlink(ob)
    coll.objects.link(ob)
    dist, side, nxt, count = 0.0, 1, 0.0, 0
    for i in range(1, len(path)):
        a, b = Vector(path[i - 1]), Vector(path[i])
        seg = (b - a).length
        while nxt <= dist + seg:
            t = (nxt - dist) / seg
            pnt, n = a.lerp(b, t), Vector(normals[i])
            tan = (b - a).normalized()
            sideways = n.cross(tan) * side
            d = (tan * 0.55 + sideways * 0.85).normalized()
            if count % 6 == 5:
                _inst(coll, kit["bloom_vine_blossom"], pnt + n * 0.006, rot=_wall_rot(n, tan), scale=rnd.uniform(0.9, 1.2))
            _inst(coll, kit["bloom_vine_leaf"], pnt + n * 0.004, rot=_wall_rot(n, d), scale=rnd.uniform(0.75, 1.25))
            side, count = -side, count + 1
            nxt += every * rnd.uniform(0.8, 1.2)
        dist += seg


def render_contact(kit, parts, path):
    sc = bpy.context.scene
    sc.render.engine = "BLENDER_EEVEE"
    try:
        sc.eevee.taa_render_samples = 16
    except Exception:
        pass
    sc.render.film_transparent = False
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.view_settings.view_transform = "Standard"
    sc.view_settings.look = "None"
    world = bpy.data.worlds.new("Bg")
    try:
        world.use_nodes = True
    except Exception:
        pass
    bg = world.node_tree.nodes.get("Background") if world.node_tree else None
    sc.world = world
    for ob in kit.values():
        ob.hide_render = True
    vc = bpy.data.materials["VC"]
    label = flat_material("Label", "#F2EEE6")
    floor = flat_material("Floor", "#8B8781")
    wallm = flat_material("Wall", "#C9C5BE")
    pillarm = flat_material("Pillar", "#B5B1AA")
    rnd = random.Random(42)
    out_dir = os.path.dirname(path)
    shots = []

    # A: the kit in a row at true scale (the vine pieces x3), with labels
    ca = bpy.data.collections.new("PanelA")
    sc.collection.children.link(ca)
    order = [n for n, _, _ in ITEMS]
    xs = []
    x = 0.0
    for name in order:
        big = name.startswith("bloom_vine")
        o = _inst(ca, kit[name], (x, 0, 0.0 if not big else 0.12), yaw=0, scale=3.0 if big else 1.0)
        if big:
            o.rotation_euler = (0, 0, 0)
        title = name.replace("bloom_", "").replace("_", " ")
        _text(ca, "%s%s\n%d tris" % (title, "  (x3)" if big else "", parts[name].tris()), (x, -0.05, -0.05), 0.024, label)
        xs.append(x)
        x += 0.235
    cx = (xs[0] + xs[-1]) / 2
    _text(ca, "Bloom kit  -  unlit vertex colour, as VBS_DartVC draws it", (cx, -0.05, 0.56), 0.03, label)
    cam_a = _cam("CamA", (cx, -4.0, 0.22 + 4.0 * math.tan(math.radians(15))), (cx, 0, 0.22), ortho=2.15)
    shots.append(("A", ca, cam_a, 1600, 600, (0.13, 0.15, 0.17)))

    # B: a floor patch as the player sees it standing (about 1.5 m eye height)
    cb = bpy.data.collections.new("PanelB")
    sc.collection.children.link(cb)
    _plane(cb, "FloorB", floor, [(-3, -2, 0), (3, -2, 0), (3, 4, 0), (-3, 4, 0)])
    weights = [("bloom_grass", 34), ("bloom_clover", 14), ("bloom_poppy", 12), ("bloom_lavender", 10),
               ("bloom_phacelia", 10), ("bloom_salvia", 10), ("bloom_sunflower", 6)]
    pool = [n for n, w in weights for _ in range(w)]
    for _ in range(420):
        px, py = rnd.uniform(-1.3, 1.3), rnd.uniform(-0.35, 1.8)
        if (px * px + (py + 0.35) ** 2) < 0.5 ** 2:
            continue  # the player's feet circle
        _inst(cb, kit[rnd.choice(pool)], (px, py, 0), yaw=rnd.uniform(0, 360), scale=rnd.uniform(0.8, 1.2))
    cam_b = _cam("CamB", (0, -0.95, 1.5), (0, 0.75, 0.0), lens=24)
    shots.append(("B", cb, cam_b, 800, 600, (0.55, 0.55, 0.55)))

    # C: vines up a wall and around a pillar, a few flowers at their feet
    cc = bpy.data.collections.new("PanelC")
    sc.collection.children.link(cc)
    _plane(cc, "FloorC", floor, [(-2, -2, 0), (2, -2, 0), (2, 0.6, 0), (-2, 0.6, 0)])
    _plane(cc, "WallC", wallm, [(-2, 0.6, 0), (2, 0.6, 0), (2, 0.6, 2.5), (-2, 0.6, 2.5)])
    pc, pr = Vector((0.45, 0.2, 0)), 0.12
    sides = 16
    pv = [(pc.x + pr * math.cos(2 * math.pi * k / sides), pc.y + pr * math.sin(2 * math.pi * k / sides), z)
          for z in (0, 2.5) for k in range(sides)]
    pme = bpy.data.meshes.new("Pillar")
    pme.from_pydata(pv, [], [[k, (k + 1) % sides, sides + (k + 1) % sides, sides + k] for k in range(sides)])
    pme.materials.append(pillarm)
    po = bpy.data.objects.new("Pillar", pme)
    cc.objects.link(po)
    wall_path, wall_n = [], []
    for i in range(40):
        t = i / 39
        wall_path.append((-0.42 + 0.12 * math.sin(t * 7.0) + 0.1 * t, 0.594, 1.55 * t))
        wall_n.append((0, -1, 0))
    _vine(cc, kit, vc, wall_path, wall_n, rnd)
    wall_path2 = [(-0.15 + 0.08 * math.sin(i / 39 * 9 + 1) + 0.25 * i / 39, 0.594, 1.1 * i / 39) for i in range(40)]
    _vine(cc, kit, vc, wall_path2, wall_n, rnd)
    sp, sn = [], []
    for i in range(60):
        t = i / 59
        a = math.radians(200) + t * 2.3 * 2 * math.pi
        rr = pr + 0.007
        sp.append((pc.x + rr * math.cos(a), pc.y + rr * math.sin(a), 1.6 * t))
        sn.append((math.cos(a), math.sin(a), 0))
    _vine(cc, kit, vc, sp, sn, rnd)
    for _ in range(90):
        px, py = rnd.uniform(-1.0, 1.0), rnd.uniform(-0.5, 0.55)
        if (Vector((px, py)) - pc.xy).length < pr + 0.04:
            continue
        _inst(cc, kit[rnd.choice(pool)], (px, py, 0), yaw=rnd.uniform(0, 360), scale=rnd.uniform(0.8, 1.2))
    cam_c = _cam("CamC", (0.05, -1.25, 1.05), (0.05, 0.45, 0.62), lens=26)
    shots.append(("C", cc, cam_c, 800, 600, (0.55, 0.55, 0.55)))

    imgs = {}
    for name, coll, cam, w, h, bgc in shots:
        for _, other, _, _, _, _ in shots:
            other.hide_render = other is not coll
        if bg is not None:
            bg.inputs[0].default_value = tuple(srgb_to_lin(c) for c in bgc) + (1.0,)
            bg.inputs[1].default_value = 1.0
        else:
            world.color = tuple(srgb_to_lin(c) for c in bgc)
        sc.camera = cam
        sc.render.resolution_x, sc.render.resolution_y = w, h
        sc.render.resolution_percentage = 100
        p = os.path.join(out_dir, "_panel_%s.png" % name)
        sc.render.filepath = p
        bpy.ops.render.render(write_still=True)
        img = bpy.data.images.load(p)
        px = np.empty(w * h * 4, dtype=np.float32)
        img.pixels.foreach_get(px)
        imgs[name] = px.reshape(h, w, 4)
        bpy.data.images.remove(img)
        os.remove(p)
    sheet = np.zeros((1200, 1600, 4), dtype=np.float32)  # rows bottom-up, like Blender's pixels
    sheet[600:1200, 0:1600] = imgs["A"]
    sheet[0:600, 0:800] = imgs["B"]
    sheet[0:600, 800:1600] = imgs["C"]
    im = bpy.data.images.new("contact", 1600, 1200, alpha=True)
    im.pixels.foreach_set(sheet.ravel())
    im.filepath_raw = path
    im.file_format = "PNG"
    im.save()
    log("contact sheet ->", path)


# ------------------------------------------------------------------------------------------------ main
def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--no-render", action="store_true")
    a = ap.parse_args(argv)
    out = os.path.abspath(a.out)
    os.makedirs(out, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    mat = vc_material()
    kit, parts, entries = {}, {}, []
    for stem, build, desc in ITEMS:
        part = build()
        n = part.tris()
        if n > BUDGET[part.kind]:
            raise SystemExit("%s: %d tris > budget %d" % (part.name, n, BUDGET[part.kind]))
        ob = to_object(part, mat)
        lo = Vector((min(v.x for v in part.v), min(v.y for v in part.v), min(v.z for v in part.v)))
        hi = Vector((max(v.x for v in part.v), max(v.y for v in part.v), max(v.z for v in part.v)))
        path = os.path.join(out, stem + ".fbx")
        export_fbx(ob, path)
        kit[stem], parts[stem] = ob, part
        e = {"file": stem + ".fbx", "mesh": part.name, "kind": part.kind, "what": desc, "tris": n,
             "verts": len(part.v), "size_m": {"width": round(hi.x - lo.x, 4), "height": round(hi.z - lo.z, 4),
                                              "depth": round(hi.y - lo.y, 4)},
             "bounds_unity": {"min": to_unity(Vector((hi.x, hi.y, lo.z))), "max": to_unity(Vector((lo.x, lo.y, hi.z)))}}
        if part.landing is not None:
            e["landing_unity"] = to_unity(part.landing)
        entries.append(e)
        log("%-20s %3d tris  %3d verts  h=%.3f  -> %s" % (part.name, n, len(part.v), hi.z - lo.z, path))
    manifest = {
        "source": "art/blender/scripts/bloom_flowers.py", "blender": bpy.app.version_string,
        "units": "meters; Unity Y-up, front +Z; one mesh per FBX, node unrotated and unscaled, pivot = origin",
        "material": "VC -> Art/_Shared/VBS_DartVC (VirtualBeeSwarm/Unlit, _UseVertexColor 1); no UVs",
        "vertex_color": "linear RGB with baked soft light; A = sway weight (flowers/grass: (y/top)^1.6; vine leaf: "
                        "distance from pivot / length; blossom: distance from its centre / radius), not opacity",
        "flowers": "pivot at the stem base on the floor; stems reach 6 mm below it; landing_unity = where a bee sits",
        "vine_pieces": "front +Z (out of the wall), leaf tip +Y; Quaternion.LookRotation(wallNormal, alongVine)",
        "double_sided": "flat parts have their own back faces 0.3 mm behind: Cull Back is fine",
        "items": entries,
    }
    with open(os.path.join(out, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=1)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "bloom_kit.blend"))
    if not a.no_render:
        render_contact(kit, parts, os.path.join(out, "contact.png"))
    log("done:", len(entries), "items ->", out)


main()
