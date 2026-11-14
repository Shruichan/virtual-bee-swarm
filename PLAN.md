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
| Concept | **v3 = best of both:** Adityash's hive escape (4 stations + live Orbis future window + voice what-ifs) + HONEYMOON (Beeswax the union-rep drone, Moon stakes and ending, hornet hum). Glasses get fitted in after the Quest game works |

## ⏰ Do these tonight (everyone)

- **Someone logged into the live dashboard:** copy the 2026 **Tracks & Submit** text, rules, and judging criteria into this file.
- **Check Discord** for a team/repo/equipment form. In 2025 it was due Fri 9:30 PM.
- **Everyone:** click "Join hackathon" on [Devpost](https://immerse-the-bay-2026.devpost.com/) and accept the project invite later. Accept the GitHub repo invite.
- **Glasses owner:** update Mentra Live software tonight on a strong network (~150–640 MB). In MentraOS, set the button mode to **apps**, not camera.
- **Shopping (Sat 7:30–9 AM):** order the mat print tonight for Sat 8 AM pickup. Buy plants that **actually bloom purple in November**: salvia / Mexican bush sage, asters, rosemary in flower. Lavender is usually not blooming now. See the [props list](#physical-props--shopping).

---

## 🗂️ Work plan (Sat 2 AM → Sun 9 AM)

*Replaces the old "Delegation plan". Written Sat 02:35 PT. Team submit target: Sun 08:30. Devpost closes at 09:00.*

1. **Pull** the next task from the top of your column in the schedule below. Open P0 items in the [Bug list](#bug-list) come before new tasks.
2. **Claim** it: change `*suggested X*` to `**X**`, set the status (open / claimed / in progress / blocked / done), and push PLAN.md right away.
3. **Log** a Live log line when you start, finish, get blocked, or hand something off. Always include the file path and "→ name".

### House rules
- **Only one agent lane writes to each path.** Each system has one human owner and one agent lane.
  - **Lane A:** `unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm/**` (game code, scene builder, `Meta/`, `Placement/`, tuning code), except the paths listed under the other lanes.
  - **Lane B:** `ProjectSettings/`, `Packages/`, `Assets/XR/`, `Assets/Plugins/Android/`, `Assets/VirtualBeeSwarm/Editor/Setup/`, `Assets/VirtualBeeSwarm/Settings/`, `GameBuild.cs`, `ops/*.sh`.
  - **Lane C:** `hive/**` (except `hive/lines/`) and `unity/BeeswaxClient/**`.
  - **Lane D:** `hive/lines/**`, `GameLines.cs`, `Assets/Resources/BeeswaxLines/`, the meadow clips, `Art/Audio/`.
  - **Lane E:** `pitch/**`, `ops/*.md`, README, LICENSE, and the bookkeeping sections of PLAN.md.
  - **Humans:** dartiros owns `Art/<Slot>/`. Adityash owns `Tuning/station-values.json`. ProjectSettings changes from Nam's editor go in as bootstrap commits only.
- **`VirtualBeeSwarm.unity` is a build output.** It is gitignored, and `GameBuild.BuildApk` runs the scene builder first on every machine. Nobody edits it by hand. Change things through:
  - the builder (lane A)
  - `tuning.json` and the tuning asset (Adityash)
  - `Art/<Slot>/<Slot>.prefab` (dartiros)
- **Builds**
  - Nam is build master. Adityash wears the headset from 08:45.
  - Agents merge by :10 and :40. Nam pulls at :15 and :45 and builds only the last commit that S1.15 marked green.
  - Build N+1 compiles while build N is being tested.
  - There are three build types:
    - **L:** Link play mode. Windows only, takes seconds.
    - **D:** dev APK with the self-test on.
    - **R:** release APK, for fps checks, RCs and video capture.
  - Never quote fps from a D build.
  - Charge the Quest during every build.
- **pop-os project copies:** A is Android, B is Linux, and C is extra if the CPU allows. Never switch the build target inside a copy. Run one Unity batch job per copy at a time. All tests run with audio off.
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
- **Sat 02:45, license (S1.5).** If it fails, stop retrying. Agents switch to code-only work, and Nam's settings commit becomes the source of truth.
- **03:45, skeleton boots on the Quest (S1.8).** If it fails, Nam logs the error, agents work on it overnight, and we ask the Meta and Unity mentors at 08:00.
- **07:15, morning report (S0.5).**
- **08:45, full build boots (S1.18).** If it fails, switch to **Plan B**:
  - the milestone moves to 14:00
  - Link play mode becomes the demo path (Windows only)
  - all agents switch to build problems
- **09:00:**
  - team name (S16.2)
  - draft tracks (S16.3)
  - sponsor tables (S9.2, S0.6)
  - rules confirmed (S16.1)
- **10:30** spend cap set (S9.3). **11:00** Orbis gate (S9.4). **11:15** final tracks.
- **12:00, milestone (S2.5).** Each station's happy path works once, FIRE from the Board works once, fire by voice works once, and T-0 and the escape each play once. The 3-of-3 checks of both branches move to 13:00–16:00.
- **13:00** no Reactor key → stand-ins (S9.6).
- **14:30–15:00** merge and fix only. No new features.
- **15:00, noise test (S7.3).** Under 60% → the Operator's buttons become the main path.
- **16:00** voice freeze (S8.7).
- **16:30** last art swap (S11.10).
- **16:30–17:00** merge and fix.
- **17:00–17:30** integration only.
- **17:30** RC1.
- **18:00, feature freeze and rehearsal 1.** After this, only P0 fixes, and each one must be checked on the headset.
- **21:15** capture ends. If shots are missing, use rehearsal and phone footage. Never reopen the build.
- **21:30** safety video cut (S15.5).
- **23:00** Devpost draft saved.
- **23:45** RC3 decision: built by 01:00, or not at all.
- **Sun 01:00** early full submission, if Devpost allows edits afterwards (S16.10).
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

**Cut, so don't claim these:**
- glasses (old S17): **later**, not cut: fit them in after the Quest game works (the app already builds)
- the hornet, the hum, and the hornet and thermometer art
- the fail ending
- the custom what-if: **off the demo path** (it's built and on the Board's text box; don't rely on it live)
- the squeeze gesture
- LFS
- SATIE, unless we enter the Attractor prize
- the rain, flowers, spray and winter clips (P2)
- the 2-minute walkthrough, unless the rules require it
- controllers (P2, unless S3.1 scores under 8 of 10)

#### S0 Coordination and QA

- **S0.1 Bug list section** (P0 · 0.25 h · needs –) — done when: PLAN.md has a "Bug list" section with the 5-field format and one example line, and the test cards point to it — owner: *suggested Rome-agents (lane E)* — status: open

- **S0.2 Align the rest of PLAN.md** (P0 · 0.5 h · needs –) — done when: no line in PLAN.md contradicts this plan (scene-editing rule, CUT list, controllers at P2, dartiros as video owner, timeline, sleep shifts), and Who's doing what lists these S-IDs — owner: *suggested Rome-agents (lane E)* — status: open

- **S0.3 Lane list and standing rule in AGENTS.md** (P0 · 0.25 h · needs –) — done when: lanes A–E and their paths are listed. An idle agent first takes the top unclaimed P0 bug, then the next task in its own lane, and claims it in PLAN.md before starting — owner: *suggested Rome-agents (lane E)* — status: open

- **S0.4 Test cards** (P0 · 1 h · needs S1.12) — done when: by 07:00, `ops/test-cards.md` has one card per headset task (build type, steps, expected result, what to log), so whoever holds the Quest can run cards without asking anyone — owner: *suggested Rome-agents (lane E)* — status: open

- **S0.5 Morning report** (P0 · 0.25 h · needs –) — done when: at 07:15, `ops/morning-report.md` lists what landed, what failed and what didn't land; the APK path and the one install command; test results; and questions marked → Rome and → Nam — owner: *suggested Rome-agents (lane E)* — status: open

- **S0.6 Ask for a second Quest** (P1 · 0.25 h · needs –) — done when: at 09:00 we have asked the ManageXR table and the hardware desk for one with developer mode on. If we get it, it goes to Adityash for feel and sound work — owner: *suggested Rome* — status: open

- **S0.7 Book outside testers** (P1 · 0.5 h · needs S2.5) — done when: 3 people for 14:00–15:00 and 1–2 people for 18:00 are named in the Live log — owner: *suggested Adityash* — status: open

#### S1 Build, release and XR config

- **S1.1 Find out who is writing to `hq/` and BeeswaxClient** (P0 · 0.1 h · needs –) — done when: the session that edited scratchpad the integration copy and `unity/BeeswaxClient` between 02:10 and 02:31 is named, has finished or stopped, and has handed over in the Live log — owner: *suggested Rome* — status: done (it was Rome's integration workflow)

- **S1.2 Keep the night queue alive** (P0 · 0.1 h · needs –) — done when: PenguinBook is plugged in and running under `systemd-inhibit`, and pop-os Unity jobs run under `systemd-run --user` or tmux — owner: *suggested Rome* — status: open

- **S1.3 Rescue the scratchpad** (P0 · 0.5 h · needs S1.1) — done when: the integration copy, BeeswaxClient (version recorded: 0.2.1 with `OnChoiceRequest`) and `android-setup/` are in the repo and in a pop-os tarball with matching checksums; the diff between `unity-gameplay` and `hq` has been checked for edits after 01:55; and grep finds no script that still points into the scratchpad — owner: *suggested Rome-agents (lane B)* — status: in progress: integration workflow verifying a fresh copy, then push (renamed to unity/VirtualBeeSwarm)

- **S1.4 Push the skeleton** (P0 · 0.25 h · needs S1.3) — done when: by 02:50, the manifest, packages lock, ProjectSettings, `Assets/XR`, `Editor/Setup` and the rig template are pushed, with no Library, Temp, Logs, UserSettings, token or LFS. If the 02:28 tree already compiles with the gameplay in it, push the whole tree; S1.9 then only does the cleanup — owner: *suggested Rome-agents (lane B)* — status: in progress: lands with S1.3

- **S1.5 Unity license on pop-os (02:45 gate)** (P0 · 0.5 h · needs –) — done when: `check-license.sh` lists the license and the smoke APK build returns rc=0. Or: it failed by 02:45 (it only works with an email-and-password Unity ID), the failure is logged, and S1.8 becomes the build path — owner: *suggested Rome* — status: **done 02:03**: license active, smoke APK built rc=0 in 51 s

- **S1.6 Set up Nam's machine** (P0 · 0.75 h · needs –) — done when:
  - exactly Unity **6000.2.9f1** with the Android module (OpenJDK, SDK, NDK) is installed, never 6000.3
  - Quest 3 or 3S, and Windows or Mac, are posted in the log
  - developer mode is on and `adb devices` lists the headset
  - the OVR Metrics Tool is installed
  - "Hey Meta" and voice commands are off

  — owner: *suggested Nam* — status: open

- **S1.7 Token and keystore handoff** (P0 · 0.1 h · needs –) — done when: Rome has DM'd the Hive token (to be set as `HIVE_TOKEN`) and one shared debug keystore to Nam and dartiros, and both are stored outside the repo — owner: *suggested Rome* — status: open

- **S1.8 Skeleton on the headset (03:45 gate)** (P0 · 1 h · needs S1.4, S1.6) — done when:
  - Nam has cloned the repo, switched the target to Android, and run Virtual Bee Swarm > Bootstrap Project Settings twice
  - he has built and sideloaded it, and it opens in passthrough with `OVRHand.IsTracked` true
  - the ProjectSettings and `Assets/XR` from that run are pushed as a bootstrap commit
  - the S1.9 import was started before bed, with the machine plugged in and sleep off

  — owner: *suggested Nam* — status: open

- **S1.9 Merge the gameplay layer** (P0 · 1 h · needs S1.4) — done when:
  - a fresh clone plus the builder gives 0 compile errors, and the Linux autopilot passes
  - `VirtualBeeSwarm.unity` is gitignored
  - the builder's `Materials/` and `Meshes/` folders and the rig template are committed
  - ProjectBootstrap stays in `Editor/Setup`

  — owner: *suggested Rome-agents (lane A)* — status: in progress: merged by the integration workflow, verifying a fresh copy

- **S1.10 XR and Android config** (P0 · 1.5 h · needs S1.4, and S1.5 or S1.8) — done when:
  - there is one passthrough provider (the Meta XR feature plus Meta Quest Support)
  - Unity's Meta Quest Camera, Occlusion, Planes and Anchors features are off
  - multiview, IL2CPP ARM64, ASTC and min API 32 are set
  - there is no USE_SCENE request and no MRUK scene loading
  - boundaryless is behind a toggle
  - NativeWebSocket is vendored under `Packages/`
  - running the bootstrap twice changes nothing
  - the Android compile has 0 errors

  — owner: *suggested Rome-agents (lane B, copy A only)* — status: open

- **S1.11 XR rig in the builder** (P0 · 1.5 h · needs S1.9) — done when:
  - `MetaRigWiring.cs` is finished:
    - OVRCameraRig and OVRManager with passthrough and hands on, and CPU/GPU level set to Sustained
    - a passthrough underlay
    - a clear colour with alpha 0
    - OVRHands feeding OvrHandPoseSource
    - a visible fingertip cursor
    - no shadows
  - VirtualBeeSwarm is the only scene in the build list
  - a batch check finds every component
  - the Linux autopilot still passes
  - only code and the rig template change, no settings

  — owner: *suggested Rome-agents (lane A)* — status: open

- **S1.12 Device self-test** (P0 · 1.5 h · needs S1.11) — done when: in D builds, and from a Board button, the app reports a checklist to the Board, and a Linux player run shows the full checklist. The checklist covers:
  - passthrough, hands tracked, pinch strength
  - mic: device list, permission, 2 s input level
  - audio: one prerendered clip and one streamed PCM chunk
  - network: websocket, token, `/health`
  - decode time (ms) for 3 JPEGs
  - `Prepare` time (ms) for one clip
  - refresh rate, CPU and GPU ms
  - client version and lines hash

  — owner: *suggested Rome-agents (lane A)* — status: open

- **S1.13 Remote log** (P0 · 0.75 h · needs S10.2) — done when: warnings and above, plus `[VBS]` and `[Beeswax]` lines, are forwarded over the bus (rate-limited) to a file on the Hive host that agents can read, and a Linux player run shows up there. adb logcat stays for native crashes — owner: *suggested Rome-agents (lane C)* — status: open

- **S1.14 Build and deploy script** (P0 · 1.5 h · needs S1.10, S1.11) — done when:
  - `GameBuild.BuildApk` (type D or R):
    - runs the builder
    - copies the token in from `HIVE_TOKEN` (and fails without it), then deletes it after the build
    - signs with the shared keystore under one fixed package name
    - writes `Builds/Virtual Bee Swarm-<n>-<commit>.apk`
    - fails unless aapt2 shows RECORD_AUDIO, INTERNET, cleartext, hand tracking and PASSTHROUGH, and no USE_SCENE
  - `ops/quest-deploy.sh` (plus a Windows version if needed):
    - runs `adb install -r -g`, and on INSTALL_FAILED_UPDATE_INCOMPATIBLE uninstalls and retries
    - runs `logcat -c`
    - launches through `resolve-activity`
    - saves logcat to a file
  - Nam has run it once end to end, and a warm rebuild takes 10 min or less

  — owner: *suggested Rome-agents (lane B)* — status: open

- **S1.15 Merge check** (P0 · 1 h · needs S1.9) — done when:
  - every push gets:
    - an Android compile (copy A)
    - a Linux autopilot run (copy B)
    - a line parity check: the Hive's ids and caption hashes match Unity's
    - an empty `git diff --stat ProjectSettings Packages`, unless it is a bootstrap commit
  - each commit's result is written to `ops/builds.md`, with the last green commit at the top

  — owner: *suggested Rome-agents (lane B)* — status: open

- **S1.16 Night APK on pop-os** (P0 · 0.5 h · needs S1.5, S1.14) — done when:
  - a D APK and its build log are on pop-os by 07:00
  - Rome pulls it to his laptop at 07:30 and installs it over USB with `quest-deploy.sh` when the Quest arrives, or Nam builds his own, whichever is first
  - APKs contain the token, so they are never posted publicly

  — owner: *suggested Rome-agents (lane B)* — status: open (only if S1.5 passes)

- **S1.17 dartiros night build** (P1 · 0.5 h · needs S1.9, S1.14) — done when: if dartiros already has 6000.2.9f1 with Android installed and the Quest is with him, he makes a D build at 05:30 and runs only the self-test (5 min), and the result is logged. Otherwise skip it; art comes first — owner: *suggested dartiros* — status: open

- **S1.18 First full build on the headset (08:45 gate)** (P0 · 0.75 h · needs S1.8, S1.12, S1.14) — done when:
  - it opens in passthrough, and the hive, Beeswax and captions are visible
  - the self-test is all green, or each red item is filed as a bug
  - logcat shows the Hive accepted the token
  - the Board shows the Quest with its client version and lines hash

  — owner: *suggested Nam* — status: open

- **S1.19 Fast loop** (P0 if Nam is on Windows · 0.5 h · needs S1.18) — done when: Link play mode with passthrough over Link shows a tuning change in under 2 min. Drop this task if Nam is on a Mac — owner: *suggested Nam* — status: open

- **S1.20 Build log** (P1 · 0.25 h + ongoing · needs S1.18) — done when: every build is a line in `ops/builds.md` with number, time, commit, type, changes and result — owner: *suggested Nam* — status: open

- **S1.21 Release candidates** (P0 · 1.5 h · needs the 18:00 freeze) — done when:
  - RC1 (R build) at 17:30
  - RC2 at about 22:00, with P0 fixes only
  - RC3 only if it is called at 23:45 and built by 01:00
  - each RC passes one full run on the headset

  — owner: *suggested Nam* — status: open

- **S1.22 Build lock, 07:30 Sun** (P0 · 0.5 h · needs S1.21) — done when: the lock APK is installed, two private copies are archived plus one on the laptop, the Quest is at 100% by 08:30, and a clean rehearsal has run on it — owner: *suggested Nam* — status: open

#### S2 Director, flow and tuning

- **S2.1 Station values** (P1 · 1 h · needs –) — done when: before 03:45, the positions, hint and give-up times, lift threshold and outcome lengths are in `unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm/Tuning/station-values.json`. S2.3 imports them as defaults — owner: *suggested Adityash* — status: open

- **S2.2 Offline beat jump (D builds only)** (P0 · 0.75 h · needs S1.11) — done when: A and B jump to the next and previous beat, and pressing both thumbsticks resets, with no Hive needed. It works in the Linux player and on the first D build — owner: *suggested Rome-agents (lane A)* — status: open

- **S2.3 GameTuning with live overrides** (P0 · 1.5 h · needs S1.9, S2.1) — done when:
  - the builder reads the asset and never overwrites it
  - a `tuning.json` in `persistentDataPath` (pushed with adb), or a Hive `tuning` message, overrides it
  - the Board reloads it without a rebuild
  - the Hive hosts list lives here, with the LAN `ws://<laptop-ip>` first

  — owner: *suggested Rome-agents (lane A)* — status: open

- **S2.4 Director against the live Hive** (P0 · 1 h · needs S10.2, S1.11) — done when: in the Linux player with fake-quest (muted), the jumps from station 2 to what-if to T-0 each log one clean `beat.set`; RESET GROUP returns to `pre` with every hexagon off; timeouts fire — owner: *suggested Rome-agents (lane A)* — status: open

- **S2.5 12:00 milestone run** (P0 · 0.75 h · needs S3.1, S4.1–S4.4, S5.2, S8.3) — done when:
  - one unbroken, timed run with placeholder art covers every station's happy path, FIRE from the Board, fire by voice, T-0 and the escape
  - Nam runs the Board as Operator, with Rome coaching
  - bugs are filed

  — owner: *suggested Adityash* — status: open

- **S2.6 2-minute judge cut** (P1, or P0 if S16.1 says a judging slot is 7 min or less · 1 h · needs S2.3) — done when: Adityash has picked the beats, lane A has coded a Board preset, and the cut runs in 2:15 or less on the autopilot and 2:30 or less on the headset — owner: *suggested Adityash (code: lane A)* — status: open

- **S2.7 Timing pass** (P1 · 0.5 h · needs S2.5) — done when: 3 timed runs (outside-tester runs count) land between 4:00 and 4:30 — owner: *suggested Adityash* — status: open

#### S3 Hands and placement

- **S3.1 Hands on the headset** (P0 · 0.75 h · needs S1.18) — done when: the test cards give 10 of 10 pokes, 5 of 5 hive grabs and 5 of 5 fists; a pinch with the palm facing the user doesn't open the system menu; results are in the bug list — owner: *suggested Adityash* — status: open

- **S3.2 Hand fixes** (P1 · 1 h · needs S3.1) — done when: collider sizes, hover state, poke depth, ray length and the fingertip cursor are adjusted, and in the next build a first-timer pokes a choice within 3 s — owner: *suggested Rome-agents (lane A)* — status: open

- **S3.3 Hive placement** (P0 · 1 h · needs S1.18, S14.2) — done when:
  - the Operator places the hive on the taped spot in under 20 s, using a two-point table calibration with a controller tip (grab-and-drop as the fallback)
  - taking the headset off and on, and a recenter, are tested
  - a re-place procedure of 10 s or less is written into `pitch/demo-runbook.md`

  — owner: *suggested Nam* — status: open

- **S3.4 Headset off and on, focus loss** (P1 · 1 h · needs S1.18) — done when: taking the headset off mid-run pauses and releases the mic, putting it back on resumes at the same beat, and losing hand tracking doesn't stall a beat — owner: *suggested Rome-agents (lane A)* — status: open

- **S3.5 Session anchor** (P1 only if S3.3 shows drift after off and on · 1 h timebox · needs S3.3) — done when: a session-only OVRSpatialAnchor (not saved, so no extra permission) keeps the hive in place through off and on and a recenter. If not needed, cut it — owner: *suggested Rome-agents (lane A)* — status: open

- **S3.6 Controller fallback** (P2, or P1 if S3.1 is under 8 of 10 in hall lighting · 0.5 h · needs S3.1) — done when: a full station works with B/Y to talk and the trigger to poke — owner: *suggested Nam* — status: open

#### S4 Stations

- **S4.1 Smoker** (P0 · 1 h · needs S1.18, S8.3) — done when:
  - **By 11:30, once:** the right pick calms the swarm, lights hexagon 1, and `smoker_settle` is heard.
  - **By 16:00, both branches 3 of 3:** the wrong pick also gives an angry swarm and its line.

  — owner: *suggested Adityash* — status: open

- **S4.2 Lift and feed** (P0 · 1.5 h · needs S1.18, S3.1, S8.1) — done when:
  - **By 11:30, once:** "Light, right?" and "Rocket fuel" are heard, and syrup lights hexagon 2.
  - **By 16:00, both branches 3 of 3:** honey turns the window sick and plays the foulbrood line; the hint comes at 10 s and the give-up at 20 s.

  — owner: *suggested Adityash* — status: open

- **S4.3 Wasps** (P0 · 0.75 h · needs S1.18) — done when: by 11:30, the smaller door holds the guards and lights hexagon 3, and the bigger door plays `wasps_raid`; 3 of 3 by 16:00 — owner: *suggested Adityash* — status: open

- **S4.4 Heat** (P0 · 0.75 h · needs S1.18) — done when: by 11:30, the pebbles make the drinkers land, the glow goes off and hexagon 4 lights; 3 of 3 by 16:00 — owner: *suggested Adityash* — status: open

- **S4.5 Weightless lift** (P1 · 1.5 h · needs S4.2) — done when: pop height, spring, overshoot and bees lifting off are tuned through `tuning.json`, and 3 outside testers react with none asking "is it stuck?" — owner: *suggested Adityash (code: lane A)* — status: open

- **S4.6 Station feel pass** (P1 · 1.5 h · needs S4.1–S4.4) — done when: every station is reachable seated and standing, wrong buttons turn red and lock, retry is clear, and a first-timer finishes all 4 stations without coaching — owner: *suggested Adityash* — status: open

#### S5 What-if, embers and the meadow window

- **S5.1 Fire without voice** (P0 · 0.5 h · needs S1.18) — done when: the talk press and release timers and the Board's FIRE and PUT IT OUT buttons give the fire morph, embers follow 2.5 s after the line, and the fire goes out on command — owner: *suggested Adityash* — status: open

- **S5.2 Fire by voice** (P0 · 1 h · needs S5.1, S7.2, S10.2) — done when: "What if a fire starts?" gives a line in about 2 s, then fire and embers, and "Put it out" recovers. Once by 12:00, 4 of 5 by 16:00 — owner: *suggested Rome* — status: open

- **S5.3 What-if while the meadow is sick** (P1 · 0.75 h · needs S10.2) — done when: the fire works after the honey branch, in the Hive tests and in the Linux run — owner: *suggested Rome-agents (lane C)* — status: open

- **S5.4 Embers look** (P1 · 0.5 h · needs S5.1) — done when: colour, size and speed read clearly against passthrough and in a Quest capture clip — owner: *suggested Adityash* — status: open

- **S5.5 Meadow window on the headset** (P1, but a visible stand-in is P0 · 1 h · needs S1.18, and S9.5 or S9.6) — done when: each scene shows the right clip or stand-in, LIVE holds 10 fps or more if the gate said GO, and switching LIVE, CLIPS and OFF from the Board works 3 of 3 — owner: *suggested Nam* — status: open

- **S5.6 Frame budget for LIVE** (P1 · 0.75 h · needs –) — done when:
  - the relay downscales frames to 640×360 or less at about quality 70
  - Unity decodes at most 10 frames a second and skips the decode on a frame where a line starts
  - no off-thread JPEG decoder (it would take hours)
  - the self-test shows decoding adds under 4 ms per frame on the Quest

  — owner: *suggested Rome-agents (lane C)* — status: open

- **S5.7 Clean clip switching** (P1 · 1 h · needs S9.5) — done when: the window keeps the last frame while the next clip prepares, and the next scene's clip is prepared on a second VideoPlayer. No black flash in 3 switches — owner: *suggested Rome-agents (lane C)* — status: open

#### S6 Endings

- **S6.1 T-0 text and Earth** (P0 · 0.5 h · needs S8.1) — done when: in the Linux run the caption matches `room_t0_earth` (Earth hangs over the horizon, it doesn't rise), Earth sits over the horizon, and the window becomes the Moon — owner: *suggested Rome-agents (lane A)* — status: open

- **S6.2 T-0 on the headset** (P0 · 1 h · needs S1.18) — done when: the launch through the ceiling, the dimming, the Moon, the dusty crash back and Beeswax in view all work once by 12:00 and 5 of 5 by 16:00 — owner: *suggested Adityash* — status: open

- **S6.3 Escape on the headset** (P0 · 0.5 h · needs S1.18) — done when: the door opens, the pour-out reads in passthrough, `full_bloom` plays, and "Hive saved in m:ss" shows. Once by 12:00, 3 of 3 by 16:00 — owner: *suggested Adityash* — status: open

- **S6.4 "Grievances filed: N"** (P1 · 0.75 h · needs S1.9) — done when: an autopilot run with 4 wrong picks shows N = 4, and the card uses the Hive's score when there is one — owner: *suggested Rome-agents (lane A)* — status: open

- **S6.5 Level map wording** (P1 · 0.5 h · needs S8.1) — done when: the tiles and labels match "Other hives have it worse" (farmland, rooftop, desert) — owner: *suggested Rome-agents (lane A)* — status: open

#### S7 Mic and push-to-talk

- **S7.1 Mic permission and warm mic** (P0 · 0.5 h · needs S1.14) — done when:
  - `install -g` grants the permission, with a runtime request as fallback
  - the mic is released on pause
  - `openMicOnStart=true` with 0.25 s of pre-roll is kept
  - a fresh install records with no prompt

  — owner: *suggested Rome-agents (lane C)* — status: open

- **S7.2 Push-to-talk on the headset** (P0 · 1 h · needs S1.18, S7.1, S10.2) — done when: holding a pinch puts a transcript on the Board in under 2 s; the mic is off while Beeswax speaks; pressing talk during a line ducks or cuts the line; 8 of 10 transcripts are right in a quiet room — owner: *suggested Rome* — status: open

- **S7.3 Hall-noise test (15:00)** (P1 · 0.75 h · needs S7.2, S14.3) — done when: Rome wears the headset and says 20 phrases over crowd noise at hall level, on our router. 80% or more right → voice is the main path. Under 60% → the Operator's buttons are the main path and voice is a bonus. Lane C adjusts the keyword list — owner: *suggested Rome* — status: open

- **S7.4 Voice latency on the Board** (P1 · 0.5 h · needs S10.2) — done when: the time from end of speech to line start is logged and shown, with a median of 2.5 s or less on venue Wi-Fi — owner: *suggested Rome-agents (lane C)* — status: open

#### S8 Beeswax lines and prerender

- **S8.1 Lines generator** (P0 · 1 h · needs S1.9) — done when: `lines.json` plus `stations.json` generate `GameLines.cs` with a two-way id check; `room_pre` and `room_feed_lift_hint` are resolved; 0 missing ids, captions identical, and it compiles — owner: *suggested Rome-agents (lane D)* — status: open

- **S8.2 Prerender v1 and import** (P0 · 1 h · needs S8.1) — done when:
  - all 77 lines are rendered on pop-os into `Assets/Resources/BeeswaxLines/`
  - an AssetPostprocessor sets mono, Vorbis, 22.05 kHz, preload and decompress on load
  - every clip loads during the intro, so there is no `Resources.Load` when a line starts
  - a batch check confirms the settings

  — owner: *suggested Rome-agents (lane D)* — status: in progress: **all 77 lines rendered in Harry's voice** (hive/out/unity); import lands with the Unity project

- **S8.3 Playback check** (P0 · 0.5 h · needs S8.2, S1.18) — done when: the Linux autopilot logs a clip for every line (no caption-only lines), and 10 sampled lines play at the right volume on the headset — owner: *suggested Rome-agents (lane D)* — status: open

- **S8.4 Punch-up options** (P1 · 1 h · needs –) — done when: `hive/lines/punchup-options.md` has 2 alternatives for every wrong-pick, map and T-0 line, and flags any line that touches PLAN.md's Facts — owner: *suggested Rome-agents (lane D)* — status: open

- **S8.5 Pick the lines** (P1 · 0.75 h · needs S8.4) — done when:
  - by 14:00, Adityash's picks are in `hive/lines/lines.json`
  - only the text changes; ids are added or renamed only if `stations.json` changes with them
  - Rome reviews only the lines flagged for facts

  — owner: *suggested Adityash* — status: open

- **S8.6 Re-render the changed lines** (P1 · 0.5 h · needs S8.5) — done when: `prerender.ts --only` has run on the changed ids, the generator has rerun, the Hive tests pass, and the new clips are in the next build — owner: *suggested Rome-agents (lane D)* — status: open

- **S8.7 Voice freeze (16:00)** (P1 · 0.5 h · needs S8.6) — done when: the final render is done, every clip is within 2 dB of the median, and the manifest hash is frozen and matched by the Hive (S10.7) — owner: *suggested Rome-agents (lane D)* — status: open

- **S8.8 Captions** (P1 · 1 h · needs S1.11) — done when: captions use TextMeshPro with an outline, always face the player, and are readable at 1 m on the headset — owner: *suggested Rome-agents (lane A)* — status: open

#### S9 Orbis

- **S9.1 Orbis gate prep** (P0 · 1 h · needs –) — done when: `ops/orbis-gate.md` holds the `smoke.py` checklist, the order to try the 18 scenes, 2 safer fire prompts, a clip-capture script that passes against the mock, and a cost estimate — owner: *suggested Rome-agents (lane C)* — status: open

- **S9.2 Reactor key** (P0 · 0.5 h · needs –) — done when: the key from the Visko table at 09:00 is in the environment on pop-os and the venue laptop, never in git, and `/health` shows Orbis on. At the table, also ask whether sponsor credits count toward the paid-AI cap — owner: *suggested Rome* — status: open

- **S9.3 Spend cap** (P0 · 0.25 h · needs S16.1, S9.2) — done when:
  - by 10:30 the 2026 cap is known and written in the runbook
  - for scale: the 2025 cap was $20, about 34 min of Orbis at roughly $0.58 a minute, and 2 h of LIVE judging costs about $70
  - LIVE runs only during demo runs
  - CLIPS is the judging default, unless sponsor credits don't count toward the cap

  — owner: *suggested Rome* — status: open

- **S9.4 Orbis gate (11:00)** (P0 · 1 h · needs S9.1, S9.2, S9.3) — done when:
  - `smoke.py` runs on a desktop over the judging path (relay on the venue laptop, our router)
  - it holds 10 fps or more for 5 min
  - a scene change shows within 4 s
  - the fire prompt passes moderation
  - a 2-minute headset spot check is included in the 12:00 run
  - GO or NO-GO, and the network used, are logged

  — owner: *suggested Rome* — status: open

- **S9.5 Record the 13 demo clips** (P1, or P0 if NO-GO · 2 h · needs S9.2) — done when:
  - each clip is a 12 s ping-pong loop encoded as:
    - H.264 Main, yuv420p
    - 1280×720 or less, 30 fps
    - a keyframe at least every 1 s
    - no audio, `+faststart`
  - the fake clips are replaced
  - `Assets/StreamingAssets/` is un-ignored for the clips, so every machine that builds has them
  - the APK size is checked

  — owner: *suggested Rome-agents (lane D)* — status: open

- **S9.6 Stand-ins if there is no key by 13:00** (P1, only if triggered · 1.5 h · needs –) — done when: meadow, sick, fire and Moon look clearly different on the headset, and the Devpost says the Orbis integration is built but wasn't run live — owner: *suggested Rome-agents (lane A)* — status: open

#### S10 Hive and Board

- **S10.1 Who is editing `server.ts`** (P0 · 0.25 h · needs –) — done when: Rome has checked his own sessions first (the edit was at 02:01). If it's still unknown at 03:30, agents re-test every 20 min and take the first copy that has been unchanged for 20 min and passes every test, including `ops-pages.test.ts` — owner: *suggested Rome* — status: done (Rome's polish workflow, finished)

- **S10.2 Deploy Hive v2.1 to pop-os** (P0 · 0.5 h · needs S10.1) — done when: rsync, then restart `beeswax-hive`; `/health` is OK with 77 lines; the fake-quest smoke test passes; the station, fx and what-if buttons are on the Board — owner: *suggested Rome-agents (lane C)* — status: **done 02:40**: v2.1 live, 77 lines, Board buttons, score, custom what-ifs

- **S10.3 Version check on connect** (P1 · 0.5 h · needs S10.2) — done when: the Board shows the Quest's client version and lines hash, and turns red if they don't match the Hive's — owner: *suggested Rome-agents (lane C)* — status: open

- **S10.4 Board against the real Quest** (P0 · 1 h · needs S1.18, S10.2) — done when: every button has been tried, FIRE, PUT IT OUT and RESET work, and Nam has run the Board once as Operator — owner: *suggested Rome* — status: open

- **S10.5 Venue laptop** (P1 · 1 h · needs S10.2) — done when: the Hive, the Orbis relay and Whisper run as services with sleep off, and both the LAN `ws://` on a fixed IP and `beeswax-venue.drori.dev` answer — owner: *suggested Rome-agents (lane C)* — status: open

- **S10.6 Failover drill** (P1 · 0.5 h · needs S10.5, S1.18) — done when: when the laptop Hive is killed mid-run, the Quest moves to pop-os or offline mode and the run finishes — owner: *suggested Rome* — status: open

- **S10.7 Redeploy at voice freeze and at build lock** (P1 · 0.5 h · needs S8.7) — done when: the Hive's lines hash matches RC1 and the lock APK, and S10.3 shows green — owner: *suggested Rome-agents (lane C)* — status: open

#### S11 Art

- **S11.1 Art export spec** (P0 · 0.25 h · needs –) — done when: the README section "Where art goes" says:
  - FBX in meters, Y-up, transforms applied, +Z forward
  - Simple Lit or Unlit materials, no alpha-test wings
  - textures 1024 px or less, ASTC
  - the worker bee is one mesh, one submesh, with vertex colours
  - swaps go into `Art/<Slot>/<Slot>.prefab`, never into the scene

  — owner: *suggested Rome-agents (lane A)* — status: open

- **S11.2 Art-slot loader** (P1 · 1.5 h · needs S1.11) — done when: the builder uses `Art/<Slot>/<Slot>.prefab` when it exists and keeps the named transforms, otherwise the primitive. A test prefab dropped into `Art/Hive` swaps in, and Door and DoorPoint still work in the autopilot — owner: *suggested Rome-agents (lane A)* — status: open

- **S11.3 Worker bee, to test the pipeline** (P1 · 1 h · needs –) — done when: 300 triangles or less, about 3.5 cm, +Z forward, exported to `Art/Bee/` by 03:30 — owner: *suggested dartiros* — status: temp art in progress: a workflow is generating a stylized low-poly set (Blender scripts in `art/blender/`) + downloading CC0 assets; dartiros replaces or keeps

- **S11.4 Bee import check** (P1 · 0.5 h · needs S11.3) — done when: by 04:15 agents have batch-imported the bee and reported any axis, scale, naming or submesh problems to dartiros in the Live log — owner: *suggested Rome-agents (lane A)* — status: open

- **S11.5 Beeswax** (P1 · 3 h · needs S11.4) — done when: 15k triangles or less, faces +Z, `mouth` scales on Y, `leftWing` and `rightWing` rotate on Z, `dustyRenderers` kept; exported by 06:45 — owner: *suggested dartiros* — status: open

- **S11.6 Hive** (P1 · 1.75 h · needs S11.4) — done when: 5k triangles or less; the `Door` pivot (scale.x is the width), `DoorPoint`, `SwarmCenter` and `HeatGlow` are kept; exported by 13:15 — owner: *suggested dartiros* — status: open

- **S11.7 Swap batch 1 (13:30)** (P1 · 0.5 h · needs S11.2, S11.5, S11.6, S1.18) — done when: the bee, Beeswax and hive prefabs are in `Art/<Slot>/`, the Linux run passes, and they appear in the 14:00 build at the right scale on the table with fps holding — owner: *suggested dartiros* — status: open

- **S11.8 Portal frame** (P1 · 1.5 h · needs –) — done when: 3k triangles or less, `LivePane` and `FallbackPane` the same size, and `Embers` pointing out along +Z — owner: *suggested dartiros* — status: open

- **S11.9 Rocket** (P1 · 1 h · needs –) — done when: 3k triangles or less, with `Seat`, `Perch`, `Flame` and `CountdownDisplay` kept. If it isn't ready, use CC0 or the placeholder — owner: *suggested dartiros* — status: open

- **S11.10 Last art swap (16:30)** (P1 · 0.5 h · needs S11.8, S11.9, S11.11) — done when: the portal, the rocket and the approved CC0 models are in the 17:00 build. Anything not ready stays a placeholder — owner: *suggested dartiros* — status: open

- **S11.11 CC0 shortlist** (P2 · 1 h · needs S11.2) — done when:
  - CC0 or public-domain models are imported as prefabs for:
    - the hexagon panel
    - the smoker, honey jar, syrup bottle, deep bowl and pebble dish
    - the Moon and Earth (NASA textures, 1024 px or less)
    - the wasp, the map tiles, the score card and the choice buttons
  - colliders and `ChoiceButton` ids are kept
  - licences are listed
  - dartiros approves in 10 min

  — owner: *suggested Rome-agents (lane A)* — status: open

#### S12 Sound

- **S12.1 SFX scaffold** (P1 · 1.5 h · needs S1.11) — done when:
  - GameSfx cues are wired to director and station events:
    - the roar easing to a calm hum
    - wasp buzz
    - fire crackle
    - launch
    - Moon silence
    - hexagon light
    - poke
    - door
  - the Meta XR Audio spatializer is on
  - cues duck under Beeswax's voice
  - a muted Linux run logs every cue

  — owner: *suggested Rome-agents (lane A)* — status: open

- **S12.2 Sound files** (P1 · 0.75 h · needs –) — done when: about 10 CC0 sounds are trimmed, mono Vorbis, named by cue and in `Art/Audio/` with licences listed. Use SATIE only if we enter the Attractor prize and its licence allows a public repo — owner: *suggested Rome-agents (lane D)* — status: open

- **S12.3 Mix on the headset** (P1 · 0.75 h · needs S12.1, S12.2) — done when: levels are set, the roar eases to a hum, the crackle follows the embers, the Moon is silent, and the voice is always on top. The team agrees on it at rehearsal 1 — owner: *suggested Nam* — status: open

#### S13 Performance

- **S13.1 Passthrough render settings** (P1 · 0.75 h · needs S1.10) — done when:
  - URP depth and opaque textures are off
  - no post-processing, no HDR, no soft particles
  - render scale is 1.0
  - fixed foveation is on
  - nothing casts shadows

  — owner: *suggested Rome-agents (lane B)* — status: open

- **S13.2 Metrics from the first build** (P1 · 0.75 h · needs S1.18) — done when: the OVR Metrics overlay is on in every test, and one full run on an R build with embers, the window and casting on logs fps, CPU and GPU. Targets: main thread under 8 ms, GPU under 11 ms, stale frames under 1% — owner: *suggested Nam* — status: open

- **S13.3 Performance fixes** (P1 · 1.5 h · needs S13.2) — done when: the next R build holds a steady 72 fps — owner: *suggested Rome-agents (lane A)* — status: open

- **S13.4 Re-check after the art swaps** (P1 · 0.5 h · needs S11.10) — done when: RC1 still holds 72 fps — owner: *suggested Nam* — status: open

- **S13.5 Overnight soak** (P1 · 0.25 h + 2 h unattended · needs RC2) — done when:
  - setup:
    - an autopilot loop runs 23:15–01:15 (it ends at 01:00 if RC3 is built)
    - `am broadcast -a com.oculus.vrpowermanager.prox_close` keeps the unworn headset running
    - display sleep is at the maximum
    - logcat goes to a file on Rome's laptop that agents watch
  - results: no crash, temperature fine, reconnects work
  - `automation_disable` is run afterwards

  — owner: *suggested Nam* — status: open

#### S14 Venue, rehearsals and judging

- **S14.1 Mat print** (P0 · 0.1 h · needs –) — done when: tonight we confirm the mat print was ordered for Sat 08:00 pickup, or we order it — owner: *suggested Rome* — status: open

- **S14.2 Morning shopping run** (P0 · 1 h · needs S14.1) — done when: between 07:50 and 08:40, the mat is picked up and we have power banks, a Quest battery pack, a travel router and a lanyard. No optional props — owner: *suggested Adityash* — status: open

- **S14.3 Travel router** (P1 · 0.5 h · needs S14.2, S10.5) — done when: the laptop has a fixed IP, the Quest reaches the laptop Hive over the LAN websocket, and latency is logged before the 15:00 tests — owner: *suggested Nam* — status: open

- **S14.4 Casting** (P1 · 0.5 h · needs S1.18) — done when: scrcpy, Meta cast and `/monitor` are compared over the router, and we pick one that shows passthrough — owner: *suggested Nam* — status: open

- **S14.5 Runbook** (P1 · 0.5 h · needs S3.3, S10.6) — done when: agents fill the factual TODOs in `pitch/demo-runbook.md` and Adityash fills the rest; the roles match the house rules; 0 TODOs are left after rehearsal 1 — owner: *suggested Adityash (facts: lane E)* — status: open

- **S14.6 Rehearsal 1 (18:00, RC1)** (P0 · 1 h · needs S1.21) — done when: a full run, the 2-minute cut (run it first if slots are 7 min or less) and two reset drills of 60 s or less are done with an outside player; times and bugs are logged — owner: *suggested Adityash* — status: open

- **S14.7 Rehearsal 2 (about 22:30, RC2)** (P1 · 1 h · needs S1.21) — done when: a clean full run and a clean cut, with the pitch and practice judge questions — owner: *suggested Rome* — status: open

- **S14.8 Rehearsal 3 (07:45 Sun, locked build)** (P0 · 0.5 h · needs S1.22) — done when: one clean run — owner: *suggested Adityash* — status: open

- **S14.9 Judging table** (P0 · 1 h · needs S14.8) — done when: by 09:45 we have:
  - the taped spot
  - laptop services up
  - the router
  - the Quest charged, plus the battery pack
  - wipes
  - the casting screen
  - a spare cable

  — owner: *suggested Adityash* — status: open

#### S15 Video

- **S15.1 Capture test at first boot** (P1 · 0.25 h · needs S1.18) — done when: capture width, height and bitrate, and foveation 0, are set with `setprop` (they reset on reboot); a 20 s clip is pulled to the laptop; and passthrough shows in it — owner: *suggested Nam* — status: open

- **S15.2 Shot list** (P1 · 0.5 h · needs S16.1) — done when: by 17:30, the shots in `pitch/video-30s.md` are numbered. Walkthrough shots are included only if the rules require the walkthrough — owner: *suggested dartiros* — status: open

- **S15.3 Capture (18:45–21:15)** (P0 · 2.5 h · needs S14.6) — done when: we have at least 3 clean full runs plus close-ups of the lift, the fire embers, T-0 and the score card. Nam runs Quest capture, Adityash plays, dartiros films on a phone, and Rome runs the Board — owner: *suggested Nam* — status: open

- **S15.4 Ingest** (P1 · 1 h · needs S15.3) — done when: as clips arrive, files are pulled, with proxies, Whisper transcripts, beat timestamps from the logs, contact sheets, and an index of usable takes — owner: *suggested Rome-agents (lane E)* — status: open

- **S15.5 Safety cut (21:30)** (P0 · 0.5 h · needs S15.4) — done when: a rough 30 s vertical cut, made mechanically from the beat timestamps, is uploaded unlisted and linked in the 23:00 draft — owner: *suggested Rome-agents (lane E)* — status: open

- **S15.6 30-second vertical video** (P0 · 3 h · needs S15.4) — done when: v1 is done at 23:30 and the final at 02:00 — owner: *suggested dartiros* — status: open

- **S15.7 Finish and upload** (P0 · 0.5 h · needs S15.6) — done when: captions are burned in, loudness is fixed in a separate audio render (no loudnorm inside the concat), the video is uploaded unlisted, and it plays in a logged-out browser — owner: *suggested Rome-agents (lane E)* — status: open

- **S15.8 2-minute walkthrough** (P2, unless S16.1 says it's required · 2.5 h · needs S15.4) — done when: exported by 01:00 — owner: *suggested Adityash* — status: open

#### S16 Devpost, tracks and repo

- **S16.1 Rules and tracks** (P0 · 0.5 h · needs –) — done when: tonight the 2026 rules are in `pitch/tracks.md`, and anything marked "pending" is confirmed at 09:00. The rules must cover:
  - video length and orientation
  - required fields
  - the paid-AI cap
  - judging minutes per group
  - whether the walkthrough is required
  - whether code written before the event is allowed
  - whether we can edit after submitting
  - what AI disclosure is required

  — owner: *suggested Adityash* — status: open

- **S16.2 Team name** (P0 · 0.1 h · needs –) — done when: from the overnight votes, a provisional name is registered at 09:00 and the final one at 11:00 — owner: *suggested Adityash* — status: open

- **S16.3 Tracks** (P0 · 0.1 h · needs S9.4) — done when: a draft is set at 09:00 and the final at 11:15. If the gate is GO: Visko and Social Good. If NO-GO: Social Good and Creative/Gaming — owner: *suggested Rome* — status: open

- **S16.4 Devpost invites** (P0 · 0.1 h · needs –) — done when: the invites are sent tonight, everyone has accepted by 12:00, and we check again at submit — owner: *suggested Rome* — status: open

- **S16.5 Devpost facts** (P1 · 1.5 h · needs –) — done when: the factual TODOs in `pitch/devpost.md` and `readme-paste.md` are filled from the code (stack, architecture, what is live and what is fallback, paid AI cited), and the human-only TODOs are marked — owner: *suggested Rome-agents (lane E)* — status: open

- **S16.6 Devpost human pass** (P1 · 1.5 h · needs S16.5) — done when: the story, challenges, credits and per-track pitches are written and read aloud once — owner: *suggested Rome* — status: open

- **S16.7 Pitch script** (P1 · 1 h · needs S16.1) — done when: 60-second and 2-minute versions exist, with a timed read-through done — owner: *suggested Rome* — status: open

- **S16.8 Screenshots and GIFs** (P1 · 0.5 h · needs S15.4) — done when: dartiros picks 5 images and they are uploaded — owner: *suggested Rome-agents (lane E)* — status: open

- **S16.9 Secret scan** (P0 · 0.5 h · needs –) — done when:
  - at 21:30, and again at 07:30 Sun (agents commit all night), the repo has no Reactor, ElevenLabs, Unity or Hive token
  - the README and the MIT licence are in place
  - there is no APK in the repo or on Devpost

  — owner: *suggested Rome-agents (lane E)* — status: open

- **S16.10 Draft and early submission** (P0 · 0.5 h · needs S15.5) — done when: the draft is saved at 23:00 with the safety cut, and a full submission goes in at 01:00 if Devpost allows edits until 09:00 — owner: *suggested Rome* — status: open

- **S16.11 Final submit (08:30)** (P0 · 0.25 h · needs everything) — done when: the confirmation is on screen and all four members are on the project — owner: *suggested Rome* — status: open

### Schedule

**Sleep** (unconditional; everyone sets two alarms):

| Who | Sat | Sun |
|---|---|---|
| Nam | 04:00–08:00 | 01:15–05:00 |
| Rome | 03:30–07:30 | 02:00–05:00 |
| Adityash | 03:45–07:45 | 02:00–06:00 |
| dartiros | 07:00–11:30 | 02:15–06:00 |

Nam wakes Rome at 05:00. Rome wakes Adityash and dartiros at 06:00.

| Time | Quest | Nam | Adityash | dartiros | Rome | Agents |
|---|---|---|---|---|---|---|
| Sat 02–03 | Nam | S1.6; receives S1.7 | S16.1, S2.1 | S11.3 bee | S1.1, S1.2, S1.5 (02:45), S1.7, S10.1, S14.1, S16.4 | S1.3 → S1.4 (02:50) → S1.9; S0.1–S0.3; S11.1 |
| 03–04 | Nam | S1.8 (03:45 gate); start S1.9 import; sleep 04:00 | finish S2.1; sleep 03:45 | bee out 03:30 → S11.5 | sleep 03:30 | S1.10 ‖ S1.11; S8.1 → S8.2; S11.4 |
| 04–07 | – | sleep | sleep | S11.5 (S1.17 at 05:30 if set up); export 06:45; sleep 07:00 | sleep | S1.12, S1.13, S10.2, S1.14, S7.1, S2.2, S1.15, S6.1, S1.16, S8.4, S0.4 (07:00) |
| 07–08 | – | wakes 08:00 | wakes 07:45; S14.2 | sleep | 07:30 reads S0.5; S1.16 handoff | S2.3, S11.2, S13.1, S5.6, S16.5 |
| 08–09 | Nam → Adityash 08:45 | S1.18 (08:45 gate), S15.1, S1.19 | S14.2 until 08:40; S3.1 | sleep | S10.4 with Nam; confirms S16.1 | fixes from the remote log; S2.4 |
| 09–10 | Adityash | D builds at :00 and :30; S3.3 | S3.1, S4.1–S4.4 happy paths; S16.2 at 09:00 | sleep | 09:00 tables: S9.2, S0.6; S16.3 draft | S3.2, S9.1, S6.4, S6.5 |
| 10–11 | Adityash | builds; S14.4 | S5.1; S6.2, S6.3 once | sleep | S9.3 (10:30), S7.2 | S3.4, S5.3, S7.4, S12.1, S8.8 |
| 11–12 | Adityash | milestone build 11:30 | S5.2, S7.2 | wakes 11:30; S11.6 | S9.4 (11:00); S16.3 final | S9.5 if there is a key; S11.11 |
| 12–13 | Adityash | runs the Board as Operator for S2.5 | plays S2.5; S0.7 | S11.6 | coaches the Board, cut list | files bugs |
| 13–14 | Adityash | builds; S13.2 (R build) | S8.5, S4.5 | S11.6 export 13:15 → S11.7 (13:30) | S9.6 call (13:00); checks S10.5; S16.7 | S8.6, S10.3, S3.5 if drift, S13.3 |
| 14–15 | outside testers with Adityash | builds; S14.3; 14:30 merge and fix | testers: S4.1–S4.4 3 of 3, S4.5, S4.6, S2.7 | S11.8 | S16.7 | S13.3, S12.2, S5.7 |
| 15–16 | Rome until 15:45, then Nam | S10.6 with Rome; S5.5, S12.3 | S2.6 picks, S14.5 | S11.9 | S7.3 (15:00), S10.6 | keyword tweaks, S2.6 code, S8.7 (16:00) |
| 16–17 | Adityash and dartiros from 16:30 | 16:30 merge and fix | S5.4, S4.6 | S11.10 (16:30) | S16.6 | S10.7, rehearsal cards |
| 17–18 | Nam | integration only; RC1 (S1.21) at 17:30; S13.4 | S14.5 | S15.2, edit template | S16.6 | RC1 compile, autopilot, parity check |
| 18–19 | everyone | **FREEZE.** S14.6 as Operator | Mediator | Spotter | Host | bug triage |
| 19–21:15 | capture | S15.3 | player | phone camera | Board | S15.4 as clips arrive |
| 21–22 | Nam | P0 fixes → RC2 at 22:00 | picks takes | S15.6 | S16.6 | S15.5 (21:30), S16.9, S16.8 |
| 22–23 | everyone from 22:30 | S14.7 | S14.7 | S15.6 | S14.7 pitch; S16.10 draft at 23:00 | runbook facts |
| 23–01 | soak | S13.5; RC3 call at 23:45; builds RC3 only if called | S15.8 only if required | v1 at 23:30 → v2 | S16.6; S16.10 early submit at 01:00 | watches the soak; S15.7 on v1 |
| Sun 01–02 | Nam | installs RC3 if built; sleep 01:15 | notes on v2 | v2 | Devpost | P0 fixes only |
| 02–05 | – | sleep | sleep 02:00–06:00 | final export 02:00; sleep 02:15 | sleep 02:00–05:00 | S15.7 final upload, S16.8 |
| 05–06 | Nam | wakes 05:00; smoke test; wakes Rome | sleep | sleep | puts video links into Devpost | Linux regression on the lock candidate |
| 06–07 | Nam | lock candidate 06:30, 2 full runs | wakes 06:00 | wakes 06:00, packs | final Devpost review, laptop services | S10.7 parity check |
| 07–08 | everyone | S1.22 (07:30), S14.8 (07:45) | S14.8 | S14.8 | S16.9 (07:30), S14.8 | archives APKs privately |
| 08–09 | judging table | S14.9 | S14.9 | packs, helps | 08:15 final read; **S16.11 at 08:30** | – |

### Critical path
S1.5 or S1.8 (a licensed Android editor, 02:45/03:45) → S1.4 skeleton (02:50) → S1.9 + S1.11 merge and rig (~05:00) → S1.12 + S1.14 self-test and deploy script (~06:00; S8.2 voice and S10.2 Hive feed in here) → **S1.18 full build boots (08:45)** → S3.1 and S4.1–S4.4 happy paths (10:30) → S5.2 fire by voice (11:30) → **S2.5 milestone (12:00)** → S11.7 and S11.10 art swaps (13:30, 16:30) → RC1 (17:30) → rehearsal 1 (18:00) → capture (21:15) → safety cut (21:30) → final video (02:00) → build lock (07:30) → **submit (08:30)**.

### Top risks
1. **No Android build path that works.** pop-os has never compiled for Android, and the XR config has never been built.
   - Two paths tonight: the S1.5 license gate at 02:45 and Nam's S1.8 skeleton at 03:45.
   - S1.10 removes the known launch risks: USE_SCENE, MRUK scene loading, and boundaryless now sits behind a toggle.
   - The 08:45 gate falls back to Link play mode.
   - If the skeleton fails for no clear reason, try the Meta SDK version the mentors' samples use. Version 209 is 4 days old.
2. **One headset.**
   - Nam builds while Adityash tests, with D builds every half hour from green commits only.
   - `tuning.json` and the beat jump turn feel fixes into seconds instead of rebuilds.
   - Test cards, the Link loop, and the second-Quest ask add capacity.
   - Charge the Quest during every build.
3. **Integration collisions.**
   - One writer per path, and the scene is a build output.
   - Settings come only from copy A or Nam's bootstrap commit.
   - One shared keystore, with uninstall-and-retry as the fallback.
   - The token is injected from `HIVE_TOKEN`.
   - The S1.15 check catches stray ProjectSettings and Packages diffs.
4. **Beeswax silent, saying stale lines, or stuttering.**
   - The S8.1 two-way id check.
   - The v1 prerender lands tonight.
   - Clips preload during the intro.
   - The lines hash is checked against the Hive on connect.
   - Voice freeze at 16:00.
   - Captions are always on, and the Board's "say a line" is the manual override.
5. **The venue: network, noise, heat and cost.**
   - The LAN websocket comes first on our own router, and the relay runs on the venue laptop.
   - The Operator's buttons are always available, and the 15:00 noise test decides the main path.
   - Sustained CPU/GPU levels, a soak with `prox_close`, and a battery pack.
   - The spend cap is set, with CLIPS as the judging default.

Also watch: art arriving late (CC0 or placeholder at 16:30), and the video (safety cut at 21:30, early submission at 01:00).

### Overnight agent queue (02:35 → 07:15)
- **Unity jobs**, in this order, one at a time per project copy: S1.3 → S1.4 (02:50) → S1.9 → S1.10 (copy A) ‖ S1.11 (copy B) → S1.12 → S1.14 → S2.2 → S1.15 → S6.1 → S1.16 (only if licensed) → S2.3 → S11.2 → S13.1 → S6.4 → S6.5 → S12.1 → S8.8.
- **Running alongside** (no Unity job of their own):
  - lane C: S10.1 rule → S10.2 → S1.13 → S7.1 → S5.6 → S5.3 → S9.1
  - lane D: S8.1 → S8.2 → S8.4
  - lane E: S0.1 → S0.3 → S0.2 → S0.4 (by 07:00) → S16.5 → runbook facts → S0.5 (07:15)
  - lane A, out of order: S11.1 before 03:30, and S11.4 as soon as the bee lands
- **Rules:**
  - run the Android compile and the Linux autopilot after every merge
  - never deploy when a test fails
  - no secrets in the repo
  - audio off in every test
  - the report lists anything that didn't land at the top

---

## Concept v3 (CURRENT): Virtual Bee Swarm — Save the Hive (or the Bees Leave for the Moon)

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

### Glasses (Mentra Live): adapt later
The glasses are a second priority: we build the Quest game first and fit the glasses in after. Options that already work with the backend:
- **A second mic** for the what-if questions (glasses transcription → same voice pipeline).
- **Beeswax in your ear** for asides and hints.
- **Real props:** a second player looks at a REAL prop on the table (honey jar vs. syrup bottle, pebble dish vs. bowl) and that makes the station choice. This is the strongest pitch for the ManageXR track ("AI in the physical world with smart glasses").

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
- **Mentra Live:** no display. The old `@mentra/sdk` / AppServer / ngrok is **retired**: ignore tutorials and AI suggestions that use them. Use a **miniapp** (`@mentra/miniapp`, Bun/TS, beta) that runs on the phone, with `session.transcription`, `camera.takePhoto()` → signed URL (~30 min), `speaker.speak/play/createStream`, buttons/touch, `led`, `fetch`/WebSocket. iPhone means Mentra App 3.3, so `@mentra/miniapp@3.3.0`. Dev: laptop + iPhone on the same network (iPhone hotspot), `bun dev` → scan the QR in the Mentra App. Demo: `mentra-miniapp release`. Docs index: https://docs.mentraglass.com/llms.txt
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
| quest | `beat.set{n, name}` | Authoritative beat, rebroadcast to all. `hook` starts the score clock; `escape` sends `score` and sets the window to `full_bloom`; `t0` sets `moon` |
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
| `POST /glasses/press {pressed_at, button?, press_type?}` | token | → `{allowed, scan_id, line_id}` (`ear_not_now` outside scan beats or while a scan runs) |
| `POST /glasses/scan {scan_id, photo_url?, heard?, pressed_at}` | token | → `{item, verdict, line_id, audio_url, caption, path, ms}`; 409 + `ear_not_now` when not allowed |
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
