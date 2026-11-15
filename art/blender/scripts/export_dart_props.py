"""Export dartiros' "Ten New Assets.blend" props (union badge, hive tool, frame rest, queen bee, queen cage, nectar droplet,
watering can, flower pots, sample tray, Moon flag, plus the human Earth flag) to Unity FBX.

    blender -b "art/blender/Ten New Assets.blend" --python art/blender/scripts/export_dart_props.py -- --out DIR [--item queen_bee ...]

Needs Blender 5.3 (the file version). One FBX per item (DIR/<item>/<item>.fbx, printed textures next to it) plus
DIR/manifest.json, which the Unity importer (Assets/VirtualBeeSwarm/NewProps/Editor/NewPropsImport.cs) reads.

Per item, on a fresh load of the .blend:
  * only the item's hierarchy stays; its showcase "NN ... display" empty becomes the root at identity (native size, meters)
  * names lose Blender's .001 suffixes; the queen's rig becomes Rig > Mesh
  * unanimated meshes are baked unrotated and unscaled at their pivot (shape keys move with them); animated objects keep
    their transforms (the watering can tilts around its placement root, soil and the pour stream grow from scale 0.001)
  * unanimated leaf meshes under one parent are joined ("<parent> Parts"): the frame rest goes from 23 meshes to 2
  * colours: each face's material colour (the viewport colour where the Principled base is left at its default, as on the
    queen, the crown and the soil) times the same soft top-front light as export_dart.py, baked into a linear vertex colour;
    materials collapse to VC, VC_Wing (queen wings) and one per printed texture
  * clips: the queen's muted NLA tracks (Hover, Blink) are unmuted and become one take each; every other item's object and
    shape-key actions (all on frames 1-60) bake into one take that the importer splits into the clips below
  * axes: Blender -Y front, Z up -> Unity +Z front, Y up (the importer bakes the axis conversion)
"""
import argparse
import json
import os
import re
import sys

import bpy
from mathutils import Matrix, Vector

FPS = 30

# item -> showcase empty (the root), kind, Unity slot, clips (name, first, last, loop) for props
ITEMS = {
    "union_badge": dict(root="01 Badge display", kind="prop", slot="UnionBadge", name="UnionBadge",
                        clips=[("Settle", 1, 60, False)]),
    "hive_tool": dict(root="02 Hive Tool display", kind="prop", slot="HiveTool", name="HiveTool",
                      clips=[("Lever", 1, 60, False)]),
    "frame_rest": dict(root="03 Frame Rest display", kind="prop", slot="FrameRest", name="FrameRest",
                       clips=[("InspectComb", 1, 60, False)]),
    "queen_bee": dict(root="Queen Bee Rig", kind="rig", slot="QueenBee"),
    "queen_cage": dict(root="05 Queen Cage display", kind="prop", slot="QueenCage", name="QueenCage",
                       clips=[("OpenPlug", 1, 60, False)]),
    "nectar_droplet": dict(root="06 Nectar Droplet display", kind="prop", slot="NectarDroplet", name="NectarDroplet",
                           clips=[("Wobble", 1, 60, False)]),
    "watering_can": dict(root="07 Watering Can display", kind="prop", slot="WateringCan", name="WateringCan",
                         clips=[("Pour", 1, 60, False)]),
    "flower_pots": dict(root="08 Flower Pots display", kind="prop", slot="FlowerPots", name="FlowerPots",
                        clips=[("Fill", 1, 60, False)]),
    "sample_tray": dict(root="09 Sample Tray display", kind="prop", slot="SampleTray", name="SampleTray",
                        clips=[("Inspect", 1, 60, False)]),
    "moon_flag": dict(root="10 Moon Flag display", kind="prop", slot="MoonFlag", name="MoonFlag",
                      clips=[("Flutter", 1, 60, True)]),
    "earth_flag": dict(root="11 Human Earth Flag display", kind="prop", slot="EarthFlag", name="EarthFlag",
                       clips=[("Flutter", 1, 60, True)]),
}

LOOPING = {"Hover"}  # rig clips that loop in game

LIGHT = Vector((0.35, -0.55, 0.76)).normalized()  # from the front (-Y), above, a little right (as export_dart.py)
DEFAULT_BASE = (0.8, 0.8, 0.8)


def log(*a):
    print("[export_dart_props]", *a, flush=True)


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
    sc = bpy.context.scene
    for ob in keep:
        if sc.objects.get(ob.name) is None:
            sc.collection.objects.link(ob)
        ob.hide_viewport = False
        ob.hide_render = False
        ob.hide_set(False)
    return list(keep)


def single_user(ob):
    if ob.data is not None and ob.data.users > 1:
        ob.data = ob.data.copy()


def apply_modifiers(ob):
    """Bakes every non-armature modifier into the mesh (bevel / weighted normal; none of them sit on a shape-keyed mesh)."""
    mods = [m for m in ob.modifiers if m.type != "ARMATURE"]
    if not mods:
        return
    if ob.data.shape_keys is not None:
        log("WARNING:", ob.name, "has shape keys and modifiers; modifiers left unapplied")
        return
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


def _is_identity(m, eps=1e-7):
    return all(abs(m[i][j] - (1.0 if i == j else 0.0)) < eps for i in range(4) for j in range(4))


def apply_transforms(objs):
    """Unanimated meshes (and the skinned mesh, relative to its rig) end with identity rotation/scale; their children
    keep their world placement. Animated objects keep their transforms (their curves are relative to them)."""
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
                ob.data.transform(delta, shape_keys=True)
                ob.data.update()
        elif ob.type == "MESH":
            single_user(ob)
            apply_modifiers(ob)
        ob.matrix_world = target
        bpy.context.view_layer.update()


def join_static(objs):
    """Fewer draw calls on the Quest: the unanimated leaf meshes under one parent become one mesh (the frame rest's 13
    stand parts and 7 comb parts, the queen cage's frame). Animated objects, shape-keyed and skinned meshes and anything
    with children (the pots carry their soil and plant anchor) stay as they are."""
    groups = {}
    for ob in objs:
        if (ob.type == "MESH" and ob.parent is not None and not animated(ob) and not ob.children and not skinned(ob)
                and ob.data.shape_keys is None):
            groups.setdefault(ob.parent, []).append(ob)
    joined = 0
    for parent, members in groups.items():
        if len(members) < 2:
            continue
        for m in members:
            single_user(m)
        first = members[0]
        with bpy.context.temp_override(active_object=first, object=first, selected_objects=members,
                                       selected_editable_objects=members):
            bpy.ops.object.join()
        first.name = strip_suffix(parent.name) + " Parts"
        first.data.name = first.name
        joined += len(members) - 1
    bpy.context.view_layer.update()
    return joined


# ------------------------------------------------------------------------------------------------ colours
def mat_kind(mat):
    """('tex', image) | ('wing', None) | ('vc', None)"""
    if mat is None:
        return "vc", None
    img = None
    if mat.node_tree:
        for nd in mat.node_tree.nodes:
            if nd.type == "TEX_IMAGE" and nd.image is not None:
                img = nd.image
    if img is not None:
        return "tex", img
    if "wing" in mat.name.lower():
        return "wing", None
    return "vc", None


def base_color(mat):
    """The Principled base colour; Daniel's queen, crown, soil and water materials leave it at the default 0.8 grey
    and carry their colour as the viewport colour, so that one wins then."""
    if mat is None:
        return DEFAULT_BASE
    c = None
    if mat.node_tree:
        b = next((nd for nd in mat.node_tree.nodes if nd.type == "BSDF_PRINCIPLED"), None)
        if b is not None:
            v = b.inputs["Base Color"].default_value
            c = (v[0], v[1], v[2])
    d = tuple(mat.diffuse_color[:3])
    if c is None or (all(abs(x - y) < 1e-3 for x, y in zip(c, DEFAULT_BASE)) and
                     not all(abs(x - y) < 1e-3 for x, y in zip(d, DEFAULT_BASE))):
        return d
    return c


def bake_colors(objs, glow_objects=()):
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
        cols = [(1.0, 1.0, 1.0) if k[0] == "tex" else base_color(m) for m, k in zip(mats, kinds)]
        rot = ob.matrix_world.to_3x3().normalized()
        for a in list(me.color_attributes):
            me.color_attributes.remove(a)
        attr = me.color_attributes.new("Col", "FLOAT_COLOR", "CORNER")
        data = [0.0] * (len(me.loops) * 4)
        cn = me.corner_normals if hasattr(me, "corner_normals") else None
        is_glow = strip_suffix(ob.name) in glow_objects
        for p in me.polygons:
            mi = min(p.material_index, len(cols) - 1)
            base = cols[mi]
            for li in p.loop_indices:
                n = (rot @ (cn[li].vector if cn is not None else p.normal)).normalized()
                if is_glow:
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
        if min(abs(x) for x in ob.matrix_world.to_scale()) < 0.01:
            continue  # grows in from scale 0.001 (soil, the pour stream): not part of the rest size
        for c in ob.bound_box:
            p = ob.matrix_world @ Vector(c)
            lo = Vector(map(min, lo, p))
            hi = Vector(map(max, hi, p))
    size = hi - lo
    return {"width": round(size.x, 4), "height": round(size.z, 4), "length": round(size.y, 4), "min_z": round(lo.z, 4)}


def tri_count(objs):
    n = 0
    for ob in objs:
        if ob.type == "MESH":
            ob.data.calc_loop_triangles()
            n += len(ob.data.loop_triangles)
    return n


def anchors(objs):
    """Empties the game uses (plant anchors, spout), in the root's space (Blender axes: x, y = back, z = up)."""
    out = {}
    for ob in objs:
        if ob.type == "EMPTY" and ob.parent is not None:
            p = ob.matrix_world.to_translation()
            out[ob.name] = [round(p.x, 4), round(p.y, 4), round(p.z, 4)]
    return out


def export_item(item, cfg, src, out_root):
    bpy.ops.wm.open_mainfile(filepath=src)
    sc = bpy.context.scene
    sc.render.fps = FPS
    sc.render.fps_base = 1.0
    root = bpy.data.objects[cfg["root"]]
    objs = keep_only(root)
    # the showcase empty (or the rig) sits at a display scale and rotation: native size, unrotated, at the origin
    root.parent = None
    root.matrix_world = Matrix.Identity(4)
    bpy.context.view_layer.update()
    kind = cfg["kind"]
    if kind == "rig":
        root.name = "Rig"
        for ob in objs:
            if ob.type == "MESH" and ob.parent == root:
                ob.name = "Mesh"
    else:
        root.name = cfg["name"]
        for ob in objs:
            if ob is root:
                continue
            want = strip_suffix(ob.name)
            if want != ob.name and bpy.data.objects.get(want) is None:
                ob.name = want
    objs = [root] + list(root.children_recursive)
    sc.frame_set(1)

    clips = []
    if kind == "rig":
        ad = root.animation_data
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
    objs = [root] + list(root.children_recursive)
    if kind == "prop":
        join_static(objs)
        objs = [root] + list(root.children_recursive)
    textures = bake_colors(objs, cfg.get("glow", set()))

    out_dir = os.path.join(out_root, item)
    os.makedirs(out_dir, exist_ok=True)
    tex_files = write_textures(textures, out_dir)

    if kind == "prop":
        sc.name = "Take"
        sc.frame_start = 1
        sc.frame_end = max(c[2] for c in cfg["clips"])
        clips = [{"name": n, "first": a, "last": b, "loop": lp} for (n, a, b, lp) in cfg["clips"]]
        sc.frame_set(1)
    else:
        sc.frame_start = 1
        sc.frame_end = max(c["last"] for c in clips)
    size = bounds(objs)
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
                else:
                    mats[m.name] = {"kind": "vc"}
    shape_keyed = sorted(o.name for o in objs if o.type == "MESH" and o.data.shape_keys is not None)
    entry = {
        "item": item, "slot": cfg["slot"], "kind": kind, "fbx": item + "/" + item + ".fbx", "fps": FPS,
        "size": size, "tris": tri_count(objs), "clips": clips,
        "materials": [dict(name=k, kind=v["kind"], texture=v.get("texture") or "") for k, v in mats.items()],
        "objects": sorted(o.name for o in objs),
        "anchors": [dict(name=k, pos=v) for k, v in sorted(anchors(objs).items())],
        "blend_shapes": shape_keyed,
        "bones": sorted(b.name for b in root.data.bones) if root.type == "ARMATURE" else [],
    }
    log(item, "->", path, entry["size"], entry["tris"], "tris,", len(clips), "clips,", len(mats), "materials",
        ("shape keys on " + ", ".join(shape_keyed)) if shape_keyed else "")
    return entry


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--item", action="append")
    a = p.parse_args(argv)
    src = bpy.data.filepath
    if not src:
        raise SystemExit("open the .blend: blender -b <file> --python export_dart_props.py -- --out DIR")
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
