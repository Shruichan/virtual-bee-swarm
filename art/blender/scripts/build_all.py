"""Build every item (or some), in parallel, each in its own fresh Blender process.

    blender -b --factory-startup --python build_all.py -- [--out DIR] [--only beeswax,hive] [--jobs 6]
                                                          [--no-render] [--no-fbx] [--no-blend]
or, without Blender in front:
    python3 build_all.py --blender /path/to/blender [same options]

Writes <out>/<item>.glb + .fbx, <out>/blend/<item>.blend, <out>/previews/<item>.png (+ _hero.png),
<out>/stats/<item>.json, <out>/stats.json (summary), <out>/previews/_contact_sheet.png and logs/<item>.log.
"""
import argparse
import json
import os
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor

HERE = os.path.dirname(os.path.abspath(__file__))
ITEMS = ["beeswax", "worker_bee", "hive", "smoker", "honey_jar", "syrup_bottle", "deep_bowl", "pebble_dish", "wasp",
         "giant_hornet", "rocket", "portal_frame", "hexagon_tile", "moon", "earth", "flowers", "grass_tuft", "ember",
         "clipboard", "seed_packet",
         # v3.1 props (2026-11-14)
         "swatter", "spray_bottle", "crowbar", "reducer", "rain_cloud", "stamp_approved", "stamp_grievance",
         "clipboard_sign", "score_card"]

try:
    import bpy  # noqa: F401
    IN_BLENDER = True
except ImportError:
    IN_BLENDER = False


def args():
    argv = sys.argv[sys.argv.index("--") + 1:] if (IN_BLENDER and "--" in sys.argv) else sys.argv[1:]
    p = argparse.ArgumentParser()
    p.add_argument("--out", default=os.path.normpath(os.path.join(HERE, "..", "out")))
    p.add_argument("--only", default="")
    p.add_argument("--jobs", type=int, default=6)
    p.add_argument("--blender", default=os.environ.get("BLENDER", ""))
    p.add_argument("--no-render", action="store_true")
    p.add_argument("--no-fbx", action="store_true")
    p.add_argument("--no-blend", action="store_true")
    p.add_argument("--sheet-only", action="store_true")
    a, _ = p.parse_known_args(argv)
    return a


def blender_bin(a):
    if IN_BLENDER:
        import bpy
        return bpy.app.binary_path
    if a.blender:
        return a.blender
    sys.exit("pass --blender /path/to/blender (or set BLENDER), or run this file with blender -b --python")


def build_one(blender, item, a, out):
    cmd = [blender, "-b", "--factory-startup", "--python", os.path.join(HERE, item + ".py"), "--", "--out", out]
    cmd += [f for f, on in (("--no-render", a.no_render), ("--no-fbx", a.no_fbx), ("--no-blend", a.no_blend)) if on]
    t0 = time.time()
    with open(os.path.join(out, "logs", item + ".log"), "w") as log:
        rc = subprocess.call(cmd, stdout=log, stderr=subprocess.STDOUT)
    return item, rc, time.time() - t0


def sheet(blender, out, items):
    if IN_BLENDER:
        sys.path.insert(0, HERE)
        import common
        common.contact_sheet(out, items)
    else:
        subprocess.call([blender, "-b", "--factory-startup", "--python", os.path.abspath(__file__), "--", "--out", out,
                         "--sheet-only", "--only", ",".join(items)])


def main():
    a = args()
    out = os.path.abspath(a.out)
    items = [i for i in a.only.split(",") if i] or ITEMS
    unknown = [i for i in items if i not in ITEMS]
    if unknown:
        sys.exit("unknown items: %s (known: %s)" % (unknown, ", ".join(ITEMS)))
    if a.sheet_only:
        sys.path.insert(0, HERE)
        import common
        common.contact_sheet(out, items)
        return
    os.makedirs(os.path.join(out, "logs"), exist_ok=True)
    blender = blender_bin(a)
    with ThreadPoolExecutor(max_workers=max(1, a.jobs)) as ex:
        results = list(ex.map(lambda it: build_one(blender, it, a, out), items))
    rows, failed = [], []
    for item, rc, secs in results:
        sp = os.path.join(out, "stats", item + ".json")
        if rc != 0 or not os.path.exists(sp):
            failed.append(item)
            print("FAILED %-14s (exit %d) see %s" % (item, rc, os.path.join(out, "logs", item + ".log")))
            continue
        with open(sp) as fh:
            rows.append(json.load(fh))
    print("\n%-14s %7s %7s  %-22s %s" % ("item", "tris", "budget", "size W x H x D (m)", "pivot"))
    for s in rows:
        flag = "  OVER BUDGET" if s["over_budget"] else ""
        size = " x ".join("%.3f" % v for v in s["size_unity_m"])
        print("%-14s %7d %7d  %-22s %s%s" % (s["item"], s["tris"], s["budget"], size, s["pivot"], flag))
    summary = {s["item"]: dict(tris=s["tris"], budget=s["budget"], size_unity_m=s["size_unity_m"], pivot=s["pivot"],
                               objects=[o["name"] for o in s["objects"]]) for s in rows}
    if not (a.only and os.path.exists(os.path.join(out, "stats.json"))):
        merged = summary
    else:
        with open(os.path.join(out, "stats.json")) as fh:
            merged = json.load(fh)
        merged.update(summary)
    with open(os.path.join(out, "stats.json"), "w") as fh:
        json.dump(merged, fh, indent=1)
    if not a.no_render:
        sheet(blender, out, [i for i in ITEMS if os.path.exists(os.path.join(out, "previews", i + "_hero.png"))])
    if failed:
        sys.exit(1)


if __name__ == "__main__":
    main()
