# HONEYMOON gameplay layer

Plain C# for Unity 6 (6000.2). Drop this folder in as `Assets/VirtualBeeSwarm/`, then build the scene from code:
menu **Virtual Bee Swarm → Build Scene**, or in batchmode:

```
Unity -batchmode -nographics -projectPath <project> -executeMethod VirtualBeeSwarm.Editor.BuildGameScene.Build -quit -logFile build.log
```

That writes `Assets/VirtualBeeSwarm/Scenes/VirtualBeeSwarm.unity`, wires the authored art with primitive fallbacks,
and copies the XR rig from `Scenes/RigTemplate.unity`. It is the sole scene in Build Settings.
Materials, meshes and textures are created only if they're missing, so your tweaks to them survive a rebuild.
The scene itself is regenerated each time (the old one is copied to `Library/SceneBackups/` first; the menu asks
before it overwrites, batch mode doesn't). **The scene is a build output:** git-ignored, never edited by hand.
`GameBuild.BuildApk` runs the builder first, and `Editor/SceneAutoBuild.cs` builds it when the editor opens the
project without it. Only `VirtualBeeSwarm.unity.meta` is tracked; the builder pins that GUID
(`BuildGameScene.SceneGuid`), so Build Settings stay the same everywhere.

**Needs:** `unity/BeeswaxClient` (protocol v2, asmdef `Beeswax.Client`) + NativeWebSocket. Optional: Meta XR Core SDK
(`com.meta.xr.sdk.core`) for hands/passthrough, and the Input System package (both input backends work).

## How it fits together

```
GameDirector (the beat clock)  --beat.set-->  GameNet  --(only file that calls BeeswaxClient)-->  Hive
   | Enter/Exit/Timeout                               ^  events: BeatRequested, GroupReset, Transcript, Intent,
   v                                                  |          Fx, SceneChanged, OperatorHeat, LineStarted
BeatBehaviours: IntroSequence (pre/hook/meet), SmokerStation, FeedStation, WaspsStation, HornetBeat,
                HeatStation, WhatIfBeat, EscapeSequence, T0Sequence, LevelMap (map/field)
World: HiveBody, HexagonPanel, SwarmSystem, BeeswaxController, FutureWindow, EmbersFx
Input: GameHands (registry) <- OvrHandPoseSource (Meta) | XRControllerHandSource (any OpenXR)
       GamePointer (mouse / hand rays), GameFingertip (poke), ChoiceButton, HivePlacement
```

- **Opening.** One worker bee brings the clipboard and waits for the signature. `IntroSequence` sends it back,
  cuts the ring, and brings in the hive. Beeswax speaks from inside, then emerges after it lands.
- **Clock and progress.** `GameDirector` reports `beat.set`, accepts `beat.request` and wraps up overdue beats.
  The four-minute clock starts at Smoker. Four distinct station fixes stop the score clock. A return to Pre clears
  progress. The hornet is off by default and can be enabled with K or a Board request.
- **Stations.** `StationBase` owns the outcome and progress. In default `handsOn.mode=full`, poking an ORDER UP
  ticket calls in a prop. Players pump the smoker, lift and feed the hive, fit the entrance reducer, and set down
  and fill the pebble dish. A wrong choice plays its consequence and leaves the other orders available.
  The crew assists if the setup stalls. The Board's `choice.request` applies only during the matching station.
- **What-if.** Voice, the Board, and desktop F/P can start or put out the fire. Silence triggers a preview and an
  unattended fire is put out before the rescue. With the Hive offline, Unity drives the whole beat locally.
- **Ending.** The rescue shows the score and restores the meadow. The lever launches the rocket in hands-on mode.
  Quest cues Moon at liftoff and restores the meadow at touchdown. Reconnect during T-0 restores the scene for the
  current launch phase. The run ends with the map and final contract.
- `GameLines` is **generated** from `hive/lines/lines.json` + `stations.json` (`Editor/Voice/BeeswaxVoiceTools`,
  menu **Virtual Bee Swarm > Voice**): every line as a field (`room_t0_earth` → `GameLines.RoomT0Earth`), the names
  the beats use (`Hook`, `FeedPrompt`, ... = the `Aliases` table in the tool) and the outcome table. Never edit it by
  hand; to give a beat another line, change `Aliases` and regenerate. **Check** is the two-way id check (lines.json =
  GameLines with identical captions, stations.json = GameIds = outcomes, GameLines.cs current, every id has a clip and
  every clip an id, clips rendered from older text, import settings) and exits 1 on errors in batch.
- Beeswax's voice: `Resources/BeeswaxLines/<line id>.mp3` (+ `_manifest.json`, the render manifest: which text each
  clip was made from). `BeeswaxRoomVoice` plays a clip by line id; without one the caption shows (and the mouth flaps).
  `Editor/Voice/BeeswaxLinesImporter` sets mono (normalize off), Vorbis 0.5, Decompress On Load, Preload Audio Data,
  Load In Background, 22050 Hz on Android. `GameNet.PreloadVoice` (called when pre starts) loads every clip and warms
  the voice's cache, so no line start does a `Resources.Load`. Each line logs `[VBS] voice CLIP <id> ...`,
  `CAPTION-ONLY <id>` or `STREAM`.

## Desktop keys (editor / no headset)

Without an XR device, `DesktopCamera` shows the floor meadow. F1 toggles the HUD.

| Key | Does |
|---|---|
| N or Space | next beat (START in pre) |
| B | previous beat |
| Shift+R | reset to pre (same as the Board's group.reset) |
| K | hornet mode: auto / always / never |
| O | force offline on/off (simulate the Hive being unreachable) |
| 1 / 2 / 3 | order a station choice (or click a ticket) |
| X / Y | carry a prop through setup / pull the lever |
| G (hold) | lift/tip the hive (feed station); or drag it with the mouse |
| C | place the rig in front of the camera (pre) |
| H (hold) | make a fist (hornet ball) |
| U (hold) | hum (heats the hornet ball) |
| T (hold) | push-to-talk (needs the Hive + BeeswaxVoiceInput); offline in the what-if beat, press + release = ask |
| F / P | what-if: "what if a fire starts?" / "put it out!" (offline/operator backup) |
| F2 | autopilot plays the whole show (K enables the optional hornet) |
| F1 | hide/show the debug HUD |
| Right mouse + WASD, Q/E | look and move |

In the headset: poke buttons with a fingertip, or point + pinch (hands) / trigger (controllers). Grab = pinch next to
the thing or ray + pinch. Fist (hands) / grip (controllers) = hornet ball. Talk = hold pinch in the what-if beat, or
controller B/Y in any voice beat. The Board's hum slider (`hum.set`) also drives the thermometer.

## Placing the hive

`GameRig/TableAnchor` carries the hive and stations in both layouts. In the default meadow layout, `MeadowLayout`
places the rig on open floor and keeps the clipboard at hand size. Before signing, an aim-and-pinch floor hit
moves the rig. `ArRig` mounts the window on a wall if one is available.

Set `meadow.rig=false` in `tuning.json` for the tabletop layout. `HivePlacement` then supports grab-and-drop of the
base plate and two-point calibration along the table's front edge. The app remembers placement for the session.

## File map

| File | What |
|---|---|
| `Net/GameNet.cs` | **The only BeeswaxClient user.** Wraps bus, stations, voice input, room voice, hum, Orbis window. Offline fallbacks, local simulations, `EnsureClientComponents` (scene builder). |
| `Core/GameDirector.cs` | Beat clock, beat.set / beat.request, max durations, hornet rule, run timer, countdown. |
| `Core/BeatBehaviour.cs` | Base for beat systems (Enter/Exit/Timeout/Reset + coroutine helpers). |
| `Core/GameIds.cs` | Protocol v2 vocabulary (beats, stations, choices, scenes, fx, intents). |
| `Core/GameLines.cs` | **Generated** (don't edit): Beeswax's lines from `hive/lines/lines.json`, the beat names, the station outcome table. |
| `Core/GameInput.cs` | Keyboard/mouse for both input backends, allocation-free. |
| `Core/GameHands.cs`, `Core/GameInterfaces.cs` | Hand-source registry + `IHandPoseSource`, `IGameTarget`, `IGameGrabTarget`, `IPassthroughDimmer`. |
| `Core/GameMaterials.cs` | Unlit materials for URP or built-in, MPB tinting, caption wrap. |
| `Core/DesktopTestCamera.cs`, `Core/GameDebugHud.cs` | Testing without a headset. |
| `Stations/*` | `StationBase` + Smoker, Feed (tip the hive), Wasps (door), Heat (water). |
| `Beats/*` | Intro (pre/hook/meet), Hornet, WhatIf, Escape, T0, LevelMap. |
| `World/*` | HiveBody, HexagonPanel, SwarmSystem, BeeswaxController, FutureWindow, EmbersFx, GameMeshes. |
| `Interaction/*` | ChoiceButton, GamePointer, GameFingertip, XRControllerHandSource. |
| `Placement/HivePlacement.cs` | Session placement and tabletop calibration. |
| `Bloom/MeadowLayout.cs`, `Bloom/MeadowField.cs` | Floor-first rig, room meadow, and placement API. |
| `HandsOn/*`, `Delivery/*` | Orders, hands-on setup, lever, and bee deliveries. |
| `Meta/*` (asmdef `VirtualBeeSwarm.Meta`) | `OvrHandPoseSource`, `OvrPassthroughDimmer`. Compiles only when `com.meta.xr.sdk.core` is installed (version define `VBS_META`). |
| `Shaders/GameUnlit.shader` | One unlit shader for everything: instanced color, optional vertex color, blend modes; URP + built-in, stereo-instancing macros. |
| `Editor/BuildGameScene.cs` | Builds the scene (and `OfflineTest.unity`: no network, no mic, no rig). |
| `Editor/GameSmokeBuild.cs` | Headless smoke test: builds a desktop player of a generated test scene that plays itself (see below). |
| `Editor/Voice/*` | `BeeswaxVoiceTools` (Generate GameLines, Import clips from `hive/out`, Check), `BeeswaxLinesImporter` (clip import settings), `MiniJson`. |
| `Editor/GameBuild.cs` | Quest APK build (`BuildApk`): runs the scene builder, then builds the Build Settings scene. |
| `Editor/SceneAutoBuild.cs` | Builds `VirtualBeeSwarm.unity` when the editor opens the project and the file is missing (`VBS_AUTOBUILD=0` off, `=1` also in batch mode). |
| `Editor/Setup/ProjectBootstrap.cs` (asmdef `VirtualBeeSwarm.Setup.Editor`) | Quest project settings + the rig template scene (VirtualBeeSwarm only). |
| `Meta/Editor/MetaRigWiring.cs` (asmdef `VirtualBeeSwarm.Meta.Editor`) | Builder step: floor-level origin, OVRHand + OVRSkeleton per hand, wires `OvrHandPoseSource` / `OvrPassthroughDimmer`. |
| `Core/GameAutopilot.cs` | Plays the whole show (F2). With runOnStart + quitWhenDone it's the smoke test: exit code 0 = pass. |
| `Tests/*` (asmdef `VirtualBeeSwarm.Tests`, editor + UNITY_INCLUDE_TESTS) | EditMode tests that enter Play mode. `-executeMethod VirtualBeeSwarm.Tests.GameSmokeRunner.Run` runs the editor smoke checks. |

## Art slots and required transforms

The worker rig supplies the courier and colony bees. Beeswax uses his drone rig, and the queen appears at the rescue.
The wasps raid the hive entrance. The hornet sequence is off by default.

| Scene location | Art slot | Keep / wire |
|---|---|---|
| `TableAnchor/Hive/LiftRoot/Body, Roof, Stand` | hive model (tippable) | `HiveBody.bodyRenderers` (tinted sick), `liftRoot` (moves when lifted), `Door` pivot (its scale.x = door width), `DoorPoint`, `SwarmCenter`, `HeatGlow` renderer |
| `.../Hexagons/Hex1-4` | hexagon panel | `HexagonPanel.hexagons` (order = smoker, feed, wasps, heat) |
| `Beeswax/Body/*` | Beeswax (faces +Z) | `BeeswaxController.mouth` (scaled on Y), `leftWing`/`rightWing` (rotated on Z), `dustyRenderers`; AudioSource + BeeswaxRoomVoice stay on the `Beeswax` root |
| `Swarm` | worker bee rig, ~3.5 cm, +Z forward | `SwarmSystem.WorkerBeeMesh` shares the rig's baked rest pose. The same rig supplies the near animation. |
| `HONEYMOON-1/*` | rocket | `Seat`, `Perch`, `Flame`, `CountdownDisplay` |
| `FutureWindow/Portal/Frame*`, `Glow` | portal frame | `LivePane (Orbis)` and `FallbackPane` (same size; Orbis texture goes on the live pane), `Embers` (+Z out of the window) |
| Station props (`Smoker`, `HoneyJar`, `SyrupBottle`, `Wasp0-3`, `DeepBowl`, `PebbleDish`, `Drinker0-4`) | real props | the references on each station component |
| `Hornet`, `Thermometer` | giant hornet, thermometer | `HornetBeat` refs (`FillPivot` scales on Y from the bottom) |
| `ScoreCard`, `LevelMap` (7 hex tiles), `MoonScene` (Moon, Earth) | real art | `EscapeSequence.scoreText`, `LevelMap.tiles`, `T0Sequence.moon/earth` |
| Choice buttons (hex + TextMesh) | nicer buttons / physical props | any collider + `ChoiceButton` (`choiceId` must be a protocol choice) |
| TextMesh captions/labels | TextMeshPro | `BeeswaxController.caption` etc. are TextMesh fields today |

The main scene uses authored slots where available. The window uses the Orbis stream or recorded clips and falls
back to tinted noise when neither is available.

## XR rig

- The builder copies the root objects of the rig template `Scenes/RigTemplate.unity` (made by
  `ProjectBootstrap`: `OVRCameraRig` with `OVRManager` passthrough on, boundary hidden while passthrough shows, mic +
  scene permission requests at startup, `OVRPassthroughLayer` underlay, transparent camera clear) into the main scene.
  Change rig settings in the template, then rebuild. Then `Meta/Editor/MetaRigWiring` sets the floor-level origin,
  adds an `OVRHand` + `OVRSkeleton` under each hand anchor (no hand mesh: real hands show in passthrough) and wires
  `OvrHandPoseSource` + `OvrPassthroughDimmer` on `GameRig/Input`.
- `DesktopCamera` switches itself off when an XR device is active and on Android.
- Token: `Resources/beeswax_token.txt` or `HIVE_TOKEN` (see the client README). Pre-grant the mic permission.
- The meadow culls floor tiles. The bee swarms use instancing and choose the animated VAT model for nearby bees
  and the worker's static shader-flap mesh farther away. Check GPU time on a headset with the full meadow and swarm enabled.

## Smoke test (whole run, no headset, no network, no mic, no sound)

`Editor/BuildGameScene.BuildOffline()` builds `Scenes/OfflineTest.unity` (GameNet forced
offline, the bus and window never connect, no HumDetector / BeeswaxVoiceInput). The autopilot plays it.

- **In the editor:** open `OfflineTest.unity` (or the real scene with key O), press Play, press F2.
- Set `VBS_AUTOPILOT_HANDSON=1` for simulated prop setup and lever input. `SMOKE_IDLE_STATION` (0–3) leaves one
  station to the timeout. `SMOKE_WHATIF_HANDS_OFF=1` exercises the fire timers. These desktop runs simulate input
  and cannot verify physical placement or tracking.
- **Headless:** `Editor/GameSmokeBuild.Build` builds a desktop player (silent: audio is switched off for that
  build and restored) of a generated test scene, then you run it under `xvfb-run ... -batchmode -nographics`. It
  logs `[VBSAutopilot] PASS` and exits 0. `SMOKE_ONLINE=1` makes an online variant (no mic components, hosts from
  `SMOKE_HOSTS`, waits up to 25 s for the Hive). The harness sets `ignoreForeignExceptions`, because the Meta SDK's
  native plugins don't exist in a desktop player and throw every frame; exceptions with a VirtualBeeSwarm/Beeswax frame
  still fail the run. It refuses to run when the active build target is Android (a desktop build would switch the
  target and reimport everything) unless `SMOKE_ALLOW_SWITCH=1`. Commands are in the class comment and the project
  README.
- **Voice in the smoke test:** the autopilot waits for `GameNet.PreloadVoice`, counts every line Beeswax starts and
  logs `[VBSAutopilot] voice: N line(s) started, N with a pre-rendered clip (... preloaded), 0 caption-only`.
  `SMOKE_REQUIRE_CLIPS=1` (the autopilot's `requireClips`) fails the run on a caption-only line or a clip that wasn't
  preloaded (S8.3). With Unity audio off in the smoke player a clip line ends after ~0.1 s, so the run is faster than
  real ("Hive saved in 1:20" at 4x instead of ~3:07); the clip itself still resolves and is logged with its length.
