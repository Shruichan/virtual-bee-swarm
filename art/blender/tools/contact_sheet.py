"""Final contact sheet for the Virtual Bee Swarm temp art set.

Imports every final/<item>/<item>.glb into ONE Blender scene, lit by the same two suns + grey world as the
per-item previews, seen by one orthographic camera (same angle for every item). Each item is auto-fit into its
own cell (so items are NOT to scale; the real size is printed under each one) and labeled with 3D text.
Also measures every item's bounds in Unity space (meters, Y-up, +Z front) and writes them to --bounds.

    blender -b --factory-startup --python contact_sheet.py -- --final DIR --out contact_sheet.png --bounds b.json
"""
import argparse
import json
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Vector

ITEMS = [
    "beeswax", "worker_bee", "hive", "hexagon_tile", "portal_frame",
    "smoker", "honey_jar", "syrup_bottle", "deep_bowl", "pebble_dish",
    "wasp", "giant_hornet", "ember", "flowers", "grass_tuft",
    "rocket", "moon", "earth", "clipboard", "seed_packet",
    # v3.1 props (2026-11-14)
    "swatter", "spray_bottle", "crowbar", "reducer", "rain_cloud",
    "stamp_approved", "stamp_grievance", "clipboard_sign", "score_card",
]
# objects that start hidden in Unity (same as the per-item previews)
HIDE = {"rocket": {"Flame"}, "hive": {"HeatGlow"}, "portal_frame": {"LivePane", "Glow"}}
NOTE = {
    "beeswax": "anim: Mouth (scale Y), WingL/R (rot Z)",
    "worker_bee": "1 mesh, 1 material, GPU instanced",
    "hive": "LiftRoot, Lid, Door, EntranceReducer, HexSlot1-4",
    "hexagon_tile": "unlit + lit state",
    "portal_frame": "LivePane / FallbackPane, Embers (+Z out)",
    "smoker": "anim: BellowsLever (rot X), Lid",
    "honey_jar": "Lid unscrews",
    "syrup_bottle": "Cap flips",
    "deep_bowl": "WRONG choice",
    "pebble_dish": "RIGHT choice, LandingSpot1-9",
    "wasp": "anim: WingL/R (rot Z)",
    "giant_hornet": "anim: Mandibles (rot X), WingL/R",
    "ember": "Ember + EmberFlake, + sprites",
    "flowers": "purple / blue / lavender",
    "grass_tuft": "1 mesh",
    "rocket": "Flame (hidden), Seat, Perch, Countdown",
    "moon": "pivot = center",
    "earth": "Clouds layer spins",
    "clipboard": "Beeswax's spreadsheet",
    "seed_packet": "optional prop",
    "swatter": "smoker WRONG choice",
    "spray_bottle": "smoker WRONG choice, SprayPoint",
    "crowbar": "wasps WRONG choice (BIGGER)",
    "reducer": "wasps RIGHT choice (SMALLER)",
    "rain_cloud": "fire brigade, Drops, RainPoint",
    "stamp_approved": "stamped on a right pick",
    "stamp_grievance": "stamped on a wrong pick",
    "clipboard_sign": "START button, PokePoint",
    "score_card": "escape, TextAnchor for the result",
}

COLS = 5
ROWS = (len(ITEMS) + COLS - 1) // COLS
CELL_PX = 400
HEADER_U = 0.32  # header height in cell units
AZ, EL = -30.0, 16.0
FONT_BOLD = ["/usr/share/fonts/opentype/fira/FiraSans-Bold.otf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"]
FONT_BOOK = ["/usr/share/fonts/opentype/fira/FiraSans-Book.otf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"]


def parse():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--final", required=True)
    p.add_argument("--stats", required=True, help="dir with <item>.json from the build (tris)")
    p.add_argument("--out", required=True)
    p.add_argument("--bounds", required=True)
    p.add_argument("--samples", type=int, default=32)
    return p.parse_args(argv)


def srgb_to_lin(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def hex_lin(h):
    h = h.lstrip("#")
    return tuple(srgb_to_lin(int(h[i:i + 2], 16) / 255.0) for i in (0, 2, 4)) + (1.0,)


def load_font(paths):
    for p in paths:
        if os.path.exists(p):
            try:
                return bpy.data.fonts.load(p, check_existing=True)
            except Exception:
                pass
    return None


_EMIT = {}


def emit_mat(hexcol):
    if hexcol in _EMIT:
        return _EMIT[hexcol]
    m = bpy.data.materials.new("Label_" + hexcol)
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    em = nt.nodes.new("ShaderNodeEmission")
    em.inputs["Color"].default_value = hex_lin(hexcol)
    em.inputs["Strength"].default_value = 1.0
    nt.links.new(em.outputs[0], out.inputs["Surface"])
    _EMIT[hexcol] = m
    return m


def setup_scene(res_x, res_y, samples):
    sc = bpy.context.scene
    for ob in list(bpy.data.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    eng = [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items]
    sc.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in eng else "BLENDER_EEVEE"
    try:
        sc.eevee.taa_render_samples = samples
    except Exception:
        pass
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.view_settings.view_transform = "Standard"
    sc.view_settings.look = "None"
    sc.render.resolution_x, sc.render.resolution_y = res_x, res_y
    sc.render.resolution_percentage = 100
    w = bpy.data.worlds.new("SheetWorld")
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
    d = Vector((math.sin(math.radians(AZ)) * math.cos(math.radians(EL)),
                -math.cos(math.radians(AZ)) * math.cos(math.radians(EL)),
                math.sin(math.radians(EL))))
    cd = bpy.data.cameras.new("SheetCam")
    cd.type = "ORTHO"
    cd.ortho_scale = float(COLS)  # width in cell units
    cd.sensor_fit = "HORIZONTAL"  # the sheet can be taller than wide (6 rows): keep ortho_scale = the width
    cd.clip_start, cd.clip_end = 0.5, 200.0
    cam = bpy.data.objects.new("SheetCam", cd)
    sc.collection.objects.link(cam)
    sc.camera = cam
    rot = (-d).to_track_quat("-Z", "Y")
    cam.rotation_euler = rot.to_euler()
    cam.location = d * 50.0
    R = rot @ Vector((1, 0, 0))
    U = rot @ Vector((0, 1, 0))
    return cam, d, R, U


def unity_bounds(pts):
    if not pts:
        return None
    a = np.array([(-p.x, p.z, -p.y) for p in pts])  # Blender -> Unity (glTF Y-up, X negated)
    lo, hi = a.min(axis=0), a.max(axis=0)
    return dict(min=[round(float(v), 4) for v in lo], max=[round(float(v), 4) for v in hi],
                size=[round(float(v), 4) for v in hi - lo])


def import_item(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    return [o for o in bpy.data.objects if o not in before]


def world_verts(objs):
    pts = []
    for o in objs:
        if o.type == "MESH":
            mw = o.matrix_world
            pts += [mw @ v.co for v in o.data.vertices]
    return pts


def add_text(body, size, loc, rot, hexcol, font, align="CENTER"):
    cu = bpy.data.curves.new("Txt", "FONT")
    cu.body = body
    cu.size = size
    cu.align_x = align
    cu.align_y = "CENTER"
    if font is not None:
        cu.font = font
    ob = bpy.data.objects.new("Txt", cu)
    bpy.context.scene.collection.objects.link(ob)
    ob.rotation_euler = rot
    ob.location = loc
    cu.materials.append(emit_mat(hexcol))
    return ob


def main():
    a = parse()
    res_x = COLS * CELL_PX
    res_y = int(round((ROWS + HEADER_U) * CELL_PX))
    cam, d, R, U = setup_scene(res_x, res_y, a.samples)
    rot = cam.rotation_euler.copy()
    bold, book = load_font(FONT_BOLD), load_font(FONT_BOOK)
    half_h = (ROWS + HEADER_U) / 2.0
    bounds = {}

    def plane(x, y, depth=0.0):
        return x * R + y * U - d * depth  # depth > 0 = farther from the camera

    for i, item in enumerate(ITEMS):
        c, r = i % COLS, i // COLS
        cx = -COLS / 2.0 + 0.5 + c
        cy = half_h - HEADER_U - 0.5 - r
        path = os.path.join(a.final, item, item + ".glb")
        objs = import_item(path)
        hide = HIDE.get(item, set())
        vis = []
        for o in objs:
            if o.name.split(".")[0] in hide:
                o.hide_render = True
            else:
                vis.append(o)
        bpy.context.view_layer.update()
        pts_all, pts_vis = world_verts(objs), world_verts(vis)
        bounds[item] = dict(visible=unity_bounds(pts_vis), all_meshes=unity_bounds(pts_all),
                            hidden_by_default=sorted(hide))
        # fit into the cell (projected onto the camera plane)
        px = [p.dot(R) for p in pts_vis]
        py = [p.dot(U) for p in pts_vis]
        pz = [p.dot(-d) for p in pts_vis]
        pw, ph = max(px) - min(px), max(py) - min(py)
        k = min(0.80 / max(pw, 1e-6), 0.60 / max(ph, 1e-6))
        center = ((max(px) + min(px)) / 2) * R + ((max(py) + min(py)) / 2) * U + ((max(pz) + min(pz)) / 2) * (-d)
        holder = bpy.data.objects.new("Holder_" + item, None)
        bpy.context.scene.collection.objects.link(holder)
        for o in objs:
            if o.parent is None:
                o.parent = holder
        target = plane(cx, cy + 0.11)
        holder.scale = (k, k, k)
        holder.location = target - k * center
        # labels
        st = {}
        sp = os.path.join(a.stats, item + ".json")
        if os.path.exists(sp):
            with open(sp) as fh:
                st = json.load(fh)
        sz = bounds[item]["visible"]["size"]
        add_text(item, 0.072, plane(cx, cy - 0.255, -2.0), rot, "#FFC845", bold)
        add_text("%s tris  |  %.2f x %.2f x %.2f m" % (format(st.get("tris", 0), ","), sz[0], sz[1], sz[2]),
                 0.046, plane(cx, cy - 0.335, -2.0), rot, "#FFF3D6", book)
        add_text(NOTE.get(item, ""), 0.040, plane(cx, cy - 0.400, -2.0), rot, "#A9A49B", book)

    add_text("VIRTUAL BEE SWARM  -  temp art set (final pick)", 0.10, plane(-COLS / 2.0 + 0.12, half_h - 0.13, -2.0),
             rot, "#FFC845", bold, align="LEFT")
    add_text("%d items, all original procedural low-poly (palette + vertex colours), Kenney CC0 sprites for smoke/embers  |  "
             "GLB + FBX, meters, Y-up, front = +Z  |  fit to cell, NOT to scale: real W x H x D under each  |  2026-11-14"
             % len(ITEMS),
             0.042, plane(-COLS / 2.0 + 0.12, half_h - 0.255, -2.0), rot, "#C9C2B6", book, align="LEFT")

    raw = os.path.splitext(a.out)[0] + "_raw.png"
    bpy.context.scene.render.filepath = raw
    bpy.ops.render.render(write_still=True)

    # composite onto a dark warm background with a panel per cell (Blender pixel rows are bottom-up)
    img = bpy.data.images.load(raw)
    w, h = img.size
    px = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    px = px.reshape(h, w, 4)
    bg = np.empty((h, w, 3), dtype=np.float32)
    bg[:] = (0.118, 0.110, 0.102)
    header_px = h - ROWS * CELL_PX
    m = 7
    for i in range(COLS * ROWS):
        c, r = i % COLS, i // COLS
        top = header_px + r * CELL_PX + m
        bot = header_px + (r + 1) * CELL_PX - m
        y0, y1 = h - bot, h - top
        bg[y0:y1, c * CELL_PX + m:(c + 1) * CELL_PX - m] = (0.176, 0.167, 0.156)
    al = px[..., 3:4]
    outp = np.ones((h, w, 4), dtype=np.float32)
    outp[..., :3] = px[..., :3] * al + bg * (1 - al)
    out_img = bpy.data.images.new("sheet", w, h, alpha=True)
    out_img.pixels.foreach_set(outp.ravel())
    out_img.filepath_raw = a.out
    out_img.file_format = "PNG"
    out_img.save()
    os.remove(raw)
    with open(a.bounds, "w") as fh:
        json.dump(bounds, fh, indent=1)
    print("SHEET_DONE", a.out, w, h)


main()
