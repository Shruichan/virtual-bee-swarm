# Live Bee Scout: booth run-of-show

> Glasses-only demo for the ManageXR track ("AI in the physical world: scavenger hunts with AI characters in your ear").
> One judge wears the Mentra Live, walks a short path of bee props, and Beeswax talks in their ear. Everyone else watches the dashboard.
> Target: about 2 minutes, scripted order, nothing open-ended.

## 1. Camera geometry

**Formula (pinhole, object near frame center):**

`width_px = 1280 * S / (2 * D * tan(HFOV/2)) = f * S / D`

| HFOV | f (px) | px per degree |
|---|---|---|
| 118° (spec, worst case) | 385 | ~11 |
| ~80° (what the stream looks like in footage) | ~760 | ~16 |

**What the footage shows (session s1, 6 clips, 1280x720 at ~14.6 fps):** the 720p stream is much tighter than 118°. A 15" MacBook keyboard (~27 cm) spans 465 to 565 px at an estimated 45 to 55 cm, which gives f ≈ 850 to 920 px, or about 70 to 85° horizontally. There's only mild barrel distortion. The 118° figure is probably for full-sensor photos or a diagonal measurement. **Size everything for 118°, expect about 2x more pixels in practice.**

**Object width in pixels (118° / ~80°):**

| Size S | 0.5 m | 0.8 m | 1.0 m | 1.5 m | 2.0 m |
|---|---|---|---|---|---|
| 8 cm (honey jar) | 62 / 122 | 38 / 76 | 31 / 61 | 21 / 41 | 15 / 30 |
| 15 cm | 115 / 228 | 72 / 143 | 58 / 114 | 38 / 76 | 29 / 57 |
| 20 cm | 154 / 304 | 96 / 190 | 77 / 152 | 51 / 101 | 38 / 76 |
| 30 cm | 231 / 456 | 144 / 285 | 115 / 228 | 77 / 152 | 58 / 114 |

- **Detector rule (needs 80 px or more):** worst-case max distance ≈ `S(cm) / 20` m. So a 20 cm prop works out to 1.0 m, and a 30 cm prop to 1.5 m.
- **Recommendation:** every station should be **at least 20 cm wide**. Small props (honey jar, spray bottle) sit on a 25 to 30 cm colored placard or tent card. The judge stands so the eye-to-prop line of sight is **0.6 to 1.0 m**: standing at the table edge with the prop in the middle of the table gives about 0.9 m.
- **AprilTag 36h11** (8x8 cells including the black border, about 30 px needed for a reliable read with some blur): max distance = `f * tag / 30`.

| Tag (black square) | 118° | ~80° |
|---|---|---|
| 5 cm | 0.6 m | 1.3 m |
| 10 cm | 1.3 m | 2.5 m |
| 12 cm | 1.5 m | 3.0 m |
| 15 cm | 1.9 m | 3.8 m |

  Use **12 cm tags** on matte letter-size tent cards, with a white quiet zone of at least one cell around each tag. Tags lose detections fast with motion blur, so the judge has to pause.
- **Field of view at 0.9 m:** 3.0 m wide at 118°, 1.5 m at 80°. With props 60 cm apart, **neighbors will be in frame**. The game has to pick the expected-next object (and the most central box), not just any object.
- **Aim offset: the camera points about 15° below where the wearer looks.** In every clip where the wearer reads the laptop screen, the screen sits in the top third of the frame and is often cut off at the top edge, while hands and keyboard fill the middle. Horizontally the looked-at thing is roughly centered (slightly left). Detections only fired when the wearer leaned in until the screen filled the frame.
  - Consequences: put props **low** (tabletop, 75 to 100 cm). Never put them at eye level, where they drop off the top of the frame. The presenter cue is "look down at it like you're reading the label."
- **Timing from the log:** the vision worker averages 9.4 fps (with dips to 0.5 fps during stream stalls). `look_ms` ≈ 0.94 s and frame-to-found ≈ 1.0 s, so the judge has to hold still on a prop for **about 2 s**. Head turns blur badly at 15 fps.
- **Exposure:** screens blow out (saturated yellow), while matte white and colored surfaces look fine. Use printed matte pictures, not screens.

## 2. Booth layout

One or two 6 ft tables (1.8 m x 0.76 m each) against the back of the booth. **The judge faces the back wall and the crowd stays behind them**, so the camera sees backdrop instead of people.

```
                    BACK WALL / CURTAIN  (camera looks this way)
  +-----------------------------------------------------------------------+
  | [ matte navy tri-fold ]   [ matte navy tri-fold ]   [ matte navy tri-fold ] |
  |  (6)HIVE   (5)WATER     (4)BEE HOTEL  (3)SPRAY     (2)HONEY    (1)FLOWER |
  |  box       dish, 15cm   on 20cm       on placard   on placard  pot, flat |
  |  (opt.)    riser (opt.) riser                      + riser             |
  |========= TABLE B (1.8 m) ==========|========= TABLE A (1.8 m) =========|
  +-----------------------------------------------------------------------+
                            ^ stop       ^ stop       ^ stop      ^ START
     (stop tape at the table edge, one mark per station, ~60 cm apart)
                                                                 
   [DASHBOARD: laptop/monitor on stand,          <-- judge walks right to left
    left end, angled 45° to aisle, eye height]   presenter walks beside them
   [operator stands at dashboard, hand on keys]     crowd/other judges: behind the tape,
                                                    near the dashboard
```

Fill in the order with the content lane's final list. The default order, also the walking order: **(1) flower → (2) honey jar → (3) spray bottle → (4) bee hotel**, with water dish and hive as optional stations 5 and 6.

- **Spacing:** about 60 cm center to center (one big step). Alternate prop heights (riser / flat) so neighbors don't line up in the same band of the frame.
- **Heights:** all props between 75 and 105 cm off the floor (tabletop plus a 0 to 25 cm riser). Nothing above 120 cm.
- **Backdrops:** matte navy or black tri-fold foam boards (36x48 in) behind each pair of props. Wood, yellow, white, green, and flowers all pop against navy. No logos, laptops, people, or cables behind the props. Cover the table with one plain matte cloth.
- **Placards:** each station gets a letter-size matte tent card with a big photo of the object, its name in large type, and a 12 cm AprilTag. The detector can lock onto either the prop or the card.
- **Light:** one or two clip-on LED lamps (diffused, warm white) above the tables, pointed down at the props. No window, screen, or bright lamp behind any prop. Angle the honey jar so it doesn't throw glare.
- **Dashboard:** at the finish (left) end, facing the aisle, so the judge ends the walk next to it. It must **not** be visible from the stop marks: it shows the props and could re-trigger.
- **Enrol on site:** recapture the detector's reference frames from the glasses, at the booth, under booth light, from the stop tape, Sunday 9:00 to 9:30.

## 3. Run-of-show (2 minutes, one judge)

Roles: **Presenter** (talks, fits glasses, walks beside the judge) and **Operator** (dashboard, hand on keys, silent).

| Time | Who | What |
|---|---|---|
| Before | Operator | Fresh stream restart (`scout-down/up` or relaunch the miniapp) **just before** the judge arrives. Battery above 40%. Dashboard shows a live video tile. Game reset. |
| 0:00 | Presenter | **Pain hook (15 s):** "Maya wants to help bees. At the garden center she's facing a wall of labels: which plant, which spray, does it even matter? She won't read a pamphlet. So we put a bee in her ear." |
| 0:15 | Presenter | "No screen, no phone. Just look around. Put these on, stand on the first tape mark." Hands over the glasses **already streaming**. |
| 0:20 | Beeswax | Intro: "Hi, I'm Beeswax. Walk up to something and stare at it. I'll do the rest." |
| 0:25 | Judge | Looks down at the flower for about 2 s. |
| 0:27 | Beeswax | "Bee fact found! Would you like to learn more?" |
| 0:29 | Judge | "Yes" (or presses the glasses button) |
| 0:30 | Beeswax | "This is a native flower. ... Now go to your next adventure: one big step to your left." (fact ≤ 10 s) |
| 0:45 | | Station 2, honey jar (same beat) |
| 1:05 | | Station 3, spray bottle: the twist, "this one hurts bees" |
| 1:25 | | Station 4, bee hotel, finale: "You found them all. Bee Scout badge earned. Go tell a human." |
| 1:40 | Presenter | Turns the judge toward the dashboard. **Close (20 s):** "Everything you just did was live: the camera saw it, you answered out loud, Beeswax reacted. Glasses-native: camera, mic, and speaker, no display. The same engine turns any room into a game board: museums, garden centers, school field trips." |
| 2:00 | | Questions. Wipe the glasses, put them on the charger. |

**Judge mode = 4 stations.** At about 20 s per station (2 s look, 3 s hook, 2 s answer, 10 s fact, 3 s walk), six stations run about 2:40. Keep 5 and 6 for relaxed visitors or a judge who asks for more.

**Other judges:** they stand by the dashboard and watch the live boxes, the "Bee fact found" banner, and the caption. Beeswax audio is mirrored on the laptop at low volume so they hear it too. Keep that speaker behind the judge so the glasses mic doesn't pick it up.

### Recovery ladder (escalate one step every ~3 s)

1. **No chime after 3 s staring.** Presenter: "Lean in and look down at it, like you're reading the label." This fixes the camera aim offset.
2. **Still nothing at 6 s.** Operator forces the expected object (needs a "force found" key on the dashboard, see asks).
3. **"Yes" not heard.** Presenter: "Or just tap the button on the right arm." (`/scout/button` already means yes.) Operator can POST the same.
4. **Silence.** For the booth, change silence from "skip" to **"I'll take that as a yes"** (`offerTimeoutMs` 6 s → 4 s).
5. **Stream frozen** (dashboard video stuck for more than 3 s). The watchdog restarts in ~8 s. Presenter fills the gap with the prop in hand ("while she finds her wings, here's the jar..."). If it's not back in 15 s, the operator plays the station's lines from the laptop speaker.
6. **Total failure.** Play the 30 s demo video on the dashboard and pitch over it.

## 4. Risks and mitigations

| Risk | Mitigation |
|---|---|
| **Stream drops every 1.5 to 3 min** (i/o timeouts in s1) | Dedicated phone hotspot (not venue Wi-Fi), phone within 2 m of the glasses, laptop on the same hotspot. Drop to **1.2 Mbps, 10 fps, keep 720p** (default is 2 Mbps at 15 fps, and the detector needs the pixels). Keyframe every 1 s. **Restart the stream right before each judge** so the 2 min falls inside a clean window. Watchdog 8 s → 4 s. |
| **Glasses battery and heat** | Stream only during visits. On the charger between judges. Check battery in MentraOS before each judge. Power bank at the booth. Take the glasses off if the temple gets warm. |
| **Noise vs. spoken "yes"** | Glasses button = yes, operator yes key, silence = yes, broad YES list (already in `scout.ts`). Ask the judge to answer clearly. Note: in s1, five "Yes." utterances came back as `answer: other` because the game was in `looking`, not `offering`. Confirm that a "Yes." during an offer maps to yes. |
| **Crowds in the camera** | Judge faces the back wall. Only the expected-next object can trigger, with ~1 s dwell. Navy backdrops. Bystanders wait by the dashboard, behind the tape. |
| **Lighting** | Own LED lamps, matte prints, no backlight. Enrol on site under booth light. |
| **Neighbors in frame** | Expected-next-only matching plus "most central box wins". 60 cm spacing with alternating heights. |
| **Tall/short judges** | Props are low and the stop tape fixes distance. The presenter's "look down at it" cue covers both. |
| **Judge wanders off order** | Beeswax: "Ooh, wrong one, sweetie. Your next fact is to your left." (optional line) |

## 5. "Walk this way"

**Doable tonight:**
- **Scripted direction** baked into each fact's last line ("one big step to your left"). Zero risk, works even blind.
- **Live left/right when the next station is visible.** At 0.9 m and 60 cm spacing, the next prop or tag is usually at the frame edge. Use the expected-next box or tag center x: under 480 → "a bit to your left", over 800 → "a bit to your right", otherwise "right in front of you, look down". If the box is in the top 150 px → "lift your chin a little". If the box is under 80 px wide → "come closer". Pre-render about 5 short lines, play at most one every 6 s.
- **Nudge on timeout:** no expected object for 8 s → repeat the scripted direction, sassily ("Left. Your other left.").

**Not tonight:** knowing where the judge is in the room when no station is visible (no SLAM, no beacons), IMU turn-by-turn, meter-accurate distances, anything behind the judge.

## Asks for other lanes

- Game: booth mode with expected-next-only matching, silence = yes, offer timeout 4 s, direction lines, and a "force found" plus "yes" key on the dashboard.
- Glasses: stream 720p, 10 fps, 1.2 Mbps, 1 s keyframes. Watchdog 4 s.
- Content: facts at 10 s or under, each ending in its direction line. The 4 judge-mode stations first.
