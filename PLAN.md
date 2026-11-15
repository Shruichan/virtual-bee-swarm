# Virtual Bee Swarm — Plan & Team Log

> **📣 READ THIS FIRST: this file is how the team talks.**
> It's our communication doc *and* the outline of the game. Every teammate and every AI agent working in this repo should:
>
> 1. **Pull and read this whole file** before starting anything (`git pull --rebase`).
> 2. **Claim** a task in [Who's doing what](#whos-doing-what) before starting it, and update its status when it changes.
> 3. **Log** every plan change, decision, finished piece, blocker, or question in the [Live log](#live-log) at the bottom. Append only, newest last.
> 4. **Push right away.** Make small, frequent commits to this file. It only works if it's current.
> 5. **Never put API keys or secrets here.** The repo is public. Keys stay on Discord and in `.env` files, which git ignores.
>
> Several people and agents push to this file at once. `.gitattributes` gives it a union merge, so a pull never stops on a conflict here. Instead, both sides' lines are kept. If you see duplicated lines after a pull, tidy them up and log that you did. Rules for agents are in [AGENTS.md](AGENTS.md).

---

## At a glance

| | |
|---|---|
| Event | Immerse the Bay 2026 (Stanford XR). Theme: *Take Me To The Moon* |
| Hard deadline | **Hacking ends and Devpost closes Sun Nov 15, 9:00 AM PT.** Judging 10 AM–12 PM, closing ceremony 1:30 PM |
| Team name | "Virtual Bee Swarm" or "Angry Virtual Bee Swarm"? **Finalize by Sat 9:00 AM** |
| Hardware | **One Meta Quest (3 or 3S: which one?)**, **Mentra Live** glasses (camera + mics + speakers, **no display**), and Rome's **iPhone** as the glasses' phone |
| Tracks | Pick 2. Leading candidates: **ManageXR** ("bring AI to the physical world with smart glasses") and **Visko** (living worlds), with **Social Good** as the fallback. The official 2026 text is on the login-gated live dashboard, so someone copy it here |
| Concept | **v3 = best of both:** Adityash's hive escape (4 stations + live Orbis future window + voice what-ifs) + HONEYMOON (Beeswax the union-rep drone, Moon stakes and ending, hornet hum). Mentra Live adds an audio-only Bee Scout field guide for plant and pollinator IDs |

## ⏰ Do these tonight (everyone)

- **Someone logged into the live dashboard:** copy the 2026 **Tracks & Submit** text, rules, and judging criteria into this file.
- **Check Discord** for a team/repo/equipment form. In 2025 it was due Fri 9:30 PM.
- **Everyone:** click "Join hackathon" on [Devpost](https://immerse-the-bay-2026.devpost.com/) and accept the project invite later. Accept the GitHub repo invite.
- **Glasses owner:** update Mentra Live software tonight on a strong network (~150–640 MB). In MentraOS, set the button mode to **apps**, not camera.
- **Shopping (Sat 7:30–9 AM):** order the mat print tonight for Sat 8 AM pickup. Buy plants that **actually bloom purple in November**: salvia / Mexican bush sage, asters, rosemary in flower. Lavender is usually not blooming now. See the [props list](#physical-props--shopping).

---

## Work plan (Sat 2 AM → Sun 9 AM)

*This replaces the old "Delegation plan". Written Sat 02:35 PT. We want to submit by Sun 08:30, and Devpost closes at 09:00.*

1. **Pull** the next task off the very top of your column in the schedule below. Open P0 bugs in the [Bug list](#bug-list) come before any new task.
2. **Claim** it: replace `*suggested X*` with `**X**`, set the status (open / claimed / in progress / blocked / done), and push PLAN.md right away.
3. **Log** a line in the Live log every time you start, finish, get blocked or hand something off. Always with the file path and "→ name".

### House rules
- **Exactly one agent lane writes to each path.** Every system has one human owner and one agent lane.
  - **Lane A:** `unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm/**` (game code, scene builder, `Meta/`, `Placement/`, tuning code), minus anything listed under the other lanes.
  - **Lane B:** `ProjectSettings/`, `Packages/`, `Assets/XR/`, `Assets/Plugins/Android/`, `Assets/VirtualBeeSwarm/Editor/Setup/`, `Assets/VirtualBeeSwarm/Settings/`, `GameBuild.cs`, `ops/*.sh`.
  - **Lane C:** `hive/**` (but not `hive/lines/`) and `unity/BeeswaxClient/**`.
  - **Lane D:** `hive/lines/**`, `GameLines.cs`, `Assets/Resources/BeeswaxLines/`, the meadow clips, `Art/Audio/`.
  - **Lane E:** `pitch/**`, `ops/*.md`, README, LICENSE, and the bookkeeping sections of PLAN.md.
  - **Lane F:** `glasses/**` (Mentra Live miniapp and its separate release build).
  - **Humans:** `Art/<Slot>/` belongs to dartiros. `Tuning/station-values.json` belongs to Adityash. ProjectSettings changes from Nam's editor only go in as bootstrap commits.
- **`VirtualBeeSwarm.unity` is a build output.** It's gitignored, and `GameBuild.BuildApk` runs the scene builder first on every machine. Nobody touches it by hand. Changes go through:
  - the builder (lane A)
  - `tuning.json` and the tuning asset (Adityash)
  - `Art/<Slot>/<Slot>.prefab` (dartiros)
- **Builds**
  - Nam is build master. Adityash has the headset on from 08:45.
  - Agents merge by :10 and :40. Nam pulls at :15 and :45 and only builds the last commit that S1.15 marked green.
  - Build N+1 compiles while we test build N.
  - There are three build types:
    - **L:** Link play mode. Windows only, takes seconds.
    - **D:** dev APK with the self-test switched on.
    - **R:** release APK. For fps checks, RCs and video capture.
  - Never quote fps numbers from a D build.
  - Keep the Quest on the charger during every build.
- **pop-os project copies:** A is Android, B is Linux, C is a spare if the CPU can take it. Never switch the build target inside a copy. Only one Unity batch job per copy at a time. All tests run with the sound off.
- **Bugs** go in the [Bug list](#bug-list). Each one needs:
  - build number
  - beat
  - what you did
  - expected vs actual
  - logcat time
- **Judging roles** (the same in all three rehearsals):
  - **Operator:** Nam
  - **Host and pitch:** Rome
  - **Mediator:** Adityash
  - **Spotter:** dartiros

### Gates
- **Sat 02:45, license (S1.5).** If that fails, don't keep trying. From then on the agents only do code, and Nam's settings commit becomes the source of truth.
- **03:45, skeleton boots on the Quest (S1.8).** If not, Nam logs the error, the agents work on it overnight, and at 08:00 we ask the Meta and Unity mentors.
- **07:15, morning report (S0.5).**
- **08:45, full build boots (S1.18).** If not, we switch to **Plan B**:
  - the milestone slips to 14:00
  - Link play mode becomes the demo path (Windows only)
  - all agents go onto build problems
- **09:00:**
  - team name (S16.2)
  - draft tracks (S16.3)
  - sponsor tables (S9.2, S0.6)
  - rules confirmed (S16.1)
- **10:30** spend cap set (S9.3). **11:00** Orbis gate (S9.4). **11:15** final tracks.
- **12:00, milestone (S2.5).** Every station's happy path works once. FIRE from the Board works once, and fire by voice does too. T-0 and the escape each run through once. The 3-of-3 checks for both branches slip to 13:00–16:00.
- **13:00** no Reactor key → stand-ins (S9.6).
- **14:30–15:00** merge and fix only. No new features.
- **15:00, noise test (S7.3).** Under 60% → the Operator's buttons become the main path.
- **16:00** voice freeze (S8.7).
- **16:30** last art swap (S11.10).
- **16:30–17:00** merge and fix.
- **17:00–17:30** integration only.
- **17:30** RC1.
- **18:00, feature freeze and rehearsal 1.** After that it's P0 fixes only, and each one gets checked on the headset.
- **21:15** capture ends. If shots are missing, use footage from the rehearsals and from phones. Never reopen the build.
- **21:30** safety video cut (S15.5).
- **23:00** Devpost draft saved.
- **23:45** RC3 decision: built by 01:00, or not at all.
- **Sun 01:00** early full submission, if Devpost still lets us edit afterwards (S16.10).
- **02:00** final video.
- **07:30 build lock and secret scan.**
- **07:45** rehearsal 3.
- **08:30 submit.**

### Task board

| System | Human owner | Agent lane |
|---|---|---|
| S0 Coordination and QA | Adityash (Rome until 08:00) | E |
| S1 Build, release, XR config | Nam | B (A for scene code) |
| S2 Director, flow, tuning | Adityash | A |
| S3 Hands and placement | Nam | A |
| S4 Stations | Adityash | A |
| S5 What-if, embers, meadow window | Adityash | A + C |
| S6 Endings | Adityash | A |
| S7 Mic and push-to-talk | Rome | C |
| S8 Beeswax lines and prerender | Adityash (words) | D |
| S9 Orbis | Rome | C (relay), D (clips) |
| S10 Hive and Board | Rome | C |
| S11 Art | dartiros | A, only when asked |
| S12 Sound | Adityash | A (cues), D (files) |
| S13 Performance | Nam | A + B |
| S14 Venue, rehearsals, judging | Adityash | E |
| S15 Video | dartiros | E |
| S16 Devpost, tracks, repo | Rome | E |
| S17 Mentra Live miniapp | Rome | F |

**Cut, so don't claim these:**
- Glasses build (S17): claimed as a separate release track; real-prop choices remain later.
- the hornet, the hum, and the hornet and thermometer art
- the fail ending
- the custom what-if: **off the demo path** (it's built and hooked up to the Board's text box, but don't rely on it live)
- the squeeze gesture
- LFS
- SATIE, unless we go for the Attractor prize
- the clips for rain, flowers, spray and winter (P2)
- the 2-minute walkthrough, unless the rules require it
- controllers (P2, unless S3.1 scores under 8 of 10)

#### S0 Coordination and QA

- **S0.1 Bug list section** (P0 · 0.25 h · needs –) — done when: PLAN.md has a "Bug list" section in the 5-field format with one example line in it, and the test cards link to it — owner: *suggested Rome-agents (lane E)* — status: open

- **S0.2 Bring the rest of PLAN.md in line** (P0 · 0.5 h · needs –) — done when: nothing in PLAN.md contradicts this plan anymore (scene-editing rule, CUT list, controllers at P2, dartiros as video owner, timeline, sleep shifts), and Who's doing what lists these S-IDs — owner: *suggested Rome-agents (lane E)* — status: open

- **S0.3 Lane list and standing rule in AGENTS.md** (P0 · 0.25 h · needs –) — done when: lanes A–E are listed with their paths. Rule for an agent with nothing to do: first take the top P0 bug nobody's claimed yet, then the next task in your own lane, and put your name on it in PLAN.md before you start — owner: *suggested Rome-agents (lane E)* — status: open

- **S0.4 Test cards** (P0 · 1 h · needs S1.12) — done when: by 07:00 `ops/test-cards.md` has one card per headset task (build type, steps, expected result, what to log), so whoever has the Quest can just work through the cards without having to ask anyone — owner: *suggested Rome-agents (lane E)* — status: open

- **S0.5 Morning report** (P0 · 0.25 h · needs –) — done when: at 07:15 `ops/morning-report.md` says what landed, what failed and what didn't land; the APK path and the one install command; the test results; and questions marked → Rome and → Nam — owner: *suggested Rome-agents (lane E)* — status: open

- **S0.6 Ask for a second Quest** (P1 · 0.25 h · needs –) — done when: at 09:00 we've asked the ManageXR table and the hardware desk for one with developer mode on. If we get one, Adityash gets it for feel and sound work — owner: *suggested Rome* — status: open

- **S0.7 Book outside testers** (P1 · 0.5 h · needs S2.5) — done when: the Live log has 3 people down by name for 14:00–15:00 and 1–2 people for 18:00 — owner: *suggested Adityash* — status: open

#### S1 Build, release and XR config

- **S1.1 Find out who's writing to `hq/` and BeeswaxClient** (P0 · 0.1 h · needs –) — done when: we know which session edited the scratchpad integration copy and `unity/BeeswaxClient` between 02:10 and 02:31, it's finished or stopped, and it's handed over in the Live log — owner: *suggested Rome* — status: done (it was Rome's integration workflow)

- **S1.2 Keep the night queue running** (P0 · 0.1 h · needs –) — done when: PenguinBook is plugged in and running under `systemd-inhibit`, and the Unity jobs on pop-os run under `systemd-run --user` or tmux — owner: *suggested Rome* — status: open

- **S1.3 Rescue the scratchpad** (P0 · 0.5 h · needs S1.1) — done when: the integration copy, BeeswaxClient (recorded version: 0.2.1 with `OnChoiceRequest`) and `android-setup/` are in the repo and in a tarball on pop-os, and the checksums match; someone has gone through the diff between `unity-gameplay` and `hq` for edits after 01:55; and grep finds no script that still points into the scratchpad — owner: *suggested Rome-agents (lane B)* — status: in progress: integration workflow verifying a fresh copy, then push (renamed to unity/VirtualBeeSwarm)

- **S1.4 Push the skeleton** (P0 · 0.25 h · needs S1.3) — done when: the manifest, packages lock, ProjectSettings, `Assets/XR`, `Editor/Setup` and the rig template are pushed by 02:50. No Library, Temp, Logs, UserSettings, no token, no LFS. If the 02:28 tree already compiles with the gameplay in it, just push the whole tree, and S1.9 is left with only the cleanup — owner: *suggested Rome-agents (lane B)* — status: in progress: lands with S1.3

- **S1.5 Unity license on pop-os (02:45 gate)** (P0 · 0.5 h · needs –) — done when: `check-license.sh` shows the license and the smoke APK build comes back with rc=0. Or: it's failed by 02:45 (it only works with an email-and-password Unity ID), the failure is logged, and S1.8 is how we build from then on — owner: *suggested Rome* — status: **done 02:03**: license active, smoke APK built rc=0 in 51 s

- **S1.6 Set up Nam's machine** (P0 · 0.75 h · needs –) — done when:
  - Unity **6000.2.9f1** with the Android module (OpenJDK, SDK, NDK) is installed. Exactly that version, never 6000.3
  - Quest 3 or 3S? Windows or Mac? Both are in the log
  - developer mode is on and `adb devices` shows the headset
  - the OVR Metrics Tool is installed
  - "Hey Meta" and voice commands are switched off

  — owner: **Nam Nguyen** — status: in progress (headset usable with project; OVR Metrics Tool and voice settings need verification)

- **S1.7 Hand over the token and keystore** (P0 · 0.1 h · needs –) — done when: Rome has DM'd Nam and dartiros the Hive token (it goes in `HIVE_TOKEN`) and one shared debug keystore, and both are kept outside the repo — owner: *suggested Rome* — status: open

- **S1.8 Skeleton on the headset (03:45 gate)** (P0 · 1 h · needs S1.4, S1.6) — done when:
  - Nam has cloned the repo, switched the target to Android and run Virtual Bee Swarm > Bootstrap Project Settings twice
  - he's built it and sideloaded it, and it starts in passthrough with `OVRHand.IsTracked` true
  - the ProjectSettings and `Assets/XR` from that run are pushed as a bootstrap commit
  - the S1.9 import got kicked off before bed, machine plugged in, sleep off

  — owner: **Nam Nguyen** — status: blocked (Android bootstrap passed twice and XR settings pushed; APK build awaits S1.7 token/keystore handoff; S1.9 batch import completed; sleep timeout is Never, physical power connection unverified)

- **S1.9 Merge in the gameplay layer** (P0 · 1 h · needs S1.4) — done when:
  - a fresh clone plus the builder gives 0 compile errors, and the Linux autopilot passes
  - `VirtualBeeSwarm.unity` is gitignored
  - the builder's `Materials/` and `Meshes/` folders and the rig template are committed
  - ProjectBootstrap stays in `Editor/Setup`

  — owner: *suggested Rome-agents (lane A)* — status: in progress: merged by the integration workflow, verifying a fresh copy

- **S1.10 XR and Android config** (P0 · 1.5 h · needs S1.4, and S1.5 or S1.8) — done when:
  - there's only one passthrough provider (the Meta XR feature plus Meta Quest Support)
  - Unity's Meta Quest Camera, Occlusion, Planes and Anchors features are off
  - multiview, IL2CPP ARM64, ASTC and min API 32 are set
  - USE_SCENE is **required** now (v3.1: Depth API + environment raycast need it); MRUK `LoadSceneOnStartup` off; `sceneSupport` Required; Meta Quest Occlusion + Session features on (Android and Standalone)
  - boundaryless sits behind a toggle
  - NativeWebSocket is vendored under `Packages/`
  - running the bootstrap twice changes nothing
  - the Android compile has 0 errors

  — owner: **Nam Nguyen** — status: in progress

- **S1.11 XR rig in the builder** (P0 · 1.5 h · needs S1.9) — done when:
  - `MetaRigWiring.cs` is finished:
    - OVRCameraRig and OVRManager, passthrough and hands on, CPU/GPU level at Sustained
    - a passthrough underlay
    - clear colour with alpha 0
    - OVRHands feeding OvrHandPoseSource
    - a fingertip cursor you can actually see
    - no shadows
  - VirtualBeeSwarm is the only scene in the build list
  - a batch check finds every component
  - the Linux autopilot still passes
  - only code and the rig template change, no settings

  — owner: *suggested Rome-agents (lane A)* — status: open

- **S1.12 Device self-test** (P0 · 1.5 h · needs S1.11) — done when: in D builds, and at the press of a button on the Board, the app reports a checklist to the Board, and a Linux player run shows the full checklist. It covers:
  - passthrough, hands tracked, pinch strength
  - mic: device list, permission, 2 s input level
  - audio: one prerendered clip and one streamed PCM chunk
  - network: websocket, token, `/health`
  - decode time (ms) for 3 JPEGs
  - `Prepare` time (ms) for one clip
  - refresh rate, CPU and GPU ms
  - client version and lines hash

  — owner: *suggested Rome-agents (lane A)* — status: open

- **S1.13 Remote log** (P0 · 0.75 h · needs S10.2) — done when: warnings and up, plus `[VBS]` and `[Beeswax]` lines, go over the bus (rate-limited) into a file on the Hive host that the agents can read, and a Linux player run shows up there. adb logcat stays for native crashes — owner: *suggested Rome-agents (lane C)* — status: open

- **S1.14 Build and deploy script** (P0 · 1.5 h · needs S1.10, S1.11) — done when:
  - `GameBuild.BuildApk` (type D or R):
    - runs the builder
    - copies the token in from `HIVE_TOKEN` (and fails without it), then deletes it again after the build
    - signs with the shared keystore under one fixed package name
    - writes `Builds/Virtual Bee Swarm-<n>-<commit>.apk`
    - fails unless aapt2 shows RECORD_AUDIO, INTERNET, cleartext, hand tracking, PASSTHROUGH, USE_SCENE and USE_ANCHOR_API
  - `ops/quest-deploy.sh` (plus a Windows version if we need one):
    - runs `adb install -r -g`; on INSTALL_FAILED_UPDATE_INCOMPATIBLE it uninstalls and tries again; then `pm grant` USE_SCENE and RECORD_AUDIO and checks `dumpsys package` shows them granted
    - runs `logcat -c`
    - launches through `resolve-activity`
    - saves logcat to a file
  - Nam has run it end to end once, and a warm rebuild takes 10 min at most

  — owner: *suggested Rome-agents (lane B)* — status: open

- **S1.15 Merge check** (P0 · 1 h · needs S1.9) — done when:
  - every push gets:
    - an Android compile (copy A)
    - a Linux autopilot run (copy B)
    - a line parity check: the Hive's ids and caption hashes match Unity's
    - an empty `git diff --stat ProjectSettings Packages`, except on bootstrap commits
  - each commit's result goes into `ops/builds.md`, with the last green commit at the very top

  — owner: *suggested Rome-agents (lane B)* — status: open

- **S1.16 Night APK on pop-os** (P0 · 0.5 h · needs S1.5, S1.14) — done when:
  - a D APK and its build log are on pop-os by 07:00
  - Rome pulls it to his laptop at 07:30 and installs it over USB with `quest-deploy.sh` once the Quest is there, or Nam builds his own, whichever happens first
  - the APKs have the token in them, so they never get posted anywhere public

  — owner: *suggested Rome-agents (lane B)* — status: open (only if S1.5 passes)

- **S1.17 dartiros night build** (P1 · 0.5 h · needs S1.9, S1.14) — done when: if dartiros already has 6000.2.9f1 with Android installed and the Quest is with him, he makes a D build at 05:30, runs only the self-test (5 min), and the result gets logged. Otherwise skip it, art comes first — owner: *suggested dartiros* — status: open

- **S1.18 First full build on the headset (08:45 gate)** (P0 · 0.75 h · needs S1.8, S1.12, S1.14) — done when:
  - it starts in passthrough, and you can see the hive, Beeswax and the captions
  - the self-test is all green, or there's a bug filed for every red item
  - logcat shows the Hive accepted the token
  - the Board shows the Quest with its client version and lines hash

  — owner: *suggested Nam* — status: open

- **S1.19 Fast loop** (P0 if Nam is on Windows · 0.5 h · needs S1.18) — done when: in Link play mode, with passthrough over Link, a tuning change shows up in under 2 min. If Nam's on a Mac, this task goes away — owner: *suggested Nam* — status: open

- **S1.20 Build log** (P1 · 0.25 h + ongoing · needs S1.18) — done when: every build gets a line in `ops/builds.md`: number, time, commit, type, changes, result — owner: *suggested Nam* — status: open

- **S1.21 Release candidates** (P0 · 1.5 h · needs the 18:00 freeze) — done when:
  - RC1 (R build) at 17:30
  - RC2 around 22:00, P0 fixes only
  - RC3 only if someone makes the call at 23:45 and it's built by 01:00
  - every RC passes one full run on the headset

  — owner: *suggested Nam* — status: open

- **S1.22 Build lock, 07:30 Sun** (P0 · 0.5 h · needs S1.21) — done when: the lock APK is installed, two private copies are archived plus one on the laptop, the Quest's at 100% by 08:30, and there's been a clean rehearsal on it — owner: *suggested Nam* — status: open

#### S2 Director, flow and tuning

- **S2.1 Station values** (P1 · 1 h · needs –) — done when: positions, hint and give-up times, the lift threshold and outcome lengths are all in `unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm/Tuning/station-values.json` before 03:45. S2.3 takes them as defaults — owner: *suggested Adityash* — status: open

- **S2.2 Offline beat jump (D builds only)** (P0 · 0.75 h · needs S1.11) — done when: A and B jump to the next and previous beat, pressing both thumbsticks at once resets, and none of it needs the Hive. Works in the Linux player and on the first D build — owner: *suggested Rome-agents (lane A)* — status: open

- **S2.3 GameTuning with live overrides** (P0 · 1.5 h · needs S1.9, S2.1) — done when:
  - the builder reads the asset and never overwrites it
  - a `tuning.json` in `persistentDataPath` (pushed over with adb) or a `tuning` message from the Hive overrides it
  - the Board reloads it, no rebuild
  - the list of Hive hosts lives here too, LAN `ws://<laptop-ip>` first

  — owner: *suggested Rome-agents (lane A)* — status: open

- **S2.4 Director against the live Hive** (P0 · 1 h · needs S10.2, S1.11) — done when: in the Linux player with fake-quest (muted), each jump from station 2 to what-if to T-0 logs exactly one clean `beat.set`; RESET GROUP goes back to `pre` with all hexagons off; timeouts actually fire — owner: *suggested Rome-agents (lane A)* — status: open

- **S2.5 12:00 milestone run** (P0 · 0.75 h · needs S3.1, S4.1–S4.4, S5.2, S8.3) — done when:
  - one unbroken run on the stopwatch, with placeholder art, covers the happy path at every station, plus FIRE from the Board, fire by voice, T-0 and the escape
  - Nam runs the Board as Operator, Rome coaches
  - bugs are filed

  — owner: *suggested Adityash* — status: open

- **S2.6 2-minute cut for the judges** (P1, or P0 if S16.1 says a judging slot is 7 min or less · 1 h · needs S2.3) — done when: Adityash has picked the beats, lane A has coded a Board preset from them, and the cut runs in 2:15 or less on autopilot and 2:30 or less on the headset — owner: *suggested Adityash (code: lane A)* — status: open

- **S2.7 Timing pass** (P1 · 0.5 h · needs S2.5) — done when: 3 timed runs land between 4:00 and 4:30 (runs by outside testers count too) — owner: *suggested Adityash* — status: open

#### S3 Hands and placement

- **S3.1 Hands on the headset** (P0 · 0.75 h · needs S1.18) — done when: the test cards come out at 10 of 10 pokes, 5 of 5 hive grabs and 5 of 5 fists; a pinch with your palm facing you doesn't open the system menu; the results are in the bug list — owner: *suggested Adityash* — status: open

- **S3.2 Hand fixes** (P1 · 1 h · needs S3.1) — done when: collider sizes, hover state, poke depth, ray length and the fingertip cursor have been adjusted, and in the next build someone trying it for the first time pokes a choice within 3 s — owner: *suggested Rome-agents (lane A)* — status: open

- **S3.3 Hive placement** (P0 · 1 h · needs S1.18, S14.2) — done when:
  - the Operator puts the hive on the taped spot in under 20 s, with a two-point table calibration using a controller tip (fallback: grab-and-drop)
  - headset off and back on, plus a recenter, have been tested
  - a re-place procedure that takes 10 s or less is written up in `pitch/demo-runbook.md`

  — owner: **Nam Nguyen** — status: in progress (NamTest is wired for MRUK hand-ray hive placement; adding Quest hand-tracking ray interaction for the start button and grab interaction for the Hive; headset/recenter testing pending)

- **S3.4 Headset off and on, focus loss** (P1 · 1 h · needs S1.18) — done when: taking the headset off mid-run pauses it and frees up the mic; putting it back on carries on from the same beat; and if hand tracking drops out, no beat gets stuck — owner: *suggested Rome-agents (lane A)* — status: open

- **S3.5 Session anchor** (P1 only if S3.3 shows drift after off and on · 1 h timebox · needs S3.3) — done when: a session-only OVRSpatialAnchor keeps the hive in place through taking the headset off and on and through a recenter. It isn't saved, so it needs no extra permission. If we don't need it, it gets cut — owner: *suggested Rome-agents (lane A)* — status: open

- **S3.6 Controller fallback** (P2, or P1 if S3.1 is under 8 of 10 in hall lighting · 0.5 h · needs S3.1) — done when: you can play through a full station with B/Y to talk and the trigger to poke — owner: *suggested Nam* — status: open

#### S4 Stations

- **S4.1 Smoker** (P0 · 1 h · needs S1.18, S8.3) — done when:
  - **By 11:30, once:** the right pick calms the swarm, hexagon 1 lights up, and you hear `smoker_settle`.
  - **By 16:00, both branches 3 of 3:** the wrong pick works too, angry swarm plus its line.

  — owner: *suggested Adityash* — status: open

- **S4.2 Lift and feed** (P0 · 1.5 h · needs S1.18, S3.1, S8.1) — done when:
  - **By 11:30, once:** you hear "Light, right?" and "Rocket fuel", and syrup lights up hexagon 2.
  - **By 16:00, both branches 3 of 3:** honey switches the window to sick and plays the foulbrood line; the hint comes at 10 s and the give-up at 20 s.

  — owner: *suggested Adityash* — status: open

- **S4.3 Wasps** (P0 · 0.75 h · needs S1.18) — done when: by 11:30, the guards hold at the smaller door and hexagon 3 lights up, and the bigger door plays `wasps_raid`; 3 of 3 by 16:00 — owner: *suggested Adityash* — status: open

- **S4.4 Heat** (P0 · 0.75 h · needs S1.18) — done when: by 11:30, the pebbles get the drinkers to land, the glow goes out and hexagon 4 lights up; 3 of 3 by 16:00 — owner: *suggested Adityash* — status: open

- **S4.5 Weightless lift** (P1 · 1.5 h · needs S4.2) — done when: pop height, spring, overshoot and the bees lifting off are tuned through `tuning.json`, and 3 outside testers react without any of them asking "is it stuck?" — owner: *suggested Adityash (code: lane A)* — status: open

- **S4.6 Station feel pass** (P1 · 1.5 h · needs S4.1–S4.4) — done when: every station can be reached sitting and standing, wrong buttons turn red and lock, it's clear how to try again, and a newcomer gets through all 4 stations without coaching — owner: *suggested Adityash* — status: open

#### S5 What-if, embers and the meadow window

- **S5.1 Fire without voice** (P0 · 0.5 h · needs S1.18) — done when: the talk press and release timers and the FIRE and PUT IT OUT buttons on the Board trigger the fire morph, embers follow 2.5 s after the line, and the fire goes out on command — owner: *suggested Adityash* — status: open

- **S5.2 Fire by voice** (P0 · 1 h · needs S5.1, S7.2, S10.2) — done when: "What if a fire starts?" gets a line in about 2 s, then fire and embers, and "Put it out" puts everything back to normal. Once by 12:00, 4 of 5 by 16:00 — owner: *suggested Rome* — status: open

- **S5.3 What-if while the meadow is sick** (P1 · 0.75 h · needs S10.2) — done when: the fire still works after the honey branch, in the Hive tests and in the Linux run — owner: *suggested Rome-agents (lane C)* — status: open

- **S5.4 Embers look** (P1 · 0.5 h · needs S5.1) — done when: colour, size and speed are easy to read against passthrough and in a Quest capture clip — owner: *suggested Adityash* — status: open

- **S5.5 Meadow window on the headset** (P1, but a visible stand-in is P0 · 1 h · needs S1.18, and S9.5 or S9.6) — done when: every scene shows the right clip or stand-in, LIVE holds 10 fps or more if the gate said GO, and switching between LIVE, CLIPS and OFF from the Board works 3 of 3 — owner: *suggested Nam* — status: open

- **S5.6 Frame budget for LIVE** (P1 · 0.75 h · needs –) — done when:
  - the relay scales frames down to 640×360 or smaller, at roughly quality 70
  - Unity decodes at most 10 frames per second and skips decoding on any frame where a line starts
  - no JPEG decoder on its own thread (that'd cost hours)
  - the self-test shows decoding takes under 4 ms per frame on the Quest

  — owner: *suggested Rome-agents (lane C)* — status: open

- **S5.7 Clean clip switching** (P1 · 1 h · needs S9.5) — done when: the window holds the last frame while the next clip gets prepared, and the next scene's clip is prepared on a second VideoPlayer. No black flash across 3 switches — owner: *suggested Rome-agents (lane C)* — status: open

#### S6 Endings

- **S6.1 T-0 text and Earth** (P0 · 0.5 h · needs S8.1) — done when: in the Linux run the caption matches `room_t0_earth` (Earth hangs over the horizon and doesn't rise), Earth sits over the horizon, and the window turns into the Moon — owner: *suggested Rome-agents (lane A)* — status: open

- **S6.2 T-0 on the headset** (P0 · 1 h · needs S1.18) — done when: the launch through the ceiling, the dimming, the Moon, the dusty crash back down and Beeswax in view all work once by 12:00 and 5 of 5 by 16:00 — owner: *suggested Adityash* — status: open

- **S6.3 Escape on the headset** (P0 · 0.5 h · needs S1.18) — done when: the door opens, the pour-out is easy to read in passthrough, `full_bloom` plays, and "Hive saved in m:ss" shows up. Once by 12:00, 3 of 3 by 16:00 — owner: *suggested Adityash* — status: open

- **S6.4 "Grievances filed: N"** (P1 · 0.75 h · needs S1.9) — done when: an autopilot run with 4 wrong picks shows N = 4, and the card takes the score from the Hive when there is one — owner: *suggested Rome-agents (lane A)* — status: open

- **S6.5 Level map wording** (P1 · 0.5 h · needs S8.1) — done when: tiles and labels match "Other hives have it worse" (farmland, rooftop, desert) — owner: *suggested Rome-agents (lane A)* — status: open

#### S7 Mic and push-to-talk

- **S7.1 Mic permission and warm mic** (P0 · 0.5 h · needs S1.14) — done when:
  - `install -g` grants the permission, with a request at runtime as the fallback
  - the mic is released on pause
  - `openMicOnStart=true` with 0.25 s of pre-roll stays in
  - a fresh install records without asking

  — owner: *suggested Rome-agents (lane C)* — status: open

- **S7.2 Push-to-talk on the headset** (P0 · 1 h · needs S1.18, S7.1, S10.2) — done when: holding a pinch gets a transcript onto the Board in under 2 s; the mic is off while Beeswax is talking; pressing talk during a line ducks it or cuts it off; 8 of 10 transcripts are right in a quiet room — owner: *suggested Rome* — status: open

- **S7.3 Hall-noise test (15:00)** (P1 · 0.75 h · needs S7.2, S14.3) — done when: Rome puts the headset on and says 20 phrases over crowd noise at hall volume, on our router. 80% or more right → voice is the main path. Under 60% → the Operator's buttons are the main path and voice is a bonus. Lane C adjusts the keyword list — owner: *suggested Rome* — status: open

- **S7.4 Voice latency on the Board** (P1 · 0.5 h · needs S10.2) — done when: the time from end of speech to the start of the line is logged and shown, with a median of 2.5 s or less on venue Wi-Fi — owner: *suggested Rome-agents (lane C)* — status: open

#### S8 Beeswax lines and prerender

- **S8.1 Lines generator** (P0 · 1 h · needs S1.9) — done when: `lines.json` and `stations.json` generate `GameLines.cs`, with an id check both ways; `room_pre` and `room_feed_lift_hint` resolve; 0 missing ids, captions identical, and it compiles — owner: *suggested Rome-agents (lane D)* — status: open

- **S8.2 Prerender v1 and import** (P0 · 1 h · needs S8.1) — done when:
  - all 77 lines are rendered on pop-os into `Assets/Resources/BeeswaxLines/`
  - an AssetPostprocessor sets mono, Vorbis, 22.05 kHz, preload and decompress on load
  - every clip loads during the intro, so no `Resources.Load` happens when a line starts
  - a batch check confirms the settings

  — owner: *suggested Rome-agents (lane D)* — status: in progress: **all 77 lines rendered in Harry's voice** (hive/out/unity); import lands with the Unity project

- **S8.3 Playback check** (P0 · 0.5 h · needs S8.2, S1.18) — done when: the Linux autopilot logs a clip for every line (no caption-only lines), and 10 sampled lines play at the right volume on the headset — owner: *suggested Rome-agents (lane D)* — status: open

- **S8.4 Punch-up options** (P1 · 1 h · needs –) — done when: `hive/lines/punchup-options.md` has 2 alternatives for every wrong-pick, map and T-0 line, and flags anything that touches the Facts in PLAN.md — owner: *suggested Rome-agents (lane D)* — status: open

- **S8.5 Pick the lines** (P1 · 0.75 h · needs S8.4) — done when:
  - by 14:00, Adityash's picks are in `hive/lines/lines.json`
  - only the text changes; ids only get added or renamed if `stations.json` changes along with them
  - Rome reviews only the lines flagged for facts

  — owner: *suggested Adityash* — status: open

- **S8.6 Re-render the changed lines** (P1 · 0.5 h · needs S8.5) — done when: `prerender.ts --only` has run over the changed ids, the generator has run again, the Hive tests are green, and the new clips are in the next build — owner: *suggested Rome-agents (lane D)* — status: open

- **S8.7 Voice freeze (16:00)** (P1 · 0.5 h · needs S8.6) — done when: the final render is through, every clip is within 2 dB of the median, and the manifest hash is frozen and matches the Hive's (S10.7) — owner: *suggested Rome-agents (lane D)* — status: open

- **S8.8 Captions** (P1 · 1 h · needs S1.11) — done when: captions use TextMeshPro with an outline, always face the player, and are readable from 1 m on the headset — owner: *suggested Rome-agents (lane A)* — status: open

#### S9 Orbis

- **S9.1 Prep the Orbis gate** (P0 · 1 h · needs –) — done when: `ops/orbis-gate.md` has the `smoke.py` checklist, plus the order we'll try the 18 scenes in, 2 safer fire prompts, a clip-capture script that passes against the mock, and a cost estimate — owner: *suggested Rome-agents (lane C)* — status: open

- **S9.2 Reactor key** (P0 · 0.5 h · needs –) — done when: we've picked up the key at the Visko table at 09:00, it's set in the environment on pop-os and on the venue laptop (never in git), and `/health` shows Orbis as on. Since we're at the table anyway, ask right then whether sponsor credits count toward the paid-AI cap — owner: *suggested Rome* — status: open

- **S9.3 Spend cap** (P0 · 0.25 h · needs S16.1, S9.2) — done when:
  - we know the 2026 cap by 10:30 and it's in the runbook
  - for context: the 2025 cap was $20, so about 34 min of Orbis at roughly $0.58 a minute, and 2 h of LIVE judging costs about $70
  - LIVE only runs during demo runs
  - CLIPS is the default for judging, unless sponsor credits don't count toward the cap

  — owner: *suggested Rome* — status: open

- **S9.4 Orbis gate (11:00)** (P0 · 1 h · needs S9.1, S9.2, S9.3) — done when:
  - `smoke.py` runs on a desktop over the judging path (relay on the venue laptop, our router)
  - it holds 10 fps or more for 5 min
  - a scene change is visible within 4 s
  - the fire prompt gets through moderation
  - a 2-minute spot check in the headset is part of the 12:00 run
  - GO or NO-GO and the network we used are logged

  — owner: *suggested Rome* — status: open

- **S9.5 Record the 13 demo clips** (P1, or P0 if NO-GO · 2 h · needs S9.2) — done when:
  - every clip is a 12 s ping-pong loop, encoded as:
    - H.264 Main, yuv420p
    - 1280×720 at most, 30 fps
    - a keyframe at least every 1 s
    - no audio, `+faststart`
  - the fake clips are replaced
  - `Assets/StreamingAssets/` isn't ignored for the clips anymore, so every machine that builds has them
  - the APK size has been checked

  — owner: *suggested Rome-agents (lane D)* — status: open

- **S9.6 Stand-ins if we don't have a key by 13:00** (P1, only if triggered · 1.5 h · needs –) — done when: meadow, sick, fire and Moon look clearly different in the headset, and the Devpost says the Orbis integration is built but didn't run live — owner: *suggested Rome-agents (lane A)* — status: open

#### S10 Hive and Board

- **S10.1 Who's editing `server.ts`** (P0 · 0.25 h · needs –) — done when: Rome has checked his own sessions first (the change was at 02:01). If it's still unclear at 03:30, the agents re-test every 20 min and take the first version that's stayed unchanged for 20 min and passes every test, `ops-pages.test.ts` included — owner: *suggested Rome* — status: done (Rome's polish workflow, finished)

- **S10.2 Deploy Hive v2.1 to pop-os** (P0 · 0.5 h · needs S10.1) — done when: rsync it over, then restart `beeswax-hive`; `/health` reports OK with 77 lines; the fake-quest smoke test passes; the Board has the station, fx and what-if buttons — owner: *suggested Rome-agents (lane C)* — status: **done 02:40**: v2.1 live, 77 lines, Board buttons, score, custom what-ifs

- **S10.3 Version check on connect** (P1 · 0.5 h · needs S10.2) — done when: the Board shows the Quest's client version and lines hash, and turns red if they don't match the Hive's — owner: *suggested Rome-agents (lane C)* — status: open

- **S10.4 Board vs. the real Quest** (P0 · 1 h · needs S1.18, S10.2) — done when: every button's been tried, FIRE, PUT IT OUT and RESET work, and Nam has run the Board once as Operator — owner: *suggested Rome* — status: open

- **S10.5 Venue laptop** (P1 · 1 h · needs S10.2) — done when: the Hive, the Orbis relay and Whisper run as services, sleep is off, and both the LAN `ws://` on a fixed IP and `beeswax-venue.drori.dev` answer — owner: *suggested Rome-agents (lane C)* — status: open

- **S10.6 Failover drill** (P1 · 0.5 h · needs S10.5, S1.18) — done when: we kill the Hive on the laptop mid-run, the Quest switches over to pop-os or offline mode, and the run still makes it to the end — owner: *suggested Rome* — status: open

- **S10.7 Redeploy at voice freeze and at build lock** (P1 · 0.5 h · needs S8.7) — done when: the Hive's lines hash matches RC1 and the lock APK, and S10.3 is green — owner: *suggested Rome-agents (lane C)* — status: open

#### S11 Art

- **S11.1 Art export spec** (P0 · 0.25 h · needs –) — done when: the README section "Where art goes" says:
  - FBX in meters, Y-up, transforms applied, +Z forward
  - Simple Lit or Unlit materials, no alpha-test wings
  - textures 1024 px at most, ASTC
  - the worker bee is one mesh, one submesh, with vertex colours
  - swaps go into `Art/<Slot>/<Slot>.prefab`, never into the scene

  — owner: *suggested Rome-agents (lane A)* — status: open

- **S11.2 Loader for art slots** (P1 · 1.5 h · needs S1.11) — done when: the builder uses `Art/<Slot>/<Slot>.prefab` if it exists and keeps the named transforms, otherwise the primitive. A test prefab in `Art/Hive` gets swapped in, and Door and DoorPoint keep working in the autopilot — owner: *suggested Rome-agents (lane A)* — status: open

- **S11.3 Worker bee as a pipeline test** (P1 · 1 h · needs –) — done when: 300 triangles at most, about 3.5 cm, +Z forward, exported to `Art/Bee/` by 03:30 — owner: *suggested dartiros* — status: temp art in progress: a workflow is generating a stylized low-poly set (Blender scripts in `art/blender/`) + downloading CC0 assets; dartiros replaces or keeps

- **S11.4 Bee import check** (P1 · 0.5 h · needs S11.3) — done when: by 04:15 the agents have batch-imported the bee and flagged any axis, scale, naming or submesh problems to dartiros in the Live log — owner: *suggested Rome-agents (lane A)* — status: open

- **S11.5 Beeswax** (P1 · 3 h · needs S11.4) — done when: 15k triangles at most, faces +Z, `mouth` scales on Y, `leftWing` and `rightWing` rotate on Z, `dustyRenderers` stays; exported by 06:45 — owner: *suggested dartiros* — status: open

- **S11.6 Hive** (P1 · 1.75 h · needs S11.4) — done when: 5k triangles at most; the `Door` pivot (scale.x is the width), `DoorPoint`, `SwarmCenter` and `HeatGlow` stay; exported by 13:15 — owner: *suggested dartiros* — status: open

- **S11.7 Swap batch 1 (13:30)** (P1 · 0.5 h · needs S11.2, S11.5, S11.6, S1.18) — done when: the bee, Beeswax and hive prefabs are in `Art/<Slot>/`, the Linux run passes, and they show up in the 14:00 build at the right scale on the table without the fps dropping — owner: *suggested dartiros* — status: open

- **S11.8 Portal frame** (P1 · 1.5 h · needs –) — done when: 3k triangles at most, `LivePane` and `FallbackPane` the same size, and `Embers` pointing outward along +Z — owner: *suggested dartiros* — status: open

- **S11.9 Rocket** (P1 · 1 h · needs –) — done when: 3k triangles at most, and `Seat`, `Perch`, `Flame` and `CountdownDisplay` stay. If it's not ready in time, go with CC0 or the placeholder — owner: *suggested dartiros* — status: open

- **S11.10 Last art swap (16:30)** (P1 · 0.5 h · needs S11.8, S11.9, S11.11) — done when: the portal, the rocket and the approved CC0 models are in the 17:00 build. Whatever isn't ready stays a placeholder — owner: *suggested dartiros* — status: open

- **S11.11 CC0 shortlist** (P2 · 1 h · needs S11.2) — done when:
  - CC0 or public-domain models are imported as prefabs for:
    - the hexagon panel
    - the smoker, honey jar, syrup bottle, deep bowl and pebble dish
    - the Moon and Earth (NASA textures, 1024 px at most)
    - the wasp, the map tiles, the score card and the choice buttons
  - colliders and `ChoiceButton` ids stay intact
  - the licences are listed
  - dartiros gives the OK in 10 min

  — owner: *suggested Rome-agents (lane A)* — status: open

#### S12 Sound

- **S12.1 SFX scaffold** (P1 · 1.5 h · needs S1.11) — done when:
  - the GameSfx cues are hooked up to director and station events:
    - the roar fading into a calm hum
    - wasp buzz
    - fire crackle
    - launch
    - silence on the Moon
    - hexagon light
    - poke
    - door
  - the Meta XR Audio spatializer is on
  - cues get ducked under Beeswax's voice
  - a muted Linux run logs every cue

  — owner: *suggested Rome-agents (lane A)* — status: open

- **S12.2 Sound files** (P1 · 0.75 h · needs –) — done when: about 10 CC0 sounds are trimmed, saved as mono Vorbis, named after their cue and sitting in `Art/Audio/`, with the licences listed. Only use SATIE if we enter the Attractor prize and the licence allows a public repo — owner: *suggested Rome-agents (lane D)* — status: open

- **S12.3 Mix on the headset** (P1 · 0.75 h · needs S12.1, S12.2) — done when: levels are set, the roar fades into a hum, the crackle follows the embers, the Moon is silent, and the voice always sits on top. The team signs off on it at rehearsal 1 — owner: *suggested Nam* — status: open

#### S13 Performance

- **S13.1 Render settings for passthrough** (P1 · 0.75 h · needs S1.10) — done when:
  - URP depth and opaque textures are off
  - no post-processing, no HDR, no soft particles
  - render scale is at 1.0
  - fixed foveation is on
  - nothing casts shadows

  — owner: *suggested Rome-agents (lane B)* — status: open

- **S13.2 Metrics from the first build** (P1 · 0.75 h · needs S1.18) — done when: the OVR Metrics overlay is on for every test, and we log fps, CPU and GPU over one full run on an R build with embers, the window and casting on. Targets: main thread under 8 ms, GPU under 11 ms, stale frames under 1% — owner: *suggested Nam* — status: open

- **S13.3 Performance fixes** (P1 · 1.5 h · needs S13.2) — done when: the next R build holds a steady 72 fps — owner: *suggested Rome-agents (lane A)* — status: open

- **S13.4 Re-check after the art swaps** (P1 · 0.5 h · needs S11.10) — done when: RC1 still holds 72 fps — owner: *suggested Nam* — status: open

- **S13.5 Overnight soak** (P1 · 0.25 h + 2 h unattended · needs RC2) — done when:
  - setup:
    - an autopilot loop runs 23:15–01:15 (stops at 01:00 if RC3 is built)
    - `am broadcast -a com.oculus.vrpowermanager.prox_close` keeps the headset running even though nobody's wearing it
    - display sleep is set to the max
    - logcat goes to a file on Rome's laptop that the agents keep an eye on
  - results: no crash, temperature OK, reconnects work
  - `automation_disable` gets run afterwards

  — owner: *suggested Nam* — status: open

#### S14 Venue, rehearsals and judging

- **S14.1 Mat print** (P0 · 0.1 h · needs –) — done when: tonight we confirm the mat print is ordered for Sat 08:00 pickup, or we order it — owner: *suggested Rome* — status: open

- **S14.2 Morning shopping run** (P0 · 1 h · needs S14.1) — done when: between 07:50 and 08:40 the mat's picked up and we've got power banks, a Quest battery pack, a travel router and a lanyard. No optional props — owner: *suggested Adityash* — status: open

- **S14.3 Travel router** (P1 · 0.5 h · needs S14.2, S10.5) — done when: the laptop has a fixed IP, the Quest reaches the Hive on the laptop over the LAN websocket, and latency is logged before the 15:00 tests — owner: *suggested Nam* — status: open

- **S14.4 Casting** (P1 · 0.5 h · needs S1.18) — done when: we've compared scrcpy, Meta cast and `/monitor` over the router and picked one that shows passthrough — owner: *suggested Nam* — status: open

- **S14.5 Runbook** (P1 · 0.5 h · needs S3.3, S10.6) — done when: the agents fill in the factual TODOs in `pitch/demo-runbook.md` and Adityash does the rest; the roles match the house rules; 0 TODOs are still open after rehearsal 1 — owner: *suggested Adityash (facts: lane E)* — status: open

- **S14.6 Rehearsal 1 (18:00, RC1)** (P0 · 1 h · needs S1.21) — done when: a full run, the 2-minute cut (do that one first if slots are 7 min or less) and two reset drills of 60 s or less are done with an outside player; times and bugs are logged — owner: *suggested Adityash* — status: open

- **S14.7 Rehearsal 2 (about 22:30, RC2)** (P1 · 1 h · needs S1.21) — done when: one clean full run and one clean cut, with the pitch and practice judge questions — owner: *suggested Rome* — status: open

- **S14.8 Rehearsal 3 (07:45 Sun, locked build)** (P0 · 0.5 h · needs S1.22) — done when: one clean run — owner: *suggested Adityash* — status: open

- **S14.9 Judging table** (P0 · 1 h · needs S14.8) — done when: by 09:45 we've got:
  - the taped-off spot
  - laptop services running
  - the router
  - the Quest charged, plus the battery pack
  - wipes
  - the casting screen
  - a spare cable

  — owner: *suggested Adityash* — status: open

#### S15 Video

- **S15.1 Capture test on first boot** (P1 · 0.25 h · needs S1.18) — done when: capture width, height and bitrate, plus foveation 0, are set with `setprop` (a reboot resets them); a 20 s clip has been pulled to the laptop; and passthrough shows in it — owner: *suggested Nam* — status: open

- **S15.2 Shot list** (P1 · 0.5 h · needs S16.1) — done when: by 17:30 the shots in `pitch/video-30s.md` are numbered. Walkthrough shots only if the rules require the walkthrough — owner: *suggested dartiros* — status: open

- **S15.3 Capture (18:45–21:15)** (P0 · 2.5 h · needs S14.6) — done when: we have at least 3 clean full runs, plus close-ups of the lift, the fire embers, T-0 and the score card. Nam does the Quest capture, Adityash plays, dartiros films on a phone and Rome runs the Board — owner: *suggested Nam* — status: open

- **S15.4 Ingest** (P1 · 1 h · needs S15.3) — done when: as clips come in, the files get pulled, with proxies, Whisper transcripts, beat timestamps from the logs, contact sheets and an index of usable takes — owner: *suggested Rome-agents (lane E)* — status: open

- **S15.5 Safety cut (21:30)** (P0 · 0.5 h · needs S15.4) — done when: a rough 30 s vertical cut, built mechanically from the beat timestamps, is uploaded unlisted and linked in the 23:00 draft — owner: *suggested Rome-agents (lane E)* — status: open

- **S15.6 30-second vertical video** (P0 · 3 h · needs S15.4) — done when: v1 is done at 23:30 and the final at 02:00 — owner: *suggested dartiros* — status: open

- **S15.7 Finish and upload** (P0 · 0.5 h · needs S15.6) — done when: captions are burned in, loudness is fixed in a separate audio render (no loudnorm in the concat), the video's uploaded unlisted, and it plays in a logged-out browser — owner: *suggested Rome-agents (lane E)* — status: open

- **S15.8 2-minute walkthrough** (P2, unless S16.1 says it's required · 2.5 h · needs S15.4) — done when: exported by 01:00 — owner: *suggested Adityash* — status: open

#### S16 Devpost, tracks and repo

- **S16.1 Rules and tracks** (P0 · 0.5 h · needs –) — done when: tonight the 2026 rules are in `pitch/tracks.md`, and anything marked "pending" gets confirmed at 09:00. The rules have to cover:
  - video length and orientation
  - required fields
  - the paid-AI cap
  - judging minutes per group
  - whether the walkthrough is required
  - whether code written before the event is allowed
  - whether we can still edit after submitting
  - what AI disclosure is required

  — owner: *suggested Adityash* — status: open

- **S16.2 Team name** (P0 · 0.1 h · needs –) — done when: a provisional name from the overnight votes is registered at 09:00, and the final one at 11:00 — owner: *suggested Adityash* — status: open

- **S16.3 Tracks** (P0 · 0.1 h · needs S9.4) — done when: a draft is in place at 09:00 and the final pick at 11:15. If the gate is GO: Visko and Social Good. If it's NO-GO: Social Good and Creative/Gaming — owner: *suggested Rome* — status: open

- **S16.4 Devpost invites** (P0 · 0.1 h · needs –) — done when: the invites go out tonight, everyone's accepted by 12:00, and we check again when we submit — owner: *suggested Rome* — status: open

- **S16.5 Devpost facts** (P1 · 1.5 h · needs –) — done when: the factual TODOs in `pitch/devpost.md` and `readme-paste.md` are filled in from the code (stack, architecture, what's live and what's fallback, paid AI cited), and the TODOs only a human can do are marked — owner: *suggested Rome-agents (lane E)* — status: open

- **S16.6 Devpost pass by hand** (P1 · 1.5 h · needs S16.5) — done when: the story, challenges, credits and per-track pitches are written and read out loud once — owner: *suggested Rome* — status: open

- **S16.7 Pitch script** (P1 · 1 h · needs S16.1) — done when: there's a 60-second and a 2-minute version, and we've done a timed read-through — owner: *suggested Rome* — status: open

- **S16.8 Screenshots and GIFs** (P1 · 0.5 h · needs S15.4) — done when: dartiros picks 5 images and they're uploaded — owner: *suggested Rome-agents (lane E)* — status: open

- **S16.9 Secret scan** (P0 · 0.5 h · needs –) — done when:
  - at 21:30, and again at 07:30 Sun (the agents commit all night), there's no Reactor, ElevenLabs, Unity or Hive token in the repo
  - the README and the MIT licence are there
  - no APK in the repo or on Devpost

  — owner: *suggested Rome-agents (lane E)* — status: open

- **S16.10 Draft and early submission** (P0 · 0.5 h · needs S15.5) — done when: the draft is saved at 23:00 with the safety cut, and a full submission goes in at 01:00 if Devpost allows edits until 09:00 — owner: *suggested Rome* — status: open

- **S16.11 Final submission (08:30)** (P0 · 0.25 h · needs everything) — done when: the confirmation is on screen and all four members are on the project — owner: *suggested Rome* — status: open

#### S17 Mentra Live miniapp

- **S17.1 Bee Scout miniapp** (P0 · 0.5 h · needs –) — done when: a button press works without a Quest scan beat, captures a photo, and speaks a cautious likely ID plus one conservation note; unclear photos ask for a retry; typecheck, flow tests and production build pass — owner: **Rome** — status: in progress.

- **S17.2 Field guide scan endpoint** (P0 · 0.5 h · needs S17.1) — done when: `field_guide: true` creates a field-only scan job at any beat, routes photos through vision rather than speech shortcuts, and sends no scan effects to Unity; documented without exposing a token — owner: **Rome** — status: in progress.

### Schedule

**Sleep** (non-negotiable; everyone sets two alarms):

| Who | Sat | Sun |
|---|---|---|
| Nam | 04:00–08:00 | 01:15–05:00 |
| Rome | 03:30–07:30 | 02:00–05:00 |
| Adityash | 03:45–07:45 | 02:00–06:00 |
| dartiros | 07:00–11:30 | 02:15–06:00 |

Nam wakes Rome at 05:00. Rome then wakes Adityash and dartiros at 06:00.

| Time | Quest | Nam | Adityash | dartiros | Rome | Agents |
|---|---|---|---|---|---|---|
| Sat 02–03 | Nam | S1.6; gets S1.7 | S16.1, S2.1 | S11.3 bee | S1.1, S1.2, S1.5 (02:45), S1.7, S10.1, S14.1, S16.4 | S1.3 → S1.4 (02:50) → S1.9; S0.1–S0.3; S11.1 |
| 03–04 | Nam | S1.8 (03:45 gate); kick off S1.9 import; sleep 04:00 | finish S2.1; sleep 03:45 | bee out 03:30 → S11.5 | sleep 03:30 | S1.10 ‖ S1.11; S8.1 → S8.2; S11.4 |
| 04–07 | – | sleep | sleep | S11.5 (S1.17 at 05:30 if it's set up); export 06:45; sleep 07:00 | sleep | S1.12, S1.13, S10.2, S1.14, S7.1, S2.2, S1.15, S6.1, S1.16, S8.4, S0.4 (07:00) |
| 07–08 | – | up at 08:00 | up at 07:45; S14.2 | sleep | reads S0.5 at 07:30; S1.16 handoff | S2.3, S11.2, S13.1, S5.6, S16.5 |
| 08–09 | Nam → Adityash 08:45 | S1.18 (08:45 gate), S15.1, S1.19 | S14.2 until 08:40; S3.1 | sleep | S10.4 with Nam; confirms S16.1 | fixes based on the remote log; S2.4 |
| 09–10 | Adityash | D builds at :00 and :30; S3.3 | S3.1, S4.1–S4.4 happy paths; S16.2 at 09:00 | sleep | 09:00 tables: S9.2, S0.6; S16.3 draft | S3.2, S9.1, S6.4, S6.5 |
| 10–11 | Adityash | builds; S14.4 | S5.1; S6.2, S6.3 once | sleep | S9.3 (10:30), S7.2 | S3.4, S5.3, S7.4, S12.1, S8.8 |
| 11–12 | Adityash | milestone build 11:30 | S5.2, S7.2 | up at 11:30; S11.6 | S9.4 (11:00); S16.3 final | S9.5 if we have a key; S11.11 |
| 12–13 | Adityash | runs the Board as Operator for S2.5 | plays S2.5; S0.7 | S11.6 | coaches the Board, cut list | files bugs |
| 13–14 | Adityash | builds; S13.2 (R build) | S8.5, S4.5 | S11.6 export 13:15 → S11.7 (13:30) | S9.6 call (13:00); checks S10.5; S16.7 | S8.6, S10.3, S3.5 if something drifts, S13.3 |
| 14–15 | outside testers with Adityash | builds; S14.3; 14:30 merge and fix | testers: S4.1–S4.4 3 of 3, S4.5, S4.6, S2.7 | S11.8 | S16.7 | S13.3, S12.2, S5.7 |
| 15–16 | Rome until 15:45, then Nam | S10.6 with Rome; S5.5, S12.3 | S2.6 picks, S14.5 | S11.9 | S7.3 (15:00), S10.6 | keyword tweaks, S2.6 code, S8.7 (16:00) |
| 16–17 | Adityash and dartiros from 16:30 | 16:30 merge and fix | S5.4, S4.6 | S11.10 (16:30) | S16.6 | S10.7, rehearsal cards |
| 17–18 | Nam | integration only; RC1 (S1.21) at 17:30; S13.4 | S14.5 | S15.2, edit the template | S16.6 | RC1 compile, autopilot, parity check |
| 18–19 | everyone | **FREEZE.** S14.6 as Operator | Mediator | Spotter | Host | bug triage |
| 19–21:15 | capture | S15.3 | player | phone camera | Board | S15.4 as clips come in |
| 21–22 | Nam | P0 fixes → RC2 at 22:00 | picks takes | S15.6 | S16.6 | S15.5 (21:30), S16.9, S16.8 |
| 22–23 | everyone from 22:30 | S14.7 | S14.7 | S15.6 | S14.7 pitch; S16.10 draft at 23:00 | runbook facts |
| 23–01 | soak | S13.5; RC3 call at 23:45; only builds RC3 if we decide to | S15.8 only if needed | v1 at 23:30 → v2 | S16.6; S16.10 early submit at 01:00 | keeps an eye on the soak; S15.7 on v1 |
| Sun 01–02 | Nam | installs RC3 if it got built; sleep 01:15 | notes on v2 | v2 | Devpost | P0 fixes only |
| 02–05 | – | sleep | sleep 02:00–06:00 | final export 02:00; sleep 02:15 | sleep 02:00–05:00 | S15.7 final upload, S16.8 |
| 05–06 | Nam | up at 05:00; smoke test; wakes Rome | sleep | sleep | puts the video links into Devpost | Linux regression on the lock candidate |
| 06–07 | Nam | lock candidate 06:30, 2 full runs | up at 06:00 | up at 06:00, packs | last Devpost check, laptop services | S10.7 parity check |
| 07–08 | everyone | S1.22 (07:30), S14.8 (07:45) | S14.8 | S14.8 | S16.9 (07:30), S14.8 | archives APKs privately |
| 08–09 | judging table | S14.9 | S14.9 | packs, helps | 08:15 final read-through; **S16.11 at 08:30** | – |

### Critical path
S1.5 or S1.8 (a licensed Android editor, 02:45/03:45) → S1.4 skeleton (02:50) → S1.9 + S1.11 merge and rig (~05:00) → S1.12 + S1.14 self-test and deploy script (~06:00; S8.2 voice and S10.2 Hive feed go in here too) → **S1.18 full build boots (08:45)** → S3.1 and S4.1–S4.4 happy paths (10:30) → S5.2 fire by voice (11:30) → **S2.5 milestone (12:00)** → S11.7 and S11.10 art swaps (13:30, 16:30) → RC1 (17:30) → rehearsal 1 (18:00) → capture (21:15) → safety cut (21:30) → final video (02:00) → build lock (07:30) → **submit (08:30)**.

### Top risks
1. **We don't have an Android build path that really works.** pop-os has never compiled for Android, and nobody has ever built the XR config.
   - We get two tries tonight: the S1.5 license gate at 02:45 and Nam's S1.8 skeleton at 03:45.
   - S1.10 clears out the launch risks we already know about: USE_SCENE, MRUK scene loading, and boundaryless sits behind a toggle now.
   - At the 08:45 gate, Link play mode is the fallback.
   - If the skeleton fails and we can't see why, try the Meta SDK version the mentors' samples use. Version 209 came out 4 days ago.
2. **We only have one headset.**
   - Nam builds while Adityash tests. D builds go out every half hour, and only from green commits.
   - With `tuning.json` and the beat jump, a feel fix takes a few seconds instead of a rebuild.
   - Test cards, the Link loop, and asking around for a second Quest buy us some breathing room.
   - Charge the Quest during every build.
3. **We get in each other's way during integration.**
   - One writer per path. The scene is a build output.
   - Settings only come from copy A or from Nam's bootstrap commit.
   - One shared keystore. If that goes wrong, uninstall and try again.
   - The token gets injected from `HIVE_TOKEN`.
   - The S1.15 check catches stray diffs in ProjectSettings and Packages.
4. **Beeswax goes quiet, says stale lines, or stutters.**
   - The two-way id check from S8.1.
   - The v1 prerender lands tonight.
   - Clips preload during the intro.
   - On connect, the lines hash gets checked against the Hive.
   - Voice freeze at 16:00.
   - Captions stay on the whole time, and "say a line" on the Board is the manual override.
5. **The venue: network, noise, heat and cost.**
   - LAN websocket first, over our own router. The relay runs on the venue laptop.
   - The Operator's buttons are always there, and the 15:00 noise test decides which path becomes the main one.
   - Sustained CPU/GPU levels, a soak with `prox_close`, and a battery pack.
   - The spend cap is set, and CLIPS is the default for judging.

Also keep an eye on: art that shows up late (CC0 or placeholder at 16:30), and the video (safety cut at 21:30, early submission at 01:00).

### Overnight agent queue (02:35 → 07:15)
- **Unity jobs**, in this order, one at a time per project copy: S1.3 → S1.4 (02:50) → S1.9 → S1.10 (copy A) ‖ S1.11 (copy B) → S1.12 → S1.14 → S2.2 → S1.15 → S6.1 → S1.16 (only if licensed) → S2.3 → S11.2 → S13.1 → S6.4 → S6.5 → S12.1 → S8.8.
- **Running alongside** (they don't get a Unity job of their own):
  - lane C: S10.1 rule → S10.2 → S1.13 → S7.1 → S5.6 → S5.3 → S9.1
  - lane D: S8.1 → S8.2 → S8.4
  - lane E: S0.1 → S0.3 → S0.2 → S0.4 (by 07:00) → S16.5 → runbook facts → S0.5 (07:15)
  - lane A, out of order: S11.1 before 03:30, and S11.4 as soon as the bee arrives
- **Rules:**
  - run the Android compile and the Linux autopilot after every merge
  - never deploy if a test fails
  - no secrets in the repo
  - audio off for every test
  - anything that didn't land goes at the very top of the report

---

## Concept v3.1 (CURRENT from Sat 05:15): AR first, everything delivered, the swarm's ultimatum

Replaces these parts of Concept v3: the logline, "Why it wins", "turns grey until it looks like the Moon" in the
future window, the demo script, "Choices are physical where we can make them", MUST/SHOULD/COULD/CUT, and the MDM
line under Sponsors. Everything else stays: Beeswax, the four stations and their facts, the voice what-if, the Moon
ending, the map.

**Logline.** Bees cut a window into your real wall and fly a sick hive through it onto your real table. Beeswax, the
union-rep drone, climbs out with the deal: let the hive die and angry swarms ravage the Earth, and you humans get the
Moon. Your evacuation rocket lands next to the hive with a four-minute boarding clock. You order the fixes and the
bees deliver them; wrong orders get repossessed back out through the wall. Fix four things and the evacuation is
cancelled. Then Beeswax takes your unused ticket, finds no flowers on the Moon and crash-lands back on your table.

**The stakes, said once and plainly**
- If we don't look after the bees, an angry swarm ravages the Earth and humans have to flee to the Moon.
  HONEYMOON-1 is now the humans' evacuation rocket, not Beeswax's.
- The player hears it in one sentence, sees it in one image, and gets one object:
  - Beeswax: "Let my hive die, and angry swarms ravage the Earth. You humans get the Moon."
  - On "angry swarms", a dark swarm pours out of the window, sweeps high over the player and settles in a band
    above the window. The room dims.
  - The rocket is flown in and planted next to the hive, reading EARTH → MOON · BOARDING 4:00.
  - "Four minutes, or you're on that rocket." The clock starts when the first station starts, not during the intro.
- During the stations the dark swarm above the window is the only stakes meter: about 30 more bees per wrong pick,
  fewer per fix. The room stays slightly dim.
- At the escape the swarm turns gold and flies home through the window, the room brightens ("the sun comes out"),
  and the rocket reads EVACUATION CANCELLED. Then Beeswax: "...Shame to waste a ticket. Non-refundable." The Moon gag
  plays as before. The Moon is a one-off bit; the game is about saving the hive.
- Tone: a threat from a petty union rep, not a disaster film. No people on screen, no crowds, nobody hurt. The
  swarm stays at least 0.6 m above the player's head and never goes behind them, and dive-bombs stop 40 cm from the
  face. A `comfort` switch (fewer bees, no overhead pass, no dives) is there for anyone who flinches.
- It's Beeswax's threat, not a fact: real swarms are docile. Pitch line: "Real bees don't attack. They quietly
  vanish. We made the stakes loud."

**Everything is delivered**
- Rule: nothing pops in. Every object comes in through the meadow window, carried by a crew of bees, and
  everything that leaves goes back out through it. Three things break the rule on purpose:
  - Beeswax climbs out of the hive (drones don't carry).
  - The wasps let themselves in ("Nobody ordered wasps").
  - The fire is something you ordered by voice.
- What gets delivered: the hive (hanging from chains of bees), the rocket, every station choice, the score card,
  the rain cloud that puts out the fire (SHOULD), and the map (COULD).
- How you order:
  - Each station's choices fly in together as Beeswax starts the prompt.
  - They park, perfectly still, in an arc above the front edge of the table.
  - The props are the buttons: poke the prop or its price tag.
  - The pick is stamped APPROVED or GRIEVANCE the moment you touch it.
- Wrong pick: the item does its bad thing. A red repo crew hauls it back out through the window while Beeswax
  explains, and the swarm above grows. Real fact: undertaker bees haul dead bees and debris out of the hive.
- Right pick: the crew sets it down on its spot on the real table (shadow, thunk, dust), and the leftover choices
  fly back out.
- Three hero flights get the polish: the hive arriving, the store-honey repo, and the fire-brigade rain cloud.
  Everything else flies in 1.5 s or less.
- If deliveries look bad on the headset, `delivery.mode` drops to `swirl` (items appear inside a spinning ring of
  bees) or to `off` (today's buttons). The show runs the same either way.

**AR first**
- The real table is the stage. The Operator aims at the table and pinches (the two-point edge calibration is the
  fallback). The hive snaps to the real surface height, and everything that lands casts a contact shadow.
- The meadow window hangs on the real wall behind the table when that wall is 3 m away or less. Otherwise it stands
  free 0.7 m behind the hive.
- The room's light is the stakes: passthrough dims when the swarm comes out and brightens when it goes home.
- Bees go behind your hands and the table (Depth API occlusion) if it passes the 13:00 frame-rate test. Bees land
  on the real table between beats.
- Room scanning (MRUK / Space Setup) is a stretch goal. Nothing in the show depends on it.
- Every AR piece has a switch in tuning.json, so the venue can turn it off without a rebuild.

**MUST / SHOULD / COULD / CUT** (replaces v3's)
- **MUST (RC1 17:30):** everything in v3's MUST, plus:
  - SIGN HERE as START
  - bees cutting the window, and the hive delivery
  - the stakes line, dark swarm, dim and rocket delivery
  - delivered choices with stamp, repo and return
  - contact shadows and the table height snap
  - swarm home and room brightening at escape
  - AR permissions
- **SHOULD (by 14:30, each behind a switch):** occlusion, window on the real wall, aim-and-pinch placement, bees
  landing on the table, the swarm as a meter, the rain-cloud fire brigade, the honey-jar struggle, the Orbis
  forecast clip.
- **COULD:** MRUK auto-placement, bees landing on an open palm, a boarding pass reading "PASSENGER: YOU", the map
  carried in, bees whipping under the table edge, embers bouncing on the table, window light on the table.
- **CUT:** the hornet (already cut), the swarm raiding through a crack in the wall, deliveries from real doors or
  windows, bees on other furniture, footage of families with suitcases, the extra delivery quips, per-grievance
  dimming, and "the meadow greys into the Moon".

**Facts to add**
- Festooning: bees hook their legs into chains, and why they do it is still debated.
- Drones don't forage and have no pollen baskets.
- Undertaker bees carry the dead and debris out of the hive.
- Bearding: bees cluster on the outside of a hot hive.
- Calm bees rarely sting (hedge this one).
- Not a fact: "angry swarms". That's Beeswax's threat.

**Sponsors and tech:** never enroll or register the Quest in any MDM (ManageXR, Meta Quest for Business). Meta bug
MAN-54 and the MQFB error `ERROR_PACKAGE_UNTRUSTED` both break scene apps. The Depth API and environment raycast are
now in use; MRUK is a stretch goal.

**Open question closed at 08:45:** Quest 3 or 3S, from `adb shell getprop ro.product.model`.

### v3.1 build tasks (V1–V26)

**Lanes for this work** (lane A keeps its current queue: S1.11, S1.12, S2.3, S3.2 and the rest).
- **Lane A2 (delivery and stakes)** owns:
  - the new `Assets/VirtualBeeSwarm/World/Delivery/**` and `World/Stakes/**`
  - announced small edits to `StationBase`, `IntroSequence`, `EscapeSequence`, `T0Sequence` and `GameDirector`
  - `SwarmSystem` until 11:00
- **Lane A-AR** owns:
  - the new `Assets/VirtualBeeSwarm/AR/**` (asmdef `VirtualBeeSwarm.AR`, Meta references as in the tech plan) and `World/Room/**` (core asmdef, plain C#)
  - announced edits to `HivePlacement`, `FutureWindow`, `Shaders/GameUnlit.shader` and `GameMaterials`
  - `SwarmSystem` from 11:00
- **Shared:** both sub-lanes edit `BuildGameScene` and `MetaRigWiring`. Keep each edit small, log it, and merge at :10 or :40.
- **Before 08:45:** only new files that aren't wired into the scene, plus lane B and lane D work.

Paths use the new names. In the staged copy at `unity/HoneymoonQuest/Assets/Honeymoon/` the same files carry the old names (`HoneymoonDirector`, `HoneymoonUnlit.shader`, and so on).

#### Settings and build (lane B)
**V1 · AR settings and permissions** · MUST · 0.5 h · depends: S1.10, S1.14 (edits both) · before 08:15
- **What:**
  - **`Editor/Setup/ProjectBootstrap.cs`:**
    - set `sceneSupport = Required`
    - keep anchor support Enabled and both Quest 3 and 3S as target devices
    - turn the Meta Occlusion and Session features on for Android **and** Standalone
    - keep the Unity OpenXR Meta Camera, Planes, Anchors, Meshing, Bounding Boxes and Raycast features off
  - **`GameBuild.cs`:** the aapt check **requires** USE_SCENE and USE_ANCHOR_API.
  - **`ops/quest-deploy.sh`:**
    - `pm grant dev.drori.virtualbeeswarm com.oculus.permission.USE_SCENE` (and `RECORD_AUDIO`)
    - then a `dumpsys package` check for `granted=true`
  - Only OVRManager requests permissions.
- **Done when:** running the bootstrap twice changes nothing; the Android compile has 0 errors; aapt shows both permissions; `dumpsys` shows both granted; no permission dialog appears on launch.

#### Delivery and stakes (lane A2, 7 h MUST, 1.75 h SHOULD)
**V12 · BeeDelivery core** · MUST · 2.5 h · depends: nothing (new file; wired after 08:45)
- **What:** `World/Delivery/BeeDelivery.cs`:
  - **Pool:** 96 bees in one instanced draw, with preallocated matrices and colours.
  - **API:** `Deliver(item, Pose to, style, crew, onLanded)`, `Park(item, slot, crew)`, `HaulAway(item, repo)`, `Return(item)`, `CutRing(windowPose, radius)`, `FinishAll()`.
  - **Styles:**
    - **Crown:** 4–12 bees in a ring of 12 cm or less above the item; the item hangs 4 cm below.
    - **Festoon:** the hive only, 6 chains of 8 bees.
  - **Path:**
    - Always starts at the window pane centre (`FutureWindow.PaneTransform`).
    - Cubic Bézier through (out 0.4 m along the window normal, up 0.2 m) and (target + up 0.3 m), eased with smoothstep.
    - Flight time `clamp(dist/speed, 1.2, 1.5)` s for crowns; 4.5 s for the hive.
    - The item swings like a pendulum: a spring on lateral acceleration, tilt capped at 20°.
    - Heavy items get a 7 cm dip at 45% and 75% of the path.
    - Exits shrink the item to 0.9 at the pane.
  - **Tints:** couriers orange-gold, repo crews red.
  - While a delivery is in flight, the hive swarm pulls its orbit in 30% (except in Angry mode).
  - The ring cut is 20 bees plus one LineRenderer trail over 2 s.
  - **`delivery.mode`:**
    - `swirl`: the item scales in inside a 0.5 s spinning ring of bees at its spot.
    - `off`: today's `SetActive` and buttons.
- **Done when:**
  - The F2 autopilot passes in `on`, `swirl` and `off`.
  - An autopilot step that sends a Board jump mid-hive-flight and mid-repo leaves no item in the air and no orphan bees.
  - The profiler shows 0 B of garbage per frame during deliveries, the pool is one draw call, and CPU is 0.15 ms or less on Linux.

**V13 · Dark swarm and dim arbiter** · MUST · 1.25 h · depends: V12 (pool conventions); edit `SwarmSystem` before 11:00
- **What:** a second `SwarmSystem` instance, `DarkSwarm`: dark red-brown bees at 5 cm, up to 150.
  - **New mode `Deck`:**
    - a band 1.2 m wide and 0.4 m deep above the window
    - 25–35° above the player's eye (30° at most on a 3S)
    - below `ceilingY − 0.2` when the ceiling is known
  - **New mode `Home`:** the bees turn gold, stream into the window pane, then hide.
  - **`PourOut`** takes a source point (the window). Its arc peaks at least 0.6 m above head height and at least 0.4 m in front of the face, never behind.
  - **Other calls:** `SetCount(n)` eases the size up or down. `Tendril()` sends 8 bees down to the table and back.
  - **Dim arbiter** `PassthroughDim.Request(key, amount)` / `Release(key)`:
    - the applied dim is the maximum of the open requests
    - `OvrPassthroughDimmer` is its only output
    - `T0Sequence` uses the key `t0` instead of calling `SetDim(0f)` on reset or exit (it does that today)
  - **`comfort` flag:** 60 bees, straight up to the band, Angry-mode dives off.
- **Done when:**
  - The autopilot logs swarm counts (hook 150 → meet 80 → +30 per wrong pick, −20 per fix → home 0).
  - It logs dim values (hook 0.25, stations 0.12, escape 0).
  - A T-0 reset no longer clears the stakes dim.
  - The logged band angle stays at 35° or less above the eye and within ±25° of the window direction.

**V14 · Hook rewrite, SIGN HERE and clock** · MUST · 1.5 h · depends: V12, V13, V6, V21 (lines)
- **What:**
  - **Pre:**
    - The SIGN HERE clipboard (4 bees, held still) calls the existing START path.
    - It is re-placed 0.45 m ahead of the head and 0.1 m below eye level on `OVRManager.HMDMounted` and whenever `pre` starts.
    - `room_pre` plays after 6 s idle while the headset is on.
  - **`ResetWorld`:**
    - The hive and rocket are hidden until delivered.
    - Beeswax is hidden inside the hive at `Hive/DoorPoint`. Today he is attached to `rocketPerch`.
    - The hive swarm stays hidden until touchdown.
  - **Hook sequence:** as in the script table. `FutureWindow.Open()` moves from `meet` to `hook`, and the local `forecast` plays on `room_hook_1`.
  - **`GameDirector`:**
    - `maxSeconds` hook 30, meet 13, smoker 50
    - the run clock and countdown start when `smoker` starts
    - display text: "EARTH → MOON · BOARDING m:ss", then "BOARDING DELAYED (UNION RULES)", "EVACUATION CANCELLED" and "PASSENGER: BEESWAX"
- **Done when:**
  - The autopilot logs START → first choice parked in 34 s or less.
  - A Board jump from pre to smoker leaves the hive, rocket, open window, Beeswax and the swarm band in their end states.
  - With delivery `off`, the same flow runs within 2 s of the same timing.
  - On the headset, 3 of 3 first-time viewers answer "the bees carried it" when asked where the hive came from.

**V15 · Stations through BeeDelivery** · MUST · 1.25 h · depends: V12, V5, S2.3
- **What:** in `StationBase`, `SetActive(props, true)` (today, on enter) becomes `Park` of every choice as a group when the prompt line starts.
  - **Arc:** 20–30 cm above the table's front edge, centred on the hive, choices at least 20 cm apart, within ±25° of gaze, completely still.
  - **Props:** normalized at runtime to a 12 cm box; colliders 16 cm or less. `ChoiceButton` becomes a price-tag plate parented under each prop.
  - **On the poke** (same frame):
    - APPROVED or GRIEVANCE stamp (scale 0 → 1.2 → 1 in 120 ms) plus a ka-chunk
    - `choice.made` is sent right away
  - **Wrong pick:** the existing consequence visuals, then `HaulAway(repo)` when the outcome line starts. Retry unlocks when the line ends, never waiting for the repo.
  - **Right pick:** `Deliver` to named spots under `TableAnchor/Spots/` (Smoker, Feeder, Door, Dish) with touchdown; the leftovers `Return`.
  - **Timeout:** the right crew delivers by itself.
  - The Board's `choice.request` uses the same path.
  - "nothing" is a crown carrying only a NOTHING tag.
  - The wasps fly out of the window on their own path.
  - **New placeholder props:** Swatter, SprayBottle, Crowbar, Reducer, NothingTag.
- **Done when:**
  - The autopilot passes with the wrong choice first, in all three delivery modes.
  - The S3.1 re-run on delivered props scores 10/10 pokes on the intended prop with 0 accidental picks.
  - The logged gap between a line ending and the choices accepting pokes is 0.3 s or less.
  - The Spotter's "is it stuck?" count is 0 over 3 runs.

**V16 · Escape and T-0 payoff** · MUST · 0.5 h · depends: V13, V3
- **What:**
  - **Escape:** `DarkSwarm.Home`; `Release("stakes")` over 2 s; the rocket reads EVACUATION CANCELLED; the score card is flown in (crown 6); the hive swarm settles on table and window spots if V10 is on.
  - **T-0:** Beeswax flies to `Perch` before `room_t0_1`.
    - Launch height = `RoomModel.ceilingY` (fallback `ar.ceilingY`) − table top − 0.05.
    - The Moon sits at the window pose + window normal × 0.4 m, toward the player. This fixes the README gap where the Moon could end up inside a wall.
- **Done when:** the autopilot passes; S6.2 and S6.3 pass 3/3 on the headset by 16:00; with the window on a wall, the Moon is never inside it.

**V17 · Honey-jar struggle** · SHOULD · 0.5 h · depends: V15
- **What:** the jar flies at 0.6 m/s with two dips and a 30% higher buzz. `room_crew_heavy` plays once over the flight. The repo is 8 bees, drags the jar 15 cm before lifting, and leaves at 0.8× speed.
- **Done when:** the Spotter logs a reaction to the jar on 2 of 3 runs.

**V18 · Rain-cloud fire brigade, and embers on the table** · SHOULD · 0.75 h · depends: V12
- **What:** on `fire_out` intent, a crown of 6 brings a cloud of a few spheres. It parks over the table for 2 s and rain particles fall. A second ember burst spawns above the table, because embers from a window 2–3 m away don't reach it.
- **Done when:** the fire → put-out sequence shows the cloud in 4 of 5 voice runs (S5.2) and in Board-button runs.

**V19 · Swarm as the meter, plus a hint** · SHOULD · 0.5 h · depends: V13, V15
- **What:**
  - Each grievance: `SetCount(+30)` and `Tendril()`.
  - Each fix: −20.
  - The first grievance plays `room_strike_lookup`.
  - After 8 s idle, the correct crew leans toward the hive with an eager buzz.
- **Done when:** the autopilot logs counts per pick; the Spotter sees testers look up after the first grievance on 2 of 3 runs.

#### AR (lane A-AR, 3 h MUST, 4 h SHOULD, 1.5 h COULD)
**V2 · AR self-test lines** · MUST · 0.5 h · depends: S1.12
- **What:** the `[VBS][AR]` lines from 3.5. The 08:45 build reports only the model and USE_SCENE.
- **Done when:** a Linux player prints every line as `n/a` with no errors; a D build shows them on the Board; 3 vs 3S is logged in PLAN at 08:45.

**V3 · RoomModel and sources** · MUST · 1 h · depends: nothing (new files)
- **What:** `World/Room/RoomModel.cs`, plain C# in the core asmdef.
  - **Contents:**
    - table top: to-world matrix, half extents, height
    - one window wall: point, normal, right, half width
    - floor height, and ceiling height (NaN if unknown)
    - 24 table landing spots, outside the footprints of the hive, stations, arc and rocket
    - 8 wall spots around the window, if there's a wall
  - **Sources:**
    - `FakeRoomSource`: the desktop FakeTable plus a wall 1.5 m behind it
    - `PlacementRoomSource`: the table from the `HivePlacement` result, with a default footprint of 1.0 × `ar.tableDepth`, and the wall from V8
- **Done when:** an EditMode test shows every spot on the table with none inside a footprint; the Linux autopilot passes with the fake source and logs the model.

**V4 · Table height snap** · MUST · 0.5 h · depends: V1, S1.18
- **What:**
  - Add one `EnvironmentRaycastManager` at `Systems/AR/EnvironmentRaycast`, enabled only in `pre`.
  - After any placement, cast down from 0.3 m above the hive centre.
  - If `normal.y > 0.9` and the hit is within 5 cm of the placed height, set the height to the hit.
- **Done when:** on a D build, a fingertip touching the real table touches the plate within 1 cm, 3/3; on Linux it is a no-op with no errors.

**V5 · Touchdown juice** · MUST · 0.5 h · depends: nothing
- **What:** `World/Room/Touchdown.Play(item, weight)`, called by BeeDelivery when an item lands:
  - a blob shadow (`_Occlude=0`), opacity 0 at 40 cm rising to 0.45 at contact
  - a squash of (1.06, 0.92, 1.06) for 70 ms
  - `Emit(8)` from one shared dust ParticleSystem
  - a thunk whose pitch drops with weight
- **Done when:** every set-down in the autopilot logs a touchdown; the Spotter rates the hive "on the table" 3/3.

**V6 · Forecast window override** · MUST · 0.5 h · depends: nothing
- **What:**
  - `FutureWindow.PlayLocal(scene, seconds)` and `StopLocal()`: an override pane in front of the live pane, with its own VideoPlayer and RenderTexture playing from `StreamingAssets/<scene>.mp4`.
  - Fallback chain: `forecast` → `smoker_angry` → a procedural dark-amber flicker (a new `forecast` case in `SceneColors`).
  - It stops on reset or a beat jump. It is never sent to the Hive or the live Orbis stream, and needs no protocol change.
- **Done when:** a Linux run shows the override at hook, then the Hive's scene; with no clip, the tint shows.

**V7 · Aim-and-pinch placement** · SHOULD · 1 h · depends: V4
- **What:** `AR/InstantPlacement.cs` (code as in the tech plan):
  - A ghost DELIVER HERE plate follows the hand ray, or head gaze when hands aren't tracked.
  - It uses `PlaceBox(0.6, 0.45, 0.3)` and requires `normal.y > 0.9`; the ghost tints green or red.
  - A right pinch calls the new `HivePlacement.PlaceAt(Pose)` (about 5 lines), then the raycast manager is disabled.
  - Two-point calibration and grab stay as overrides.
  - Session anchor (S3.5) only if S3.3 shows drift.
- **Done when:** the Operator places in 10 s or less, 3/3, at the work table and at the venue at 16:00; the S3.3 runbook is updated.

**V8 · Window on the real wall** · SHOULD · 0.75 h · depends: V3, V7 or V4
- **What:**
  - Reparent `FutureWindow` from `TableAnchor` to a new `WallAnchor`.
  - After placement, cast horizontally from the eye along the player→hive direction.
  - **Accept the wall if:** the hit is within 3 m of the hive, within 20° of that direction, `|normal.y| < 0.2` and normal confidence > 0.5.
  - **Placement:**
    - centre height `clamp(tableTop + 0.55, 1.2, 1.7)`
    - 4 cm off the wall, rotated `LookRotation(-normal)`
    - pane width `clamp(0.5 × distance, 0.9, 1.6)` m
  - **Otherwise free-standing:** 0.7 m behind the hive, 1.0 m wide, raised.
- **Done when:** at the work table the window hangs flush on the wall, facing the player, 3/3; aiming into open space gives the free-standing window; the fake source covers both cases on Linux.

**V9 · Depth occlusion** · SHOULD, high · 1.5 h · depends: S1.18 (base shader verified in both eyes), V1
- **What:**
  - **Shader:** the `GameUnlit.shader` edit from the tech plan (multi_compile, META_DEPTH macros, `_Occlude` toggle), with `_EnvironmentDepthBias` as a **per-instance** property.
  - **Manager:** add `EnvironmentDepthManager` disabled (one instance; `RemoveHands=false`). `ArBootstrap` enables it if `IsSupported`. `DepthControl` maps `ar.occlusion`; `off` disables the manager.
  - **Materials:** `_Occlude` and bias as in 3.2.
- **Done when:**
  - **G1:** on the 10:30 D build with `soft`, a hand hides the bees in both eyes, and the hive base doesn't flicker against the table.
  - The Linux autopilot passes and the visuals are unchanged with occlusion off.
  - The default is set by V23.

**V10 · Bees land on the table** · SHOULD · 0.75 h · depends: V3, V13 (`SwarmSystem` handed over at 11:00)
- **What:**
  - `SwarmSystem` gets the `RoomModel` and preallocated per-bee state arrays.
  - Bees can't sink below table top + 1 cm inside the table footprint.
  - In Idle and Calm, every 4th bee takes off, flies to a spot, and lands for 2–5 s with its wings still.
  - At the escape, 60% land on table and window spots.
  - Landed bees get bias 0.06 per instance.
- **Done when:** the autopilot passes; the swarm costs 0.2 ms or less for 150 bees in the Linux profiler; landed bees float 1 cm or less on the headset, 3/3.

**V11 · MRUK spike (and the EffectMesh debug overlay)** · COULD · 1.5 h timebox · depends: every SHOULD green at 13:00
- **What:** `MrukRoomSource` and `RoomPlacement` per the tech plan, with these settings:
  - `LoadSceneOnStartup=false`, `EnableWorldLock`, inactive on Linux, no Space Setup request in R builds
  - **Table:** the nearest TABLE volume with its top 0.5–1.2 m high, placed via `PlaceOnEdge`
  - **Window wall:** the critic's rule from V8; drop the WINDOW_FRAME preference
  - **Ceiling:** height from the ceiling anchors
  - Space Setup must be run at the table first.
- **Fallbacks:** any load result other than Success, or `-10035`, `-2` or "not trusted" in logcat, leaves V7 as the path.
- **Done when:** MRUK returns `Success` and auto-places within 3 cm, 3/3, at both the work table and the venue. Otherwise `ar.placement=raycast` stays the judging default.

#### Hive (lane C)
**V20 · Score clock and comment** · MUST · 0.25 h plus a redeploy · depends: V21
- **What:**
  - In `hive/src/server.ts` `beatEntered`, `hook` resets the state and sets `clock_start = null`. Today it starts the clock.
  - Remove `meet` from `ON_CLOCK`, so `smoker` starts the clock. Fix the observe text and the tests.
  - Change the `BEATS` comment in `protocol.ts` from "fix it or we move to the Moon" to "fix it or the swarm takes the Earth".
  - Redeploy so `/health` reports the new line count (it shows 77 today).
- **Done when:** the Hive tests are green, and in an online smoke run the Hive's score text matches Unity's card within 1 s.

#### Lines (lane D) and the re-render list
**V21 · Final lines, render and import** · MUST · 1 h · depends: nothing · start now and finish by 08:15

Text edits to `hive/lines/lines.json` (the LINES pass already applied the rest):

| id | Now | Final |
|---|---|---|
| `room_hook_1` | "If my hive dies, Local Twenty Thousand goes on strike. Bees don't picket. We swarm." | "Let my hive die, and angry swarms ravage the Earth. You humans get the Moon." (14 words) |
| `room_meet_2` | "...Fix them in four minutes and your flight's cancelled." | "Four things are killing my hive. Four minutes, or you're on that rocket." (13 words) |
| `room_strike_lookup` | "Hear that? Look up. Every grievance makes the strike bigger." | "Hear that? Look up. Bees don't picket. We swarm." (8 words, first grievance only) |
| `ear_hint_smoker` (glasses batch, low priority) | "Psst. The smoker. Squeeze it." | "Psst. The smoker. Poke it." |

**Lines to remove** (added an hour ago, referenced by nothing; or tag them `cut` and don't render them):
- `room_stakes_forecast` (merged into `room_hook_1`)
- `room_delivery`, `_2`, `_3`, `_4`
- `room_crew_heavy_2`, `_3`
- `room_repo`, `_2`, `_3`
- `room_return`, `_2`
- `room_strike_grows`, `_2`, `_3`

**Test edits in `hive/test/ai-lines.test.ts`:**
- Line 55: `toContain("We swarm.")` becomes `toContain("ravage the Earth")`.
- Lines 57–58: assert the new `room_meet_2` text exactly, and drop or capitalize the "four minutes" check.

**Re-render with `bun scripts/prerender.ts --only …`** (from `hive/`). That's 15 lines:
- **With `--force`, 9 lines** (the old mp3s exist in `hive/out/unity`, checked): `room_hook_1`, `room_hook_2`, `room_meet_2`, `room_lift_light`, `room_feed_rocket_fuel`, `room_wasps_intro`, `room_whatif_out`, `room_escape`, `room_t0_1`.
- **New, 6 lines:** `room_pre`, `room_hook_carry`, `room_strike_lookup`, `room_crew_heavy`, `room_smoker_hand`, `room_fact_festoon`.
- **Not rendered:** everything removed above, plus `room_meet_window` and `room_meet_stat` (off the demo path).

**After rendering:**
- Regenerate `GameLines.cs` (S8.1). Today's `HoneymoonLines.cs` captions are stale for `room_pre`, `room_hook_1`/`_2`, `room_meet_2`, `room_wasps_intro`, `room_whatif_out`, `room_escape`, `room_t0_1`, `room_whatif_prompt` and `room_map`.
- Import the mp3s into `Assets/Resources/BeeswaxLines/`.
- Rome fact-checks `room_hook_carry`, `room_fact_festoon` and `room_smoker_hand`.

**Done when:** the Hive suite is green; the 9 forced mp3s are newer than the `lines.json` edit; the autopilot logs a clip for every line on the path (S8.3).

#### Orbis window scenes (Rome in the sandbox, lane D files)
**V22 · Forecast and evacuation clips; moon prompt fix** · SHOULD · Rome 0.5 h, lane D 0.25 h · depends: S9.2 (the Reactor key)
- **Don't add keys to `hive/lines/orbis_scenes.json`.** `ai-lines.test.ts:159` and `contract-v21.test.ts:251` require that it hold exactly the protocol's `SCENES`. A new scene would need protocol v2.2, the C# mirror and the relay.
- **Instead:** create `hive/lines/orbis_local_scenes.json`. These are Unity-only clips, never streamed live. They follow the same prompt rules.
- **Record:** `cd hive/orbis-relay && uv run python record_clips.py --scenes ../lines/orbis_local_scenes.json` (the `--scenes` flag is supported). Encode per S9.5 and copy into `StreamingAssets/`.

```json
{
  "forecast": {
    "prompt": "A faded grey meadow with drooping wildflowers and a white wooden beehive on a low stand in the center, with bare oak trees under a dark amber sky. An enormous swirling cloud of honeybees fills the sky, and on a far hill a tall silver rocket stands on a launch tower with its lights on. Wide shot, eye-level, static camera, deep depth of field.",
    "seed": 20000, "clip": "forecast.mp4"
  },
  "forecast_b": {
    "prompt": "A faded grey meadow with drooping wildflowers and a white wooden beehive on a low stand in the center, with bare oak trees under a dark, stormy amber sky. On a far hill a tall silver rocket stands on a launch tower with its lights on. Wide shot, eye-level, static camera, deep depth of field.",
    "seed": 20000, "clip": "forecast_b.mp4"
  },
  "evacuation": {
    "prompt": "A faded grey meadow with a white wooden beehive on a low stand in the center, with bare oak trees under a dark amber sky full of swirling honeybees. On a far hill a tall silver rocket lifts off from its launch tower on a bright column of white exhaust, rising toward a pale full Moon. Wide shot, eye-level, static camera, deep depth of field."
    , "seed": 20000, "clip": "evacuation.mp4"
  }
}
```

**Using the clips:**
- `forecast` is the "Earth if we lose" scene. It's a 12 s ping-pong loop played at 0:14–0:25. Use `forecast_b` if Orbis won't draw the bee cloud; the 3D swarm supplies the cloud anyway.
- `evacuation` is COULD. It plays once with no ping-pong (a launch played in reverse looks wrong) at 0:20–0:25, just before the 3D rocket comes out of the window.
- **None of these show people.** The critic's taste cut stands.

**One edit to `orbis_scenes.json`** (a fact fix, matching `room_t0_earth` and S6.1). Apply it before S9.5 records `moon`:
- `moon`: "The blue and white Earth rises over the horizon into a black sky full of stars." → "The blue and white Earth hangs low above the horizon in a black sky full of stars."
- No other scene prompts change. `smoker_angry` (darkening sky, cloud of agitated bees) doubles as the forecast fallback clip.

**Done when:** `forecast.mp4` plays at the hook on the headset; the moon clip is re-recorded with the new prompt; the contract and ai-lines tests are green.

#### Humans
- **V23 · Frame-rate A/B (Nam, MUST, 0.5 h, 13:00):**
  - R build, OVR Metrics, 60 s of the angry smoker swarm plus the dark swarm, deliveries and embers.
  - Run it with occlusion off, hard and soft, and apply the rule in 3.4.
  - Add depth to the S13.5 soak test.
- **V24 · Headset checks (Adityash, about 1 h spread through the day):**
  - G1 and G3 at 09:30 and 10:30
  - the S3.1 re-run on delivered props
  - the "is it stuck?" count
  - whether testers look up after the first grievance
  - whether `comfort` mode needs to be on
- **V25 · Props (dartiros, about 1.5 h, by the 16:30 art swap):**
  - swatter, spray bottle, crowbar, reducer block, clipboard, rain cloud, price-tag plate, and APPROVED/GRIEVANCE stamp textures
  - each fits a 12 cm box and uses `VirtualBeeSwarm/Unlit`
- **V26 · Venue check (Nam and Adityash, 0.5 h, 16:00):**
  - wall or free-standing window at the judging table
  - the swarm band in view
  - aim-and-pinch working in hall light
  - set the flags and write them into `pitch/demo-runbook.md`

**Does it fit?**

| | MUST | SHOULD | COULD | Window |
|---|---|---|---|---|
| A2 | 7 h | 1.75 h | — | 05:30–14:30 (9 h). SHOULDs are cut first if late. |
| A-AR | 3 h | 4 h | 1.5 h | 05:30–14:30. MRUK only if green at 13:00. |
| B / C / D | 0.5 / 0.25 / 1 h | D 0.25 h | — | B by 08:15; D lines by 08:15; C by 09:30 |

---

---

## Concept v3 (base; v3.1 above takes priority where they differ): Virtual Bee Swarm — Save the Hive (or the Bees Leave for the Moon)

Best of both: **Adityash's hive escape** (4 stations, the live Orbis "future window" steered by voice, practical beekeeping, a score, a map of more hives) as the spine, plus **HONEYMOON** (Beeswax the union-rep drone, the "we're leaving for the Moon" stakes and theme twist, the hornet hum, the Moon ending). The full v2 write-up is in git history if you want to borrow more.

**Logline:** Beeswax's hive is falling apart and the union has voted: fix it in four minutes or the whole colony moves to the Moon. You're in the Quest with the hive on your real table. Four things are killing it. Every choice you make shows up live in a window into the meadow's future, and you can *ask* that future questions out loud.

**Beeswax** is a stingerless **drone** ("no stinger, no job, so they made me union rep") and shop steward of **Local 20,000**, for the ~20,000 bee species. Loud, petty, keeps a spreadsheet, talks in union procedure: "Motion carried." "Grievance filed." "Noted for the minutes." "I'll allow it." His rocket, **HONEYMOON-1**, is parked next to the hive, already fueled with honey.

**Why it wins:**
- One-sentence goal (fix four things, the door opens) with real stakes (or they leave for the Moon), and a score: "Hive saved in 2:47."
- **Visko's dream demo:** a live Orbis world that changes direction mid-stream because of what you did and what you *said*, including the fire that spills embers into the room.
- Three lunch-table moments: **the honey mistake** ("store honey can kill a hive?!"), **the fire** ("I said 'what if a fire starts' and the window caught fire and embers came into the room"), and **"TAKE ME TO THE MOON!"** (he goes anyway, finds no flowers, comes back).
- Real beekeeping, true facts, and a hint that there's a lot more game (six more hives).

### The future window (live Visko Orbis)
A glowing portal next to the hive shows a **live Orbis stream of the meadow's future**. Good choices make it settle and bloom; bad ones turn it sick and grey. Grey enough and it starts to look like the Moon ("That's your yard, to a bee, without us."). The player can steer it by voice ("What if a fire starts?" / "Put it out").
- **Reality check:** Orbis prompt changes take **2–4 s** to show up (a morph, not a cut). Beeswax always has a line ready to cover the wait, and **every window state has a recorded fallback clip** that plays if the stream isn't up. Test the "fire" prompt in the Reactor sandbox early (moderation can end a session).

### Demo script (~4:15)

| Time | Beat | What happens |
|---|---|---|
| 0:00–0:15 | Hook | The hive sits on the real table; HONEYMOON-1 is parked next to it with a countdown. Beeswax: "The union voted. Fix my hive in four minutes or we're moving to the Moon." |
| 0:15–0:35 | Meet Beeswax + the future window | Beeswax flies in: "Four things are killing my hive. Fix them and the door opens." The portal opens beside the hive, showing the meadow live. Voiceover (video only): "That window is a live Visko Orbis stream. It shows the future of every choice you make." |
| 0:35–1:05 | **Station 1: Smoker** | Angry bees swarm out. Choices: swat, spray water, smoker. Smoker → the bees calm down, first hexagon lights up. Window: the meadow settles, bees head out to forage. Beeswax: "Smoke covers up our alarm signal and makes us fill up on honey. Also, our alarm smells like bananas. Don't eat one near my house." |
| 1:05–1:45 | **Station 2: Tip the hive, then feed it** | "Is there enough food in there?" The player tips the hive with their hands and it lifts way too easily. "We're starving." Choices: store-bought honey jar or sugar syrup. Honey → the window shifts *mid-stream* and the colony gets sick: "…Grievance filed. Store honey can carry a disease that wipes out hives. Spores last for decades." Syrup → the scene recovers, second hexagon. (For the video, show the honey mistake first.) |
| 1:45–2:20 | **Station 3: Wasps at the door** | Wasps buzz at the entrance. Choices: make the entrance bigger or smaller. Smaller → guard bees block the wasps, third hexagon. Window: the wasps give up. Beeswax: "Smaller door, fewer guards needed to hold it." **If ahead (+30 s):** a giant hornet forces its way in anyway → make a **fist** to ball the swarm around it → "Now HUM!" (the Quest mic hears the hum) → 46 °C → "STOP, you're cooking the girls!" Japanese honeybees really do this. |
| 2:20–2:55 | **Station 4: Heat wave** | The hive glows red. Choices: deep bowl of water, shallow dish with pebbles, or nothing. Pebbles → bees land and drink, fourth hexagon. Beeswax: "We spread water drops in the hive and fan our wings. Bee air conditioning." |
| 2:55–3:20 | **The "what-if" bang** | The player asks out loud: "What if a fire starts?" Beeswax: "Don't. Say. That." The window's meadow catches fire (2–4 s morph), then **3D embers spill out of the portal into the real room** (Unity VFX, instant). "Put it out!" → the stream recovers. This is the clip people remember and Orbis's real selling point: steering a live world with your voice. |
| 3:20–3:40 | **Escape** | All four hexagons glow, the hive door opens, the swarm pours out into the room, and the window grows into a full forest bloom. Score card: "Hive saved in 2:47." Beeswax: "Motion carried. The union votes to stay." |
| 3:40–4:15 | **T-0: Take me to the Moon** | "…I'm still going. Ticket's non-refundable." "TAKE ME TO THE MOON!" HONEYMOON-1 launches with only Beeswax aboard. The window swells into the Moon: grey dust, Earth rising. "…Not one flower. Only planet with flowers." He crashes back onto the real table, dusty. "Same time tomorrow? There are six more hives." A map flashes with locked levels (farmland, city rooftop, desert). |

- **If running long:** skip the hornet hum first. Never cut the honey mistake, the fire, or the Moon.
- **Choices are physical where we can make them:** grab the smoker and squeeze, lift/tip the hive with your hands, pick up the jar. Floating buttons are the fallback.

### Facts (all true, each tied to an action)

- Smoke masks the alarm pheromone (which smells like bananas) and makes bees gorge on honey, so they calm down → **Station 1**.
- Beekeepers "heft" the hive to check food stores. Feed **sugar syrup**, never store-bought honey: it can carry American foulbrood spores, which stay viable for decades → **Station 2**.
- An entrance reducer makes the door easier to defend: fewer guards are needed to hold it → **Station 3**.
- Japanese honeybees kill giant hornets by balling around them and heating them to ~46 °C; the bees survive a few degrees more (Ono et al., *Nature* 1995) → **hornet hum** (if ahead).
- Bees need shallow water with landing spots (they drown in deep water), and they cool the hive by spreading water and fanning their wings → **Station 4**.
- Optional opener stat: US beekeepers lost more than half their managed colonies in one recent year (Auburn-led survey, 2024–25). Hedge every number.

### Glasses (Mentra Live): Bee Scout field guide
Mentra Live has no display, so its independent mode uses one button press to capture a plant or pollinator and speaks a likely ID plus one bee-friendly observation or action. It says when a photo is too unclear for a confident ID and avoids claiming species, native status, or safety without evidence. The same miniapp can still play ear asides and send the scout's plea during the Quest demo; field guide scans do not trigger Quest effects.

### Sponsors
- **Visko Orbis (via Reactor):** the future window, steered live by choices and voice. **Go/no-go Sat 11 AM** for live; recorded clips per window state are the floor either way.
- **Attractor Labs SATIE:** hive mood (angry roar → calm hum), the fire, the wasps, the launch, the Moon's silence.
- **ManageXR:** depends on how far the glasses get. **Do NOT enroll our only Quest in ManageXR** (Meta bug MAN-54 breaks MRUK/scene apps under MDM, and enrollment needs a factory reset).
- Tracks: **Visko** (strongest fit now) + **ManageXR** if the glasses earn it, otherwise **Social Good**. Confirm on the dashboard.

### MUST / SHOULD / COULD / CUT
- **MUST (freeze Sat 6 PM):** hive on the real table, Beeswax + pre-rendered lines + captions, 4 stations with choices + hexagons, the future window (live Orbis **or** recorded clips per state) reacting to each choice, voice what-if (fire + put it out) with an operator button as backup, Unity embers out of the portal, escape + score card, T-0 Moon ending, locked-levels map, Beat Board. **Unity is the only beat clock** (offline timers finish every beat).
- **SHOULD:** hornet hum (pitch-detected), physical choices (squeeze smoker, tip hive, grab jar), SATIE moods, glasses as second mic + ear.
- **COULD:** glasses real-prop choices, more what-ifs ("what if it rains?", "what if we plant clover?"), scrcpy cast, spectator page.
- **CUT:** live 3D generation, controllers.

### Gates
- **Sat 11 AM, Orbis gate:** live stream holds ≥10 fps in the Quest window for 5 min and a prompt change shows within ~4 s → live + Visko track. Otherwise clips + Social Good.
- **Sat 6 PM:** feature freeze, full run with strangers, stopwatch on.
- **Sun 7:30 AM:** build lock (sideload APK kept). **Sun ~8:30 AM:** Devpost + 30 s vertical video submitted.

## Who's doing what

Claims now live on the **Task board** in the Work plan at the top of this file (S-IDs). The old task list was replaced at 03:00 Sat.

**In flight, Sat 2:45 PM** (pull often; tell Rome on Discord before you touch these areas):
- **Rome (agents):** Daniel's rigged art in Unity (Beeswax rig + 21 clips, animated hornet/wasps/hive lid/smoker/spray/rocket, his worker bee baked into every swarm, music bed) + footage fixes -> D-7 APK, then commit. Then merge Nam's PR #1 on top, then the **Unity 6000.3.28f1 LTS** upgrade (trial passed: no code changes, Input System 1.20.1, URP 17.3, Meta SDK stays 209).
- **Rome (agents):** ride script v2 (intro + flow, 5 min of fact-checked bee facts + puns, Ask Beeswax AI moment); forecast window (peaceful Paris, then a laser-eyed bee topples the Eiffel Tower on the stakes line); SATIE sound; Orbis video wall for the load-in screen.
- **Nam:** S1.10 Voice SDK websocket fix (merged as PR #1). Next: headset test of the latest APK.
- **dartiros:** art. Notes on the .blend in the log below.
- **Adityash:** stations, tuning, rehearsal (S2/S4/S14).

## Physical props & shopping

Optional (only if we do physical choices or the glasses real-prop mode): a jar of honey, a squeeze bottle labeled "SUGAR SYRUP", a shallow dish + pebbles, a deep bowl, a bee smoker (or a stand-in). Also: power banks, a travel router or a dedicated hotspot, a union-mediator lanyard for whoever introduces the demo.

---

## Tech plan (verified research, Fri 11 PM)

```
 Quest app (Unity)  ◄──── wss bus ────►  Hive (Bun/TS)  ◄── HTTPS/WS ──  Mentra miniapp (on iPhone) ── BT ── Mentra Live
   beat clock, swarm,                     scan pipeline, Claude,             button, photo, transcription,
   pre-rendered lines                     voice (ElevenLabs), Beat Board      ear audio
          ▲                                       │
          └──── JPEG frames ◄── Orbis relay (Python, laptop) ◄── WebRTC ── Reactor (Visko Orbis)
```

- **Hosts:** `beeswax.drori.dev` (pop-os, always on: use it tonight) and `beeswax-venue.drori.dev` (Rome's laptop at the venue: **primary on demo day**). Clients try venue first, then pop-os.
- **Dev server is LIVE (Sat 12:30 AM):** `https://beeswax.drori.dev/health` · Beat Board `https://beeswax.drori.dev/board?token=TOKEN` · spectator `…/monitor?token=TOKEN` · bus `wss://beeswax.drori.dev/bus?role=quest&token=TOKEN` · Orbis frames `wss://beeswax.drori.dev/orbis/frames?token=TOKEN` (fake test pattern until the Reactor key is in). **Get TOKEN from Rome privately**: never commit it. Unity setup: `unity/BeeswaxClient/README.md`. Running the Hive locally: `cd hive && bun install && bun run start` (tests: `bun --no-env-file test`, so a local `.env` with real keys never gets used by tests).
- **Mentra Live:** no display. Bee Scout uses a local Mentra miniapp (`@mentra/miniapp@3.3.0`) with button, camera, mic, LED and speaker APIs; the independent `field_guide` scan does not require a Quest beat. The current SDK is beta and supports local dev/release installs; Mentra's current docs recommend the Bluetooth SDK for new Mentra Live development and do not support Miniapp Store distribution yet. Docs: https://docs.mentraglass.com/llms.txt
- **Orbis = Reactor** (`reactor/visko-orbis-stable`): ~$0.58/min while connected, WebRTC, 832×480 @ 18 fps, first frame ~3–4 s, prompt changes morph in 2–4 s, 5 concurrent sessions per account, clips expire in 24 h. No Unity SDK, so a Python `reactor-sdk==1.14.0` relay sends JPEGs to Unity. Tune prompts in the sandbox: https://www.reactor.inc/sandbox?model=visko-orbis-stable. Keep prompts non-violent (moderation ends the session).
- **Quest (U1/U2):** Unity 6 (6000.0.66f2+), Unity OpenXR + Meta XR feature group, `com.meta.xr.sdk.core` / `com.meta.xr.mrutilitykit` / `com.meta.xr.sdk.interaction.ovr` **209.0.0** via Building Blocks + Project Setup Tool. Floor/stage origin (a system recenter must not move the mat). Declare boundaryless. Bees with Burst + `Graphics.RenderMeshInstanced`. Meta XR Audio (`com.meta.xr.sdk.audio` 209.0.0) as the spatializer. **Avoid** Voice SDK, VFX Graph, colocation. Pre-grant mic permission. Check scrcpy casting of passthrough early.
- **Voice:** one ElevenLabs Voice-Designed Beeswax voice for everything. Scripted lines are pre-rendered and shipped inside the Unity build and the miniapp. Live lines (off-list roasts) are streamed.
- **Claude:** Haiku 5.5 or Sonnet 5.5 for scan verdicts (benchmarked on our props, Rome picks), structured JSON output with fixed enums. Unity only ever executes known enums.
- **SATIE:** use it as a sound *designer* through its MCP (library/Freesound pulls are free, takes ~3¢). Import as **mono, Vorbis** clips and spatialize in Unity. Generated takes on a free account may not be OK in a public repo: ask the Attractor reps.
- **Rules (2025, until 2026's are copied in):** 30 s vertical demo video, public repo under **MIT**, paid AI services cited in the README (≤$20 in 2025), Devpost + git snapshotted at 9:01 AM.

### Event contract (Hive ↔ Quest ↔ glasses ↔ Beat Board)

Source of truth: `hive/src/protocol.ts`, **v2.1** (additive: `PROTOCOL_VERSION` is still 2). C# mirror: `unity/BeeswaxClient/Runtime/BeeswaxProtocol.cs`. Change both together and log it here. `hive/test/contract.test.ts` and `hive/test/contract-v21.test.ts` check the mirror, the data files, the routes and who may send what.

- **Bus:** `wss://<host>/bus?role=quest|board|monitor|glasses&token=…` (or header `x-hive-token`). JSON text frames plus binary frames `[kind u8][payload]`. A client sends `hello{role, client, version}` first, then every ~10 s as a keepalive; the Hive answers each with `welcome{group_id, beat, mode, server_time, host}`. A client on another protocol version gets one `error`, and the Board sees a warning.
- **Binary kinds:** `0x01 SCAN_PHOTO` (Hive → Quest + monitor: the JPEG after `scan.photo`), `0x02 TTS_PCM16` (Hive → Quest: PCM16 LE mono at `tts.start.sample_rate`, between `tts.start` and `tts.end`), `0x03 MIC_PCM16` (Quest or glasses → Hive: PCM16 LE mono 16 kHz, between `listen.start` and `listen.end`). The Orbis frames socket is separate and sends bare JPEGs.
- **Unity owns the beat clock.** The Hive and the Board only *request*; Unity accepts or ignores. With no Quest connected, the Hive applies a `beat.request` itself (rehearsal).
- **One line, one device:** room lines play only from Beeswax on the Quest, ear lines only in the glasses.

**Client → Hive.** From any other role, the Hive answers with an `error` naming the type.

| From | Message | What the Hive does |
|---|---|---|
| quest | `beat.set{n, name}` | Authoritative beat, rebroadcast to all. `smoker` (the first station) starts the score clock (v3.1); `escape` sends `score` and sets the window to `full_bloom`; `t0` sets `moon` |
| quest | `choice.made{station, choice, correct}` | Plays the outcome from `hive/lines/stations.json`: room `say`, `fx`, window scene, memory. Board/monitor get a copy |
| quest | `swarm.arrived{socket}`, `hornet.cooked{overshoot}` | Logged to board/monitor |
| quest | `say{channel, line_id, tts_id, audio_url, caption}` | Unity cueing a line: `ear` → glasses; `room` → board/monitor copy only (Unity already played it) |
| quest, glasses | `listen.start{source, mode: ptt\|vad}` + `MIC_PCM16` + `listen.end{source, cancel?}` | Speech-to-text → `transcript` → router → `intent` → line + window + fx |
| board, quest | `orbis.request{want?, stage?, scene?}` | v2.1: with `scene`/`stage` it only switches the scene; only a bare `want: live\|clips\|off` changes the mode (live starts the billed stream, clips/off stop it). Forwarded to the Quest as `orbis.request{want: <current mode>, scene, stage}`; everyone gets the new `orbis{}` |
| board | `beat.request{n}` or `{next: true}` | → Quest (no Quest: the Hive applies it → `beat.set`) |
| board | `mode{duo\|solo}`, `group.reset{group_id?}` | Rebroadcast to all. A reset clears memory, score and custom prompt; window → `meadow` |
| board | `hum.set{value 0..1}` | → Quest |
| board | `verdict.force{item}`, `say.force{line_id, channel}` | Operator scan verdict; force-play a line |
| board | `choice.request{station, choice}` (v2.1) | → Quest only; Unity applies it like the player's own pick and reports `choice.made`. No Quest: the Hive plays the outcome itself |
| board | `fx.force{name}` (v2.1) | The Quest (and board/monitor) receive it as a plain `fx{name}` |
| board | `whatif.force{topic}` or `{undo: true}` (v2.1) | Same path as a spoken what-if or "put it out" (line, window, fx), but no `transcript`/`intent`. `topic: "custom"` is refused: post the words to `/board/custom` |
| glasses, board | `plea{text}` | → Quest clipboard + board/monitor (field beat: board/monitor only) |
| any | `latency{hop, ms}`, `log{text}`, `error{message}` | → board/monitor |

**Hive → clients.** A client that sends one of these is ignored; the Hive never relays it (a client's `orbis` only becomes a log line).

| Message | To | Notes |
|---|---|---|
| `welcome{…}` | the client that said hello | |
| `beat.set`, `mode`, `group.reset` | all | |
| `orbis{state: off\|priming\|ready\|failed\|ended, stage, scene, fallback_clip}` | all | The Hive owns the window. `stage` = index in `SCENES`; `fallback_clip` = the scene's recorded clip; `whatif_custom` has none, so it names the clip that was showing |
| `orbis.request{want, scene?, stage?}` | quest | A forwarded Board/Quest request |
| `say{channel: room\|ear, line_id, tts_id, audio_url, caption}` | room → quest, ear → glasses, both + board/monitor | |
| `tts.start{tts_id, sample_rate, caption}` + `TTS_PCM16` + `tts.end{tts_id}` | quest | Live room lines, one at a time |
| `fx{name, delay_ms?, duration_ms?}` | quest + board/monitor | A later `embers_stop` cancels an `embers` still waiting on its delay |
| `choice.request{station, choice}` | quest | v2.1 |
| `hum.set{value}` | quest | |
| `transcript{text, final, source, ms, stt}` | all | `/board/custom` text arrives as `source: "quest", stt: "board"` |
| `intent{name, topic, text, path, ms, custom_prompt?}` | all | `name`: what_if, undo, ask, ack, unknown. `topic`: fire, rain, flowers, spray, winter, custom. v2.1 `custom_prompt` is present only for topic `custom`: the vetted Orbis prompt, or `null` = refused (Beeswax says `room_whatif_refuse`) |
| `score{elapsed_s, text, grievances}` | all | v2.1, when escape starts: "Hive saved in 2:47", grievances = wrong picks |
| `plea{text}` | quest + board/monitor | |
| `scan.started{scan_id}`, `scan.photo{scan_id, width, height}` + `SCAN_PHOTO`, `scan.verdict{scan_id, item, verdict, confidence, seen, socket, line_id, tts_id, caption, path, ms}` | quest + board/monitor (photo: quest + monitor; field beat: board/monitor only) | Glasses real-prop mode (later) |
| `choice.made`, `swarm.arrived`, `hornet.cooked`, `latency`, `log` | board/monitor | |
| `error{message}` | the sender | |

- **Enums (Unity only executes these):** `BEATS`, `STATIONS` / `CHOICES` / `CORRECT_CHOICE`, `SCENES` (18; v2.1 `whatif_custom` has no recorded clip, so clips mode keeps the last clip), `WHATIF_TOPICS` (v2.1 adds `custom`), `FX`, `INTENTS`, `ITEMS`, `VERDICTS`.
- **Data (`hive/lines/`):** `lines.json` (every `room_*`/`ear_*` id the code names; `alt:<id>` variants), `stations.json` (station → choice → `{correct, line_id, scene, fx?}`; Unity's offline copy `unity/BeeswaxClient/Resources/beeswax_stations.json` must stay byte-identical), `orbis_scenes.json` (a prompt and seed per scene; clips are `<scene>.mp4`).

**HTTP on the Hive.** "token" = `?token=<HIVE_TOKEN>` or header `x-hive-token`; one token for everything.

| Route | Auth | Notes |
|---|---|---|
| `GET /health` | open | `{ok, version, clients, beat, beat_name, group_id, mode, orbis{state, stage, scene, want, relay, custom_prompt?}, solved, score, score_text, grievances, tts, stt, …}` |
| `GET /board`, `GET /monitor` | token | Beat Board, spectator page |
| `GET /lines.json`, `GET /stations.json` | token | Line catalog, station table |
| `POST /board/custom {text}` | token | v2.1: the operator types a what-if (or anything). Routed exactly like speech, in any beat → `{ok, intent, topic, path, ms, custom_prompt?, blocked?, answer?}` |
| `POST /glasses/press {pressed_at, button?, press_type?, field_guide?}` | token | → `{allowed, scan_id, line_id}`; `field_guide: true` starts an independent field scan at any beat |
| `POST /glasses/scan {scan_id, photo_url?, heard?, pressed_at, field_guide?}` | token | → `{item, verdict, line_id, audio_url, caption, path, ms}`; field guide always requires a usable photo for identification |
| `POST /glasses/utterance {text, at}` | token | The glasses' own transcription, same pipeline as Quest voice → `{intent, topic, line_id, audio_url, caption}` |
| `POST /glasses/plea {text}`, `POST /glasses/latency {hop, ms}`, `POST /glasses/event {…}` | token | Plea → Quest clipboard; hop timing; free-form log |
| `GET /audio/<id>.mp3` | open (random id) | Live ear lines |

**Orbis relay** (`hive/orbis-relay/relay.py`, port 8241, same token; the public host serves it under `/orbis/*`): `POST /orbis/warm`, `POST /orbis/start {prompt?, seed?, stage?, scene?}`, `POST /orbis/stage {prompt?, stage?, scene?, reset?}`, `POST /orbis/stop`, `GET /orbis/status`, `GET /orbis/frame.jpg`, `WS /orbis/frames` (binary JPEGs, newest only; `?status=1` adds the relay's status JSON), `GET /healthz` (open). Only the Hive calls the POSTs (`ORBIS_RELAY_URL`), driven by `orbis.request`. Unity only reads `/orbis/frames`, and takes just the live state from the relay's status: the scene and clip come from the Hive's `orbis{}`.

## Decisions

- Theme: bee conservation, Beeswax the AI bee, an angry swarm, hornets ("not all wasps are bad"), pre-made assets the AI only *picks*, a scripted short demo that implies larger scope.
- Fri 11 PM: Mentra Live replaces XREAL; Beeswax's voice is an ElevenLabs designed voice; Claude calls use Haiku 5.5 or Sonnet 5.5.
- Fri 11:40 PM (Rome): **concept v3 = best of both**: Adityash's hive escape (4 stations, live Orbis future window, voice what-ifs, score, map) + HONEYMOON (Beeswax the union-rep drone, Moon stakes + ending, hornet hum). Voice comes from the Quest mic or the glasses mic, through Whisper-style speech-to-text. Glasses are fitted in after the Quest game works.
- Equipment: OpenBCI excluded. Keys never go in the repo.

## Open questions

- Which Quest is it: 3 or 3S? (A 3 has better passthrough and depth.)
- 2026 tracks, criteria, and rules (dashboard). Do sponsor prizes count toward the 2-track limit?
- Judging format: pairs or solo? Open hall or booths?
- Orbis: do the Visko keys from Discord work on reactor.inc, and what's the balance?
- SATIE: runtime API or authoring only? Can generated takes go in a public repo?
- Who writes Beeswax's lines? (Rome drafts v1, the funniest person edits.)
- How does the player talk to the Quest: hold a pinch, a controller button, or always listening with Beeswax prompting "ask me anything"?
- Which station choices are physical (squeeze, tip, grab) vs floating buttons?

## Team

| Name | GitHub | Focus |
|---|---|---|
| Rome | [Shruichan](https://github.com/Shruichan) | Backend, voice, Orbis, glasses, pitch (+ agents) |
| dartiros | [realdarter](https://github.com/realdarter) | 3D models |
| Nam Nguyen | [NamNgHH](https://github.com/NamNgHH) | Unity lead + headset |
| Adityash? | [sushirito](https://github.com/sushirito) | Gameplay content + feel, design owner (confirm GitHub) |

## Live log

*Append below, newest last. Format: `- YYYY-MM-DD HH:MM PT · Name (add "agent" if an AI wrote it) · message`. Address someone with "→ Name". Never edit or delete other people's lines.*

- 2026-11-13 21:45 PT · Rome (agent) · Created this doc from the kickoff meeting notes plus Rome's answers: Beeswax does both scout + coach on both devices, commands an angry swarm, theme-park framing, free-form Orbis "show me". Added AGENTS.md (rules for AI agents) and a union merge for this file. Invited NamNgHH and sushirito as collaborators, so accept your invites. → everyone: claim tasks above, and fill in names for "Speaker 1" and "Speaker 4".
- 2026-11-13 23:10 PT · Rome (agent) · **Concept v2 "HONEYMOON" proposed** (6-pitch design panel + judge personas + red team) and the plan rewritten around the real hardware: one Quest, Mentra Live (no display), iPhone. Research notes are in "Tech plan". Rome claimed: Hive backend, Mentra app, Unity client scripts, Orbis relay, voice. → everyone: read Concept v2 and the "Do these tonight" list, then claim a Unity/3D task. → Rome: confirm the concept with the team and log "accepted".
- 2026-11-13 23:45 PT · Rome (agent) · **Concept v3 is the plan: Adityash's hive escape + HONEYMOON, best of both.** Spine = Adityash's 4 stations + live Orbis future window + voice what-if + escape/score/map. Added from HONEYMOON: Beeswax the union-rep drone, "fix it or we move to the Moon" stakes, hornet hum (if ahead), T-0 Moon ending. Fixed one fact line (smaller door = *fewer guards needed*). Tasks rewritten for v3, so claim yours. → Adityash: thank you, check I kept your beats right. Hive backend/voice/Orbis/Unity client scripts are being built now (Rome).
- 2026-11-14 00:40 PT · Rome (agent) · **Backend v1 is in the repo and live** at beeswax.drori.dev (Hive bus + Beat Board + monitor + Orbis relay in fake mode; voice off until the ElevenLabs key). Code: `hive/`, `hive/orbis-relay/`, `unity/BeeswaxClient/` (drop-in C#, compiled clean in Unity 6000.2.9f1), `glasses/` (Mentra miniapp, later). Now adapting all of it to concept v3: stations + `choice.made`, voice what-ifs (Quest/glasses mic → speech-to-text → intents → Orbis scene + embers), per-scene fallback clips. Protocol bumps to v2; I'll log when it lands. → Unity folks: start from `unity/BeeswaxClient/README.md`; ask Rome for the token.
- 2026-11-14 01:00 PT · Rome (agent) · Started the **Unity base project** (`unity/HoneymoonQuest`, Unity 6000.2.9f1, Meta XR 209/OpenXR, Android/Quest settings, placeholder v3 systems, desktop keys for testing without a headset) and the **pitch kit** (`pitch/`). → Unity folks: if you already started your own project, say so here and we'll merge; otherwise wait ~2 h for the base and claim the systems to replace with real art/feel.
- 2026-11-14 01:25 PT · Rome (agent) · **Pitch kit drafted** in `pitch/` (devpost.md, video-30s.md, walkthrough-2min.md, tracks.md, judge-qa.md, demo-runbook.md, demo-card.md, gallery.md), reviewed by a jaded-judge pass and a fact check (fixes: heat ball is ~46–47 °C with 500+ bees, *Japanese* honeybees, so Beeswax says "My cousins in Japan do this"; from the Moon's surface Earth *hangs* over the horizon, it doesn't rise; newest Auburn loss figure is 39.9% for 2025–26 after two record years). **Game asks from the review are in `pitch/script-and-build-asks.md`**: claim them. Biggest ones: the hive lift should feel weightless + "Light, right? That's bad." (U1); "Grievances filed: N" on the score card (U2 + Rome); let the what-if happen while the meadow is still sick (U1); optional fail ending if the timer runs out (U1). One name everywhere: **the meadow window**; the player talks to **Beeswax**, not the window. Rome takes the line edits + Board fire button + off-list what-ifs.
- 2026-11-14 01:50 PT · Rome (agent) · **Claude Code tools for the team** (optional, if you use Claude Code): Unity's official plugin: `claude plugin marketplace add Unity-Technologies/unity-agent-plugin` then `claude plugin install unity@unity-agent-plugin` (skills: create-xr, craft-game-floors for juice/polish, unity-pipeline to compile/Play Mode/screenshot a running Editor, unity-cli). The Unity CLI itself: `curl -fsSL https://unity.com/install.sh | bash`. A reviewed design skill, game-design-core (plain markdown, Apache-2.0): https://github.com/omer-metin/skills-for-antigravity/tree/main/skills/game-design-core → copy into `~/.claude/skills/`.
- 2026-11-14 02:10 PT · Rome (agent) · **Delegation plan added** (top of file): Nam = Unity lead + headset, Adityash = gameplay content/feel + design owner, dartiros = 3D, Rome = backend/voice/Orbis/glasses/pitch. Only Nam edits the scene; everyone else works in prefabs. Beeswax's voice is **Harry** (ElevenLabs) and it's live on beeswax.drori.dev; all lines get pre-rendered once the script polish lands.
- 2026-11-14 02:15 PT · Rome (agent) · Delegation is flexible: the areas are starting points, most of us are on dev, take the next open task when you finish one. Adityash has the most time, so he floats to whatever is behind.
- 2026-11-14 02:45 PT · Rome (agent) · **Backend v2.1 live** on beeswax.drori.dev: Beat Board operator buttons (station choices, fx, fire/put it out, typed custom what-ifs), score card with "Grievances filed: N", unplanned what-ifs (Claude writes a safe Orbis prompt or Beeswax refuses: "Grievance filed. Not that one."), questions answered only from the vetted facts. **All 77 Beeswax lines pre-rendered in Harry's voice** (ElevenLabs); ear lines are in the glasses app, Unity copies land with the Unity project. Unity can now build Quest APKs on pop-os (license fixed). Event contract section below is updated to v2.1.
- 2026-11-14 03:00 PT · Rome (agent) · **Granular Work plan is in** (top of file): ~100 tasks across 17 systems, each with an S-ID, priority, estimate, deps, a done-when you can check, and a suggested owner; hour-by-hour schedule with sleep; critical path; overnight agent queue. **The game is called Virtual Bee Swarm** (HONEYMOON-1 is only the rocket's name); the Unity project lands as `unity/VirtualBeeSwarm`. Already done: S1.5 Unity license + APK build, S10.2 backend v2.1, all 77 voice lines rendered. → Nam, Adityash, dartiros: your first tasks are in the team message / the schedule table.
- 2026-11-14 03:40 PT · Rome (agent) · Work plan rewritten to read like a person wrote it (same tasks, IDs, times, owners and statuses; checked by script).
- 2026-11-14 05:15 PT · Rome · **Direction update:** (1) **AR first on the Quest 3**: use the real room (hive on the real table, meadow window hung on a real wall, bees flying behind your hands and furniture), manual placement only as the fallback. (2) **Everything arrives carried by the swarm**: the hive, each station's choices, the score card; wrong picks get hauled away. (3) **New stakes**: if we don't take care of the bees, the angry swarm ravages the Earth and humans have to flee to the Moon (the rocket on the table is the humans' evacuation rocket; Beeswax takes it himself at the end). A design pass is working out the details now; concept + tasks get updated when it lands. The per-person rewrite of the work plan is dropped.
- 2026-11-14 06:15 PT · Rome (agent) · **Concept v3.1 is in** (above Concept v3): AR first (hive snapped onto the real table with shadows; window on the real wall, depth occlusion and bees landing are switchable SHOULDs; full room scan is a stretch goal), **everything is delivered by bees through the meadow window** (wrong picks get repossessed), and the stakes are one line + one image: "Let my hive die, and angry swarms ravage the Earth. You humans get the Moon." HONEYMOON-1 is now the humans' evacuation rocket. New build tasks V1–V26 are listed under it. Lines updated and re-rendered (83 clips in Harry's voice). Temp art set is in `art/` (contact sheet + Blender scripts); darter keeps or replaces any piece.
- 2026-11-14 06:55 PT · Rome (agent) · **Unity project is on GitHub: `unity/VirtualBeeSwarm`** (Unity 6000.2.9f1 + Android; the scene builds itself on first open; F2 = autopilot; Virtual Bee Swarm → Build Quest APK). README has the steps. Token goes in `Assets/Resources/beeswax_token.txt` (git-ignored, DM Rome). First APK (v3, before bee delivery) is on pop-os: `~/immerse-the-bay/unity-work/builds/VirtualBeeSwarm-D-4-20261114-0630.apk`; install with `ops/quest-deploy.sh <apk>`. Heads up: Meta's SDK writes an access token into `Assets/Resources/DevAgentSettings.asset` when the editor opens; it's git-ignored now, never force-add it. The v3.1 build (bee delivery, stakes, AR placement, temp art) is in progress → Nam: if you change the project, push small and log it so the merge stays clean.
- 2026-11-14 13:33 PT · Nam Nguyen (agent) · Claimed S1.10 to fix the Meta Voice SDK NativeWebSocket assembly collision in `unity/VirtualBeeSwarm/Packages/` and verify Android compilation. → team
- 2026-11-14 14:45 PT · Rome (agent) · **Everyone: move to Unity 6000.3.28f1 LTS** (Hub: Installs -> Install Editor -> LTS 6000.3.28f1, with Android Build Support + OpenJDK + Android SDK & NDK). The project switches once Daniel's art + Nam's PR are merged and pass; don't open main in 6000.2 after that. -> Nam: folding PR #1 in; the junk files `Assets/_Recovery/`, `TempAssembly.dll`, `Assets/#Meta/*.id` get removed and git-ignored; do we need the Voice SDK (`sdk.all`) or only the fix? -> dartiros: your art is wired in. The .blend files need Blender 5.3 (the art README says 4.2); Peach Blush base colour is grey; Mouth O / blush stay full-size in Angry, Sad, Blush, Sooty Shake; insect actions all use the Beeswax Rig slot and their NLA tracks are muted; the full-size wasp is ~6k tris (decimate?). -> everyone: `git pull --rebase` before you start, push small, log here.
- 2026-11-14 14:46 PT · Nam Nguyen (agent) · Claimed S3.3 to add generic prefab spawning through the AR environment raycaster in `unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm/AR/SpawnObject.cs`. → team
- 2026-11-14 15:00 PT · Nam Nguyen (agent) · Claiming S1.6 to verify Unity/Android modules, headset connectivity, OVR Metrics Tool, and voice settings on Nam's machine. → team
- 2026-11-14 15:00 PT · Nam Nguyen (agent) · Verified Unity 6000.2.9f1 with SDK/NDK/OpenJDK and Quest 3 visible in adb on Windows. The OVR Metrics Tool app isn't confirmed installed; "Hey Meta" and voice-command settings need headset-side verification. → Nam
- 2026-11-14 15:14 PT · Nam Nguyen (agent) · Nam confirms the Quest is testable and usable with the Unity project; S1.6 is no longer blocked on project usability. OVR Metrics Tool and voice settings still need verification. → team
- 2026-11-14 15:10 PT · Nam Nguyen (agent) · Added generic `SpawnObject` using the AR environment raycaster; editor diagnostics report no errors. Scene hookup and Quest verification remain pending. → team
- 2026-11-14 15:02 PT · Nam Nguyen (agent) · Claiming S1.8 to bootstrap Android settings twice, build and sideload on the Quest 3, then verify passthrough and hand tracking; existing Unity edits are preserved while syncing. → team
- 2026-11-14 15:36 PT · Nam Nguyen (agent) · Android bootstrap passed twice and XR settings were pushed in `5672add`; Quest 3 is connected but the app is not installed. APK build is blocked because S1.7's local HIVE_TOKEN, shared keystore and password are unavailable. S1.9 import and charger/sleep-off remain pending. → Rome: please arrange the secure S1.7 handoff; → Nam: keep the Quest charged and disable sleep before the import.
- 2026-11-14 15:38 PT · Nam Nguyen (agent) · Confirmed Windows sleep timeout is Never on AC and DC. Could not confirm physical power connection; a different Unity project is still open, so I left it untouched and did not start a competing import. → Nam: confirm power and close the other Unity project when ready for S1.9 import.
- 2026-11-14 15:41 PT · Nam Nguyen (agent) · Started and completed a Unity 6000.2.9f1 batch import of `unity/VirtualBeeSwarm`; Asset Database refresh completed, exit code 0, no asset changes or C# compiler errors. This was a warm import, not S1.9's fresh-clone verification. → team
- 2026-11-14 16:10 PT · Rome (agent) · Claimed: ride script v2 in the game (42 main-path lines re-rendered in Harry's voice, new intro/stations/what-if/end-card lines wired in Unity, build + screenshot review, then push). Touches hive/lines, Assets/Resources/BeeswaxLines, Beats/, Stations/, Core/GameLines.cs. Satie sound and the forecast moment are on main. → Nam: D-8 APK has everything up to Satie; download link coming.
- 2026-11-14 16:35 PT · Rome (agent) · **Decision: stay on Unity 6000.2.9f1 until after judging** (the 6000.3.28f1 LTS trial passed with no code changes, but switching now only costs everyone a reinstall + re-import). Ignore the 14:45 move-to-LTS note. → Nam: test APKs (D-8 now, D-9 with script v2 next) are at beeswax.drori.dev/dl/, password from Rome by DM. Install with `ops/quest-deploy.sh <apk>` so the scene + mic permissions are granted.
- 2026-11-14 16:16 PT · Rome (agent) · Claiming S17.1: validate and package the Mentra Live miniapp as an isolated glasses build in `glasses/`, check its end-to-end flow/build path, and document the current local install limits. → team
- 2026-11-14 15:53 PT · Nam Nguyen (agent) · Reviewing S3.3 against the generic `SpawnObject` work in `unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm/AR/SpawnObject.cs`; the plan's P0 path is two-point table calibration (controller tip, grab-and-drop fallback), while aim-and-pinch is V7 SHOULD. → team
- 2026-11-14 15:53 PT · Nam Nguyen (agent) · Updated optional `SpawnObject` to require the calibrated pre-beat table, fit the plan's placement box, snap near-table hits to table height, and parent spawned props to `TableAnchor`; `HivePlacement` remains the S3.3 calibration path. Editor diagnostics are clean; Quest/recenter checks remain. → team
- 2026-11-14 16:18 PT · Rome (agent) · Pivoting S17 to Bee Scout: the glasses now scan plants and pollinators anytime, speak a cautious likely ID plus one conservation action, and avoid Quest effects; adding the independent `field_guide` scan path. → team
- 2026-11-14 16:25 PT · Rome (agent) · Heads up on overlap: the Ask Beeswax workflow is editing the Hive's speech/AI path (hive/src router.ts, director.ts, tts.ts + new files) right now, and Bee Scout's `field_guide` scan touches the same Hive (server.ts glasses routes, director vision). → Rome (codex/mentra-live-build): keep field_guide in new files or the glasses routes, pull main before touching director.ts, and log here before merging.
- 2026-11-14 16:22 PT · Nam Nguyen (agent) · Updating optional `SpawnObject` to get left/right pointer rays from `GameHands.TryGetPointerRay` instead of serialized hand-ray Transforms; the existing environment raycast and calibrated-table checks stay in place. → team
- 2026-11-14 16:24 PT · Nam Nguyen (agent) · `SpawnObject` now consumes the left/right `GameHands` pointer rays; serialized ray-start Transforms are no longer required. Editor diagnostics are clean; headset/recenter checks remain pending. → team
- 2026-11-14 16:29 PT · Nam Nguyen (agent) · Aligning optional `SpawnObject` with the 2025 spawner's existing-object activation/cycling and surface-raycast behavior while retaining current `GameHands` and calibrated-table APIs. → team
- 2026-11-14 16:30 PT · Nam Nguyen (agent) · `SpawnObject` now raycasts as before, activates/deactivates and cycles assigned scene objects, and faces the camera; runtime hookup is still absent from the generated scene builder, and headset verification remains pending. → team
- 2026-11-14 16:45 PT · Rome (agent) · Not merged yet: codex/mentra-live-build 6ada14b (Bee Scout) fails checks on its own: `tsc` hive/src/server.ts:705 `fieldGuide` isn't in `JudgeInputLike`, and the protocol C# mirror test fails (protocol.ts changed without unity/BeeswaxClient/Runtime/BeeswaxProtocol.cs). → Rome (codex): fix those two, pull main (the Ask Beeswax build is also editing director.ts), then it gets merged.
- 2026-11-14 16:37 PT · Nam Nguyen (agent) · Starting NamTest scene wiring to match the working GameScene start flow while spawning the hive prefab; checking the current scene and preserving its placement path. → team
- 2026-11-14 16:50 PT · Rome (agent) · → new-art workflow (props lane): dartiros updated `art/blender/Ten New Assets.blend` (44ee59d): 11 assets / 18 clips now, plus a human Earth flag, badge pin settle, hive tool lever, droplet wobble, pot soil/plant anchors, watering-can water level + stream. Re-pull and export from this version. → Rome (codex/mentra-live-build): still not merged, same two failures at 3e9a6c1 (tsc server.ts:705 fieldGuide, C# protocol mirror test).
- 2026-11-14 16:39 PT · Nam Nguyen (agent) · Decision: adapt NamTest to use its existing MRUK room and hand anchors, with one Hive target and a start-button trigger, matching the old GameScene flow rather than adding the full ArRig. → team
- 2026-11-14 16:55 PT · Rome (agent) · → Nam: heads up so we don't build placement twice: the main game scene (made by BuildGameScene, in every APK) already has AR placement (AR/ArRig + InstantPlacement: aim + pinch on the real table via EnvironmentRaycast, two-point/grab fallback) and the v3.1 start is the SIGN HERE clipboard (START button only when delivery is off). Most useful right now: install D-8 from beeswax.drori.dev/dl/ (D-9 with script v2 soon) and test the real flow on the Quest: placement, dim, dark swarm height, deliveries, hands. If you want MRUK, the room bloom (Bloom/, in progress) uses MRUK rooms when a scan exists and could use your help testing that on the headset.
- 2026-11-14 16:58 PT · Rome · **Bee puns galore.** Every Beeswax line, live Ask Beeswax answer, caption, stamp, score card and UI text should land a bee pun where it fits (more than one is fine), facts stay accurate and lines stay short. → all agents: if a prompt or rule says "one pun max", raise it.
- 2026-11-14 17:00 PT · Rome (agent) · → new-art workflow (props lane): dartiros updated Ten New Assets.blend again (661f7f7, people on the human flag); export from the latest. → Rome (codex/mentra-live-build): "Bee Scout bonus art" overlaps the new-art + environment workflow and Daniel's props; say what art you mean before making any, and the branch still has the two failing checks (tsc server.ts:705, C# protocol mirror).
- 2026-11-14 16:55 PT · Nam Nguyen (agent) · NamTest is wired and saved with MRUK hand rays, TABLE filtering, the Hive scene object, CenterEye target, and a PointableCanvas start button calling SetSpawningStatus(true); Unity reports no errors. Headset/recenter verification remains pending. → team
- 2026-11-14 16:58 PT · Nam Nguyen (agent) · Removed the one-off scene setup utility after saving NamTest; updated the MRUK label-filter call to its current constructor API. Unity refresh/compile now reports zero warnings and errors; headset/recenter test remains pending. → team
- 2026-11-14 17:02 PT · Nam Nguyen (agent) · Claiming the NamTest start-button interaction bug: investigate the PointableCanvas/input routing and fix the scene or setup. → team
- 2026-11-14 17:06 PT · Nam Nguyen (agent) · Confirmed both the start button and spawned Hive need Quest hand-tracking interaction (point + pinch); expanding the NamTest fix to add ray and grab input. → team
- 2026-11-14 17:12 PT · Rome (agent) · dartiros pushed his original 2D bee artwork + drawing timelapse to main (99066a6, art/bee-reference/). → Rome (codex/mentra-live-build): that's the bee reference S18.2 sticker sheets were waiting for. → dartiros: great, previews going to Rome now; we'll use it on the load-in video wall and the Devpost/demo title cards.
