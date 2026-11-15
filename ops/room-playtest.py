"""Record and index the Unity room fixtures with the existing desktop capture player."""

from __future__ import annotations

import argparse
import csv
import html
import json
import os
import re
import shutil
import subprocess
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

SOURCE_FPS = 30
BEAT = re.compile(r"^\[VBS\] beat (\d+) (\w+)(?: \(offline\))?$")
REQUIRED = ["pre", "hook", "meet", "smoker", "feed", "wasps", "heat", "whatif", "escape", "t0", "map", "pre"]
VIEWS = [
    ("Clipboard", "beat 1 hook", -0.6), ("Hive delivery", "fire the reveal", 0.5),
    ("Meet", "beat 2 meet", 4.0), ("Smoker", "[VBS][HandsOn] puff 1 of 3", 0.44),
    ("Lift and feed", "beat 4 feed", 7.0), ("Wasps", "beat 5 wasps", 6.0),
    ("Cooling", "beat 7 heat", 6.0), ("Fire", "window scene whatif_fire", 3.2),
    ("Rain", "fire brigade: rain cloud", 2.3), ("Recovery", "beat 9 escape", 3.2),
    ("Launch", "window scene moon", 0.4), ("Moon", "window scene moon", 5.2),
    ("Crash", "crash ->", 0.65), ("Contract", "[VBS][EndCard] delivered", 3.0),
]


def options() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--player", required=True, type=Path, help="Windows CaptureBuild player")
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--rooms", nargs="+", type=int, choices=range(1, 22), default=list(range(1, 22)))
    parser.add_argument("--jobs", type=int, choices=range(1, 5), default=2)
    parser.add_argument("--every", type=int, choices=(1, 2, 3), default=2, help="frame stride at 30 fps")
    parser.add_argument("--ffmpeg", help="FFmpeg with libx264, or use PATH/imageio-ffmpeg")
    parser.add_argument("--keep-frames", action="store_true")
    parser.add_argument("--silent", action="store_true", help="omit the prerecorded dialogue track")
    return parser.parse_args()


def encoder(explicit: str | None) -> str:
    if explicit or (binary := shutil.which("ffmpeg")):
        return explicit or binary
    from imageio_ffmpeg import get_ffmpeg_exe
    return get_ffmpeg_exe()


def read_events(path: Path) -> list[tuple[int, str]]:
    rows = []
    for line in path.read_text(encoding="utf-8-sig", errors="replace").splitlines():
        number, sep, message = line.partition("\t")
        if sep and number.isdecimal():
            rows.append((int(number), message))
    return rows


def timestamp(seconds: float) -> str:
    millis = round(seconds * 1000)
    minutes, second = divmod(millis, 60_000)
    hour, minute = divmod(minutes, 60)
    return f"{hour:02}:{minute:02}:{second // 1000:02},{second % 1000:03}"


def stage_subtitles(path: Path, beats: list[tuple[int, str]], length: float) -> None:
    blocks = []
    for index, (frame, name) in enumerate(beats):
        start = frame / SOURCE_FPS
        end = min(length, beats[index + 1][0] / SOURCE_FPS if index + 1 < len(beats) else length)
        if end > start:
            blocks.append(f"{index + 1}\n{timestamp(start)} --> {timestamp(end)}\n{name}\n")
    path.write_text("\n".join(blocks), encoding="utf-8")


def voice_inputs(directory: Path, clip_dir: Path, length: float) -> tuple[list[str], list[str]]:
    with (directory / "lines.tsv").open(encoding="utf-8-sig", newline="") as stream:
        lines = [row for row in csv.DictReader(stream, delimiter="\t") if row["kind"] == "start"]
    inputs, filters, tracks = [], [], []
    for index, row in enumerate(lines):
        begin = float(row["seconds"])
        next_begin = float(lines[index + 1]["seconds"]) if index + 1 < len(lines) else length
        duration = min(float(row["clip_seconds"] or 0), next_begin - begin, length - begin)
        clip = clip_dir / (row["id"] + ".mp3")
        if duration <= 0 or not clip.is_file():
            continue
        inputs.extend(["-i", str(clip)])
        track = f"dialogue{index}"
        filters.append(f"[{len(tracks) + 2}:a]atrim=duration={duration:.3f},asetpts=PTS-STARTPTS,adelay={round(begin * 1000)}:all=1[{track}]")
        tracks.append(f"[{track}]")
    if not tracks:
        return [], []
    mix = "".join(tracks) + f"amix=inputs={len(tracks)}:normalize=0,apad[audio]"
    return inputs, ["-filter_complex", ";".join(filters + [mix]), "-map", "[audio]", "-c:a", "aac", "-b:a", "96k"]


def encode(directory: Path, frames: list[Path], every: int, ffmpeg: str, clip_dir: Path | None) -> None:
    length = len(frames) * every / SOURCE_FPS
    inputs, audio = voice_inputs(directory, clip_dir, length) if clip_dir else ([], [])
    temporary = directory / "ride.partial.mp4"
    command = [
        ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-threads", "1", "-filter_complex_threads", "2",
        "-f", "image2pipe", "-framerate", f"{SOURCE_FPS}/{every}", "-vcodec", "png", "-i", "-",
        "-i", str(directory / "stages.srt"), *inputs, "-map", "0:v", "-map", "1:s",
        *audio, "-c:v", "libx264", "-threads", "2", "-preset", "veryfast", "-crf", "24", "-pix_fmt", "yuv420p",
        "-c:s", "mov_text", "-metadata:s:s:0", "title=Ride stages", "-movflags", "+faststart",
        "-t", f"{length:.3f}", str(temporary),
    ]
    with (directory / "encoder.log").open("wb") as output:
        with subprocess.Popen(command, stdin=subprocess.PIPE, stdout=output, stderr=output) as process:
            assert process.stdin is not None
            try:
                for frame in frames:
                    with frame.open("rb") as source:
                        shutil.copyfileobj(source, process.stdin)
                process.stdin.close()
            except BrokenPipeError:
                process.wait()
                raise RuntimeError(f"encoder stopped early, see {directory / 'encoder.log'}") from None
            if process.wait() != 0:
                raise RuntimeError(f"encoder failed, see {directory / 'encoder.log'}")
    temporary.replace(directory / "ride.mp4")


def storyboards(directory: Path, frames: list[Path], events: list[tuple[int, str]], every: int) -> None:
    from PIL import Image, ImageDraw

    samples = []
    for label, needle, offset in VIEWS:
        hit = next((frame for frame, line in events if needle in line), None)
        if hit is None and label == "Hive delivery":
            hit = next((frame for frame, line in events if "beat 1 hook" in line), None)
            offset = 6.0
        if hit is not None:
            index = min(len(frames) - 1, max(0, round((hit + offset * SOURCE_FPS) / every)))
            samples.append((label, index))
    for first in range(0, len(samples), 7):
        strip = samples[first : first + 7]
        sheet = Image.new("RGB", (960, 540 + 72), "#182020")
        draw = ImageDraw.Draw(sheet)
        for cell, (label, index) in enumerate(strip):
            x, y = (cell % 3) * 320, (cell // 3) * 204
            with Image.open(frames[index]) as source:
                sheet.paste(source.resize((320, 180), Image.Resampling.LANCZOS).convert("RGB"), (x, y))
            draw.text((x + 7, y + 184), f"{label}  {index * every / SOURCE_FPS:.1f}s", fill="white")
        sheet.save(directory / f"review-{first // 7 + 1}.jpg", quality=87)


def room(number: int, args: argparse.Namespace, ffmpeg: str, clip_dir: Path | None) -> dict:
    directory = args.out / f"room-{number:02}"
    directory.mkdir()
    env = {**os.environ,
           "VBS_CAPTURE_OUT": str(directory), "VBS_CAPTURE_ROOM": f"{number:02}",
           "VBS_CAPTURE_VIEWS": "pov", "VBS_CAPTURE_MAX_SECONDS": "390",
           "VBS_CAPTURE_EVERY": str(args.every), "VBS_AUTOPILOT_HANDSON": "1",
           "VBS_AUTOPILOT_MISTAKES": "feed"}
    result = {"room": number, "ok": False}
    try:
        with (directory / "process.log").open("wb") as output:
            process = subprocess.run(
                [str(args.player), "-batchmode", "-force-d3d11", "-logFile", str(directory / "player.log")],
                env=env, stdout=output, stderr=output, timeout=600,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        frames = sorted((directory / "pov").glob("f_*.png"))
        if not frames or any(int(frame.stem[2:]) != i * args.every for i, frame in enumerate(frames)):
            raise RuntimeError("no frames or a gap in the recording; source frames retained")
        events = read_events(directory / "events.tsv")
        header = next((message for _, message in events if message.startswith("[VBSRoomTest] room=")), "")
        name = re.search(r" name=(.+?) size=", header)
        if name:
            result["name"] = name.group(1)
        beats = [(frame, match.group(2)) for frame, message in events if (match := BEAT.match(message))]
        last = (len(frames) - 1) * args.every
        captured = [(frame, name) for frame, name in beats if frame <= last + args.every]
        names = [name for _, name in captured]
        ordered = iter(names)
        full_ride = all(name in ordered for name in REQUIRED) and len({frame for frame, _ in captured}) >= len(REQUIRED)
        length = len(frames) * args.every / SOURCE_FPS
        hand_setups = [message for _, message in events if "done by hand" in message]
        completed = {message.rsplit("autopilot ", 1)[-1].split(":", 1)[0] for message in hand_setups}
        full_hands = {"smoker", "feed", "wasps", "heat"} <= completed
        result.update(
            ok=process.returncode == 0 and full_ride and full_hands,
            returncode=process.returncode,
            seconds=round(length, 2),
            frames=len(frames),
            sampled_fps=SOURCE_FPS / args.every,
            recorded_beats=names,
            hand_setups=hand_setups,
            hand_setups_complete=full_hands,
            room_audit=next((message for _, message in reversed(events) if message.startswith("[VBS][RoomAudit] SUMMARY")), ""),
        )
        stage_subtitles(directory / "stages.srt", captured, length)
        storyboards(directory, frames, events, args.every)
        encode(directory, frames, args.every, ffmpeg, clip_dir)
        if not args.keep_frames:
            for frame in frames:
                frame.unlink()
            (directory / "pov").rmdir()
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as error:
        result["ok"] = False
        result["error"] = str(error)
    (directory / "result.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"Room {number:02}: {'PASS' if result['ok'] else 'CHECK'}", flush=True)
    return result


def write_index(output: Path, results: list[dict], silent: bool) -> None:
    cards = []
    for result in results:
        number = result["room"]
        root = f"room-{number:02}"
        title = html.escape(result.get("name") or f"Room {number:02}")
        if (output / root / "ride.mp4").exists():
            media = f'<video controls preload="none" src="{root}/ride.mp4"></video>'
            media += f'<p><a href="{root}/review-1.jpg">Opening and repairs</a> · <a href="{root}/review-2.jpg">Fire and ending</a></p>'
        else:
            media = ""
        status = "Completed" if result["ok"] else "Needs review"
        detail = html.escape(result.get("error", ""))
        cards.append(f'<section><h2>{title} · {status}</h2>{media}<p>{detail}</p><a href="{root}/events.tsv">Timeline</a></section>')
    audio = "Silent capture." if silent else "Dialogue track reconstructed from the recorded line timestamps. Effects are listed in the timeline."
    (output / "index.html").write_text(
        '<!doctype html><meta charset="utf-8"><title>HONEYMOON room playtest</title>'
        '<style>body{font:16px system-ui;margin:2rem;background:#f7f5ec;color:#202820}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(330px,1fr));gap:1.5rem}section{padding:1rem;background:#fff}video{width:100%;aspect-ratio:16/9;background:#202020}a{color:#235842}</style>'
        f'<h1>HONEYMOON room playtest</h1><p>Desktop simulation. {audio}</p><main>{"".join(cards)}</main>',
        encoding="utf-8",
    )


def main() -> int:
    args = options()
    args.player, args.out = args.player.resolve(strict=True), args.out.resolve()
    numbers = sorted(set(args.rooms))
    args.out.mkdir(parents=True, exist_ok=True)
    existing = [number for number in numbers if (args.out / f"room-{number:02}").exists()]
    if existing:
        raise SystemExit(f"Use a fresh output folder or choose unused room IDs: {existing}")
    ffmpeg = encoder(args.ffmpeg)
    root = Path(__file__).resolve().parents[1]
    clips = root / "unity/VirtualBeeSwarm/Assets/Resources/BeeswaxLines"
    with ThreadPoolExecutor(max_workers=args.jobs) as pool:
        results = list(pool.map(lambda number: room(number, args, ffmpeg, None if args.silent else clips), numbers))
    all_results = [json.loads(path.read_text(encoding="utf-8")) for path in sorted(args.out.glob("room-*/result.json"))]
    (args.out / "results.json").write_text(json.dumps(all_results, indent=2) + "\n", encoding="utf-8")
    write_index(args.out, all_results, args.silent)
    return 0 if all(result["ok"] for result in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
