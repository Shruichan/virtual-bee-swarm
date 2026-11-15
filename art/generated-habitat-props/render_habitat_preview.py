"""Render an ortho preview of the two Bee Scout habitat teaching props."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
from render_preview import draw_model

ROOT=Path(__file__).parent
W,H=1200,760
canvas=Image.new("RGBA",(W,H),(247,249,239,255))
draw=ImageDraw.Draw(canvas)
bold=ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",38)
reg=ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",20)
draw.text((W/2,24),"BEE SCOUT  •  TINY HABITAT HELPERS",font=bold,fill=(54,73,43,255),anchor="mt")
labels=[
    ("pollinator_water_dish","A shallow sip stop","Fresh water + landing stones"),
    ("solitary_bee_nest_block","One kind of bee home","Most native bees nest in soil"),
]
for i,(name,title,note) in enumerate(labels):
    x=35+i*575
    draw.rounded_rectangle((x,98,x+535,710),radius=28,fill=(255,255,255,230),outline=(211,224,193,255),width=3)
    draw_model(canvas,name,(x+45,145,445,405))
    draw.text((x+267,578),title,font=ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",26),fill=(54,73,43,255),anchor="mt")
    draw.text((x+267,623),note,font=reg,fill=(86,102,73,255),anchor="mt")
canvas.save(ROOT/"habitat-preview.png")
