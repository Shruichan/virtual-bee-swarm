# BeeswaxClient (Unity 6, Meta Quest)

Networking, audio and textures for the HONEYMOON Quest app: the Hive bus, the polaroid photo, Beeswax's voice in the room, the Orbis window and the hum detector. It has **no Meta SDK dependency**: gameplay (beat machine, swarm, Beeswax's body) stays with the Quest team and just listens to C# events.

The contract is [`hive/src/protocol.ts`](../../hive/src/protocol.ts). `Runtime/BeeswaxProtocol.cs` mirrors it, so change both together.

| Component | What it does |
|---|---|
| `BeeswaxBus` | One per app, in the first scene (it persists). Connects to `wss://<host>/bus?role=quest&token=…`, sends `hello`, reconnects with backoff, moves to the next host after 3 failed connects (and back to the first host once its `/health` answers), and raises one C# event per message type on the main thread. Send helpers: `SendBeatSet`, `SendSwarmArrived`, `SendHornetCooked`, `SendLatency`, `SendLog`. |
| `PolaroidFeed` | `scan.photo` JPEG → one reused `Texture2D`, assigned to a Renderer and/or RawImage. Events: `OnScanStarted` (blank polaroid + flash), `OnPhoto(texture, meta)`. |
| `BeeswaxRoomVoice` | Put it on Beeswax. Plays room lines one at a time: pre-rendered clips by `line_id` and live PCM16 lines (`tts.start` → chunks → `tts.end`). Exposes `Amplitude` (0..1, for mouth flaps) and `OnLineStarted/OnLineEnded(caption)`. |
| `OrbisWindow` | Live JPEG frames from `wss://<bus host>/orbis/frames?status=1&token=…`, newest frame only. Shows a looping fallback clip for the current stage when no frame arrives for 8 s or on `orbis{failed\|ended\|off}`. Obeys the Board's `orbis.request`: `live`, `clips` (clips only) or `off` (pane hidden, frames socket closed so the relay can idle-stop its billed session). |
| `HumDetector` | YIN pitch detector on the Quest mic. `Voiced` once a pitch in 80–500 Hz holds for 300 ms. Exposes `Level`, `HumSeconds`, and `Heat` (0..1 thermometer). `hum.set` from the Board overrides `Heat`. |
| `LatencyReporter` | `Mark`/`Lap`/`Report` hop timings to the Board. Add the component and it also reports the standard Quest hops. |
| `Samples/BeeswaxDebugHUD` | Debug text (connection, last messages, voice, hum, Orbis). Shows on screen in the editor. In the headset, assign a world-space TextMesh, or bind `onText` to a TextMeshPro. |

## Install

1. **NativeWebSocket** (its assembly is `endel.nativewebsocket`): Package Manager → **+** → *Add package from git URL* → `https://github.com/endel/NativeWebSocket.git#upm`. You can also add this line to `Packages/manifest.json`:
   ```json
   "com.endel.nativewebsocket": "https://github.com/endel/NativeWebSocket.git#upm",
   ```
2. **This folder**, either way works:
   - **Local package (recommended, one copy in the repo):** add `"com.virtualbeeswarm.beeswax-client": "file:../../BeeswaxClient"` to `Packages/manifest.json`. The path is relative to the project's `Packages/` folder; the example assumes the project lives at `unity/<Project>/`.
   - **Copy or symlink** the folder into `Assets/BeeswaxClient`.
   The `.meta` files ship with it, so script GUIDs (and scene references) are the same on every machine. Keep them.
3. **Player Settings (Android):**
   - *Other Settings → Internet Access* = **Require**
   - *Scripting Backend* = **IL2CPP**, *Target Architectures* = **ARM64** only
   - Microphone: Unity adds `RECORD_AUDIO` to the manifest by itself because `HumDetector` uses `Microphone`. Pre-grant it so no dialog pops up in the headset: `adb shell pm grant <your.package.name> android.permission.RECORD_AUDIO`. If it isn't granted, `HumDetector` asks at runtime the first time it listens.
4. **Audio:** set *Project Settings → Audio → Spatializer Plugin* to Meta XR Audio. Import Beeswax lines as **mono, Vorbis, Load Type = Decompress On Load**. With that Load Type, mouth flaps come from the clip itself. Otherwise they come from `AudioSource.GetOutputData`, which some spatializers zero out.

## Token

The Hive rejects clients without `HIVE_TOKEN`. **Never commit it.** The bus reads the first one it finds, in this order:

1. the `Token` field on `BeeswaxBus` (inspector; keep scene changes with a real token out of commits), then
2. `Resources/beeswax_token.txt` (a TextAsset; just the token, one line). `unity/BeeswaxClient/.gitignore` ignores `Resources/beeswax_token.txt` inside this folder. If you put it in the game project's own `Assets/Resources/`, add a gitignore line there too, then
3. the `HIVE_TOKEN` environment variable (editor/desktop only).

The token is URL-escaped into the query string. It ends up inside the APK, which is fine for a sideloaded demo build but not for a public one.

## Scene setup

- An empty root object `Beeswax` with `BeeswaxBus` (+ `LatencyReporter`, optionally `HumDetector`, `BeeswaxDebugHUD`). Hosts default to `wss://beeswax-venue.drori.dev` (venue laptop, primary on demo day) and then `wss://beeswax.drori.dev` (pop-os). For local testing, put `ws://<laptop-LAN-IP>:<port>` first.
- Beeswax's body: an `AudioSource` + `BeeswaxRoomVoice`. It configures the source as 3D and spatialized. Add pre-rendered lines to its list, or drop them in `Resources/BeeswaxLines/<line_id>`.
- Polaroid quad: `PolaroidFeed` with `targetRenderer` = the photo quad (URP `_BaseMap`; built-in `_MainTex` is detected).
- Window: `OrbisWindow` with `targetRenderer` = the window pane over the diorama. Fallback clips go in `stageClips` (index = stage) or in `stageClipFiles` under StreamingAssets. With no live frame and no clip, the pane hides and the diorama shows.

Every component finds `BeeswaxBus.Instance` by itself if you leave its `bus` field empty.

## Wiring it to the beat machine

Unity is the only beat clock. The Hive and the Board only *request*. Subscribe in `Start` (the bus is ready by then) and unsubscribe in `OnDestroy`:

```csharp
using Beeswax;
using UnityEngine;

public class HiveBridge : MonoBehaviour
{
    [SerializeField] BeatMachine beats;       // yours
    [SerializeField] SwarmController swarm;   // yours
    [SerializeField] BeeswaxController beeswax; // yours
    [SerializeField] HumDetector hum;

    void Start()
    {
        var bus = BeeswaxBus.Instance;
        bus.OnBeatRequest += OnBeatRequest;
        bus.OnScanStarted += OnScanStarted;
        bus.OnScanVerdict += OnVerdict;
        bus.OnPlea += OnPlea;
        bus.OnGroupReset += OnGroupReset;
        beats.BeatEntered += OnBeatEntered;   // your beat machine's event
    }

    void OnDestroy()
    {
        var bus = BeeswaxBus.Instance;
        if (bus == null) return;
        bus.OnBeatRequest -= OnBeatRequest;
        bus.OnScanStarted -= OnScanStarted;
        bus.OnScanVerdict -= OnVerdict;
        bus.OnPlea -= OnPlea;
        bus.OnGroupReset -= OnGroupReset;
    }

    void OnBeatEntered(int n)
    {
        BeeswaxBus.Instance.SendBeatSet(n);   // authoritative; re-sent after a reconnect if the Hive lost it
        if (n == Beat.Hornet) hum.WarmUp();   // open the mic early; set hum.Listening = true at "Now HUM!"
        else hum.Listening = false;
    }

    void OnBeatRequest(BeatRequest r)
    {
        if (r.HasN) beats.TryGoTo(r.n);       // you may ignore requests (e.g. never skip T-0)
        else if (r.next) beats.TryNext();
    }

    void OnScanStarted(ScanStarted s) => beeswax.WhipAround();

    void OnVerdict(ScanVerdict v)
    {
        // The line itself (v.line_id or the live tts_id) is played by BeeswaxRoomVoice.
        if (v.verdict == Verdict.Good && v.HasSocket) swarm.Beeline(v.socket);   // then: bus.SendSwarmArrived(v.socket)
        else swarm.UTurn();
    }

    void OnPlea(Plea p) => beeswax.Clipboard(p.text);
    void OnGroupReset(GroupReset g) => beats.ResetToPre();
}
```

Other hooks: `OnModeChanged("duo"|"solo")`, `OnSay` (the voice already plays `channel:"room"`), `OnOrbis` / `OnOrbisRequest` (the window already reacts), `OnHumSet` (the detector already applies it), `OnConnectionChanged(state, host)`, `OnMessage(type, json)` for anything else. When the hornet is cooked: `bus.SendHornetCooked(overshoot)`. `SendSwarmArrived` and `SendHornetCooked` are queued for 10 s if the bus is offline. Everything else is dropped while offline.

**Nullable fields** (JsonUtility has no nulls): a null/missing string becomes `""` and a null/missing number becomes `-1`. So `ScanVerdict.socket == -1` means no beeline (use `HasSocket`), `line_id == ""` means no pre-rendered line, and `BeatRequest.n == -1` means "next" (use `HasN`).

### The hum

- Call `CalibrateNoiseFloor()` at group reset while nobody hums (1.5 s), so the gate fits the room.
- `Listening = true` only for "Now HUM!", then read `Heat` (0..1) for the thermometer, plus `Voiced`/`Level` for feedback. `ResetHum()` before each hornet. Default: about 8 s of strong humming fills it. Tune `heatPerHumSecond`.
- **Duck the game audio while listening.** The Quest mic hears its own speakers, and the hive roar and hornet drone sit right in the 80–500 Hz hum band. Either pull those buses down by ~12 dB or high-pass them above ~600 Hz. Assign `listeningSnapshot`/`normalSnapshot` (AudioMixer snapshots) and the detector switches them for you. Assign `ignoreWhileSpeaking` = Beeswax's voice so "Now HUM!" itself isn't counted.
- The operator slider (`hum.set`) sets `Heat` directly and raises `OnOperatorHeat`. Live humming keeps adding to it.

## Binary framing

Bus binary frames are `[kind: u8][payload…]` (`BINARY_KIND`):

| kind | payload | belongs to |
|---|---|---|
| `0x01` SCAN_PHOTO | JPEG bytes | the most recent `scan.photo` → `OnScanPhoto(meta, jpeg)` |
| `0x02` TTS_PCM16 | PCM16 little-endian mono | the most recent `tts.start` → `OnTtsChunk(tts_id, pcm)`, at `tts.start.sample_rate` |

NativeWebSocket hands text and binary frames to the same `OnMessage(byte[])`, so the client tells them apart by the first byte: `{` (or whitespace) = JSON, `0x01`/`0x02` = binary, anything else is ignored. On `/orbis/frames`, a frame may be a raw JPEG (`FF D8 …`) or `[kind][JPEG]`. JSON `orbis{}` status on that socket is understood too.

## Testing

- **Editor:** Play mode works (NativeWebSocket uses .NET's `ClientWebSocket` off-WebGL). Set the token via `HIVE_TOKEN` or the inspector, turn on `logTraffic` on the bus, and add `BeeswaxDebugHUD`.
- **Against the Hive:** run it locally (`cd hive && bun run start`) and put `ws://<laptop-LAN-IP>:<port>` first in the bus hosts, or use the deployed hosts. Then simulate the glasses with `bun run scripts/fake-glasses.ts` from `hive/` (see the script header for options). The HUD should show `scan.started` → `scan.photo` (the polaroid fills) → `scan.verdict` → Beeswax speaks.
- **Without the network:** `HumDetector.FeedSamples(samples, count, rate)` analyses any audio (e.g. a recorded hum clip). `PolaroidFeed.Show(jpegBytes)` and `BeeswaxRoomVoice.Play(lineId, caption)` work offline.

## Robustness notes

- Every event fires on the main thread, and a throwing handler is logged without breaking the bus. Malformed JSON, unknown types and unknown binary kinds are logged and ignored.
- A connect attempt times out after 6 s, and abandoned sockets are aborted. A link that drops after it was healthy (≥ 5 s) retries the same host after 0.5 s. Failed connects back off 0.5 → 8 s, and after 3 in a row the bus moves to the next host. After such a failover it checks `https://<first host>/health` every 30 s (`returnToPrimarySeconds`) and moves back when it answers 200, like the glasses do, so both end up on the same Hive. `UseHost(i)` / `Reconnect()` switch by hand; a host picked with `UseHost` is kept. **Still check on the Board that the Quest and the glasses are on the same Hive.**
- The heartbeat, every 10 s, is `hello` (the Hive answers every hello with `welcome`, the same keepalive the Board and glasses use) plus `latency{hop:"quest.frame", ms:<frame time>}` for the Board. It keeps proxies from idling the socket out. If nothing at all arrives for 30 s (`staleSeconds`), the link is treated as dead (half-open TCP after a Wi-Fi drop) and the bus reconnects. `OnWelcome` fires once per link; heartbeat welcomes only refresh `GroupId`/`Mode`, and re-send `beat.set` if the Hive lost the beat.
- After the app was paused for more than 15 s (headset off), the bus reconnects.
- JPEG decoding (`LoadImage`) runs on the main thread: about 1 ms for 640×480 on a desktop CPU, and more on the Quest. The Hive already re-encodes photos to ≤ 1024 px. A JPEG that fails to decode keeps the previous image (Unity would otherwise show its 8×8 "?" texture). The Orbis window decodes at most one frame per rendered frame, capped by `maxDecodesPerSecond`, and drops stale frames.
- Voice: live lines are an `AudioClip` at the line's `sample_rate` (16 kHz from the Hive) fed by a PCM reader callback; Unity resamples it to the output rate. A missing clip still shows its caption for a reading-time estimate. The same `line_id` from `say` and `scan.verdict` within 3 s plays once. A live line with no `tts.end` finishes after 4 s without audio. Lines that waited more than 15 s in the queue are skipped. `group.reset` clears everything.
