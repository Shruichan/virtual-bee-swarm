# Virtual Bee Swarm (Unity project)

The Quest 3 / 3S mixed-reality app for HONEYMOON. A worker bee brings a clipboard. Sign it to start the hive delivery,
meet Beeswax, and take on four jobs before the rocket leaves for the Moon. The generated Unity scene combines Meta
passthrough and hand tracking with the meadow, authored art, and the Hive client for voice and the future window.

- [Gameplay flow, systems, and art slots](Assets/VirtualBeeSwarm/README-gameplay.md)
- Hive client (token, hosts, voice, window clips): [`../BeeswaxClient/README.md`](../BeeswaxClient/README.md)

**`VirtualBeeSwarm.unity` is a build output** (`PLAN.md` house rules). It's git-ignored, the scene builder
(`Editor/BuildGameScene.cs`) regenerates it, and nobody edits it by hand. Change the builder, the tuning asset or the
art prefabs instead. Only its `.meta` is in git: the builder keeps that GUID, so Build Settings never change.

## Open it

1. Unity **6000.2.9f1** with **Android Build Support** (OpenJDK, Android SDK & NDK). The project is saved with
   6000.2.9f1. 6000.3 LTS also opens it, but it upgrades the project and reimports everything. Only switch if the
   whole team switches, and don't commit `ProjectSettings/ProjectVersion.txt` otherwise.
2. Keep `unity/BeeswaxClient` next to this folder. `Packages/manifest.json` loads it as `file:../../BeeswaxClient`.
3. Unity Hub → Add → `unity/VirtualBeeSwarm`. The first import resolves the Meta packages and the vendored
   NativeWebSocket client.
4. The editor generates the scene on first open if it is missing. After pulling changes, run **Virtual Bee Swarm →
   Build Scene** to refresh it, then press Play. The desktop camera shows the floor meadow without a headset.
   Press **F2** to run the show.
5. **Token** (to talk to the Hive): ask Rome for `HIVE_TOKEN`, then put it alone on one line in
   `Assets/Resources/beeswax_token.txt`. That file is git-ignored (this repo is public), so never `git add -f` it.
   Without it everything still works offline: local lines, timers and fallbacks. In the editor the `HIVE_TOKEN`
   environment variable also works.

Hosts are tried in order: `wss://beeswax-venue.drori.dev` (the venue laptop on demo day), then
`wss://beeswax.drori.dev`. They're set on `Beeswax (client)` → `BeeswaxBus.hosts`. For a local Hive, put
`ws://<laptop-LAN-IP>:<port>` first.

## What's in the scene

| Object | What |
|---|---|
| `OVRCameraRig` | Copied from the rig template `Scenes/RigTemplate.unity`: passthrough underlay, mic and scene (USE_SCENE) permission requests at startup (v3.1 V1), boundary per the toggle (off by default), floor-level origin, an `OVRHand` + `OVRSkeleton` per hand (no hand mesh, so the real hands show). |
| `Beeswax (client)` | `BeeswaxBus`, `BeeswaxStations`, `HumDetector`, `BeeswaxVoiceInput`, `LatencyReporter`. No token in the scene. |
| `GameRig/Systems` | `GameNet` (the only code that calls the client), `GameDirector` (the beat clock), debug HUD, autopilot, `Beats/` (intro, hornet, what-if, escape, T-0, map). |
| `GameRig/TableAnchor` | Placement anchor for the hive, stations, Beeswax, rocket, and ending props. `MeadowLayout` scales it and sets it on the floor. `ArRig` can mount the window on a real wall. |
| `GameRig/Input` | `OvrHandPoseSource` (hands + Touch controllers), `XRControllerHandSource` (fallback), `OvrPassthroughDimmer` (Moon), pointers, fingertips. |
| `GameRig/DesktopCamera`, `DesktopOnly` | Desktop testing only. They switch themselves off in the headset. |

`VirtualBeeSwarm.unity` is the **only scene in Build Settings**. `RigTemplate.unity` (tracked) stays in the project only
as the rig template. To change rig settings, edit it there, then rebuild.

**Rebuilding the scene:** menu **Virtual Bee Swarm → Build Scene**, or headless
`-executeMethod VirtualBeeSwarm.Editor.BuildGameScene.Build`. `GameBuild.BuildApk` runs the builder first on every
build, and the editor builds the scene by itself when it opens the project without it. The menu asks before it
replaces an existing scene (batch mode never asks); the old one is copied to `Library/SceneBackups/`. Prefabs,
materials and meshes are kept.

## In the headset

- Start in an open patch of floor. The hive rig places itself in front of you and the meadow fills the room. To move
  the rig before signing, aim at clear floor and pinch. The table layout is available with `meadow.rig=false`.
- Poke **SIGN HERE** when the worker bee brings it. A fingertip poke, a hand ray and pinch, or a controller trigger
  activates a ticket. Pinch to grab a prop, or use the ray and pinch.
- Use **ORDER UP** to order a smoker and pump it with the honey-dipper lever. Lift the hive and pour syrup into the
  feeder. Fit the entrance reducer, then set down the pebble dish and pull the lever to fill it.
- The hive makes a sound when you lift it. With a Touch controller, the hand lifting it gets one short pulse.
  The pebble dish gives a little dust and a thunk when it snaps into place.
- Hold a pinch to ask the future window a **what if**, or use B/Y on a controller. Ask about a fire, then say
  **put it out**. Beeswax demonstrates and clears the fire if the player stays quiet, including when the Hive is down.
- After the hive is saved, pull the lever to launch the rocket. The Moon appears at liftoff. The map and final
  contract show after landing.

## Desktop keys (editor, no headset)

| Key | Does |
|---|---|
| N or Space | next beat (START in pre) |
| B | previous beat |
| Shift+R | reset to pre |
| 1 / 2 / 3 | order a station choice (or click a ticket) |
| X / Y | carry a prop through its setup / pull the honey-dipper lever |
| G (hold) | lift/tip the hive (feed station), or drag it with the mouse |
| C | place the rig in front of the camera |
| H (hold) | fist (hornet ball) |
| U (hold) | hum (heats the hornet ball) |
| T (hold) | push-to-talk (needs the Hive); offline in the what-if beat, a press + release asks |
| F / P | what-if: "what if a fire starts?" / "put it out!" (offline/operator backup) |
| K | hornet: auto / always / never |
| O | force offline on/off |
| F2 | autopilot plays the show (K enables the optional hornet) |
| F1 | debug HUD |
| Right mouse + WASD, Q/E | look and move |

## Build to the Quest

One-time setup:
1. Put the headset in developer mode: Meta Horizon phone app → the headset → Headset settings → Developer mode.
2. Switch the platform: **File → Build Profiles → Android → Switch Platform**. The first time reimports everything,
   which takes several minutes. On pop-os that's copy A (`~/immerse-the-bay/unity-work/A/VirtualBeeSwarm`). Never switch
   copies B or C.
3. Run **Virtual Bee Swarm → Bootstrap Project Settings** twice. The first run applies the Android settings and the
   second must change nothing. The last line has to say `S1.10 check: PASS`. That check covers:
   - one passthrough provider (Meta XR feature + Meta Quest Support; of the Unity OpenXR: Meta features only Session and Occlusion are on)
   - multiview, IL2CPP ARM64, ASTC, min API 32, Vulkan
   - `USE_SCENE` (Scene Support Required, v3.1 V1), no MRUK scene loading, the boundary toggle, NativeWebSocket vendored

   **Meta XR → Project Setup Tool** should then show only the XR Simulator item open.
4. Get the shared **debug keystore** and the **Hive token** from Rome (S1.7). Both stay outside the repo. Set the env
   vars `HIVE_TOKEN` and `VBS_KEYSTORE_PASS`, plus `VBS_KEYSTORE` if the keystore isn't at
   `~/immerse-the-bay/keys/vbs-debug.keystore`, then restart the editor/Hub so it sees them.

Wireless adb (USB once, then Wi-Fi):
```bash
adb devices                          # USB plugged in: accept "Allow USB debugging" in the headset
adb tcpip 5555
adb shell ip -f inet addr show wlan0 # the headset's IP (or Settings → Wi-Fi → your network)
adb connect <quest-ip>:5555          # now unplug; `adb devices` shows <quest-ip>:5555
```

Build (S1.14):
- Menu **Virtual Bee Swarm → Build Quest APK (D, development)** or **(R, release)**. Headless:
  `HIVE_TOKEN=… VBS_KEYSTORE_PASS=… VBS_BUILD_TYPE=D Unity -batchmode -nographics -quit -projectPath . -buildTarget Android -executeMethod VirtualBeeSwarm.Editor.GameBuild.BuildApk -logFile apk.log`
- On pop-os, run `~/immerse-the-bay/unity-work/A/build-apk.sh D`. It reads the token and the keystore settings itself
  and writes `~/immerse-the-bay/unity-work/builds/VirtualBeeSwarm-D-<n>-<date>.apk` plus `<apk>.manifest.txt`.
- Every build does these steps:
  1. runs the scene builder, refuses MRUK and requires the OVRManager scene-permission request
  2. injects `HIVE_TOKEN` into `Assets/Resources/beeswax_token.txt` and deletes it afterwards (no token, no build)
  3. signs with the shared keystore
  4. checks the APK with aapt2: RECORD_AUDIO, INTERNET, cleartext, hand tracking, PASSTHROUGH,
     USE_SCENE and USE_ANCHOR_API must be there
- D = development build (`Debug.isDebugBuild` is true, so the self-test can switch on; never quote fps from it). R =
  release. Without git, the APK's name carries the date-time.

Install and run (laptop, Quest on USB or wireless adb):
```bash
ops/quest-deploy.sh VirtualBeeSwarm-D-4-20261114-0630.apk         # install -r -g, logcat -c, launch, logcat -> ~/immerse-the-bay/quest-logs/
ops/quest-deploy.sh -t 120 -o boot.logcat.txt <apk>               # stop logging after 2 min
```
If the installed copy was signed with another key (INSTALL_FAILED_UPDATE_INCOMPATIBLE), it uninstalls the app and
tries again. Its terminal output shows only `[VBS]`/`[Beeswax]` lines, errors and crashes. The full logcat goes to
the file.

The APKs carry the Hive token, so never post them anywhere public. Don't enroll the demo Quest in ManageXR (`PLAN.md`, Meta bug MAN-54). Keep a sideload APK of every build that works.

## Where art goes

The rules are in `PLAN.md` (FBX in meters, Y-up, ≤1024 px textures, tri budgets).
- Models go in `Assets/VirtualBeeSwarm/Art/<Name>/`, and prefabs made from them go in `Assets/VirtualBeeSwarm/Prefabs/`.
- FBX imports as is. **GLB needs the glTFast package** (`com.unity.cloud.gltfast`), which isn't installed, so ask
  for FBX or add the package.
- Put art swaps in `Art/<Slot>/<Slot>.prefab`. The builder loads authored slots and uses primitives for missing art.
  Keep the named transforms and component fields below.

| Art slot | Keep / wire |
|---|---|
| `TableAnchor/Hive/LiftRoot/Body, Roof, Stand` | `HiveBody.bodyRenderers` (tinted when sick), `liftRoot`, `Door` (scale.x = door width), `DoorPoint`, `SwarmCenter`, `HeatGlow` |
| `.../Hexagons/Hex1-4` | `HexagonPanel.hexagons` (smoker, feed, wasps, heat) |
| `Beeswax/Body/*` (faces +Z) | `BeeswaxController.mouth` (scaled on Y), `leftWing`/`rightWing` (rotated on Z), `dustyRenderers`. The AudioSource + BeeswaxRoomVoice stay on `Beeswax`. |
| `Swarm` | The worker rig's VAT bake supplies the nearby animation and the static mesh shared by the meadow and swarms. |
| `HONEYMOON-1/*` | `Seat`, `Perch`, `Flame`, `CountdownDisplay` |
| `FutureWindow/Portal/Frame*`, `Glow` | `LivePane (Orbis)` + `FallbackPane` (same size), `Embers` (+Z out of the window) |
| Station props (`Smoker`, `HoneyJar`, `SyrupBottle`, `Wasp0-3`, `DeepBowl`, `PebbleDish`, `Drinker0-4`) | the references on each station component |
| `Hornet`, `Thermometer` | `HornetBeat` refs (`FillPivot` scales on Y) |
| `ScoreCard`, `LevelMap` (7 tiles), `MoonScene` (Moon, Earth) | `EscapeSequence.scoreText`, `LevelMap.tiles`, `T0Sequence.moon/earth` |

Materials: keep them unlit and shadowless (URP/Unlit or `VirtualBeeSwarm/Unlit`). The tinted parts (hive body, Beeswax)
use a `_BaseColor`/`_Color` property block.
- **Beeswax's voice:** the prerendered mp3s live in `Assets/Resources/BeeswaxLines/<line_id>.mp3` with the render
  manifest as `_manifest.json`. Until a clip exists, the caption shows instead. After a re-render (`hive/out/unity`):
  **Virtual Bee Swarm > Voice > Import clips from hive/out**, then **Regenerate GameLines** if `lines.json` changed, then
  **Check**. Batch, from the project folder (`VBS_LINES_DIR` / `VBS_VOICE_OUT` when the project isn't at
  `unity/VirtualBeeSwarm` in the repo):
  `Unity -batchmode -nographics -quit -projectPath . -executeMethod VirtualBeeSwarm.Editor.BeeswaxVoiceTools.Check -logFile voice-check.log`
  (`.Import`, `.Generate`; exit code 0 = no errors; `VBS_STRICT_CLIPS=1` also fails on missing, orphan or stale clips).
  Import settings are set by `BeeswaxLinesImporter` (mono, Vorbis 0.5, Decompress On Load, preload, 22050 Hz on
  Android); don't change them by hand.
- **Meadow window fallback clips:** put `<scene>.mp4` in `Assets/StreamingAssets/` (720p H.264, small). The scene
  names are in the client README.

## Autopilot and smoke test

- **F2** in Play mode runs from SIGN HERE through the stations, wildfire, rescue, launch, and final contract.
  Set `VBS_AUTOPILOT_HANDSON=1` before launch to exercise the prop setup and lever in the desktop run.
- Headless smoke test (silent: audio is switched off for that build only). It needs the active target to be desktop;
  once the project is on Android, use F2 instead:

```bash
UNITY=~/Unity/Hub/Editor/6000.2.9f1/Editor/Unity
$UNITY -batchmode -nographics -quit -projectPath . -executeMethod VirtualBeeSwarm.Editor.GameSmokeBuild.Build -logFile smoke-build.log
xvfb-run -a Builds/Smoke/vbs-smoke.x86_64 -batchmode -nographics -logFile $PWD/smoke-run.log; echo "exit $?"
grep VBSAutopilot smoke-run.log       # ... PASS, exit 0
```
`SMOKE_ONLINE=1` runs it against the Hive (no mic; the token comes from the token file). Other options are listed
in the comment at the top of `Editor/GameSmokeBuild.cs`.

**Earlier build-machine checks (2026-11-14, pop-os):**
- Batch compile: 0 errors, 0 warnings. All VirtualBeeSwarm assemblies compiled: runtime, Editor, Meta, Meta.Editor,
  Setup.Editor and Tests. All five game assemblies also compile with `UNITY_ANDROID` (csc, Android defines).
- Scene build: 643 components, 0 missing scripts, 10/10 beats, 1 OVRCameraRig, hands wired. The scene keeps GUID
  `ed6503827dca69c6db76a5f955666566`; `ProjectSettings/` is unchanged by a rebuild.
- Opening the project without the scene (as after a fresh clone) builds it automatically, with the same GUID.
- Offline smoke: 2 runs at 4x, PASS ("Hive saved in 3:07").
- Online smoke against `wss://beeswax.drori.dev` (before the rename, 02:30 PT): PASS. The Hive recorded all four
  stations solved and "Hive saved in 2:17".
- Voice import (S8.2, ~05:55 PT): 77 clips imported, import settings OK on 77/77 (batch Check), two-way id check
  clean against `lines.json` (98 ids), offline smoke with `SMOKE_REQUIRE_CLIPS=1`: PASS, 62 lines started, all 62 with
  a preloaded clip, 0 caption-only.
- Android (S1.10 + S1.14, ~06:25 PT, pop-os copy A): the bootstrap ran twice, the second run changed no file, and the
  S1.10 check passes. The cold IL2CPP D build had 0 script errors and 0 script warnings. The aapt2 check passed, and
  the signer is the shared debug key (SHA-256 `39:5E:44:51:…:CE:87:5D`).

## Room recordings

Build a Windows capture player from Unity 6000.2.9f1 with
`-batchmode -quit -force-d3d11 -buildTarget StandaloneWindows64 -executeMethod VirtualBeeSwarm.Capture.Editor.CaptureBuild.Build`.
Set `CAPTURE_OUT` if you want the player outside the project. The default output is `Builds/Capture`.

From the repo root, use Python with Pillow and FFmpeg with H.264 support. The runner accepts FFmpeg on PATH,
`--ffmpeg`, or the binary bundled with `imageio-ffmpeg`.

```powershell
py ops/room-playtest.py --player "C:\path\to\vbs-capture.exe" --out "C:\path\to\room-review"
```

Open `index.html` in the output folder. It links each room's video, review frames, and event timeline. Use
`--rooms 4 9 20` to repeat selected cases in a fresh output folder. The desktop run uses furniture volumes for
placement and sightline checks. Review the video even when the script completes all four hand actions.
The starting gaze can hide controls, and a partial floor fit can intersect furniture. Dialogue is reconstructed
from the captured timestamps. Quest tracking, passthrough, physical reach, and headset frame time still need a device run.

## Known gaps

- The current floor-first layout still needs headset checks for placement, hand and controller reach, passthrough,
  push-to-talk, video playback, and frame rate. The desktop smoke test uses simulated inputs.
- Build warnings that matter on the Quest:
  - Unity: "Active Input Handling is set to Both, this is unsupported on Android and might cause issues with input and
    application performance". Both is deliberate (OVRInput and the Input System), but if input acts up on device,
    this is the first suspect.
  - D builds only: Meta's XR Operator (DevAgent) implicit OpenXR API layer ships with
    `FOREGROUND_SERVICE_MEDIA_PROJECTION` and `com.oculus.experimental.enabled` (not required). Meta's own build hook
    leaves it out of R builds.
- Meta's DevAgent build hook writes the build machine's LAN IP and an agent-bridge access token into
  `Assets/Resources/DevAgentSettings.asset` (tracked). `GameBuild` clears them after every build. Building from the
  editor's own Build Profiles window doesn't, so check that file before committing.
- Check custom shaders and captions in both eyes on the Quest.
- Window playback uses live Orbis or the recorded clips under `Assets/StreamingAssets`. If a scene has neither
  source, the local pane shows its scene colors and tinted noise.
- `-executeMethod VirtualBeeSwarm.Tests.GameSmokeRunner.Run` runs the editor play-mode checks. The standalone
  smoke test above and the editor runner have different native-plugin and graphics paths.
