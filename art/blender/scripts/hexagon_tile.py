"""Hexagon tile: one of the 4 progress hexes on the hive front (fits HexSlot1..4 in hive.glb). Pointy-top, 4.4 cm
across, faces the player (Unity +Z). Pivot = center of the back face. Two states side by side:
  HexTile      (amber rim, cream face: not done yet)   > Face
  HexTile_Lit  (amber rim, glowing honey face: done)   > Face_Lit
Swap the Face material (or just use HexTile_Lit) when a station is solved."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "hexagon_tile"
BUDGET = 3000

R, DEPTH, FACE_R = 0.022, 0.008, 0.0165


def tile(name, x, face_color, face_mat):
    t = C.MB(name)
    t.hexprism("amber", R, DEPTH, C.M((x, -DEPTH / 2, 0), (90, 0, 0)), bevel=(0.0018, 1))
    ob = t.to_object(origin=(x, 0, 0))
    f = C.MB("Face_Lit" if name.endswith("Lit") else "Face", mat=face_mat)
    f.hexprism(face_color, FACE_R, 0.003, C.M((x, -DEPTH - 0.0006, 0), (90, 0, 0)), bevel=(0.0008, 1))
    f.hexprism("honey_dark" if face_mat == "pal" else "white", FACE_R * 0.42, 0.001,
               C.M((x, -DEPTH - 0.0022, 0), (90, 0, 0)))  # a small inner hex
    f.to_object(origin=(x, -DEPTH - 0.0006, 0), parent=ob)
    return ob


def build():
    tile("HexTile", -0.03, "cream", "pal")
    tile("HexTile_Lit", 0.03, "honey", "glow")


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="back face center", hero_az=-20)
