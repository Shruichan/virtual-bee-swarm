"""Render a simple orthographic preview of the three OBJ meadow models."""
from pathlib import Path
import math
from PIL import Image, ImageDraw, ImageFont

ROOT=Path(__file__).parent
W,H=1600,760
BG=(247,249,239,255)

def unit(v):
    n=math.sqrt(sum(x*x for x in v)); return tuple(x/n for x in v) if n else (0,0,1)
def dot(a,b): return sum(x*y for x,y in zip(a,b))

def load(name):
    colors={}; mat=None
    for line in (ROOT/f"{name}.mtl").read_text().splitlines():
        p=line.split()
        if len(p)==2 and p[0]=="newmtl": mat=p[1]
        elif len(p)==4 and p[0]=="Kd": colors[mat]=tuple(round(255*float(x)) for x in p[1:])
    verts=[]; faces=[]; mat=None
    for line in (ROOT/f"{name}.obj").read_text().splitlines():
        p=line.split()
        if not p: continue
        if p[0]=="v": verts.append(tuple(map(float,p[1:4])))
        elif p[0]=="usemtl": mat=p[1]
        elif p[0]=="f":
            ix=[int(x.split("/")[0])-1 for x in p[1:]]
            for i in range(1,len(ix)-1): faces.append((mat,(ix[0],ix[i],ix[i+1])))
    return verts,faces,colors

def draw_model(canvas,name,box):
    verts,faces,colors=load(name); cam=unit((1.2,1.8,6.0)); right=unit((cam[2],0,-cam[0])); up=unit((cam[1]*right[2]-cam[2]*right[1],cam[2]*right[0]-cam[0]*right[2],cam[0]*right[1]-cam[1]*right[0]))
    center=tuple((min(v[i] for v in verts)+max(v[i] for v in verts))/2 for i in range(3))
    pts=[]
    for v in verts:
        q=tuple(v[i]-center[i] for i in range(3)); pts.append((dot(q,right),dot(q,up),dot(q,cam)))
    span=max(max(p[0] for p in pts)-min(p[0] for p in pts),max(p[1] for p in pts)-min(p[1] for p in pts)); scale=min(box[2]/span,box[3]/span); ox=box[0]+box[2]/2; oy=box[1]+box[3]/2
    light=unit((-.35,.8,.65)); draw=ImageDraw.Draw(canvas); items=[]
    for mat,ix in faces:
        a,b,c=(verts[k] for k in ix); u=tuple(b[i]-a[i] for i in range(3)); v=tuple(c[i]-a[i] for i in range(3)); n=unit((u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0])); sh=.58+.42*abs(dot(n,light)); rgb=tuple(min(255,int(t*sh)) for t in colors.get(mat,(150,150,150)))+(255,)
        poly=[(ox+pts[k][0]*scale,oy-pts[k][1]*scale) for k in ix]; depth=sum(pts[k][2] for k in ix)/3; items.append((depth,poly,rgb))
    for _,poly,rgb in sorted(items,key=lambda item:item[0]): draw.polygon(poly,fill=rgb)

canvas=Image.new("RGBA",(W,H),BG); draw=ImageDraw.Draw(canvas); bold="/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"; reg="/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
draw.text((W/2,24),"BEE SCOUT  •  MEADOW ENVIRONMENT PACK",font=ImageFont.truetype(bold,40),fill=(54,73,43,255),anchor="mt")
labels=[("meadow_flowers","Flower clump"),("soft_grass_tuft","Soft grass"),("flowering_vine","Flowering vine")]
for i,(name,label) in enumerate(labels):
    x=35+i*520; draw.rounded_rectangle((x,95,x+490,710),radius=28,fill=(255,255,255,210),outline=(211,224,193,255),width=3)
    draw_model(canvas,name,(x+35,150,420,480))
    draw.text((x+245,650),label,font=ImageFont.truetype(bold,28),fill=(54,73,43,255),anchor="mt")
canvas.save(ROOT/"preview.png")
