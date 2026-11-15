"""Printed AprilTag markers -> ScoutBox(via="marker"). The "works no matter what" trigger.

Each station card carries one AprilTag from OpenCV's built-in cv2.aruco dictionaries (no model download).
Family tag25h9: 7x7 cells (5x5 data + black border), so cells are 14% bigger than tag36h11's 8x8 at the same
printed size, and it had zero false detections of ids 0-5 on 1139 real glasses frames (tag16h5 reads further
but false-fires; tag36h11 is slower and shorter-ranged here). One fixed id per label, see TAG_IDS.
Detection runs on the full 1280x720 grayscale frame (small, far tags need every pixel), with OpenCV's ArUco
pipeline tuned for small, blurry tags and no corner refinement (we only need a box, not a pose).
bench_markers.py measures range vs printed size on synthetic composites over real glasses frames.
"""

from __future__ import annotations

import cv2
import numpy as np

from .types import ScoutBox

FAMILY = "DICT_APRILTAG_25h9"
# One fixed tag id per label. Ids 0..5 of the family; anything else seen in the frame is ignored.
TAG_IDS: dict[str, int] = {
    "beehive": 0,
    "native_flower": 1,
    "honey_jar": 2,
    "pebble_dish": 3,
    "bug_spray": 4,
    "bee_hotel": 5,
}


def make_params(fast: bool = True) -> cv2.aruco.DetectorParameters:
    p = cv2.aruco.DetectorParameters()
    # Small, far tags: allow perimeters down to ~1.6% of the long image side (~20 px for 1280 px),
    # i.e. a tag about 5 px a side. Decoding still needs ~2 px per cell, which is the real limit.
    p.minMarkerPerimeterRate = 0.016
    p.minCornerDistanceRate = 0.05
    p.minDistanceToBorder = 1
    # Adaptive threshold windows: 3, 9, 15, 21 px. The small ones catch the far tags, the larger ones
    # survive blur and uneven light. Fewer windows = faster.
    p.adaptiveThreshWinSizeMin = 3
    p.adaptiveThreshWinSizeMax = 21 if fast else 33
    p.adaptiveThreshWinSizeStep = 6 if fast else 4
    p.polygonalApproxAccuracyRate = 0.05  # blurry, compressed corners are not perfectly straight
    # Sample each cell on an 8x8 grid and ignore its outer 20%: blurry cell edges bleed into neighbours.
    p.perspectiveRemovePixelPerCell = 8
    p.perspectiveRemoveIgnoredMarginPerCell = 0.2
    p.cornerRefinementMethod = cv2.aruco.CORNER_REFINE_NONE
    # tag25h9 has min Hamming distance 9; correcting up to ~80% of its 4 correctable bits gains range for
    # 1 stray id-0..5 frame in 380 real ones (MarkerTracker's 2-frame confirm removes those).
    p.errorCorrectionRate = 0.8
    return p


class MarkerDetector:
    def __init__(self, family: str = FAMILY, tag_ids: dict[str, int] | None = None, fast: bool = True) -> None:
        self.family = family
        self.tag_ids = dict(tag_ids or TAG_IDS)
        self.by_id = {v: k for k, v in self.tag_ids.items()}
        self.dictionary = cv2.aruco.getPredefinedDictionary(getattr(cv2.aruco, family))
        self.detector = cv2.aruco.ArucoDetector(self.dictionary, make_params(fast))

    def raw(self, gray: np.ndarray) -> list[tuple[int, np.ndarray]]:
        """[(tag id, corners[4, 2] in pixels)] for every tag of the family in the frame."""
        corners, ids, _ = self.detector.detectMarkers(gray)
        if ids is None:
            return []
        return [(int(i), c.reshape(4, 2)) for i, c in zip(ids.ravel(), corners)]

    def detect(self, frame_bgr: np.ndarray) -> list[ScoutBox]:
        gray = frame_bgr if frame_bgr.ndim == 2 else cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2GRAY)
        h, w = gray.shape[:2]
        out: list[ScoutBox] = []
        for tid, c in self.raw(gray):
            label = self.by_id.get(tid)
            if label is None:
                continue
            x0, y0 = c.min(axis=0)
            x1, y1 = c.max(axis=0)
            out.append(ScoutBox(label=label, score=1.0, x=float(x0 / w), y=float(y0 / h), w=float((x1 - x0) / w),
                                h=float((y1 - y0) / h), via="marker", tag_id=tid))
        out.sort(key=lambda b: -b.w * b.h)
        return out


def tag_image(tag_id: int, cells_px: int = 40, family: str = FAMILY, quiet_cells: int = 1) -> np.ndarray:
    """The tag as a uint8 image (black border included) with a white quiet zone of `quiet_cells` cells."""
    d = cv2.aruco.getPredefinedDictionary(getattr(cv2.aruco, family))
    n = d.markerSize + 2  # data cells + 1-cell black border each side
    img = cv2.aruco.generateImageMarker(d, tag_id, n * cells_px)
    q = quiet_cells * cells_px
    return cv2.copyMakeBorder(img, q, q, q, q, cv2.BORDER_CONSTANT, value=255)


def tag_cells(family: str = FAMILY) -> int:
    """Cells across the tag's black square (data + border), e.g. 8 for tag36h11."""
    return cv2.aruco.getPredefinedDictionary(getattr(cv2.aruco, family)).markerSize + 2
