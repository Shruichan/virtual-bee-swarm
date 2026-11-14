"""Score card: flown in by a crew of 6 at the escape (V16). A dark union certificate with a honey border, hexagon
badges and an OFFICIAL RESULT tab. The result itself ("HIVE SAVED IN 2:09 / GRIEVANCES FILED: 2") stays a Unity
TextMesh: parent it to TextAnchor. Pivot = card center, faces the player.
Objects: ScoreCard (empty) > Card (one mesh), TextAnchor (empty just in front of the card face)."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "score_card"
BUDGET = 3000

W, H, T = 0.46, 0.14, 0.008


def build():
    root = C.empty("ScoreCard", size=0.1)
    mb = C.MB("Card", smooth_angle=30)
    face = C.M((0, 0, 0), (90, 0, 0))  # local XY = card face (y up), local +Z = toward the player
    fz = T / 2
    mb.poly("charcoal", C.rounded_rect(W, H, 0.016, 3), T, face, bevel=(0.0015, 1))
    mb.ring("honey", C.rounded_rect(W - 0.01, H - 0.01, 0.012, 3), C.rounded_rect(W - 0.02, H - 0.02, 0.008, 3),
            0.002, face @ C.M((0, 0, fz + 0.0006)))
    # tab on top
    mb.poly("amber", C.rounded_rect(0.17, 0.03, 0.008, 2), T * 0.8, face @ C.M((0, H / 2 + 0.006, 0)),
            bevel=(0.001, 1))
    mb.text("charcoal", "OFFICIAL RESULT", 0.0125, 0.0, face @ C.M((0, H / 2 + 0.0085, T * 0.4 + 0.0004)),
            resolution=2)
    # hexagon badges at both ends
    for s in (-1, 1):
        x = s * (W / 2 - 0.036)
        mb.hexprism("amber", 0.021, 0.004, face @ C.M((x, 0, fz + 0.002)), bevel=(0.0012, 1))
        mb.hexprism("honey_dark", 0.012, 0.002, face @ C.M((x, 0, fz + 0.0045)))
    # small print under the result
    mb.text("stone", "LOCAL 20,000  *  BEE WORKERS UNION", 0.0072, 0.0, face @ C.M((0, -H / 2 + 0.018, fz + 0.0004)),
            resolution=2)
    mb.to_object(origin=(0, 0, 0), parent=root)
    C.empty("TextAnchor", origin=(0, -(fz + 0.003), 0.006), parent=root, size=0.03)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="card center", hero_az=-15)
