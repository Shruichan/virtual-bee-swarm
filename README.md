# HONEYMOON

We made HONEYMOON, a mixed-reality game for Meta Quest, as Angry Virtual Bee Swarm. This year's theme was *Take Me To The Moon*. [Immerse the Bay 2026](https://immerse-the-bay-2026.devpost.com/)

A worker bee brings you a clipboard. Sign it and the crew delivers a sick hive to your room. Beeswax climbs out and gives you four minutes to calm the bees, feed them, fend off wasps, and cool the hive. Ask a what-if and watch it play out in the meadow window. Save the hive and Earth cancels the evacuation. Beeswax takes the rocket anyway.

## Play in Unity

1. Add `unity/VirtualBeeSwarm` in Unity Hub using **Unity 6000.2.9f1**.
2. Choose **Virtual Bee Swarm → Build Scene**, open the generated scene, and press Play.
3. Click **SIGN HERE** to start or press **F2** to run the full demo. The scripted ride works offline.

For headset controls, APK builds, and smoke tests, see the [Unity setup guide](unity/VirtualBeeSwarm/README.md).

## Where things are

| Folder | What's in it |
|---|---|
| [`art/`](art/) | Blender sources, bee artwork, music masters, and meadow OBJ exports. |
| [`unity/VirtualBeeSwarm/`](unity/VirtualBeeSwarm/) | The Unity project to open. Its imported art lives in [`Assets/VirtualBeeSwarm/Art/`](unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm/Art/). |
| [`unity/BeeswaxClient/`](unity/BeeswaxClient/) | The local Unity package for the Hive connection, voice, and offline station choices. Edit station data in [`hive/lines/stations.json`](hive/lines/stations.json) and sync the package copy. |
| [`hive/`](hive/) | The backend, operator board, and dialogue data. |
| [`glasses/`](glasses/) | The Mentra glasses app. |

In Unity's `Art/` folder, `Bee/` is the low-poly fallback for the swarm, `WorkerBee/` is the animated worker model, and `BeeVAT/` is the baked animation used by the swarm. The import and animation scripts are in [`ArtAnim/`](unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm/ArtAnim/).

Beeswax's Blender source and export notes are in [`art/blender/beeswax/`](art/blender/beeswax/). The tracked FBX is under [Unity's Beeswax rig](unity/VirtualBeeSwarm/Assets/VirtualBeeSwarm/Art/Beeswax/Rig/).

## Run the Hive

Create `hive/.env` from `hive/.env.example` and set `HIVE_TOKEN` to the shared local value. Start it from a terminal.

```sh
cd hive
bun install
bun run start
```

The default port is `8240`. The Hive handles speech, the Beat Board, and window cues. Live video and the glasses miniapp have their own setup. [Orbis relay](hive/orbis-relay/README.md) · [Mentra Live](glasses/README.md)

## Contributing

The team is Rome, dartiros, Nam Nguyen, and Adityash. Read the plan and claim your part before making changes. [Team plan and live log](PLAN.md) · [Agent instructions](AGENTS.md)

## AI services used

| Service | What it does in the project | Paid by |
|---|---|---|
| Anthropic Claude (Haiku 5.5 / Sonnet 5.5) | Scan verdicts, voice intent routing, and live answers | team |
| ElevenLabs | Beeswax's recorded and live voice, plus Scribe transcription | team |
| Visko Orbis via Reactor | Live scenes in the meadow window | sponsor keys |
| Attractor Labs SATIE | Sound design (hive moods, hornet, stings) | sponsor credits |
| MentraOS | Glasses speech-to-text and photo upload | platform |

## License

MIT. [License](LICENSE)
