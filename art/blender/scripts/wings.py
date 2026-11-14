"""Wing-flap tags for VirtualBeeSwarm/Unlit (Shaders/GameUnlit.shader flaps tagged vertices in the vertex shader).

Two extra UV layers, after the palette "UVMap" (Unity uv0):
  WingHinge (Unity uv1) = the hinge point (x, y) in UNITY mesh space; the hinge axis is the body axis (Unity Z)
  WingFlap  (Unity uv2) = (wing side in Unity x: +1 / -1, 0 = not a wing; blur side: +1 top face, -1 bottom, 0 none)
Usage in an item script: faces = mb.poly(...wing...); wings.tag(mb, faces, m) right after each wing primitive, and
wings.write_uvs() as the last line of build() (after any scale_all). Untagged meshes get no extra layers.
"""
from mathutils import Vector

import common as C

W, HX, HY, HZ, BLUR = "wing", "wing_hx", "wing_hy", "wing_hz", "wing_blur"


def _layers(bm):
    fl = bm.verts.layers.float
    return {k: (fl.get(k) or fl.new(k)) for k in (W, HX, HY, HZ, BLUR)}


def tag(mb, faces, m, hinge=None):
    """Tag the verts of `faces` (just made by mb.poly with matrix m, asset space) as one wing. Hinge = m's
    translation (the outline's local origin is the wing root) unless given; blur side = which side of the wing's
    mid-plane (local Z of m) the vertex is on."""
    L = _layers(mb.bm)
    h = Vector(hinge) if hinge is not None else m.to_translation()
    n = (m.to_3x3() @ Vector((0, 0, 1))).normalized()
    verts = {v for f in faces for v in f.verts}
    c = sum((v.co for v in verts), Vector()) / max(1, len(verts))
    side = -1.0 if c.x > h.x else 1.0  # Unity x = -Blender x
    for v in verts:
        v[L[W]] = side
        v[L[HX]], v[L[HY]], v[L[HZ]] = h.x, h.y, h.z
        d = (v.co - c).dot(n)
        v[L[BLUR]] = 1.0 if d > 1e-7 else (-1.0 if d < -1e-7 else 0.0)


def write_uvs(scale=1.0):
    """Write WingHinge/WingFlap on every tagged mesh in C.REGISTRY. `scale` = any uniform scale applied to the
    meshes after tagging (giant_hornet's scale_all). Hinges are stored relative to the object origin, converted to
    Unity axes the way the FBX lands: Unity (x, y, z) = Blender (-x, z, -y)."""
    tagged = 0
    for ob in C.REGISTRY:
        if ob.type != "MESH" or W not in ob.data.attributes:
            continue
        me = ob.data
        origin = C.world_loc(ob)
        nv = len(me.vertices)
        vals = {k: [0.0] * nv for k in (W, HX, HY, HZ, BLUR)}
        for k in vals:
            me.attributes[k].data.foreach_get("value", vals[k])
        if not me.uv_layers.get("UVMap"):
            me.uv_layers.new(name="UVMap")  # keep the palette layer first (Unity uv0); finalize() fills it
        hinge = me.uv_layers.new(name="WingHinge")
        flap = me.uv_layers.new(name="WingFlap")
        for loop in me.loops:
            i = loop.vertex_index
            if vals[W][i] != 0.0:
                h = Vector((vals[HX][i], vals[HY][i], vals[HZ][i])) * scale - origin
                hinge.data[loop.index].uv = (-h.x, h.z)
                flap.data[loop.index].uv = (vals[W][i], vals[BLUR][i])
                tagged += 1
            else:
                hinge.data[loop.index].uv = (0.0, 0.0)
                flap.data[loop.index].uv = (0.0, 0.0)
        for k in vals:
            me.attributes.remove(me.attributes[k])
    print("VBS_WINGS tagged %d wing loops" % tagged)
