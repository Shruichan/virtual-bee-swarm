using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Events;

namespace Beeswax
{
    /// <summary>
    /// Debug text: bus connection, last messages, Beeswax's voice, the player's voice (push-to-talk/VAD, transcript,
    /// intent), hum, Orbis (live/clip, scene) and the last fx, operator choice and score.
    /// In the headset: put a TextMesh (GameObject > 3D Object > Legacy > Text) in front of you and assign it,
    /// or bind onText to your TextMeshPro's text property in the inspector.
    /// In the editor/desktop it also draws on screen (drawOnScreen).
    /// </summary>
    public class BeeswaxDebugHUD : MonoBehaviour
    {
        [SerializeField] BeeswaxBus bus;
        [SerializeField] BeeswaxRoomVoice voice;
        [SerializeField] HumDetector hum;
        [SerializeField] OrbisWindow orbis;
        [SerializeField] BeeswaxVoiceInput voiceIn;
        [SerializeField] TextMesh worldText;
        [SerializeField] UnityEvent<string> onText = new UnityEvent<string>();
        [SerializeField] bool drawOnScreen = true;
        [SerializeField] int maxLines = 8;
        [SerializeField] float refreshSeconds = 0.2f;

        public string Text { get; private set; } = "";

        readonly Queue<string> _lines = new Queue<string>();
        readonly StringBuilder _sb = new StringBuilder();
        BeeswaxBus _bound;
        string _lastTranscript = "", _lastIntent = "", _lastFx = "", _lastOperator = "", _lastScore = "";
        float _nextRefresh;

        void OnEnable() => Bind();
        void Start() => Bind();
        void OnDisable() => Unbind();

        void Bind()
        {
            var b = bus != null ? bus : BeeswaxBus.Instance;
            if (b == null || b == _bound) return;
            Unbind();
            _bound = b;
            b.OnConnectionChanged += HandleConnection;
            b.OnMessage += HandleMessage;
            b.OnScanPhoto += HandlePhoto;
            b.OnTranscript += HandleTranscript;
            b.OnIntent += HandleIntent;
            b.OnFx += HandleFx;
            // choice.request is read from OnMessage, not OnChoiceRequest: a HUD subscriber would count as a handler and
            // silence the bus's "choice.request arrived but nothing handles it" warning.
            b.OnScore += HandleScore;
            b.OnGroupReset += HandleGroupReset;
        }

        void Unbind()
        {
            if (_bound == null) return;
            _bound.OnConnectionChanged -= HandleConnection;
            _bound.OnMessage -= HandleMessage;
            _bound.OnScanPhoto -= HandlePhoto;
            _bound.OnTranscript -= HandleTranscript;
            _bound.OnIntent -= HandleIntent;
            _bound.OnFx -= HandleFx;
            _bound.OnScore -= HandleScore;
            _bound.OnGroupReset -= HandleGroupReset;
            _bound = null;
        }

        void HandleConnection(BusState state, string host) => Add($"{state} {host}");
        void HandlePhoto(ScanPhoto meta, byte[] jpeg) => Add($"photo {jpeg.Length / 1024} KB");
        void HandleTranscript(Transcript t) => _lastTranscript = $"{t.source}: \"{t.text}\" ({t.stt} {t.ms:0} ms{(t.final ? "" : ", partial")})";
        void HandleIntent(Intent i) => _lastIntent = $"{i.name}{(i.HasTopic ? " " + i.topic : "")} via {i.path} {i.ms:0} ms" +
            (i.IsCustom ? (i.CustomRefused ? " REFUSED" : $" \"{(i.custom_prompt.Length > 60 ? i.custom_prompt.Substring(0, 60) + "…" : i.custom_prompt)}\"") : "");
        void HandleChoiceRequest(string json)
        {
            ChoiceRequest r;
            try { r = BeeswaxProtocol.Parse<ChoiceRequest>(json); }
            catch (System.Exception) { return; }
            _lastOperator = $"{r.station}:{r.choice}{(r.IsValid ? "" : " (not in CHOICES)")} at {Time.realtimeSinceStartup:0.0}";
        }
        void HandleScore(Score s) => _lastScore = $"\"{s.text}\" ({s.Clock}, grievances {s.grievances})";
        void HandleGroupReset(GroupReset _) { _lastOperator = ""; _lastScore = ""; }
        void HandleFx(Fx f) => _lastFx = $"{f.name}{(f.HasDelay ? " +" + f.delay_ms + " ms" : "")} at {Time.realtimeSinceStartup:0.0}";

        void HandleMessage(string type, string json)
        {
            if (type == MsgType.ChoiceRequest) HandleChoiceRequest(json);
            if (type == MsgType.Latency) return; // too chatty
            Add(json.Length > 90 ? json.Substring(0, 90) + "…" : json);
        }

        void Add(string line)
        {
            _lines.Enqueue($"{Time.realtimeSinceStartup,7:0.0} {line}");
            while (_lines.Count > maxLines) _lines.Dequeue();
            _nextRefresh = 0f;
        }

        void Update()
        {
            Bind();
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + refreshSeconds;

            _sb.Clear();
            var b = _bound;
            if (b != null)
            {
                _sb.Append("BUS ").Append(b.State).Append("  ").Append(b.CurrentHost)
                   .Append("  beat ").Append(b.LastBeat).Append("  group ").Append(b.GroupId)
                   .Append("  ").Append(b.SmoothedFrameMs.ToString("0.0")).Append(" ms/frame\n");
                if (!b.IsConnected && b.LastCloseReason.Length > 0) _sb.Append("  last: ").Append(b.LastCloseReason).Append('\n');
            }
            else _sb.Append("BUS missing (add a BeeswaxBus)\n");
            if (voice != null)
                _sb.Append("VOICE ").Append(voice.IsSpeaking ? "\"" + voice.CurrentCaption + "\"" : "idle")
                   .Append("  amp ").Append(voice.Amplitude.ToString("0.00")).Append("  queued ").Append(voice.QueuedCount).Append('\n');
            if (voiceIn != null)
            {
                _sb.Append("MIC ").Append(voiceIn.Mode).Append(voiceIn.VadArmed ? " armed" : "")
                   .Append(voiceIn.IsTalking ? " TALKING " + voiceIn.TalkModeName + " " + voiceIn.TalkSeconds.ToString("0.0") + "s" : "")
                   .Append(voiceIn.Delivering ? " sending" : "").Append(voiceIn.AwaitingReply ? " thinking" : "")
                   .Append(voiceIn.MicRunning ? "" : " (mic off)")
                   .Append("  level ").Append(voiceIn.Level.ToString("0.00")).Append(" (").Append(voiceIn.LevelDb.ToString("0"))
                   .Append(" dB, floor ").Append(voiceIn.NoiseFloorDb.ToString("0")).Append(")  sent ").Append(voiceIn.UtterancesSent)
                   .Append(" lost ").Append(voiceIn.UtterancesLost).Append('\n');
            }
            if (b != null && (b.MicFramesSent > 0 || b.IsListening))
                _sb.Append("  mic frames ").Append(b.MicFramesSent).Append(" (").Append(b.MicBytesSent / 1024).Append(" KB)")
                   .Append(b.IsListening ? " open" : "").Append("  queued ").Append(b.QueuedSendBytes).Append(" B\n");
            if (_lastTranscript.Length > 0) _sb.Append("HEARD ").Append(_lastTranscript).Append('\n');
            if (_lastIntent.Length > 0) _sb.Append("INTENT ").Append(_lastIntent).Append('\n');
            if (_lastFx.Length > 0)
                _sb.Append("FX ").Append(_lastFx).Append(b != null && b.PendingFxCount > 0 ? "  (" + b.PendingFxCount + " waiting)" : "").Append('\n');
            if (_lastOperator.Length > 0) _sb.Append("OPERATOR choice ").Append(_lastOperator).Append('\n');
            if (_lastScore.Length > 0) _sb.Append("SCORE ").Append(_lastScore).Append('\n');
            if (hum != null)
                _sb.Append("HUM ").Append(hum.Listening ? "listening" : "off").Append(hum.Voiced ? " VOICED " : " ")
                   .Append(hum.PitchHz.ToString("0")).Append(" Hz  ").Append(hum.LevelDb.ToString("0")).Append(" dB (floor ")
                   .Append(hum.NoiseFloorDb.ToString("0")).Append(")  level ").Append(hum.Level.ToString("0.00"))
                   .Append("  heat ").Append(hum.Heat.ToString("0.00")).Append('\n');
            if (orbis != null)
                _sb.Append("ORBIS ").Append(orbis.IsLive ? "LIVE" : "clip").Append(" state=").Append(orbis.State)
                   .Append(" scene ").Append(orbis.Scene).Append(orbis.PendingScene.Length > 0 ? " (asked " + orbis.PendingScene + ")" : "")
                   .Append(orbis.ClipScene != orbis.Scene && orbis.ClipScene.Length > 0 ? " (clip of " + orbis.ClipScene + ")" : "")
                   .Append(orbis.IsLive ? "" : " " + orbis.PlayingClip).Append("  frames ").Append(orbis.FramesDecoded)
                   .Append(" (dropped ").Append(orbis.FramesDropped).Append(")  decode ")
                   .Append(orbis.LastDecodeMs.ToString("0.0")).Append(" ms\n");
            _sb.Append("STATIONS ").Append(BeeswaxStations.Source.Length > 0 ? BeeswaxStations.Source + " (" + BeeswaxStations.LoadedCount + ")" : "defaults").Append('\n');
            foreach (var l in _lines) _sb.Append(l).Append('\n');

            Text = _sb.ToString();
            if (worldText != null) worldText.text = Text;
            onText.Invoke(Text);
        }

        void OnGUI()
        {
            if (!drawOnScreen) return;
            GUI.Label(new Rect(10, 10, Screen.width - 20, Screen.height - 20), Text);
        }
    }
}
