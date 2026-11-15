#!/usr/bin/env python3
"""Generate low-poly habitat props for the Bee Scout AR field guide."""
from __future__ import annotations

import math
from pathlib import Path

OUT = Path(__file__).parent
PALETTE = {
    "ceramic": (0.25, 0.58, 0.66), "water": (0.36, 0.79, 0.86),
    "stone": (0.74, 0.68, 0.53), "stone_light": (0.89, 0.82, 0.66),
    "wood": (0.49, 0.29, 0.15), "wood_light": (0.70, 0.48, 0.27),
    "roof": (0.32, 0.47, 0.30), "cavity": (0.12, 0.075, 0.045),
}


class Mesh:
    def __init__(self, name: str):
        self.name = name
        self.vertices: list[tuple[float, float, float]] = []
        self.faces: list[tuple[str, tuple[int, ...]]] = []

    def add(self, material: str, verts, faces):
        base = len(self.vertices) + 1
        self.vertices.extend(verts)
        self.faces.extend((material, tuple(base + i for i in face)) for face in faces)

    def ellipsoid(self, center, scale, material, segments=8, rings=4):
        cx, cy, cz = center; sx, sy, sz = scale
        verts=[]
        for j in range(rings + 1):
            lat=-math.pi/2 + math.pi*j/rings
            for i in range(segments):
                lon=2*math.pi*i/segments
                verts.append((cx+sx*math.cos(lat)*math.cos(lon), cy+sy*math.sin(lat), cz+sz*math.cos(lat)*math.sin(lon)))
        faces=[]
        for j in range(rings):
            for i in range(segments):
                a=j*segments+i; b=j*segments+(i+1)%segments
                c=(j+1)*segments+(i+1)%segments; d=(j+1)*segments+i
                faces.extend(((a,d,c),(a,c,b)))
        self.add(material, verts, faces)

    def box(self, lo, hi, material):
        x0,y0,z0=lo; x1,y1,z1=hi
        v=[(x0,y0,z0),(x1,y0,z0),(x1,y1,z0),(x0,y1,z0),
           (x0,y0,z1),(x1,y0,z1),(x1,y1,z1),(x0,y1,z1)]
        f=[(0,2,1),(0,3,2),(4,5,6),(4,6,7),(0,1,5),(0,5,4),
           (1,2,6),(1,6,5),(2,3,7),(2,7,6),(3,0,4),(3,4,7)]
        self.add(material,v,f)

    def front_disc(self, x, y, z, radius, material, sides=12):
        # Flat circle facing +Z, used for small, readable nest entrances.
        verts=[(x,y,z)]+[(x+radius*math.cos(2*math.pi*i/sides),y+radius*math.sin(2*math.pi*i/sides),z) for i in range(sides)]
        self.add(material,verts,[(0,1+i,1+(i+1)%sides) for i in range(sides)])

    def dish(self):
        # A shallow lathed bowl: outer wall, rounded rim, inner wall, and floor.
        profile=[(.052,.012),(.080,.020),(.096,.047),(.094,.058),(.083,.056),(.074,.036),(.052,.025),(.008,.024)]
        n=12; verts=[]
        for radius,y in profile:
            for i in range(n):
                a=2*math.pi*i/n; verts.append((radius*math.cos(a),y,radius*math.sin(a)))
        faces=[]
        for j in range(len(profile)-1):
            for i in range(n):
                a=j*n+i; b=j*n+(i+1)%n; c=(j+1)*n+(i+1)%n; d=(j+1)*n+i
                faces.extend(((a,b,c),(a,c,d)))
        self.add("ceramic",verts,faces)
        # Water surface and five broad stones that rise above it as landing spots.
        self.ellipsoid((0,.030,0),(.069,.002,.069),"water",segments=12,rings=2)
        for i in range(5):
            a=2*math.pi*i/5
            self.ellipsoid((.052*math.cos(a),.045,.052*math.sin(a)),(.020,.013,.017),"stone_light" if i%2==0 else "stone",segments=7,rings=3)

    def write(self):
        lines=[f"# {self.name}",f"mtllib {self.name}.mtl",f"o {self.name}"]
        lines.extend(f"v {x:.5f} {y:.5f} {z:.5f}" for x,y,z in self.vertices)
        current=None
        for material,face in self.faces:
            if material!=current: lines.append(f"usemtl {material}"); current=material
            lines.append("f "+" ".join(map(str,face)))
        (OUT/f"{self.name}.obj").write_text("\n".join(lines)+"\n")


def water_station():
    m=Mesh("pollinator_water_dish")
    m.dish()
    # Low, stable feet keep the dish visually readable in an AR tabletop scene.
    for i in range(3):
        a=2*math.pi*i/3
        m.ellipsoid((.057*math.cos(a),.006,.057*math.sin(a)),(.013,.006,.013),"ceramic",segments=7,rings=3)
    m.write()


def nesting_block():
    m=Mesh("solitary_bee_nest_block")
    # Compact timber block with pitched cap. The dark circles are illustrative entrances.
    m.box((-.075,.018,-.045),(.075,.178,.045),"wood")
    m.box((-.083,.178,-.052),(.083,.190,.052),"wood_light")
    m.add("roof",[(-.093,.190,-.060),(0,.244,-.060),(.093,.190,-.060),
                   (-.093,.190,.060),(0,.244,.060),(.093,.190,.060)],
          [(0,1,4,3),(1,2,5,4),(0,3,5,2),(0,2,1),(3,4,5)])
    # A small grid of shallow cavity openings on the front (+Z) face.
    for row,y in enumerate((.060,.099,.138)):
        for col,x in enumerate((-.045,0,.045)):
            r=.010 if (row+col)%2==0 else .008
            m.front_disc(x,y,.046,r*1.4,"wood_light")
            m.front_disc(x,y,.048,r,"cavity")
    # Short feet lift the block off the ground and read clearly in mixed reality.
    for x in (-.052,.052):
        m.box((x-.009,0,-.036),(x+.009,.018,.036),"wood_light")
    m.write()


def write_mtl():
    lines=["# Matte, readable colors for small AR habitat props."]
    for name,c in PALETTE.items():
        lines.extend((f"newmtl {name}",f"Kd {c[0]:.3f} {c[1]:.3f} {c[2]:.3f}","Ka 0.12 0.12 0.12","Ks 0.04 0.04 0.04","Ns 8",""))
    for name in ("pollinator_water_dish","solitary_bee_nest_block"):
        (OUT/f"{name}.mtl").write_text("\n".join(lines))


if __name__ == "__main__":
    water_station(); nesting_block(); write_mtl()
