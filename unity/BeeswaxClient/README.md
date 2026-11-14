# BeeswaxClient (Unity 6, Meta Quest)

Networking, audio and textures for the "Save the Hive" Quest app (concept v3): the Hive bus, station choices, the player's voice, Beeswax's voice in the room, the future window (Orbis), and the hum detector. It has **no Meta SDK dependency**: gameplay (beat machine, stations, swarm, Beeswax's body, embers) stays with the Quest team and just calls these components and listens to C# events.

The contract is [`hive/src/protocol.ts`](../../hive/src/protocol.ts) (PROTOCOL_VERSION 2, with the additive v2.1 messages). `Runtime/BeeswaxProtocol.cs` mirrors all of it (beats, stations, choices, scenes, what-if topics, fx, intents, every message), so change both together.

**v2.1** (still version 2, nothing removed): the Board's operator buttons (`choice.request` for a station choice, `fx.force`, `whatif.force`), the `score` message, custom what-ifs (topic `custom`, scene `whatif_custom`, `Intent.custom_prompt`), and an optional `want` on `orbis.request`.

## The v3 flow in one picture

| Player does | Unity calls | The Hive answers with | Unity events |
|---|---|---|---|
| Enters a beat | `bus.SendBeatSet(n)` | the window scene for escape/T-0/map | `OnOrbis`, the window switches by itself |
| Picks a choice at a station | `stations.Choose(station, choice)` (sends `choice.made`) | `say` (Beeswax's line), `orbis{scene}`, `fx` | the voice and window react by themselves; `OnFx` for swarm/hive glow |
| Holds the talk button / pinch | `voiceIn.BeginTalk()` … `voiceIn.EndTalk()` | `transcript`, `intent`, then a line, scene and fx | `OnTranscript`, `OnIntent`, `OnFx` |
| "What if a fire starts?" | (voice, as above) | `intent{what_if, fire}`, `orbis{whatif_fire}`, `fx{embers, delay_ms}` | spawn embers out of the portal on `OnFxDue("embers")` (it waits out `delay_ms`), stop on `embers_stop` |
| "Put it out!" | (voice) | `intent{undo}`, `orbis{fire_out}`, `fx{embers_stop}` | stop the embers (an `embers` still waiting on its delay is cancelled for `OnFxDue`) |
| "What if a dragon shows up?" (unplanned) | (voice) | `intent{what_if, custom, custom_prompt}` and Beeswax's line; with the live stream up also `orbis{whatif_custom}` (in clips mode the Hive leaves the scene alone and Beeswax says the clips line); if Claude refused: `custom_prompt` empty, the refusal line, no scene change | live: the window morphs; if live drops while on `whatif_custom`, the previous scene's clip plays (there is no `whatif_custom` clip). `i.IsCustom`, `i.CustomRefused` |
| *(Board)* taps a station choice | your `OnChoiceRequest` handler calls the same code as the player's pick (or tick `BeeswaxStations.applyChoiceRequests`) | `choice.request{station, choice}`, then the usual `say`/`orbis`/`fx` for the `choice.made` you send | `OnChoiceRequest`, then `stations.OnChoice` as for the player |
| *(Board)* forces an fx | | a plain `fx` (the Hive turns `fx.force` into one) | `OnFx` / `OnFxDue`, nothing new |
| *(Board)* forces a what-if / "put it out" | | the same `orbis`, `fx` and line as the spoken one, but no `transcript`/`intent` (`whatif.force` itself never reaches the Quest) | as for voice, minus `OnIntent`: key what-if feel off `window.OnSceneChanged` / the fx |
| *(Board)* types an unplanned what-if | | `transcript`, `intent{what_if, custom, custom_prompt}` and the line, as if the player said it (POST `/board/custom`) | as for voice |
| Enters escape | `bus.SendBeatSet(Beat.Escape)` | `score{elapsed_s, text, grievances}` | `OnScore` (also `bus.LastScore`): put `text` on the score card. The score is frozen at the first escape; re-entering escape sends the same one again |

**Unity is the only beat clock.** If the Hive is down, `BeeswaxStations` plays the same outcome locally (line, window clip, fx), so your offline timers still finish every beat with the right line and clip.

## Components

| Component | What it does |
|---|---|
| `BeeswaxBus` | One per app, in the first scene (it persists). Connects to `wss://<host>/bus?role=quest&token=…`, sends `hello`, reconnects with backoff, fails over between hosts, and raises one C# event per message type on the main thread. Send helpers: `SendBeatSet`, `SendChoiceMade(station, choice)` (computes `correct`), `SendListenStart(mode)` / `SendMicFrame(pcm16)` / `SendListenEnd(cancel)`, `SendOrbisRequest(want, scene)`, `SendSwarmArrived`, `SendHornetCooked`, `SendLatency`, `SendLog`. Events (v2): `OnTranscript`, `OnIntent`, `OnFx`, `OnFxDue`, `OnChoiceMade`, `OnListenStart/End`; (v2.1) `OnChoiceRequest`, `OnScore`. `InjectLocal(msg)` raises the events for a message as if the Hive had sent it (offline fallbacks, editor testing). |
| `BeeswaxStations` | Station choices. `Choose(station, choice)` sends `choice.made`; if the bus is offline, or neither the line nor the scene comes back within 1.5 s, it plays the outcome locally. Static `Resolve`/`TryGet`/`IntroLine` read an offline copy of `hive/lines/stations.json` (see below). Events: `OnChoice(outcome, sentToHive)`, `OnPlayedLocally(outcome)`. Operator choices: `applyChoiceRequests` (off by default) makes it call `Choose` for the Board's `choice.request` in that station's beat (`IsOperatorChoice` is true during that `OnChoice`); `ApplyChoiceRequest(r)` does it by hand. |
| `BeeswaxVoiceInput` | The player's voice. Push-to-talk `BeginTalk()` / `EndTalk()` / `Cancel()` to bind to any input, plus an optional energy VAD (`Mode = Vad`, `VadArmed = true`). Streams 16 kHz PCM16 in 40 ms `MIC_PCM16` frames between `listen.start` and `listen.end`, with 0.25 s of pre-roll and a 0.15 s release tail, capped at 15 s. `OnDuck(bool)` (and optional mixer snapshots) so game audio can duck while listening. `AwaitingReply` is true between `listen.end` and the intent (show "thinking"). |
| `BeeswaxMic` | The one shared microphone recording (static). `HumDetector` and `BeeswaxVoiceInput` both read it, so they can run at the same time. Never call `Microphone.Start/End` yourself while either is in the scene. |
| `BeeswaxRoomVoice` | Put it on Beeswax. Plays room lines one at a time: pre-rendered clips by `line_id` and live PCM16 lines (`tts.start` → chunks → `tts.end`). Exposes `Amplitude` (0..1, for mouth flaps) and `OnLineStarted/OnLineEnded(caption)`. |
| `OrbisWindow` | The future window. Live JPEG frames from `wss://<bus host>/orbis/frames`, newest frame only. Whenever live is down (no frame for 8 s, `orbis{failed\|ended\|off}`, or the Board chose `clips`) it loops the recorded clip **for the current scene** (`whatif_custom` has none, so it keeps the previous scene's clip: `ClipScene`). The scene follows `orbis{scene}` from the Hive, the Board's `orbis.request{scene}`, or your `RequestScene(scene)`. `OnSceneChanged(scene)`. |
| `HumDetector` | YIN pitch detector on the shared mic for the hornet beat. `Voiced` once a pitch in 80–500 Hz holds for 300 ms. `Level`, `HumSeconds`, `Heat` (0..1 thermometer). `hum.set` from the Board overrides `Heat`. |
| `PolaroidFeed` | (Glasses real-prop mode, later.) `scan.photo` JPEG → one reused `Texture2D`. |
| `LatencyReporter` | `Mark`/`Lap`/`Report` hop timings to the Board. The voice input reports `quest.listen_end_to_transcript` and `quest.listen_end_to_intent`. |
| `Samples/BeeswaxDebugHUD` | Debug text: connection, mic (mode, talking, level, frames sent), last transcript/intent/fx, window scene and clip, stations source, last messages. On screen in the editor; in the headset assign a world-space TextMesh, or bind `onText` to a TextMeshPro. |

## Install

1. **NativeWebSocket** (its assembly is `endel.nativewebsocket`): Package Manager → **+** → *Add package from git URL* → `https://github.com/endel/NativeWebSocket.git#upm`, or add to `Packages/manifest.json`:
   ```json
   "com.endel.nativewebsocket": "https://github.com/endel/NativeWebSocket.git#upm",
   ```
2. **This folder**, either way:
   - **Local package (recommended):** add `"com.virtualbeeswarm.beeswax-client": "file:../../BeeswaxClient"` to `Packages/manifest.json` (path relative to the project's `Packages/` folder; the example assumes the project lives at `unity/<Project>/`).
   - **Copy or symlink** the folder into `Assets/BeeswaxClient`.
   The `.meta` files ship with it, so script GUIDs (and scene references) match on every machine. Keep them.
3. **Player Settings (Android):** *Internet Access* = **Require**; *Scripting Backend* = **IL2CPP**, *Target Architectures* = **ARM64** only.
4. **Microphone:** Unity adds `RECORD_AUDIO` itself because the scripts use `Microphone`. Pre-grant it so no dialog pops up in the headset: `adb shell pm grant <your.package.name> android.permission.RECORD_AUDIO`. Otherwise `BeeswaxMic` asks once, at the first `Acquire` (with `openMicOnStart` that is app start, which is what you want).
5. **Audio:** *Project Settings → Audio → Spatializer Plugin* = Meta XR Audio. Import Beeswax lines as **mono, Vorbis, Load Type = Decompress On Load** (mouth flaps then come from the clip itself).
6. **Station data:** `Resources/beeswax_stations.json` ships in this folder (a copy of `hive/lines/stations.json`). When the Hive's table changes, refresh it:
   ```sh
   cp hive/lines/stations.json unity/BeeswaxClient/Resources/beeswax_stations.json
   # or, after `bun scripts/prerender.ts`, the Hive's Unity export:
   cp hive/out/unity/stations.json unity/BeeswaxClient/Resources/beeswax_stations.json
   ```
   Don't put a second `beeswax_stations.json` in another `Resources/` folder (Unity would pick one at random); to use another file, drag it onto `BeeswaxStations.stationsJson`.

## Token

The Hive rejects clients without `HIVE_TOKEN`. **Never commit it.** The bus reads the first one it finds:

1. the `Token` field on `BeeswaxBus` (inspector; keep scene changes with a real token out of commits), then
2. `Resources/beeswax_token.txt` (a TextAsset; just the token, one line). `unity/BeeswaxClient/.gitignore` ignores it inside this folder. If you put it in the game's own `Assets/Resources/`, add a gitignore line there too, then
3. the `HIVE_TOKEN` environment variable (editor/desktop only).

It ends up inside the APK, which is fine for a sideloaded demo build but not for a public one.

## Scene setup

- Root object `Beeswax` with `BeeswaxBus`, `BeeswaxStations`, `BeeswaxVoiceInput`, `LatencyReporter` (+ `HumDetector`, `BeeswaxDebugHUD`). Hosts default to `wss://beeswax-venue.drori.dev` (venue laptop, primary on demo day), then `wss://beeswax.drori.dev` (pop-os). For local testing put `ws://<laptop-LAN-IP>:<port>` first.
- Beeswax's body: an `AudioSource` + `BeeswaxRoomVoice`. Add pre-rendered lines to its list, or drop the mp3s from `hive/out/unity/` into `Resources/BeeswaxLines/<line_id>`.
- On `BeeswaxStations`: assign `voice` (Beeswax) and `window` (the portal), so offline outcomes play.
- On `BeeswaxVoiceInput`: assign `ignoreWhileSpeaking` = Beeswax's voice (VAD won't trigger on him), and either the two mixer snapshots or your own `OnDuck` handler.
- Window: `OrbisWindow` with `targetRenderer` = the portal pane. Fallback clips per scene: fill `sceneClips` (scene → VideoClip), or put `<scene>.mp4` files in `Assets/StreamingAssets/` (or a subfolder named in `sceneClipFolder`). The scene names are `BeeswaxProtocol.Scenes` (`meadow`, `smoker_settle`, `smoker_angry`, `feed_sick`, `feed_recover`, `wasps_raid`, `wasps_leave`, `heat_stress`, `heat_drink`, `whatif_fire`, `fire_out`, `whatif_rain`, `whatif_flowers`, `whatif_spray`, `whatif_winter`, `whatif_custom`, `full_bloom`, `moon`). `whatif_custom` needs no clip (the window keeps the one it has; give it one only through `sceneClips`). A missing file is skipped (logged once), and the pane hides if nothing plays.

Every component finds `BeeswaxBus.Instance` by itself if you leave its `bus` field empty.

## Wiring it to the game

Subscribe in `Start` and unsubscribe in `OnDestroy`:

```csharp
using Beeswax;
using UnityEngine;

public class HiveBridge : MonoBehaviour
{
    [SerializeField] BeatMachine beats;          // yours
    [SerializeField] SwarmController swarm;      // yours
    [SerializeField] EmbersFx embers;            // yours (particles out of the portal)
    [SerializeField] BeeswaxStations stations;
    [SerializeField] BeeswaxVoiceInput voiceIn;
    [SerializeField] HumDetector hum;

    void Start()
    {
        var bus = BeeswaxBus.Instance;
        bus.OnBeatRequest += OnBeatRequest;
        bus.OnFxDue += OnFx;                     // OnFxDue waits out delay_ms and drops an embers that embers_stop beat
        bus.OnIntent += OnIntent;
        bus.OnChoiceRequest += OnChoiceRequest;  // the Board's operator twin of a station choice
        bus.OnScore += OnScore;
        bus.OnGroupReset += OnGroupReset;
        stations.OnChoice += OnChoice;
        voiceIn.OnDuck += duck => AudioMixerHelper.Duck(duck);  // or assign the mixer snapshots instead
        beats.BeatEntered += OnBeatEntered;
    }

    void OnDestroy()
    {
        var bus = BeeswaxBus.Instance;
        if (bus == null) return;
        bus.OnBeatRequest -= OnBeatRequest;
        bus.OnFxDue -= OnFx;
        bus.OnIntent -= OnIntent;
        bus.OnChoiceRequest -= OnChoiceRequest;
        bus.OnScore -= OnScore;
        bus.OnGroupReset -= OnGroupReset;
    }

    void OnBeatEntered(int n)
    {
        BeeswaxBus.Instance.SendBeatSet(n);      // authoritative; re-sent after a reconnect if the Hive lost it
        voiceIn.VadArmed = n == Beat.WhatIf;      // only if you use VAD; push-to-talk needs nothing here
        if (n == Beat.Hornet) hum.WarmUp(); else hum.Listening = false;
    }

    // Your station UI / hand interaction calls this (smoker squeeze, tip + jar, door, water dish).
    public void Pick(string station, string choice) => stations.Choose(station, choice);
    // e.g. Pick(Station.Feed, Choice.StoreHoney)

    void OnChoice(BeeswaxStations.Outcome o, bool sentToHive)
    {
        if (o.correct) beats.LightHexagon(o.station);   // the line, window scene and fx are handled for you
    }

    // The Board picked for the player (their hands failed, or the judge cut). Run the same code as a real pick.
    // You may ignore it outside that station's beat. (Or skip this and tick BeeswaxStations.applyChoiceRequests.)
    void OnChoiceRequest(ChoiceRequest r)
    {
        if (beats.Current == BeeswaxProtocol.StationBeat(r.station)) Pick(r.station, r.choice);
    }

    void OnScore(Score s) => beats.ShowScoreCard(s.text, s.grievances);   // "Hive saved in 2:47", wrong picks

    // Bind these to the talk input (a held pinch, a controller button, a poke button...).
    public void OnTalkPressed() => voiceIn.BeginTalk();
    public void OnTalkReleased() => voiceIn.EndTalk();

    // From OnFxDue: already at the right time (embers come delay_ms after the line, covering the 2-4 s Orbis morph).
    void OnFx(Fx f)
    {
        switch (f.name)
        {
            case FxName.Embers: embers.Play(); break;
            case FxName.EmbersStop: embers.Stop(); break;
            case FxName.SwarmCalm: swarm.Calm(); break;
            case FxName.SwarmAngry: swarm.Angry(); break;
            case FxName.HiveGlowRed: beats.HiveGlow(true); break;
            case FxName.HiveGlowOff: beats.HiveGlow(false); break;
        }
    }

    void OnIntent(Intent i)
    {
        // The Hive already sends the line, the scene and the fx. React here only for extra feel, e.g.:
        // (the Board's what-if buttons send no intent, only the line/scene/fx: for feel that must also run then,
        // use window.OnSceneChanged(Scene.WhatIfFire) instead)
        if (i.name == IntentName.WhatIf && i.topic == WhatIfTopic.Fire) beats.WhatIfFireStarted();
        if (i.CustomRefused) beats.BeeswaxShakesHead();  // an unplanned what-if Claude wouldn't draw
    }

    void OnBeatRequest(BeatRequest r)
    {
        if (r.HasN) beats.TryGoTo(r.n);          // you may ignore requests (e.g. never skip T-0)
        else if (r.next) beats.TryNext();
    }

    void OnGroupReset(GroupReset g) => beats.ResetToPre();
}
```

**Fx timing:** `OnFx` fires the moment an fx arrives, so with it you honour `delay_ms` yourself and must make a later `embers_stop` cancel an `embers` still waiting on its delay (the Hive sends `embers` with a 2.5 s delay, and a quick "put it out!" can beat it). `OnFxDue` does both: it fires each fx when it should start, and a later fx of the same name or its opposite (`embers_stop`↔`embers`, `swarm_angry`↔`swarm_calm`, `hive_glow_off`↔`hive_glow_red`) cancels one still waiting. `group.reset` cancels all; `bus.CancelPendingFx(name)` and `PendingFxCount` are there too. Subscribe to one of the two per effect, not both. The Board's `fx.force` arrives as a plain `fx` (if a Hive ever forwarded `fx.force` itself, the bus plays it as an fx, once).

Other hooks: `OnTranscript` (captions: "You said: …"), `voiceIn.AwaitingReply` (Beeswax "hmm" pose), `voiceIn.OnTalkEnded(reason, seconds)` (`Released`, `Cancelled`, `Capped`, `Silence`, `TooShort`, `Disabled`), `window.OnSceneChanged`, `window.RequestScene(scene)` (rarely needed: the Hive sets the scene from choices, beats and voice), `OnModeChanged`, `OnSay` (the voice already plays `channel:"room"`), `OnConnectionChanged(state, host)`, `OnMessage(type, json)` for anything else. `SendSwarmArrived`, `SendHornetCooked` and `SendChoiceMade` are queued for 10 s while offline; everything else is dropped while offline (the voice input keeps its own audio, below).

**Nullable fields** (JsonUtility has no nulls): a null/missing string becomes `""` and a null/missing number `-1`. So `Intent.topic == ""` unless it's a what-if (`HasTopic`), `Intent.custom_prompt == ""` unless it's a custom what-if Claude accepted (`IsCustom`, `CustomRefused`), `Fx.delay_ms == -1` means now (`HasDelay`), `OrbisStatus.scene == ""` means none, `OrbisRequest.want == ""` means no mode change (`HasWant`), `BeatRequest.n == -1` means "next" (`HasN`), `Score.elapsed_s`/`grievances == -1` mean missing (`HasElapsed`; `Score.Clock` formats it as `2:47`).

### Voice details

- **Push-to-talk** is the default and the most reliable in a loud hall. VAD (`Mode = Vad`, `VadArmed = true`) starts on speech ≥ 12 dB above the room's floor for 120 ms and ends after 0.8 s of quiet; it ignores Beeswax while he speaks (`ignoreWhileSpeaking`). Disarming it mid-sentence cancels that utterance.
- **Mic warm-up:** `openMicOnStart` (default on) opens the mic at start so BeginTalk has 0.25 s of pre-roll and no start-up gap. Nothing is sent unless an utterance is open.
- **Offline:** if the bus is down when the player talks, or drops mid-utterance, the audio is kept and the whole utterance is (re)sent once the link is back, if that happens within 5 s of the player stopping (`deliverGraceSeconds`). Otherwise it is dropped (`UtterancesLost`).
- **Shared mic:** `HumDetector` and `BeeswaxVoiceInput` share one recording through `BeeswaxMic`, so both can run at once. The hum detector then hears the player talking, and the voice input hears the hum; in practice the hornet beat doesn't need talking, so release the talk button there.
- **Ducking:** while listening, duck or high-pass the hive roar and Beeswax (the Quest mic hears its own speakers): assign `duckedSnapshot`/`normalSnapshot`, or handle `OnDuck`.
- **No mic (tests, editor):** turn `useMicrophone` off and push audio with `FeedSamples(samples, count, sampleRate)`; any rate is resampled to 16 kHz.

### The window

- `orbis{scene}` from the Hive switches the clip at once, live or not. When live frames arrive they win (unless the Board chose `clips`).
- Only the Hive sets the scene. The relay's own status on the frames socket only updates `State` (so `failed`/`ended` switch to clips at once): after a stop the relay keeps reporting the last scene it streamed, which would otherwise pull the window back to an old clip on every frames reconnect.
- The Hive sends `orbis{}` only when something changes, so after every (re)connect the window reads the Hive's current scene and Board mode from `GET /health` (`syncOnWelcome`). A Quest that restarts or reconnects mid-demo shows the right clip at once. Any `orbis{}`/`orbis.request` that arrives first wins.
- `RequestScene(scene)` switches the local clip now and sends `orbis.request{want, scene}`. Offline, it is re-sent after the next welcome, and Hive statuses naming another scene are ignored until the Hive confirms ours.
- The `want` it carries (`RequestWant`): a running stream (`priming`/`ready`) → `live`; else the Board's mode; else `clips`. The Hive ignores `want` on a request that carries a scene (v2.1 makes `want` optional and says so), so the Quest can't start or stop the billed stream either way. It still sends one because a v2 Hive rejects an `orbis.request` without `want`. `bus.SendOrbisRequest("", scene)` sends a scene-only request for a v2.1 Hive.
- **Custom what-ifs (`whatif_custom`)** have a live prompt Claude writes on the spot but no recorded clip. While the window is on it, clips mode keeps playing the clip of the last scene that had one (`ClipScene`; the Hive's `fallback_clip` for it names that same kept clip, but the window goes by its own `ClipScene`), so the pane never goes blank. Live mode shows the morph as usual. A `sceneClips` entry for `whatif_custom` (say, a generic "something strange in the meadow" loop) is used if you add one; `StreamingAssets/whatif_custom.mp4` is not looked for.
- Clip lookup per scene: `sceneClips` → the Hive's `fallback_clip` name (matched against `sceneClips`/`stageClips`/`stageClipFiles`) → `StreamingAssets/<sceneClipFolder>/<fallback_clip>` when it isn't `<scene>.mp4` (e.g. a `*_pingpong.mp4` loop named in `hive/lines/orbis_scenes.json`) → `StreamingAssets/<sceneClipFolder>/<scene>.mp4` → the v1 stage clips.

### The hum

- Call `CalibrateNoiseFloor()` at group reset while nobody hums (1.5 s).
- `Listening = true` only for "Now HUM!", then read `Heat` (0..1) for the thermometer plus `Voiced`/`Level` for feedback. `ResetHum()` before each hornet. About 8 s of strong humming fills it (`heatPerHumSecond`).
- Duck the game audio while listening (the hive roar and hornet drone sit in the 80–500 Hz hum band): assign `listeningSnapshot`/`normalSnapshot`. Assign `ignoreWhileSpeaking` = Beeswax's voice so "Now HUM!" itself isn't counted.
- The operator slider (`hum.set`) sets `Heat` directly and raises `OnOperatorHeat`.

### Glasses real-prop mode (later)

The v1 scan messages still work: `OnScanStarted`, `OnScanPhoto` (+ `PolaroidFeed`), `OnScanVerdict` (`HasSocket`), `OnPlea`. The verdict's room line plays on Beeswax by itself.

## Binary framing

Bus binary frames are `[kind: u8][payload…]` (`BINARY_KIND`):

| kind | direction | payload | belongs to |
|---|---|---|---|
| `0x01` SCAN_PHOTO | Hive → Quest | JPEG bytes | the most recent `scan.photo` → `OnScanPhoto(meta, jpeg)` |
| `0x02` TTS_PCM16 | Hive → Quest | PCM16 LE mono | the most recent `tts.start` → `OnTtsChunk(tts_id, pcm)` |
| `0x03` MIC_PCM16 | Quest → Hive | PCM16 LE mono 16 kHz, 40 ms (1280 bytes) per frame | the open `listen.start` (`SendMicFrame` drops frames when none is open) |

NativeWebSocket hands text and binary frames to the same `OnMessage(byte[])`, so the client tells them apart by the first byte: `{` (or whitespace) = JSON, `0x01`–`0x03` = binary. On `/orbis/frames`, a frame may be a raw JPEG (`FF D8 …`) or `[kind][JPEG]`, plus JSON `orbis` status (`?status=1`).

## Testing

- **Editor:** Play mode works (NativeWebSocket uses .NET's `ClientWebSocket` off-WebGL). Set the token via `HIVE_TOKEN` or the inspector, turn on `logTraffic`, and add `BeeswaxDebugHUD`.
- **Against the Hive:** `cd hive && bun run start`, then put `ws://<laptop-LAN-IP>:<port>` first in the bus hosts. Pick a station choice: the HUD shows the Hive's `say`, `orbis{scene}` and `fx`. Hold talk, say "what if a fire starts", release: `HEARD`, `INTENT what_if fire`, `FX embers`.
- **Without the network:** `stations.Choose(...)` plays outcomes locally after 1.5 s; `bus.InjectLocal(new Fx { name = FxName.Embers })` (or any message: `new ChoiceRequest { station = Station.Feed, choice = Choice.SugarSyrup }`, `new Score { elapsed_s = 167, text = "Hive saved in 2:47", grievances = 1 }`, an `Intent` with `topic = WhatIfTopic.Custom`) fires the same events as the Hive; `voiceIn.FeedSamples(...)`, `HumDetector.FeedSamples(...)`, `PolaroidFeed.Show(jpeg)` and `BeeswaxRoomVoice.Play(lineId, caption)` work offline.

## Robustness notes

- Every event fires on the main thread, and a throwing handler is logged without breaking the bus. Malformed JSON, unknown types and unknown binary kinds are logged and ignored. Invalid station/choice pairs and unknown scenes are refused with an error log, never sent.
- **Sends never block the frame.** NativeWebSocket writes synchronously, which on a half-open link (Wi-Fi drop) would freeze the render loop once the kernel buffer fills, and mic streaming fills it in a second or two. Every socket therefore writes from one pool thread, in order (`listen.start` → frames → `listen.end` stay ordered with text frames). If a write is stuck for 4 s (`sendStallSeconds`) the bus drops the link and reconnects; the voice input pauses sending while more than 64 KB is queued.
- A connect attempt times out after 6 s. A link that drops after it was healthy (≥ 5 s) retries the same host after 0.5 s. Failed connects back off 0.5 → 8 s, and after 3 in a row the bus moves to the next host; after a failover it checks the first host's `/health` every 30 s and moves back on a 200. `UseHost(i)` / `Reconnect()` switch by hand. **Still check on the Board that the Quest and the glasses are on the same Hive.**
- Heartbeat every 10 s: `hello` (the Hive answers with `welcome`) plus `latency{hop:"quest.frame"}`. Nothing at all for 30 s → reconnect. Welcomes re-send `beat.set` if the Hive lost the beat. After 15 s paused (headset off) the bus reconnects.
- **Station outcomes offline:** `BeeswaxStations` plays line + clip + fx locally and queues `choice.made` (10 s). When the queued choice reaches the Hive, its repeat of the same line is skipped once (`SuppressNextFromBus`), and its scene is confirmed; a later local scene wins.
- The same station + choice again within 1.5 s (a double-fired grab) is ignored by `BeeswaxStations.Choose` (no send, no `OnChoice`). The Hive drops such a repeat too, so sending it would only replay the outcome locally. That also makes an operator `choice.request` handled twice (your handler and `applyChoiceRequests`) harmless for the choice itself.
- A `choice.request` that nothing handles logs a warning (subscribe to `OnChoiceRequest` or turn on `applyChoiceRequests`); one naming a pair outside CHOICES is logged and dropped. With a Quest connected the Hive does **not** apply the choice itself (it only does that when no Quest is on the bus), so an unhandled request means the Board's station button does nothing. The Quest then sends the Board a `log` ("operator choice wasps:bigger_door ignored: nothing on the Quest handles choice.request"), and `applyChoiceRequests` does the same when it skips a request for another station's beat. `BeeswaxDebugHUD` shows operator choices without subscribing, so it never hides that warning.
- Voice lines: a missing clip still shows its caption for a reading-time estimate. The same `line_id` from `say` and `scan.verdict` within 3 s plays once. A live line with no `tts.end` finishes after 4 s without audio. Lines that waited more than 15 s in the queue are skipped. `group.reset` clears everything.
- JPEG decoding runs on the main thread (about 1 ms for 640×480 on a desktop CPU, more on the Quest). A JPEG that fails to decode keeps the previous image. The window decodes at most one frame per rendered frame and drops stale frames.
