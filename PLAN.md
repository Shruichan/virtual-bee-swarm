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
| S19 Live Bee Scout (glasses-only, ManageXR) | Adityash | G (branch `scout/live`) |
| S20 Pollinator habitat props in the game | Rome | A (new Art slots + table dressing; no scene edits) |

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

  — owner: **Nam Nguyen** — status: in progress (NamTest has GameScene's hand-anchor MRUK spawning path and full AR building-block setup, with Hive as the target; game/minigame, event-handler, and Afference API/haptics are excluded; Quest retest pending)

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

