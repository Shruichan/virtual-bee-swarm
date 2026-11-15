# Audit of sort-bee-assets @ f15d953 (25 commits ahead of main) — 5 read-only reviewers + tests

Tests on this exact commit: Hive typecheck clean, bun test 459/459. Unity two-ride smoke: see PLAN line.

## Verdicts per request
| Request | Verdict | Key evidence / gap |
|---|---|---|
| Remove stray small natural hive + leftover lever | DONE | WildBeeNest off by default (DressingDirector:33); lever hidden unless live (HoneyLever.RefreshVisibility). Side effect: solitary bee + habitat block also gone. |
| Rocket tiny next to nest, grows to board, small for crash | DONE w/ bug | T0Sequence Awake `FindAnyObjectByType<HiveBody>()` skips inactive objects -> null hive => board spot/crash side/outward push wrong. Use FindObjectsInactive.Include. |
| Rock the hive; early release -> retry line | DONE (minor) | HiveBody.TrackRock + RockReleasedEarly. Too chatty: hint right after prompt + at 8 s + retry on each release; StopVoice cuts Beeswax mid-line. room_feed_intro still says "Now lift the hive". Autopilot never rocks (GameAutopilot:273) -> smoke only exercises the failure path. |
| Error sound + warning symbol, then wasps | PARTIAL | "!" text pill + stamp_grievance; wasps "! WASPS INCOMING" + 0.12 s countdown_beep. No icon; not a distinct error sound. |
| Objective items flash white, stop when grabbed | DONE | GamePointer.LateUpdate additive white pulse, stops on grab. Only one item flashes; FindObjectsByType ~5/s during stations (small GC). |
| EMPTY HIVE text centered | DONE | Card straight ahead, centered alignment. |
| Wasps slower + swatters rotate with wrist | **BROKEN / PARTIAL** | Dive speed went UP ~35% (WaspBatOff Spawn speed 0.9–1.25 -> 1.25–1.80). Wrist rotation done (palm/controller). "Red, bigger" not changed. Raid line "One made it to the door!" no longer matches. |
| Wasp vs bee fun fact | DONE | room_wasps_arrive new text (wrong voice, see below). |
| Ending one thing at a time | DONE (caveats) | Comb disappears after 7 s untouched; contract hidden when comb arrives and never returns; Maps tab next to Ask card. Edge: old J-key Ask card shown first can block comb/Ask/Maps for the rest of the beat. |
| Question chips speak | PARTIAL | 4 chip answers bundled; card now shows only 1 chip. ask_offline / ask_not_heard / ask_stall and ask_killing / ask_sting_help / ask_offlist / ask_people still caption-only when live TTS is down. |
| "Show me" plays video | PARTIAL | Only in Map beat and only utterances starting "show me"; with no keyword it shows full_bloom, which is already up. Field beat / "can I see…" still get the union-break line. hive/src unchanged. |
| Maps menu / restart / click back in | PARTIAL | Clean ResetToPre, can't fire mid-ride, both inputs. Only "Replay this hive" + "Back" (no map to choose); no confirm, no click repeat guard; panel 0.22 m below eyes near resting hands (accidental replay risk). |
| Ending UI too high / award before map | PARTIAL | Order right. Contract card still at/above eye (EscapeSequence.DeliverEndCard endCardAboveEye 0.09). |
| Fix freezing | No new hard stall | See ending edge case above. |
| Petting: hearts, happy sound, swat mad, barrel roll | DONE | Speed-based pet vs swat; roll after 3 pets/7 s; sounds have voices. Hearts/roll not cleared on reset (cosmetic). |
| Shake hive -> wobble + buzz; bees/wasps react; vibration | PARTIAL | Wobble+buzz yes; bees/wasps do NOT react to shaking; haptics safe on hands/desktop. |
| Bees consistent (authored model) | Already OK | All renderers use BeeVat.AuthoredWorkerMesh with procedural fallback only; if old bees show on Quest, the authored mesh isn't loading there. |
| Syrup/items spawn inside hive; show item flying to it | MISSING | FeedStation pour to PourPoint (y 0.40) still clips the roof; no fly-to effect. |
| UI grabbable; controllers -> hands mid-ride | MOSTLY DONE | Grab ends on input switch. Bug: _waitForNeutral waits for grip release, so picking controllers up while squeezing grip blocks clicks. Two hands on one prop still possible. |
| Order board too close to face | PARTIAL | Min 0.58 m, once on Show; post sinks through the floor when lowered; not re-aimed; Android-only. |
| Portal weird places / rotated away | PARTIAL | Now faces the player; position logic unchanged. |
| Grass above head after resume | PARTIAL | Only re-forces floor tracking origin for 3 s after pause/focus; no recenter handling, FloorY never re-measured. |
| Banana line / "humans get the Moon" rewrite | DONE text, see voice | hook_1 now contradicts hook_2 / meet_2 / ask fact (whose rocket?); t0_fake_moon callback orphaned. |

## Voice consistency (owner wants ONE voice)
12 of 117 clips are the 24 kHz local voice, not Harry (44.1 kHz ElevenLabs): room_hook_1, room_smoker_banana, room_wasps_arrive, room_feed_lift_hint, room_feed_rock_retry, room_pal_show_me, room_meet_hey_beeswax, room_t0_fake_moon, ask_bananas, ask_drone_sting, ask_help_most, ask_wildfire. _manifest.json is stale (3 old captions, 7 missing entries) -> BeeswaxVoiceTools.Check will flag them. Re-render all 12 with ELEVENLABS_VOICE_ID=SOYHLrjzK2X1ezoPC6cr, eleven_multilingual_v2, then regenerate the manifest (~1,100 chars).
