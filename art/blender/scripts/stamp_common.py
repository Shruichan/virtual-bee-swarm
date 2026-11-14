"""Shared builder for the two rubber-stamp marks (stamp_approved.py, stamp_grievance.py): an inked double-border
box with one bold word, a thin slab that stands upright facing the player. Pivot = center. Not an item itself."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

W, H, T = 0.11, 0.042, 0.0025


def build_stamp(name, word, color, size):
    mb = C.MB(name, smooth_angle=20)
    face = C.M((0, 0, 0), (90, 0, 0))  # local XY = stamp face (y up), local +Z = toward the player
    mb.ring(color, C.rounded_rect(W, H, 0.007, 3), C.rounded_rect(W - 0.0064, H - 0.0064, 0.005, 3), T, face)
    mb.ring(color, C.rounded_rect(W - 0.0108, H - 0.0108, 0.0036, 3), C.rounded_rect(W - 0.0136, H - 0.0136, 0.0024, 3),
            T, face)
    mb.text(color, word, size, T, face @ C.M((0, -0.0012, 0)), resolution=3, spacing=1.04)
    mb.to_object(origin=(0, 0, 0))
