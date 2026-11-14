"""Virtual Bee Swarm: shared palette, materials, mesh helpers, export and preview rendering.

Every item script (beeswax.py, hive.py, ...) builds its geometry with MB ("mesh builder", one bmesh per
output object), tags each face with a PALETTE key, and hands a build() function to run(). run() then does:

    empty scene -> build() -> palette UVs + vertex colours + materials -> .blend -> .glb + .fbx
    -> preview PNGs -> stats json

Conventions (match the Unity art spec in PLAN.md, S11.1):
  * meters, Blender Z-up while building; exports are Y-up (+Z forward in Unity)
  * the FRONT of every model faces Blender -Y, which lands on Unity +Z
  * a character's LEFT is Blender +X (Unity -X)
  * every object has zero rotation and unit scale; its origin is its pivot
  * colour = one face-level palette key -> UV into a tiny palette texture AND a vertex colour (Col)
"""
import argparse
import json
import math
import os
import sys
import time

import bmesh
import bpy
import numpy as np
from mathutils import Euler, Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_OUT = os.path.normpath(os.path.join(HERE, "..", "out"))

# ----------------------------------------------------------------------------------------------- palette
# Change a hex value and rebuild to recolour every item that uses it. Add new keys at the END so the
# existing cells of the palette texture keep their place (8 x 8 grid, 64 colours max).
PALETTE = [
    ("amber", "#F2A900"),
    ("honey", "#FFC845"),
    ("cream", "#FFF3D6"),
    ("charcoal", "#2B2B2B"),
    ("leaf", "#5BAA4A"),
    ("sky", "#7EC8E3"),
    ("fire", "#FF6B2C"),
    ("white", "#FFFFFF"),
    ("wood", "#C98B4B"),
    ("wood_dark", "#8A5A2E"),
    ("wing", "#E3F4FF"),
    ("moon", "#B4B4B0"),
    ("moon_dark", "#76767A"),
    ("purple", "#8E6BD8"),
    ("lavender", "#B9A2F2"),
    ("blue", "#5B8DEF"),
    ("ocean", "#2F7FC1"),
    ("leaf_dark", "#3E7F33"),
    ("steel", "#B9C3CC"),
    ("steel_dark", "#6E7A86"),
    ("pink", "#FF8FAB"),
    ("ember", "#FFE08A"),
    ("honey_dark", "#C77800"),
    ("hornet", "#E8871E"),
    ("hornet_dark", "#4A2E1F"),
    ("wasp", "#FFE03A"),
    ("stone", "#B8AFA3"),
    ("stone_dark", "#8F877D"),
    ("plastic", "#F4FAFC"),
    ("red", "#E5483B"),
    ("water", "#5FB6DA"),
    ("ink", "#3B3F4A"),
    ("terracotta", "#D9774B"),
    ("smoke_wing", "#FFF1C9"),
    # added by the art-director pass (2026-11-14): darker can, copper lid, leather pleats, moon crater rims
    ("steel_mid", "#93A0AC"),
    ("copper", "#C8783A"),
    ("leather", "#A8482C"),
    ("leather_dark", "#4E2414"),
    ("moon_light", "#D6D6D2"),
    # added for the v3.1 props (2026-11-14): rubber-stamp green, rain-cloud whites
    ("stamp_green", "#2E9E58"),
    ("cloud", "#F4F7FB"),
    ("cloud_dark", "#AFBCCB"),
]
GRID = 8  # palette texture is GRID x GRID cells
CELL = 8  # pixels per cell -> 64 x 64 px texture
PAL = {k: i for i, (k, _) in enumerate(PALETTE)}
assert len(PALETTE) <= GRID * GRID

# Material slots. Every mesh object gets exactly one. Bees/insects are slightly emissive so they read in
# passthrough; "glow" is for flames, embers and lit hexes; panes keep real UVs for a video texture.
MATERIALS = {
    "pal": dict(name="VBS_Palette", emission=0.08),
    "bee": dict(name="VBS_Palette_Bee", emission=0.45),
    "glow": dict(name="VBS_Glow", emission=1.0),
    "glow_soft": dict(name="VBS_GlowSoft", emission=2.0, alpha=0.35),
    "pane_live": dict(name="VBS_PaneLive", image=None, color="white", emission=0.6),
    "pane_fallback": dict(name="VBS_PaneFallback", image="meadow_fallback", color="white", emission=0.6),
}


def hex_rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4))


def srgb_to_lin(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def color_of(key):
    return hex_rgb(dict(PALETTE)[key])


def cell_uv(index):
    col, row = index % GRID, index // GRID
    return ((col + 0.5) / GRID, (row + 0.5) / GRID)


# ----------------------------------------------------------------------------------------------- math helpers
def M(loc=(0, 0, 0), rot=(0, 0, 0), scale=(1, 1, 1)):
    """Matrix from location, XYZ euler in DEGREES, and scale (number or 3-tuple)."""
    if isinstance(scale, (int, float)):
        scale = (scale, scale, scale)
    e = Euler([math.radians(a) for a in rot], "XYZ")
    return Matrix.LocRotScale(Vector(loc), e, Vector(scale))


def frame(origin, z_axis, y_hint=(0, 0, 1), scale=(1, 1, 1)):
    """Matrix whose local Z points along z_axis and local Y is as close to y_hint as possible."""
    z = Vector(z_axis).normalized()
    y = Vector(y_hint)
    y = (y - z * y.dot(z))
    if y.length < 1e-6:
        y = Vector((0, 1, 0)) if abs(z.y) < 0.9 else Vector((1, 0, 0))
        y = (y - z * y.dot(z))
    y.normalize()
    x = y.cross(z)
    if isinstance(scale, (int, float)):
        scale = (scale, scale, scale)
    m = Matrix((
        (x.x * scale[0], y.x * scale[1], z.x * scale[2], origin[0]),
        (x.y * scale[0], y.y * scale[1], z.y * scale[2], origin[1]),
        (x.z * scale[0], y.z * scale[1], z.z * scale[2], origin[2]),
        (0, 0, 0, 1)))
    return m


def bands(axis_param, table):
    """Pick a palette key from [(t_max, key), ...] (ascending) for parameter t."""
    for tmax, key in table:
        if axis_param <= tmax:
            return key
    return table[-1][1]


def ellipsoid_decal(center, radii, origin, right, up, out):
    """lfn for a decal: local (x right, y up, z = height above surface) -> point on an ellipsoid surface.

    The decal is shot along `out` (pointing away from the surface, toward the viewer) through
    origin + right*x + up*y, lands on the front side of the ellipsoid and is lifted by z along the normal."""
    c, R = Vector(center), Vector(radii)
    o, r_, u_, d = Vector(origin), Vector(right), Vector(up), Vector(out).normalized()

    def f(v):
        p0 = o + r_ * v.x + u_ * v.y
        P = Vector(((p0.x - c.x) / R.x, (p0.y - c.y) / R.y, (p0.z - c.z) / R.z))
        D = Vector((d.x / R.x, d.y / R.y, d.z / R.z))
        a, b, cc = D.dot(D), 2 * P.dot(D), P.dot(P) - 1
        disc = b * b - 4 * a * cc
        t = (-b + math.sqrt(disc)) / (2 * a) if disc >= 0 else 0.0
        s = p0 + d * t
        n = Vector(((s.x - c.x) / R.x ** 2, (s.y - c.y) / R.y ** 2, (s.z - c.z) / R.z ** 2)).normalized()
        return s + n * v.z
    return f


def cyl_wrap(R, z0, a0_deg=0.0):
    """lfn: flat local art (x along the surface, y up, z = height) wrapped around a vertical cylinder of radius R,
    centered on the front (-Y) unless a0_deg turns it (positive = toward +X)."""
    a0 = math.radians(a0_deg)

    def f(v):
        a = a0 + v.x / R
        rr = R + v.z
        return Vector((rr * math.sin(a), -rr * math.cos(a), z0 + v.y))
    return f


def ellipse_wrap(rx, ry, z0):
    """lfn: flat local art wrapped onto the front of a vertical elliptic cylinder (radii rx along X, ry along Y)."""
    def f(v):
        s = max(-0.999, min(0.999, v.x / rx))
        phi = math.asin(s)
        n = Vector((math.sin(phi) / rx, -math.cos(phi) / ry, 0.0)).normalized()
        return Vector((rx * math.sin(phi), -ry * math.cos(phi), z0 + v.y)) + n * v.z
    return f


def circle_pts(r, n, start_deg=0.0, rx=None, ry=None):
    rx = rx if rx is not None else r
    ry = ry if ry is not None else r
    return [(rx * math.cos(math.radians(start_deg) + 2 * math.pi * i / n),
             ry * math.sin(math.radians(start_deg) + 2 * math.pi * i / n)) for i in range(n)]


def rounded_rect(w, h, r, n=3):
    """CCW outline of a w x h rectangle (centered) with corner radius r, n segments per corner."""
    pts = []
    corners = [(w / 2 - r, h / 2 - r, 0), (-w / 2 + r, h / 2 - r, 90), (-w / 2 + r, -h / 2 + r, 180),
               (w / 2 - r, -h / 2 + r, 270)]
    for cx, cy, a0 in corners:
        for i in range(n + 1):
            a = math.radians(a0 + 90.0 * i / n)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return pts


# ----------------------------------------------------------------------------------------------- scene registry
REGISTRY = []  # every object created for the item, in creation order
_FONT = [None]


def world_loc(ob):
    loc = Vector(ob.location)
    p = ob.parent
    while p is not None:
        loc += p.location
        p = p.parent
    return loc


def place(ob, world_origin, parent=None):
    world_origin = Vector(world_origin)
    if parent is not None:
        ob.parent = parent
        ob.location = world_origin - world_loc(parent)
    else:
        ob.location = world_origin
    ob.rotation_euler = (0, 0, 0)
    ob.scale = (1, 1, 1)


def empty(name, origin=(0, 0, 0), parent=None, size=0.02, kind="PLAIN_AXES"):
    """A named transform (pivot / anchor point). Identity rotation, so its Unity +Z is the model's front."""
    ob = bpy.data.objects.new(name, None)
    ob.empty_display_type = kind
    ob.empty_display_size = size
    bpy.context.scene.collection.objects.link(ob)
    place(ob, origin, parent)
    REGISTRY.append(ob)
    return ob


def get_font():
    if _FONT[0] is None:
        for path in ("/usr/share/fonts/opentype/fira/FiraSans-ExtraBold.otf",
                     "/usr/share/fonts/truetype/fira/FiraSans-ExtraBold.ttf",
                     "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
                     "/Library/Fonts/Arial Bold.ttf", "C:/Windows/Fonts/arialbd.ttf"):
            if os.path.exists(path):
                try:
                    _FONT[0] = bpy.data.fonts.load(path, check_existing=True)
                    break
                except Exception:
                    pass
        if _FONT[0] is None:
            _FONT[0] = False  # Blender's built-in font
    return _FONT[0] or None


# ----------------------------------------------------------------------------------------------- mesh builder
class MB:
    """Builds one output mesh object. Every primitive is created in its own local space, optionally bent by
    `lfn(v) -> Vector` (local), moved by matrix `m`, optionally bent again by `vfn` (asset space), bevelled,
    then every new face is tagged with a palette key (`color` = key, or callable(center, normal) -> key)."""

    def __init__(self, name, mat="pal", smooth_angle=50.0):
        self.name = name
        self.mat = mat
        self.smooth_angle = smooth_angle
        self.bm = bmesh.new()
        self.pal = self.bm.faces.layers.int.new("pal")

    # -- core
    def _begin(self):
        return set(self.bm.faces)

    def _end(self, before, color, smooth, m=None, lfn=None, vfn=None, bevel=None, recalc=False):
        bm = self.bm
        faces = [f for f in bm.faces if f not in before]
        verts = list({v for f in faces for v in f.verts})
        if lfn:
            for v in verts:
                v.co = Vector(lfn(v.co.copy()))
        if m is not None:
            bmesh.ops.transform(bm, matrix=m, verts=verts)
        if vfn:
            for v in verts:
                v.co = Vector(vfn(v.co.copy()))
        if recalc:
            bmesh.ops.recalc_face_normals(bm, faces=faces)
        if bevel:
            w, segs = bevel if isinstance(bevel, tuple) else (bevel, 1)
            edges = list({e for f in faces for e in f.edges})
            bmesh.ops.bevel(bm, geom=edges, offset=w, offset_type="OFFSET", segments=segs, profile=0.5,
                            affect="EDGES", clamp_overlap=True)
            faces = [f for f in bm.faces if f not in before]
        bm.normal_update()
        for f in faces:
            key = color(f.calc_center_median(), f.normal) if callable(color) else color
            if key not in PAL:
                raise KeyError("unknown palette key %r" % key)
            f[self.pal] = PAL[key]
            f.smooth = smooth
        return faces

    # -- primitives
    def sphere(self, color, m=None, seg=16, rings=8, smooth=True, lfn=None, vfn=None):
        b = self._begin()
        bmesh.ops.create_uvsphere(self.bm, u_segments=seg, v_segments=rings, radius=1.0)
        return self._end(b, color, smooth, m, lfn, vfn)

    def ico(self, color, m=None, subdiv=1, smooth=False, lfn=None, vfn=None):
        b = self._begin()
        bmesh.ops.create_icosphere(self.bm, subdivisions=subdiv, radius=1.0)  # 1 = 20 tris, 2 = 80, 3 = 320, 4 = 1280
        return self._end(b, color, smooth, m, lfn, vfn)

    def box(self, color, m=None, bevel=None, smooth=False, lfn=None, vfn=None):
        """Unit cube (-0.5..0.5); size it with the matrix scale."""
        b = self._begin()
        bmesh.ops.create_cube(self.bm, size=1.0)
        return self._end(b, color, smooth, m, lfn, vfn, bevel)

    def lathe(self, color, profile, seg=16, m=None, cap_bottom=True, cap_top=True, smooth=True, lfn=None,
              vfn=None, start_deg=0.0, bevel=None):
        """Revolve [(radius, z), ...] (bottom to top for outward normals) around local Z."""
        b = self._begin()
        bm = self.bm
        angs = [math.radians(start_deg) + 2 * math.pi * k / seg for k in range(seg)]
        rings = []
        for r, z in profile:
            if r <= 1e-7:
                rings.append([bm.verts.new((0.0, 0.0, z))])
            else:
                rings.append([bm.verts.new((r * math.cos(a), r * math.sin(a), z)) for a in angs])
        for i in range(len(rings) - 1):
            A, B = rings[i], rings[i + 1]
            if len(A) == 1 and len(B) == 1:
                continue
            for k in range(seg):
                k2 = (k + 1) % seg
                if len(A) == 1:
                    bm.faces.new((A[0], B[k2], B[k]))
                elif len(B) == 1:
                    bm.faces.new((A[k], A[k2], B[0]))
                else:
                    bm.faces.new((A[k], A[k2], B[k2], B[k]))
        if cap_bottom and len(rings[0]) > 1:
            bm.faces.new(list(reversed(rings[0])))
        if cap_top and len(rings[-1]) > 1:
            bm.faces.new(rings[-1])
        return self._end(b, color, smooth, m, lfn, vfn, bevel)

    def cyl(self, color, r, depth, seg=12, m=None, r2=None, smooth=True, caps=True, lfn=None, vfn=None,
            bevel=None):
        """Cylinder / cone along local Z from 0 to depth (base at the origin)."""
        r2 = r if r2 is None else r2
        prof = [(r, 0.0), (r2, depth)]
        return self.lathe(color, prof, seg, m, caps, caps, smooth, lfn, vfn, bevel=bevel)

    def tube(self, color, p0, p1, r, r1=None, seg=8, smooth=True, caps=True):
        """Round bar from p0 to p1 (asset space)."""
        p0, p1 = Vector(p0), Vector(p1)
        d = p1 - p0
        m = frame(p0, d, (0, 0, 1) if abs(d.normalized().z) < 0.95 else (0, -1, 0))
        return self.cyl(color, r, d.length, seg, m, r1, smooth, caps)

    def limb(self, color, pts, r, r_end=None, seg=8, joint_color=None, end_ball=None, end_color=None):
        """Poly-line of round bars with ball joints (antennae, legs, stems)."""
        r_end = r if r_end is None else r_end
        n = len(pts) - 1
        for i in range(n):
            ra = r + (r_end - r) * i / n
            rb = r + (r_end - r) * (i + 1) / n
            self.tube(color, pts[i], pts[i + 1], ra, rb, seg)
            if 0 < i + 1 < len(pts) - 1:
                self.sphere(joint_color or color, M(pts[i + 1], scale=rb * 1.02), seg=seg, rings=max(4, seg // 2))
        if end_ball:
            self.sphere(end_color or color, M(pts[-1], scale=end_ball), seg=max(8, seg), rings=max(5, seg // 2))

    def torus(self, color, R, r, seg=16, seg2=8, m=None, smooth=True, lfn=None, vfn=None):
        b = self._begin()
        bm = self.bm
        grid = []
        for i in range(seg):
            u = 2 * math.pi * i / seg
            row = []
            for j in range(seg2):
                v = 2 * math.pi * j / seg2
                row.append(bm.verts.new(((R + r * math.cos(v)) * math.cos(u), (R + r * math.cos(v)) * math.sin(u),
                                         r * math.sin(v))))
            grid.append(row)
        for i in range(seg):
            for j in range(seg2):
                i2, j2 = (i + 1) % seg, (j + 1) % seg2
                bm.faces.new((grid[i][j], grid[i2][j], grid[i2][j2], grid[i][j2]))
        return self._end(b, color, smooth, m, lfn, vfn, recalc=True)

    def poly(self, color, pts, depth, m=None, smooth=False, lfn=None, vfn=None, bevel=None, cap_back=True):
        """Extrude a 2D outline (local XY) by depth along local Z (centered on z=0)."""
        b = self._begin()
        bm = self.bm
        area = sum(pts[i][0] * pts[(i + 1) % len(pts)][1] - pts[(i + 1) % len(pts)][0] * pts[i][1]
                   for i in range(len(pts)))
        if area < 0:
            pts = list(reversed(pts))
        lo = [bm.verts.new((x, y, -depth / 2)) for x, y in pts]
        hi = [bm.verts.new((x, y, depth / 2)) for x, y in pts]
        n = len(pts)
        bm.faces.new(hi)
        if cap_back:
            bm.faces.new(list(reversed(lo)))
        for i in range(n):
            i2 = (i + 1) % n
            bm.faces.new((lo[i], lo[i2], hi[i2], hi[i]))
        return self._end(b, color, smooth, m, lfn, vfn, bevel)

    def hexprism(self, color, r, depth, m=None, pointy=True, bevel=None, smooth=False, cap_back=True):
        start = 90.0 if pointy else 0.0
        return self.poly(color, circle_pts(r, 6, start), depth, m, smooth, bevel=bevel, cap_back=cap_back)

    def text(self, color, body, size, depth, m=None, lfn=None, vfn=None, resolution=2, align="CENTER",
             smooth=False, spacing=1.0):
        """Extruded text lying in local XY (reads from +Z), centered on the origin."""
        cu = bpy.data.curves.new("tmp_text", "FONT")
        cu.body = body
        cu.size = size
        cu.extrude = depth / 2
        cu.align_x = align
        cu.align_y = "CENTER"
        cu.resolution_u = resolution
        cu.space_character = spacing
        font = get_font()
        if font:
            cu.font = font
        ob = bpy.data.objects.new("tmp_text", cu)
        bpy.context.scene.collection.objects.link(ob)
        dg = bpy.context.evaluated_depsgraph_get()
        me = bpy.data.meshes.new_from_object(ob.evaluated_get(dg))
        b = self._begin()
        self.bm.from_mesh(me)
        bpy.data.objects.remove(ob)
        bpy.data.curves.remove(cu)
        bpy.data.meshes.remove(me)
        bmesh.ops.remove_doubles(self.bm, verts=list({v for f in self.bm.faces if f not in b for v in f.verts}),
                                 dist=1e-6)
        return self._end(b, color, smooth, m, lfn, vfn)

    def half_dome(self, color, m, plane_co, plane_no, seg=16, rings=8, smooth=True):
        """A sphere (shaped by m) cut by a plane (asset space), keeping the side plane_no points to, capped."""
        b = self._begin()
        bmesh.ops.create_uvsphere(self.bm, u_segments=seg, v_segments=rings, radius=1.0)
        new = [f for f in self.bm.faces if f not in b]
        verts = list({v for f in new for v in f.verts})
        bmesh.ops.transform(self.bm, matrix=m, verts=verts)
        geom = verts + list({e for f in new for e in f.edges}) + new
        bmesh.ops.bisect_plane(self.bm, geom=geom, dist=1e-6, plane_co=Vector(plane_co), plane_no=Vector(plane_no),
                               clear_inner=True)
        new = [f for f in self.bm.faces if f not in b]
        boundary = [e for f in new for e in f.edges if e.is_boundary]
        if boundary:
            bmesh.ops.holes_fill(self.bm, edges=list(set(boundary)), sides=0)
        return self._end(b, color, smooth)

    def flat(self, color, pts, m=None, lfn=None, vfn=None, smooth=False):
        """A single flat face from a 2D outline (local XY, facing +Z). Cheap decals: labels, tiles, text plates."""
        b = self._begin()
        area = sum(pts[i][0] * pts[(i + 1) % len(pts)][1] - pts[(i + 1) % len(pts)][0] * pts[i][1]
                   for i in range(len(pts)))
        if area < 0:
            pts = list(reversed(pts))
        self.bm.faces.new([self.bm.verts.new((x, y, 0.0)) for x, y in pts])
        return self._end(b, color, smooth, m, lfn, vfn)

    def ring(self, color, outer, inner, depth, m=None, bevel=None, smooth=False, lfn=None, vfn=None):
        """A flat ring/frame between two CCW outlines with the same point count, extruded along local Z."""
        b = self._begin()
        bm = self.bm
        n = len(outer)
        of = [bm.verts.new((x, y, depth / 2)) for x, y in outer]
        ob = [bm.verts.new((x, y, -depth / 2)) for x, y in outer]
        fi = [bm.verts.new((x, y, depth / 2)) for x, y in inner]
        bi = [bm.verts.new((x, y, -depth / 2)) for x, y in inner]
        for i in range(n):
            j = (i + 1) % n
            bm.faces.new((of[i], of[j], fi[j], fi[i]))
            bm.faces.new((ob[j], ob[i], bi[i], bi[j]))
            bm.faces.new((ob[i], ob[j], of[j], of[i]))
            bm.faces.new((bi[j], bi[i], fi[i], fi[j]))
        return self._end(b, color, smooth, m, lfn, vfn, bevel)

    # -- output
    def to_object(self, name=None, origin=(0, 0, 0), parent=None, mat=None, keep_uv=False, preview_hide=False):
        name = name or self.name
        o = Vector(origin)
        bmesh.ops.translate(self.bm, vec=-o, verts=list(self.bm.verts))
        me = bpy.data.meshes.new(name)
        self.bm.to_mesh(me)
        self.bm.free()
        ob = bpy.data.objects.new(name, me)
        bpy.context.scene.collection.objects.link(ob)
        place(ob, o, parent)
        ob["vbs_mat"] = mat or self.mat
        ob["smooth_angle"] = self.smooth_angle
        if keep_uv:
            ob["keep_uv"] = True
        if preview_hide:
            ob["preview_hide"] = True
        REGISTRY.append(ob)
        return ob


# ----------------------------------------------------------------------------------------------- textures/materials
_MATS = {}
_IMAGES = {}


def _save_image_atomic(img, path):
    tmp = "%s.%d.tmp.png" % (path[:-4], os.getpid())
    img.filepath_raw = tmp
    img.file_format = "PNG"
    img.save()
    os.replace(tmp, path)
    img.filepath_raw = path
    img.filepath = path


def make_image(name, w, h, rgba, out_dir, alpha=False):
    """rgba: numpy float array (h, w, 4) in sRGB, row 0 = BOTTOM. Saved to out/textures/<name>.png."""
    os.makedirs(os.path.join(out_dir, "textures"), exist_ok=True)
    path = os.path.join(out_dir, "textures", name + ".png")
    img = bpy.data.images.new(name, w, h, alpha=alpha)
    img.pixels.foreach_set(np.ascontiguousarray(rgba, dtype=np.float32).ravel())
    _save_image_atomic(img, path)
    img.pack()
    _IMAGES[name] = img
    return img


def palette_image(out_dir):
    if "VBS_Palette" in _IMAGES:
        return _IMAGES["VBS_Palette"]
    n = GRID * CELL
    px = np.ones((n, n, 4), dtype=np.float32)
    px[:, :, :3] = 1.0
    for i, (_, hx) in enumerate(PALETTE):
        col, row = i % GRID, i // GRID
        px[row * CELL:(row + 1) * CELL, col * CELL:(col + 1) * CELL, :3] = hex_rgb(hx)
    return make_image("VBS_Palette", n, n, px, out_dir)


def meadow_image(out_dir):
    """A stand-in for the meadow-window video (16:9): sky, hills, flowers. Only for the FallbackPane preview."""
    if "meadow_fallback" in _IMAGES:
        return _IMAGES["meadow_fallback"]
    w, h = 256, 144
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    v = yy / h
    px = np.ones((h, w, 4), dtype=np.float32)
    sky_top, sky_low = np.array(hex_rgb("#7EC8E3")), np.array(hex_rgb("#CDEFF7"))
    t = np.clip((v - 0.45) / 0.55, 0, 1)[..., None]
    px[..., :3] = sky_low * (1 - t) + sky_top * t
    hill1 = 0.42 + 0.06 * np.sin(xx / w * 6.0 + 1.0)
    hill2 = 0.30 + 0.05 * np.sin(xx / w * 9.0 + 3.0)
    px[v < hill1, :3] = hex_rgb("#7CC35E")
    px[v < hill2, :3] = hex_rgb("#5BAA4A")
    rng = np.random.default_rng(3)
    for _ in range(40):
        cx, cy = rng.uniform(0, w), rng.uniform(0, h * 0.28)
        c = hex_rgb(["#8E6BD8", "#5B8DEF", "#FFC845", "#B9A2F2"][rng.integers(0, 4)])
        m = (xx - cx) ** 2 + (yy - cy) ** 2 < rng.uniform(2.0, 5.0)
        px[m, :3] = c
    sun = (xx - w * 0.8) ** 2 + (yy - h * 0.8) ** 2 < 120
    px[sun, :3] = hex_rgb("#FFE08A")
    return make_image("meadow_fallback", w, h, px, out_dir)


def get_material(kind, out_dir):
    if kind in _MATS:
        return _MATS[kind]
    d = MATERIALS[kind]
    mat = bpy.data.materials.new(d["name"])
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    bsdf.inputs["Roughness"].default_value = 0.6
    bsdf.inputs["Metallic"].default_value = 0.0
    if "Specular IOR Level" in bsdf.inputs:
        bsdf.inputs["Specular IOR Level"].default_value = 0.3
    img = None
    if "image" in d:
        if d["image"] == "meadow_fallback":
            img = meadow_image(out_dir)
    else:
        img = palette_image(out_dir)
    if img is not None:
        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.image = img
        tex.interpolation = "Closest" if img.name == "VBS_Palette" else "Linear"
        tex.location = (-400, 200)
        nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        nt.links.new(tex.outputs["Color"], bsdf.inputs["Emission Color"])
    else:
        c = color_of(d.get("color", "white"))
        lin = tuple(srgb_to_lin(x) for x in c) + (1.0,)
        bsdf.inputs["Base Color"].default_value = lin
        bsdf.inputs["Emission Color"].default_value = lin
    bsdf.inputs["Emission Strength"].default_value = d.get("emission", 0.0)
    alpha = d.get("alpha", 1.0)
    if alpha < 1.0:
        bsdf.inputs["Alpha"].default_value = alpha
        if hasattr(mat, "surface_render_method"):
            mat.surface_render_method = "BLENDED"
        else:
            mat.blend_method = "BLEND"
    mat.diffuse_color = (1, 1, 1, alpha)
    _MATS[kind] = mat
    return mat


# ----------------------------------------------------------------------------------------------- finalize
def finalize(out_dir):
    for ob in REGISTRY:
        if ob.type != "MESH":
            continue
        me = ob.data
        n = len(me.polygons)
        pal = np.zeros(n, dtype=np.int32)
        me.attributes["pal"].data.foreach_get("value", pal)
        totals = np.zeros(n, dtype=np.int32)
        me.polygons.foreach_get("loop_total", totals)
        loop_pal = np.repeat(pal, totals)
        if not ob.get("keep_uv"):
            uvl = me.uv_layers.get("UVMap") or me.uv_layers.new(name="UVMap")
            centers = np.array([cell_uv(i) for i in range(GRID * GRID)], dtype=np.float32)
            uvl.data.foreach_set("uv", centers[loop_pal].ravel())
        cols = np.array([hex_rgb(h) + (1.0,) for _, h in PALETTE], dtype=np.float32)
        ca = me.color_attributes.new("Col", "BYTE_COLOR", "CORNER")
        try:
            ca.data.foreach_set("color_srgb", cols[loop_pal].ravel())
        except Exception:
            lin = np.vectorize(srgb_to_lin)(cols[:, :3])
            cols_lin = np.concatenate([lin, cols[:, 3:]], axis=1).astype(np.float32)
            ca.data.foreach_set("color", cols_lin[loop_pal].ravel())
        me.color_attributes.active_color = ca
        me.color_attributes.render_color_index = me.color_attributes.active_color_index
        me.set_sharp_from_angle(angle=math.radians(ob["smooth_angle"]))
        me.materials.clear()
        me.materials.append(get_material(ob["vbs_mat"], out_dir))
        me.update()


def tri_count(ob):
    me = ob.data
    me.calc_loop_triangles()
    return len(me.loop_triangles)


def check_transforms():
    bad = []
    for ob in REGISTRY:
        if any(abs(a) > 1e-6 for a in ob.rotation_euler) or any(abs(s - 1) > 1e-6 for s in ob.scale):
            bad.append(ob.name)
    return bad


# ----------------------------------------------------------------------------------------------- export
def export(item, out_dir, fbx=True):
    glb = os.path.join(out_dir, item + ".glb")
    bpy.ops.export_scene.gltf(
        filepath=glb, export_format="GLB", use_selection=False, export_yup=True, export_apply=True,
        export_texcoords=True, export_normals=True, export_materials="EXPORT", export_vertex_color="NONE",
        export_cameras=False, export_lights=False, export_extras=False, export_animations=False,
        export_image_format="AUTO")
    paths = [glb]
    if fbx:
        path = os.path.join(out_dir, item + ".fbx")
        _fbx_space(True)
        try:
            bpy.ops.export_scene.fbx(
                filepath=path, use_selection=False, object_types={"EMPTY", "MESH"}, global_scale=1.0,
                apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y",
                bake_space_transform=False, use_mesh_modifiers=True, mesh_smooth_type="OFF", colors_type="SRGB",
                use_tspace=False, add_leaf_bones=False, bake_anim=False, path_mode="COPY", embed_textures=True,
                use_custom_props=False)
        finally:
            _fbx_space(False)
        paths.append(path)
    return paths


def _fbx_space(on):
    """Blender's bake_space_transform mangles grandchildren (90 deg X + Z-up offsets), so instead every mesh and
    child transform is pre-converted to Y-up and each root gets a +90 X that the exporter's own -90 X axis
    conversion cancels. Result: FBX nodes carry exactly the Blender transforms (zero rotation for our objects),
    Y-up, +Z front, meters. on=False undoes it."""
    conv = Matrix.Rotation(math.radians(-90), 4, "X")
    inv = conv.inverted()
    meshes = {ob.data for ob in REGISTRY if ob.type == "MESH"}
    for me in meshes:
        me.transform(conv if on else inv)
        me.update()
    for ob in REGISTRY:
        mb = ob.matrix_basis.copy()
        if ob.parent is None or ob.parent not in REGISTRY:
            ob.matrix_basis = mb @ inv if on else mb @ conv
        else:
            ob.matrix_basis = conv @ mb @ inv if on else inv @ mb @ conv
    bpy.context.view_layer.update()


# ----------------------------------------------------------------------------------------------- preview
def _setup_preview_scene():
    sc = bpy.context.scene
    eng = [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items]
    sc.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in eng else "BLENDER_EEVEE"
    try:
        sc.eevee.taa_render_samples = 24
    except Exception:
        pass
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.view_settings.view_transform = "Standard"
    sc.view_settings.look = "None"
    w = bpy.data.worlds.new("PreviewWorld")
    w.use_nodes = True
    bg = w.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.62, 0.64, 0.68, 1.0)
    bg.inputs[1].default_value = 0.9
    sc.world = w
    for name, energy, rot in (("Key", 3.2, (50, 0, -40)), ("Rim", 1.4, (60, 0, 150))):
        ld = bpy.data.lights.new(name, "SUN")
        ld.energy = energy
        ld.angle = math.radians(8)
        lo = bpy.data.objects.new(name, ld)
        lo.rotation_euler = [math.radians(a) for a in rot]
        sc.collection.objects.link(lo)
    cd = bpy.data.cameras.new("PreviewCam")
    cd.lens = 50
    cam = bpy.data.objects.new("PreviewCam", cd)
    sc.collection.objects.link(cam)
    sc.camera = cam
    for ob in REGISTRY:
        if ob.get("preview_hide"):
            ob.hide_render = True
    return cam


def _bounds():
    pts = []
    dg = bpy.context.evaluated_depsgraph_get()
    for ob in REGISTRY:
        if ob.type == "MESH" and not ob.hide_render:
            mw = ob.evaluated_get(dg).matrix_world
            pts += [mw @ Vector(c) for c in ob.bound_box]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return lo, hi


def _aim(cam, center, radius, az, el):
    fov = 2 * math.atan(18.0 / cam.data.lens)
    dist = radius / math.sin(fov / 2) * 0.86
    d = Vector((math.sin(math.radians(az)) * math.cos(math.radians(el)),
                -math.cos(math.radians(az)) * math.cos(math.radians(el)),
                math.sin(math.radians(el))))
    cam.location = center + d * dist
    cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
    cam.data.clip_start = max(0.001, dist * 0.02)
    cam.data.clip_end = dist * 20


def _render(path, size):
    sc = bpy.context.scene
    sc.render.resolution_x = sc.render.resolution_y = size
    sc.render.resolution_percentage = 100
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)


def _load_px(path):
    img = bpy.data.images.load(path)
    w, h = img.size
    a = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(a)
    bpy.data.images.remove(img)
    return a.reshape(h, w, 4)


def _save_px(px, path):
    h, w = px.shape[:2]
    img = bpy.data.images.new(os.path.basename(path), w, h, alpha=True)
    img.pixels.foreach_set(np.ascontiguousarray(px, dtype=np.float32).ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)


def render_previews(item, out_dir, size=512, hero_az=-30.0):
    prev = os.path.join(out_dir, "previews")
    tmp = os.path.join(prev, "_tmp_" + item)
    os.makedirs(tmp, exist_ok=True)
    cam = _setup_preview_scene()
    lo, hi = _bounds()
    center = (lo + hi) / 2
    radius = (hi - lo).length / 2
    _aim(cam, center, radius, hero_az, 18)
    _render(os.path.join(prev, item + "_hero.png"), size)
    half = size // 2
    sheet = np.zeros((size, size, 4), dtype=np.float32)
    for i, az in enumerate((hero_az, hero_az + 90, hero_az + 180, hero_az + 270)):
        p = os.path.join(tmp, "v%d.png" % i)
        _aim(cam, center, radius, az, 15)
        _render(p, half)
        px = _load_px(p)
        col, row = i % 2, i // 2
        y0 = size - (row + 1) * half  # pixel rows are bottom-up
        sheet[y0:y0 + half, col * half:(col + 1) * half] = px
        os.remove(p)
    os.rmdir(tmp)
    _save_px(sheet, os.path.join(prev, item + ".png"))


def contact_sheet(out_dir, items, cols=5, thumb=256, path=None):
    prev = os.path.join(out_dir, "previews")
    rows = (len(items) + cols - 1) // cols
    W, H = cols * thumb, rows * thumb
    sheet = np.zeros((H, W, 4), dtype=np.float32)
    sheet[..., :3] = 0.16
    sheet[..., 3] = 1.0
    for i, item in enumerate(items):
        p = os.path.join(prev, item + "_hero.png")
        if not os.path.exists(p):
            continue
        px = _load_px(p)
        f = px.shape[0] // thumb
        if f > 1:
            px = px[:f * thumb, :f * thumb].reshape(thumb, f, thumb, f, 4).mean(axis=(1, 3))
        a = px[..., 3:4]
        col, row = i % cols, i // cols
        y0 = H - (row + 1) * thumb
        cell = sheet[y0:y0 + thumb, col * thumb:(col + 1) * thumb]
        cell[..., :3] = px[..., :3] * a + cell[..., :3] * (1 - a)
    _save_px(sheet, path or os.path.join(prev, "_contact_sheet.png"))


# ----------------------------------------------------------------------------------------------- run
def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", default=DEFAULT_OUT)
    p.add_argument("--no-render", action="store_true")
    p.add_argument("--no-fbx", action="store_true")
    p.add_argument("--no-blend", action="store_true")
    p.add_argument("--size", type=int, default=512)
    a, _ = p.parse_known_args(argv)
    return a


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    REGISTRY.clear()
    _MATS.clear()
    _IMAGES.clear()
    _FONT[0] = None
    sc = bpy.context.scene
    sc.unit_settings.system = "METRIC"
    sc.unit_settings.scale_length = 1.0


def run(item, build, budget, pivot="base", notes="", hero_az=-30.0):
    """Build one item and write <out>/<item>.glb/.fbx, blend/<item>.blend, previews/, stats/<item>.json."""
    a = parse_args()
    out = os.path.abspath(a.out)
    for sub in ("", "blend", "previews", "stats", "textures"):
        os.makedirs(os.path.join(out, sub), exist_ok=True)
    t0 = time.time()
    reset()
    palette_image(out)
    build()
    finalize(out)
    bad = check_transforms()
    if bad:
        raise RuntimeError("objects with rotation/scale (must be applied): %s" % bad)
    objs = []
    total = 0
    for ob in REGISTRY:
        tris = tri_count(ob) if ob.type == "MESH" else 0
        total += tris
        wl = world_loc(ob)
        objs.append(dict(name=ob.name, type=ob.type, tris=tris, parent=ob.parent.name if ob.parent else None,
                         material=ob.data.materials[0].name if ob.type == "MESH" else None,
                         pivot_unity=[round(-wl.x, 4), round(wl.z, 4), round(-wl.y, 4)],
                         hidden_in_preview=bool(ob.get("preview_hide"))))
    if not a.no_blend:
        bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "blend", item + ".blend"), compress=True)
    files = export(item, out, fbx=not a.no_fbx)
    lo, hi = _bounds_all()
    stats = dict(item=item, tris=total, budget=budget, over_budget=total > budget, pivot=pivot,
                 size_unity_m=[round(hi.x - lo.x, 4), round(hi.z - lo.z, 4), round(hi.y - lo.y, 4)],
                 bounds_blender=dict(min=[round(c, 4) for c in lo], max=[round(c, 4) for c in hi]),
                 objects=objs, files=[os.path.basename(f) for f in files], notes=notes)
    if not a.no_render:
        render_previews(item, out, a.size, hero_az)
    stats["seconds"] = round(time.time() - t0, 1)
    with open(os.path.join(out, "stats", item + ".json"), "w") as fh:
        json.dump(stats, fh, indent=1)
    print("VBS_DONE %s tris=%d budget=%d size=%s" % (item, total, budget, stats["size_unity_m"]))
    return stats


def _bounds_all():
    pts = []
    for ob in REGISTRY:
        if ob.type == "MESH":
            off = world_loc(ob)
            pts += [Vector(c) + off for c in ob.bound_box]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return lo, hi
