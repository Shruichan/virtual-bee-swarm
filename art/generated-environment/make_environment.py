#!/usr/bin/env python3
"""Generate lightweight, stylized meadow props as portable Unity OBJ meshes."""
from __future__ import annotations

import math
from pathlib import Path

OUT = Path(__file__).parent
MATERIALS = {
    "leaf_green": (0.24, 0.52, 0.22),
    "leaf_light": (0.40, 0.69, 0.28),
    "stem": (0.20, 0.39, 0.16),
    "petal_white": (1.00, 0.94, 0.78),
    "petal_lilac": (0.62, 0.37, 0.82),
    "petal_pink": (0.94, 0.48, 0.66),
    "petal_sun": (1.00, 0.69, 0.12),
    "pollen": (1.00, 0.82, 0.26),
}


class Mesh:
    def __init__(self, name):
        self.name = name
        self.vertices = []
        self.faces = []

    def add(self, material, vertices, faces):
        start = len(self.vertices) + 1
        self.vertices.extend(vertices)
        self.faces.extend((material, tuple(start + i for i in face)) for face in faces)

    def ellipsoid(self, center, scale, material, rz=0.0, ry=0.0, segments=12, rings=8):
        cx, cy, cz = center; sx, sy, sz = scale
        czr, szr = math.cos(rz), math.sin(rz)
        cyr, syr = math.cos(ry), math.sin(ry)
        verts = []
        for j in range(rings + 1):
            lat = -math.pi/2 + math.pi*j/rings
            for i in range(segments):
                lon = 2*math.pi*i/segments
                x = sx*math.cos(lat)*math.cos(lon)
                y = sy*math.sin(lat)
                z = sz*math.cos(lat)*math.sin(lon)
                x, y = x*czr-y*szr, x*szr+y*czr
                x, z = x*cyr+z*syr, -x*syr+z*cyr
                verts.append((cx+x, cy+y, cz+z))
        faces = []
        for j in range(rings):
            for i in range(segments):
                a=j*segments+i; b=j*segments+(i+1)%segments
                c=(j+1)*segments+(i+1)%segments; d=(j+1)*segments+i
                faces.extend(((a,d,c),(a,c,b)))
        self.add(material, verts, faces)

    def cylinder(self, start, end, radius, material, sides=8):
        p0, p1 = start, end
        w = tuple(p1[i]-p0[i] for i in range(3)); length=math.sqrt(sum(x*x for x in w)); w=tuple(x/length for x in w)
        helper=(0,0,1) if abs(w[2])<.9 else (1,0,0)
        u=(w[1]*helper[2]-w[2]*helper[1],w[2]*helper[0]-w[0]*helper[2],w[0]*helper[1]-w[1]*helper[0]); ul=math.sqrt(sum(x*x for x in u)); u=tuple(x/ul for x in u)
        v=(w[1]*u[2]-w[2]*u[1],w[2]*u[0]-w[0]*u[2],w[0]*u[1]-w[1]*u[0])
        verts=[]
        for p in (p0,p1):
            for i in range(sides):
                a=2*math.pi*i/sides
                verts.append(tuple(p[k]+radius*(u[k]*math.cos(a)+v[k]*math.sin(a)) for k in range(3)))
        faces=[]
        for i in range(sides):
            n=(i+1)%sides; faces.append((i,n,sides+n,sides+i))
        faces.extend((tuple(reversed(range(sides))),tuple(range(sides,sides*2))))
        self.add(material, verts, faces)

    def flower(self, center, radius, petal_mat, petals=9):
        cx,cy,cz=center
        for i in range(petals):
            a=2*math.pi*i/petals
            px=cx+math.cos(a)*radius*.60; py=cy+math.sin(a)*radius*.60
            self.ellipsoid((px,py,cz), (radius*.55,radius*.23,radius*.14), petal_mat, rz=a, segments=10, rings=6)
        self.ellipsoid((cx,cy,cz+radius*.08),(radius*.30,radius*.30,radius*.20),"pollen",segments=12,rings=8)

    def write(self):
        lines=[f"# {self.name}","mtllib meadow.mtl",f"o {self.name}"]
        lines.extend(f"v {x:.5f} {y:.5f} {z:.5f}" for x,y,z in self.vertices)
        current=None
        for material,face in self.faces:
            if material!=current: lines.append(f"usemtl {material}"); current=material
            lines.append("f "+" ".join(map(str,face)))
        (OUT/f"{self.name}.obj").write_text("\n".join(lines)+"\n")


def flowers():
    m=Mesh("meadow_flowers")
    stems=[(-.15,.43,.02),(0,.57,0),(.15,.48,-.02)]
    for i,(x,h,z) in enumerate(stems):
        m.cylinder((x*.65,.015,z),(x,h-.04,z+.01),.009,"stem")
        for side,yy in ((-1,.16),(1,.25),(-1,.33)):
            m.ellipsoid((x+side*.055,yy,z+.005),(.070,.022,.014),"leaf_light" if i==1 else "leaf_green",ry=side*.45,rz=side*.2)
    m.flower(stems[0],.078,"petal_white",10)
    m.flower(stems[1],.064,"petal_lilac",8)
    m.flower(stems[2],.058,"petal_pink",7)
    m.write()


def grass():
    m=Mesh("soft_grass_tuft")
    # Individual tapered leaf forms, fanned around a small central crown.
    for i in range(13):
        a=2*math.pi*i/13
        height=.25+.12*(.5+.5*math.sin(i*2.41))
        lean=.07+.08*(.5+.5*math.cos(i*1.73))
        x=math.cos(a); z=math.sin(a)
        center=(x*lean*.45,height*.53,z*lean*.45)
        m.ellipsoid(center,(.023,height*.55,.014),"leaf_light" if i%4==0 else "leaf_green",rz=-x*.24,ry=a,segments=10,rings=8)
    m.ellipsoid((0,.055,0),(.085,.045,.075),"leaf_green",segments=12,rings=8)
    m.write()


def vine():
    m=Mesh("flowering_vine")
    pts=[]
    for i in range(15):
        t=i/14
        pts.append((.075*math.sin(t*2*math.pi),.78*t,.025*math.sin(t*math.pi*3)))
    for a,b in zip(pts,pts[1:]): m.cylinder(a,b,.010,"stem",7)
    for i in range(1,12):
        t=i/14; x,y,z=pts[i]
        side=-1 if i%2 else 1
        ang=side*(.48+.18*math.sin(i*1.7))
        m.ellipsoid((x+side*.055,y,z+.01),(.085,.026,.014),"leaf_light" if i%3==0 else "leaf_green",rz=side*.16,ry=ang)
    m.flower((pts[5][0]-.03,.31,pts[5][2]+.025),.045,"petal_lilac",7)
    m.flower((pts[9][0]+.035,.50,pts[9][2]+.025),.050,"petal_pink",8)
    m.flower((pts[13][0]-.02,.73,pts[13][2]+.025),.044,"petal_white",7)
    m.write()


def write_mtl():
    lines=["# Soft matte palette for a stylized AR meadow."]
    for name,color in MATERIALS.items():
        lines.extend((f"newmtl {name}",f"Kd {color[0]:.3f} {color[1]:.3f} {color[2]:.3f}","Ka 0.15 0.15 0.15","Ks 0.05 0.05 0.05","Ns 10",""))
    (OUT/"meadow.mtl").write_text("\n".join(lines))


if __name__=="__main__":
    flowers(); grass(); vine(); write_mtl()
