"""Smoke-test a running relay: status, frames over the WebSocket (JPEG check), measured fps.

    uv run python smoke.py                         # watch only (never starts a billed session)
    uv run python smoke.py --start --stage --seconds 15
    uv run python smoke.py --url https://beeswax-venue.drori.dev --seconds 300   # the Sat 11 AM fps gate

--start POSTs /orbis/start first and /orbis/stop at the end. HIVE_TOKEN is read from the environment.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
import time

import aiohttp


async def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--url", default=f"http://127.0.0.1:{os.environ.get('ORBIS_PORT', '8241')}")
    p.add_argument("--seconds", type=float, default=8)
    p.add_argument("--start", action="store_true", help="POST /orbis/start before and /orbis/stop after")
    p.add_argument("--stage", action="store_true", help="POST /orbis/stage (next stage) halfway through")
    args = p.parse_args()
    base = args.url.rstrip("/")
    params = {"token": os.environ["HIVE_TOKEN"]} if os.environ.get("HIVE_TOKEN") else {}
    timeout = aiohttp.ClientTimeout(total=None, sock_connect=10)

    async with aiohttp.ClientSession(timeout=timeout) as http:
        async with http.get(f"{base}/orbis/status", params=params) as r:
            print("status", r.status, await r.text())
            if r.status != 200:
                return 1
        if args.start:
            async with http.post(f"{base}/orbis/start", params=params, json={"stage": 0}) as r:
                print("start", r.status, await r.text())

        ws_url = base.replace("http", "ws", 1) + "/orbis/frames"
        frames, bad, sizes, gaps, staged = 0, 0, [], [], False
        t0 = last = time.monotonic()
        first_at = None
        async with http.ws_connect(ws_url, params={**params, "status": "1"}, heartbeat=20) as ws:
            while (now := time.monotonic()) - t0 < args.seconds:
                if args.stage and not staged and now - t0 > args.seconds / 2:
                    staged = True
                    async with http.post(f"{base}/orbis/stage", params=params, json={}) as r:
                        print("stage", r.status, (await r.json()).get("stage"))
                try:
                    msg = await ws.receive(timeout=max(0.1, args.seconds - (now - t0)))
                except asyncio.TimeoutError:
                    break
                now = time.monotonic()
                if msg.type == aiohttp.WSMsgType.TEXT:
                    s = json.loads(msg.data)
                    print(f"  [{now - t0:5.1f}s] state={s['state']} stage={s['stage']} fps={s['fps']}")
                elif msg.type == aiohttp.WSMsgType.BINARY:
                    data = msg.data
                    ok = data[:2] == b"\xff\xd8" and data[-2:] == b"\xff\xd9"
                    frames += 1
                    bad += not ok
                    sizes.append(len(data))
                    if first_at is None:
                        first_at = now
                        print(f"  first frame after {now - t0:.1f}s: {len(data)} bytes, starts {data[:4].hex()}")
                    else:
                        gaps.append(now - last)
                    last = now
                else:
                    print("  ws closed:", msg.type)
                    break

        if args.start:
            async with http.post(f"{base}/orbis/stop", params=params) as r:
                print("stop", r.status, (await r.json()).get("state"))

    span = (last - first_at) if first_at and frames > 1 else 0
    fps = (frames - 1) / span if span else 0
    print(f"frames={frames} bad_jpeg={bad} fps={fps:.1f} avg_kb={sum(sizes) / max(1, len(sizes)) / 1024:.1f} "
          f"max_gap_ms={max(gaps, default=0) * 1000:.0f}")
    return 0 if frames and not bad else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
