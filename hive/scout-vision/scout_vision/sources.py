"""Frame sources that always hand out the newest frame (backlog is dropped).

    rtsp://127.0.0.1:8554/scout   MediaMTX (the glasses' stream); reconnects forever
    webcam:0                      local camera
    path/to/video.mp4             paced at the file's own fps, like a live stream (loops with --loop)
    path/to/folder/               images, each held for `hold_s` seconds
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import threading
import time
from dataclasses import dataclass
from pathlib import Path

os.environ.setdefault("OPENCV_LOG_LEVEL", "ERROR")  # no per-retry "can't capture by name" warnings
os.environ.setdefault("OPENCV_FFMPEG_LOGLEVEL", "-8")  # quiet libav (404s while the glasses are not streaming)

import cv2  # noqa: E402
import numpy as np  # noqa: E402


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
            elif self.spec.startswith("rtsp://") and shutil.which("ffmpeg") and os.environ.get("SCOUT_CAPTURE", "opencv") == "ffmpeg":
                self._run_ffmpeg()
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

    def _run_ffmpeg(self, w: int = 1280, h: int = 720) -> None:
        """RTSP via an ffmpeg pipe: no demuxer buffering (nobuffer, low_delay), raw RGB frames, and a stall
        watchdog: when the glasses' stream drops, reconnect after 2 s of silence instead of OpenCV's ~30 s."""
        cmd = ["ffmpeg", "-loglevel", "error", "-nostdin", "-rtsp_transport", "tcp", "-fflags", "nobuffer", "-flags", "low_delay",
               "-probesize", "32768", "-analyzeduration", "0", "-timeout", "3000000", "-i", self.spec, "-an",
               "-vf", f"scale={w}:{h}", "-pix_fmt", "rgb24", "-f", "rawvideo", "-"]
        size = w * h * 3
        last_warn = 0.0
        while not self._stop:
            proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=size)
            last_frame = [time.time()]

            def watchdog(p=proc, lf=last_frame) -> None:
                while p.poll() is None:
                    if time.time() - lf[0] > (2.0 if self.connected else 8.0):
                        p.kill()
                        return
                    time.sleep(0.25)

            threading.Thread(target=watchdog, daemon=True).start()
            got = 0
            while not self._stop:
                buf = proc.stdout.read(size)
                if not buf or len(buf) < size:
                    break
                if not got:
                    print(f"[source] connected {self.spec} (ffmpeg)", file=sys.stderr, flush=True)
                    self.connected = True
                got += 1
                last_frame[0] = time.time()
                self._push(np.frombuffer(buf, np.uint8).reshape(h, w, 3), is_bgr=False)
            proc.kill()
            proc.wait()
            if got:
                print(f"[source] lost {self.spec} after {got} frames, reconnecting", file=sys.stderr, flush=True)
            elif time.time() - last_warn > 5:
                print(f"[source] cannot open {self.spec}, retrying", file=sys.stderr, flush=True)
                last_warn = time.time()
            self.connected = False
            if not self._stop:
                time.sleep(0.3)

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
