"""Offline test of the live (Reactor) code path against a mock reactor_sdk. No network, no credits.

    uv run python test_relay.py
"""

from __future__ import annotations

import asyncio
import io
import os
import sys
import threading
import time
import types

from PIL import Image

os.environ.pop("REACTOR_API_KEY", None)
import relay  # noqa: E402


class MockTrack:
    def __init__(self, name: str, kind: str) -> None:
        self.name, self.kind, self.direction, self.handlers = name, kind, "recvonly", []

    def on_raw_frame(self, fn):
        self.handlers.append(fn)
        return fn


class MockTrackList(list):
    def with_kind(self, kind):
        return MockTrackList(t for t in self if t.kind == kind)

    def with_direction(self, direction):
        return MockTrackList(t for t in self if t.direction == direction)


class MockReactor:
    """The slice of reactor_sdk.Reactor the relay uses, with the same names and call shapes."""

    instances: list[MockReactor] = []
    fail_command: str | None = None
    # Orbis style: reject only via a command_error broadcast, then hang ("hang") or reply None ("none").
    broadcast_reject: tuple[str, str] | None = None
    reconnect_ok = True

    def __init__(self, model_name, api_key=None, **kw):
        self.model_name, self.kw = model_name, kw
        self.status = "disconnected"
        self.session_id = f"sess-{len(MockReactor.instances) + 1}"
        self.commands: list[tuple[str, dict]] = []
        self.handlers: dict[str, list] = {}
        self.video, self.audio = MockTrack("main_video", "video"), MockTrack("main_audio", "audio")
        self.generating = self.closed = self.disconnected = False
        self.reconnects = 0
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._frames, daemon=True)
        MockReactor.instances.append(self)

    def on(self, event, fn):
        self.handlers.setdefault(event, []).append(fn)

    def _fire(self, event, *args):
        for fn in self.handlers.get(event, []):
            fn(*args)

    @property
    def tracks(self):
        return MockTrackList([self.video, self.audio])

    def track(self, name):
        return self.video

    async def connect(self):
        await asyncio.sleep(0.05)
        self.status = "ready"
        self._fire("status_changed", "ready")
        self._fire("message", {"type": "state", "data": {"available_resolutions": ["native", "1080p"], "resolution": "1080p"}})
        self._thread.start()

    async def send_command(self, name, data):
        self.commands.append((name, data))
        if name == MockReactor.fail_command:
            raise RuntimeError(f"mock rejects {name}")
        if MockReactor.broadcast_reject and name == MockReactor.broadcast_reject[0]:
            self._fire("message", {"type": "command_error", "data": {"command": name, "reason": "mock moderation"}})
            if MockReactor.broadcast_reject[1] == "hang":
                await asyncio.sleep(3600)
            return None
        if name == "start":
            self.generating = True
        elif name == "reset":
            self.generating = False
        return {"type": f"{name}_ok", "data": {}}

    async def reconnect(self):
        self.reconnects += 1
        if not MockReactor.reconnect_ok:
            err = RuntimeError("session gone")
            err.recoverable = False
            raise err
        self.status = "ready"
        self._fire("status_changed", "ready")

    async def disconnect(self):
        self.disconnected = True
        self.status = "disconnected"
        self._fire("status_changed", "disconnected")

    def close(self):
        self.closed = True
        self._stop.set()

    def drop(self):
        self.status = "disconnected"
        self._fire("status_changed", "disconnected")

    def _frames(self):
        bgra = bytes([30, 160, 60, 255]) * (1920 * 1080)  # a 1080p delivery tier, BGRA like the SDK
        n = 0
        while not self._stop.wait(1 / 18):
            if self.generating and self.status == "ready":
                n += 1
                for fn in list(self.video.handlers):
                    fn(bgra, 1920, 1080, n, 0, b"")


sys.modules["reactor_sdk"] = types.SimpleNamespace(Reactor=MockReactor)


async def until(cond, timeout=8.0, what=""):
    t0 = time.monotonic()
    while not cond():
        if time.monotonic() - t0 > timeout:
            raise AssertionError(f"timed out waiting for {what}")
        await asyncio.sleep(0.05)


def names(client):
    return [c[0] for c in client.commands]


async def main() -> None:
    cfg = relay.Config()
    cfg.fake, cfg.api_key = False, "rk_test_not_real"
    cfg.idle_s, cfg.stall_s, cfg.prime_s = 3.0, 2.0, 5.0
    r = relay.Relay(cfg)
    assert isinstance(r.source, relay.ReactorSource)
    r.run()

    # 1. start: settings once, seed, prompt, start; frames -> ready; JPEG downscaled to <= 640 wide
    r.clients = 1  # pretend a frames client is connected (keeps the idle stop away)
    r.start(None, None, 0, False)
    await until(lambda: r.state == "ready", what="ready")
    c = MockReactor.instances[-1]
    assert names(c) == ["set_audio_enabled", "set_resolution", "set_seed", "set_prompt", "start"], c.commands
    assert c.commands[0][1] == {"audio_enabled": False} and c.commands[1][1] == {"resolution": "native"}
    assert c.commands[2][1] == {"seed": 20000}
    assert c.kw["max_session_duration_seconds"] == int(cfg.max_s + 60)
    img = Image.open(io.BytesIO(r.latest))
    assert r.latest[:2] == b"\xff\xd8" and img.size == (640, 360), img.size
    assert img.getpixel((10, 10))[1] > 120, img.getpixel((10, 10))  # BGRA -> RGB: green stays green
    print("ok  start + frames:", img.size, f"{len(r.latest)} bytes")

    # 2. morph stage = one set_prompt; reset stage = reset, seed, prompt, start (settings not repeated)
    c.commands.clear()
    r.change_stage(None, 1, False, False)
    await until(lambda: r.stage == 1 and not r._ops, what="morph")
    assert names(c) == ["set_prompt"] and "sprouts" in c.commands[0][1]["prompt"], c.commands
    c.commands.clear()
    r.change_stage(None, 4, False, False)  # earthrise has no morph -> reset
    await until(lambda: r.stage == 4 and not r._ops, what="reset stage")
    assert names(c) == ["reset", "set_seed", "set_prompt", "start"], c.commands
    print("ok  morph + reset stage changes")

    # 2b. a prompt rejected only by a command_error broadcast fails fast (not after 15 s) and keeps the stream
    for mode in ("hang", "none"):
        MockReactor.broadcast_reject = ("set_prompt", mode)
        t0 = time.monotonic()
        r.change_stage("a scene the mock refuses", 3, False, False)
        await until(lambda: not r._ops, what=f"rejected stage ({mode})")
        assert time.monotonic() - t0 < 2 and "mock moderation" in (r.error or ""), (mode, r.error)
        assert r.state == "ready" and r.stage == 4 and not r.source._cmd_waiters, (r.state, r.stage)
    MockReactor.broadcast_reject = None
    print("ok  command_error broadcast -> fast non-fatal stage error:", r.error)

    # 3. a dropped connection -> reconnect() on the same session -> ready again
    c.drop()
    await until(lambda: c.reconnects == 1 and r.state == "ready", what="recover")
    assert len(MockReactor.instances) == 1
    print("ok  drop -> reconnect -> ready")

    # 4. stop -> disconnect + close (billing ends), state ended
    await r.stop("test")
    assert r.state == "ended" and c.disconnected and c.closed and r.latest is None
    print("ok  stop disconnects")

    # 5. idle: no clients for idle_s -> auto stop
    r.clients = 0
    r.start(None, None, 2, False)
    await until(lambda: r.state == "ready", what="ready again")
    c2 = MockReactor.instances[-1]
    assert c2 is not c
    await until(lambda: r.state == "ended", timeout=8, what="idle stop")
    assert c2.disconnected and c2.closed
    print("ok  idle auto-stop")

    # 6. a rejected command -> failed, and the session is still ended (no orphaned billing)
    MockReactor.fail_command = "start"
    r.clients = 1
    r.start(None, None, 0, False)
    await until(lambda: r.state == "failed", what="failed")
    c3 = MockReactor.instances[-1]
    assert c3.disconnected and c3.closed and "mock rejects start" in r.error
    MockReactor.fail_command = None
    print("ok  command failure -> failed + disconnected:", r.error)

    # 7. drop with an unrecoverable session -> failed without retrying for 30 s
    r.start(None, None, 0, False)
    await until(lambda: r.state == "ready", what="ready 3")
    c4 = MockReactor.instances[-1]
    MockReactor.reconnect_ok = False
    c4.drop()
    await until(lambda: r.state == "failed", what="unrecoverable drop")
    assert c4.reconnects == 1 and c4.closed
    MockReactor.reconnect_ok = True
    print("ok  unrecoverable drop -> failed:", r.error)

    # 8. stall: frames stop while 'connected' -> reconnect cycle -> still nothing -> failed (prime timeout)
    r.start(None, None, 0, False)
    await until(lambda: r.state == "ready", what="ready 4")
    c5 = MockReactor.instances[-1]
    c5.generating = False  # frames stop silently
    await until(lambda: r.state == "failed", timeout=20, what="stall -> failed")
    assert c5.reconnects >= 1 and c5.closed, (c5.reconnects, c5.closed)
    print("ok  stall -> reconnect -> failed:", r.error)

    await r.shutdown()
    await frames_client_after_session()
    await named_scenes()
    print("all relay tests passed")


async def frames_client_after_session() -> None:
    """Regression: a frames client connecting after a session ended (seq > 0, no latest frame) used to spin the
    event loop forever, freezing every endpoint including /orbis/stop and SIGTERM."""
    from aiohttp.test_utils import TestClient, TestServer

    cfg = relay.Config(fake_flag=True)
    cfg.token = ""
    client = TestClient(TestServer(relay.build_app(relay.Relay(cfg), cfg)))
    await client.start_server()
    try:
        assert (await (await client.post("/orbis/start?wait=1", json={})).json())["state"] == "ready"
        await asyncio.sleep(0.3)
        assert (await (await client.post("/orbis/stop")).json())["state"] == "ended"
        ws = await client.ws_connect("/orbis/frames")
        status = await asyncio.wait_for(client.get("/orbis/status"), 2)  # froze here before the fix
        assert (await status.json())["state"] == "ended"
        await client.post("/orbis/start", json={"stage": 1})
        msg = await ws.receive(timeout=8)
        assert msg.type.name == "BINARY" and msg.data[:2] == b"\xff\xd8", msg.type
        await ws.close()
        await client.post("/orbis/stop")
    finally:
        await client.close()
    print("ok  frames client after a session ended: relay stays responsive, gets the next session's frames")


async def named_scenes() -> None:
    """v2 named scenes: optional `scene` on /orbis/start and /orbis/stage, prompts from the scene file when no prompt
    is given, echoed in status as `scene` + `fallback_clip` = <scene>.mp4. The stage flow is unchanged."""
    import json as _json
    import shutil
    import tempfile
    from pathlib import Path

    from aiohttp.test_utils import TestClient, TestServer

    tmp = Path(tempfile.mkdtemp(prefix="orbis-scenes-"))
    book = tmp / "orbis_scenes.json"
    book.write_text(_json.dumps({
        "seed": 777,
        "scenes": [
            {"id": "meadow", "prompt": "A living meadow with a beehive. Static camera.", "seed": 5},
            {"id": "whatif_fire", "prompt": "The meadow on fire. Static camera.", "morph": "Flames spread across the meadow."},
            {"id": "Bad Id", "prompt": "skipped"},
        ],
    }))
    cfg = relay.Config(fake_flag=True)
    cfg.token, cfg.scene_file = "", book
    client = TestClient(TestServer(relay.build_app(relay.Relay(cfg), cfg)))
    await client.start_server()

    async def post(path: str, body: dict, status: int = 200) -> dict:
        resp = await client.post(path, json=body)
        data = await resp.json()
        assert resp.status == status, (path, body, resp.status, data)
        return data

    try:
        s = await post("/orbis/start?wait=1", {"scene": "meadow"})
        assert (s["state"], s["scene"], s["fallback_clip"], s["seed"], s["stage"]) == ("ready", "meadow", "meadow.mp4", 5, 0), s
        assert s["prompt"].startswith("A living meadow"), s["prompt"]
        s = await post("/orbis/stage?wait=1", {"scene": "whatif_fire"})  # morph, stage unchanged
        assert (s["scene"], s["stage"], s["fallback_clip"], s["prompt"]) == ("whatif_fire", 0, "whatif_fire.mp4", "Flames spread across the meadow."), s
        s = await post("/orbis/stage?wait=1", {"scene": "fire_out", "prompt": "The fire goes out and the meadow recovers."})
        assert (s["scene"], s["fallback_clip"]) == ("fire_out", "fire_out.mp4"), s  # passthrough: not in the file
        assert (await (await client.get("/orbis/status")).json())["scene"] == "fire_out"
        await post("/orbis/stage", {"scene": "moon"}, 400)  # unknown scene and no prompt
        await post("/orbis/stage", {"scene": "Bad Id", "prompt": "x"}, 400)  # not a clip-safe id
        await post("/orbis/stage", {"scene": 3}, 400)
        s = await post("/orbis/stage?wait=1", {"stage": 2, "scene": ""})  # stage flow: scene cleared, stage clip
        assert (s["scene"], s["stage"], s["fallback_clip"]) == (None, 2, "stage_2.mp4"), s
        s = await post("/orbis/stage?wait=1", {})  # empty body = next stage, as before
        assert (s["scene"], s["stage"]) == (None, 3), s
        # no stream: a named stage change is remembered (no session opened) and an empty start resumes it
        await post("/orbis/stop", {})
        s = await post("/orbis/stage?wait=1", {"scene": "whatif_fire"})
        assert (s["state"], s["scene"], s["fallback_clip"]) == ("ended", "whatif_fire", "whatif_fire.mp4"), s
        s = await post("/orbis/start?wait=1", {})
        assert (s["state"], s["scene"], s["seed"]) == ("ready", "whatif_fire", 777), s  # file-level seed
        assert s["prompt"] == "The meadow on fire. Static camera.", s["prompt"]
        # the file is re-read when it changes
        await asyncio.sleep(0.05)
        book.write_text(_json.dumps({"scenes": {"moon": {"prompt": "Grey lunar dust, Earth rising. Static camera."}}}))
        os.utime(book, (time.time() + 5, time.time() + 5))
        s = await post("/orbis/stage?wait=1", {"scene": "moon"})
        assert (s["scene"], s["prompt"]) == ("moon", "Grey lunar dust, Earth rising. Static camera."), s
        await post("/orbis/stop", {})
    finally:
        await client.close()
        shutil.rmtree(tmp, ignore_errors=True)
    print("ok  named scenes: start/stage by scene, prompt from the scene file, echoed scene + fallback clip, stage flow intact")


if __name__ == "__main__":
    asyncio.run(main())
