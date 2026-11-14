"""Export dartiros' rigged creatures and animated props from "Virtual Bee Swarm Art.blend" to Unity FBX.

    blender -b "art/blender/Virtual Bee Swarm Art.blend" --python art/blender/scripts/export_dart.py -- --out DIR [--item hornet ...]

Needs the Blender that saved the file (5.3: the .blend is file version 503; 4.5 reads it with data loss).
One FBX per item (DIR/<item>/<item>.fbx) plus DIR/manifest.json (clips, materials, sizes) that the Unity
importer (Assets/VirtualBeeSwarm/ArtAnim/Editor/DartArtImport.cs) reads.

Per item, on a fresh load of the .blend:
  * only the item's hierarchy stays; it is taken off its showcase "Display - ..." empty (native size, meters)
  * names lose Blender's .001 suffixes; a rig becomes Rig > Mesh
  * transforms applied: every unanimated mesh ends up unrotated and unscaled at its pivot (modifiers applied first)
  * the hive's many small parts are joined per part (Stand, BoxLower, BoxUpper, Lid, Combs) and gets a HeatGlow box
  * colours: each face's material colour times a soft top-front light, baked into a linear vertex colour; materials
    collapse to VC (opaque), VC_Wing (wings), VC_Glow (flame) and one per printed texture (written next to the FBX)
  * clips: a rig's NLA strips each become a take (in place, 30 fps); a prop's object actions bake into one take that
    the Unity importer splits (open / squeeze / pull / ignition + flicker)
  * axes: Blender -Y front, Z up -> Unity +Z front, Y up (the importer bakes the axis conversion)
"""
import argparse
import json
import math
import os
import re
import sys

import bpy
from mathutils import Matrix, Vector

FPS = 30

# item -> root object, kind, Unity slot, clip splits for props (name, first, last, loop)
ITEMS = {
    "hornet": dict(root="Hornet - Rigged Rig", kind="rig", slot="Hornet"),
    "wasp": dict(root="Wasp - Remake Rig", kind="rig", slot="Wasp"),
    "wasp_tiny": dict(root="Wasp - Tiny Rig", kind="rig", slot="TinyWasp"),
    "worker_bee": dict(root="Bee - Worker Rig", kind="rig", slot="WorkerBee"),
    "bumblebee": dict(root="Bee - Bumblebee Rig", kind="rig", slot="Bumblebee"),
    "solitary_bee": dict(root="Bee - Solitary Rig", kind="rig", slot="SolitaryBee"),
    "hive": dict(root="Hive", kind="prop", slot="Hive",
                 clips=[("LidOpen", 1, 46, False), ("LidClose", 46, 60, False), ("LidOpenClose", 1, 60, False)]),
    "smoker": dict(root="Smoker.001", kind="prop", slot="Smoker",
                   clips=[("Squeeze", 8, 31, False), ("SqueezeTwice", 1, 60, False)]),
    "spray_bottle": dict(root="SprayBottle", kind="prop", slot="SprayBottle",
                         clips=[("Pull", 9, 26, False), ("PullTwice", 1, 60, False)]),
    "rocket": dict(root="HONEYMOON-1", kind="prop", slot="Rocket",
                   clips=[("Ignition", 1, 16, False), ("Flicker", 16, 55, True)]),
}

# rig clips that loop in game; the rest are one-shots
LOOPING = {"Hover", "Walk", "Talk", "Arm Swing", "Thorn Spin", "Thorn Bob"}

LIGHT = Vector((0.35, -0.55, 0.76)).normalized()  # from the front (-Y), above, a little right


def log(*a):
    print("[export_dart]", *a, flush=True)


# ------------------------------------------------------------------------------------------------ helpers
def strip_suffix(name):
    return re.sub(r"\.\d{3}$", "", name)


def animated(ob):
    ad = ob.animation_data
    return bool(ad and (ad.action or len(ad.nla_tracks)))


def skinned(ob):
    return ob.type == "MESH" and any(m.type == "ARMATURE" for m in ob.modifiers)


def topo(objs):
    s = set(objs)
    out = []

    def visit(o):
        if o in out:
            return
        if o.parent in s:
            visit(o.parent)
        out.append(o)
    for o in objs:
        visit(o)
    return out


def keep_only(root):
    keep = {root} | set(root.children_recursive)
    for ob in list(bpy.data.objects):
        if ob not in keep:
            bpy.data.objects.remove(ob, do_unlink=True)
    # the item may sit in several collections; make sure everything is in the scene
    sc = bpy.context.scene
    for ob in keep:
        if sc.objects.get(ob.name) is None:
            sc.collection.objects.link(ob)
        ob.hide_viewport = False
        ob.hide_render = False
        ob.hide_set(False)
    return list(keep)


def unparent_root(root):
    mb = root.matrix_basis.copy()
    root.parent = None
    root.matrix_basis = mb
    bpy.context.view_layer.update()


def rename(objs, root, kind, item):
    if kind == "rig":
        root.name = "Rig"
        for ob in objs:
            if ob.type == "MESH" and ob.parent == root:
                ob.name = "Mesh"
        return
    root.name = strip_suffix(root.name)
    for ob in objs:
        if ob is root:
            continue
        want = strip_suffix(ob.name)
        if want != ob.name and bpy.data.objects.get(want) is None:
            ob.name = want


def single_user(ob):
    if ob.data is not None and ob.data.users > 1:
        ob.data = ob.data.copy()


def apply_modifiers(ob):
    """Bakes every non-armature modifier into the mesh (bevel widths are in object units, so before the scale)."""
    mods = [m for m in ob.modifiers if m.type != "ARMATURE"]
    if not mods:
        return
    arm = [(m.name, m.object) for m in ob.modifiers if m.type == "ARMATURE"]
    for m in ob.modifiers:
        m.show_viewport = m.type != "ARMATURE"
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(ob.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
    old = ob.data
    ob.data = me
    me.name = old.name
    for m in mods:
        ob.modifiers.remove(m)
    for m in ob.modifiers:
        m.show_viewport = True
    if old.users == 0:
        bpy.data.meshes.remove(old)


def apply_transforms(objs):
    """Unanimated meshes (and skinned meshes, relative to their rig) end with identity rotation/scale; their
    children keep their world placement."""
    world = {ob: ob.matrix_world.copy() for ob in objs}
    for ob in topo(objs):
        w = world[ob]
        target = w
        if ob.type == "MESH" and not animated(ob):
            single_user(ob)
            apply_modifiers(ob)
            if skinned(ob) and ob.parent is not None:
                target = world[ob.parent] if ob.parent in world else ob.parent.matrix_world.copy()
            else:
                loc, _, _ = w.decompose()
                target = Matrix.Translation(loc)
            delta = target.inverted() @ w
            if not _is_identity(delta):
                ob.data.transform(delta)
                ob.data.update()
        elif ob.type == "MESH":
            single_user(ob)
            apply_modifiers(ob)
        ob.matrix_world = target
        bpy.context.view_layer.update()


def _is_identity(m, eps=1e-7):
    return all(abs(m[i][j] - (1.0 if i == j else 0.0)) < eps for i in range(4) for j in range(4))


def join(name, members, parent, anchor=None):
    """Joins meshes into one object called name, parented to parent, pivot at anchor (world) or the first member."""
    members = [m for m in members if m is not None and m.type == "MESH"]
    if not members:
        return None
    for m in members:
        single_user(m)
        apply_modifiers(m)
    first = members[0]
    pivot = anchor if anchor is not None else first.matrix_world.to_translation()
    with bpy.context.temp_override(active_object=first, object=first, selected_objects=members,
                                   selected_editable_objects=members):
        bpy.ops.object.join()
    ob = first
    w = ob.matrix_world.copy()
    target = Matrix.Translation(pivot)
    ob.data.transform(target.inverted() @ w)
    ob.parent = parent
    ob.matrix_world = target
    ob.name = name
    ob.data.name = name
    bpy.context.view_layer.update()
    return ob


# ------------------------------------------------------------------------------------------------ the hive
def prepare_hive(objs):
    """Stand / BoxLower / BoxUpper / Lid / Combs as single meshes (fewer draw calls; HiveBody tints them), Door, DoorSlot,
    EntranceReducer, the anchors and the Lid animation kept, plus a HeatGlow box (the source has only an empty)."""
    D = bpy.data
    lift = D.objects["LiftRoot"]
    stand = D.objects["Stand"]
    lid = D.objects["Lid"]
    combs = D.objects.get("Comb frames")
    upper = D.objects["BoxUpper"]
    lower = D.objects["BoxLower"]
    keep_alone = {"DoorSlot", "EntranceReducer", "BoxUpper", "BoxLower"}

    # Stand: the trestle under the hive (a mesh object called Stand replaces the empty)
    stand_meshes = [o for o in stand.children_recursive if o.type == "MESH"]
    st_parent, st_loc = stand.parent, stand.matrix_world.to_translation()
    stand.name = "Stand_empty"
    st = join("Stand", stand_meshes, st_parent, st_loc)

    # Lid: one mesh object called Lid, carrying the empty's transform and the Lid Open action
    lid_meshes = [o for o in lid.children_recursive if o.type == "MESH"]
    act, slot = lid.animation_data.action, lid.animation_data.action_slot
    lid_mb = lid.matrix_basis.copy()
    lid_world = lid.matrix_world.copy()
    lid.name = "Lid_empty"
    lid.animation_data.action = None
    joined = join("LidMesh", lid_meshes, None, lid_world.to_translation())
    joined.data.transform(lid_world.inverted() @ joined.matrix_world)  # into the lid's own space
    new_lid = D.objects.new("Lid", joined.data)
    for c in joined.users_collection:
        c.objects.link(new_lid)
    new_lid.parent = lift
    new_lid.matrix_basis = lid_mb
    new_lid.animation_data_create()
    new_lid.animation_data.action = act
    try:
        new_lid.animation_data.action_slot = slot
    except Exception:
        pass
    D.objects.remove(joined, do_unlink=True)

    # Combs inside the box (show when the lid opens)
    comb_ob = None
    if combs is not None:
        comb_meshes = [o for o in combs.children_recursive if o.type == "MESH"]
        cl = combs.matrix_world.to_translation()
        combs.name = "Combs_empty"
        comb_ob = join("Combs", comb_meshes, lift, cl)

    # BoxLower: the body plus every other small static part under LiftRoot (rails, joints, plate, landing board...)
    rest = [o for o in lift.children if o.type == "MESH" and o.name not in keep_alone and o is not new_lid
            and o is not comb_ob and o is not st and not animated(o)]
    lower_loc = lower.matrix_world.to_translation()
    join("BoxLower", [lower] + rest, lift, lower_loc)

    # HeatGlow: a box 2 cm around both boxes, pivot = the source empty
    hg = D.objects.get("HeatGlow")
    # BoxLower now includes the landing board: the footprint comes from BoxUpper, the bottom from BoxLower
    ub = [upper.matrix_world @ Vector(c) for c in upper.bound_box]
    ux = (min(p.x for p in ub), max(p.x for p in ub))
    uy = (min(p.y for p in ub), max(p.y for p in ub))
    zmin = min((lower.matrix_world @ Vector(c)).z for c in lower.bound_box)
    lo = Vector((ux[0] - 0.02, uy[0] - 0.02, zmin - 0.005))
    hi = Vector((ux[1] + 0.02, uy[1] + 0.02, max(p.z for p in ub) + 0.02))
    pivot = hg.matrix_world.to_translation() if hg else (lo + hi) / 2
    me = D.meshes.new("HeatGlow")
    c = [Vector((x, y, z)) - pivot for z in (lo.z, hi.z) for y in (lo.y, hi.y) for x in (lo.x, hi.x)]
    faces = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    me.from_pydata([tuple(v) for v in c], [], faces)
    me.update()
    me.materials.append(D.materials.new("HEATGLOW"))  # Unity: Materials/VBS_Glow (HiveBody tints it)
    glow = D.objects.new("HeatGlow_mesh", me)
    bpy.context.scene.collection.objects.link(glow)
    if hg is not None:
        glow.parent = hg.parent
        glow.matrix_world = Matrix.Translation(pivot)
        hg_children = list(hg.children)
        hg.name = "HeatGlow_empty"
        for ch in hg_children:
            mw = ch.matrix_world.copy()
            ch.parent = glow
            ch.matrix_world = mw
        D.objects.remove(hg, do_unlink=True)
    else:
        glow.parent = lift
        glow.matrix_world = Matrix.Translation(pivot)
    glow.name = "HeatGlow"

    for n in ("Stand_empty", "Lid_empty", "Combs_empty"):
        o = D.objects.get(n)
        if o is not None:
            for ch in list(o.children):
                mw = ch.matrix_world.copy()
                ch.parent = lift
                ch.matrix_world = mw
            D.objects.remove(o, do_unlink=True)
    bpy.context.view_layer.update()


# ------------------------------------------------------------------------------------------------ colours
def mat_kind(mat):
    """('tex', image) | ('wing', None) | ('glow', None) | ('vc', None)"""
    if mat is None:
        return "vc", None
    n = mat.name.lower()
    if mat.name == "HEATGLOW":
        return "heatglow", None
    img = None
    if mat.node_tree:
        for nd in mat.node_tree.nodes:
            if nd.type == "TEX_IMAGE" and nd.image is not None:
                img = nd.image
    if img is not None:
        return "tex", img
    if "wing" in n:
        return "wing", None
    return "vc", None


def base_color(mat):
    if mat is None:
        return (0.8, 0.8, 0.8)
    if mat.node_tree:
        b = next((nd for nd in mat.node_tree.nodes if nd.type == "BSDF_PRINCIPLED"), None)
        if b is not None:
            c = b.inputs["Base Color"].default_value
            return (c[0], c[1], c[2])
    c = mat.diffuse_color
    return (c[0], c[1], c[2])


def bake_colors(objs, glow_objects=()):
    """Linear vertex colour per face corner = material colour (white for printed textures) x soft light; then
    collapse the material slots to the shared kinds."""
    D = bpy.data
    vc = D.materials.get("VC") or D.materials.new("VC")
    wing = D.materials.get("VC_Wing") or D.materials.new("VC_Wing")
    glow = D.materials.get("VC_Glow") or D.materials.new("VC_Glow")
    textures = {}
    for ob in objs:
        if ob.type != "MESH":
            continue
        me = ob.data
        if len(me.polygons) == 0:
            continue
        mats = [s.material for s in ob.material_slots] or [None]
        kinds = [mat_kind(m) for m in mats]
        cols = [(1.0, 1.0, 1.0) if k[0] in ("tex", "heatglow") else base_color(m) for m, k in zip(mats, kinds)]
        # normals in the object's space = rest pose world space (transforms applied); rotate for anything else
        rot = ob.matrix_world.to_3x3().normalized()
        for a in list(me.color_attributes):
            me.color_attributes.remove(a)
        attr = me.color_attributes.new("Col", "FLOAT_COLOR", "CORNER")
        data = [0.0] * (len(me.loops) * 4)
        cn = me.corner_normals if hasattr(me, "corner_normals") else None
        is_glow = ob.name in glow_objects
        for p in me.polygons:
            mi = min(p.material_index, len(cols) - 1)
            base = cols[mi]
            k = kinds[mi][0]
            for li in p.loop_indices:
                n = (rot @ (cn[li].vector if cn is not None else p.normal)).normalized()
                if k == "heatglow":
                    s = 1.0
                elif is_glow:
                    s = 0.85 + 0.15 * max(0.0, n.dot(LIGHT))
                else:
                    lam = max(0.0, n.dot(LIGHT))
                    sky = 0.5 + 0.5 * n.z
                    s = 0.42 + 0.48 * lam + 0.22 * sky
                data[li * 4:li * 4 + 4] = [min(1.0, base[0] * s), min(1.0, base[1] * s), min(1.0, base[2] * s), 1.0]
        attr.data.foreach_set("color", data)
        me.color_attributes.active_color = attr
        try:
            me.color_attributes.render_color_index = 0
        except Exception:
            pass

        # collapse materials
        targets = []
        for m, (k, img) in zip(mats, kinds):
            if is_glow:
                t = glow
            elif k == "tex":
                t = textures.get(img.name)
                if t is None:
                    t = D.materials.new("TEX_" + re.sub(r"[^A-Za-z0-9_]+", "_", os.path.splitext(img.name)[0]))
                    t["image"] = img.name
                    textures[img.name] = t
            elif k == "wing":
                t = wing
            elif k == "heatglow":
                t = m
            else:
                t = vc
            targets.append(t)
        uniq = []
        for t in targets:
            if t not in uniq:
                uniq.append(t)
        remap = [uniq.index(t) for t in targets]
        idx = [0] * len(me.polygons)
        me.polygons.foreach_get("material_index", idx)
        idx = [remap[min(i, len(remap) - 1)] for i in idx]
        me.materials.clear()
        for t in uniq:
            me.materials.append(t)
        me.polygons.foreach_set("material_index", idx)
        me.update()
    return textures


def write_textures(textures, out_dir):
    files = {}
    for img_name, mat in textures.items():
        img = bpy.data.images[img_name]
        fn = re.sub(r"[^A-Za-z0-9_.]+", "_", os.path.splitext(img_name)[0]) + ".png"
        path = os.path.join(out_dir, fn)
        if img.packed_file is not None and img.file_format == "PNG":
            with open(path, "wb") as f:
                f.write(img.packed_file.data)
        else:
            img.filepath_raw = path
            img.file_format = "PNG"
            img.save()
        files[mat.name] = fn
    return files


# ------------------------------------------------------------------------------------------------ export
def bounds(objs):
    lo = Vector((1e9,) * 3)
    hi = Vector((-1e9,) * 3)
    for ob in objs:
        if ob.type != "MESH":
            continue
        for c in ob.bound_box:
            p = ob.matrix_world @ Vector(c)
            lo = Vector(map(min, lo, p))
            hi = Vector(map(max, hi, p))
    # Blender (x, y, z) -> Unity size (x, z, y): width, height, length
    size = hi - lo
    return {"width": round(size.x, 4), "height": round(size.z, 4), "length": round(size.y, 4),
            "min_z": round(lo.z, 4)}


def tri_count(objs):
    n = 0
    for ob in objs:
        if ob.type == "MESH":
            ob.data.calc_loop_triangles()
            n += len(ob.data.loop_triangles)
    return n


def export_item(item, cfg, src, out_root):
    bpy.ops.wm.open_mainfile(filepath=src)
    sc = bpy.context.scene
    sc.render.fps = FPS
    sc.render.fps_base = 1.0
    root = bpy.data.objects[cfg["root"]]
    objs = keep_only(root)
    unparent_root(root)
    kind = cfg["kind"]
    rename(objs, root, kind, item)
    objs = [root] + list(root.children_recursive)
    sc.frame_set(1)

    clips = []
    if kind == "rig":
        ad = root.animation_data
        # the rest pose under the active action is what the FBX bind pose shows: use the rest pose
        for tr in ad.nla_tracks:
            tr.mute = False
            tr.is_solo = False
            for s in tr.strips:
                s.mute = False
                clips.append({"name": s.name, "first": int(round(s.frame_start)), "last": int(round(s.frame_end)),
                              "loop": s.name in LOOPING})
        ad.action = None
        for pb in root.pose.bones:
            pb.matrix_basis = Matrix.Identity(4)
        sc.frame_set(1)
    apply_transforms(objs)
    if item == "hive":
        prepare_hive(objs)
    objs = [root] + list(root.children_recursive)
    glow_objects = {"Flame"} if item == "rocket" else set()
    textures = bake_colors(objs, glow_objects)

    out_dir = os.path.join(out_root, item)
    os.makedirs(out_dir, exist_ok=True)
    tex_files = write_textures(textures, out_dir)

    if kind == "prop":
        sc.name = "Take"
        sc.frame_start = 1
        sc.frame_end = max(c[2] for c in cfg["clips"])
        clips = [{"name": n, "first": a, "last": b, "loop": lp} for (n, a, b, lp) in cfg["clips"]]
        sc.frame_set(1)
    path = os.path.join(out_dir, item + ".fbx")
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=False, object_types={"ARMATURE", "EMPTY", "MESH"}, global_scale=1.0,
        apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y",
        bake_space_transform=False, use_mesh_modifiers=True, mesh_smooth_type="OFF", colors_type="LINEAR",
        prioritize_active_color=True, use_tspace=False, add_leaf_bones=False, primary_bone_axis="Y",
        secondary_bone_axis="X", armature_nodetype="NULL", use_armature_deform_only=False,
        bake_anim=True, bake_anim_use_all_bones=True, bake_anim_use_nla_strips=(kind == "rig"),
        bake_anim_use_all_actions=False, bake_anim_force_startend_keying=True, bake_anim_step=1.0,
        bake_anim_simplify_factor=0.0, path_mode="STRIP", embed_textures=False, use_custom_props=False)

    mats = {}
    for ob in objs:
        if ob.type == "MESH":
            for s in ob.material_slots:
                m = s.material
                if m is None or m.name in mats:
                    continue
                if m.name.startswith("TEX_"):
                    mats[m.name] = {"kind": "tex", "texture": tex_files.get(m.name)}
                elif m.name == "VC_Wing":
                    mats[m.name] = {"kind": "wing"}
                elif m.name == "VC_Glow":
                    mats[m.name] = {"kind": "glow"}
                elif m.name == "HEATGLOW":
                    mats[m.name] = {"kind": "heatglow"}
                else:
                    mats[m.name] = {"kind": "vc"}
    entry = {
        "item": item, "slot": cfg["slot"], "kind": kind, "fbx": item + "/" + item + ".fbx", "fps": FPS,
        "size": bounds(objs), "tris": tri_count(objs), "clips": clips,
        "materials": [dict(name=k, kind=v["kind"], texture=v.get("texture") or "") for k, v in mats.items()],
        "objects": sorted(o.name for o in objs),
        "bones": sorted(b.name for b in root.data.bones) if root.type == "ARMATURE" else [],
    }
    log(item, "->", path, entry["size"], entry["tris"], "tris,", len(clips), "clips,", len(mats), "materials")
    return entry


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--item", action="append")
    a = p.parse_args(argv)
    src = bpy.data.filepath
    if not src:
        raise SystemExit("open the .blend: blender -b <file> --python export_dart.py -- --out DIR")
    if bpy.app.version < (5, 3, 0) and bpy.data.version >= (5, 3, 0):
        log("WARNING: Blender %s reads a %s file with data loss; use Blender 5.3" %
            (bpy.app.version_string, ".".join(map(str, bpy.data.version))))
    out = os.path.abspath(a.out)
    os.makedirs(out, exist_ok=True)
    items = a.item or list(ITEMS)
    manifest = {"source": os.path.basename(src), "blender": bpy.app.version_string,
                "file_version": list(bpy.data.version), "items": []}
    for item in items:
        manifest["items"].append(export_item(item, ITEMS[item], src, out))
    with open(os.path.join(out, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=1)
    log("wrote", os.path.join(out, "manifest.json"))


main()
