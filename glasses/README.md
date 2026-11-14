# Beeswax in your ear: the Mentra Live miniapp

The glasses half of HONEYMOON. Mentra Live has no display, so everything happens by ear:
the scout presses the button, Beeswax says "hold still", the glasses take a photo and
listen to what the scout says, the Hive judges it, and the verdict plays in the scout's ear. The miniapp
also plays ear asides from the Hive bus (hornet warning, "they're on your flower") and
sends the scout's plea to the Hive during the plea beat.

It's a **Mentra miniapp** (`@mentra/miniapp` **3.3.0**, pinned for Mentra App 3.3 on iPhone)
that runs on the phone inside the Mentra App. The old cloud SDK (`@mentra/sdk`, AppServer,
ngrok, console API keys) is retired; don't follow tutorials that use it.

| On the glasses | Does |
|---|---|
| Button, short press | Scan (ignored while a scan is in flight) |
| Button, long press | Replay the last line |
| Touch bar double tap or long press | Replay the last line (if the system keeps the button's long press) |
| LED | Orange blink while scanning, green = good, red = bad/dangerous, blue = roast |

## Setup (once per laptop)

```bash
cd glasses
bun install
cp src/config.local.example.ts src/config.local.ts   # `bun run build` / `bun dev` do this for you
```

Put the `HIVE_TOKEN` (from the Hive's `.env`, ask Rome) in `src/config.local.ts` as
`hiveToken`. Git ignores that file; never commit the token. Note that the token is bundled into
the miniapp, which is fine for a LAN install but means this build must never be `pack`ed and
hosted on a public URL.

The Hive hosts are tried in order: `https://beeswax-venue.drori.dev` (Rome's laptop, primary on
demo day), then `https://beeswax.drori.dev` (pop-os, always on: use it tonight). On a fallback
host the miniapp checks the primary every 30 s and moves back when it answers, so the glasses
end up on the same Hive as the Quest. To force one host, set `hosts` in `config.local.ts`.

## Glasses and iPhone settings

- **Mentra App → glasses settings → button mode = apps**, not camera. In camera mode the
  system takes its own photo and the miniapp never sees the press.
- **Ear audio follows the iPhone's media route.** Mentra Live must be the iPhone's Bluetooth
  audio output, or the lines play from the phone speaker.
- Update the glasses software on a strong network before the event (~150-640 MB).
- During judging, keep the Mentra App open and the iPhone's auto-lock off.

## Dev loop (laptop + iPhone)

1. Put the laptop and the iPhone on **the same network**: the iPhone's hotspot works.
2. `bun dev` in `glasses/`. It builds, serves on the LAN, and prints a QR code.
3. On the iPhone: **Mentra App → Settings → Miniapp Developer Settings → Scan Miniapp QR Code**.
4. Every save rebuilds and reloads on the phone. `console.log` from the background shows up in
   the `bun dev` terminal (lines start with `[beeswax]`).

Open the **Beeswax** tile in the Mentra App for the status page: Hive connection and host, beat,
glasses and cloud state, last line, latest latency per hop, and a log. **Test scan** runs the
same flow as a button press (handy to test the Hive without the glasses); **Replay** replays.

## Release for judging (no laptop needed)

1. Bump `version` in `miniapp.json` (the Mentra App refuses an older version than the installed one).
2. `bun run release` (runs `mentra-miniapp release`): builds, packs, and serves a
   `miniapp://release` QR on the LAN.
3. Scan it from the same Miniapp Developer Settings screen. The install persists and runs offline.

Do the release after `bun run lines` has copied the Hive's prerendered ear lines in (see Ear lines).

## How a scan works

```
press ─► "ear_hold_still" clip (embedded, no network) + LED orange
      ─► POST /glasses/press                      not allowed → play its line_id (ear_not_now)
      ─► takePhoto (low, compress medium)  ┐      in a scan beat the photo starts at the press,
      ─► listen (from 1.5 s before press)  ┘      without waiting for the ack (photoBeforeAck)
      ─► POST /glasses/scan {scan_id, photo_url, heard, pressed_at}
         as soon as the photo is in and nobody is mid-sentence (at most 3 s of listening);
         if the photo failed, it waits for words instead
      ─► verdict: embedded clip by line_id → else audio_url → else Mentra TTS of the caption
4 s after the press with no verdict yet: "ear_hold_still_2". After 25 s the scan gives up.
```

Every hop is timed with `Date.now()` and sent to `POST /glasses/latency`: `press_ack`, `photo`,
`scan_rtt`, `press_to_verdict`, `press_to_audio`.

**Scan gate fallback (speech-first):** set `speechFirst: true` in `config.local.ts`. The scan
POSTs what the judge said as soon as they finish a sentence (or after 3 s), plays that verdict,
then POSTs the photo alone with the same `scan_id` so the Quest polaroid still develops. The
Hive only develops that photo (no second verdict or beeline); checked against the real Hive.

**Plea:** during beat `plea` (and 4 s after it), every final transcript goes to
`POST /glasses/plea`, or over the bus if HTTP fails.

## Ear lines

`hive/scripts/prerender.ts` (paid, needs the ElevenLabs key; run it on the Hive host) writes
`hive/out/glasses/lines.generated.ts`: `SAMPLE_RATE` and `LINE_PCM16: Record<line_id, base64>`,
PCM16 little-endian mono. Copy it in with `bun run lines`, then rebuild/release.
`src/background/lines.ts` loads it (or the placeholder's older `{pcm, ms, caption}` shape) and takes
every caption from the Hive's catalog `hive/lines/lines.json`, so a line the Hive names by id alone
(`ear_practice`, `ear_not_now`, ...) is still spoken by Mentra TTS when it isn't embedded.
Until the first prerender the file holds two beep placeholders (`ear_hold_still`, `ear_hold_still_2`);
every other line falls back to its `audio_url`, then to Mentra TTS of the caption (a different
voice: the last resort). `ear_no_signal` and `ear_scan_failed` are glasses-only and not in the
catalog yet, so they are always TTS.

## Config knobs (`src/config.local.ts`)

| Key | Default | Notes |
|---|---|---|
| `hosts` | venue, then pop-os | Most preferred first |
| `photo` | `low`, compress `medium`, no shutter sound, 10 s timeout | Bigger photos cross BLE slower |
| `warmCamera` | `true` | Pre-warms the camera when a scan beat starts |
| `photoBeforeAck` | `true` | Start the photo at the press in a known scan beat |
| `speechFirst` | `false` | The scan-gate fallback above |
| `heardWindowMs` / `heardLookbackMs` | 3000 / 1500 | Listening window around the press |
| `stallMs` | 4000 | When "ear_hold_still_2" plays |
| `pressTimeoutMs` / `scanTimeoutMs` / `giveUpMs` | 2500 / 12000 / 25000 | Per host try; whole scan |

## Troubleshooting

- **Photos take 3.5-13.7 s.** That's known Mentra behaviour (worst over BLE). Watch the `photo`
  hop on the status page. Levers: glasses on Wi-Fi, `photo.compress: "high"`, `speechFirst: true`.
  If photos start failing, try `warmCamera: false`.
- **No transcription, or TTS fallback lines are silent:** both need the MentraOS cloud. The status
  page's **Cloud** row should say `connected`; check the phone's network first.
- **Hive row red/connecting:** the log says why. `401` = wrong `hiveToken`. Cloudflare `502/530`
  = that host's Hive or tunnel is down; the miniapp moves to the next host by itself.
- **Every press says "not now":** the Hive thinks the beat isn't a scan beat (roses, beeline,
  offlist, field). Check the beat on the status page and the Beat Board.
- **Nothing happens on a press:** button mode isn't `apps`, or the glasses aren't connected (status
  page **Glasses** row). Use **Test scan** to check the rest of the chain.
- **Lines play from the phone, not the glasses:** fix the iPhone's Bluetooth audio route.
- **Dev build won't load:** phone and laptop must share a network; `bun dev` reprints the QR when
  the laptop's IP changes. A release install of the same package must be uninstalled first only
  if it was signed (ours aren't).

## Checks

```bash
bun run typecheck   # tsc, including the shared contract in ../hive/src/protocol.ts
bun test            # flow tests against a local fake Hive (real hive bus code), no network or audio
bun run build       # both layers into dist/ (also run by dev/release/pack)
```

## Files

- `src/background/index.ts`: registers the miniapp; `app.ts` wires everything to the session.
- `scan.ts` (button scan), `ear.ts` (line queue and fallbacks), `hive.ts` (HTTPS with failover,
  wss bus with reconnect), `transcript.ts`, `led.ts`, `config.ts`, `protocol.ts` (re-exports
  `hive/src/protocol.ts`).
- `src/ui/`: the status page. `src/shared/channels.ts`: background ↔ UI messages.
