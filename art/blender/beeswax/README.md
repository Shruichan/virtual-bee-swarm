# Beeswax animations

The tracked [Beeswax.fbx](../../../unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm/Art/Beeswax/Rig/Beeswax.fbx) is imported as a Generic rig. To try a new export from `Beeswax.blend`, set `VBS_BEESWAX_FBX` to the new FBX before running the Beeswax rig builder. Hover is the flight loop. The game moves Beeswax through the room. Keep Hover looping on the base layer and use masked Override layers for gestures. The clips are baked, so masks keep one gesture from resetting unrelated bones. Give each bone one driver. If the Animator runs Hover, leave the controller's direct wing animation fields unassigned.

| Clip | When to use it |
|---|---|
| Hover | Default flight. Wings flap and all four stubs dangle. |
| Walk | Grounded steps with all four stubs. |
| Arm Swing | Alternate the front stubs during forward movement. Mask the front pair. |
| Wag | Wiggle the abdomen for a playful reaction. |
| Blink | Occasional idle blink. Mask both eyelid bones. |
| Double Blink | A quick double take. |
| Antenna Twitch | Idle variation or noticing something. Mask the antenna bones. |
| Yes Nod | Confirm a correct choice. |
| No Shake | React to a wrong choice. |
| Curious Tilt | Ask a question or give a hint. |
| Look Around | Scan the room or meadow. |
| Startle | React to the fire reveal. |
| Celebrate | Reward a correct choice or the end of the demo. |
| Talk | Switch the smile to a round O in syllable beats. Mask Mouth and Mouth O. Trigger it during dialogue. |
| Blush | Show the flat pink cheek marks for a bashful reaction. |
| Sad | Lower the head and antennae and show a small frown. |
| Angry | Narrow the eyes and show slanted brows and a frown. |
| Sooty Shake | Shake after the smoker beat. Drive the soot tint separately in Unity. |
| Thorn Spin | Sweep the thorn in a circle. |
| Thorn Flick | Sweep the thorn side to side with a small lift. |
| Thorn Bob | Move the thorn up and down. |

Enable Loop Time for sustained motions. Use one-shot transitions for reactions, or loop Talk while dialogue is active. Mask thorn clips to Thorn. Put gestures that share bones on the same layer or give one priority. For example, Sad and Angry both use the face, while Arm Swing can override Hover on the front stubs only.

The Blender FBX re-import check covered all 21 clip endpoints. The Unity prefab and voice hookup still need an in-game check.
