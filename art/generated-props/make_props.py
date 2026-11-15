#!/usr/bin/env python3
"""Generate the small Bee Scout bonus meshes as portable OBJ + MTL files."""
from __future__ import annotations

import math
from pathlib import Path

OUT = Path(__file__).parent

MATERIALS = {
    "honey": (0.97, 0.62, 0.12),
    "honey_light": (1.00, 0.81, 0.31),
    "charcoal": (0.16, 0.12, 0.13),
    "wing": (0.70, 0.88, 0.92),
    "white": (1.00, 0.97, 0.87),
    "wood": (0.39, 0.19, 0.10),
    "wood_light": (0.65, 0.34, 0.16),
    "bark": (0.22, 0.12, 0.09),
    "flame_red": (0.93, 0.19, 0.07),
    "flame_orange": (1.00, 0.43, 0.07),
    "flame_gold": (1.00, 0.77, 0.19),
    "stone": (0.37, 0.43, 0.44),
    "stone_light": (0.60, 0.64, 0.60),
}


class Obj:
    def __init__(self, title: str):
        self.title = title
        self.vertices: list[tuple[float, float, float]] = []
        self.faces: list[tuple[str, tuple[int, ...]]] = []

    def mesh(self, name: str, material: str, vertices, faces):
        start = len(self.vertices) + 1
        self.vertices.extend(vertices)
        for face in faces:
            self.faces.append((material, tuple(start + i for i in face)))

    def ellipsoid(self, name, center, scale, material, segments=20, rings=12):
        cx, cy, cz = center
        sx, sy, sz = scale
        verts = []
        for j in range(rings + 1):
            lat = -math.pi / 2 + math.pi * j / rings
            for i in range(segments):
                lon = 2 * math.pi * i / segments
                verts.append((cx + sx * math.cos(lat) * math.cos(lon),
                              cy + sy * math.sin(lat),
                              cz + sz * math.cos(lat) * math.sin(lon)))
        faces = []
        for j in range(rings):
            for i in range(segments):
                a = j * segments + i
                b = j * segments + (i + 1) % segments
                c = (j + 1) * segments + (i + 1) % segments
                d = (j + 1) * segments + i
                faces.extend(((a, d, c), (a, c, b)))
        self.mesh(name, material, verts, faces)

    def striped_ellipsoid(self, center, radii, materials, segments=24, bands=8):
        cx, cy, cz = center
        rx, ry, rz = radii
        t0, t1 = -0.88, 0.88
        verts = []
        for row in range(bands + 1):
            t = t0 + (t1 - t0) * row / bands
            r = math.sqrt(max(0.0, 1 - t * t)) * 1.006
            for i in range(segments):
                a = 2 * math.pi * i / segments
                verts.append((cx + rx * r * math.cos(a), cy + ry * r * math.sin(a), cz + rz * t))
        band_faces: list[list[tuple[int, ...]]] = [[] for _ in materials]
        for row in range(bands):
            m = min(len(materials) - 1, int(row * len(materials) / bands))
            for i in range(segments):
                a = row * segments + i
                b = row * segments + (i + 1) % segments
                c = (row + 1) * segments + (i + 1) % segments
                d = (row + 1) * segments + i
                band_faces[m].extend(((a, d, c), (a, c, b)))
        start = len(self.vertices) + 1
        self.vertices.extend(verts)
        for material, faces in zip(materials, band_faces):
            self.faces.extend((material, tuple(start + i for i in face)) for face in faces)

    def cylinder(self, start, end, radius, material, sides=12):
        p0, p1 = start, end
        axis = tuple(p1[i] - p0[i] for i in range(3))
        length = math.sqrt(sum(x*x for x in axis))
        w = tuple(x / length for x in axis)
        helper = (0, 1, 0) if abs(w[1]) < 0.9 else (1, 0, 0)
        u = (w[1]*helper[2]-w[2]*helper[1], w[2]*helper[0]-w[0]*helper[2], w[0]*helper[1]-w[1]*helper[0])
        ul = math.sqrt(sum(x*x for x in u)); u = tuple(x/ul for x in u)
        v = (w[1]*u[2]-w[2]*u[1], w[2]*u[0]-w[0]*u[2], w[0]*u[1]-w[1]*u[0])
        verts = []
        for p in (p0, p1):
            for i in range(sides):
                a = 2 * math.pi * i / sides
                verts.append(tuple(p[k] + radius * (u[k]*math.cos(a) + v[k]*math.sin(a)) for k in range(3)))
        faces = []
        for i in range(sides):
            n = (i + 1) % sides
            faces.append((i, n, sides + n, sides + i))
        faces.extend((tuple(reversed(range(sides))), tuple(range(sides, sides*2))))
        self.mesh("cylinder", material, verts, faces)

    def flame(self, center, height, radius, material, lobes=5, rings=12, sides=20, tilt=0.0):
        cx, cy, cz = center
        verts = []
        for j in range(rings + 1):
            t = 0.015 + 0.985 * j / rings
            swell = math.sin(math.pi * t) ** 0.66
            base = radius * (0.14 + 0.86 * swell) * (1 - 0.30 * t)
            shift = tilt * t * t
            for i in range(sides):
                a = 2 * math.pi * i / sides
                lobed = 1 + 0.12 * math.cos(lobes * a + t * 2.0)
                verts.append((cx + shift + base * lobed * math.cos(a), cy + height*t, cz + base * 0.72 * lobed * math.sin(a)))
        faces = []
        for j in range(rings):
            for i in range(sides):
                a = j*sides+i; b = j*sides+(i+1)%sides
                c = (j+1)*sides+(i+1)%sides; d = (j+1)*sides+i
                faces.extend(((a,b,c),(a,c,d)))
        self.mesh("flame", material, verts, faces)

    def write(self, filename: str):
        lines = [f"# {self.title}", f"mtllib {filename}.mtl", "o " + filename]
        lines.extend(f"v {x:.5f} {y:.5f} {z:.5f}" for x, y, z in self.vertices)
        current = None
        for material, face in self.faces:
            if material != current:
                lines.append(f"usemtl {material}")
                current = material
            lines.append("f " + " ".join(map(str, face)))
        (OUT / f"{filename}.obj").write_text("\n".join(lines) + "\n")


def bee():
    o = Obj("Pocket Bee — a friendly pollinator mascot")
    o.striped_ellipsoid((0, 0, -0.10), (0.065, 0.060, 0.105), ["charcoal", "honey", "charcoal", "honey_light", "charcoal"], bands=12)
    o.ellipsoid("thorax", (0, 0, 0.015), (0.075, 0.068, 0.075), "honey")
    o.ellipsoid("head", (0, 0.007, 0.145), (0.075, 0.072, 0.073), "honey_light")
    # Stubby, blue-tinted wings read clearly at thumbnail size.
    o.ellipsoid("left wing", (-0.072, 0.068, 0.005), (0.078, 0.014, 0.050), "wing")
    o.ellipsoid("right wing", (0.072, 0.068, 0.005), (0.078, 0.014, 0.050), "wing")
    for side in (-1, 1):
        o.cylinder((side*0.025, 0.060, 0.185), (side*0.045, 0.095, 0.208), 0.006, "charcoal", 8)
        for z in (-0.11, -0.015, 0.065):
            o.cylinder((side*0.035, -0.037, z), (side*0.060, -0.070, z-0.012), 0.0055, "charcoal", 8)
        o.ellipsoid("eye", (side*0.030, 0.018, 0.228), (0.014, 0.018, 0.009), "charcoal", 12, 8)
        o.ellipsoid("eye sparkle", (side*0.030-0.004, 0.025, 0.237), (0.004, 0.005, 0.002), "white", 8, 6)
    # Smile as three tiny connected cylinders facing forward (+Z).
    smile = [(-0.020, -0.020, 0.227), (0, -0.028, 0.231), (0.020, -0.020, 0.227)]
    for a, b in zip(smile, smile[1:]):
        o.cylinder(a, b, 0.003, "charcoal", 6)
    o.write("pocket_bee")


def campfire():
    o = Obj("Ember Buddy — a friendly campfire prop")
    # Four short, crossed logs, kept low and chunky for readability.
    o.cylinder((-0.22, 0.055, -0.10), (0.22, 0.055, 0.10), 0.052, "wood", 12)
    o.cylinder((-0.22, 0.055, 0.10), (0.22, 0.055, -0.10), 0.052, "wood_light", 12)
    o.cylinder((-0.16, 0.105, 0), (0.16, 0.105, 0), 0.047, "bark", 12)
    o.cylinder((0, 0.105, -0.16), (0, 0.105, 0.16), 0.047, "wood_light", 12)
    for i in range(9):
        a = 2*math.pi*i/9
        mat = "stone_light" if i % 3 == 0 else "stone"
        o.ellipsoid("hearth pebble", (0.245*math.cos(a), 0.030, 0.245*math.sin(a)), (0.055, 0.036, 0.048), mat, 12, 8)
    o.flame((0.0, 0.12, 0.0), 0.40, 0.13, "flame_red", tilt=-0.035)
    o.flame((0.0, 0.13, 0.120), 0.27, 0.080, "flame_gold", tilt=0.020)
    # Friendly face on the bright inner flame, looking toward +Z.
    for x in (-0.030, 0.030):
        o.ellipsoid("ember eye", (x, 0.235, 0.178), (0.010, 0.014, 0.008), "charcoal", 10, 6)
    smile = [(-0.018, 0.202, 0.179), (0, 0.196, 0.182), (0.018, 0.202, 0.179)]
    for a, b in zip(smile, smile[1:]):
        o.cylinder(a, b, 0.0028, "charcoal", 6)
    # Tiny floating sparks.
    for x, y, z in ((-0.16, 0.42, 0.01), (0.16, 0.50, -0.03), (0.04, 0.57, 0.00)):
        o.ellipsoid("spark", (x, y, z), (0.012, 0.020, 0.012), "flame_gold", 8, 6)
    o.write("ember_buddy")


def write_mtl():
    lines = ["# Matte, deliberately simple colors for Unity's OBJ importer."]
    for name, color in MATERIALS.items():
        lines.extend((f"newmtl {name}", f"Kd {color[0]:.3f} {color[1]:.3f} {color[2]:.3f}", "Ka 0.15 0.15 0.15", "Ks 0.08 0.08 0.08", "Ns 12", ""))
    (OUT / "pocket_bee.mtl").write_text("\n".join(lines))
    (OUT / "ember_buddy.mtl").write_text("\n".join(lines))


if __name__ == "__main__":
    bee()
    campfire()
    write_mtl()