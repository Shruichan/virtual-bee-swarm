"""Re-export a hand-edited .blend with the same settings the generators use (GLB + FBX, Y-up, +Z front, meters,
no axis-conversion rotations in the FBX).

    blender -b out/blend/beeswax.blend --python scripts/export_open.py -- --out out [--item beeswax] [--no-fbx]

Exports every mesh and empty in the open file. Keep the object names and pivots from the README table if the model
replaces a Unity slot."""
import argparse
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", default=C.DEFAULT_OUT)
    p.add_argument("--item", default=os.path.splitext(os.path.basename(bpy.data.filepath or "untitled"))[0])
    p.add_argument("--no-fbx", action="store_true")
    a, _ = p.parse_known_args(argv)
    os.makedirs(a.out, exist_ok=True)
    C.REGISTRY[:] = [ob for ob in bpy.context.scene.objects if ob.type in ("MESH", "EMPTY")]
    rotated = [ob.name for ob in C.REGISTRY if any(abs(v) > 1e-6 for v in ob.rotation_euler)
               or any(abs(v - 1) > 1e-6 for v in ob.scale)]
    if rotated:
        print("NOTE: these objects carry rotation/scale (fine, but Unity will see it): %s" % rotated)
    for f in C.export(a.item, os.path.abspath(a.out), fbx=not a.no_fbx):
        print("wrote", f)


main()
