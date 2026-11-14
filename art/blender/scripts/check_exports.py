"""Sanity-check the exported files: reads every <item>.glb (JSON chunk) and <item>.fbx (Blender's own FBX parser)
and prints node names, pivots in UNITY space, and whether anything carries a rotation or scale.

    blender -b --factory-startup --python check_exports.py -- --out DIR [--only hive,beeswax]

Unity space here = glTF/FBX space with X negated (what glTFast and Unity's FBX importer do): +Y up, +Z = front."""
import argparse
import json
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from build_all import ITEMS  # noqa: E402


def glb_nodes(path):
    with open(path, "rb") as fh:
        data = fh.read()
    ln = struct.unpack_from("<I", data, 12)[0]
    js = json.loads(data[20:20 + ln])
    out = {}

    def walk(i, parent_t):
        n = js["nodes"][i]
        t = n.get("translation", [0, 0, 0])
        wt = [parent_t[k] + t[k] for k in range(3)]
        out[n.get("name", "?")] = dict(t=[-wt[0], wt[1], wt[2]], rot="rotation" in n, scale="scale" in n)
        for c in n.get("children", []):
            walk(c, wt)
    for r in js["scenes"][0]["nodes"]:
        walk(r, [0, 0, 0])
    return out, js


def fbx_nodes(path):
    try:
        from io_scene_fbx import parse_fbx
    except ImportError:
        import addon_utils
        addon_utils.enable("io_scene_fbx")
        from io_scene_fbx import parse_fbx
    root, _ver = parse_fbx.parse(path)
    out, axes = {}, {}
    for el in root.elems:
        if el.id == b"GlobalSettings":
            for p70 in el.elems:
                for p in p70.elems:
                    if p.props and p.props[0] in (b"UpAxis", b"UpAxisSign", b"FrontAxis", b"FrontAxisSign",
                                                  b"UnitScaleFactor"):
                        axes[p.props[0].decode()] = p.props[-1]
        if el.id == b"Objects":
            for ob in el.elems:
                if ob.id != b"Model":
                    continue
                name = ob.props[1].split(b"\x00")[0].decode()
                t, r, s = [0, 0, 0], [0, 0, 0], [1, 1, 1]
                for p70 in ob.elems:
                    for p in p70.elems:
                        if p.props and p.props[0] == b"Lcl Translation":
                            t = list(p.props[4:7])
                        if p.props and p.props[0] == b"Lcl Rotation":
                            r = list(p.props[4:7])
                        if p.props and p.props[0] == b"Lcl Scaling":
                            s = list(p.props[4:7])
                out[name] = dict(t_local=[round(-t[0], 4), round(t[1], 4), round(t[2], 4)],
                                 rot=any(abs(v) > 1e-4 for v in r), scale=[round(v, 4) for v in s])
    return out, axes


def roundtrip(path):
    """Import a file into an empty scene; return {object name: world position (Blender Z-up)} and mesh bounds."""
    import bpy
    from mathutils import Vector
    bpy.ops.wm.read_factory_settings(use_empty=True)
    if path.endswith(".glb"):
        bpy.ops.import_scene.gltf(filepath=path)
    else:
        bpy.ops.import_scene.fbx(filepath=path)
    bpy.context.view_layer.update()
    pos, pts = {}, []
    for ob in bpy.context.scene.objects:
        pos[ob.name] = ob.matrix_world.translation.copy()
        if ob.type == "MESH":
            pts += [ob.matrix_world @ Vector(c) for c in ob.bound_box]
    lo = Vector([min(p[i] for p in pts) for i in range(3)])
    hi = Vector([max(p[i] for p in pts) for i in range(3)])
    return pos, lo, hi


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", default=os.path.normpath(os.path.join(HERE, "..", "out")))
    p.add_argument("--only", default="")
    a, _ = p.parse_known_args(argv)
    bad = 0
    for item in [i for i in a.only.split(",") if i] or ITEMS:
        glb = os.path.join(a.out, item + ".glb")
        if not os.path.exists(glb):
            continue
        nodes, js = glb_nodes(glb)
        mins = [min(acc["min"][1] for acc in js["accessors"] if acc.get("type") == "VEC3" and "min" in acc)]
        flagged = [n for n, d in nodes.items() if d["rot"] or d["scale"]]
        print("\n== %s.glb  nodes=%d  materials=%s  rotated/scaled nodes=%s" % (
            item, len(nodes), [m["name"] for m in js.get("materials", [])], flagged or "none"))
        for n, d in nodes.items():
            print("   %-18s unity pos %s" % (n, [round(v, 4) for v in d["t"]]))
        bad += len(flagged)
        fbx = os.path.join(a.out, item + ".fbx")
        if os.path.exists(fbx):
            fn, axes = fbx_nodes(fbx)
            fl = [n for n, d in fn.items() if d["rot"] or any(abs(v - 1) > 1e-4 for v in d["scale"])]
            print("   fbx: axes %s  models=%d  rotated/scaled=%s" % (axes, len(fn), fl or "none"))
            bad += len(fl)
            gp, glo, ghi = roundtrip(glb)
            fp, flo, fhi = roundtrip(fbx)
            worst = max([(gp[n] - fp[n]).length for n in gp if n in fp] + [(glo - flo).length, (ghi - fhi).length])
            missing = sorted(set(gp) ^ set(fp))
            print("   round trip glb vs fbx: worst offset %.5f m, bounds min %s max %s%s" % (
                worst, [round(v, 3) for v in glo], [round(v, 3) for v in ghi],
                ", name mismatch %s" % missing if missing else ""))
            if worst > 1e-3 or missing:
                bad += 1
    print("\nCHECK %s" % ("OK" if bad == 0 else "found %d problems" % bad))


main()
