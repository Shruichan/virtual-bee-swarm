using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Events;

namespace Beeswax
{
    /// <summary>
    /// Debug text: bus connection, last messages, voice, hum and Orbis stats.
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
        [SerializeField] TextMesh worldText;
        [SerializeField] UnityEvent<string> onText = new UnityEvent<string>();
        [SerializeField] bool drawOnScreen = true;
        [SerializeField] int maxLines = 8;
        [SerializeField] float refreshSeconds = 0.2f;

        public string Text { get; private set; } = "";

        readonly Queue<string> _lines = new Queue<string>();
        readonly StringBuilder _sb = new StringBuilder();
        BeeswaxBus _bound;
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
        }

        void Unbind()
        {
            if (_bound == null) return;
            _bound.OnConnectionChanged -= HandleConnection;
            _bound.OnMessage -= HandleMessage;
            _bound.OnScanPhoto -= HandlePhoto;
            _bound = null;
        }

        void HandleConnection(BusState state, string host) => Add($"{state} {host}");
        void HandlePhoto(ScanPhoto meta, byte[] jpeg) => Add($"photo {jpeg.Length / 1024} KB");

        void HandleMessage(string type, string json)
        {
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
            if (hum != null)
                _sb.Append("HUM ").Append(hum.Listening ? "listening" : "off").Append(hum.Voiced ? " VOICED " : " ")
                   .Append(hum.PitchHz.ToString("0")).Append(" Hz  ").Append(hum.LevelDb.ToString("0")).Append(" dB (floor ")
                   .Append(hum.NoiseFloorDb.ToString("0")).Append(")  level ").Append(hum.Level.ToString("0.00"))
                   .Append("  heat ").Append(hum.Heat.ToString("0.00")).Append('\n');
            if (orbis != null)
                _sb.Append("ORBIS ").Append(orbis.IsLive ? "LIVE" : "clip").Append(" state=").Append(orbis.State)
                   .Append(" stage ").Append(orbis.Stage).Append("  frames ").Append(orbis.FramesDecoded)
                   .Append(" (dropped ").Append(orbis.FramesDropped).Append(")  decode ")
                   .Append(orbis.LastDecodeMs.ToString("0.0")).Append(" ms\n");
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
