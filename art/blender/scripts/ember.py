"""Ember: tiny glowing particle meshes for the fire spilling out of the meadow window, plus a soft sprite texture.
  Ember      (1.2 cm faceted glowing ball, glow material)
  EmberFlake (elongated glowing shard, for variety)
  textures/ember.png (64 x 64 radial glow sprite with alpha, for a particle system instead of meshes)
Pivot = center of each."""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

ITEM = "ember"
BUDGET = 3000


def sprite(out_dir, size=64):
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float32)
    r = np.sqrt((xx - size / 2 + 0.5) ** 2 + (yy - size / 2 + 0.5) ** 2) / (size / 2)
    core, mid = np.array(C.hex_rgb("#FFF6D0")), np.array(C.hex_rgb("#FF6B2C"))
    t = np.clip(r / 0.6, 0, 1)[..., None]
    px = np.zeros((size, size, 4), dtype=np.float32)
    px[..., :3] = core * (1 - t) + mid * t
    px[..., 3] = np.clip(1 - r, 0, 1) ** 1.6
    C.make_image("ember", size, size, px, out_dir, alpha=True)


def build():
    e = C.MB("Ember", mat="glow", smooth_angle=60)
    e.ico(lambda c, n: "honey" if n.z > -0.2 else "fire", C.M((-0.015, 0, 0), scale=0.006), subdiv=2)
    e.to_object(origin=(-0.015, 0, 0))
    f = C.MB("EmberFlake", mat="glow", smooth_angle=10)
    f.ico("fire", C.M((0.015, 0, 0), (20, 30, 0), (0.0035, 0.0025, 0.009)), subdiv=1)
    f.to_object(origin=(0.015, 0, 0))
    sprite(C.parse_args().out)


if __name__ == "__main__":
    C.run(ITEM, build, BUDGET, pivot="center of each", hero_az=-20)
