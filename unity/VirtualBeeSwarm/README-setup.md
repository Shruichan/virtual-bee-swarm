# Virtual Bee Swarm: project setup

The Quest 3 / 3S mixed-reality app for Virtual Bee Swarm (see `PLAN.md`, "Concept v3"). This README covers
setup only: versions, how to open the project, and how to build it. Gameplay lives in other folders.

## Layout

```
unity/
  BeeswaxClient/      local UPM package (Hive bus, voice, Orbis window, hum detector)
  VirtualBeeSwarm/    this project
    Packages/manifest.json   references the client as "file:../../BeeswaxClient"
    Assets/VirtualBeeSwarm/Editor/Setup/ProjectBootstrap.cs   settings bootstrap (idempotent; asmdef VirtualBeeSwarm.Setup.Editor)
    Assets/VirtualBeeSwarm/Settings/                    URP asset + renderer (created by the bootstrap)
    Assets/VirtualBeeSwarm/Scenes/RigTemplate.unity     rig template: OVRCameraRig + passthrough (created by the bootstrap;
                                                  the scene builder copies it into VirtualBeeSwarm.unity; not in Build Settings)
    Assets/VirtualBeeSwarm/Scenes/VirtualBeeSwarm.unity       THE game scene (only scene in Build Settings). A build
                                                  output: git-ignored (only its .meta is tracked). See README.md
```

Keep the two folders side by side. The relative path in `manifest.json` depends on it.

## Versions

| What | Version |
|---|---|
| Unity | **6000.2.9f1** (with the Android Build Support module, its SDK/NDK/OpenJDK) |
| Meta XR Core SDK `com.meta.xr.sdk.core` | 209.0.0 (scoped registry `https://npm.developer.oculus.com`, scope `com.meta.xr`) |
| Meta MR Utility Kit `com.meta.xr.mrutilitykit` | 209.0.0 |
| Meta Interaction SDK (OVR) `com.meta.xr.sdk.interaction.ovr` | 209.0.0 (pulls `com.meta.xr.sdk.interaction` 209.0.0) |
| Meta XR Audio `com.meta.xr.sdk.audio` | 209.0.0 |
| OpenXR Plugin `com.unity.xr.openxr` | 1.18.0 |
| Unity OpenXR: Meta `com.unity.xr.meta-openxr` | 2.7.0 (pulls AR Foundation 6.6.0). Installed, but **every feature it adds is off** (S1.10). It was only there for the Depth API, which the game doesn't use |
| XR Plug-in Management `com.unity.xr.management` | 4.7.0 |
| URP `com.unity.render-pipelines.universal` | 17.2.0 |
| uGUI + TextMeshPro `com.unity.ugui` | 2.0.0 (TMP is part of uGUI in Unity 6) |
| Input System `com.unity.inputsystem` | 1.14.2 |
| NativeWebSocket `com.endel.nativewebsocket` | 1.1.6, **vendored** in `Packages/com.endel.nativewebsocket/` (embedded package: the `upm` branch at `ea014c9`, the commit the git dependency was locked to; see its `VENDORED.md`). No git download any more |
| Beeswax client `com.virtualbeeswarm.beeswax-client` | `file:../../BeeswaxClient` |

## Settings (written by `ProjectBootstrap.cs`)

- Android, **IL2CPP**, **ARM64 only**, min API **32** (Meta: required), target API **34** (Meta: recommended for the store), Vulkan only, **ASTC** (the Player setting, so it's in `ProjectSettings.asset`), GameActivity.
- **Linear** color space. Package `dev.drori.virtualbeeswarm` (Android and Standalone), product `Virtual Bee Swarm`, company `Virtual Bee Swarm`.
- **Internet Access = Require.** `RECORD_AUDIO` is guaranteed by a post-Gradle manifest hook (also in `ProjectBootstrap.cs`), and the base scene's OVRManager asks for it at startup.
- Cleartext allowed (`insecureHttpOption = AlwaysAllowed`, Meta's network security config off) so the `ws://<laptop-LAN-IP>` fallback works.
- XR Plug-in Management: **OpenXR** loader on Android and Standalone. Render mode: single-pass instanced (**multiview**).
- OpenXR features (S1.10: **one passthrough provider**): the **Meta XR feature group** (Meta XR feature, foveation, subsampled layout) plus **Meta Quest Support** (internet permission kept) and the Oculus Touch profile. Passthrough is Meta XR Core's `OVRPassthroughLayer` on the rig. Every *Unity OpenXR: Meta* (AR Foundation) feature is **off**: Camera (it would be a second passthrough provider), Occlusion, Planes, Anchors, Bounding Boxes, Meshes, Raycasts, Session and Unity's Boundary Visibility. Planes, boxes, meshes and raycasts would each add `USE_SCENE`.
- Meta project config: Quest 2 / Pro / 3 / 3S, **hand tracking** (controllers and hands), **passthrough required**, spatial anchors, passthrough loading screen. **Scene Support = None**, so there's no `USE_SCENE` and no scene model: nothing uses MRUK, and the table is placed by hand. The boundary follows the toggle (below).
- The rig template (`Scenes/RigTemplate.unity`): the OVRManager doesn't request the scene permission, the boundary suppression follows the toggle, passthrough and the mic request stay on. No MRUK or `OVRSceneManager` component is allowed in it, and the bootstrap and `GameBuild` both refuse one.
- Then Meta's **Project Setup Tool** fixes: every Required and Recommended item with an automatic fix, except two.
  - The XR Simulator install is skipped: it's an async download that batch mode would wait on until it times out.
  - MRUK's "Scene Support should be Required" rule is marked **Ignored**. It fires for any scene with an OVRCameraRig, and its fix would bring `USE_SCENE` back. Meta stores the ignore in `Assets/Oculus/OculusProjectSetupSettings.asset`, which is in git, so **Fix All** in the editor skips it on every machine.
- After those, the OpenXR validator's automatic fixes, for example latency optimization = Prioritize Input Polling. The hard requirements are re-applied after both tools have run.
- `Assets/Plugins/Android/AndroidManifest.xml` is re-patched from the Meta config on every run (Meta's **Update AndroidManifest.xml**). At build time Meta's hook only *adds* to a custom manifest, so a stale `USE_SCENE` in it would otherwise ship.
- Last step: the **S1.10 check**. The run fails unless all of these hold:
  - IL2CPP, ARM64, min API 32, ASTC, Vulkan, the package name
  - the OpenXR loader only, multiview, Meta XR feature + Quest Support on, every Unity Meta feature off
  - Scene Support None, passthrough, hands, the boundary consistent with the toggle
  - no `USE_SCENE` in the custom manifest, no scene-permission request in the rig template
  - NativeWebSocket embedded

  `GameBuild` runs the same check before every APK.
- **URP** asset (`Assets/VirtualBeeSwarm/Settings/VirtualBeeSwarmURP.asset`: MSAA 4x, no HDR, no depth/opaque textures).
- Spatializer and ambisonic decoder: **Meta XR Audio**. The Linux editor can't load that plugin, so spatial audio can't be previewed there. The Android player loads it.
- Active Input Handling: **Both**.
- Meta's setup tool generated `Assets/Plugins/Android/AndroidManifest.xml` (single GameActivity, passthrough required, hand tracking, anchors, supported devices; no scene, and no boundary visibility while the toggle is off). Keep it in git. Meta's Gradle hook and `ProjectBootstrap.cs` add the rest (INTERNET, RECORD_AUDIO) at build time.

### Status (2026-11-14 ~06:30 PT, pop-os copy A, Android target)

- The Android-only steps have run, on copy A (`~/immerse-the-bay/unity-work/A/VirtualBeeSwarm`, Android only). The bootstrap ran twice from the old settings: the first run applied S1.10 and the second changed **no file**. The S1.10 check passes, and the Android compile has 0 errors.
- The settings files from that run (`ProjectSettings/`, `Assets/XR/`, `Assets/Oculus/`, `Assets/Plugins/Android/`, `Packages/`, the rig template) were copied back into the source copy as files. The source copy's own build target wasn't switched.
- First D APK `VirtualBeeSwarm-D-4-20261114-0630.apk`: 0 script errors, and the aapt2 check passed (README.md, "Known gaps"). Never run on a headset yet.

### Boundary toggle (S1.10)

One switch: the Android scripting define, set from the menu **Virtual Bee Swarm → Boundary**. Headless, put `VBS_BOUNDARY_MODE=off|contextual|app` on a bootstrap run. Choosing a mode re-runs the bootstrap, which sets the Meta config, the rig template and the custom manifest to match.

| Mode | Define | What it does |
|---|---|---|
| **Off** (default) | none | The normal Meta boundary. No Boundary API permission and no suppression request. The safest choice for a first launch |
| Contextual | `VBS_BOUNDARYLESS` | The Boundary API: `BOUNDARY_VISIBILITY` permission, and the rig's `OVRManager.shouldBoundaryVisibilityBeSuppressed` hides the boundary while passthrough is visible |
| App | `VBS_BOUNDARYLESS_APP` | A fully boundaryless app: `com.oculus.feature.BOUNDARYLESS_APP`, added by the post-Gradle hook. MR-only, and it can't be combined with the Boundary API |

Turn it on once a D build has booted on the headset in Off mode. Changing the toggle changes `ProjectSettings` and the Meta config, so commit it as a bootstrap commit.

## Open it

1. Install Unity **6000.2.9f1** with **Android Build Support** (OpenJDK + Android SDK & NDK).
2. Open `unity/VirtualBeeSwarm` (Unity Hub → Add → this folder). The first import downloads the Meta packages from npm.developer.oculus.com (NativeWebSocket is vendored, no git needed), so allow a few minutes.
3. File → Build Profiles → **Android** → Switch Platform (the bootstrap already does this on machines that have the module).
4. Run **Virtual Bee Swarm → Bootstrap Project Settings** (twice the first time, see README.md). It's safe to run any time, and its last step tells you whether the S1.10 Quest settings hold.
5. The Hive token: see `unity/BeeswaxClient/README.md` ("Token"). Never commit it.

Headless (what pop-os uses):

```bash
UNITY=~/Unity/Hub/Editor/6000.2.9f1/Editor/Unity
P=~/immerse-the-bay/unity-work/VirtualBeeSwarm      # or <repo>/unity/VirtualBeeSwarm

# settings bootstrap (idempotent; exits non-zero if a step failed)
$UNITY -batchmode -nographics -projectPath $P -buildTarget Android \
  -executeMethod VirtualBeeSwarm.EditorTools.ProjectBootstrap.Run -quit -logFile - | tee bootstrap.log
grep VBSBootstrap bootstrap.log

# compile check: expect 0
$UNITY -batchmode -nographics -projectPath $P -buildTarget Android -quit -logFile - | grep -c "error CS"
```

Only one Unity process may have the project open at a time.

## Build an APK

`GameBuild.BuildApk` (S1.14) builds type **D** (development; `Debug.isDebugBuild` is true, so this is where the self-test switches itself on; never quote fps from it) or **R** (release). Use the menu **Virtual Bee Swarm → Build Quest APK (D, development)** / **(R, release)**, or headless with `VBS_BUILD_TYPE=D|R`. Each build does this:
1. Checks the Android target and the S1.10 settings.
2. Runs the scene builder and checks the scene: no MRUK, no scene-permission request.
3. Writes `HIVE_TOKEN` into `Assets/Resources/beeswax_token.txt` and deletes it after the build. There's no build without a token.
4. Signs with the shared debug keystore. The passwords come from env vars and never reach `ProjectSettings`.
5. Writes `VirtualBeeSwarm-<D|R>-<n>-<commit or date>.apk`.
6. Checks the manifest with aapt2: RECORD_AUDIO, INTERNET, cleartext, hand tracking and PASSTHROUGH must be there, and USE_SCENE must not. A failed check renames the APK `*.apk.REJECTED`.

The env vars are listed at the top of `Editor/GameBuild.cs`. The keystore and its passwords live outside the repo, in `~/immerse-the-bay/keys/` on pop-os (S1.7).

```bash
# pop-os, copy A: reads the token from hive/.env and the keystore settings from ~/immerse-the-bay/keys/vbs-debug.env
~/immerse-the-bay/unity-work/A/build-apk.sh D        # -> ~/immerse-the-bay/unity-work/builds/VirtualBeeSwarm-D-<n>-<date>.apk

# laptop, Quest on USB: install -r -g (uninstall + retry on a signature mismatch), logcat -c, launch, logcat to a file
ops/quest-deploy.sh ~/Downloads/VirtualBeeSwarm-D-1-<date>.apk
```

Don't enroll the demo Quest in ManageXR (PLAN.md, Meta bug MAN-54).
