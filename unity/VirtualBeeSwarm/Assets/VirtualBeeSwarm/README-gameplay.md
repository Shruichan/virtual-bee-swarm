# Virtual Bee Swarm gameplay layer (concept v3 "Save the Hive")

Plain C# for Unity 6 (6000.2). Drop this folder in as `Assets/VirtualBeeSwarm/`, then build the scene from code:
menu **Virtual Bee Swarm → Build Scene**, or in batchmode:

```
Unity -batchmode -nographics -projectPath <project> -executeMethod VirtualBeeSwarm.Editor.BuildGameScene.Build -quit -logFile build.log
```

That writes `Assets/VirtualBeeSwarm/Scenes/VirtualBeeSwarm.unity` with every system wired and primitive placeholders, copies the
XR rig in from `Scenes/RigTemplate.unity` (see "XR rig" below) and makes it the only scene in Build Settings.
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

- **Unity is the only beat clock.** `GameDirector` reports `beat.set` on every change, accepts `beat.request`
  (any beat; `pre` = full reset), and auto-advances a beat that runs past its max duration
  (`maxSeconds[]`, index = beat number). On a timeout the beat wraps up first (a station: Beeswax does it himself,
  "Noted for the minutes"), then the director moves on after `timeoutGraceSeconds` (15 s; a wrap-up that finishes
  sooner advances sooner). A station left while its right answer is still playing (timeout grace ran out, operator
  skip) still lights its hexagon and counts as solved; a station skipped with no answer stays unlit until the escape
  lights all four. Jumping ahead past `meet` still opens the window. The hornet plays only if the run
  clock is under `hornetIfRunSecondsBelow` (125 s) when wasps ends, or when forced (key K / Board request n=6).
  Run clock = hook → escape; that's the score card's "Hive saved in m:ss". The rocket shows the 4:00 countdown.
- **Stations** (`StationBase`): prompt → choice buttons → `GameNet.MakeChoice` (sends `choice.made` through the
  client's `BeeswaxStations`, which plays the outcome line/clip/fx locally if the Hive is down or silent for 1.5 s)
  → the station's own visuals → right: hexagon lights, beat completes; wrong: consequence plays, that button turns red
  and locks, retry. The Board's `choice.request` (protocol v2.1, operator twin of a choice) is applied exactly like a
  press during that station's beat and ignored elsewhere. `GameNet` subscribes to the client's typed
  `BeeswaxBus.OnChoiceRequest` (client 0.2.1+); keep `BeeswaxStations.applyChoiceRequests` off. `fx.force` arrives as a plain `fx` (the Hive and the v2.1 client convert it).
- **Offline** (Hive unreachable, or key O = forced offline): nothing waits on the network. Choices fall back to
  `BeeswaxStations` (client offline) or to `GameLines` (forced offline / no BeeswaxStations). Beat lines play
  at once, online too: the Hive never voices a beat change (`hive/src/server.ts` `beatEntered` only sets the window
  scene), and the voice suppresses a Hive copy of the same line id. Only a station outcome without `BeeswaxStations`
  uses `SayUnlessHive` (wait 1.2 s for the Hive's line). Desktop F/P inject the fire/put-out intent + fx locally (and
  steer the live window if online).
- **What-if:** the fire is tracked from the window scene (`whatif_fire` / `fire_out`), whoever caused it: the
  player's voice, the Board's `whatif.force`, an `orbis.request`, or F/P. `fire_out` completes the beat. Offline
  (Hive unreachable or forced), a talk press + release counts as the question (first = fire, second = put it out),
  and with no input Beeswax starts the fire 9 s after the prompt and puts it out 8 s later, so the beat always shows it.
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

Without an XR device, `DesktopCamera` takes over (fake table shown, other MainCameras off). F1 toggles the HUD.

| Key | Does |
|---|---|
| N or Space | next beat (START in pre) |
| B | previous beat |
| Shift+R | reset to pre (same as the Board's group.reset) |
| K | hornet mode: auto / always / never |
| O | force offline on/off (simulate the Hive being unreachable) |
| 1 / 2 / 3 | pick a station choice (also: click the buttons) |
| G (hold) | lift/tip the hive (feed station); or drag it with the mouse |
| C | place the rig in front of the camera (pre) |
| H (hold) | make a fist (hornet ball) |
| U (hold) | hum (heats the hornet ball) |
| T (hold) | push-to-talk (needs the Hive + BeeswaxVoiceInput); offline in the what-if beat, press + release = ask |
| F / P | what-if: "what if a fire starts?" / "put it out!" (offline/operator backup) |
| F2 | autopilot: plays the whole show by itself (wrong choice first at each station, hornet on) |
| F1 | hide/show the debug HUD |
| Right mouse + WASD, Q/E | look and move |

In the headset: poke buttons with a fingertip, or point + pinch (hands) / trigger (controllers). Grab = pinch next to
the thing or ray + pinch. Fist (hands) / grip (controllers) = hornet ball. Talk = hold pinch in the what-if beat, or
controller B/Y in any voice beat. The Board's hum slider (`hum.set`) also drives the thermometer.

## Placing the hive on the real table (pre beat)

Everything on the table hangs under `GameRig/TableAnchor` (hive, rocket, window, stations, Beeswax's spots,
Moon). `HivePlacement` (on the base plate) moves that anchor:
1. **Grab and drop** the translucent base plate; on release the rig turns to face the player.
2. **Two-point calibration:** pinch the table's front edge twice (left, then right) where no button is under the
   pointer. The hive goes 22 cm in from the middle of that edge, facing you.

In the headset the rig first appears in front of the player (55 cm ahead, 75 cm below the eyes, once the head is
tracked; `autoPlaceInHeadset`), so it never starts inside them. Then use 1. or 2.
The pose is kept for this app session (static), not across app restarts. Only in `pre` unless `onlyInPre` is off.

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
| `Placement/HivePlacement.cs` | Grab-and-drop + two-point table calibration. |
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
| `Tests/*` (asmdef `VirtualBeeSwarm.Tests`, editor + UNITY_INCLUDE_TESTS) | Same run as an EditMode test that enters play mode (`GameSmokeTests`) and as `-executeMethod VirtualBeeSwarm.Tests.GameSmokeRunner.Run`. The runner works headless (PASS on pop-os, 2026-11-14) and ignores errors with no VirtualBeeSwarm/Beeswax frame (Meta's native plugins are missing on a Linux editor) and missing window clips. The NUnit test fails on any error log, so it fails there. |

## What the modeler replaces (keep the named transforms)

| Placeholder (in the scene) | Replace with | Keep / wire |
|---|---|---|
| `TableAnchor/Hive/LiftRoot/Body, Roof, Stand` | hive model (tippable) | `HiveBody.bodyRenderers` (tinted sick), `liftRoot` (moves when lifted), `Door` pivot (its scale.x = door width), `DoorPoint`, `SwarmCenter`, `HeatGlow` renderer |
| `.../Hexagons/Hex1-4` | hexagon panel | `HexagonPanel.hexagons` (order = smoker, feed, wasps, heat) |
| `Beeswax/Body/*` | Beeswax (faces +Z) | `BeeswaxController.mouth` (scaled on Y), `leftWing`/`rightWing` (rotated on Z), `dustyRenderers`; AudioSource + BeeswaxRoomVoice stay on the `Beeswax` root |
| `Swarm` (procedural bee mesh) | worker bee mesh, ~3.5 cm, +Z forward | `SwarmSystem.beeMesh` + an instanced material (vertex colors or texture) |
| `HONEYMOON-1/*` | rocket | `Seat`, `Perch`, `Flame`, `CountdownDisplay` |
| `FutureWindow/Portal/Frame*`, `Glow` | portal frame | `LivePane (Orbis)` and `FallbackPane` (same size; Orbis texture goes on the live pane), `Embers` (+Z out of the window) |
| Station props (`Smoker`, `HoneyJar`, `SyrupBottle`, `Wasp0-3`, `DeepBowl`, `PebbleDish`, `Drinker0-4`) | real props | the references on each station component |
| `Hornet`, `Thermometer` | giant hornet, thermometer | `HornetBeat` refs (`FillPivot` scales on Y from the bottom) |
| `ScoreCard`, `LevelMap` (7 hex tiles), `MoonScene` (Moon, Earth) | real art | `EscapeSequence.scoreText`, `LevelMap.tiles`, `T0Sequence.moon/earth` |
| Choice buttons (hex + TextMesh) | nicer buttons / physical props | any collider + `ChoiceButton` (`choiceId` must be a protocol choice) |
| TextMesh captions/labels | TextMeshPro | `BeeswaxController.caption` etc. are TextMesh fields today |

Placeholder-only: all meshes are primitives, TextMesh with the built-in font, and the window's local fallback is
tinted scrolling noise per scene (real fallback clips come through the client's `OrbisWindow.sceneClips`).

## XR rig

- The builder copies the root objects of the rig template `Scenes/RigTemplate.unity` (made by
  `ProjectBootstrap`: `OVRCameraRig` with `OVRManager` passthrough on, boundary hidden while passthrough shows, mic +
  scene permission requests at startup, `OVRPassthroughLayer` underlay, transparent camera clear) into the main scene.
  Change rig settings in the template, then rebuild. Then `Meta/Editor/MetaRigWiring` sets the floor-level origin,
  adds an `OVRHand` + `OVRSkeleton` under each hand anchor (no hand mesh: real hands show in passthrough) and wires
  `OvrHandPoseSource` + `OvrPassthroughDimmer` on `GameRig/Input`.
- `DesktopCamera` switches itself off when an XR device is active and on Android.
- Token: `Resources/beeswax_token.txt` or `HIVE_TOKEN` (see the client README). Pre-grant the mic permission.
- Performance: one instanced draw for 150 bees, unlit everywhere, no shadows, <= 80 ember particles, no per-frame
  allocations in the game loop (captions/labels allocate only when their text changes).

## Smoke test (whole run, no headset, no network, no mic, no sound)

`Editor/BuildGameScene.BuildOffline()` builds `Scenes/OfflineTest.unity` (GameNet forced
offline, the bus and window never connect, no HumDetector / BeeswaxVoiceInput). The autopilot plays it.

- **In the editor:** open `OfflineTest.unity` (or the real scene with key O), press Play, press F2.
- Autopilot options: `idleStation` (0-3) leaves that station to the max-duration wrap-up, `whatIfHandsOff` lets the
  offline what-if timers run the fire. The pop-os harness sets them from `SMOKE_IDLE_STATION` / `SMOKE_WHATIF_HANDS_OFF`.
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
