"""Render the station objects from dartiros' "Virtual Bee Swarm Art.blend" one at a time, same camera and light.

    /Applications/Blender.app/Contents/MacOS/Blender -b "art/blender/Virtual Bee Swarm Art.blend" \
        --python hive/scout-vision/detect/station_art/render_dart.py -- --out OUT_DIR [--size 1800] [--only beehive,...]

Writes OUT_DIR/<label>.png (transparent RGBA). compose.py crops them and puts them on the card background.
Needs Blender 5.3 (the .blend's version). Uses the file's own models and materials; two stations have no model in
the file, so they are built here from simple shapes in the same low-poly style and palette (a sunflower, a wooden
bee hotel), and the spray bottle gets a red body and a BUG SPRAY label (bug_spray_label.png, make_labels.py).
"""
import argparse
import math
import os
import sys

import bpy
from mathutils import Vector

# label -> (showcase display empty, camera azimuth deg, elevation deg); EXPOSURE: per-station exposure tweak (stops)
STATIONS = {
    "beehive": ("Display - Managed Hive - Proposed", -30, 18),
    "honey_jar": ("Display - Honey Jar | Printed", -25, 14),
    "pebble_dish": ("Display - PebbleDish", -25, 38),
    "bug_spray": ("Display - Spray Bottle | Animated", -35, 14),
    "bee_hotel": ("Card - Bee Hotel", -28, 14),
    "native_flower": ("Card - Sunflower", -18, 10),
}
EXPOSURE = {"pebble_dish": -0.7}
HERE = os.path.dirname(os.path.abspath(__file__))


def mat(name, rgb, rough=0.6):
    m = bpy.data.materials.get(name)
    if m is not None:
        return m
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes.get("Principled BSDF")
    b.inputs["Base Color"].default_value = (*rgb, 1.0)
    b.inputs["Roughness"].default_value = rough
    m.diffuse_color = (*rgb, 1.0)
    return m


def add(prim, name, parent, mat_, loc=(0, 0, 0), rot=(0, 0, 0), scale=(1, 1, 1), **kw):
    getattr(bpy.ops.mesh, prim)(location=(0, 0, 0), **kw)
    ob = bpy.context.active_object
    ob.name = name
    ob.parent = parent
    ob.location = loc
    ob.rotation_euler = [math.radians(a) for a in rot]
    ob.scale = scale
    ob.data.materials.append(mat_)
    return ob


def fix_default_grey():
    """The file's known slip (see export_dart.py): materials left at the default grey use their viewport colour."""
    for m in bpy.data.materials:
        if not m.node_tree:
            continue
        b = m.node_tree.nodes.get("Principled BSDF")
        if b is None or any(n.type == "TEX_IMAGE" for n in m.node_tree.nodes):
            continue
        c = tuple(b.inputs["Base Color"].default_value)[:3]
        if all(abs(x - 0.8) < 1e-3 for x in c):
            b.inputs["Base Color"].default_value = tuple(m.diffuse_color)


def build_sunflower():
    root = bpy.data.objects.new("Card - Sunflower", None)
    bpy.context.scene.collection.objects.link(root)
    root.location = (30, 0, 0)
    leaf, dark = mat("Leaf green", (0.13, 0.3, 0.09)), mat("Grass shadow", (0.07, 0.19, 0.08))
    gold, pollen = mat("Honey gold", (1.0, 0.52, 0.06)), mat("Pollen", (0.93, 0.4, 0.04))
    disc = mat("Sunflower disc", (0.16, 0.07, 0.02), 0.8)
    add("primitive_cylinder_add", "Stem", root, leaf, loc=(0, 0, 0.25), scale=(0.014, 0.014, 0.25), vertices=8)
    for side, z, a in ((1, 0.2, -35), (-1, 0.32, 35)):
        add("primitive_uv_sphere_add", "Leaf", root, leaf, loc=(side * 0.075, 0, z), rot=(0, a, 0),
            scale=(0.085, 0.035, 0.008), segments=8, ring_count=5)
    head = bpy.data.objects.new("Head", None)
    bpy.context.scene.collection.objects.link(head)
    head.parent = root
    head.location = (0, -0.01, 0.53)
    head.rotation_euler = (math.radians(70), 0, 0)  # face the camera (front = -Y), tipped up a little
    add("primitive_cylinder_add", "Disc", head, disc, loc=(0, 0, 0.012), scale=(0.075, 0.075, 0.016), vertices=16)
    add("primitive_uv_sphere_add", "DiscDome", head, disc, loc=(0, 0, 0.024), scale=(0.07, 0.07, 0.022),
        segments=16, ring_count=6)
    add("primitive_cylinder_add", "Back", head, dark, loc=(0, 0, -0.004), scale=(0.08, 0.08, 0.01), vertices=16)
    for ring, (n, r, m, z, off) in enumerate(((18, 0.112, pollen, 0.0, 10.0), (18, 0.118, gold, 0.008, 0.0))):
        for i in range(n):
            a = math.radians(off + 360.0 * i / n)
            add("primitive_uv_sphere_add", f"Petal{ring}_{i}", head, m, loc=(r * math.cos(a), r * math.sin(a), z),
                rot=(0, 0, math.degrees(a)), scale=(0.048, 0.019, 0.006), segments=8, ring_count=4)
    return root


def build_bee_hotel():
    root = bpy.data.objects.new("Card - Bee Hotel", None)
    bpy.context.scene.collection.objects.link(root)
    root.location = (34, 0, 0)
    cedar, cream = mat("Cedar", (0.39, 0.14, 0.05)), mat("Butter cream", (1.0, 0.89, 0.65))
    teal, espresso = mat("Meadow teal", (0.07, 0.23, 0.18)), mat("Espresso", (0.07, 0.04, 0.03))
    bamboo = mat("Bamboo", (0.78, 0.52, 0.2))
    W, H, D, t = 0.30, 0.30, 0.12, 0.022
    for nm, loc, sc in (("Left", (-W / 2 + t / 2, 0, H / 2), (t / 2, D / 2, H / 2)),
                        ("Right", (W / 2 - t / 2, 0, H / 2), (t / 2, D / 2, H / 2)),
                        ("Bottom", (0, 0, t / 2), (W / 2, D / 2, t / 2)),
                        ("Top", (0, 0, H - t / 2), (W / 2, D / 2, t / 2)),
                        ("Back", (0, D / 2 - t / 2, H / 2), (W / 2, t / 2, H / 2)),
                        ("Shelf", (0, 0, H / 2), (W / 2 - t, D / 2 - 0.004, t / 3))):
        add("primitive_cube_add", nm, root, cedar, loc=loc, scale=sc)
    # bamboo tubes in two compartments, ends flush with the front
    r = 0.019
    for zc0 in (t, H / 2 + t / 3):
        rows = 3
        for row in range(rows):
            z = zc0 + r + 0.003 + row * (2 * r * 0.92)
            cols = 6 if row % 2 == 0 else 5
            for c in range(cols):
                x = (c - (cols - 1) / 2) * (2 * r + 0.002)
                add("primitive_cylinder_add", "Tube", root, bamboo, loc=(x, -0.01, z), rot=(90, 0, 0),
                    scale=(r, r, D / 2 - 0.012), vertices=10)
                add("primitive_cylinder_add", "Hole", root, espresso, loc=(x, -D / 2 + 0.001, z), rot=(90, 0, 0),
                    scale=(r * 0.55, r * 0.55, 0.002), vertices=10)
    # pitched roof, like the hive's teal roof
    for side in (-1, 1):
        add("primitive_cube_add", "Roof", root, teal, loc=(side * 0.085, -0.005, H + 0.05), rot=(0, side * 32, 0),
            scale=(0.105, D / 2 + 0.03, 0.012))
    add("primitive_cube_add", "Ridge", root, cream, loc=(0, -0.005, H + 0.105), scale=(0.016, D / 2 + 0.032, 0.012))
    # gable: a cedar triangle filling the space under the roof
    me = bpy.data.meshes.new("Gable")
    hw, gy0, gy1, top = W / 2, -D / 2, D / 2, 0.1
    verts = [(-hw, gy0, H), (hw, gy0, H), (0, gy0, H + top), (-hw, gy1, H), (hw, gy1, H), (0, gy1, H + top)]
    me.from_pydata(verts, [], [(0, 1, 2), (5, 4, 3), (0, 3, 4, 1), (1, 4, 5, 2), (2, 5, 3, 0)])
    me.materials.append(cedar)
    g = bpy.data.objects.new("Gable", me)
    bpy.context.scene.collection.objects.link(g)
    g.parent = root
    return root


def make_pebble_dish():
    """Blue dish (the only blue station), clear water, grey pebbles: same model, fresh opaque materials."""
    disp = bpy.data.objects["Display - PebbleDish"]
    greys = [mat("Pebble light", (0.62, 0.6, 0.56)), mat("Pebble mid", (0.42, 0.4, 0.37)), mat("Pebble dark", (0.26, 0.25, 0.23))]
    for ob in disp.children_recursive:
        if ob.type != "MESH":
            continue
        for i in range(len(ob.data.materials)):
            if ob.name.startswith("Dish"):
                ob.data.materials[i] = mat("Dish blue", (0.04, 0.2, 0.7)) if i == 0 else mat("Dish rim", (0.12, 0.38, 0.85))
            elif ob.name.startswith("Water"):
                ob.data.materials[i] = mat("Dish water", (0.16, 0.45, 0.7), 0.15)
            elif ob.name.startswith("Pebbles"):
                ob.data.materials[i] = greys[i % 3]


def make_bug_spray():
    lab = os.path.join(HERE, "bug_spray_label.png")
    img = bpy.data.images.load(lab)
    disp = bpy.data.objects["Display - Spray Bottle | Animated"]
    for ob in disp.children_recursive:
        if ob.type != "MESH":
            continue
        for i, m in enumerate(ob.data.materials):
            if m is None:
                continue
            if ob.name.startswith("Label"):
                m = m.copy()
                for n in m.node_tree.nodes:
                    if n.type == "TEX_IMAGE":
                        n.image = img
                ob.data.materials[i] = m
            elif ob.name.startswith("Bottle"):
                ob.data.materials[i] = mat("Spray red", (0.72, 0.06, 0.04), 0.45)
            elif ob.name.startswith("Trigger"):
                ob.data.materials[i] = mat("Spray charcoal", (0.03, 0.03, 0.03))


def args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--size", type=int, default=1800)
    p.add_argument("--only", default="")
    p.add_argument("--samples", type=int, default=64)
    return p.parse_args(argv)


def tree(ob):
    return [ob, *ob.children_recursive]


def bounds(objs):
    dg = bpy.context.evaluated_depsgraph_get()
    pts = []
    for ob in objs:
        if ob.type in {"MESH", "CURVE", "FONT", "SURFACE"} and not ob.hide_render:
            ev = ob.evaluated_get(dg)
            pts += [ev.matrix_world @ Vector(c) for c in ev.bound_box]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return lo, hi


def setup(size, samples):
    sc = bpy.context.scene
    eng = [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items]
    sc.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in eng else "BLENDER_EEVEE"
    try:
        sc.eevee.taa_render_samples = samples
    except Exception:
        pass
    sc.render.film_transparent = True
    sc.render.resolution_x = sc.render.resolution_y = size
    sc.render.resolution_percentage = 100
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.view_settings.view_transform = "Standard"
    sc.view_settings.look = "None"
    w = bpy.data.worlds.new("CardWorld")
    w.use_nodes = True
    bg = w.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (1.0, 0.93, 0.80, 1.0)  # warm cream fill, matches the card background
    bg.inputs[1].default_value = 0.45
    sc.world = w
    for ob in list(bpy.data.objects):
        if ob.type == "LIGHT":
            ob.hide_render = True
    for name, energy, rot in (("CardKey", 3.6, (50, 0, -35)), ("CardRim", 1.6, (65, 0, 150))):
        ld = bpy.data.lights.new(name, "SUN")
        ld.energy = energy
        ld.angle = math.radians(10)
        lo = bpy.data.objects.new(name, ld)
        lo.rotation_euler = [math.radians(a) for a in rot]
        sc.collection.objects.link(lo)
    cd = bpy.data.cameras.new("CardCam")
    cd.lens = 60
    cam = bpy.data.objects.new("CardCam", cd)
    sc.collection.objects.link(cam)
    sc.camera = cam
    return cam


def aim(cam, lo, hi, az, el, fill=0.80):
    center = (lo + hi) / 2
    radius = (hi - lo).length / 2
    fov = 2 * math.atan(18.0 / cam.data.lens)
    dist = radius / math.sin(fov / 2) / fill
    d = Vector((math.sin(math.radians(az)) * math.cos(math.radians(el)),
                -math.cos(math.radians(az)) * math.cos(math.radians(el)), math.sin(math.radians(el))))
    cam.location = center + d * dist
    cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
    cam.data.clip_start = max(0.001, dist * 0.02)
    cam.data.clip_end = dist * 20


def main():
    a = args()
    os.makedirs(a.out, exist_ok=True)
    only = [s for s in a.only.split(",") if s]
    fix_default_grey()
    build_sunflower()
    build_bee_hotel()
    make_bug_spray()
    make_pebble_dish()
    cam = setup(a.size, a.samples)
    keep = {cam.name, "CardKey", "CardRim"}
    for label, (disp, az, el) in STATIONS.items():
        if only and label not in only:
            continue
        root = bpy.data.objects.get(disp)
        if root is None:
            print("MISSING", disp, flush=True)
            continue
        mine = {o.name for o in tree(root)}
        for ob in bpy.data.objects:
            if ob.type in {"CAMERA", "LIGHT"} and ob.name not in keep:
                continue
            if ob.name in keep:
                continue
            ob.hide_render = ob.name not in mine
        lo, hi = bounds([bpy.data.objects[n] for n in mine])
        aim(cam, lo, hi, az, el)
        bpy.context.scene.view_settings.exposure = EXPOSURE.get(label, 0.0)
        bpy.context.scene.frame_set(1)
        bpy.context.scene.render.filepath = os.path.join(a.out, label + ".png")
        bpy.ops.render.render(write_still=True)
        print("RENDERED", label, flush=True)


main()
