"""Seed packet (optional prop): a paper packet of meadow seeds with a crimped top, a flower picture and SEEDS
printed on it. Stands upright facing the player. Pivot = base. One mesh."""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "seed_packet"
BUDGET = 3000

W, H, T = 0.075, 0.105, 0.006


def build():
    mb = C.MB("SeedPacket", smooth_angle=40)
    mb.box("cream", C.M((0, 0, H / 2), scale=(W, T, H)), bevel=(0.0015, 1))
    zig = [(-W / 2, 0.0), (W / 2, 0.0)]
    n = 10
    for i in range(n + 1):
        x = W / 2 - W * i / n
        zig.append((x, 0.012 if i % 2 == 0 else 0.008))
    mb.poly("white", zig, T * 0.55, C.M((0, 0, H - 0.001), (90, 0, 0)))
    front = C.M((0, -T / 2 - 0.0006, 0), (90, 0, 0))  # local XY = packet face (y = height), +Z toward the viewer
    mb.flat("sky", C.rounded_rect(W - 0.01, 0.034, 0.004, 2), front @ C.M((0, 0.058, 0)))
    mb.flat("leaf", C.rounded_rect(W - 0.01, 0.032, 0.004, 2), front @ C.M((0, 0.026, 0)))
    # hills
    mb.flat("leaf_dark", [(-(W - 0.01) / 2 + 0.002, 0.041), ((W - 0.01) / 2 - 0.002, 0.041)] +
            [((W - 0.01) / 2 - 0.002 - (W - 0.014) * i / 12, 0.041 + 0.006 * math.sin(math.pi * i / 12))
             for i in range(1, 12)], front @ C.M((0, 0, 0.0002)))
    # the flower picture
    fx, fy = 0.0, 0.052
    mb.flat("leaf_dark", C.rounded_rect(0.0025, 0.03, 0.001, 1), front @ C.M((fx, fy - 0.017, 0.0004)))
    for i in range(6):
        a = 2 * math.pi * i / 6
        mb.flat("purple", C.circle_pts(1, 10, rx=0.0075, ry=0.0045),
                front @ C.M((fx + 0.009 * math.cos(a), fy + 0.009 * math.sin(a), 0.0006), (0, 0, math.degrees(a))))
    mb.flat("honey", C.circle_pts(0.0055, 10), front @ C.M((fx, fy, 0.0008)))
    mb.text("charcoal", "SEEDS", 0.017, 0.0, front @ C.M((0, 0.0915, 0.0004)), resolution=2)
    mb.text("leaf_dark", "MEADOW MIX", 0.0068, 0.0, front @ C.M((0, 0.0795, 0.0004)), resolution=2)
    mb.to_object(origin=(0, 0, 0))


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="base (bottom center)", hero_az=-20)
