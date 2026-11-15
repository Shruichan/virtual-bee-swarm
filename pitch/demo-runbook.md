# Demo runbook (judging table)

> Judging is Sun 10 AM to 12 PM. Hacking ends and Devpost closes at 9:00 AM, so setup is 9:00 to 10:00.
> TODO: judging format (pairs or solo? open hall or booths? minutes per group?). Fill in once someone reads the dashboard. The full run below is about 4 minutes in the headset plus 2 minutes of talk. **If a judge says "we have two minutes," use the [2-minute judge cut](#2-minute-judge-cut).**
> **At the table, use `demo-card.md`** (one card). This file is for setup and for the night before.
> Print this or keep it open on a second phone. **Never put the Hive token on a screen judges can see**, and never write it in this file.

## Roles

| Role | Who | Job |
|---|---|---|
| **Mediator** (wears the "union mediator" lanyard) | TODO | Greets judges, does the 30 s intro, fits the headset, says the nudge lines below, does the after-pitch. The only person talking to the player. |
| **Operator** | Rome (TODO confirm) | Laptop + Beat Board. Watches the run, steps in only when something fails. Doesn't talk unless asked a backend question. |
| **Spotter** | TODO | Stands next to the player. Safety (cables, table edges), hands over props, resets the table between groups. |
| **Host** | TODO | Talks to the second judge or anyone waiting. Points at the cast screen and explains what they're seeing. Takes the questions while the player is still in the headset. |

## The night before / Sunday morning

- [ ] **7:30 AM build lock.** Keep the sideloaded APK on the laptop (TODO: path). If the app breaks on the headset: `adb install -r <apk>`.
- [ ] Devpost + 30 s video submitted by ~8:30 AM (see `devpost.md`, `video-30s.md`).
- [ ] Everyone has read `judge-qa.md` once.
- [ ] Mediator has done the intro out loud at least twice.

## Setup checklist (9:00 to 10:00)

**Power**
- [ ] Quest at 100%, with its charging cable and a USB-C power bank at the table. TODO: is it a Quest 3 or 3S? Either way, plan on it not lasting the full two hours in mixed reality: plug it in between groups.
- [ ] Laptop plugged in, **lid open, sleep off.** (Rome's laptop really does suspend after ~15 quiet minutes. Closing the lid freezes the browser with the Beat Board in it.)
- [ ] Phone (hotspot) plugged in or on a power bank. Hotspots drain phones fast.
- [ ] [GLASSES] Mentra Live charged; miniapp release build open on the iPhone.

**Network**
- [ ] Our own hotspot (TODO: phone hotspot or travel router, and whose) is up. Quest and laptop both on it, not on venue Wi-Fi.
- [ ] Hive running on the laptop (`cd hive && bun run start`) and the Orbis relay running live (`cd hive/orbis-relay && uv run --env-file ../.env python relay.py`). TODO Rome: confirm these are still the right commands on Sunday.
- [ ] Tunnels up. Both of these answer: `https://beeswax-venue.drori.dev/health` (primary) and `https://beeswax.drori.dev/health` (backup, home server). The headset tries venue first and moves to the backup after 3 failed connects.
- [ ] Beat Board open on the operator's phone or laptop (`/board?token=…`). The Quest shows as connected.
- [ ] Spectator page or cast open on a second screen for the host and waiting judges. TODO: which one works: scrcpy, Meta casting, or `/monitor?token=…` (token not visible on screen).

**Window (Orbis)**
- [ ] [LIVE] Prewarm about 2 minutes before the first group: Board → Window → **LIVE**. TODO: write down how long connect + GPU + first frame really took at the 11 AM gate (the relay gives up on connecting after 90 s). Board should show `ready` and a frame rate of 10+.
- [ ] [CLIPS] Board → Window → **CLIPS**. Check that each clip plays on the headset.
- [ ] Either way: flip LIVE → CLIPS → LIVE once and watch the window switch, so we know the fallback clips are loaded on the headset.
- [ ] TODO Rome: the fire prompt was tested in the Reactor sandbox (moderation can end the session). If it was flaky, plan to switch to CLIPS just for the fire beat.
- [ ] Board says the model is **Orbis Stable** (about $0.58/min), not Dynamic (about $1.25/min).

**Voice**
- [ ] Mic permission granted on the headset (no popup).
- [ ] Say "what if it rains" in the headset and see the transcript appear on the Board within a few seconds. TODO: if the venue is loud, decide between push-to-talk and always listening.
- [ ] Quest volume high enough to hear Beeswax over the hall.

**Play area**
- [ ] Mark the player spot and clear the floor and cable paths.
- [ ] Check that the default meadow rig stands clear of furniture and the order tickets and lever are within reach.
- [ ] Keep the play area brightly and evenly lit for hand tracking.
- [ ] Props in their start spots (TODO: only if physical props made it): honey jar, syrup bottle, pebble dish, deep bowl, smoker.
- [ ] Face-cover wipes, tissues. Cables taped down.
- [ ] Stopwatch on someone's phone.

**Orbis cost guard**
- Orbis bills about $0.58 per connected minute, from the moment the session is `ready` until it ends. Warm-up and gaps between groups count.
- **Rules risk, decide before 9 AM:** 2025 capped paid AI at $20 (per our notes). Two hours of live Orbis during judging is about $70. TODO (whoever reads the 2026 rules): do sponsor-provided Orbis credits count toward any cap? If nobody can confirm, run **CLIPS** for judging and go LIVE only when a judge asks to see it.
- The relay stops a session after 90 s with no viewers, and **hard-stops any session at 15 minutes**. With back-to-back groups, the session can hit that cap in the middle of someone's demo. Before each group, check the session time on the Board/status; if it's past ~10 minutes, flip OFF then LIVE to get a fresh one. TODO Rome: or raise `ORBIS_MAX_S` for the judging window and accept the cost.
- Gap of more than 2 minutes between groups: Board → **OFF**, then LIVE again 2 minutes before the next group.

---

## The run (about 4 minutes)

### Before the clock (≤ 60 s)

**Mediator:**
> "Hi! I'm the union mediator. Poke SIGN HERE when the bee hands you the clipboard. Once the hive arrives, you have four minutes to fix it or we're headed to the Moon. When Beeswax asks for a what-if, pinch and hold to talk."

Fit the headset. **Spotter** — "Can you see the meadow? Hold up your hands."
**Operator:** Board mode SOLO (or DUO if glasses), window LIVE and `ready`, Quest connected.

### On the clock

| Time | Beat | Player does | Beeswax (in game) | Mediator (only if needed) | Operator |
|---|---|---|---|---|---|
| 0:00–0:15 | Hook | Signs the worker bee's clipboard and watches the hive arrive | Speaks from inside the hive | Point out SIGN HERE if needed | Watch the beat change on the Board. |
| 0:15–0:35 | Meet + window | Watches Beeswax emerge and the evacuation rocket arrive | Gives the four-minute ultimatum | | Check the window is ready. |
| 0:35–1:05 | 1. Smoker | Orders the smoker and pumps the lever | Reacts to the choice | "Which one would you want if you were a bee?" | |
| 1:05–1:45 | 2. Lift + feed | Lifts the hive, orders syrup, and pours it into the roof feeder | Explains what store honey can do | "Try lifting it. Heavy?" | |
| 1:45–2:20 | 3. Wasps | Orders the reducer and slots it into the entrance | "Smaller door, fewer guards needed to hold it." | | |
| 2:20–2:55 | 4. Heat | Sets down the pebble dish and pumps water | "Pebbles! Somewhere to land…" | | |
| 2:55–3:20 | What-if | Asks Beeswax "What if a fire starts?", then "Put it out!" | "Don't. Say. That." / "Okay. It's out." | Freezes: "Ask him what happens if there's a fire." Not heard: "Louder, he's a bee." | Not heard twice: trigger fire from the Board (TODO Rome: what-if buttons). Same for "put it out." |
| 3:20–3:40 | Hive saved | Watches the bees fly out and gets the scorecard | "Motion carried. No ravaging today. Cancel the evacuation." | | |
| 3:40–4:15 | T-0 + map | Pulls the launch lever, watches the Moon and crash, and gets the new contract | "TAKE ME TO THE MOON!" / "...Not one flower." | Let the Moon play. | |

**Running long?** Keep the honey mistake, fire, and Moon. Beeswax grants an extension if the boarding clock runs out.

### After the headset comes off (60 to 90 s)

**Mediator:**
> "So: every station is real beekeeping. Did you pick the honey? It sounds like the nice option. Store honey can carry a disease that kills hives."
> [LIVE] "That window was Visko's Orbis world model, making the meadow live. When you said 'fire,' we changed its prompt mid-stream, and it morphed from the frame it was showing. It never cuts."
> [CLIPS] "That window was footage we generated with Visko Orbis for each scene. The live stream is off today because TODO."
> "All of Beeswax's own lines are pre-recorded, the AI only picks from a list, and if the Wi-Fi dies the demo still finishes. Questions?"

If they picked syrup: "Nice. The honey sounds like the kind option, and it can carry spores of a disease that kills hives. The spores last for decades." TODO: once the Sat 6 PM run has a real count ("N of M people picked the honey"), say that instead.
If there's time and the window is live: "Want to give Beeswax one more what-if?" TODO: list which what-ifs actually work (rain, clover, spray, winter are written, none tested). If none work besides fire, skip this line.

Then go to `judge-qa.md`.

---

## 2-minute judge cut

For "we have two minutes." Skips stations 1, 3 and 4, keeps the three things people remember.

| Time | Beat | Operator (Board) | Mediator |
|---|---|---|---|
| Before | Intro, headset on | Board → RESET GROUP, then jump to **station 2** | "Beeswax needs us to save his hive or we have to evacuate to the Moon. Check how heavy the hive feels." |
| 0:00–0:40 | Station 2: lift + feed | (nothing) | If they go straight to syrup, cover the honey mistake in the recap. |
| 0:40–1:10 | What-if | Jump to the **what-if** beat | "Ask him: what if a fire starts?" |
| 1:10–1:50 | T-0 | Jump to **T-0** | Nothing. Let the Moon play. |
| 1:50–2:00 | Off | | "Did you pick the honey? It sounds like the nice option, and it can kill a hive. Questions?" |

TODO: run this cut once on Saturday with a stopwatch.

---

## When things fail

| What you see | What it probably is | Do this | Say this (only if asked) |
|---|---|---|---|
| Window freezes or goes black, Board shows `failed` or 0 fps | Orbis stream dropped | Nothing during the run: the headset switches to clips by itself after 8 s. Operator: Board → CLIPS to lock it for the rest of this group. Retry LIVE between groups. | "The live stream dropped, so the window switched to the recorded version of the same scenes." |
| Session ends right after the fire prompt | Orbis moderation | Same as above. Use CLIPS for the fire for the rest of the day. | Same. |
| Player talks, no transcript on the Board | Mic, noise, or speech-to-text down | Mediator: "Louder, he's a bee." Then operator triggers the beat from the Board. | "The hall's loud. Let's try once more." |
| Transcript shows up but nothing happens | Intent missed (said something we don't handle) | Mediator: "Ask him 'what if a fire starts.'" Operator triggers it if needed. | |
| Board says the Quest is disconnected (Hive down) | Server crashed, tunnel down, or hotspot dropped | Don't stop the demo. Unity keeps going on its offline timers, lines are in the app, window plays clips. Operator: check the hotspot, restart `bun run start`, the headset reconnects by itself. TODO U1: confirm what triggers the fire beat when the Hive is down. | "The server dropped, so it's running offline. You're seeing the same game minus voice." |
| Hive is floating or inside furniture | Room tracking or placement | Check tracking and recenter in the clear play area. Restart the app if placement remains wrong. | |
| Hands not tracking | Light or hands out of view | Spotter: "Bring your hands up in front of you." Floating buttons are the fallback. | |
| Headset battery under ~20% | | Finish this group plugged in, then charge. Swap order with talk time. | |
| App crashed | | Relaunch from the headset library. Board → RESET GROUP. If it keeps crashing, reinstall the locked APK. | "We'll restart it. Takes 20 seconds." |
| Judge doesn't want to wear the headset | | A teammate plays, the judge watches the cast and calls out the what-if for the player to repeat. [GLASSES] Offer the glasses role instead. | |

## Reset between groups (target: 60 s)

1. [ ] Board → **RESET GROUP**. Headset goes back to the start. TODO U1: confirm reset puts Unity back to the `pre` beat with all four hexagons off.
2. [ ] Window: still `ready`? Session time under ~10 min? Otherwise OFF → LIVE. Long gap expected: OFF.
3. [ ] Props back to their start spots. Hive lined up with the tape.
4. [ ] Headset: wipe the face cover, check battery, plug in if under 40%.
5. [ ] Board mode SOLO or DUO for the next group.
6. [ ] Write one line in the notes: time, what broke, what they laughed at, anything a judge asked that we couldn't answer. (This is how `judge-qa.md` gets better during the morning.)
