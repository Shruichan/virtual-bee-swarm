using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace Beeswax
{
    public enum BusState { Offline, Connecting, Open, Welcomed }

    /// <summary>
    /// The Quest's connection to the Hive bus: wss://&lt;host&gt;/bus?role=quest&amp;token=…
    /// Tries hosts in order, reconnects with backoff, moves to the next host after N failed connects,
    /// and raises one C# event per message type. Every event fires on the main thread.
    /// Put one in the first scene (it survives scene loads); find it with BeeswaxBus.Instance.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class BeeswaxBus : MonoBehaviour
    {
        public static BeeswaxBus Instance { get; private set; }

        [Header("Hive")]
        [Tooltip("Tried in order. wss://host, ws://ip:port (local testing) or a bare host name.")]
        [SerializeField] List<string> hosts = new List<string> { "wss://beeswax-venue.drori.dev", "wss://beeswax.drori.dev" };
        [Tooltip("HIVE_TOKEN. Leave empty to read Resources/beeswax_token.txt (gitignored), then the HIVE_TOKEN env var (editor).")]
        [SerializeField] string token = "";
        [SerializeField] string clientName = "beeswax-unity";
        [SerializeField] bool connectOnStart = true;
        [SerializeField] bool persistAcrossScenes = true;

        [Header("Reconnect")]
        [SerializeField] float connectTimeoutSeconds = 6f;
        [SerializeField] float minBackoffSeconds = 0.5f;
        [SerializeField] float maxBackoffSeconds = 8f;
        [Tooltip("Failed connects in a row before trying the next host.")]
        [SerializeField] int failuresBeforeSwitch = 3;
        [Tooltip("A link that dies sooner than this after opening counts as a failed connect (e.g. a rejected token).")]
        [SerializeField] float minHealthySeconds = 5f;
        [Tooltip("Reconnect after the app was paused (headset off) this long; the old socket may be dead.")]
        [SerializeField] float reconnectAfterPauseSeconds = 15f;
        [Tooltip("After a failover, check the first host's /health every N s and move back once it answers, so the Quest " +
                 "and the glasses (which do the same) end up on the same Hive. 0 = stay on the fallback.")]
        [SerializeField] float returnToPrimarySeconds = 30f;

        [Header("Heartbeat")]
        [Tooltip("Every N s re-send hello (the Hive answers with welcome, like the Board/glasses keepalive) and " +
                 "latency{hop:\"quest.frame\", ms:<frame time>} for the Board. 0 = off.")]
        [SerializeField] float heartbeatSeconds = 10f;
        [Tooltip("Reconnect when nothing (not even a heartbeat welcome) arrives for N s: catches half-open links. 0 = off.")]
        [SerializeField] float staleSeconds = 30f;
        [Tooltip("Queued swarm.arrived / hornet.cooked older than this are dropped instead of sent after a reconnect.")]
        [SerializeField] float outboxMaxAgeSeconds = 10f;
        [SerializeField] bool logTraffic;

        // ---------------------------------------------------------------- events (main thread)
        public event Action<BusState, string> OnConnectionChanged; // (state, host)
        /// <summary>First welcome on each new link (heartbeat welcomes only update GroupId/Mode/LastWelcome).</summary>
        public event Action<Welcome> OnWelcome;
        public event Action<BeatRequest> OnBeatRequest;
        public event Action<BeatSet> OnBeatSet;            // rebroadcast of a beat.set (maybe our own)
        public event Action<string> OnModeChanged;         // "duo" | "solo"
        public event Action<GroupReset> OnGroupReset;
        public event Action<ScanStarted> OnScanStarted;
        public event Action<ScanPhoto, byte[]> OnScanPhoto; // JPEG bytes; PolaroidFeed turns them into a texture
        public event Action<ScanVerdict> OnScanVerdict;
        public event Action<Say> OnSay;                    // all channels; the Quest should play only "room"
        public event Action<TtsStart> OnTtsStart;
        public event Action<string, byte[]> OnTtsChunk;    // (tts_id, PCM16 LE mono bytes without the kind byte)
        public event Action<TtsEnd> OnTtsEnd;
        public event Action<Plea> OnPlea;
        public event Action<HumSet> OnHumSet;
        public event Action<OrbisStatus> OnOrbis;
        public event Action<OrbisRequest> OnOrbisRequest;  // Board: live | clips | off (+ stage); OrbisWindow obeys it
        public event Action<SwarmArrived> OnSwarmArrived;
        public event Action<HornetCooked> OnHornetCooked;
        public event Action<Latency> OnLatency;
        public event Action<ErrorMsg> OnHiveError;
        /// <summary>Every text message, known or not: (type, raw json). Handy for debug HUDs.</summary>
        public event Action<string, string> OnMessage;

        // ---------------------------------------------------------------- state
        public BusState State { get; private set; } = BusState.Offline;
        public bool IsConnected => State == BusState.Open || State == BusState.Welcomed;
        /// <summary>Normalized base URL of the host in use (or being tried), e.g. "wss://beeswax.drori.dev".</summary>
        public string CurrentHost => hosts.Count == 0 ? "" : NormalizeHost(hosts[_hostIndex % hosts.Count]);
        public int CurrentHostIndex => _hostIndex;
        public IReadOnlyList<string> Hosts => hosts;
        public string Token => _token ?? (_token = ResolveToken());
        public string GroupId { get; private set; } = "";
        public string Mode { get; private set; } = "";
        public Welcome LastWelcome { get; private set; }
        /// <summary>Last beat this Quest reported (-1 = none yet).</summary>
        public int LastBeat { get; private set; } = -1;
        /// <summary>Hive clock minus local clock, from welcome.server_time (ms; rough).</summary>
        public double ServerClockOffsetMs { get; private set; }
        public string LastCloseReason { get; private set; } = "";
        public float SmoothedFrameMs => _frameMs;

        readonly BeeswaxSocket _socket = new BeeswaxSocket();
        readonly List<(float at, string json)> _outbox = new List<(float, string)>();
        string _token;
        bool _wantConnected, _welcomedThisLink;
        int _hostIndex, _hostFailures, _backoffStep;
        float _nextAttemptAt = -1f, _lastInboundAt, _nextHeartbeatAt, _beatSentAt = -100f;
        float _frameMs = 13.9f;
        DateTime _pausedAtUtc;
        ScanPhoto _pendingPhoto;
        string _lastScanId = "", _currentTtsId = "";
        // return-to-primary probe (only while connected to a fallback host after an automatic failover)
        UnityWebRequest _probe;
        bool _onFallback;
        float _nextProbeAt, _probeInterval, _returnedAt = -1000f;

        // ---------------------------------------------------------------- lifecycle
        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[Beeswax] a second BeeswaxBus was destroyed; keeping the first.");
                Destroy(this);
                return;
            }
            Instance = this;
            if (persistAcrossScenes)
            {
                if (transform.parent == null) DontDestroyOnLoad(gameObject);
                else Debug.LogWarning("[Beeswax] BeeswaxBus must be on a root object to persist across scenes.");
            }
            _socket.Opened += HandleOpened;
            _socket.Closed += HandleClosed;
            _socket.Message += HandleFrame;
        }

        void Start()
        {
            if (Instance == this && connectOnStart) Connect();
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;
            _wantConnected = false;
            CancelProbe();
            _socket.Close();
        }

        void OnApplicationQuit()
        {
            _wantConnected = false;
            _socket.Close();
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) { _pausedAtUtc = DateTime.UtcNow; return; }
            if (!_wantConnected || _pausedAtUtc == default) return;
            double pausedFor = (DateTime.UtcNow - _pausedAtUtc).TotalSeconds;
            if (IsConnected && pausedFor > reconnectAfterPauseSeconds)
            {
                Debug.Log($"[Beeswax] resumed after {pausedFor:0}s paused; reconnecting.");
                Reconnect();
            }
            else if (!IsConnected) _nextAttemptAt = Time.realtimeSinceStartup;
        }

        void Update()
        {
            if (Instance != this) return;
            _frameMs = Mathf.Lerp(_frameMs, Time.unscaledDeltaTime * 1000f, 0.05f);
            _socket.Tick();

            float now = Time.realtimeSinceStartup;
            if (_wantConnected && _socket.State == BeeswaxSocket.Status.Idle && _nextAttemptAt >= 0f && now >= _nextAttemptAt)
                StartAttempt();

            if (!IsConnected) return;
            if (heartbeatSeconds > 0f && now >= _nextHeartbeatAt)
            {
                _nextHeartbeatAt = now + heartbeatSeconds;
                SendHello(); // the Hive answers every hello with a welcome: our application-level pong
                SendLatency("quest.frame", Math.Round(_frameMs, 1));
            }
            if (staleSeconds > 0f && now - _lastInboundAt > staleSeconds)
            {
                Debug.LogWarning($"[Beeswax] nothing from {CurrentHost} for {staleSeconds:0}s; reconnecting.");
                _socket.Abort();
                HandleClosed(true, "stale");
                return;
            }
            if (_onFallback && returnToPrimarySeconds > 0f && _probe == null && now >= _nextProbeAt) ProbePrimary();
        }

        // ---------------------------------------------------------------- connection control
        public void Connect()
        {
            _wantConnected = true;
            _token = ResolveToken();
            if (string.IsNullOrEmpty(_token))
                Debug.LogWarning("[Beeswax] no token: set it on BeeswaxBus or in Resources/beeswax_token.txt. The Hive will likely reject us.");
            if (hosts.Count == 0) { Debug.LogError("[Beeswax] BeeswaxBus has no hosts."); return; }
            if (_socket.State == BeeswaxSocket.Status.Idle) StartAttempt();
        }

        public void Disconnect()
        {
            _wantConnected = false;
            CancelProbe();
            _socket.Close();
            SetState(BusState.Offline);
        }

        /// <summary>Drop the current link and reconnect to the same host now.</summary>
        public void Reconnect()
        {
            _socket.Abort();
            SetState(BusState.Offline);
            _wantConnected = true;
            _nextAttemptAt = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Switch to hosts[index] now (e.g. back to the venue host from a debug menu). A host picked by hand is kept:
        /// the automatic return to the first host only follows an automatic failover.
        /// </summary>
        public void UseHost(int index)
        {
            if (hosts.Count == 0) return;
            _hostIndex = Mathf.Clamp(index, 0, hosts.Count - 1);
            _hostFailures = 0;
            _backoffStep = 0;
            _onFallback = false;
            CancelProbe();
            Reconnect();
        }

        /// <summary>Full URL for a Hive path on the current host, with the token, e.g. UrlFor("/orbis/frames").</summary>
        public string UrlFor(string path, string query = null)
        {
            string q = string.IsNullOrEmpty(query) ? "" : query + "&";
            return CurrentHost + path + "?" + q + "token=" + Uri.EscapeDataString(Token);
        }

        public static string NormalizeHost(string host)
        {
            host = (host ?? "").Trim().TrimEnd('/');
            if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "wss://" + host.Substring(8);
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "ws://" + host.Substring(7);
            return host.Contains("://") ? host : "wss://" + host;
        }

        void StartAttempt()
        {
            _nextAttemptAt = -1f;
            _socket.ConnectTimeoutSeconds = connectTimeoutSeconds;
            SetState(BusState.Connecting);
            _socket.Open(UrlFor("/bus", "role=" + Role.Quest));
        }

        void HandleOpened()
        {
            _lastInboundAt = Time.realtimeSinceStartup;
            _nextHeartbeatAt = _lastInboundAt + heartbeatSeconds;
            _pendingPhoto = null;
            _welcomedThisLink = false;
            SetState(BusState.Open);
            Debug.Log("[Beeswax] connected to " + CurrentHost);
            SendHello();
            FlushOutbox();
        }

        void SendHello() => Send(new Hello { role = Role.Quest, client = clientName + "/" + Application.version });

        void HandleClosed(bool wasOpen, string reason)
        {
            LastCloseReason = reason;
            float now = Time.realtimeSinceStartup;
            bool healthy = wasOpen && now - _socket.OpenedAt >= minHealthySeconds;
            SetState(BusState.Offline);
            if (!_wantConnected) return;

            float delay;
            if (healthy)
            {
                // A good link dropped (Hive restart, Wi-Fi blip): retry the same host right away.
                _hostFailures = 0;
                _backoffStep = 0;
                delay = minBackoffSeconds;
                Debug.LogWarning($"[Beeswax] lost {CurrentHost}: {reason}. Reconnecting.");
            }
            else
            {
                _hostFailures++;
                _backoffStep++;
                string failedHost = CurrentHost;
                if (hosts.Count > 1 && _hostFailures >= failuresBeforeSwitch)
                {
                    if (_hostIndex == 0)
                    {
                        // Back off the return probe if the primary failed again soon after we returned to it
                        // (e.g. /health answers but the bus doesn't), so we don't flap every 30 s.
                        _probeInterval = now - _returnedAt < 120f
                            ? Mathf.Min(300f, Mathf.Max(returnToPrimarySeconds, _probeInterval) * 2f)
                            : returnToPrimarySeconds;
                        _nextProbeAt = now + _probeInterval;
                    }
                    _hostIndex = (_hostIndex + 1) % hosts.Count;
                    _onFallback = _hostIndex != 0;
                    _hostFailures = 0;
                    _backoffStep = 0; // the new host gets quick retries too
                    delay = minBackoffSeconds;
                }
                else
                {
                    delay = Mathf.Min(maxBackoffSeconds, minBackoffSeconds * Mathf.Pow(2f, _backoffStep - 1));
                    delay *= UnityEngine.Random.Range(0.8f, 1.2f);
                }
                Debug.LogWarning($"[Beeswax] {failedHost} failed ({reason}); next try {CurrentHost} in {delay:0.0}s.");
            }
            _nextAttemptAt = now + delay;
        }

        void SetState(BusState s)
        {
            if (State == s) return;
            State = s;
            Raise(OnConnectionChanged, s, CurrentHost);
        }

        string ResolveToken()
        {
            if (!string.IsNullOrWhiteSpace(token)) return token.Trim();
            var asset = Resources.Load<TextAsset>("beeswax_token");
            if (asset != null && !string.IsNullOrWhiteSpace(asset.text)) return asset.text.Trim();
            return (Environment.GetEnvironmentVariable("HIVE_TOKEN") ?? "").Trim();
        }

        // ---------------------------------------------------------------- receive
        void HandleFrame(byte[] data)
        {
            _lastInboundAt = Time.realtimeSinceStartup;
            if (data == null || data.Length == 0) return;

            if (BeeswaxProtocol.LooksLikeJson(data)) { HandleJson(BeeswaxProtocol.Utf8(data)); return; }

            switch (data[0])
            {
                case BinaryKind.ScanPhoto:
                    var meta = _pendingPhoto ?? new ScanPhoto { scan_id = _lastScanId };
                    _pendingPhoto = null;
                    if (logTraffic) Debug.Log($"[Beeswax] ← photo {data.Length - 1} bytes");
                    Raise(OnScanPhoto, meta, BeeswaxProtocol.Payload(data));
                    break;
                case BinaryKind.TtsPcm16:
                    Raise(OnTtsChunk, _currentTtsId, BeeswaxProtocol.Payload(data));
                    break;
                default:
                    Debug.LogWarning($"[Beeswax] ignored binary frame of kind 0x{data[0]:X2} ({data.Length} bytes)");
                    break;
            }
        }

        void HandleJson(string json)
        {
            string type = BeeswaxProtocol.PeekType(json);
            if (logTraffic) Debug.Log("[Beeswax] ← " + (json.Length > 300 ? json.Substring(0, 300) + "…" : json));
            Raise(OnMessage, type, json);
            try
            {
                switch (type)
                {
                    case MsgType.Welcome:
                        var w = BeeswaxProtocol.Parse<Welcome>(json);
                        LastWelcome = w;
                        GroupId = w.group_id;
                        Mode = w.mode;
                        if (w.server_time > 0) ServerClockOffsetMs = w.server_time - NowUnixMs();
                        SetState(BusState.Welcomed);
                        // Unity owns the beat: if the Hive lost it (restart, host switch), tell it again. Skip right
                        // after a beat.set: this welcome may answer a hello sent before it.
                        if (LastBeat >= 0 && w.beat != LastBeat && Time.realtimeSinceStartup - _beatSentAt > 2f)
                        {
                            Debug.Log($"[Beeswax] Hive says beat {w.beat}, we are at {LastBeat}: re-sending beat.set.");
                            SendBeatSet(LastBeat);
                        }
                        if (!_welcomedThisLink)
                        {
                            _welcomedThisLink = true;
                            Raise(OnWelcome, w);
                        }
                        break;
                    case MsgType.BeatRequest: Raise(OnBeatRequest, BeeswaxProtocol.Parse<BeatRequest>(json)); break;
                    case MsgType.BeatSet: Raise(OnBeatSet, BeeswaxProtocol.Parse<BeatSet>(json)); break;
                    case MsgType.Mode:
                        Mode = BeeswaxProtocol.Parse<ModeMsg>(json).mode;
                        Raise(OnModeChanged, Mode);
                        break;
                    case MsgType.GroupReset:
                        var g = BeeswaxProtocol.Parse<GroupReset>(json);
                        if (g.group_id.Length > 0) GroupId = g.group_id;
                        Raise(OnGroupReset, g);
                        break;
                    case MsgType.ScanStarted:
                        var s = BeeswaxProtocol.Parse<ScanStarted>(json);
                        _lastScanId = s.scan_id;
                        Raise(OnScanStarted, s);
                        break;
                    case MsgType.ScanPhoto: _pendingPhoto = BeeswaxProtocol.Parse<ScanPhoto>(json); break;
                    case MsgType.ScanVerdict: Raise(OnScanVerdict, BeeswaxProtocol.Parse<ScanVerdict>(json)); break;
                    case MsgType.Say: Raise(OnSay, BeeswaxProtocol.Parse<Say>(json)); break;
                    case MsgType.TtsStart:
                        var t = BeeswaxProtocol.Parse<TtsStart>(json);
                        _currentTtsId = t.tts_id;
                        Raise(OnTtsStart, t);
                        break;
                    case MsgType.TtsEnd: Raise(OnTtsEnd, BeeswaxProtocol.Parse<TtsEnd>(json)); break;
                    case MsgType.Plea: Raise(OnPlea, BeeswaxProtocol.Parse<Plea>(json)); break;
                    case MsgType.HumSet: Raise(OnHumSet, BeeswaxProtocol.Parse<HumSet>(json)); break;
                    case MsgType.Orbis: Raise(OnOrbis, BeeswaxProtocol.Parse<OrbisStatus>(json)); break;
                    case MsgType.OrbisRequest: Raise(OnOrbisRequest, BeeswaxProtocol.Parse<OrbisRequest>(json)); break;
                    case MsgType.SwarmArrived: Raise(OnSwarmArrived, BeeswaxProtocol.Parse<SwarmArrived>(json)); break;
                    case MsgType.HornetCooked: Raise(OnHornetCooked, BeeswaxProtocol.Parse<HornetCooked>(json)); break;
                    case MsgType.Latency: Raise(OnLatency, BeeswaxProtocol.Parse<Latency>(json)); break;
                    case MsgType.Error:
                        var e = BeeswaxProtocol.Parse<ErrorMsg>(json);
                        Debug.LogWarning("[Beeswax] Hive error: " + e.message);
                        Raise(OnHiveError, e);
                        break;
                    case "":
                        Debug.LogWarning("[Beeswax] ignored frame without a type: " + (json.Length > 120 ? json.Substring(0, 120) : json));
                        break;
                    // Anything else (log, verdict.force, ...) is only visible through OnMessage.
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Beeswax] bad '{type}' message ({ex.Message}): {json}");
            }
        }

        // ---------------------------------------------------------------- send
        /// <summary>Tell the Hive the beat machine entered beat n (Unity is the only beat clock).</summary>
        public void SendBeatSet(int n)
        {
            LastBeat = n;
            if (Send(new BeatSet(n))) _beatSentAt = Time.realtimeSinceStartup; // if offline: re-sent after the next welcome
        }

        public void SendSwarmArrived(int socket) => SendOrQueue(new SwarmArrived { socket = socket });
        public void SendHornetCooked(bool overshoot) => SendOrQueue(new HornetCooked { overshoot = overshoot });
        public void SendLatency(string hop, double ms) => Send(new Latency { hop = hop, ms = ms, source = Role.Quest });
        public void SendLog(string text) => Send(new LogMsg { text = text, source = Role.Quest });

        /// <summary>Send any protocol message now. Returns false (and drops it) when not connected.</summary>
        public bool Send(BusMessage msg) => SendJson(BeeswaxProtocol.ToJson(msg));

        public bool SendJson(string json)
        {
            bool ok = _socket.SendText(json);
            if (logTraffic) Debug.Log((ok ? "[Beeswax] → " : "[Beeswax] (offline, dropped) → ") + json);
            return ok;
        }

        void SendOrQueue(BusMessage msg)
        {
            string json = BeeswaxProtocol.ToJson(msg);
            if (SendJson(json)) return;
            if (_outbox.Count >= 16) _outbox.RemoveAt(0);
            _outbox.Add((Time.realtimeSinceStartup, json));
        }

        void FlushOutbox()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var (at, json) in _outbox)
                if (now - at <= outboxMaxAgeSeconds) SendJson(json);
            _outbox.Clear();
        }

        // ---------------------------------------------------------------- return to the primary host
        /// <summary>https://&lt;host&gt;/health for a bus host ("wss://h" → "https://h/health").</summary>
        public static string HealthUrl(string host)
        {
            host = NormalizeHost(host);
            if (host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) host = "https://" + host.Substring(6);
            else if (host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) host = "http://" + host.Substring(5);
            return host + "/health";
        }

        void ProbePrimary()
        {
            if (hosts.Count < 2) { _onFallback = false; return; }
            _nextProbeAt = Time.realtimeSinceStartup + Mathf.Max(returnToPrimarySeconds, _probeInterval);
            var req = UnityWebRequest.Get(HealthUrl(hosts[0]));
            req.timeout = 4;
            _probe = req;
            req.SendWebRequest().completed += _ =>
            {
                bool up = req.result == UnityWebRequest.Result.Success && req.responseCode == 200;
                req.Dispose();
                if (_probe != req) return; // cancelled meanwhile
                _probe = null;
                if (!up || !_onFallback || !_wantConnected) return;
                Debug.Log($"[Beeswax] primary host {NormalizeHost(hosts[0])} is back; moving the bus there.");
                _returnedAt = Time.realtimeSinceStartup;
                UseHost(0);
            };
        }

        void CancelProbe()
        {
            var req = _probe;
            _probe = null;
            if (req == null) return;
            try { req.Abort(); } catch (Exception) { /* already finished */ }
        }

        // ---------------------------------------------------------------- helpers
        static double NowUnixMs() => (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

        // A throwing handler must not break the bus or the other handlers.
        static void Raise<T>(Action<T> ev, T a)
        {
            if (ev == null) return;
            foreach (var d in ev.GetInvocationList())
                try { ((Action<T>)d)(a); } catch (Exception e) { Debug.LogException(e); }
        }

        static void Raise<T1, T2>(Action<T1, T2> ev, T1 a, T2 b)
        {
            if (ev == null) return;
            foreach (var d in ev.GetInvocationList())
                try { ((Action<T1, T2>)d)(a, b); } catch (Exception e) { Debug.LogException(e); }
        }
    }
}
