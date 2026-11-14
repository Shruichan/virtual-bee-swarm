"""Copy the art-director build into final/<item>/ (+ CC0 Kenney VFX sprites, shared palette, source scripts).

    python3 -P assemble.py STAGING_DIR      (STAGING_DIR = ~/immerse-the-bay/art-staging)
"""
import glob
import os
import shutil
import sys

ITEMS = ["beeswax", "worker_bee", "hive", "smoker", "honey_jar", "syrup_bottle", "deep_bowl", "pebble_dish", "wasp",
         "giant_hornet", "rocket", "portal_frame", "hexagon_tile", "moon", "earth", "flowers", "grass_tuft", "ember",
         "clipboard", "seed_packet",
         # v3.1 props (2026-11-14)
         "swatter", "spray_bottle", "crowbar", "reducer", "rain_cloud", "stamp_approved", "stamp_grievance",
         "clipboard_sign", "score_card"]

st = os.path.abspath(sys.argv[1])
# optional: python3 -P assemble.py STAGING_DIR item1,item2  (re-copy only those items)
if len(sys.argv) > 2 and sys.argv[2]:
    ITEMS = [i for i in sys.argv[2].split(",") if i]
out = os.path.join(st, "final", "_work", "out")
fin = os.path.join(st, "final")
dl = os.path.join(st, "downloaded")


def cp(src, dst):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(src, dst)


for it in ITEMS:
    d = os.path.join(fin, it)
    if os.path.isdir(d):
        shutil.rmtree(d)
    os.makedirs(d)
    for ext in ("glb", "fbx"):
        cp(os.path.join(out, "%s.%s" % (it, ext)), os.path.join(d, "%s.%s" % (it, ext)))
    cp(os.path.join(out, "blend", it + ".blend"), os.path.join(d, it + ".blend"))
    cp(os.path.join(out, "previews", it + "_hero.png"), os.path.join(d, it + "_preview.png"))
    cp(os.path.join(out, "previews", it + ".png"), os.path.join(d, it + "_turntable.png"))

# extra textures
cp(os.path.join(out, "textures", "ember.png"), os.path.join(fin, "ember", "ember_sprite.png"))
cp(os.path.join(out, "textures", "meadow_fallback.png"), os.path.join(fin, "portal_frame", "meadow_fallback.png"))
cp(os.path.join(out, "textures", "VBS_Palette.png"), os.path.join(fin, "_shared", "VBS_Palette.png"))

# CC0 Kenney particle sprites (VFX textures, not meshes)
for item, pat in (("smoker", "smoke_*.png"), ("ember", "*.png")):
    src = os.path.join(dl, item, "kenney_particle-pack")
    dst = os.path.join(fin, item, "vfx_kenney_cc0")
    for f in sorted(glob.glob(os.path.join(src, pat))):
        cp(f, os.path.join(dst, os.path.basename(f)))
    cp(os.path.join(src, "License.txt"), os.path.join(dst, "License.txt"))

# source scripts used for this build (rebuild: see _source/README.txt)
srcd = os.path.join(fin, "_source")
if os.path.isdir(srcd):
    shutil.rmtree(srcd)
shutil.copytree(os.path.join(st, "final", "_work", "scripts"), os.path.join(srcd, "scripts"),
                ignore=shutil.ignore_patterns("__pycache__"))
shutil.copytree(os.path.join(st, "final", "_work", "tools"), os.path.join(srcd, "tools"),
                ignore=shutil.ignore_patterns("__pycache__", "montage.py"))
print("ASSEMBLED", fin)
