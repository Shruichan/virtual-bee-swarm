"""HONEYMOON Orbis relay: Visko Orbis (Reactor, WebRTC) -> newest-only JPEG frames over a WebSocket.

    uv run python relay.py            # live if REACTOR_API_KEY is set, else fake
    uv run python relay.py --fake     # animated test pattern, no Reactor, no credits

HTTP (all take ?token=<HIVE_TOKEN> or header x-hive-token when HIVE_TOKEN is set):
    POST /orbis/warm                          connect a session early (billing starts at ready)
    POST /orbis/start {prompt?, seed?, stage?, passthrough?}
    POST /orbis/stage {prompt?, stage?, reset?, passthrough?}
    POST /orbis/stop
    GET  /orbis/status                        {state, stage, since, fps, ...}
    GET  /orbis/frame.jpg                     latest frame (debug / monitor)
    WS   /orbis/frames                        binary JPEG frames (add ?status=1 for JSON status text frames)
POSTs return at once and the work runs in order in the background; add ?wait=1 to block until it settles.
"""

from __future__ import annotations

import argparse
import asyncio
import hmac
import io
import json
import logging
import math
import os
import threading
import time
from pathlib import Path
from typing import Any

from aiohttp import WSMsgType, web
from PIL import Image, ImageChops, ImageDraw, ImageFont, ImageOps

log = logging.getLogger("orbis")

HERE = Path(__file__).resolve().parent
MODEL = "reactor/visko-orbis-stable"
GEN_W, GEN_H = 832, 480  # what Orbis generates
# Smallest delivery tiers first: we downscale to <= ORBIS_MAX_W anyway, so a bigger tier only costs bandwidth.
RESOLUTION_PREFERENCE = ("native", "480p", "720p", "1080p")


def _env_num(name: str, default: float, cast=float) -> Any:
    raw = os.environ.get(name, "").strip()
    if not raw:
        return default
    try:
        value = cast(raw)
        if value <= 0:
            raise ValueError
        return value
    except ValueError:
        log.warning("ignoring bad %s=%r, using %s", name, raw, default)
        return default


class Config:
    def __init__(self, fake_flag: bool = False) -> None:
        self.port = int(_env_num("ORBIS_PORT", 8241, int))
        self.bind = os.environ.get("ORBIS_BIND", "127.0.0.1")
        self.token = os.environ.get("HIVE_TOKEN", "")
        self.api_key = os.environ.get("REACTOR_API_KEY", "")
        self.model = os.environ.get("ORBIS_MODEL", MODEL)
        self.idle_s = _env_num("ORBIS_IDLE_S", 90.0)
        self.max_s = _env_num("ORBIS_MAX_S", 900.0)
        self.max_w = int(_env_num("ORBIS_MAX_W", 640, int))
        self.fps = _env_num("ORBIS_FPS", 12.0)
        self.quality = int(_env_num("ORBIS_JPEG_Q", 70, int))
        self.connect_timeout_s = _env_num("ORBIS_CONNECT_TIMEOUT_S", 90.0)
        self.prime_s = _env_num("ORBIS_PRIME_S", 45.0)  # start -> first frame, else failed
        self.stall_s = _env_num("ORBIS_STALL_S", 15.0)  # ready but no frames -> reconnect
        self.wait_s = _env_num("ORBIS_WAIT_S", 60.0)  # ?wait=1 limit
        self.scenes_path = Path(os.environ.get("ORBIS_SCENES", HERE / "scenes.json"))
        self.fake = fake_flag or os.environ.get("ORBIS_FAKE", "") not in ("", "0", "false")
        self.fake_reason = "requested" if self.fake else ""
        if not self.fake and not self.api_key:
            self.fake, self.fake_reason = True, "REACTOR_API_KEY is not set"


def load_scenes(path: Path) -> dict[str, Any]:
    """{seed, seconds, scenes: [{id, stage, prompt, morph}]}; an empty set if the file is missing or bad."""
    try:
        data = json.loads(path.read_text())
        scenes = [s for s in data.get("scenes", []) if isinstance(s.get("stage"), int) and s.get("prompt")]
        return {"seed": int(data.get("seed", 42)), "seconds": float(data.get("seconds", 12)), "scenes": scenes}
    except Exception as exc:
        log.warning("no scenes from %s (%s): every request must carry a prompt", path, exc)
        return {"seed": 42, "seconds": 12.0, "scenes": []}


# ---------------------------------------------------------------- test pattern (fake mode + placeholder clips)

STAGE_COLORS = [
    ((38, 38, 44), (150, 150, 160)),  # 0 grey moon yard
    ((44, 28, 70), (176, 120, 226)),  # 1 one purple patch
    ((22, 58, 36), (126, 204, 120)),  # 2 greening
    ((30, 84, 40), (250, 206, 84)),  # 3 full bloom
    ((2, 2, 12), (44, 96, 210)),  # 4 earthrise
]
_gradients: dict[tuple[int, int, int], Image.Image] = {}
_fonts: dict[int, Any] = {}


def _font(size: int) -> Any:
    if size not in _fonts:
        try:
            _fonts[size] = ImageFont.load_default(size=size)
        except Exception:
            _fonts[size] = ImageFont.load_default()
    return _fonts[size]


def _gradient(w: int, h: int, stage: int) -> Image.Image:
    key = (w, h, stage)
    if key not in _gradients:
        lo, hi = STAGE_COLORS[stage % len(STAGE_COLORS)]
        # A horizontally periodic ramp (0 -> 255 -> 0), so scrolling it never shows a seam.
        ramp = Image.new("L", (256, 1))
        ramp.putdata([int(255 * (1 - abs(2 * i / 255 - 1))) for i in range(256)])
        _gradients[key] = ImageOps.colorize(ramp.resize((w, h)), lo, hi)
    return _gradients[key]


def render_test_pattern(w: int, h: int, t: float, stage: int, label: str, frame_no: int, note: str = "") -> Image.Image:
    """Scrolling gradient + drifting honeycomb + stage number + wall clock, so motion and latency are obvious."""
    img = ImageChops.offset(_gradient(w, h, stage), int(t * 60) % w, 0)
    draw = ImageDraw.Draw(img)
    r = max(12, w // 26)
    dx, dy = math.sqrt(3) * r, 1.5 * r
    ox, oy = (t * 22) % dx, (t * 14) % (2 * dy)
    lit = int(t * 4)
    cols, rows = int(w / dx) + 3, int(h / dy) + 3
    for row in range(-1, rows):
        for col in range(-1, cols):
            cx = col * dx + (dx / 2 if row % 2 else 0) + ox - dx
            cy = row * dy + oy - 2 * dy
            pts = [(cx + r * math.cos(a), cy + r * math.sin(a)) for a in (math.pi / 3 * i + math.pi / 6 for i in range(6))]
            fill = (255, 196, 40) if (row * cols + col) % 37 == lit % 37 else None
            draw.polygon(pts, fill=fill, outline=(255, 214, 90), width=2)
    now = time.time()
    clock = time.strftime("%H:%M:%S", time.localtime(now)) + f".{int(now * 1000) % 1000:03d}"
    big, mid, small = _font(max(18, h // 6)), _font(max(12, h // 14)), _font(max(10, h // 24))
    ink = {"fill": "white", "stroke_width": 2, "stroke_fill": "black"}
    draw.text((12, 10), "ORBIS FAKE", font=mid, **ink)
    draw.text((w / 2, h / 2 - h * 0.06), f"STAGE {stage}", font=big, anchor="mm", **ink)
    draw.text((w / 2, h / 2 + h * 0.1), label, font=mid, anchor="mm", **ink)
    draw.text((12, h - 12), clock, font=mid, anchor="ls", **ink)
    draw.text((w - 12, h - 12), f"#{frame_no}", font=mid, anchor="rs", **ink)
    if note:
        draw.text((w / 2, h * 0.72), note[:70], font=small, anchor="mm", **ink)
    return img


def encode_jpeg(img: Image.Image, max_w: int, quality: int) -> bytes:
    if img.width > max_w:
        img = img.resize((max_w, max(2, round(img.height * max_w / img.width))), Image.Resampling.BILINEAR, reducing_gap=2.0)
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=quality)
    return buf.getvalue()


# ---------------------------------------------------------------- sources


class FakeSource:
    """Same interface as ReactorSource, no network: lets Unity and the Hive be built without credits."""

    kind = "fake"

    def __init__(self, cfg: Config) -> None:
        self.cfg = cfg
        self.connected = self.started = False
        self.stage, self.label, self.prompt = 0, "", ""
        self.src_frames, self.last_frame_at = 0, 0.0
        self._run_t0 = 0.0
        self.on_lost = lambda reason: None

    async def connect(self) -> None:
        await asyncio.sleep(1.0)  # pretend to wait for a GPU
        self.connected = True

    async def start(self, prompt: str, seed: int, passthrough: bool) -> None:
        self.prompt, self.started, self._run_t0 = prompt, True, time.monotonic()

    async def steer(self, prompt: str, passthrough: bool) -> None:
        self.prompt = prompt

    async def recover(self, force: bool = False) -> bool:
        return True

    async def disconnect(self) -> None:
        self.connected = self.started = False

    def grab(self) -> Any:
        if not self.started or time.monotonic() - self._run_t0 < 1.5:  # first-frame delay, like the real model
            return None
        self.src_frames += 1
        self.last_frame_at = time.monotonic()
        return (time.time(), self.stage, self.label, self.src_frames, self.prompt)

    def encode(self, frame: Any) -> bytes:
        t, stage, label, n, prompt = frame
        w = min(self.cfg.max_w, GEN_W)
        h = round(w * GEN_H / GEN_W / 2) * 2
        return encode_jpeg(render_test_pattern(w, h, t, stage, label, n, prompt), w, self.cfg.quality)


class ReactorSource:
    """One Reactor session at a time. SDK callbacks for status/messages arrive on the loop thread;
    video frames arrive on the SDK's media thread and only ever touch `_slot` (newest wins)."""

    kind = "reactor"

    def __init__(self, cfg: Config) -> None:
        self.cfg = cfg
        self.client: Any = None
        self.connected = self.started = False
        self.stage, self.label = 0, ""
        self.src_frames, self.last_frame_at = 0, 0.0
        self.model_state: dict[str, Any] = {}
        self._cmd_waiters: dict[str, asyncio.Future] = {}  # command name -> resolved by its command_error broadcast
        self.on_lost = lambda reason: None
        self._slot: tuple[bytes, int, int] | None = None
        self._slot_lock = threading.Lock()
        self._expect_connected = False
        self._recovering = False
        self._settings_applied = False
        self._bg: set[asyncio.Task] = set()

    # -- lifecycle

    async def connect(self) -> None:
        from reactor_sdk import Reactor  # lazy: fake mode must work even if the native lib cannot load

        await self.disconnect()
        client = Reactor(
            model_name=self.cfg.model,
            api_key=self.cfg.api_key,
            # Server-side backstop: Reactor ends the session even if this process dies.
            max_session_duration_seconds=int(min(86400, self.cfg.max_s + 60)),
        )
        self.client = client
        client.on("status_changed", lambda s, c=client: self._on_status(c, s))
        client.on("message", lambda m, c=client: self._on_message(c, m))
        client.on("error", lambda e, c=client: self._on_error(c, e))
        await asyncio.wait_for(client.connect(), self.cfg.connect_timeout_s)
        await self._attach_video(client)
        self.connected, self.started, self._settings_applied = True, False, False
        self._expect_connected = True
        log.info("reactor session %s ready", client.session_id)

    async def _attach_video(self, client: Any) -> None:
        tracks: list[Any] = []
        for _ in range(50):  # the track list arrives with the model's capabilities, just after connect
            tracks = list(client.tracks.with_kind("video").with_direction("recvonly"))
            if tracks:
                break
            await asyncio.sleep(0.1)
        track = next((t for t in tracks if t.name == "main_video"), tracks[0] if tracks else None)
        if track is None:
            log.warning("no video track declared yet; registering main_video by name")
            track = client.track("main_video")
        track.on_raw_frame(self._on_frame)  # handlers survive reconnects

    async def disconnect(self) -> None:
        client, self.client = self.client, None
        self._expect_connected = False
        self.connected = self.started = False
        with self._slot_lock:
            self._slot = None
        if client is None:
            return
        try:
            await asyncio.wait_for(client.disconnect(), 10)  # ends the session: stops billing
        except Exception as exc:
            log.warning("disconnect: %r (the session ends by itself within ~30 s)", exc)
        finally:
            try:
                client.close()
            except Exception:
                log.exception("close")

    async def recover(self, force: bool = False) -> bool:
        """Resume the same session (Reactor keeps it alive ~30 s after a drop).
        force: cycle the connection even if it still reads ready (frames stalled)."""
        client = self.client
        if client is None:
            return False
        self._recovering = True
        deadline, delay = time.monotonic() + 28, 1.0
        try:
            while time.monotonic() < deadline:
                if client.status == "ready" and not force:
                    self.connected = True
                    return True
                force = False
                try:
                    await asyncio.wait_for(client.reconnect(), max(1.0, deadline - time.monotonic()))
                    self.connected = True
                    log.info("reconnected to session %s", client.session_id)
                    return True
                except asyncio.TimeoutError:
                    log.warning("reconnect timed out")
                except Exception as exc:
                    log.warning("reconnect failed: %r", exc)
                    if getattr(exc, "recoverable", True) is False:
                        return False
                await asyncio.sleep(delay)
                delay = min(delay * 2, 5.0)
            return False
        finally:
            self._recovering = False

    # -- generation

    async def start(self, prompt: str, seed: int, passthrough: bool) -> None:
        if self.started:
            await self._cmd("reset", {})  # a seed only applies to a fresh run
            self.started = False
        with self._slot_lock:
            self._slot = None
        if not self._settings_applied:
            await self._cmd("set_audio_enabled", {"audio_enabled": False})  # SATIE does the sound
            await self._pick_resolution()
            self._settings_applied = True  # both survive reset
        await self._cmd("set_seed", {"seed": seed})
        await self._set_prompt(prompt, passthrough)
        await self._cmd("start", {})
        self.started = True

    async def steer(self, prompt: str, passthrough: bool) -> None:
        await self._set_prompt(prompt, passthrough)  # morphs at the next chunk boundary (~2-4 s)

    async def _set_prompt(self, prompt: str, passthrough: bool) -> None:
        data: dict[str, Any] = {"prompt": prompt}
        if passthrough:
            data["passthrough"] = True
        await self._cmd("set_prompt", data)

    async def _pick_resolution(self) -> None:
        offered = self.model_state.get("available_resolutions") or []
        want = next((r for r in RESOLUTION_PREFERENCE if r in offered), None)
        if want and want != self.model_state.get("resolution"):
            try:
                await self._cmd("set_resolution", {"resolution": want})
            except Exception as exc:
                log.warning("set_resolution %s: %r", want, exc)

    async def _cmd(self, name: str, data: dict[str, Any]) -> Any:
        if self.client is None:
            raise RuntimeError(f"{name}: no session")
        # Orbis reports a rejected command only as a `command_error` broadcast (schema docs), so the awaited
        # call may hang or return None. Race it against that broadcast instead of trusting the reply alone.
        rejected: asyncio.Future = asyncio.get_running_loop().create_future()
        self._cmd_waiters[name] = rejected
        send = asyncio.ensure_future(self.client.send_command(name, data))
        send.add_done_callback(lambda t: t.cancelled() or t.exception())  # never "exception was never retrieved"
        try:
            done, _ = await asyncio.wait({send, rejected}, timeout=15, return_when=asyncio.FIRST_COMPLETED)
            if rejected.done():
                raise RuntimeError(f"{name} rejected: {rejected.result()}")
            if not done:
                raise RuntimeError(f"{name}: no reply in 15 s")
            reply = send.result()
        finally:
            send.cancel()  # no-op when finished; drops the await (not the session) on rejection/timeout/cancel
            if self._cmd_waiters.get(name) is rejected:
                del self._cmd_waiters[name]
        if isinstance(reply, dict) and reply.get("type") == "command_error":
            raise RuntimeError(f"{name} rejected: {reply.get('data')}")
        return reply

    # -- frames (media thread)

    def _on_frame(self, bgra: bytes, width: int, height: int, frame_id: int, timestamp_us: int, user_data: bytes) -> None:
        with self._slot_lock:
            self._slot = (bgra, width, height)
            self.src_frames += 1
            self.last_frame_at = time.monotonic()

    def grab(self) -> Any:
        with self._slot_lock:
            frame, self._slot = self._slot, None
        return frame

    def encode(self, frame: Any) -> bytes:
        bgra, w, h = frame
        img = Image.frombuffer("RGB", (w, h), bgra, "raw", "BGRX", 0, 1)
        return encode_jpeg(img, self.cfg.max_w, self.cfg.quality)

    # -- SDK events (loop thread)

    def _on_status(self, client: Any, status: str) -> None:
        if client is not self.client:
            return
        log.info("reactor status: %s", status)
        if status == "ready":
            self.connected = True
        elif status == "disconnected" and self._expect_connected and not self._recovering:
            self.connected = False
            self.on_lost("connection dropped")

    def _on_message(self, client: Any, msg: Any) -> None:
        if client is not self.client or not isinstance(msg, dict):
            return
        kind, data = msg.get("type"), msg.get("data") or {}
        if kind == "state" and isinstance(data, dict):
            self.model_state = data
        elif kind == "command_error":
            log.warning("command_error %s: %s", data.get("command"), data.get("reason"))
            waiter = self._cmd_waiters.get(data.get("command"))
            if waiter is not None and not waiter.done():
                waiter.set_result(data.get("reason") or "command_error")
        elif kind == "generation_complete" and self.started:
            log.info("run reached its chunk cap; starting again")  # the model never auto-restarts
            task = asyncio.ensure_future(self._restart())
            self._bg.add(task)
            task.add_done_callback(self._bg.discard)
        elif kind in ("generation_started", "generation_reset"):
            log.info("%s %s", kind, data)

    async def _restart(self) -> None:
        try:
            await self._cmd("start", {})
        except Exception as exc:
            log.warning("restart after generation_complete: %r", exc)

    def _on_error(self, client: Any, err: Any) -> None:
        if client is self.client:
            log.warning("reactor error %s: %s (recoverable=%s)", getattr(err, "code", "?"), err, getattr(err, "recoverable", "?"))


# ---------------------------------------------------------------- relay state machine


class Relay:
    def __init__(self, cfg: Config) -> None:
        self.cfg = cfg
        self.scenes = load_scenes(cfg.scenes_path)
        self.source: FakeSource | ReactorSource = FakeSource(cfg) if cfg.fake else ReactorSource(cfg)
        self.source.on_lost = self._on_lost
        self.state, self.stage, self.since = "off", 0, _now_ms()
        self.prompt, self.seed, self.error = "", self.scenes["seed"], ""
        self.latest: bytes | None = None
        self.seq = 0
        self.fps = self.src_fps = 0.0
        self.clients = 0
        self._frame_ev = asyncio.Event()
        self._state_ev = asyncio.Event()
        self._lock = asyncio.Lock()  # control ops run one at a time, in arrival order
        self._ops: set[asyncio.Task] = set()
        self._tasks: list[asyncio.Task] = []
        self._stop_task: asyncio.Task | None = None
        self._stage_gen = 0
        self._session_t0: float | None = None
        self._waiting_since = 0.0  # start/recover time, for the first-frame deadline
        self._last_demand = time.monotonic()
        self._published = 0

    # -- public API

    def status(self) -> dict[str, Any]:
        scene = self.scene_for(self.stage)
        clip = f"{scene['id']}.mp4" if scene else None
        return {
            "type": "orbis",
            "state": self.state,
            "stage": self.stage,
            "since": self.since,
            "fps": round(self.fps, 1),
            "src_fps": round(self.src_fps, 1),
            "fallback_clip": clip,
            "fake": self.cfg.fake,
            "connected": self.source.connected,
            "generating": self.source.started,
            "prompt": self.prompt,
            "seed": self.seed,
            "error": self.error or None,
            "clients": self.clients,
            "session_s": round(time.monotonic() - self._session_t0) if self._session_t0 else 0,
            "server_time": _now_ms(),
        }

    def scene_for(self, stage: int) -> dict[str, Any] | None:
        return next((s for s in self.scenes["scenes"] if s["stage"] == stage), None)

    def demand(self) -> None:
        self._last_demand = time.monotonic()

    def warm(self) -> None:
        self.demand()
        self._priming_now()
        self._queue("warm", self._op_warm)

    def _priming_now(self) -> None:
        """Answer the POST with the state it is heading to, not the stale one."""
        if self.state in ("off", "ended", "failed"):
            self.error = ""
            self._set_state("priming")

    def start(self, prompt: str | None, seed: int | None, stage: int | None, passthrough: bool) -> None:
        stage = self.stage if stage is None else stage
        if prompt is None:
            scene = self.scene_for(stage)
            if scene is None:
                raise ValueError(f"no prompt given and no scene for stage {stage}")
            prompt = scene["prompt"]
        seed = self.scenes["seed"] if seed is None else seed
        self.demand()
        self.stage = stage  # so the reply and the priming status already name this stage's fallback clip
        self._priming_now()
        self._stage_gen += 1  # any queued stage change is now stale
        self._queue("start", lambda: self._op_start(prompt, seed, stage, passthrough))

    def change_stage(self, prompt: str | None, stage: int | None, reset: bool, passthrough: bool) -> None:
        """No prompt: use the scene's morph prompt (or its full prompt + reset when it has no morph).
        No stage and no prompt: the next stage."""
        stage = self.stage + 1 if stage is None and prompt is None else (self.stage if stage is None else stage)
        scene = self.scene_for(stage)
        if prompt is None and scene is None:
            raise ValueError(f"no prompt given and no scene for stage {stage}")
        full = prompt or scene["prompt"]
        morph = prompt or scene.get("morph")
        reset = reset or not morph
        self.demand()
        self._stage_gen += 1
        gen = self._stage_gen
        self._queue("stage", lambda: self._op_stage(full, morph, stage, reset, passthrough, gen), fatal=False)

    async def stop(self, reason: str) -> None:
        for task in list(self._ops):
            task.cancel()
        async with self._lock:
            await self._end("ended", reason)

    async def wait_settled(self, timeout: float) -> None:
        """Until queued ops finish and the state is no longer priming (or timeout)."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if not self._ops and self.state != "priming":
                return
            self._state_ev.clear()
            try:
                await asyncio.wait_for(self._state_ev.wait(), min(0.5, max(0.01, deadline - time.monotonic())))
            except asyncio.TimeoutError:
                pass

    async def wait_frame(self, after_seq: int, timeout: float) -> bool:
        """True when a frame newer than after_seq is in `latest`. Must really await when there is none:
        after a session ends `seq` stays high while `latest` is None, and returning at once there made a
        new frames client spin forever without yielding, freezing the whole relay (stop included)."""
        if self.seq > after_seq and self.latest is not None:
            return True
        try:
            await asyncio.wait_for(self._frame_ev.wait(), timeout)
        except asyncio.TimeoutError:
            return False
        return self.seq > after_seq and self.latest is not None

    # -- ops (run under self._lock)

    def _queue(self, name: str, fn, fatal: bool = True) -> None:
        task = asyncio.create_task(self._run_op(name, fn, fatal))
        self._ops.add(task)
        task.add_done_callback(self._op_done)

    def _op_done(self, task: asyncio.Task) -> None:
        self._ops.discard(task)
        self._state_ev.set()

    async def _run_op(self, name: str, fn, fatal: bool) -> None:
        async with self._lock:
            try:
                await fn()
            except asyncio.CancelledError:
                log.info("%s cancelled", name)
                raise
            except Exception as exc:
                log.exception("%s failed", name)
                if fatal:
                    await self._end("failed", f"{name}: {exc}")
                else:  # e.g. a rejected stage prompt: keep streaming the old scene; a drop is handled by recover
                    self.error = f"{name}: {exc}"
                    self._set_state(self.state)

    async def _ensure_connected(self) -> None:
        if self.source.connected:
            return
        self.error = ""
        self._set_state("priming")
        log.info("connecting to %s ...", "fake source" if self.cfg.fake else self.cfg.model)
        self._session_t0 = time.monotonic()
        await self.source.connect()

    async def _op_warm(self) -> None:
        await self._ensure_connected()

    async def _op_start(self, prompt: str, seed: int, stage: int, passthrough: bool) -> None:
        self.stage, self.prompt, self.seed = stage, prompt, seed
        self._label_source()
        await self._ensure_connected()
        self._set_state("priming")
        await self.source.start(prompt, seed, passthrough)
        self._waiting_since = time.monotonic()
        log.info("started stage %s seed %s: %s", stage, seed, prompt[:90])

    async def _op_stage(self, full: str, morph: str | None, stage: int, reset: bool, passthrough: bool, gen: int) -> None:
        if gen != self._stage_gen:
            return  # a newer stage change is queued; skip straight to it
        if not self.source.started:
            # No stream: remember the stage for the next /orbis/start, but never open a billed session here.
            self.stage, self.prompt = stage, full
            self._label_source()
            self._set_state(self.state)
            return
        prompt = full if reset or not morph else morph
        if reset:
            await self.source.start(prompt, self.seed, passthrough)
            self._waiting_since = time.monotonic()
        else:
            await self.source.steer(prompt, passthrough)
        self.stage, self.prompt, self.error = stage, prompt, ""
        self._label_source()
        self._set_state(self.state)  # bump `since` so watchers see the stage change
        log.info("stage %s (%s): %s", stage, "reset" if reset else "morph", prompt[:90])

    async def _op_recover(self, reason: str, force: bool = False) -> None:
        if self.state not in ("priming", "ready"):
            return
        log.warning("recovering: %s", reason)
        self._set_state("priming")  # no frames within ORBIS_PRIME_S after this -> failed
        if not await self.source.recover(force):
            await self._end("failed", f"lost the session ({reason})")
            return
        self._waiting_since = time.monotonic()

    def _label_source(self) -> None:
        scene = self.scene_for(self.stage)
        self.source.stage, self.source.label = self.stage, scene["id"] if scene else f"stage {self.stage}"

    async def _end(self, state: str, reason: str) -> None:
        was_live = self.state in ("priming", "ready") or self.source.connected or self._session_t0 is not None
        await self.source.disconnect()
        self._session_t0 = None
        self.latest = None
        if state == "failed":
            self.error = reason
        if was_live or state == "failed":
            log.info("session %s: %s", state, reason)
            self._set_state(state)

    def _on_lost(self, reason: str) -> None:
        if self.state in ("priming", "ready"):
            self._queue("recover", lambda: self._op_recover(reason))

    def _set_state(self, state: str) -> None:
        self.state, self.since = state, _now_ms()
        self._state_ev.set()

    # -- background loops

    def run(self) -> None:
        self._tasks = [asyncio.create_task(self._pump()), asyncio.create_task(self._watchdog())]

    async def shutdown(self) -> None:
        for task in self._tasks:
            task.cancel()
        try:
            await asyncio.wait_for(self.stop("relay shutting down"), 15)
        except Exception:
            log.exception("shutdown")

    async def _pump(self) -> None:
        """At most ORBIS_FPS times a second: take the newest source frame, JPEG it in a thread, publish."""
        loop = asyncio.get_running_loop()
        period, next_t = 1.0 / self.cfg.fps, loop.time()
        failures = 0
        while True:
            next_t += period
            delay = next_t - loop.time()
            if delay < 0:  # fell behind: skip ahead instead of bursting
                next_t, delay = loop.time(), 0
            await asyncio.sleep(delay)
            if self.state in ("off", "ended", "failed"):
                continue
            frame = self.source.grab()
            if frame is None:
                continue
            try:
                jpeg = await asyncio.to_thread(self.source.encode, frame)
                failures = 0
            except Exception:
                failures += 1
                if failures in (1, 100):
                    log.exception("frame encode failed")
                continue
            if self.state in ("off", "ended", "failed"):
                continue  # the session ended while we encoded
            self.latest, self.seq = jpeg, self.seq + 1
            self._published += 1
            ev, self._frame_ev = self._frame_ev, asyncio.Event()
            ev.set()
            if self.state == "priming" and self.source.started:
                self._set_state("ready")
                log.info("first frame (%d bytes)", len(jpeg))

    async def _watchdog(self) -> None:
        last_t, last_pub, last_src, last_log = time.monotonic(), 0, 0, 0.0
        while True:
            await asyncio.sleep(1.0)
            try:
                now = time.monotonic()
                dt = max(1e-3, now - last_t)
                self.fps = (self._published - last_pub) / dt
                self.src_fps = (self.source.src_frames - last_src) / dt
                last_t, last_pub, last_src = now, self._published, self.source.src_frames
                if self.clients:
                    self._last_demand = now
                live = self.state in ("priming", "ready")
                if live and now - last_log > 10:
                    last_log = now
                    log.info("%s stage %s: out %.1f fps, source %.1f fps, %d client(s)", self.state, self.stage, self.fps, self.src_fps, self.clients)
                if not live or self._stopping():
                    continue
                if self._session_t0 and now - self._session_t0 > self.cfg.max_s:
                    self._spawn_stop(f"hard cap ORBIS_MAX_S={self.cfg.max_s:.0f}s")
                elif now - self._last_demand > self.cfg.idle_s:
                    self._spawn_stop(f"idle {self.cfg.idle_s:.0f}s with no frame clients")
                elif self._ops or not self.source.started:
                    continue
                elif self.state == "priming" and now - max(self._waiting_since, self.source.last_frame_at) > self.cfg.prime_s:
                    self._ops_fail(f"no frames within {self.cfg.prime_s:.0f}s")
                elif self.state == "ready" and now - self.source.last_frame_at > self.cfg.stall_s:
                    self._queue("recover", lambda: self._op_recover("stream stalled", force=True))
            except Exception:
                log.exception("watchdog")

    def _stopping(self) -> bool:
        return self._stop_task is not None and not self._stop_task.done()

    def _spawn_stop(self, reason: str) -> None:
        log.info("auto-stop: %s", reason)
        self._stop_task = asyncio.create_task(self.stop(reason))

    def _ops_fail(self, reason: str) -> None:
        async def fail() -> None:
            await self._end("failed", reason)

        self._queue("fail", fail)


def _now_ms() -> int:
    return int(time.time() * 1000)


# ---------------------------------------------------------------- HTTP / WS


def build_app(relay: Relay, cfg: Config) -> web.Application:
    sockets: set[web.WebSocketResponse] = set()

    def authorized(request: web.Request) -> bool:
        if not cfg.token:
            return True
        given = request.query.get("token") or request.headers.get("x-hive-token") or ""
        return hmac.compare_digest(given.encode(), cfg.token.encode())

    @web.middleware
    async def guard(request: web.Request, handler):
        if request.method == "OPTIONS":
            resp: web.StreamResponse = web.Response(status=204)
        elif request.path != "/healthz" and not authorized(request):
            resp = web.json_response({"error": "bad token"}, status=401)
        else:
            try:
                resp = await handler(request)
            except web.HTTPException:
                raise  # 404/405 from the router
            except ValueError as exc:  # bad input
                resp = web.json_response({"error": str(exc)}, status=400)
            except Exception as exc:  # never let one request take the relay down
                log.exception("%s %s", request.method, request.path)
                resp = web.json_response({"error": str(exc)}, status=500)
        if not resp.prepared:
            resp.headers["Access-Control-Allow-Origin"] = "*"
            resp.headers["Access-Control-Allow-Headers"] = "content-type, x-hive-token"
        return resp

    async def body(request: web.Request) -> dict[str, Any]:
        if not request.can_read_body:
            return {}
        try:
            data = await request.json()
        except Exception:
            raise ValueError("body must be JSON") from None
        if not isinstance(data, dict):
            raise ValueError("body must be a JSON object")
        return data

    def field(data: dict[str, Any], key: str, kind: type, check=lambda v: True) -> Any:
        value = data.get(key)
        if value is None:
            return None
        if (kind is int and isinstance(value, bool)) or not isinstance(value, kind) or not check(value):
            raise ValueError(f"bad {key}: {value!r}")
        return value

    def prompt_of(data: dict[str, Any]) -> str | None:
        p = field(data, "prompt", str, lambda v: 0 < len(v.strip()) <= 2000)
        return p.strip() if p else None

    async def reply(request: web.Request) -> web.Response:
        if request.query.get("wait") in ("1", "true"):
            await relay.wait_settled(cfg.wait_s)
        return web.json_response({"ok": True, **relay.status()})

    async def warm(request: web.Request) -> web.Response:
        relay.warm()
        return await reply(request)

    async def start(request: web.Request) -> web.Response:
        data = await body(request)
        relay.start(
            prompt_of(data),
            field(data, "seed", int, lambda v: 0 <= v < 2**31),
            field(data, "stage", int, lambda v: 0 <= v < 100),
            bool(data.get("passthrough")),
        )
        return await reply(request)

    async def stage(request: web.Request) -> web.Response:
        data = await body(request)
        relay.change_stage(
            prompt_of(data),
            field(data, "stage", int, lambda v: 0 <= v < 100),
            bool(data.get("reset")),
            bool(data.get("passthrough")),
        )
        return await reply(request)

    async def stop(request: web.Request) -> web.Response:
        await relay.stop("stop requested")
        return web.json_response({"ok": True, **relay.status()})

    async def status(request: web.Request) -> web.Response:
        return web.json_response(relay.status())

    async def frame_jpg(request: web.Request) -> web.Response:
        relay.demand()
        if relay.latest is None:
            return web.Response(status=204)
        return web.Response(body=relay.latest, content_type="image/jpeg", headers={"Cache-Control": "no-store"})

    async def frames(request: web.Request) -> web.WebSocketResponse:
        ws = web.WebSocketResponse(heartbeat=20, compress=False, max_msg_size=64 * 1024)
        await ws.prepare(request)
        want_status = request.query.get("status") in ("1", "true")
        sockets.add(ws)
        relay.clients += 1
        relay.demand()
        log.info("frames client connected (%d)", relay.clients)

        async def sender() -> None:
            last_seq, last_since = 0, -1
            try:
                while not ws.closed:
                    if want_status and relay.since != last_since:
                        last_since = relay.since
                        await ws.send_str(json.dumps(relay.status()))
                    if not await relay.wait_frame(last_seq, 1.0) or relay.latest is None:
                        continue
                    jpeg, last_seq = relay.latest, relay.seq  # newest only: anything older is dropped
                    await asyncio.wait_for(ws.send_bytes(jpeg), 5)
            except asyncio.CancelledError:
                raise
            except Exception as exc:  # a stuck or vanished client: drop it
                log.info("frames client dropped: %r", exc)
                await ws.close()

        task = asyncio.create_task(sender())
        try:
            async for msg in ws:  # read only to notice close; clients have nothing to say
                if msg.type == WSMsgType.ERROR:
                    break
        finally:
            task.cancel()
            try:
                await task
            except BaseException:
                pass
            sockets.discard(ws)
            relay.clients -= 1
            relay.demand()  # the idle clock starts when the last client leaves
            log.info("frames client left (%d)", relay.clients)
            await ws.close()
        return ws

    async def healthz(request: web.Request) -> web.Response:
        return web.Response(text="ok")

    app = web.Application(middlewares=[guard])
    app.router.add_post("/orbis/warm", warm)
    app.router.add_post("/orbis/start", start)
    app.router.add_post("/orbis/stage", stage)
    app.router.add_post("/orbis/stop", stop)
    app.router.add_get("/orbis/status", status)
    app.router.add_get("/orbis/frame.jpg", frame_jpg)
    app.router.add_get("/orbis/frames", frames)
    app.router.add_get("/healthz", healthz)

    async def on_startup(_app: web.Application) -> None:
        relay.run()

    async def on_shutdown(_app: web.Application) -> None:
        for ws in list(sockets):
            await ws.close()
        await relay.shutdown()  # end the Reactor session: it bills per second while connected

    app.on_startup.append(on_startup)
    app.on_shutdown.append(on_shutdown)
    return app


def main() -> None:
    parser = argparse.ArgumentParser(description="Visko Orbis -> JPEG frame relay")
    parser.add_argument("--fake", action="store_true", help="animated test pattern instead of Reactor")
    parser.add_argument("--port", type=int, help="overrides ORBIS_PORT")
    parser.add_argument("--bind", help="overrides ORBIS_BIND (default 127.0.0.1)")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    logging.getLogger("aiohttp.access").setLevel(logging.WARNING)

    cfg = Config(fake_flag=args.fake)
    cfg.port = args.port or cfg.port
    cfg.bind = args.bind or cfg.bind
    if cfg.fake:
        log.warning("FAKE MODE (%s): serving a test pattern, no Reactor session, no credits spent", cfg.fake_reason)
    else:
        log.info("live mode: %s, idle stop %.0fs, hard cap %.0fs", cfg.model, cfg.idle_s, cfg.max_s)
    if not cfg.token:
        log.warning("HIVE_TOKEN is not set: the relay accepts requests without a token")

    async def make_app() -> web.Application:
        return build_app(Relay(cfg), cfg)  # the Relay needs the running loop

    log.info("listening on http://%s:%d", cfg.bind, cfg.port)
    web.run_app(make_app(), host=cfg.bind, port=cfg.port, print=None, shutdown_timeout=5)


if __name__ == "__main__":
    main()
