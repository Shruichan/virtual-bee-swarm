"""Pretrained COCO object detector (ultralytics YOLO) -> ScoutBox dicts for the dashboard.

    from detect import yolo
    boxes = yolo.detect(frame_bgr)              # mapped booth classes only, via "detector"
    boxes = yolo.detect(frame_rgb, bgr=False)   # the worker's Frame.rgb
    raw = yolo.raw_detect(frame_bgr)            # every COCO box, label = COCO name (demo mode)

Weights download once into cache/ (gitignored). Runs on MPS when available, else CPU.
"""

from __future__ import annotations

import os
import threading
from pathlib import Path

import numpy as np

# COCO class name -> booth label. Anything not listed is ignored by detect().
COCO_TO_SCOUT: dict[str, str] = {
    "potted plant": "native_flower",
    "bottle": "bug_spray",
    "bowl": "pebble_dish",
    "vase": "honey_jar",
    "cup": "honey_jar",
    "teddy bear": "honey_jar",  # the honey bear bottle
}

# Stricter per-COCO-class floors where false positives are costly (teddy bear fires on textured wood).
MIN_CONF: dict[str, float] = {"teddy bear": 0.5}

ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / "cache"
MODEL = os.environ.get("SCOUT_YOLO_MODEL", "yolo11n.pt")
IMGSZ = 640
CONF = 0.35

_lock = threading.Lock()
_models: dict[str, object] = {}


def _device() -> str:
    import torch

    return "mps" if torch.backends.mps.is_available() else "cpu"


def _load(name: str):
    with _lock:
        m = _models.get(name)
        if m is None:
            from ultralytics import YOLO

            CACHE.mkdir(parents=True, exist_ok=True)
            m = YOLO(str(CACHE / name))  # ultralytics downloads known asset names to this path
            m.predict(np.zeros((IMGSZ, IMGSZ, 3), np.uint8), imgsz=IMGSZ, device=_device(), verbose=False)  # warm up
            _models[name] = m
        return m


def warmup() -> None:
    """Load the model and run one dummy pass, so the first live frame is not slow."""
    _load(MODEL)


def _boxes(model, frame: np.ndarray, bgr: bool, conf: float, imgsz: int) -> list[tuple[str, float, float, float, float, float]]:
    if not bgr:
        frame = frame[:, :, ::-1]  # ultralytics treats numpy input as BGR
    h, w = frame.shape[:2]
    r = model.predict(np.ascontiguousarray(frame), imgsz=imgsz, conf=conf, device=_device(), verbose=False)[0]
    if r.boxes is None or len(r.boxes) == 0:
        return []
    xyxy = r.boxes.xyxy.cpu().numpy()
    cls = r.boxes.cls.cpu().numpy().astype(int)
    score = r.boxes.conf.cpu().numpy()
    out = []
    for (x1, y1, x2, y2), c, s in zip(xyxy, cls, score):
        out.append((r.names[int(c)], float(s), x1 / w, y1 / h, (x2 - x1) / w, (y2 - y1) / h))
    return out


def _box(label: str, s: float, x: float, y: float, bw: float, bh: float) -> dict:
    return {"label": label, "score": round(s, 3), "x": round(float(x), 4), "y": round(float(y), 4), "w": round(float(bw), 4), "h": round(float(bh), 4), "via": "detector"}


def raw_detect(frame: np.ndarray, bgr: bool = True, conf: float = CONF, imgsz: int = IMGSZ) -> list[dict]:
    """Every COCO detection, label = COCO class name (not a ScoutClass; for a dashboard demo overlay)."""
    return [_box(*b) for b in _boxes(_load(MODEL), frame, bgr, conf, imgsz)]


def detect(frame: np.ndarray, bgr: bool = True, conf: float = CONF, imgsz: int = IMGSZ) -> list[dict]:
    """ScoutBox dicts for COCO classes in COCO_TO_SCOUT, best first. Normalized x,y (top-left), w,h."""
    out = [_box(COCO_TO_SCOUT[n], s, x, y, bw, bh) for n, s, x, y, bw, bh in _boxes(_load(MODEL), frame, bgr, conf, imgsz) if n in COCO_TO_SCOUT and s >= MIN_CONF.get(n, 0.0)]
    return sorted(out, key=lambda b: -b["score"])

