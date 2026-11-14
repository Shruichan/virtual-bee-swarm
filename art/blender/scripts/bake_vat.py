"""Bake a rigged insect's in-place clips into a vertex animation texture (VAT) for the instanced swarms.

The swarms draw 100-300 bees in one instanced draw call, and skinned meshes can't be instanced, so the worker bee's
Hover (and Walk) clips are baked here: object-space position (and normal) per vertex per frame. Unity's
BeeVAT importer (Assets/VirtualBeeSwarm/BeeVAT/Editor) turns the file into the mesh, the RGBAHalf textures and the
BeeVAT material (Shaders/BeeVAT.shader).

    blender -b "Virtual Bee Swarm Art.blend" --python scripts/bake_vat.py -- \
        --out ../../unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm/Art/BeeVAT/worker_bee.vat.bytes

Options: --rig/--mesh (default the worker bee), --clips Hover,Walk, --tris 560 (decimate target; 0 = keep all),
--preview out.png (a workbench still of the baked mesh, for a quick look).

Space: Unity's, like every other export here: meters scaled so the bee is 1 unit long on Z (the swarms scale by
beeLength), +Z = the bee's front (head), +Y up, pivot = centre of the rest pose's bounding box. Clips play in place.
The .blend is only read, never saved.

File (little endian): b"VAT1", uint32 n, n bytes of JSON header, then float32 pos[frames][src][3],
float32 nrm[frames][src][3], int32 src_of_vertex[verts], float32 rgba[verts][4] (linear), float32 uv[verts][2] (x = VAT column, y = wing weight),
int32 tris[tris][3] (Unity winding).
"""
import argparse
import json
import struct
import sys

import bpy
from mathutils import Vector


def args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--rig", default="Bee - Worker Rig")
    p.add_argument("--mesh", default="Bee - Worker Mesh")
    p.add_argument("--clips", default="Hover,Walk")
    p.add_argument("--tris", type=int, default=560)
    p.add_argument("--preview", default="")
    return p.parse_args(argv)


def clip_action(rig, clip):
    """The action of the NLA track named `clip` (Daniel keeps one track per clip), else an action ending in it."""
    ad = rig.animation_data
    for t in ad.nla_tracks:
        if t.name == clip and t.strips and t.strips[0].action:
            s = t.strips[0]
            return s.action, int(round(s.action_frame_start)), int(round(s.action_frame_end))
    for a in bpy.data.actions:
        if a.name.endswith(clip) and rig.name.split(" Rig")[0] in a.name:
            return a, int(a.frame_range[0]), int(a.frame_range[1])
    raise SystemExit("no clip %r on %s" % (clip, rig.name))


def use_action(rig, act):
    ad = rig.animation_data
    ad.use_nla = False
    ad.action = act
    if hasattr(ad, "action_slot") and getattr(act, "slots", None):
        ad.action_slot = act.slots[0]  # his clips all carry the slot "OBBeeswax Rig"; assign it explicitly


def decimate(mesh_obj, target_tris):
    """Collapse-decimate before the armature, wings protected (thin planes fall apart). Same topology every frame."""
    if target_tris <= 0:
        return
    m = mesh_obj.data
    m.calc_loop_triangles()
    tris = len(m.loop_triangles)
    if tris <= target_tris:
        return
    wing = [mesh_obj.vertex_groups[n].index for n in ("Wing.L", "Wing.R") if n in mesh_obj.vertex_groups]
    vg = mesh_obj.vertex_groups.new(name="_vat_decimate")
    keep = 0
    for v in m.vertices:
        w = max([g.weight for g in v.groups if g.group in wing] or [0.0])
        if w < 0.5:
            vg.add([v.index], 1.0, "REPLACE")
        else:
            keep += 1
    mod = mesh_obj.modifiers.new("_vat_decimate", "DECIMATE")
    mod.decimate_type = "COLLAPSE"
    mod.vertex_group = vg.name
    mod.vertex_group_factor = 1.0
    mod.use_collapse_triangulate = True
    mesh_obj.modifiers.move(len(mesh_obj.modifiers) - 1, 0)
    # the wing faces stay as they are; aim the rest at what's left of the budget
    lo, hi = 0.02, 1.0
    for _ in range(14):
        mod.ratio = (lo + hi) / 2
        n = count_tris(mesh_obj)
        if n > target_tris:
            hi = mod.ratio
        else:
            lo = mod.ratio
    mod.ratio = lo
    print("[bake_vat] decimate %d -> %d tris (ratio %.3f, %d wing verts kept)" % (tris, count_tris(mesh_obj), lo, keep))


def count_tris(obj):
    dg = bpy.context.evaluated_depsgraph_get()
    ev = obj.evaluated_get(dg)
    me = ev.to_mesh()
    me.calc_loop_triangles()
    n = len(me.loop_triangles)
    ev.to_mesh_clear()
    return n


def main():
    a = args()
    sc = bpy.context.scene
    rig = bpy.data.objects[a.rig]
    mobj = bpy.data.objects[a.mesh]
    clips = [c.strip() for c in a.clips.split(",") if c.strip()]
    fps = sc.render.fps / sc.render.fps_base

    # basis from the rest skeleton (rig space): forward = abdomen -> head, up = rig +Z, right = forward x up
    bones = rig.data.bones
    fwd = (bones["Head"].head_local - bones["Abdomen"].head_local)
    up = Vector((0, 0, 1))
    fwd = (fwd - up * fwd.dot(up)).normalized()
    right = fwd.cross(up).normalized()
    print("[bake_vat] rig-space forward", tuple(round(x, 3) for x in fwd), "right", tuple(round(x, 3) for x in right))

    decimate(mobj, a.tris)

    rig_inv = rig.matrix_world.inverted()

    def sample(frame):
        sc.frame_set(frame)
        dg = bpy.context.evaluated_depsgraph_get()
        ev = mobj.evaluated_get(dg)
        me = ev.to_mesh()
        mw = rig_inv @ ev.matrix_world
        nw = mw.to_3x3()
        P, N = [], []
        for v in me.vertices:
            p = mw @ v.co
            n = (nw @ v.normal).normalized()
            P.append((p.dot(right), p.dot(up), p.dot(fwd)))
            N.append((n.dot(right), n.dot(up), n.dot(fwd)))
        topo = None
        if frame == sample.first:
            me.calc_loop_triangles()
            wing = {mobj.vertex_groups[n].index for n in ("Wing.L", "Wing.R") if n in mobj.vertex_groups}
            ww = [max([g.weight for g in v.groups if g.group in wing] or [0.0]) for v in me.vertices]
            topo = ([(t.vertices[:], t.material_index) for t in me.loop_triangles], len(me.vertices), ww)
        ev.to_mesh_clear()
        return P, N, topo

    frames_pos, frames_nrm, clip_meta, topo = [], [], [], None
    for ci, clip in enumerate(clips):
        act, f0, f1 = clip_action(rig, clip)
        use_action(rig, act)
        sample.first = f0 if ci == 0 else -1
        rows = []
        for f in range(f0, f1 + 1):
            P, N, t = sample(f)
            if t:
                topo = t
            rows.append((P, N))
        # a seamless loop repeats its first pose on the last frame: drop it so the loop doesn't stall
        d = max((Vector(p) - Vector(q)).length for p, q in zip(rows[0][0], rows[-1][0]))
        span = max(max(abs(c) for c in p) for p in rows[0][0]) or 1.0
        if d < 1e-3 * span and len(rows) > 2:
            rows.pop()
        clip_meta.append({"name": clip, "action": act.name, "start": len(frames_pos), "frames": len(rows), "fps": fps,
                          "seam": d})
        for P, N in rows:
            frames_pos.append(P)
            frames_nrm.append(N)
        print("[bake_vat] %s: %s frames %d-%d -> %d rows (loop seam %.4f)" % (clip, act.name, f0, f1, len(rows), d))

    tris, nsrc, wingw = topo
    # wing beats per loop (the shader runs the wings on their own, faster clock): peaks of the most-moving wing vertex
    for c in clip_meta:
        rows = range(c["start"], c["start"] + c["frames"])
        wv = [s for s in range(nsrc) if wingw[s] > 0.5]
        c["wing_beats"] = 0
        if wv:
            s = max(wv, key=lambda k: max(frames_pos[r][k][1] for r in rows) - min(frames_pos[r][k][1] for r in rows))
            y = [frames_pos[r][s][1] for r in rows]
            if max(y) - min(y) > 1e-4:
                mid = (max(y) + min(y)) / 2
                c["wing_beats"] = sum(1 for i in range(len(y)) if y[i - 1] < mid <= y[i])
        print("[bake_vat] %s: %d wing beats per loop" % (c["name"], c["wing_beats"]))
    if any(len(P) != nsrc for P in frames_pos):
        raise SystemExit("vertex count changes between frames: the decimate/armature stack isn't stable")

    # normalize: rest = the first clip's first frame; 1 unit long on Z, pivot at its bounding-box centre
    rest = frames_pos[0]
    lo = [min(p[i] for p in rest) for i in range(3)]
    hi = [max(p[i] for p in rest) for i in range(3)]
    centre = [(lo[i] + hi[i]) / 2 for i in range(3)]
    length = hi[2] - lo[2]
    scale = 1.0 / length
    frames_pos = [[((p[0] - centre[0]) * scale, (p[1] - centre[1]) * scale, (p[2] - centre[2]) * scale) for p in P]
                  for P in frames_pos]
    blo = [min(p[i] for P in frames_pos for p in P) for i in range(3)]
    bhi = [max(p[i] for P in frames_pos for p in P) for i in range(3)]

    # output vertices: one per (source vertex, material), coloured by the material's base colour (linear)
    cols = []
    for slot in mobj.material_slots:
        c = (1.0, 1.0, 1.0, 1.0)
        mt = slot.material
        if mt is not None:
            c = tuple(mt.diffuse_color)
            if mt.node_tree:
                for n in mt.node_tree.nodes:
                    if n.type == "BSDF_PRINCIPLED":
                        c = tuple(n.inputs["Base Color"].default_value)
                        break
        cols.append((c[0], c[1], c[2], 1.0))
    if not cols:
        cols = [(1.0, 1.0, 1.0, 1.0)]
    vmap, src_of, rgba, out_tris = {}, [], [], []
    for (v3, mi) in tris:
        idx = []
        for s in v3:
            k = (s, mi)
            if k not in vmap:
                vmap[k] = len(src_of)
                src_of.append(s)
                rgba.append(cols[min(mi, len(cols) - 1)])
            idx.append(vmap[k])
        out_tris.append((idx[0], idx[2], idx[1]))  # right-handed -> Unity's left-handed: reverse the winding
    uv = [((s + 0.5) / nsrc, wingw[s]) for s in src_of]  # x = VAT column, y = wing weight (wings beat faster)

    header = {
        "version": 1, "rig": rig.name, "mesh": mobj.name, "blender": bpy.app.version_string,
        "source": bpy.path.basename(bpy.data.filepath), "src_verts": nsrc, "verts": len(src_of),
        "tris": len(out_tris), "wing_verts": sum(1 for w in wingw if w > 0.5), "rows": len(frames_pos), "clips": clip_meta, "length_blender": length,
        "bounds_min": blo, "bounds_max": bhi, "materials": [s.material.name if s.material else "" for s in mobj.material_slots],
    }
    hj = json.dumps(header).encode()
    with open(a.out, "wb") as f:
        f.write(b"VAT1")
        f.write(struct.pack("<I", len(hj)))
        f.write(hj)
        for P in frames_pos:
            f.write(struct.pack("<%df" % (3 * nsrc), *[c for p in P for c in p]))
        for N in frames_nrm:
            f.write(struct.pack("<%df" % (3 * nsrc), *[c for n in N for c in n]))
        f.write(struct.pack("<%di" % len(src_of), *src_of))
        f.write(struct.pack("<%df" % (4 * len(rgba)), *[c for col in rgba for c in col]))
        f.write(struct.pack("<%df" % (2 * len(uv)), *[c for t in uv for c in t]))
        f.write(struct.pack("<%di" % (3 * len(out_tris)), *[i for t in out_tris for i in t]))
    print("[bake_vat] wrote %s: %d source verts, %d verts, %d tris, %d rows, clips %s, bounds %s..%s" % (
        a.out, nsrc, len(src_of), len(out_tris), len(frames_pos),
        ", ".join("%s %d" % (c["name"], c["frames"]) for c in clip_meta),
        [round(x, 3) for x in blo], [round(x, 3) for x in bhi]))

    if a.preview:
        preview(frames_pos, out_tris, src_of, rgba, clip_meta, a.preview)


def preview(frames_pos, out_tris, src_of, rgba, clip_meta, path):
    """Workbench still: a row of baked frames (Hover then Walk) as plain meshes, vertex colours, Unity's axes."""
    scn = bpy.data.scenes.new("vat_preview")
    picks = []
    for c in clip_meta:
        for k in range(4):
            picks.append(c["start"] + k * c["frames"] // 4)
    for col, row in enumerate(picks):
        P = frames_pos[row]
        me = bpy.data.meshes.new("vat_%d" % row)
        # Unity (x, y, z) -> Blender (-x, -z, y): front (+Z) lands on Blender -Y, facing a camera on -Y
        verts = [(-P[s][0], -P[s][2], P[s][1]) for s in src_of]
        me.from_pydata(verts, [], [(t[0], t[2], t[1]) for t in out_tris])
        ca = me.color_attributes.new("Col", "FLOAT_COLOR", "POINT")
        for i, c in enumerate(rgba):
            ca.data[i].color = c
        ob = bpy.data.objects.new("vat_%d" % row, me)
        # three-quarter view: turn each bee 35 degrees
        ob.rotation_euler = (0, 0, 0.6)
        ob.location = ((col % 4) * 1.6 - 2.4, 0, -(col // 4) * 1.4)
        scn.collection.objects.link(ob)
    cam = bpy.data.cameras.new("cam")
    cam.type = "ORTHO"
    cam.ortho_scale = 7.0
    co = bpy.data.objects.new("cam", cam)
    co.location = (0, -10, -0.7 + 1.2)
    co.rotation_euler = (1.45, 0, 0)
    scn.collection.objects.link(co)
    scn.camera = co
    scn.render.engine = "BLENDER_WORKBENCH"
    scn.display.shading.color_type = "VERTEX"
    scn.display.shading.light = "STUDIO"
    scn.render.resolution_x, scn.render.resolution_y = 1200, 800
    scn.render.film_transparent = False
    scn.render.filepath = path
    bpy.context.window.scene = scn if bpy.context.window else None
    bpy.ops.render.render(write_still=True, scene=scn.name)
    print("[bake_vat] preview", path)


main()
