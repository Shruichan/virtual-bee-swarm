"""Small orthographic preview renderer for the low-poly OBJ props (Pillow only)."""
from pathlib import Path
import math
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).parent
W, H = 1400, 760
BG = (250, 247, 239, 255)


def unit(v):
    n = math.sqrt(sum(x*x for x in v))
    if n < 1e-12:
        return (0.0, 0.0, 1.0)
    return tuple(x/n for x in v)


def dot(a, b):
    return sum(x*y for x, y in zip(a, b))


def load(name):
    colors = {}
    for line in (ROOT / f"{name}.mtl").read_text().splitlines():
        p = line.split()
        if len(p) == 4 and p[0] == "Kd":
            colors[material] = tuple(round(255*float(x)) for x in p[1:])
        elif len(p) == 2 and p[0] == "newmtl":
            material = p[1]
    verts, faces, current = [], [], None
    for line in (ROOT / f"{name}.obj").read_text().splitlines():
        p = line.split()
        if not p: continue
        if p[0] == "v": verts.append(tuple(map(float, p[1:4])))
        elif p[0] == "usemtl": current = p[1]
        elif p[0] == "f":
            ids = [int(x.split("/")[0])-1 for x in p[1:]]
            for i in range(1, len(ids)-1): faces.append((current, (ids[0], ids[i], ids[i+1])))
    return verts, faces, colors


def render(name, box):
    verts, faces, colors = load(name)
    cam = unit((1.0, 1.4, 6.0))
    right = unit((cam[2], 0, -cam[0]))
    up = unit((cam[1]*right[2]-cam[2]*right[1], cam[2]*right[0]-cam[0]*right[2], cam[0]*right[1]-cam[1]*right[0]))
    center = tuple((min(v[i] for v in verts)+max(v[i] for v in verts))/2 for i in range(3))
    projected = []
    for v in verts:
        p = tuple(v[i]-center[i] for i in range(3))
        projected.append((dot(p, right), dot(p, up), dot(p, cam)))
    extent = max(max(p[0] for p in projected)-min(p[0] for p in projected), max(p[1] for p in projected)-min(p[1] for p in projected))
    scale = min(box[2]/extent, box[3]/extent)
    ox, oy = box[0]+box[2]/2, box[1]+box[3]/2
    light = unit((-.4, .8, .7))
    draw_items = []
    for mat, ids in faces:
        xyz = [verts[i] for i in ids]
        a, b, c = xyz
        u = tuple(b[i]-a[i] for i in range(3)); v = tuple(c[i]-a[i] for i in range(3))
        normal = unit((u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0]))
        shade = .55 + .45*abs(dot(normal, light))
        color = tuple(min(255, int(ch*shade)) for ch in colors.get(mat, (180, 180, 180))) + (255,)
        poly = [(ox+p[0]*scale, oy-p[1]*scale) for p in (projected[i] for i in ids)]
        depth = sum(projected[i][2] for i in ids)/3
        draw_items.append((depth, poly, color))
    # Higher camera-space z is closer; draw far surfaces first.
    draw_items.sort(key=lambda x: x[0])
    draw = ImageDraw.Draw(canvas)
    for _, poly, color in draw_items:
        draw.polygon(poly, fill=color)


canvas = Image.new("RGBA", (W, H), BG)
font_path = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
font = ImageFont.truetype(font_path, 42)
ImageDraw.Draw(canvas).text((W//2, 30), "BEE SCOUT  •  BONUS PROPS", fill=(68, 56, 44, 255), font=font, anchor="mt")
render("pocket_bee", (70, 110, 570, 560))
render("ember_buddy", (760, 110, 570, 560))
canvas.save(ROOT / "preview.png")
