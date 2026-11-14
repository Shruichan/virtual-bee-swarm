"""Meadow-window stand-in clips: one seamless loop per window scene, rendered in Eevee from the same low-poly props and
palette as the rest of the art (hive, flowers, grass tufts, bees, wasps, pebble dish, Earth). These are rendered
stand-ins, not Orbis: the Orbis fallback layer (relay clips/, Unity StreamingAssets/<scene>.mp4) plays them when the
live stream is off or failing, and offline.

    blender -b --factory-startup --python window_clips.py -- --out DIR [--only meadow,moon] [--jobs 4]
            [--seconds 10] [--fps 30] [--samples 16] [--still] [--keep-frames]

Writes DIR/<scene>.mp4 (832x480, H.264 Main, yuv420p, a keyframe every second, no audio, +faststart, <= 3 MB) and
DIR/stills/<scene>.jpg (+ DIR/stills/contact_sheet.jpg). --still renders only the middle frame (for look checks).
Scene ids and creative brief: hive/lines/orbis_scenes.json (named scenes) and hive/orbis-relay/scenes.json (the old
backyard stages moon_0, stage_1-3, earthrise). Every motion uses whole cycles per clip, so the last frame runs
straight back into the first. The one exception is forecast_ravaged (the hook's forecast): a one-shot that ends on the
first frame of its own seamless loop, DIR/forecast_aftermath.mp4.
"""
import argparse
import math
import os
import random
import shutil
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor

import bpy
from mathutils import Euler, Matrix, Quaternion, Vector, noise

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import common as C  # noqa: E402
import earth as EARTH  # noqa: E402
import flowers as FLOWERS  # noqa: E402
import grass_tuft as GRASS  # noqa: E402
import hive as HIVE  # noqa: E402
import pebble_dish as DISH  # noqa: E402
import wasp as WASP  # noqa: E402
import worker_bee as BEE  # noqa: E402

TAU = 2 * math.pi
W, H = 832, 480
MAX_BYTES = 3 * 1000 * 1000

# Scene-only colours, appended after the art palette (only in this process; the item exports never see them).
EXTRA = [("grass_a", "#6DB552"), ("grass_b", "#4F9A3F"), ("dry_a", "#A69B6E"), ("dry_b", "#857C59"),
         ("gold_a", "#D7B655"), ("gold_b", "#B8963E"), ("bark", "#6B4A2E"), ("oak", "#4E8F3C"),
         ("oak_dark", "#3A7130"), ("snow", "#F3F7FB"), ("snow_shade", "#C8D5E4"), ("fence", "#EADFC8"),
         ("fence_dark", "#C2B396"), ("smoke", "#EEEBE4"), ("smoke_dark", "#77716C"), ("ash", "#3B3633"),
         ("flame", "#FF8A22"), ("mist", "#F7F8EE"), ("rain", "#D2E8F6"), ("sun", "#FFF7DA")]
for _k, _h in EXTRA:
    C.PALETTE.append((_k, _h))
    C.PAL[_k] = len(C.PALETTE) - 1
assert len(C.PALETTE) <= C.GRID * C.GRID
BASE_PALETTE = list(C.PALETTE)

C.MATERIALS.update({
    "veil": dict(name="VBS_Veil", emission=0.6, alpha=0.32),
    "veil_thin": dict(name="VBS_VeilThin", emission=0.8, alpha=0.16),
    "rainmat": dict(name="VBS_Rain", emission=0.9, alpha=0.45),
    "flame": dict(name="VBS_Flame", emission=1.6),
    "star": dict(name="VBS_Star", emission=6.0),
    "sunmat": dict(name="VBS_Sun", emission=12.0),
    "laser": dict(name="VBS_Laser", emission=9.0),
    "laser_halo": dict(name="VBS_LaserHalo", emission=5.0, alpha=0.3),
    "eyeglow": dict(name="VBS_EyeGlow", emission=7.0),
})

ANIM = []  # callables f(phase in [0, 1)) that pose the scene for one frame


# ============================================================================================ small helpers
def env(u, a=0.12, b=0.2):
    """0 -> 1 -> 0 over a life u in [0, 1): fade in over a, out over b (used as a scale, so nothing pops)."""
    return max(0.0, min(1.0, u / a, (1.0 - u) / b))


def wave(ph, k, p=0.0):
    return math.sin(TAU * k * ph + p)


def look(vel, level=0.6):
    """Quaternion turning a model whose front is -Y (and up +Z) to fly along vel."""
    v = Vector((vel.x, vel.y, vel.z * level))
    if v.length < 1e-6:
        return Quaternion()
    return v.normalized().to_track_quat("-Y", "Z")


def link(ob):
    bpy.context.scene.collection.objects.link(ob)
    return ob


def inst(proto, loc, scale=1.0, rot=(0, 0, 0)):
    o = proto.copy()
    o.hide_render = False
    o.parent = None
    link(o)
    o.location = loc
    o.rotation_euler = rot
    o.scale = (scale,) * 3 if isinstance(scale, (int, float)) else scale
    return o


def mover(proto, scale):
    o = inst(proto, (0, 0, -50), scale)
    o.rotation_mode = "QUATERNION"
    return o


def hide_tree(root):
    for o in [root] + list(root.children_recursive):
        o.hide_render = True


def bake(root, name):
    """Join a finalized item hierarchy into one mesh (materials kept) so it can be copied cheaply."""
    import bmesh
    bpy.context.view_layer.update()
    parts = [o for o in [root] + list(root.children_recursive) if o.type == "MESH" and not o.get("preview_hide")]
    bm = bmesh.new()
    mats = []
    inv = root.matrix_world.inverted()
    for o in parts:
        t = o.data.copy()
        t.transform(inv @ o.matrix_world)
        remap = []
        for m in t.materials:
            if m not in mats:
                mats.append(m)
            remap.append(mats.index(m))
        for p in t.polygons:
            p.material_index = remap[p.material_index] if remap else 0
        bm.from_mesh(t)
        bpy.data.meshes.remove(t)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    for m in mats:
        me.materials.append(m)
    ob = link(bpy.data.objects.new(name, me))
    ob.hide_render = True
    hide_tree(root)
    return ob


# ============================================================================================ prototypes
def proto_oak(kind):
    """Low-poly oak: trunk + ico-sphere canopy. kind: leafy | bare | frost | dusk."""
    mb = C.MB("Oak_" + kind, smooth_angle=0)
    rnd = random.Random(7)
    bark = "ash" if kind == "dusk" else "bark"
    mb.limb(bark, [(0, 0, 0), (0.05, 0, 1.0), (0.0, 0.03, 1.9)], 0.16, 0.10, seg=7)
    for a, h in ((30, 1.5), (150, 1.7), (260, 1.35), (95, 2.0)):
        ra = math.radians(a)
        mb.limb(bark, [(0, 0, h), (0.55 * math.cos(ra), 0.55 * math.sin(ra), h + 0.5),
                       (0.95 * math.cos(ra), 0.95 * math.sin(ra), h + 0.75)], 0.07, 0.03, seg=6)
    if kind != "bare":
        keys = {"leafy": ("oak", "oak_dark"), "frost": ("snow", "snow_shade"), "dusk": ("ash", "smoke_dark")}[kind]
        for i in range(9):
            a = rnd.uniform(0, TAU)
            r = rnd.uniform(0.2, 0.9)
            c = (r * math.cos(a), r * math.sin(a), 2.35 + rnd.uniform(-0.25, 0.55))
            s = rnd.uniform(0.55, 0.85)
            mb.ico(keys[i % 2], C.M(c, (rnd.uniform(0, 40), rnd.uniform(0, 40), rnd.uniform(0, 90)),
                                    (s, s * 0.95, s * 0.8)), subdiv=1)
    ob = mb.to_object(origin=(0, 0, 0))
    ob.hide_render = True
    return ob


def proto_clover():
    mb = C.MB("Clover", smooth_angle=40)
    for i in range(3):
        a = 120 * i
        mb.sphere("leaf_dark", C.M((0, 0, 0.012), (0, 0, a)) @ C.M((0.011, 0, 0), scale=(0.011, 0.008, 0.002)),
                  seg=8, rings=4)
    mb.limb("leaf", [(0, 0, 0), (0.002, 0, 0.04), (0.0, 0.002, 0.07)], 0.0016, seg=5)
    for j in range(7):
        a = TAU * j / 7
        mb.sphere("white" if j % 2 else "cream", C.M((0.006 * math.cos(a), 0.006 * math.sin(a), 0.074),
                                                     scale=0.0055), seg=6, rings=4)
    mb.sphere("cream", C.M((0, 0, 0.08), scale=0.006), seg=6, rings=4)
    ob = mb.to_object(origin=(0, 0, 0))
    ob.hide_render = True
    return ob


def proto_sunflower():
    mb = C.MB("Sunflower", smooth_angle=45)
    mb.limb("leaf", [(0, 0, 0), (0.01, 0, 0.18), (0, -0.01, 0.34)], 0.006, 0.005, seg=6)
    for z, a in ((0.1, 30), (0.2, 210)):
        mb.sphere("leaf_dark", C.M((0, 0, z), (0, -30, a)) @ C.M((0.03, 0, 0), scale=(0.03, 0.016, 0.004)),
                  seg=8, rings=4)
    head = C.M((0, -0.012, 0.345), (70, 0, 0))
    mb.cyl("wood_dark", 0.026, 0.012, seg=12, m=head)
    for i in range(12):
        a = 360.0 * i / 12
        mb.sphere("honey" if i % 2 else "amber",
                  head @ C.M((0, 0, 0.006), (0, 0, a)) @ C.M((0.036, 0, 0), scale=(0.016, 0.007, 0.003)),
                  seg=6, rings=4)
    ob = mb.to_object(origin=(0, 0, 0))
    ob.hide_render = True
    return ob


def proto_cloud(key="cloud", key2="cloud_dark", name="SkyCloud"):
    mb = C.MB(name, smooth_angle=0)
    rnd = random.Random(4)
    for i in range(7):
        x = (i - 3) * 0.55 + rnd.uniform(-0.15, 0.15)
        s = 0.75 - abs(i - 3) * 0.12 + rnd.uniform(-0.05, 0.1)
        mb.ico(lambda c, n: key2 if n.z < -0.3 else key, C.M((x, rnd.uniform(-0.2, 0.2), s * 0.35),
                                                               scale=(s, s * 0.7, s * 0.6)), subdiv=2)
    ob = mb.to_object(origin=(0, 0, 0))
    ob.hide_render = True
    return ob


def proto_blob(name, key, mat):
    mb = C.MB(name, mat=mat, smooth_angle=60)
    mb.ico(key, C.M(scale=1.0), subdiv=2, smooth=True)
    ob = mb.to_object(origin=(0, 0, 0))
    ob.hide_render = True
    return ob


def proto_flame():
    mb = C.MB("Flame", mat="flame", smooth_angle=60)
    prof = [(0.0, 0.0), (0.09, 0.05), (0.12, 0.16), (0.09, 0.32), (0.04, 0.48), (0.0, 0.6)]
    mb.lathe(lambda c, n: "ember" if c.z < 0.2 else ("flame" if c.z < 0.38 else "fire"), prof, seg=7, smooth=False,
             lfn=lambda v: Vector((v.x * (1 + 0.25 * math.sin(v.z * 9)), v.y * 0.7, v.z)))
    ob = mb.to_object(origin=(0, 0, 0))
    ob.hide_render = True
    return ob


def proto_spark(name="Spark", key="ember", mat="star"):
    mb = C.MB(name, mat=mat, smooth_angle=0)
    mb.ico(key, C.M(scale=1.0), subdiv=1)
    ob = mb.to_object(origin=(0, 0, 0))
    ob.hide_render = True
    return ob


def proto_streak():
    mb = C.MB("RainStreak", mat="rainmat", smooth_angle=0)
    mb.box("rain", C.M((0, 0, 0), scale=(0.006, 0.006, 0.22)))
    ob = mb.to_object(origin=(0, 0, 0))
    ob.hide_render = True
    return ob


def proto_ring():
    mb = C.MB("Ripple", smooth_angle=60)
    mb.torus("rain", 1.0, 0.06, seg=20, seg2=4, m=C.M(scale=(1, 1, 0.3)))
    ob = mb.to_object(origin=(0, 0, 0))
    ob.hide_render = True
    return ob


# ============================================================================================ the world
class Look:
    def __init__(self, **kw):
        self.zenith = kw.get("zenith", "#5FA9DE")
        self.horizon = kw.get("horizon", "#CFEFF9")
        self.amb = kw.get("amb", "#B9D2E6")
        self.amb_k = kw.get("amb_k", 0.9)
        self.sun = kw.get("sun", 3.2)
        self.sun_col = kw.get("sun_col", "#FFF4E0")
        self.sun_dir = kw.get("sun_dir", (-35, 50))  # (azimuth from camera, elevation) the light comes FROM
        self.glare = kw.get("glare", False)
        self.cam = kw.get("cam", "meadow")
        self.palette = kw.get("palette", {})
        # new palette keys for one scene, each borrowing the slot of a key that scene never uses: {new: (donor, hex)}
        self.alias = kw.get("alias", {})
        # one-shot clip (seconds) that ends on the first frame of a seamless loop (seconds) written as loop_id
        self.oneshot = kw.get("oneshot", 0.0)
        self.loop = kw.get("loop", 0.0)
        self.loop_id = kw.get("loop_id", "")
        self.stills = kw.get("stills", [])  # extra stills: [(name, phase)], phase = scene clock / loop


def lin(hx):
    return tuple(C.srgb_to_lin(c) for c in C.hex_rgb(hx)) + (1.0,)


def setup_render(lk, samples):
    sc = bpy.context.scene
    eng = [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items]
    sc.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in eng else "BLENDER_EEVEE"
    sc.eevee.taa_render_samples = samples
    sc.render.resolution_x, sc.render.resolution_y, sc.render.resolution_percentage = W, H, 100
    sc.render.film_transparent = False
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGB"
    sc.view_settings.view_transform = "Standard"
    sc.view_settings.look = "None"
    try:
        sc.eevee.use_shadows = True
    except Exception:
        pass

    # sky: a vertical gradient the camera sees, a flat ambient colour for the lighting
    w = bpy.data.worlds.new("Sky")
    w.use_nodes = True
    nt = w.node_tree
    nt.nodes.clear()
    tc = nt.nodes.new("ShaderNodeTexCoord")
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(tc.outputs["Generated"], sep.inputs[0])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].position = 0.0
    ramp.color_ramp.elements[0].color = lin(lk.horizon)
    ramp.color_ramp.elements[1].position = 0.45
    ramp.color_ramp.elements[1].color = lin(lk.zenith)
    nt.links.new(sep.outputs[2], ramp.inputs[0])
    sky = nt.nodes.new("ShaderNodeBackground")
    sky.inputs[1].default_value = 1.0
    nt.links.new(ramp.outputs[0], sky.inputs[0])
    amb = nt.nodes.new("ShaderNodeBackground")
    amb.inputs[0].default_value = lin(lk.amb)
    amb.inputs[1].default_value = lk.amb_k
    lp = nt.nodes.new("ShaderNodeLightPath")
    mix = nt.nodes.new("ShaderNodeMixShader")
    nt.links.new(lp.outputs["Is Camera Ray"], mix.inputs[0])
    nt.links.new(amb.outputs[0], mix.inputs[1])
    nt.links.new(sky.outputs[0], mix.inputs[2])
    out = nt.nodes.new("ShaderNodeOutputWorld")
    nt.links.new(mix.outputs[0], out.inputs[0])
    sc.world = w

    ld = bpy.data.lights.new("Sun", "SUN")
    ld.energy = lk.sun
    ld.color = C.hex_rgb(lk.sun_col)
    ld.angle = math.radians(3)
    so = link(bpy.data.objects.new("Sun", ld))
    az, el = map(math.radians, lk.sun_dir)
    frm = Vector((math.sin(az) * math.cos(el), -math.cos(az) * math.cos(el), math.sin(el)))  # toward the light
    so.rotation_euler = (-frm).to_track_quat("-Z", "Y").to_euler()

    cd = bpy.data.cameras.new("Cam")
    cam = link(bpy.data.objects.new("Cam", cd))
    if lk.cam == "low":  # earthrise: down in the dust, looking a little up
        cd.lens = 30
        cam.location = (0, -3.0, 0.28)
        target = Vector((0, 6.0, 0.55))
    elif lk.cam == "paris":  # forecast: a little lower and tilted up, so the Eiffel Tower fits behind the hive
        cd.lens = 30
        cam.location = (0, -3.2, 0.55)
        target = Vector((0.8, 6.0, 1.45))
    else:
        cd.lens = 30
        cam.location = (0, -2.75, 0.62)
        target = Vector((0, 0.6, 0.3))
    cam.rotation_euler = (target - Vector(cam.location)).to_track_quat("-Z", "Y").to_euler()
    cd.clip_start, cd.clip_end = 0.05, 400
    sc.camera = cam

    if lk.glare:
        try:
            sc.use_nodes = True
            tree = sc.node_tree
            tree.nodes.clear()
            rl = tree.nodes.new("CompositorNodeRLayers")
            gl = tree.nodes.new("CompositorNodeGlare")
            gl.glare_type = "FOG_GLOW"
            for attr, val in (("quality", "HIGH"), ("threshold", 0.85), ("size", 7)):
                try:
                    setattr(gl, attr, val)
                except Exception:
                    pass
            for name, val in (("Threshold", 0.85), ("Strength", 0.7), ("Size", 0.6)):
                if name in gl.inputs:
                    gl.inputs[name].default_value = val
            co = tree.nodes.new("CompositorNodeComposite")
            tree.links.new(rl.outputs["Image"], gl.inputs["Image"])
            tree.links.new(gl.outputs["Image"], co.inputs["Image"])
        except Exception as e:  # a compositor API change must not cost the clip
            print("VBS_CLIPS glare off:", e)
            sc.use_nodes = False


def height(x, y, hills=True):
    z = 0.025 * noise.noise(Vector((x * 0.9, y * 0.9, 0.3)))
    if hills and y > 7:
        t = min(1.0, (y - 7) / 16)
        z += (t * t * (3 - 2 * t)) * (1.6 + 1.3 * noise.noise(Vector((x * 0.11, y * 0.11, 2.0))))
    return z


def ground(keyfn, hills=True, name="Ground"):
    """Faceted ground from y=-5 to 60 (finer near the camera). keyfn(x, y, rnd) -> palette key."""
    mb = C.MB(name, smooth_angle=0)
    bm = mb.bm
    before = mb._begin()
    xs = [x * 0.5 for x in range(-30, 31)]
    xs = [-40.0, -28.0, -20.0] + xs + [20.0, 28.0, 40.0]
    ys = [-5 + 0.45 * i for i in range(27)] + [7.15 + 0.9 * i for i in range(16)] + [22 + 3 * i for i in range(14)]
    rnd = random.Random(12)
    grid = [[bm.verts.new((x + (rnd.uniform(-0.08, 0.08) if abs(x) < 15 else 0), y, height(x, y, hills)))
             for x in xs] for y in ys]
    for j in range(len(ys) - 1):
        for i in range(len(xs) - 1):
            a, b, c, d = grid[j][i], grid[j][i + 1], grid[j + 1][i + 1], grid[j + 1][i]
            if (i + j) % 2:
                bm.faces.new((a, b, c))
                bm.faces.new((a, c, d))
            else:
                bm.faces.new((a, b, d))
                bm.faces.new((b, c, d))
    kr = random.Random(3)
    mb._end(before, lambda c, n: keyfn(c.x, c.y, kr), False)
    return mb.to_object(origin=(0, 0, 0))


def in_view(y, margin=0.25):
    """Half-width of the frame at depth y (camera at y=-2.75, lens 30)."""
    return 0.62 * (y + 2.75) + margin


def scatter(rnd, n, y0, y1, avoid_hive=True, bias=1.6, spread=1.0, avoid=()):
    pts = []
    tries = 0
    while len(pts) < n and tries < n * 30:
        tries += 1
        y = y0 + (y1 - y0) * (rnd.random() ** bias)
        x = rnd.uniform(-1, 1) * in_view(y) * spread
        if avoid_hive and (abs(x) < 0.42 and -0.95 < y < 0.42 or abs(x) < 0.3 + 0.1 * (y + 2) and y < -0.9):
            continue
        if any((x - ax) ** 2 + (y - ay) ** 2 < ar * ar for ax, ay, ar in avoid):
            continue
        pts.append((x, y))
    return pts


class World:
    """Builds the props for one scene and keeps handles the scene functions animate."""

    def __init__(self, lk, a):
        self.lk = lk
        self.rnd = random.Random(20000)
        C.reset()
        C.PALETTE[:] = BASE_PALETTE
        for i, (k, _) in enumerate(C.PALETTE):
            if k in lk.palette:
                C.PALETTE[i] = (k, lk.palette[k])
        for new, (donor, hx) in lk.alias.items():
            i = C.PAL[donor]
            C.PALETTE[i] = (donor, hx)
            C.PAL[new] = i
        ANIM.clear()
        self.tmp = os.path.join(a.work, "tex")
        C.palette_image(self.tmp)
        self.p = {}

    # -- prototypes (built once per scene, before finalize)
    def protos(self, *names):
        for n in names:
            if n == "hive":
                HIVE.build()
            elif n == "flowers":
                FLOWERS.build()
                for k in ("Flower_Purple", "Flower_Blue", "Flower_Lavender"):
                    bpy.data.objects[k].hide_render = True
            elif n == "grass":
                GRASS.build()
                bpy.data.objects["GrassTuft"].hide_render = True
            elif n == "bee":
                BEE.build()
                bpy.data.objects["WorkerBee"].hide_render = True
            elif n == "wasp":
                WASP.build()
            elif n == "dish":
                DISH.build()
            elif n == "earth":
                EARTH.build()
            elif n == "clover":
                self.p["clover"] = proto_clover()
            elif n == "sunflower":
                self.p["sunflower"] = proto_sunflower()
            elif n.startswith("oak_"):
                self.p[n] = proto_oak(n[4:])
            elif n == "cloud":
                self.p["cloud"] = proto_cloud()
            elif n == "cloud_grey":
                self.p["cloud_grey"] = proto_cloud("stone", "stone_dark", "SkyCloudGrey")
            elif n == "smoke":
                self.p["smoke"] = proto_blob("Smoke", "smoke", "veil")
            elif n == "smoke_dark":
                self.p["smoke_dark"] = proto_blob("SmokeDark", "smoke_dark", "veil")
            elif n == "mist":
                self.p["mist"] = proto_blob("Mist", "mist", "veil_thin")
            elif n == "flame":
                self.p["flame"] = proto_flame()
            elif n == "spark":
                self.p["spark"] = proto_spark()
            elif n == "flake":
                self.p["flake"] = proto_spark("Flake", "snow", "pal")
            elif n == "dust":
                self.p["dust"] = proto_spark("Dust", "moon_light", "pal")
            elif n == "star":
                self.p["star"] = proto_spark("Star", "white", "star")
            elif n == "streak":
                self.p["streak"] = proto_streak()
            elif n == "ripple":
                self.p["ripple"] = proto_ring()

    def finalize(self):
        C.finalize(self.tmp)
        o = bpy.data.objects
        if "WorkerBee" in o:
            self.p["bee"] = o["WorkerBee"]
        for k, n in (("purple", "Flower_Purple"), ("blue", "Flower_Blue"), ("lavender", "Flower_Lavender"),
                     ("grass", "GrassTuft")):
            if n in o:
                self.p[k] = o[n]
        if "Wasp" in o:
            self.p["wasp"] = bake(o["Wasp"], "WaspBaked")
        if "Hive" in o:
            o["HeatGlow"].hide_render = True
            o["EntranceReducer"].hide_render = True
        if "PebbleDish" in o:
            o["PebbleDish"].location = DISH_AT
            o["PebbleDish"].scale = (2.2,) * 3

    # -- set dressing
    def trees(self, kind, n=11):
        rnd = random.Random(31)
        spots = [(-9.5, 20), (-5.6, 24), (-13.5, 25), (6.2, 21), (10.5, 25), (14.5, 20.5), (-2.2, 31), (3.4, 29),
                 (-18, 22), (19, 26), (0.4, 37)]
        for x, y in spots[:n]:
            s = rnd.uniform(1.5, 2.0)
            inst(self.p["oak_" + kind], (x, y, height(x, y) - 0.1), s, (0, 0, rnd.uniform(0, TAU)))

    def clouds(self, n=4, grey=False, z=(7, 11), drift=0.6):
        rnd = random.Random(55)
        proto = self.p["cloud_grey" if grey else "cloud"]
        for i in range(n):
            x0, y0 = rnd.uniform(-16, 16), rnd.uniform(28, 45)
            o = inst(proto, (x0, y0, rnd.uniform(*z) + 0.12 * y0), rnd.uniform(2.6, 4.2))
            p = rnd.uniform(0, TAU)
            ANIM.append(lambda ph, o=o, x0=x0, p=p: setattr(o, "location", (x0 + drift * wave(ph, 1, p),
                                                                             o.location.y, o.location.z)))

    def meadow_plants(self, n_flowers=230, n_grass=260, n_clover=120, droop=0.0, sway=True, sunflowers=0,
                      kinds=("purple", "blue", "lavender"), y1=8.0, avoid=()):
        rnd = self.rnd

        def spots(*a, **kw):
            return scatter(*a, avoid=avoid, **kw)
        for x, y in spots(rnd, n_grass, -2.0, y1 + 3, bias=1.4):
            inst(self.p["grass"], (x, y, height(x, y) - 0.005), rnd.uniform(2.0, 3.2), (0, 0, rnd.uniform(0, TAU)))
        if n_clover:
            for x, y in spots(rnd, n_clover, -1.9, y1, bias=1.5):
                inst(self.p["clover"], (x, y, height(x, y)), rnd.uniform(1.6, 2.3), (0, 0, rnd.uniform(0, TAU)))
        hero = []
        for x, y in spots(rnd, n_flowers, -1.5, y1, bias=1.7):
            k = rnd.choice(kinds)
            s = rnd.uniform(1.4, 1.9) * (0.85 if y < -0.5 else 1.0)
            tilt = math.radians(droop * rnd.uniform(0.7, 1.2)) if droop else 0.0
            yaw = rnd.uniform(-0.6, 0.6)
            yaw += (rnd.random() < 0.5) * math.pi * (droop > 0)  # drooping heads face both ways
            o = inst(self.p[k], (x, y, height(x, y)), s, (tilt, 0, yaw))
            if sway and y < 2.5:
                hero.append(o)
        for x, y in spots(rnd, sunflowers, -1.2, y1 * 0.7, bias=1.3):
            inst(self.p["sunflower"], (x, y, height(x, y)), rnd.uniform(1.6, 2.3), (0, 0, rnd.uniform(-0.4, 0.4)))
        for o in hero:
            base = tuple(o.rotation_euler)
            p1, p2 = rnd.uniform(0, TAU), rnd.uniform(0, TAU)
            k = rnd.choice((1, 2))
            amp = math.radians(rnd.uniform(3, 6))

            def f(ph, o=o, base=base, p1=p1, p2=p2, k=k, amp=amp):
                o.rotation_euler = (base[0] + amp * wave(ph, k, p1), base[1] + amp * 0.7 * wave(ph, k + 1, p2), base[2])
            ANIM.append(f)

    # -- bees and other movers
    def orbit(self, proto, c, r, k, scale, buzz=0.012, kb=(23, 29, 31), level=0.6):
        rnd = self.rnd
        o = mover(proto, scale)
        p = [rnd.uniform(0, TAU) for _ in range(6)]
        c = Vector(c)

        def pos(ph):
            return c + Vector((r[0] * wave(ph, k[0], p[0]) + buzz * wave(ph, kb[0], p[3]),
                               r[1] * wave(ph, k[1], p[1]) + buzz * wave(ph, kb[1], p[4]),
                               r[2] * wave(ph, k[2], p[2]) + buzz * wave(ph, kb[2], p[5])))

        def f(ph):
            a, b = pos(ph), pos(ph + 1e-3)
            o.location = a
            o.rotation_quaternion = look(b - a, level)
        ANIM.append(f)
        return o

    def stream(self, proto, A, B, n, k, scale, arc=0.25, lane=0.12, wob=0.03, fade=(0.08, 0.12)):
        rnd = self.rnd
        A, B = Vector(A), Vector(B)
        side = (B - A).cross(Vector((0, 0, 1)))
        side = side.normalized() if side.length > 1e-6 else Vector((1, 0, 0))
        for i in range(n):
            o = mover(proto, scale)
            off = (i + rnd.uniform(-0.3, 0.3)) / n
            ln = rnd.uniform(-1, 1) * lane
            lz = rnd.uniform(-0.5, 1) * lane
            pp = [rnd.uniform(0, TAU) for _ in range(3)]
            kw = rnd.choice((11, 13, 17))

            def pos(ph, off=off, ln=ln, lz=lz, pp=pp, kw=kw):
                u = (k * ph + off) % 1.0
                s = math.sin(math.pi * u)
                buzz = Vector((wave(ph, kw, pp[0]), wave(ph, kw + 2, pp[1]), wave(ph, kw + 4, pp[2]))) * wob
                return u, A.lerp(B, u) + side * ln * s + Vector((0, 0, arc * 4 * u * (1 - u) + lz * s)) + buzz

            def f(ph, o=o, pos=pos):
                u, a = pos(ph)
                _, b = pos(ph + 1e-3)
                o.location = a
                o.rotation_quaternion = look(b - a)
                e = scale * env(u, *fade)
                o.scale = (e, e, e)
            ANIM.append(f)

    def sitter(self, proto, at, scale, normal=(0, -1, 0), head=(0, 0, 1), jitter=0.004, kj=7, spin=0.0):
        """A bee on a surface: back along `normal`, head toward `head`, small crawl/jitter."""
        rnd = self.rnd
        o = mover(proto, scale)
        nrm, hd = Vector(normal).normalized(), Vector(head).normalized()
        rot = C.frame((0, 0, 0), nrm, -hd).to_3x3().to_quaternion()
        at = Vector(at)
        pp = [rnd.uniform(0, TAU) for _ in range(3)]
        t1 = hd.cross(nrm).normalized()

        def f(ph):
            o.location = at + t1 * jitter * wave(ph, kj, pp[0]) + hd * jitter * wave(ph, kj + 1, pp[1])
            o.rotation_quaternion = Quaternion(nrm, spin * wave(ph, kj * 3, pp[2])) @ rot
        ANIM.append(f)
        return o

    def particles(self, proto, n, box, vel, k, scale, grow=(1.0, 1.0), wob=0.0, kw=3, fade=(0.12, 0.2),
                  stretch=(1, 1, 1)):
        """n copies drifting from a random point in box by `vel` over each life; k lives per clip."""
        rnd = self.rnd
        (x0, x1), (y0, y1), (z0, z1) = box
        vel = Vector(vel)
        for i in range(n):
            o = mover(proto, scale)
            start = Vector((rnd.uniform(x0, x1), rnd.uniform(y0, y1), rnd.uniform(z0, z1)))
            off = rnd.random()
            pp = [rnd.uniform(0, TAU) for _ in range(2)]
            ks = k if isinstance(k, int) else rnd.choice(k)
            sz = rnd.uniform(0.7, 1.3)

            def f(ph, o=o, start=start, off=off, pp=pp, ks=ks, sz=sz):
                u = (ks * ph + off) % 1.0
                o.location = start + vel * u + Vector((wob * wave(ph, kw, pp[0]), 0, wob * 0.5 * wave(ph, kw, pp[1])))
                e = scale * sz * (grow[0] + (grow[1] - grow[0]) * u) * env(u, *fade)
                o.scale = (e * stretch[0], e * stretch[1], e * stretch[2])
            ANIM.append(f)


# ============================================================================================ the scenes
SKY_BLUE = dict(zenith="#4F9FDB", horizon="#CBEBF7")
SICK = {"leaf": "#9A9466", "leaf_dark": "#77714F", "purple": "#8D8296", "lavender": "#A49DAA", "blue": "#7E8796",
        "oak": "#7B7457", "oak_dark": "#625C45", "grass_a": "#A49A6E", "grass_b": "#857C58", "honey": "#C9AE6A"}
HEAT = {"leaf": "#C7AE4E", "leaf_dark": "#A68C3C", "oak": "#8C9A62", "oak_dark": "#788654", "sky": "#D8CCA2"}
DUSK = {"leaf": "#8C7A3E", "leaf_dark": "#6B5A2E", "purple": "#7D4E6E", "lavender": "#8E6A7E", "blue": "#5E5E7E"}
HIVE_DOOR = Vector((0, -0.215, 0.07))
DISH_AT = (0.55, -0.8, 0.0)
BEE_S = 2.2


def meadow_ground(kind="green"):
    keys = {"green": ("grass_a", "grass_b", "leaf"), "dry": ("dry_a", "dry_b", "stone_dark"),
            "gold": ("gold_a", "gold_b", "dry_a"), "snow": ("snow", "snow_shade", "snow"),
            "dusk": ("dry_b", "gold_b", "ash")}[kind]

    def key(x, y, r):
        v = r.random()
        return keys[0] if v < 0.55 else (keys[1] if v < 0.9 else keys[2])
    ground(key)


SCENES = {}


def scene(sid, **look):
    def deco(fn):
        SCENES[sid] = (fn, Look(**look))
        return fn
    return deco


@scene("meadow", **SKY_BLUE)
def s_meadow(w):
    w.protos("hive", "flowers", "grass", "bee", "clover", "oak_leafy", "cloud")
    w.finalize()
    meadow_ground()
    w.trees("leafy")
    w.clouds(4)
    w.meadow_plants()
    b = w.p["bee"]
    w.stream(b, HIVE_DOOR, (-1.6, 1.8, 0.35), 3, 1, BEE_S)
    w.stream(b, (1.3, 0.9, 0.3), HIVE_DOOR, 3, 1, BEE_S)
    for c in ((-0.9, -0.6, 0.3), (1.0, -0.3, 0.32)):
        w.orbit(b, c, (0.18, 0.12, 0.06), (1, 2, 3), BEE_S)


@scene("smoker_settle", **SKY_BLUE)
def s_smoker_settle(w):
    w.protos("hive", "flowers", "grass", "bee", "clover", "oak_leafy", "cloud", "mist")
    w.finalize()
    meadow_ground()
    w.trees("leafy")
    w.clouds(4)
    w.meadow_plants()
    b = w.p["bee"]
    for dest in ((-1.7, 1.2, 0.25), (1.8, 1.6, 0.3), (-0.6, 3.5, 0.5)):
        w.stream(b, HIVE_DOOR, dest, 6, 1, BEE_S, arc=0.18, lane=0.05, wob=0.01)
    w.particles(w.p["mist"], 18, ((-2.4, -1.2), (-0.7, 0.3), (0.25, 0.6)), (3.6, 0.4, 0.3), 1, 0.08,
                grow=(0.6, 1.5), wob=0.05, fade=(0.2, 0.3), stretch=(2.4, 1.0, 0.6))


@scene("smoker_angry", zenith="#59606F", horizon="#A79C93", amb="#8E8F96", amb_k=0.7, sun=1.8, sun_col="#F2E6D8")
def s_smoker_angry(w):
    w.protos("hive", "flowers", "grass", "bee", "clover", "oak_leafy", "cloud_grey")
    w.finalize()
    meadow_ground()
    w.trees("leafy")
    w.clouds(7, grey=True, z=(5, 8))
    w.meadow_plants(n_flowers=170)
    b = w.p["bee"]
    rnd = w.rnd
    for i in range(150):
        r = rnd.uniform(0.15, 0.6)
        c = (rnd.uniform(-0.15, 0.15), rnd.uniform(-0.45, 0.1), rnd.uniform(0.2, 0.55))
        w.orbit(b, c, (r, r * 0.7, r * 0.45), (rnd.choice((2, 3, 4)), rnd.choice((3, 5)), rnd.choice((4, 6, 7))),
                BEE_S * 0.9, buzz=0.02)


@scene("feed_sick", zenith="#8E98A3", horizon="#C9CCCB", amb="#A3A6A6", amb_k=0.85, sun=1.4, sun_col="#E8E6E0",
       palette=SICK)
def s_feed_sick(w):
    w.protos("hive", "flowers", "grass", "bee", "oak_bare", "cloud_grey")
    w.finalize()
    meadow_ground("dry")
    w.trees("bare")
    w.clouds(8, grey=True, z=(5, 8), drift=0.3)
    w.meadow_plants(n_flowers=140, n_clover=0, droop=60, sway=False)
    b = w.p["bee"]
    for i, x in enumerate((-0.09, 0.0, 0.07, 0.12)):
        w.sitter(b, (x, -0.245, 0.068), BEE_S * 0.8, normal=(0, 0, 1), head=(w.rnd.uniform(-1, 1), -1, 0),
                 jitter=0.012, kj=1 + i % 2)


@scene("feed_recover", zenith="#5AA7DE", horizon="#D9EEF2")
def s_feed_recover(w):
    w.protos("hive", "flowers", "grass", "bee", "clover", "oak_leafy", "cloud", "cloud_grey")
    w.finalize()
    meadow_ground()
    w.trees("leafy")
    w.clouds(3)
    w.meadow_plants(n_flowers=200)
    b = w.p["bee"]
    for src in ((-2.2, 2.0, 0.6), (2.0, 1.4, 0.45), (0.6, 4.0, 0.8)):
        w.stream(b, src, HIVE_DOOR, 6, 1, BEE_S, arc=0.15, lane=0.06, wob=0.012)


@scene("wasps_raid", **SKY_BLUE)
def s_wasps_raid(w):
    w.protos("hive", "flowers", "grass", "bee", "clover", "oak_leafy", "cloud", "wasp")
    w.finalize()
    meadow_ground()
    w.trees("leafy")
    w.clouds(4)
    w.meadow_plants()
    rnd = w.rnd
    for i in range(11):
        c = (rnd.uniform(-0.2, 0.2), rnd.uniform(-0.42, -0.28), rnd.uniform(0.08, 0.32))
        o = w.orbit(w.p["wasp"], c, (0.05, 0.03, 0.03), (1, 2, 3), 2.3, buzz=0.01, kb=(37, 41, 43))
    for i in range(26):
        r = rnd.uniform(0.6, 1.3)
        w.orbit(w.p["bee"], (rnd.uniform(-0.3, 0.3), rnd.uniform(-0.6, 0.6), rnd.uniform(0.35, 0.8)),
                (r, r * 0.5, 0.2), (rnd.choice((1, 2)), rnd.choice((2, 3)), 3), BEE_S, buzz=0.02)


@scene("wasps_leave", **SKY_BLUE)
def s_wasps_leave(w):
    w.protos("hive", "flowers", "grass", "bee", "clover", "oak_leafy", "cloud", "wasp")
    w.finalize()
    bpy.data.objects["EntranceReducer"].hide_render = False
    meadow_ground()
    w.trees("leafy")
    w.clouds(4)
    w.meadow_plants()
    b = w.p["bee"]
    for i in range(7):
        x = -0.075 + 0.025 * i
        w.sitter(b, (x, -0.252, 0.081), BEE_S * 0.75, normal=(0, 0, 1), head=(0, -1, 0), jitter=0.002, kj=3 + i % 3)
    w.stream(w.p["wasp"], (0.1, -0.5, 0.25), (2.6, 3.0, 1.1), 2, 1, 2.3, arc=0.3, lane=0.1, wob=0.02)
    w.stream(w.p["wasp"], (-0.15, -0.55, 0.3), (-2.4, 2.4, 1.0), 2, 1, 2.3, arc=0.3, lane=0.1, wob=0.02)
    w.stream(b, HIVE_DOOR + Vector((0, -0.1, 0.05)), (-1.5, 1.5, 0.3), 3, 1, BEE_S)


@scene("heat_stress", zenith="#E7E3C9", horizon="#FFF8E6", amb="#E9DDB8", amb_k=1.0, sun=4.6, sun_col="#FFF2D2",
       sun_dir=(-20, 62), glare=True, palette=HEAT)
def s_heat_stress(w):
    w.protos("hive", "flowers", "grass", "bee", "oak_leafy")
    w.finalize()
    meadow_ground("gold")
    w.trees("leafy")
    sun = proto_spark("SunDisc", "sun", "sunmat")
    inst(sun, (-12.0, 40.0, 7.6), 2.6)
    w.meadow_plants(n_flowers=110, n_clover=0, droop=25, sway=False)
    b = w.p["bee"]
    rnd = w.rnd
    for i in range(110):  # the beard: a thick cluster hanging down the front under the entrance and up the box
        u = rnd.random()
        x = rnd.gauss(0, 0.075)
        z = 0.085 + 0.24 * (u ** 1.6)
        if abs(x) > 0.16:
            continue
        w.sitter(b, (x, -0.207 - rnd.uniform(0, 0.012), z), BEE_S * 0.75, normal=(rnd.uniform(-0.8, 0.8), -1, 0),
                 head=(rnd.uniform(-0.6, 0.6), 0, 1), jitter=0.004, kj=rnd.choice((1, 2)))
    for i in range(5):  # a few still flying, slowly
        w.orbit(b, (rnd.uniform(-0.4, 0.4), -0.5, 0.3), (0.12, 0.06, 0.05), (1, 2, 3), BEE_S)


@scene("heat_drink", **SKY_BLUE)
def s_heat_drink(w):
    w.protos("hive", "flowers", "grass", "bee", "clover", "oak_leafy", "cloud", "dish")
    w.finalize()
    meadow_ground()
    w.trees("leafy")
    w.clouds(3)
    w.meadow_plants(avoid=((DISH_AT[0], DISH_AT[1], 0.42),))
    b = w.p["bee"]
    bpy.context.view_layer.update()
    spots = [o.matrix_world.translation.copy() for o in bpy.data.objects if o.name.startswith("LandingSpot")]
    for i, p in enumerate(spots[:7]):
        w.sitter(b, p + Vector((0, 0, 0.006)), BEE_S * 0.75, normal=(0, 0, 1),
                 head=(math.cos(i * 1.7), math.sin(i * 1.7), 0), jitter=0.003, kj=2 + i % 3)
    for x in (-0.06, 0.0, 0.06):  # fanners: on the landing board, facing the hive, wings buzzing
        w.sitter(b, (x, -0.245, 0.068), BEE_S * 0.75, normal=(0, 0, 1), head=(0, 1, 0), jitter=0.0015, kj=9,
                 spin=0.12)
    dish = Vector(DISH_AT) + Vector((0, 0, 0.07))
    w.stream(b, HIVE_DOOR, dish, 3, 1, BEE_S, arc=0.12, lane=0.04)
    w.stream(b, dish, HIVE_DOOR, 3, 1, BEE_S, arc=0.12, lane=0.04)


@scene("whatif_fire", zenith="#5B3557", horizon="#FF9A45", amb="#C97A55", amb_k=0.65, sun=2.2, sun_col="#FFB070",
       sun_dir=(160, 12), glare=True, palette=DUSK)
def s_whatif_fire(w):
    w.protos("hive", "flowers", "grass", "bee", "oak_dusk", "flame", "spark", "smoke_dark")
    w.finalize()
    meadow_ground("dusk")
    w.trees("dusk")
    w.meadow_plants(n_flowers=90, n_clover=0, sway=False)
    rnd = w.rnd
    fill = bpy.data.lights.new("FireLight", "POINT")
    fill.energy, fill.color, fill.shadow_soft_size = 400, C.hex_rgb("#FF8A3A"), 1.5
    link(bpy.data.objects.new("FireLight", fill)).location = (0, 2.2, 0.6)
    flames = []
    for i in range(48):
        y = rnd.uniform(1.4, 5.0)
        x = rnd.uniform(-1, 1) * in_view(y)
        s = rnd.uniform(0.45, 0.9) * (0.8 + 0.12 * y)
        o = inst(w.p["flame"], (x, y, height(x, y) - 0.02), s, (0, 0, rnd.uniform(0, TAU)))
        flames.append((o, s, [rnd.uniform(0, TAU) for _ in range(3)], rnd.choice((5, 6, 7, 9))))

    def flick(ph):
        for o, s, p, k in flames:
            h = 1 + 0.28 * wave(ph, k, p[0]) + 0.12 * wave(ph, 2 * k + 1, p[1])
            wd = 1 + 0.12 * wave(ph, k + 2, p[2])
            o.scale = (s * wd, s * wd, s * h)
    ANIM.append(flick)
    w.particles(w.p["spark"], 160, ((-3.5, 3.5), (0.8, 4.0), (0.1, 0.8)), (0.25, -0.6, 2.2), (1, 2), 0.012,
                grow=(1.0, 0.5), wob=0.12, kw=4)
    w.particles(w.p["smoke_dark"], 26, ((-4, 4), (2.5, 6.0), (0.6, 1.2)), (0.6, 0.6, 3.2), 1, 0.7,
                grow=(0.6, 2.0), wob=0.2)
    b = w.p["bee"]
    w.orbit(b, (0.0, -0.45, 0.25), (0.1, 0.05, 0.04), (1, 2, 3), BEE_S)


@scene("fire_out", zenith="#5DA5D8", horizon="#D8E6E6", amb="#B9C8D0", amb_k=0.9, sun=2.6)
def s_fire_out(w):
    w.protos("hive", "flowers", "grass", "bee", "clover", "oak_leafy", "cloud", "cloud_grey", "mist")
    w.finalize()
    rnd = random.Random(9)
    burnt = [(rnd.uniform(-1, 1) * in_view(y) * 0.9, y) for y in [rnd.uniform(0.8, 4.0) for _ in range(9)]]

    def key(x, y, r):
        for bx, by in burnt:
            if (x - bx) ** 2 + (y - by) ** 2 * 1.6 < 1.0:
                return "ash" if r.random() < 0.8 else "stone_dark"
        v = r.random()
        return "grass_a" if v < 0.5 else ("grass_b" if v < 0.9 else "leaf")
    ground(key)
    w.trees("leafy")
    w.clouds(3)
    w.clouds(3, grey=True)
    w.meadow_plants(n_flowers=120, n_grass=200, n_clover=60)
    for bx, by in burnt:  # fresh shoots in the ash
        for i in range(5):
            x, y = bx + rnd.uniform(-0.6, 0.6), by + rnd.uniform(-0.4, 0.4)
            inst(w.p["grass"], (x, y, height(x, y)), rnd.uniform(0.9, 1.4), (0, 0, rnd.uniform(0, TAU)))
    for bx, by in burnt:
        w.particles(w.p["mist"], 5, ((bx - 0.5, bx + 0.5), (by - 0.3, by + 0.3), (0.0, 0.1)), (0.15, 0.2, 1.3), 1,
                    0.07, grow=(0.6, 1.8), wob=0.06, fade=(0.25, 0.4), stretch=(0.9, 0.9, 2.2))
    b = w.p["bee"]
    w.stream(b, (-2.0, 2.5, 0.8), HIVE_DOOR, 5, 1, BEE_S, arc=0.15, lane=0.08)
    w.stream(b, (1.8, 2.0, 0.7), HIVE_DOOR, 4, 1, BEE_S, arc=0.15, lane=0.08)


@scene("whatif_rain", zenith="#7D8794", horizon="#BCC3C8", amb="#9EA7AF", amb_k=0.95, sun=1.0, sun_col="#E6ECF2",
       palette={"cloud": "#9AA4AE", "cloud_dark": "#7B8590"})
def s_whatif_rain(w):
    w.protos("hive", "flowers", "grass", "bee", "clover", "oak_leafy", "cloud", "streak", "ripple")
    w.finalize()
    rnd = random.Random(21)
    puddles = [(rnd.uniform(-1, 1) * in_view(y) * 0.8, y, rnd.uniform(0.25, 0.5))
               for y in [rnd.uniform(-1.6, 2.5) for _ in range(9)]]
    puddles = [p for p in puddles if not (abs(p[0]) < 0.6 and -1.0 < p[1] < 0.5)]
    meadow_ground()
    for x, y, r in puddles:
        mb = C.MB("Puddle", smooth_angle=0)
        mb.cyl("water", r, 0.012, seg=9, m=C.M((x, y, height(x, y) + 0.004), (0, 0, rnd.uniform(0, 90)), (1, 0.6, 1)))
        mb.to_object(origin=(x, y, 0))
    w.trees("leafy")
    w.clouds(9, z=(4.5, 7), drift=0.4)
    w.meadow_plants(n_flowers=200)
    w.particles(w.p["streak"], 520, ((-4, 4), (-2.0, 6.0), (2.2, 2.6)), (-0.25, 0, -2.6), (5, 6), 1.0,
                fade=(0.02, 0.02))
    for x, y, r in puddles:
        for i in range(3):
            o = mover(w.p["ripple"], 0.01)
            cx, cy = x + rnd.uniform(-r, r) * 0.5, y + rnd.uniform(-r, r) * 0.3
            off = rnd.random()
            z0 = height(x, y) + 0.012

            def f(ph, o=o, cx=cx, cy=cy, off=off, z0=z0, r=r):
                u = (2 * ph + off) % 1.0
                rr = 0.02 + 0.14 * u
                o.location = (cx, cy, z0 - 0.02 * u * u)
                o.scale = (rr, rr * 0.6, rr)
            ANIM.append(f)
    b = w.p["bee"]
    for x in (-0.05, 0.03):  # resting just inside the entrance
        w.sitter(b, (x, -0.2, 0.063), BEE_S * 0.75, normal=(0, 0, 1), head=(0, -1, 0), jitter=0.002, kj=1)


@scene("whatif_flowers", zenith="#3F97DD", horizon="#C4E8F7", sun=3.4)
def s_whatif_flowers(w):
    w.protos("hive", "flowers", "grass", "bee", "clover", "oak_leafy", "cloud")
    w.finalize()
    meadow_ground()
    w.trees("leafy")
    w.clouds(3)
    w.meadow_plants(n_flowers=520, n_clover=420, n_grass=200, kinds=("purple", "purple", "lavender", "blue"))
    rnd = w.rnd
    for i in range(44):
        y = rnd.uniform(-1.5, 3.5)
        x = rnd.uniform(-1, 1) * in_view(y) * 0.8
        w.orbit(w.p["bee"], (x, y, rnd.uniform(0.18, 0.45)), (rnd.uniform(0.1, 0.35), 0.1, 0.06),
                (1, rnd.choice((2, 3)), rnd.choice((3, 4))), BEE_S)


@scene("whatif_spray", zenith="#C9BE8A", horizon="#F1E3A8", amb="#D6CCA0", amb_k=0.95, sun=2.4, sun_col="#FFF0C4",
       palette={"leaf": "#7FA052", "leaf_dark": "#62803F"})
def s_whatif_spray(w):
    w.protos("hive", "flowers", "grass", "bee", "clover", "oak_leafy", "mist")
    w.finalize()
    meadow_ground()
    w.trees("leafy", n=9)
    w.meadow_plants(n_flowers=200)
    # the tractor, far right edge of the meadow
    tx, ty = 6.2, 13.0
    tz = height(tx, ty)
    t = C.MB("Tractor", smooth_angle=30)
    t.box("red", C.M((0, 0, 0.75), scale=(1.4, 0.8, 0.6)), bevel=(0.05, 1))
    t.box("red", C.M((-0.55, 0, 1.35), scale=(0.7, 0.75, 0.75)), bevel=(0.04, 1))
    t.box("sky", C.M((-0.55, 0, 1.45), scale=(0.72, 0.6, 0.45)))
    t.box("charcoal", C.M((0.75, 0, 1.25), scale=(0.08, 0.08, 0.5)))
    for sx, r, ww in ((-0.55, 0.55, 0.3), (0.55, 0.32, 0.22)):
        for sy in (1, -1):
            t.cyl("charcoal", r, ww, seg=12, m=C.M((sx, sy * 0.48, r), (90, 0, 0)))
            t.cyl("honey", r * 0.45, ww + 0.02, seg=10, m=C.M((sx, sy * 0.49, r), (90, 0, 0)))
    t.box("steel", C.M((-1.3, 0, 0.9), scale=(0.12, 3.6, 0.08)))
    t.to_object(origin=(0, 0, 0)).location = (tx, ty, tz)
    o = bpy.data.objects["Tractor"]
    o.rotation_euler = (0, 0, math.radians(-15))
    ANIM.append(lambda ph: setattr(o, "location", (tx, ty, tz + 0.02 * abs(wave(ph, 12)))))
    w.particles(w.p["mist"], 30, ((tx - 1.6, tx - 1.0), (ty - 2.0, ty + 2.0), (tz + 0.5, tz + 1.0)),
                (-9.5, -9.0, -0.4), 1, 0.45, grow=(0.6, 2.0), wob=0.25, fade=(0.15, 0.3), stretch=(1.6, 1.6, 0.5))
    w.particles(w.p["mist"], 12, ((-3.5, 3.5), (1.0, 4.5), (0.2, 0.6)), (-1.2, -0.3, -0.1), 1, 0.25,
                grow=(0.8, 1.3), wob=0.1, fade=(0.3, 0.3), stretch=(2.2, 1.6, 0.4))
    w.orbit(w.p["bee"], (-0.5, -0.5, 0.3), (0.2, 0.1, 0.05), (1, 2, 3), BEE_S)


@scene("whatif_winter", zenith="#AFC6DA", horizon="#EEF2F5", amb="#D5DEE8", amb_k=1.0, sun=2.0, sun_col="#F2F6FF",
       sun_dir=(-40, 30), glare=True)
def s_whatif_winter(w):
    w.protos("hive", "grass", "bee", "oak_frost", "flake")
    w.finalize()
    meadow_ground("snow")
    w.trees("frost")
    rnd = w.rnd
    for x, y in scatter(rnd, 60, -1.8, 8, bias=1.4):  # a few grass tips poking through
        inst(w.p["grass"], (x, y, height(x, y) - 0.06), rnd.uniform(1.4, 2.0), (0, 0, rnd.uniform(0, TAU)))
    cap = C.MB("SnowCap", smooth_angle=0)
    cap.box("snow", C.M((0, 0.0, 0.485), scale=(0.41, 0.47, 0.045)), bevel=(0.018, 2))
    for i in range(5):
        cap.ico("snow", C.M((rnd.uniform(-0.15, 0.15), rnd.uniform(-0.18, 0.18), 0.5), scale=(0.07, 0.07, 0.03)),
                subdiv=1)
    cap.to_object(origin=(0, 0, 0.45))
    glow = C.MB("DoorGlow", mat="glow", smooth_angle=0)
    glow.box("honey", C.M((0, -0.2045, 0.0695), scale=(0.2, 0.004, 0.02)))
    glow.to_object(origin=(0, -0.2, 0.07))
    gl = bpy.data.lights.new("DoorLight", "POINT")
    gl.energy, gl.color, gl.shadow_soft_size = 4, C.hex_rgb("#FFB347"), 0.05
    link(bpy.data.objects.new("DoorLight", gl)).location = (0, -0.26, 0.08)
    w.particles(w.p["flake"], 260, ((-3.5, 3.5), (-1.6, 6.0), (1.6, 2.0)), (0.2, 0, -1.9), 1, 0.009, wob=0.08,
                kw=3, fade=(0.03, 0.05))


@scene("full_bloom", zenith="#3F8FD6", horizon="#FFE3A6", amb="#E8D7B0", amb_k=0.95, sun=3.8, sun_col="#FFD48A",
       sun_dir=(-55, 24), glare=True)
def s_full_bloom(w):
    w.protos("hive", "flowers", "grass", "bee", "clover", "oak_leafy", "cloud", "sunflower")
    w.finalize()
    meadow_ground()
    w.trees("leafy")
    w.clouds(2)
    w.meadow_plants(n_flowers=600, n_clover=320, n_grass=200, sunflowers=46,
                    kinds=("purple", "lavender", "lavender", "blue"))
    b = w.p["bee"]
    rnd = w.rnd
    for i in range(12):
        a = math.radians(-160 + 140 * i / 11)
        dest = (2.6 * math.cos(a) * (1.0 + 0.5 * (i % 3)), 0.6 + 2.8 * abs(math.sin(a)) * (1 + 0.4 * (i % 2)),
                rnd.uniform(0.25, 0.7))
        w.stream(b, HIVE_DOOR, dest, 9, 1, BEE_S, arc=0.2, lane=0.1, wob=0.015)
    for i in range(50):
        y = rnd.uniform(-1.5, 3.0)
        x = rnd.uniform(-1, 1) * in_view(y) * 0.8
        w.orbit(b, (x, y, rnd.uniform(0.2, 0.5)), (rnd.uniform(0.08, 0.3), 0.1, 0.05),
                (1, rnd.choice((2, 3)), rnd.choice((3, 4))), BEE_S)


def crater_ground(rnd, n=26, area=((-6, 6), (-1.8, 14)), hills=False):
    def key(x, y, r):
        v = r.random()
        return "moon" if v < 0.55 else ("moon_dark" if v < 0.85 else "moon_light")
    ground(key, hills=hills)
    cr = C.MB("Craters", smooth_angle=0)
    for i in range(n):
        x, y = rnd.uniform(*area[0]), rnd.uniform(*area[1])
        r = rnd.uniform(0.18, 0.7) * (1 + 0.08 * max(0.0, y))
        z = height(x, y, hills)
        cr.torus("moon_light", r, r * 0.11, seg=12, seg2=4, smooth=False, m=C.M((x, y, z + 0.003), scale=(1, 1, 0.35)))
        cr.cyl("moon_dark", r * 0.92, 0.004, seg=12, m=C.M((x, y, z - 0.001)), smooth=False)
    cr.to_object(origin=(0, 0, 0))


def stars(w, n=520):
    rnd = random.Random(77)
    st = C.MB("Stars", mat="star", smooth_angle=0)
    for i in range(n):
        az = rnd.uniform(-1.3, 1.3)
        el = rnd.uniform(0.02, 0.9) ** 1.3
        d = Vector((math.sin(az) * math.cos(el), math.cos(az) * math.cos(el), math.sin(el)))
        st.ico("white" if rnd.random() < 0.85 else "sky", C.M(d * 150, scale=rnd.uniform(0.08, 0.22)), subdiv=1)
    st.to_object(origin=(0, 0, 0))


@scene("moon", zenith="#000000", horizon="#05060A", amb="#3A3E4A", amb_k=0.35, sun=4.2, sun_col="#FFFFFF",
       sun_dir=(-75, 20))
def s_moon(w):
    w.protos("hive", "earth", "dust")
    stars(w)
    rnd = random.Random(5)
    crater_ground(rnd)
    w.finalize()
    e = bpy.data.objects["Earth"]
    e.location = (3.4, 40.0, 3.6)
    e.scale = (30,) * 3
    e.rotation_euler = (math.radians(15), 0, math.radians(200))
    cl = bpy.data.objects["Clouds"]
    ANIM.append(lambda ph: setattr(cl, "rotation_euler", (0, 0, math.radians(6) * wave(ph, 1))))
    w.particles(w.p["dust"], 90, ((-3, 3), (-1.5, 3.0), (0.0, 0.12)), (0.8, 0.2, 0.05), (1, 2), 0.006, wob=0.02)


@scene("moon_0", zenith="#F1F1EE", horizon="#F7F7F4", amb="#DCDCD8", amb_k=1.0, sun=1.2, sun_col="#FFFFFF")
def s_moon_0(w):
    yard(w, "grey", clothesline=True)


@scene("stage_1", zenith="#F1F1EE", horizon="#F7F7F4", amb="#DCDCD8", amb_k=1.0, sun=1.3, sun_col="#FFFFFF")
def s_stage_1(w):
    yard(w, "grey", patch=True)


@scene("stage_2", zenith="#78B6E0", horizon="#D9EEF6", amb="#C9D9E3", amb_k=0.95, sun=2.6)
def s_stage_2(w):
    yard(w, "spread")


@scene("stage_3", zenith="#3F8FD6", horizon="#FFE6B0", amb="#E8D7B0", amb_k=0.95, sun=3.6, sun_col="#FFD48A",
       sun_dir=(-55, 26), glare=True)
def s_stage_3(w):
    yard(w, "lush")


@scene("earthrise", zenith="#000000", horizon="#04050A", amb="#2E3240", amb_k=0.3, sun=5.0, sun_col="#FFFFFF",
       sun_dir=(-80, 14), cam="low")
def s_earthrise(w):
    w.protos("earth", "dust")
    stars(w, 700)
    rnd = random.Random(8)
    crater_ground(rnd, n=40, area=((-8, 8), (-2.5, 20)))
    w.finalize()
    e = bpy.data.objects["Earth"]
    e.location = (-1.6, 45.0, 6.2)
    e.scale = (34,) * 3
    e.rotation_euler = (math.radians(20), 0, math.radians(120))
    cl = bpy.data.objects["Clouds"]
    ANIM.append(lambda ph: setattr(cl, "rotation_euler", (0, 0, math.radians(5) * wave(ph, 1))))
    w.particles(w.p["dust"], 120, ((-3, 3), (-2.0, 3.0), (0.0, 0.12)), (0.9, 0.2, 0.06), (1, 2), 0.006, wob=0.02)


def yard(w, kind, clothesline=False, patch=False):
    """The old stage flow: a small fenced backyard, grey dust -> green -> full bloom."""
    names = ["grass", "bee", "dust", "flowers", "clover"]
    w.protos(*names)
    rnd = random.Random(14)
    fence = C.MB("Fence", smooth_angle=0)
    for i in range(-12, 13):
        x = i * 0.42
        fence.box("fence" if i % 2 else "fence_dark", C.M((x, 3.4, 0.6), scale=(0.16, 0.05, 1.2)))
        fence.ico("fence", C.M((x, 3.4, 1.22), scale=(0.08, 0.03, 0.06)), subdiv=1)
    for z in (0.35, 0.95):
        fence.box("fence_dark", C.M((0, 3.44, z), scale=(10.4, 0.04, 0.09)))
    for s in (1, -1):
        for j in range(12):
            y = 3.4 - j * 0.42
            fence.box("fence" if j % 2 else "fence_dark", C.M((s * 5.1, y, 0.6), scale=(0.05, 0.16, 1.2)))
    fence.to_object(origin=(0, 0, 0))
    if clothesline:
        cl = C.MB("Clothesline", smooth_angle=0)
        for x, y in ((-1.7, 1.6), (1.5, 2.1)):
            cl.cyl("steel_dark", 0.03, 1.32, seg=8, m=C.M((x, y, 0)))
            cl.box("steel_dark", C.M((x, y, 1.3), scale=(0.05, 0.6, 0.05)))
        for dy in (-0.25, 0.0, 0.25):
            cl.tube("white", (-1.7, 1.6 + dy, 1.29), (1.5, 2.1 + dy, 1.29), 0.011, seg=5)
        cl.to_object(origin=(0, 0, 0))

    def key(x, y, r):
        v = r.random()
        grey = "moon" if v < 0.55 else ("moon_dark" if v < 0.85 else "moon_light")
        green = "grass_a" if v < 0.55 else ("grass_b" if v < 0.9 else "leaf")
        if kind == "grey" or y > 6:
            return grey if y < 6 else ("moon" if v < 0.6 else "moon_dark")
        if kind == "lush":
            return green
        d = min(abs(y - 3.0), (x * x + (y - 0.9) ** 2) ** 0.5 - 0.6)
        return green if d < 0.9 + 0.5 * noise.noise(Vector((x, y, 1.0))) else grey
    ground(key, hills=False)
    if kind != "lush":
        cr = C.MB("Craters", smooth_angle=0)
        for i in range(14):
            x, y = rnd.uniform(-3.5, 3.5), rnd.uniform(-1.8, 2.8)
            if kind == "spread" and (abs(y - 3.0) < 1.0 or x * x + (y - 0.9) ** 2 < 1.6):
                continue
            r = rnd.uniform(0.15, 0.4)
            cr.torus("moon", r, r * 0.09, seg=10, seg2=4, smooth=False,
                     m=C.M((x, y, height(x, y, False) + 0.002), scale=(1, 1, 0.3)))
            cr.cyl("moon_dark", r * 0.95, 0.003, seg=10, m=C.M((x, y, height(x, y, False))), smooth=False)
        cr.to_object(origin=(0, 0, 0))
    w.finalize()
    b = w.p["bee"]
    if patch:
        for i in range(26):
            a, r = rnd.uniform(0, TAU), rnd.uniform(0, 0.4)
            x, y = r * math.cos(a), 0.6 + r * math.sin(a) * 0.7
            inst(w.p["lavender"], (x, y, height(x, y, False)), rnd.uniform(2.2, 2.9), (0, 0, rnd.uniform(0, TAU)))
        for i in range(3):
            w.orbit(b, (rnd.uniform(-0.2, 0.2), 0.45, 0.5), (0.16, 0.1, 0.06), (1, 2, 3), BEE_S * 1.2)
    if kind == "spread":
        for i in range(60):
            x = rnd.uniform(-4.8, 4.8)
            y = rnd.uniform(2.6, 3.2)
            inst(w.p[rnd.choice(("lavender", "purple", "blue"))], (x, y, 0), rnd.uniform(1.7, 2.3),
                 (0, 0, rnd.uniform(-0.5, 0.5)))
        for i in range(40):
            a, r = rnd.uniform(0, TAU), rnd.uniform(0, 0.8)
            x, y = r * math.cos(a), 0.9 + r * math.sin(a) * 0.7
            inst(w.p[rnd.choice(("lavender", "purple"))], (x, y, height(x, y, False)), rnd.uniform(1.7, 2.3),
                 (0, 0, rnd.uniform(0, TAU)))
        for x, y in scatter(rnd, 120, -1.5, 3.2, avoid_hive=False):
            if abs(y - 3.0) < 1.0 or x * x + (y - 0.9) ** 2 < 1.6:
                inst(w.p["clover"], (x, y, height(x, y, False)), rnd.uniform(1.6, 2.2), (0, 0, rnd.uniform(0, TAU)))
        for i in range(12):
            w.orbit(b, (rnd.uniform(-2.5, 2.5), rnd.uniform(0.5, 3.0), rnd.uniform(0.3, 0.7)),
                    (rnd.uniform(0.1, 0.3), 0.1, 0.06), (1, 2, 3), BEE_S)
    if kind == "lush":
        w.p["grass"].hide_render = True
        for x, y in scatter(rnd, 200, -1.9, 3.3, avoid_hive=False, bias=1.2):
            inst(w.p["grass"], (x, y, height(x, y, False)), rnd.uniform(2.0, 3.0), (0, 0, rnd.uniform(0, TAU)))
        for x, y in scatter(rnd, 420, -1.5, 3.3, avoid_hive=False, bias=1.1):
            inst(w.p[rnd.choice(("lavender", "purple", "blue", "lavender"))], (x, y, height(x, y, False)),
                 rnd.uniform(1.5, 2.3) * (0.8 if y < -0.5 else 1.0), (0, 0, rnd.uniform(0, TAU)))
        for x, y in scatter(rnd, 260, -1.9, 3.3, avoid_hive=False):
            inst(w.p["clover"], (x, y, height(x, y, False)), rnd.uniform(1.6, 2.2), (0, 0, rnd.uniform(0, TAU)))
        for i in range(48):
            w.orbit(b, (rnd.uniform(-2.8, 2.8), rnd.uniform(-1.2, 3.0), rnd.uniform(0.2, 0.7)),
                    (rnd.uniform(0.1, 0.35), 0.12, 0.06), (1, rnd.choice((2, 3)), rnd.choice((3, 4))),
                    BEE_S * rnd.choice((1.0, 1.0, 1.5)))
    if kind in ("grey",):
        w.particles(w.p["dust"], 140, ((-4, 3), (-1.8, 3.2), (0.0, 0.25)), (1.2, 0.3, 0.12), (1, 2), 0.007,
                    wob=0.03)


# ============================================================================================ forecast (the hook)
# room_hook_1 ("Let my hive die, and angry swarms ravage the Earth...") in the meadow window: forecast_peaceful, a
# seamless loop of a sunny Paris park, is cut on "angry swarms" to forecast_ravaged: red sky, dark swarms, and a giant
# laser-eyed bee slices the Eiffel Tower, which topples while a tiny tourist is flung off the top (cartoon, nobody hurt
# on screen). The one-shot ends on the first frame of forecast_aftermath, a seamless loop. Unity's ForecastMoment times
# the SFX to the beats below (cut = t 0): laser 0.10-0.62, the top tips at 0.62 and lands at 1.55, fling at 1.35.
PARIS = {"eiffel": ("snow", "#7B5C43"), "eiffel_dark": ("snow_shade", "#54402F"), "eiffel_light": ("moon_light", "#B08D63"),
         "facade": ("fence", "#ECDDBE"), "facade_dark": ("fence_dark", "#CDB88F"), "roof": ("moon", "#66737F"),
         "roof_dark": ("moon_dark", "#4B5661"), "skin": ("hornet", "#EDB991"), "skin_dark": ("hornet_dark", "#A8714C"),
         "path": ("plastic", "#E3D3AE"), "laser": ("wasp", "#FF2A18"), "laser_core": ("smoke_wing", "#FFE4DA"),
         "eye_glow": ("stamp_green", "#FF3A1C"), "scorch": ("terracotta", "#33261F")}
RAVAGED = dict(PARIS, facade=("fence", "#A38F76"), facade_dark=("fence_dark", "#7A6955"), roof=("moon", "#3E454F"),
               roof_dark=("moon_dark", "#2B3037"), path=("plastic", "#8F7F68"))
SCORCHED = {"leaf": "#6E5A3A", "leaf_dark": "#4E3F2A", "purple": "#6B4A5A", "lavender": "#7A6070", "blue": "#4E4E66",
            "grass_a": "#6A5238", "grass_b": "#54412E", "cream": "#B9A98C", "white": "#D2C9BA"}
TOWER_AT = Vector((4.6, 19.0, 0.0))
CUT_Z = 1.9
# (z, half-width of the four corner legs), bottom to top
TOWER = [(0.0, 1.55), (0.6, 1.25), (1.2, 1.0), (1.9, 0.82), (2.6, 0.66), (3.6, 0.5), (4.6, 0.38), (5.9, 0.26),
         (7.2, 0.15)]
PICNICS = [(-2.2, 3.4, 20.0), (3.3, 3.2, -25.0), (-1.6, 10.5, 5.0)]
RAV_LOOP = 6.0  # aftermath loop (s); every motion in forecast_ravaged repeats whole cycles of it
RAV_ONESHOT = 7.0
LASER_T = (0.10, 0.62, 0.75)  # beams on, sweep done, beams off
FALL_T = (0.62, 1.55)  # the top tips, lands
FLING_T = 1.35
FALL_DIR = Vector((0.82, -0.57, 0.0)).normalized()  # the top falls right and toward the camera (reads bigger)


def smooth(a, b, t):
    u = max(0.0, min(1.0, (t - a) / (b - a)))
    return u * u * (3 - 2 * u)


def tw(z):
    """Half-width of the tower legs at height z."""
    for (z0, w0), (z1, w1) in zip(TOWER, TOWER[1:]):
        if z <= z1:
            return w0 + (w1 - w0) * (z - z0) / (z1 - z0)
    return TOWER[-1][1]


def eiffel(split):
    """Low-poly Eiffel Tower at TOWER_AT: four curved legs, X-bracing, arches, two platforms, a cabin and a mast.
    split: two objects, TowerBase (below CUT_Z) and TowerTop (pivot on the axis at CUT_Z, so it can tip over)."""
    base, top = C.MB("TowerBase", smooth_angle=0), C.MB("TowerTop", smooth_angle=0)
    corners = [(1, 1), (1, -1), (-1, -1), (-1, 1)]
    for (z0, w0), (z1, w1) in zip(TOWER, TOWER[1:]):
        mb = base if z1 <= CUT_Z + 1e-6 else top
        r0, r1 = 0.15 * (1 - z0 / 9.0) + 0.03, 0.15 * (1 - z1 / 9.0) + 0.03
        for sx, sy in corners:  # legs
            mb.tube("eiffel", (sx * w0, sy * w0, z0), (sx * w1, sy * w1, z1), r0, r1, seg=6)
        for i in range(4):  # girder ring at the top of the band, and X-bracing on each face (not under the arches)
            (ax, ay), (bx, by) = corners[i], corners[(i + 1) % 4]
            mb.tube("eiffel_dark", (ax * w1, ay * w1, z1), (bx * w1, by * w1, z1), 0.035, seg=5)
            if z0 >= 1.2:
                mb.tube("eiffel_dark", (ax * w0, ay * w0, z0), (bx * w1, by * w1, z1), 0.022, seg=4)
                mb.tube("eiffel_dark", (bx * w0, by * w0, z0), (ax * w1, ay * w1, z1), 0.022, seg=4)
    for i in range(4):  # the big arches between the legs
        (ax, ay), (bx, by) = corners[i], corners[(i + 1) % 4]
        pts = []
        for k in range(9):
            a = math.pi * k / 8
            z = 0.12 + 0.95 * math.sin(a)
            w = tw(z) + 0.02
            u = 0.5 - 0.5 * math.cos(a)
            pts.append(((ax + (bx - ax) * u) * w, (ay + (by - ay) * u) * w, z))
        base.limb("eiffel_light", pts, 0.05, 0.05, seg=5)
    for z, extra, thick, mb in ((1.2, 0.35, 0.16, base), (2.6, 0.25, 0.12, top)):  # platforms with a light rim
        w = 2 * tw(z) + extra
        mb.box("eiffel_dark", C.M((0, 0, z), scale=(w, w, thick)))
        mb.box("eiffel_light", C.M((0, 0, z + thick / 2 + 0.02), scale=(w + 0.04, w + 0.04, 0.04)))
    top.box("eiffel_light", C.M((0, 0, 7.36), scale=(0.42, 0.42, 0.34)))  # the top cabin
    top.box("eiffel_dark", C.M((0, 0, 7.56), scale=(0.5, 0.5, 0.06)))
    top.cyl("eiffel", 0.17, 0.32, seg=6, m=C.M((0, 0, 7.58)), r2=0.03)
    top.tube("eiffel_dark", (0, 0, 7.9), (0, 0, 8.2), 0.02, seg=4)
    if not split:  # one static tower: the base keeps everything
        return base.to_object(origin=(0, 0, 0)), top.to_object(origin=(0, 0, 0))
    return base.to_object(origin=(0, 0, 0)), top.to_object(origin=(0, 0, CUT_Z))


def person(name, shirt, hair, pose="sit", arm_up=False):
    """A seated (or standing, arms out) cartoon person ~0.45 m: one mesh. Returns (object, waving arm or None)."""
    mb = C.MB(name, smooth_angle=60)
    stand = pose == "stand"
    hip = 0.32 if stand else 0.06
    mb.sphere(shirt, C.M((0, 0, hip + 0.11), scale=(0.105, 0.075, 0.15)), seg=8, rings=5)
    mb.sphere("skin", C.M((0, 0, hip + 0.32), scale=0.075), seg=8, rings=5)
    mb.sphere(hair, C.M((0, 0.01, hip + 0.345), scale=(0.078, 0.078, 0.055)), seg=8, rings=4)
    if stand:  # the tourist: legs apart, arms up and out
        for s in (1, -1):
            mb.limb("ink", [(0.04 * s, 0, hip), (0.08 * s, 0, hip * 0.5), (0.1 * s, 0, 0.0)], 0.03, 0.025, seg=5)
            mb.limb(shirt, [(0.1 * s, 0, hip + 0.2), (0.2 * s, 0, hip + 0.3), (0.26 * s, 0, hip + 0.42)], 0.025, seg=5,
                    end_ball=0.03, end_color="skin")
    else:  # sitting cross-legged on the blanket, hands in the lap
        for s in (1, -1):
            mb.limb("ink", [(0.05 * s, 0, hip), (0.13 * s, -0.12, 0.05), (-0.02 * s, -0.17, 0.03)], 0.032, 0.028, seg=5)
            if not (arm_up and s == 1):
                mb.limb(shirt, [(0.1 * s, 0, hip + 0.2), (0.13 * s, -0.05, hip + 0.1), (0.05 * s, -0.12, hip + 0.06)],
                        0.024, seg=5, end_ball=0.028, end_color="skin")
    ob = mb.to_object(origin=(0, 0, 0))
    arm = None
    if arm_up:  # a waving arm, pivot at the shoulder
        am = C.MB(name + "Arm", smooth_angle=60)
        am.limb(shirt, [(0, 0, 0), (0.06, 0, 0.1), (0.07, 0, 0.21)], 0.024, seg=5, end_ball=0.03, end_color="skin")
        arm = am.to_object(origin=(0, 0, 0))
    return ob, arm


def paris_ground(kind):
    grass = {"green": ("grass_a", "grass_b", "leaf"), "scorched": ("scorch", "ash", "dry_b")}[kind]

    def key(x, y, r):
        u = (y + 1.0) / (TOWER_AT.y + 1.0)
        ax = 1.4 + (TOWER_AT.x - 1.4) * u
        plaza = math.hypot(x - TOWER_AT.x, (y - TOWER_AT.y) * 0.8) < 3.3
        if plaza or (-1.0 < y < TOWER_AT.y and abs(x - ax) < 0.45 + 0.03 * y):
            return "path" if kind == "green" or r.random() < 0.7 else "ash"
        v = r.random()
        return grass[0] if v < 0.55 else (grass[1] if v < 0.9 else grass[2])
    ground(key, hills=False)


def paris_city(ravaged):
    """Haussmann blocks (cream facades, zinc mansard roofs, rows of windows) behind and beside the park.
    Returns the roof spots fires can sit on."""
    rnd = random.Random(48)
    mb = C.MB("Paris", smooth_angle=0)
    roofs = []

    def block(cx, cy, w, d, h, yaw, windows=True):
        R = C.M((cx, cy, 0), (0, 0, yaw))
        mb.box("facade", R @ C.M((0, 0, h / 2), scale=(w, d, h)))
        mb.box("roof", R @ C.M((0, 0, h + 0.27), scale=(w * 0.96, d * 0.9, 0.54)),
               lfn=lambda v: Vector((v.x * (1 - 0.3 * (v.z + 0.5)), v.y * (1 - 0.45 * (v.z + 0.5)), v.z)))
        mb.box("facade_dark", R @ C.M((0, -d / 2 - 0.02, h - 0.04), scale=(w + 0.06, 0.08, 0.08)))
        mb.box("facade_dark", R @ C.M((0, -d / 2 - 0.02, 0.62), scale=(w, 0.08, 0.05)))
        for j in range(2):
            mb.box("facade_dark", R @ C.M((rnd.uniform(-w / 3, w / 3), rnd.uniform(-d / 5, d / 5), h + 0.62),
                                          scale=(0.16, 0.16, 0.34)))
        if windows:
            floors, cols = max(2, int(h / 0.52)), max(2, int(w / 0.55))
            for i in range(floors):
                for j in range(cols):
                    x = (j + 0.5) / cols * w - w / 2
                    z = 0.3 + i * (h - 0.45) / floors
                    mb.box("ink", R @ C.M((x, -d / 2 - 0.012, z + 0.12), scale=(0.17, 0.02, 0.25)))
            for j in range(cols):  # dormers in the roof
                x = (j + 0.5) / cols * w - w / 2
                mb.box("facade", R @ C.M((x, -d * 0.36, h + 0.3), scale=(0.18, 0.16, 0.22)))
        roofs.append(((R @ Vector((rnd.uniform(-w / 4, w / 4), 0, h + 0.5))).to_tuple(), w))

    x = -27.0
    while x < 32:  # back rows
        w = rnd.uniform(3.1, 3.6)
        block(x, 28 + rnd.uniform(-0.6, 0.6), w, 2.6, rnd.uniform(2.1, 2.9), 0)
        block(x + 1.8, 33.5 + rnd.uniform(-0.6, 0.6), w, 2.6, rnd.uniform(2.6, 3.4), 0)
        x += w + 0.25
    for i in range(16):  # far band: plain blocks hide the end of the ground
        block(-36 + 4.8 * i, 42 + rnd.uniform(-1, 1), 4.4, 3, rnd.uniform(3.4, 5.2), 0, windows=False)
    y = 6.0
    while y < 26:  # side rows face the park
        w = rnd.uniform(3.0, 3.5)
        block(-11.5, y, w, 2.6, rnd.uniform(2.1, 2.8), 90)
        block(16.5, y, w, 2.6, rnd.uniform(2.1, 2.8), -90)
        y += w + 0.3
    mb.to_object(origin=(0, 0, 0))
    return roofs


def paris_trees(w, kind):
    rnd = random.Random(36)
    for x0, ys in ((-4.9, (3.6, 6.2, 8.8, 11.4, 14.0, 16.6)), (12.4, (6.0, 8.6, 11.2, 13.8, 16.4))):
        for y in ys:
            inst(w.p["oak_" + kind], (x0 + rnd.uniform(-0.2, 0.2), y, -0.05), rnd.uniform(1.1, 1.3),
                 (0, 0, rnd.uniform(0, TAU)))


def picnic(w, x, y, yaw, people, ravaged):
    """A checked blanket, a basket and fruit (bees pollinate it), and seated people (none in the ravaged world)."""
    R = C.M((x, y, 0.004), (0, 0, yaw))
    mb = C.MB("Picnic", smooth_angle=0)
    for i in range(4):
        for j in range(4):
            key = ("red" if (i + j) % 2 else "white") if not ravaged else ("scorch" if (i + j) % 2 else "ash")
            mb.box(key, R @ C.M(((i - 1.5) * 0.24, (j - 1.5) * 0.24, 0.0), scale=(0.24, 0.24, 0.012)))
    tip = 70 if ravaged else 0
    mb.box("wood", R @ C.M((0.3, 0.3, 0.07), (tip, 0, 15), (0.2, 0.14, 0.13)), bevel=(0.01, 1))
    mb.torus("wood_dark", 0.08, 0.01, seg=10, seg2=4, m=R @ C.M((0.3, 0.3, 0.14), (90 + tip, 0, 15), (1, 1, 1)))
    if not ravaged:
        for i, (fx, fy) in enumerate(((-0.12, 0.08), (-0.05, 0.14), (0.02, 0.06), (0.1, 0.16), (-0.18, -0.02))):
            mb.sphere("red" if i % 2 else "pink", R @ C.M((fx, fy, 0.035), scale=0.035), seg=6, rings=4)
            mb.box("leaf", R @ C.M((fx, fy, 0.07), scale=(0.02, 0.02, 0.012)))
    mb.to_object(origin=(0, 0, 0))
    if ravaged:
        return
    shirts = (("blue", "leather_dark"), ("pink", "honey_dark"), ("leaf", "charcoal"))
    spots = ((-0.5, 0.05), (0.5, 0.05)) if people == 2 else ((0.0, 0.5),)
    for k in range(people):
        shirt, hair = shirts[(int(abs(x) * 7) + k) % 3]
        ob, arm = person("Picnicker", shirt, hair, arm_up=k == 0)
        pw = R @ Vector((spots[k][0], spots[k][1], 0.0))
        face = math.atan2(x - pw.x, -(y - pw.y))  # local -Y (the face) toward the middle of the blanket
        ob.location = (pw.x, pw.y, 0.0)
        ob.rotation_euler = (0, 0, face)
        ob.scale = (1.15,) * 3
        p = w.rnd.uniform(0, TAU)
        ANIM.append(lambda ph, ob=ob, face=face, p=p: setattr(ob, "rotation_euler", (0, 0.04 * wave(ph, 2, p), face)))
        if arm is not None:  # waving
            arm.scale = (1.15,) * 3
            arm.location = ob.matrix_basis @ Vector((0.1, 0, 0.26))
            arm.rotation_mode = "XYZ"
            ANIM.append(lambda ph, arm=arm, face=face, p=p: setattr(arm, "rotation_euler",
                                                                  (0, 0.5 + 0.35 * wave(ph, 4, p), face)))


def tourist(name="Tourist"):
    ob, _ = person(name, "honey", "charcoal", pose="stand")
    ob.rotation_mode = "XYZ"
    ob.scale = (1.0,) * 3
    return ob


def giant_bee():
    """An angry giant worker bee (front = +X, ~2.6 m): charcoal head, glowing red eyes under angry brows, flapping
    wings. Returns (root empty, eyes in root space, wings)."""
    root = C.empty("GiantBee", (0, 0, 0), size=0.5)
    mb = C.MB("GiantBeeBody", mat="bee", smooth_angle=60)
    mb.sphere("charcoal", C.M((1.0, 0, 0.12), scale=(0.42, 0.4, 0.42)), seg=10, rings=7)
    mb.sphere("honey", C.M((0.35, 0, 0.12), scale=(0.48, 0.45, 0.48)), seg=10, rings=7)
    m = C.M((-0.62, 0, 0.0), (0, 90, 0), (0.55, 0.55, 0.85))
    inv = m.inverted()
    mb.sphere(lambda c, n: C.bands((inv @ c).z, BEE.STRIPES), m, seg=12, rings=9)
    mb.lathe("charcoal", [(0.09, 0.0), (0.0, 0.3)], seg=6, m=C.frame((-1.4, 0, -0.02), (-1, 0, 0)), cap_top=False,
             smooth=False)
    for s in (1, -1):
        mb.box("charcoal", C.M((1.26, 0.2 * s, 0.47), (28 * s, 0, 0), (0.1, 0.26, 0.07)))  # angry brows
        mb.limb("charcoal", [(1.2, 0.12 * s, 0.45), (1.45, 0.3 * s, 0.85), (1.7, 0.42 * s, 0.95)], 0.025, seg=5,
                end_ball=0.06)
        for k, lx in enumerate((0.55, 0.3, 0.05)):
            mb.limb("charcoal", [(lx, 0.25 * s, -0.2), (lx + 0.1, 0.45 * s, -0.45), (lx - 0.05 + 0.08 * k, 0.5 * s, -0.75)],
                    0.035, 0.025, seg=5)
    body = mb.to_object(origin=(0, 0, 0))
    body.parent = root
    em = C.MB("GiantBeeEyes", mat="eyeglow", smooth_angle=60)
    eyes = [Vector((1.36, 0.21 * s, 0.26)) for s in (1, -1)]
    for e in eyes:
        em.sphere("eye_glow", C.M(e, scale=(0.13, 0.15, 0.14)), seg=8, rings=5)
    ey = em.to_object(origin=(0, 0, 0))
    ey.parent = root
    wings = []
    for s in (1, -1):
        wm = C.MB("GiantWing", mat="veil_thin", smooth_angle=0)
        pts = [(0.12, 0.0), (0.22, 0.45), (0.05, 1.2), (-0.25, 1.45), (-0.55, 1.3), (-0.5, 0.75), (-0.25, 0.15)]
        wm.poly("sky", [(x, y * s) for x, y in pts], 0.02)
        wo = wm.to_object(origin=(0, 0, 0))
        wo.parent = root
        wo.location = (0.35, 0.12 * s, 0.55)
        wo.rotation_mode = "XYZ"
        wings.append((wo, s))
    return root, eyes, wings, ey


def specks(w, center, radius, n, k=(1, 2), scale=0.05, buzz=0.18):
    """A dark swarm far away: n specks on their own orbits inside an ellipsoid."""
    rnd = w.rnd
    c = Vector(center)
    for i in range(n):
        off = Vector((rnd.uniform(-1, 1) * radius[0], rnd.uniform(-1, 1) * radius[1], rnd.uniform(-1, 1) * radius[2])) * 0.5
        r = (rnd.uniform(0.3, 1.0) * radius[0], rnd.uniform(0.2, 0.7) * radius[1], rnd.uniform(0.2, 0.7) * radius[2])
        w.orbit(w.p["speck"], c + off, r, (rnd.choice(k), rnd.choice(k) + 1, rnd.choice(k) + 2),
                scale * rnd.uniform(0.7, 1.3), buzz=buzz, kb=(29, 31, 37))


def gate(objs_before, t0, t1, clock):
    """Scale every mover created since objs_before by smoothstep(t0, t1, clock(ph)) (runs after their own setters)."""
    new = [o for o in bpy.data.objects if o.name not in objs_before]

    def f(ph):
        k = smooth(t0, t1, clock(ph))
        if k >= 1.0:
            return
        for o in new:
            o.scale = tuple(v * k for v in o.scale)
    return f


@scene("forecast_peaceful", zenith="#4E9EDC", horizon="#D7EEF7", sun=3.4, sun_col="#FFF1D8", sun_dir=(-40, 42),
       cam="paris", alias=PARIS, stills=[("forecast_peaceful", 0.5)])
def s_forecast_peaceful(w):
    w.protos("hive", "flowers", "grass", "bee", "clover", "oak_leafy", "cloud")
    w.finalize()
    paris_ground("green")
    paris_city(False)
    for o in eiffel(split=False):
        o.location = TOWER_AT
    t = tourist()
    t.location = TOWER_AT + Vector((0.18, -0.18, 7.6))
    paris_trees(w, "leafy")
    w.clouds(4)
    avoid = tuple((x, y, 0.75) for x, y, _ in PICNICS) + ((1.6, 1.5, 0.5), (1.9, 3.0, 0.45))
    w.meadow_plants(n_flowers=170, n_grass=170, n_clover=90, y1=7.0, avoid=avoid)
    for x, y, yaw in PICNICS:
        picnic(w, x, y, yaw, 1 if y > 8 else 2, False)
    b = w.p["bee"]
    w.stream(b, HIVE_DOOR, (-1.5, 1.6, 0.3), 3, 1, BEE_S)
    w.stream(b, (1.2, 1.0, 0.3), HIVE_DOOR, 3, 1, BEE_S)
    for c in ((-1.0, -0.5, 0.3), (0.95, -0.3, 0.32), (-2.0, 3.3, 0.22), (3.2, 3.0, 0.22), (-0.4, 1.8, 0.3)):
        w.orbit(b, c, (0.16, 0.11, 0.05), (1, 2, 3), BEE_S)


@scene("forecast_ravaged", zenith="#3B0A12", horizon="#FF6A2E", amb="#B5523A", amb_k=0.6, sun=2.2, sun_col="#FF8A50",
       sun_dir=(150, 14), glare=True, cam="paris", alias=RAVAGED, palette=SCORCHED, oneshot=RAV_ONESHOT,
       loop=RAV_LOOP, loop_id="forecast_aftermath",
       stills=[("forecast_laser", 0.42 / RAV_LOOP), ("forecast_aftermath", (RAV_ONESHOT + 3.0) / RAV_LOOP)])
def s_forecast_ravaged(w):
    w.protos("hive", "flowers", "grass", "bee", "oak_dusk", "flame", "spark", "smoke_dark")
    w.p["speck"] = proto_spark("Speck", "charcoal", "pal")
    w.finalize()
    clock = lambda ph: ph * RAV_LOOP  # noqa: E731  (scene seconds; 0 = the cut on "angry swarms")
    rnd = random.Random(66)
    paris_ground("scorched")
    roofs = paris_city(True)
    paris_trees(w, "dusk")
    w.meadow_plants(n_flowers=110, n_grass=150, n_clover=0, droop=60, sway=False, y1=7.0,
                    avoid=tuple((x, y, 0.75) for x, y, _ in PICNICS))
    for x, y, yaw in PICNICS:
        picnic(w, x, y, yaw, 0, True)

    # ---- the tower: the base stays; the top tips over the cut's front-right edge until its tip hits the ground,
    # leaving a diagonal of girders from the stub down into the park (reads at window size, unlike lying flat)
    base, top = eiffel(split=True)
    base.location = TOWER_AT
    top.rotation_mode = "QUATERNION"
    axis = Vector((0, 0, 1)).cross(FALL_DIR)  # tipping about this axis moves +Z toward FALL_DIR
    wc = tw(CUT_Z)
    edge = FALL_DIR * wc + Vector((0, 0, CUT_Z))
    phi_end = math.radians(112)

    def top_pose(t):
        """(location of TowerTop's origin, the cut centre; tip angle)."""
        u = max(0.0, min(1.0, (t - FALL_T[0]) / (FALL_T[1] - FALL_T[0])))
        phi = phi_end * u * u
        if t > FALL_T[1]:  # a small rebound on landing
            d = t - FALL_T[1]
            phi -= 0.06 * math.exp(-8 * d) * abs(math.sin(16 * d))
        return TOWER_AT + edge + Matrix.Rotation(phi, 3, axis) @ (-FALL_DIR * wc), phi

    def f_top(ph):
        loc, phi = top_pose(clock(ph))
        top.location = loc
        top.rotation_quaternion = Quaternion(axis, phi)
    ANIM.append(f_top)
    # glowing cut on the stub, lit as the beam passes each leg, cooling to a steady glow
    cuts = []
    for sx, sy in ((-1, -1), (-1, 1), (1, -1), (1, 1)):
        glow = C.MB("CutGlow", mat="laser", smooth_angle=0)
        glow.box("laser", C.M((0, 0, 0), scale=(0.26, 0.26, 0.05)))
        o = glow.to_object(origin=(0, 0, 0))
        o.location = TOWER_AT + Vector((sx * wc, sy * wc, CUT_Z))
        cuts.append((o, LASER_T[0] + (LASER_T[1] - LASER_T[0]) * (0.1 if sx < 0 else 0.9)))

    def f_cut(ph):
        t = clock(ph)
        for o, lit in cuts:
            k = smooth(lit - 0.05, lit + 0.05, t) * (1.0 - 0.45 * smooth(1.2, 3.0, t)) * (1.0 + 0.08 * wave(ph, 3))
            o.scale = (max(k, 1e-4),) * 3
    ANIM.append(f_cut)

    # ---- the tourist on the top cabin, flung off as the top whips over (Unity plays the scream at FLING_T)
    tr, _ = person("Tourist", "sky", "charcoal", pose="stand")
    tr.rotation_mode = "QUATERNION"
    tr.scale = (1.6,) * 3  # cartoon-big, so the flight reads at window size
    local = Vector((0.18, -0.18, 7.6 - CUT_Z))
    p_fling, phi_fling = top_pose(FLING_T)
    p_fling = p_fling + Matrix.Rotation(phi_fling, 3, axis) @ local
    v0, g = Vector((2.4, 4.0, 6.5)), 7.0

    def f_tourist(ph):
        t = clock(ph)
        if t < FLING_T:
            loc, phi = top_pose(t)
            tr.location = loc + Matrix.Rotation(phi, 3, axis) @ local
            tr.rotation_quaternion = Quaternion(axis, phi)
            tr.hide_render = False
            return
        d = t - FLING_T  # flung up and away, cartwheeling, down behind the roofs (the scream falls away with him)
        p = p_fling + v0 * d + Vector((0, 0, -0.5 * g * d * d))
        tr.location = p
        tr.rotation_quaternion = Quaternion(axis, phi_fling + TAU * 2.2 * d) @ Quaternion((0, 0, 1), 2.0 * d)
        tr.hide_render = d > 2.6 or p.z < -0.3
    ANIM.append(f_tourist)

    # ---- the giant bee and its laser eyes
    root, eyes, wings, eye_ob = giant_bee()
    B = Vector((-0.9, 16.5, 4.5))
    yaw, pitch, big = math.radians(13), math.radians(20), 1.45
    rot = Euler((0, pitch, yaw), "XYZ").to_matrix() * big
    root.rotation_euler = (0, pitch, yaw)
    root.scale = (big,) * 3
    eye_light = bpy.data.lights.new("EyeLight", "POINT")
    eye_light.color, eye_light.shadow_soft_size = C.hex_rgb("#FF3A1C"), 0.3
    eye_lo = link(bpy.data.objects.new("EyeLight", eye_light))

    def bee_at(ph):
        return B + Vector((0.12 * wave(ph, 1, 0.4), 0.08 * wave(ph, 2, 1.1), 0.16 * wave(ph, 2)))

    def f_bee(ph):
        root.location = bee_at(ph)
        for wo, s in wings:
            wo.rotation_euler = (s * (0.35 + 0.45 * wave(ph, 42)), 0, 0)
        k = 1.0 + 0.12 * wave(ph, 6) + 0.25 * (1 - smooth(0.0, 0.2, clock(ph)))
        eye_ob.scale = (k, k, k)
        eye_lo.location = root.location + rot @ Vector((1.7, 0, 0.3))
        eye_light.energy = 60 * k
    ANIM.append(f_bee)
    beams = []
    for e in eyes:
        core = C.MB("Laser", mat="laser", smooth_angle=0)
        core.cyl("laser_core", 1.0, 1.0, seg=8, smooth=True)
        halo = C.MB("LaserHalo", mat="laser_halo", smooth_angle=0)
        halo.cyl("laser", 1.0, 1.0, seg=10, smooth=True)
        cs, hs = core.to_object(origin=(0, 0, 0)), halo.to_object(origin=(0, 0, 0))
        for o in (cs, hs):
            o.rotation_mode = "QUATERNION"
        beams.append((e, cs, hs))

    def target(t):
        u = smooth(LASER_T[0] + 0.05, LASER_T[1], t)
        return TOWER_AT + Vector((-wc + 2 * wc * u, -wc - 0.05, CUT_Z))

    def f_beams(ph):
        t = clock(ph)
        on = LASER_T[0] <= t < LASER_T[2]
        grow = smooth(LASER_T[0], LASER_T[0] + 0.08, t)
        thin = 1.0 - smooth(LASER_T[1], LASER_T[2], t)
        for e, cs, hs in beams:
            for o in (cs, hs):
                o.hide_render = not on
            if not on:
                continue
            eye = bee_at(ph) + rot @ e
            d = target(t) - eye
            q = d.to_track_quat("Z", "Y")
            for o, r in ((cs, 0.045), (hs, 0.13)):
                o.location = eye
                o.rotation_quaternion = q
                rr = r * (0.85 + 0.15 * wave(ph, 60)) * thin
                o.scale = (rr, rr, d.length * grow)
    ANIM.append(f_beams)
    # sparks where the beam cuts, and a shower when the top lands
    sparks = []
    for i in range(80):
        b0 = rnd.uniform(LASER_T[0] + 0.05, LASER_T[1])
        v = Vector((rnd.uniform(-1.8, 1.8), rnd.uniform(-2.0, 0.2), rnd.uniform(0.6, 3.4)))
        sparks.append((mover(w.p["spark"], 0.05), b0, target(b0), v, rnd.uniform(0.3, 0.65)))
    side = Vector((0, 0, 1)).cross(FALL_DIR)
    land = []
    for i in range(40):
        p0 = TOWER_AT + FALL_DIR * rnd.uniform(0.5, 6.5) + side * rnd.uniform(-0.6, 0.6) + Vector((0, 0, 0.3))
        v = Vector((rnd.uniform(-1.2, 1.2), rnd.uniform(-1.5, 0.8), rnd.uniform(1.5, 4.0)))
        land.append((mover(w.p["spark"], 0.035), FALL_T[1] + rnd.uniform(0.0, 0.25), p0, v, rnd.uniform(0.4, 0.9)))

    def f_sparks(ph):
        t = clock(ph)
        for o, b0, p0, v, life in sparks + land:
            d = t - b0
            if d < 0 or d > life:
                o.scale = (1e-4,) * 3
                continue
            o.location = p0 + v * d + Vector((0, 0, -4.5 * d * d))
            e = 0.05 * (1 - d / life)
            o.scale = (e, e, e)
    ANIM.append(f_sparks)
    # dust when the top lands: pale puffs along the fallen tower, gone before the loop starts
    puffs = []
    for i in range(18):
        p0 = TOWER_AT + FALL_DIR * rnd.uniform(0.6, 6.6) + side * rnd.uniform(-0.8, 0.8) + Vector((0, 0, 0.25))
        puffs.append((mover(w.p["smoke_dark"], 0.1), p0, Vector((rnd.uniform(-0.4, 0.4), rnd.uniform(-0.5, 0.3), 0.9)),
                      rnd.uniform(0.0, 0.2)))

    def f_puffs(ph):
        t = clock(ph)
        for o, p0, v, dl in puffs:
            d = t - FALL_T[1] - dl
            if d < 0 or d > 2.2:
                o.scale = (1e-4,) * 3
                continue
            o.location = p0 + v * d
            e = (0.25 + 0.55 * d) * env(d / 2.2, 0.08, 0.5)
            o.scale = (e * 1.4, e, e)
    ANIM.append(f_puffs)

    # ---- fire: the park and the roofs burn from the cut on; the fallen tower catches as it lands
    def flame_at(p, s, start=None):
        o = inst(w.p["flame"], p, s, (0, 0, rnd.uniform(0, TAU)))
        k, p1, p2 = rnd.choice((9, 12, 15)), rnd.uniform(0, TAU), rnd.uniform(0, TAU)

        def f(ph, o=o, s=s, k=k, p1=p1, p2=p2, start=start):
            g = 1.0 if start is None else smooth(start, start + 0.6, clock(ph))
            h = 1 + 0.28 * wave(ph, k, p1) + 0.12 * wave(ph, 2 * k + 3, p2)
            wd = 1 + 0.12 * wave(ph, k + 3, p2)
            o.scale = (max(1e-4, s * wd * g), max(1e-4, s * wd * g), max(1e-4, s * h * g))
        ANIM.append(f)
    fires = []
    while len(fires) < 9:  # clusters of flames in the park
        y = rnd.uniform(4.0, 17.0)
        x = rnd.uniform(-1, 1) * (0.55 * (y + 3.2))
        if (abs(x) < 1.6 and y < 6.0) or any((x - fx) ** 2 + (y - fy) ** 2 < 6.0 for fx, fy in fires):
            continue
        fires.append((x, y))
        for k in range(rnd.choice((3, 4, 5))):
            flame_at((x + rnd.uniform(-0.6, 0.6), y + rnd.uniform(-0.4, 0.4), -0.02),
                     rnd.uniform(0.45, 1.1) * (0.8 + 0.04 * y))
    for (rx, ry, rz), bw in roofs:
        if rnd.random() < 0.3 and -20 < rx < 26 and ry < 36:
            flame_at((rx, ry, rz - 0.2), rnd.uniform(1.0, 1.6))
    for i in range(14):
        u = (i + 0.5) / 14
        flame_at(TOWER_AT + FALL_DIR * (0.9 + 6.0 * u + rnd.uniform(-0.2, 0.2)) + side * rnd.uniform(-0.5, 0.5),
                 rnd.uniform(0.7, 1.2), start=FALL_T[1] + 0.05 + 0.9 * u)
    mid = TOWER_AT + FALL_DIR * 3.5 + Vector((0, 0, 1.5))
    for loc, energy, col, start in (((0, 6.0, 1.2), 500, "#FF7A30", None), (mid, 900, "#FF8A3A", FALL_T[1])):
        ld = bpy.data.lights.new("FireLight", "POINT")
        ld.color, ld.shadow_soft_size = C.hex_rgb(col), 2.0
        lo = link(bpy.data.objects.new("FireLight", ld))
        lo.location = loc

        def f_light(ph, ld=ld, energy=energy, start=start):
            g = 1.0 if start is None else smooth(start, start + 0.5, clock(ph))
            ld.energy = energy * g * (1 + 0.15 * wave(ph, 12) + 0.08 * wave(ph, 31))
        ANIM.append(f_light)
    soot = proto_blob("Soot", "ash", "veil_thin")
    C.REGISTRY[:] = [soot]
    C.finalize(w.tmp)
    before = {o.name for o in bpy.data.objects}
    for fx, fy in fires[:6]:
        if fy > 8:
            w.particles(soot, 4, ((fx - 0.3, fx + 0.3), (fy - 0.3, fy + 0.3), (1.0, 1.6)), (0.6, 0.5, 4.5), 1, 0.3,
                        grow=(0.6, 2.2), wob=0.15, fade=(0.15, 0.35))
    w.particles(w.p["spark"], 150, ((-6, 12), (4.0, 20.0), (0.1, 1.2)), (0.4, -0.3, 3.2), (1, 2), 0.014,
                grow=(1.0, 0.5), wob=0.15, kw=5)
    ANIM.append(gate(before, 0.0, 0.5, clock))  # smoke and embers fade in over the first half second
    before = {o.name for o in bpy.data.objects}
    w.particles(soot, 10, ((mid.x - 2.0, mid.x + 2.0), (mid.y - 1.2, mid.y + 1.2), (0.8, 1.4)), (0.5, 0.5, 4.0), 1, 0.32,
                grow=(0.6, 2.2), wob=0.2, fade=(0.15, 0.35))
    ANIM.append(gate(before, FALL_T[1], FALL_T[1] + 1.2, clock))  # smoke over the fallen tower

    # ---- dark angry swarms over the city, around the giant bee, and furious bees at the hive
    specks(w, (-6.5, 25.0, 7.0), (3.2, 1.6, 1.3), 320, scale=0.065)
    specks(w, (10.0, 27.0, 8.5), (3.6, 1.6, 1.4), 320, scale=0.065)
    specks(w, (4.6, 21.0, 9.5), (2.6, 1.4, 1.0), 220, k=(2, 3), scale=0.06)
    specks(w, (-0.9, 16.5, 4.6), (2.6, 1.6, 1.4), 160, k=(2, 3), scale=0.05, buzz=0.12)
    b = w.p["bee"]
    for i in range(46):
        r = rnd.uniform(0.2, 0.7)
        c = (rnd.uniform(-0.2, 0.2), rnd.uniform(-0.45, 0.2), rnd.uniform(0.25, 0.6))
        w.orbit(b, c, (r, r * 0.7, r * 0.45), (rnd.choice((1, 2)), rnd.choice((2, 3)), rnd.choice((3, 4))), BEE_S * 0.9,
                buzz=0.02)

    # ---- a short camera shake when the top lands (gone long before the loop)
    cam_box = []

    def f_shake(ph):
        cam = bpy.context.scene.camera
        if not cam_box:
            cam_box.append(Vector(cam.location))
        d = clock(ph) - FALL_T[1]
        k = 0.03 * math.exp(-6 * d) if d > 0 else 0.0
        cam.location = cam_box[0] + Vector((k * math.sin(55 * d), 0, k * math.sin(71 * d + 1)))
    ANIM.append(f_shake)


# ============================================================================================ render + encode
def outputs(sid, lk, a):
    """[(clip id, [phase of each frame])]: a seamless loop of --seconds, or (lk.oneshot) a one-shot of lk.oneshot
    seconds followed by its seamless loop lk.loop_id of lk.loop seconds (phase = scene seconds / lk.loop)."""
    if lk.oneshot:
        n1, n2 = int(round(lk.oneshot * a.fps)), int(round(lk.loop * a.fps))
        return [(sid, [f / a.fps / lk.loop for f in range(n1)]),
                (lk.loop_id, [(lk.oneshot + f / a.fps) / lk.loop for f in range(n2)])]
    n = int(round(a.seconds * a.fps))
    return [(sid, [f / n for f in range(n)])]


def encode(frames, out, fps):
    for crf in (22, 24, 26, 29, 32):
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-framerate", str(fps), "-i",
                        os.path.join(frames, "%04d.png"), "-c:v", "libx264", "-preset", "slow", "-crf", str(crf),
                        "-pix_fmt", "yuv420p", "-profile:v", "main", "-g", str(fps), "-keyint_min", str(fps),
                        "-sc_threshold", "0", "-movflags", "+faststart", "-an", out], check=True)
        if os.path.getsize(out) <= MAX_BYTES * 0.97:
            break
    return crf


def render_scene(sid, a):
    fn, lk = SCENES[sid]
    w = World(lk, a)
    fn(w)
    for o in bpy.data.objects:  # any mesh made after finalize still needs the palette material
        if o.type == "MESH" and not o.data.materials:
            C.REGISTRY[:] = [o]
            C.finalize(w.tmp)
    setup_render(lk, a.samples)
    sc = bpy.context.scene
    stills = os.path.join(a.out, "stills")
    os.makedirs(stills, exist_ok=True)

    def shoot(ph, path):
        for an in ANIM:
            an(ph)
        sc.render.filepath = path
        bpy.ops.render.render(write_still=True)

    made = []
    named = {}  # still name -> (output index, frame) of the frame nearest its phase
    outs = outputs(sid, lk, a)
    for name, ph in lk.stills:
        best = min(((abs(p - ph), i, f) for i, (_, phs) in enumerate(outs) for f, p in enumerate(phs)))
        named[name] = best[1:]
    for i, (cid, phases) in enumerate(outs):
        frames = os.path.join(a.work, "frames", cid)
        shutil.rmtree(frames, ignore_errors=True)
        os.makedirs(frames)
        n = len(phases)
        todo = sorted({n // 2} | {f for oi, f in named.values() if oi == i}) if a.still else range(n)
        t0 = time.time()
        for f in todo:
            shoot(phases[f], os.path.join(frames, "%04d.png" % f))
        print("VBS_CLIPS %s: %d frame(s) in %.1fs, %d movers" % (cid, len(todo), time.time() - t0, len(ANIM)))
        mid = os.path.join(frames, "%04d.png" % (n // 2))
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", mid, "-q:v", "3", os.path.join(stills, cid + ".jpg")],
                       check=True)
        for name, (oi, f) in named.items():
            if oi == i:
                shutil.copyfile(os.path.join(frames, "%04d.png" % f), os.path.join(stills, name + ".png"))
        if a.still:
            continue
        out = os.path.join(a.out, cid + ".mp4")
        crf = encode(frames, out, a.fps)
        if not a.keep_frames:
            shutil.rmtree(frames, ignore_errors=True)
        print("VBS_CLIPS_DONE %s %d bytes crf=%d" % (cid, os.path.getsize(out), crf))
        made.append(out)
    return made or None


def contact_sheet(out, ids):
    stills = os.path.join(out, "stills")
    have = [i for i in ids if os.path.exists(os.path.join(stills, i + ".jpg"))]
    if not have:
        return
    cols = 4
    rows = (len(have) + cols - 1) // cols
    inputs, chains = [], []
    for k, i in enumerate(have):
        inputs += ["-i", os.path.join(stills, i + ".jpg")]
        chains.append("[%d:v]scale=416:240,drawbox=y=ih-22:h=22:w=iw:color=black@0.55:t=fill,"
                      "drawtext=text='%s':x=6:y=h-18:fontsize=14:fontcolor=white[v%d]" % (k, i, k))
    for k in range(len(have), rows * cols):  # pad the grid
        inputs += ["-f", "lavfi", "-i", "color=c=0x202020:s=416x240:d=1"]
        chains.append("[%d:v]null[v%d]" % (k, k))
    layout = "|".join("%d_%d" % (416 * (k % cols), 240 * (k // cols)) for k in range(rows * cols))
    fc = ";".join(chains) + ";" + "".join("[v%d]" % k for k in range(rows * cols)) + \
        "xstack=inputs=%d:layout=%s" % (rows * cols, layout)
    r = subprocess.run(["ffmpeg", "-loglevel", "error", "-y"] + inputs + ["-filter_complex", fc, "-frames:v", "1",
                                                                         "-q:v", "3",
                                                                         os.path.join(stills, "contact_sheet.jpg")])
    if r.returncode:
        print("VBS_CLIPS contact sheet failed (drawtext needs a font?)")


def args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--work", default="")
    p.add_argument("--only", default="")
    p.add_argument("--jobs", type=int, default=1)
    p.add_argument("--seconds", type=float, default=10.0)
    p.add_argument("--fps", type=int, default=30)
    p.add_argument("--samples", type=int, default=16)
    p.add_argument("--still", action="store_true")
    p.add_argument("--keep-frames", action="store_true")
    p.add_argument("--no-sheet", action="store_true")
    a, _ = p.parse_known_args(argv)
    a.out = os.path.abspath(a.out)
    a.work = os.path.abspath(a.work or os.path.join(a.out, "_work"))
    return a


def main():
    a = args()
    os.makedirs(a.out, exist_ok=True)
    ids = [i for i in a.only.split(",") if i] or list(SCENES)
    bad = [i for i in ids if i not in SCENES]
    if bad:
        sys.exit("unknown scene(s): %s (have %s)" % (bad, ", ".join(SCENES)))
    if a.jobs > 1 and len(ids) > 1:
        argv = sys.argv[sys.argv.index("--") + 1:]
        keep = []
        skip = False
        for x in argv:
            if skip:
                skip = False
                continue
            if x in ("--only", "--jobs"):
                skip = True
                continue
            keep.append(x)
        logs = os.path.join(a.work, "logs")
        os.makedirs(logs, exist_ok=True)

        def one(sid):
            cmd = [bpy.app.binary_path, "-b", "--factory-startup", "--python-exit-code", "1", "--python",
                   os.path.abspath(__file__), "--"] + \
                keep + ["--only", sid, "--jobs", "1", "--no-sheet"]
            with open(os.path.join(logs, sid + ".log"), "w") as fh:
                rc = subprocess.run(cmd, stdout=fh, stderr=subprocess.STDOUT).returncode
            print("VBS_CLIPS job %s rc=%d" % (sid, rc), flush=True)
            return rc
        with ThreadPoolExecutor(a.jobs) as ex:
            rcs = list(ex.map(one, ids))
        if not a.no_sheet:
            contact_sheet(a.out, ids)
        sys.exit(1 if any(rcs) else 0)
    for sid in ids:
        render_scene(sid, a)
    if not a.no_sheet:
        contact_sheet(a.out, ids)


if __name__ == "__main__":
    main()
