"""Record the window's fallback clips from Visko Orbis (one Reactor session, one fixed seed per scene).

    uv run python record_clips.py --dry-run                  # show the plan and the cost estimate
    uv run python record_clips.py                            # every scene in ../lines/orbis_scenes.json (needs REACTOR_API_KEY)
    uv run python record_clips.py --only whatif_fire,fire_out --seconds 10
    uv run python record_clips.py --fake                     # placeholder clips from the test pattern, no credits
    uv run python record_clips.py --scenes scenes.json       # the old stage clips (moon_0 ... earthrise)

Scenes come from hive/lines/orbis_scenes.json: each needs an id (the protocol.ts SCENES name, also the clip file
name) and a prompt; seed and seconds are optional per scene or for the whole file (defaults: seed 20000, 12 s).

For each scene: reset, set_seed, set_prompt, start, wait for the first frame, let it run N seconds, then
request_clip(N) + download (fragmented MP4) and remux to H.264 MP4 (-c copy when the stream is already H.264).
Every frame is also captured locally, so a failed SDK recording still leaves a clip.
Output: clips/<id>.mp4 (+ clips/raw/ originals, clips/manifest.json). clips/ is git-ignored.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import logging
import os
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Any

from relay import GEN_H, GEN_W, HERE, SCENE_FILE, Config, ReactorSource, load_scene_file, render_test_pattern

log = logging.getLogger("record")

PRICE_PER_MIN = 0.58  # USD while connected (PLAN.md research); check the dashboard for the real rate
CLIP_FPS = 18
OVERHEAD_S = 15  # per scene: first frame + clip readiness + download, roughly
DEFAULT_SEED = 20000
DEFAULT_SECONDS = 12.0


class RawVideoWriter:
    """Pipes raw frames into ffmpeg -> H.264 MP4."""

    def __init__(self, path: Path, width: int, height: int, fps: float, pix_fmt: str) -> None:
        self.path = path
        cmd = [
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", pix_fmt, "-s", f"{width}x{height}", "-framerate", str(fps), "-i", "-",
            "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", str(path),
        ]  # fmt: skip
        self.proc = subprocess.Popen(cmd, stdin=subprocess.PIPE)

    def write(self, data: bytes) -> None:
        assert self.proc.stdin is not None
        self.proc.stdin.write(data)

    def close(self) -> bool:
        try:
            if self.proc.stdin:
                self.proc.stdin.close()
            return self.proc.wait(timeout=120) == 0
        except Exception:
            self.proc.kill()
            return False


class LocalCapture:
    """Backup recording of the frames this client receives (written on the SDK's media thread)."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self.active = True
        self.frames = 0
        self.first_at: float | None = None
        self._writer: RawVideoWriter | None = None
        self._size: tuple[int, int] | None = None
        self._lock = threading.Lock()

    def on_frame(self, bgra: bytes, width: int, height: int) -> None:
        with self._lock:
            if not self.active:
                return
            try:
                if self._writer is None:
                    self._writer, self._size = RawVideoWriter(self.path, width, height, CLIP_FPS, "bgra"), (width, height)
                    self.first_at = time.monotonic()
                if (width, height) == self._size:
                    self._writer.write(bgra)
                    self.frames += 1
            except Exception as exc:
                log.warning("local capture stopped: %r", exc)
                self.active = False

    def stop(self) -> bool:
        with self._lock:
            self.active = False
            writer, self._writer = self._writer, None
        return bool(writer and writer.close() and self.frames)


class RecordingSource(ReactorSource):
    """The relay's Reactor session, with every frame also handed to the current LocalCapture."""

    capture: LocalCapture | None = None

    def _on_frame(self, bgra: bytes, width: int, height: int, frame_id: int, timestamp_us: int, user_data: bytes) -> None:
        capture = self.capture
        if capture is not None:
            capture.on_frame(bgra, width, height)


# ---------------------------------------------------------------- ffmpeg helpers


def probe(path: Path) -> dict[str, Any]:
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
         "stream=codec_name,width,height:format=duration", "-of", "json", str(path)],
        capture_output=True, text=True, timeout=60,
    )  # fmt: skip
    data = json.loads(out.stdout or "{}")
    stream = (data.get("streams") or [{}])[0]
    return {
        "codec": stream.get("codec_name"),
        "width": stream.get("width"),
        "height": stream.get("height"),
        "duration": float((data.get("format") or {}).get("duration") or 0),
    }


def remux(src: Path, dst: Path, reencode: bool) -> str:
    """Video only, faststart MP4. -c copy when it is already H.264; otherwise (or on failure) libx264."""
    codec = probe(src)["codec"]
    tmp = dst.with_name(dst.stem + ".part.mp4")  # rename at the end so readers never see half a file
    attempts = ([] if reencode or codec != "h264" else ["copy"]) + ["x264"]
    for mode in attempts:
        enc = ["-c", "copy"] if mode == "copy" else ["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p"]
        cmd = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(src), "-map", "0:v:0", "-an", *enc,
               "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", str(tmp)]  # fmt: skip
        if subprocess.run(cmd, timeout=600).returncode == 0 and probe(tmp)["duration"] > 0:
            tmp.replace(dst)
            return mode
        log.warning("remux (%s) of %s failed", mode, src.name)
    tmp.unlink(missing_ok=True)
    raise RuntimeError(f"could not remux {src}")


def make_pingpong(src: Path) -> Path:
    """<id>_pingpong.mp4: forward then reversed, so a looping VideoPlayer never jumps."""
    dst = src.with_name(src.stem + "_pingpong.mp4")
    cmd = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(src), "-filter_complex",
           "[0:v]split[a][b];[b]reverse[r];[a][r]concat=n=2:v=1:a=0[v]", "-map", "[v]", "-an",
           "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(dst)]  # fmt: skip
    subprocess.run(cmd, check=True, timeout=600)
    return dst


# ---------------------------------------------------------------- recording


async def record_scene(src: RecordingSource, scene: dict[str, Any], seed: int, seconds: float, out: Path, args) -> dict[str, Any]:
    raw_dir = out / "raw"
    raw_dir.mkdir(parents=True, exist_ok=True)
    local = raw_dir / f"{scene['id']}.local.mp4"
    capture = LocalCapture(local)
    src.capture = capture
    log.info("[%s] seed %d, %.0fs: %s", scene["id"], seed, seconds, scene["prompt"][:80])
    await src.start(scene["prompt"], seed, passthrough=False)

    t0 = time.monotonic()
    while capture.first_at is None:
        if not src.connected:
            raise RuntimeError("session dropped before the first frame")
        if time.monotonic() - t0 > 60:
            raise RuntimeError("no frames within 60 s")
        await asyncio.sleep(0.2)
    log.info("[%s] first frame after %.1fs", scene["id"], capture.first_at - t0)
    await asyncio.sleep(seconds + 1.0)
    src.capture = None
    local_ok = await asyncio.to_thread(capture.stop)
    log.info("[%s] local capture: %d frames (%.1f fps)", scene["id"], capture.frames, capture.frames / (seconds + 1.0))

    dst = out / f"{scene['id']}.mp4"
    try:
        clip = await asyncio.wait_for(src.client.request_clip(seconds), 30)
        reactor_file = raw_dir / f"{scene['id']}.reactor.mp4"
        await asyncio.wait_for(src.client.download(clip, reactor_file, ready_timeout=90), 180)
        mode = await asyncio.to_thread(remux, reactor_file, dst, args.reencode)
        origin = f"reactor ({mode})"
    except Exception as exc:
        if not local_ok:
            raise RuntimeError(f"SDK clip failed ({exc!r}) and the local capture is empty") from exc
        log.warning("[%s] SDK clip failed (%r); using the local capture", scene["id"], exc)
        shutil.copyfile(local, dst)
        origin = "local capture"
    return {"origin": origin}


async def record_live(scenes: list[dict[str, Any]], plan: dict[str, Any], out: Path, args) -> list[dict[str, Any]]:
    cfg = Config()
    total = sum(plan[s["id"]]["seconds"] + OVERHEAD_S for s in scenes)
    cfg.max_s = total * 2 + 120  # server-side backstop: Reactor ends the session even if this script dies
    src = RecordingSource(cfg)
    results = []
    try:
        for scene in scenes:
            entry = {**scene, **plan[scene["id"]]}
            for attempt in (1, 2):
                try:
                    if not src.connected:
                        log.info("connecting to %s ...", cfg.model)
                        await src.connect()
                    entry.update(await record_scene(src, scene, entry["seed"], entry["seconds"], out, args))
                    break
                except Exception as exc:
                    log.error("[%s] attempt %d failed: %r", scene["id"], attempt, exc)
                    entry["error"] = repr(exc)
                    await src.disconnect()  # a fresh session for the retry
            else:
                results.append(entry)
                continue
            entry.pop("error", None)
            results.append(entry)
    finally:
        await src.disconnect()  # billing is per connected second
    return results


def record_fake(scenes: list[dict[str, Any]], plan: dict[str, Any], out: Path) -> list[dict[str, Any]]:
    results = []
    for scene in scenes:
        p = plan[scene["id"]]
        dst = out / f"{scene['id']}.mp4"
        writer = RawVideoWriter(dst, GEN_W, GEN_H, CLIP_FPS, "rgb24")
        t0 = time.time()
        for i in range(int(p["seconds"] * CLIP_FPS)):
            img = render_test_pattern(GEN_W, GEN_H, t0 + i / CLIP_FPS, scene["stage"], scene["id"], i, scene["prompt"])
            writer.write(img.tobytes())
        if not writer.close():
            raise RuntimeError(f"ffmpeg failed for {dst}")
        results.append({**scene, **p, "origin": "fake test pattern"})
    return results


def write_manifest(out: Path, results: list[dict[str, Any]], model: str) -> None:
    path = out / "manifest.json"
    try:
        manifest = json.loads(path.read_text())
    except Exception:
        manifest = {"clips": {}}
    for r in results:
        if r.get("error"):
            continue
        info = probe(out / f"{r['id']}.mp4")
        manifest["clips"][r["id"]] = {
            "scene": r["id"], "stage": r["stage"], "file": f"{r['id']}.mp4", "origin": r["origin"], "seed": r["seed"],
            "seconds": round(info["duration"], 2), "width": info["width"], "height": info["height"],
            "codec": info["codec"], "prompt": r["prompt"], "model": model, "recorded_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        }  # fmt: skip
    path.write_text(json.dumps(manifest, indent=2) + "\n")


def main() -> int:
    parser = argparse.ArgumentParser(description="Record Visko Orbis fallback clips")
    parser.add_argument("--scenes", type=Path, default=SCENE_FILE, help="scene file (default: ../lines/orbis_scenes.json)")
    parser.add_argument("--out", type=Path, default=HERE / "clips")
    parser.add_argument("--only", help="comma-separated scene ids")
    parser.add_argument("--seconds", type=float, help="clip length (default: scenes.json)")
    parser.add_argument("--seed", type=int, help="override every scene's seed")
    parser.add_argument("--fake", action="store_true", help="placeholder clips from the test pattern, no Reactor")
    parser.add_argument("--reencode", action="store_true", help="always re-encode to H.264 instead of -c copy")
    parser.add_argument("--pingpong", action="store_true", help="also write <id>_pingpong.mp4 for seamless looping")
    parser.add_argument("--dry-run", action="store_true", help="print the plan and cost, record nothing")
    parser.add_argument("--yes", action="store_true", help="skip the cost confirmation")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")

    try:
        data = load_scene_file(args.scenes)
    except FileNotFoundError:
        log.error(
            "%s does not exist yet. It holds the window scenes (id, prompt, optional seed) and is written by whoever "
            "owns hive/lines/. Pass --scenes PATH to use another file (e.g. --scenes scenes.json for the old stage clips).",
            args.scenes,
        )
        return 2
    except ValueError as exc:  # includes JSON syntax errors
        log.error("could not read %s: %s", args.scenes, exc)
        return 2
    scenes = data["scenes"]
    for s in scenes:
        if s.get("clip") not in (None, f"{s['id']}.mp4"):
            log.warning("%s: clip %r ignored; clips are always written as %s.mp4 (the relay's fallback_clip)", s["id"], s["clip"], s["id"])
    for i, s in enumerate(scenes):  # stage only colours the fake test pattern and labels the manifest
        s["stage"] = s["stage"] if isinstance(s.get("stage"), int) and not isinstance(s.get("stage"), bool) else i
    if args.only:
        wanted = {s.strip() for s in args.only.split(",") if s.strip()}
        unknown = wanted - {s["id"] for s in scenes}
        if unknown:
            log.error("unknown scene id(s): %s", ", ".join(sorted(unknown)))
            return 2
        scenes = [s for s in scenes if s["id"] in wanted]
    if not scenes:
        log.error("no scenes to record (check %s)", args.scenes)
        return 2
    file_seed = data["seed"] if data["seed"] is not None else DEFAULT_SEED
    file_seconds = data["seconds"] if data["seconds"] is not None else DEFAULT_SECONDS

    def num(value: Any, default: float) -> float:
        return float(value) if isinstance(value, (int, float)) and not isinstance(value, bool) else default

    plan = {
        s["id"]: {
            "seed": args.seed if args.seed is not None else int(num(s.get("seed"), file_seed)),
            "seconds": max(2.0, min(300.0, args.seconds or num(s.get("seconds"), file_seconds))),
        }
        for s in scenes
    }
    est_s = 20 + sum(p["seconds"] + OVERHEAD_S for p in plan.values())
    for s in scenes:
        print(f"  {s['id']:<15} seed {plan[s['id']]['seed']:<6} {plan[s['id']]['seconds']:.0f}s  {s['prompt'][:70]}...")
    if not args.fake:
        print(f"  about {est_s / 60:.1f} min connected, about ${est_s / 60 * PRICE_PER_MIN:.2f} at ${PRICE_PER_MIN}/min")
    if args.dry_run:
        return 0
    for tool in ("ffmpeg", "ffprobe"):
        if not shutil.which(tool):
            log.error("%s is not on PATH", tool)
            return 2
    if not args.fake and not os.environ.get("REACTOR_API_KEY"):
        log.error("REACTOR_API_KEY is not set (use --fake for placeholder clips)")
        return 2
    if not args.fake and not args.yes:
        if not sys.stdin.isatty():  # nobody to ask: never spend credits silently
            log.error("stdin is not a terminal: pass --yes to spend Reactor credits without the prompt")
            return 2
        if input("Spend Reactor credits on this? [y/N] ").strip().lower() != "y":
            return 1

    args.out.mkdir(parents=True, exist_ok=True)
    model = "fake" if args.fake else Config().model
    results = record_fake(scenes, plan, args.out) if args.fake else asyncio.run(record_live(scenes, plan, args.out, args))
    if args.pingpong:
        for r in results:
            if not r.get("error"):
                make_pingpong(args.out / f"{r['id']}.mp4")
    write_manifest(args.out, results, model)
    failed = [r["id"] for r in results if r.get("error")]
    for r in results:
        print(f"  {r['id']:<15} {'FAILED ' + r['error'] if r.get('error') else r['origin'] + ' -> ' + str(args.out / (r['id'] + '.mp4'))}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
