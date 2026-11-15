"""Frame sources that always hand out the newest frame (backlog is dropped).

    rtsp://127.0.0.1:8554/scout   MediaMTX (the glasses' stream); reconnects forever
    webcam:0                      local camera
    path/to/video.mp4             paced at the file's own fps, like a live stream (loops with --loop)
    path/to/folder/               images, each held for `hold_s` seconds
"""

from __future__ import annotations

import os
import sys
import threading
import time
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np


@dataclass
class Frame:
    rgb: np.ndarray  # HxWx3 uint8
    at: float  #       epoch ms when read
    seq: int
    pos_ms: float | None = None  # position in a file source (for replay checks)


class Source:
    def __init__(self, spec: str, loop: bool = False, hold_s: float = 2.0) -> None:
        self.spec = spec
        self.loop = loop
        self.hold_s = hold_s
        self._latest: Frame | None = None
        self._cond = threading.Condition()
        self._stop = False
        self.ended = False
        self.connected = False
        self._seq = 0
        if spec.startswith("webcam:"):
            self.label = spec
        elif "://" in spec:
            self.label = spec
        elif Path(spec).is_dir():
            self.label = f"folder:{Path(spec).resolve()}"
        else:
            self.label = f"file:{Path(spec).resolve()}"
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    # ------------------------------------------------------------------ consumer side

    def latest(self, after_seq: int = -1, timeout: float = 2.0) -> Frame | None:
        """Newest frame with seq > after_seq; waits up to `timeout` for one."""
        with self._cond:
            end = time.time() + timeout
            while (self._latest is None or self._latest.seq <= after_seq) and not self.ended:
                left = end - time.time()
                if left <= 0:
                    return None
                self._cond.wait(left)
            f = self._latest
            return f if f is not None and f.seq > after_seq else None

    def stop(self) -> None:
        self._stop = True

    def _push(self, bgr_or_rgb: np.ndarray, is_bgr: bool = True, pos_ms: float | None = None) -> None:
        rgb = cv2.cvtColor(bgr_or_rgb, cv2.COLOR_BGR2RGB) if is_bgr else bgr_or_rgb
        with self._cond:
            self._seq += 1
            self._latest = Frame(rgb, time.time() * 1000, self._seq, pos_ms)
            self._cond.notify_all()

    # ------------------------------------------------------------------ producer side

    def _run(self) -> None:
        try:
            if self.label.startswith("folder:"):
                self._run_folder()
            elif self.spec.startswith("webcam:") or "://" in self.spec:
                self._run_live()
            else:
                self._run_file()
        finally:
            with self._cond:
                self.ended = True
                self._cond.notify_all()

    def _run_live(self) -> None:
        if "://" in self.spec:
            os.environ.setdefault("OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp|fflags;nobuffer|flags;low_delay")
        last_warn = 0.0
        while not self._stop:
            cap = cv2.VideoCapture(int(self.spec.split(":")[1])) if self.spec.startswith("webcam:") else cv2.VideoCapture(self.spec, cv2.CAP_FFMPEG)
            cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
            if not cap.isOpened():
                if time.time() - last_warn > 5:
                    print(f"[source] cannot open {self.spec}, retrying", file=sys.stderr, flush=True)
                    last_warn = time.time()
                time.sleep(1)
                continue
            print(f"[source] connected {self.spec}", file=sys.stderr, flush=True)
            self.connected = True
            while not self._stop:
                ok, bgr = cap.read()  # this thread reads as fast as frames arrive, so nothing piles up
                if not ok:
                    break
                self._push(bgr)
            self.connected = False
            cap.release()
            if not self._stop:
                print(f"[source] lost {self.spec}, reconnecting", file=sys.stderr, flush=True)
                time.sleep(0.5)

    def _run_file(self) -> None:
        while not self._stop:
            cap = cv2.VideoCapture(self.spec)
            if not cap.isOpened():
                raise SystemExit(f"cannot open {self.spec}")
            fps = cap.get(cv2.CAP_PROP_FPS) or 15.0
            self.connected = True
            t0 = time.time()
            n = 0
            while not self._stop:
                ok, bgr = cap.read()
                if not ok:
                    break
                due = t0 + n / fps
                delay = due - time.time()
                if delay > 0:
                    time.sleep(delay)
                self._push(bgr, pos_ms=n * 1000.0 / fps)
                n += 1
            cap.release()
            if not self.loop:
                return

    def _run_folder(self) -> None:
        files = sorted(p for p in Path(self.spec).rglob("*") if p.suffix.lower() in (".jpg", ".jpeg", ".png"))
        if not files:
            raise SystemExit(f"no images in {self.spec}")
        self.connected = True
        while not self._stop:
            for f in files:
                bgr = cv2.imread(str(f))
                if bgr is None:
                    continue
                end = time.time() + self.hold_s
                while time.time() < end and not self._stop:
                    self._push(bgr)
                    time.sleep(1 / 15)
            if not self.loop:
                return
