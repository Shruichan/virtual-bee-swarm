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
    /// Sends are queued and written off the main thread (BeeswaxSocket), so a dead link never stalls a frame.
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
        [Tooltip("Reconnect when a send has been stuck this long (half-open link while the mic streams). 0 = off.")]
        [SerializeField] float sendStallSeconds = 4f;
        [Tooltip("Queued swarm.arrived / hornet.cooked / choice.made older than this are dropped instead of sent after a reconnect.")]
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
        public event Action<OrbisRequest> OnOrbisRequest;  // Board: live | clips | off (+ stage/scene); OrbisWindow obeys it
        public event Action<SwarmArrived> OnSwarmArrived;
        public event Action<HornetCooked> OnHornetCooked;
        public event Action<Latency> OnLatency;
        public event Action<ErrorMsg> OnHiveError;
        // v2
        /// <summary>A choice.made on the bus. The Hive sends choice.made only to the Board/monitor, so on the Quest this
        /// fires only for InjectLocal; react to the Hive's say/orbis/fx instead (BeeswaxStations does).</summary>
        public event Action<ChoiceMade> OnChoiceMade;
        /// <summary>Hive: speech-to-text of an utterance (source quest | glasses). Partial ones have final = false.</summary>
        public event Action<Transcript> OnTranscript;
        /// <summary>Hive: what the player meant (what_if + topic, undo, ask, ack, unknown).</summary>
        public event Action<Intent> OnIntent;
        /// <summary>
        /// Hive: play an effect (embers, swarm_calm, ...). Fires on receipt: honour delay_ms yourself, and make a later
        /// embers_stop cancel an embers still waiting on its delay. Or use OnFxDue, which does both. The Board's fx.force
        /// reaches the Quest as a plain fx, so this covers it too. Use OnFx or OnFxDue, not both, for the same effect.
        /// </summary>
        public event Action<Fx> OnFx;
        /// <summary>
        /// The same effects as OnFx, raised when they should start: at once, or after delay_ms. A later fx of the same name
        /// or its opposite (embers_stop for embers, swarm_angry for swarm_calm, hive_glow_off for hive_glow_red, and back)
        /// cancels one still waiting, so "put it out" before the embers land means no embers. group.reset clears them.
        /// </summary>
        public event Action<Fx> OnFxDue;
        /// <summary>listen.start/listen.end from the bus. The Hive never rebroadcasts them (InjectLocal only).</summary>
        public event Action<ListenStart> OnListenStart;
        public event Action<ListenEnd> OnListenEnd;
        // v2.1
        /// <summary>
        /// Board (via the Hive): the operator's twin of a player's station choice. Apply it exactly like the player's own
        /// (call BeeswaxStations.Choose, which reports choice.made as usual); you may ignore it outside that station's beat.
        /// BeeswaxStations.ApplyChoiceRequests does that for you. Only pairs in CHOICES are raised.
        /// </summary>
        public event Action<ChoiceRequest> OnChoiceRequest;
        /// <summary>Hive: the run's score, sent when the escape beat starts (also kept in LastScore).</summary>
        public event Action<Score> OnScore;
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
        int? _lastBeatScoreSeconds;
        bool _scoreRequiresNewRun;
        /// <summary>Hive clock minus local clock, from welcome.server_time (ms; rough).</summary>
        public double ServerClockOffsetMs { get; private set; }
        public string LastCloseReason { get; private set; } = "";
        public float SmoothedFrameMs => _frameMs;
        /// <summary>Seconds since anything arrived on the current link (large when offline).</summary>
        public float SecondsSinceInbound => Time.realtimeSinceStartup - _lastInboundAt;
        /// <summary>Bytes queued on the socket but not yet written (mic backpressure).</summary>
        public int QueuedSendBytes => _socket.QueuedSendBytes;
        /// <summary>A listen.start was sent on this link and its listen.end not yet: MIC_PCM16 frames are accepted.</summary>
        public bool IsListening => _listenOpen;
        public string CurrentListenMode { get; private set; } = "";
        public int MicFramesSent { get; private set; }
        public long MicBytesSent { get; private set; }
        /// <summary>The last score message of this group (null before escape, and after group.reset).</summary>
        public Score LastScore { get; private set; }
        /// <summary>Effects waiting on their delay_ms for OnFxDue.</summary>
        public int PendingFxCount => _pendingFx.Count;

        readonly BeeswaxSocket _socket = new BeeswaxSocket();
        readonly List<(float at, string group, string json)> _outbox = new List<(float, string, string)>();
        string _token;
        bool _wantConnected, _welcomedThisLink, _outboxGroupConfirmed;
        int _hostIndex, _hostFailures, _backoffStep;
        float _nextAttemptAt = -1f, _lastInboundAt, _nextHeartbeatAt, _beatSentAt = -100f;
        float _frameMs = 13.9f;
        DateTime _pausedAtUtc;
        ScanPhoto _pendingPhoto;
        string _lastScanId = "", _currentTtsId = "";
        bool _listenOpen;
        // OnFxDue: effects waiting on delay_ms; fx.force/fx de-duplication
        readonly List<(Fx fx, float due)> _pendingFx = new List<(Fx, float)>();
        string _lastFxName = "", _lastForcedFxName = "";
        float _lastFxAt = -100f, _lastForcedFxAt = -100f;
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
            _pendingFx.Clear();
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
            if (_pendingFx.Count > 0) RaiseDueFx(); // works offline too (InjectLocal, BeeswaxStations' local outcomes)

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
            if (sendStallSeconds > 0f && _socket.SendStallSeconds > sendStallSeconds)
            {
                Debug.LogWarning($"[Beeswax] a send to {CurrentHost} is stuck ({_socket.QueuedSendBytes} bytes queued); reconnecting.");
                _socket.Abort();
                HandleClosed(true, "send stalled");
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
            _listenOpen = false;
            _nextHeartbeatAt = _lastInboundAt + heartbeatSeconds;
            _pendingPhoto = null;
            _welcomedThisLink = false;
            _outboxGroupConfirmed = false;
            SetState(BusState.Open);
            Debug.Log("[Beeswax] connected to " + CurrentHost);
            SendHello();
        }

        void SendHello() => Send(new Hello { role = Role.Quest, client = clientName + "/" + Application.version });

        void HandleClosed(bool wasOpen, string reason)
        {
            LastCloseReason = reason;
            _listenOpen = false; // the Hive drops an open utterance with the socket
            _outboxGroupConfirmed = false;
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
                case BinaryKind.MicPcm16:
                    break; // client → Hive only; an echo is not for us
                default:
                    Debug.LogWarning($"[Beeswax] ignored binary frame of kind 0x{data[0]:X2} ({data.Length} bytes)");
                    break;
            }
        }

        void HandleJson(string json, bool local = false)
        {
            string type = BeeswaxProtocol.PeekType(json);
            if (logTraffic && !local) Debug.Log("[Beeswax] ← " + (json.Length > 300 ? json.Substring(0, 300) + "…" : json));
            Raise(OnMessage, type, json);
            try
            {
                switch (type)
                {
                    case MsgType.Welcome:
                        var w = BeeswaxProtocol.Parse<Welcome>(json);
                        LastWelcome = w;
                        bool scoreMatchesGroup = string.IsNullOrEmpty(GroupId) || GroupId == w.group_id;
                        if (!string.IsNullOrEmpty(w.group_id))
                        {
                            if (!scoreMatchesGroup) InvalidateGroupScore();
                            GroupId = w.group_id;
                        }
                        Mode = w.mode;
                        if (w.server_time > 0) ServerClockOffsetMs = w.server_time - NowUnixMs();
                        SetState(BusState.Welcomed);
                        // Unity owns the beat and the frozen run time. The first welcome may already have the
                        // correct beat but lack the score, so reaffirm the score once per link for this group.
                        bool missedBeat = w.beat != LastBeat && Time.realtimeSinceStartup - _beatSentAt > 2f;
                        int? score = scoreMatchesGroup ? _lastBeatScoreSeconds : null;
                        bool reaffirmScore = !_welcomedThisLink && score.HasValue;
                        if (LastBeat >= 0 && (missedBeat || reaffirmScore))
                        {
                            Debug.Log($"[Beeswax] Hive says beat {w.beat}, we are at {LastBeat}: re-sending beat.set.");
                            SendBeatMessage(LastBeat, score);
                        }
                        // A socket can reconnect after the Board has started a different group.
                        // Check the Hive's group and reconcile the beat before replaying player actions.
                        if (!local && !string.IsNullOrEmpty(w.group_id))
                        {
                            _outboxGroupConfirmed = true;
                            FlushOutbox(w.group_id, Time.realtimeSinceStartup, SendJson);
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
                        InvalidateGroupScore();
                        _outbox.Clear();
                        CancelPendingFx();
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
                    case MsgType.ChoiceMade: Raise(OnChoiceMade, BeeswaxProtocol.Parse<ChoiceMade>(json)); break;
                    case MsgType.Transcript: Raise(OnTranscript, BeeswaxProtocol.Parse<Transcript>(json)); break;
                    case MsgType.Intent: Raise(OnIntent, BeeswaxProtocol.Parse<Intent>(json)); break;
                    case MsgType.Fx:
                        var fx = BeeswaxProtocol.Parse<Fx>(json);
                        // The Hive forwards the Board's fx.force as this fx; if it ever sent both, play it once.
                        if (fx.name == _lastForcedFxName && Time.realtimeSinceStartup - _lastForcedFxAt < 1f) { _lastForcedFxName = ""; break; }
                        _lastFxName = fx.name;
                        _lastFxAt = Time.realtimeSinceStartup;
                        DispatchFx(fx);
                        break;
                    case MsgType.FxForce:
                        // Normally never reaches the Quest (the Hive turns it into a plain fx). If a Hive forwards it
                        // unchanged, play it like an fx, unless that fx just arrived.
                        var ff = BeeswaxProtocol.Parse<FxForce>(json);
                        if (!BeeswaxProtocol.IsFx(ff.name)) { Debug.LogWarning($"[Beeswax] fx.force '{ff.name}' is not one of FX; ignored."); break; }
                        if (ff.name == _lastFxName && Time.realtimeSinceStartup - _lastFxAt < 1f) break;
                        _lastForcedFxName = ff.name;
                        _lastForcedFxAt = Time.realtimeSinceStartup;
                        DispatchFx(new Fx { name = ff.name });
                        break;
                    case MsgType.ChoiceRequest:
                        var cr = BeeswaxProtocol.Parse<ChoiceRequest>(json);
                        if (!cr.IsValid) { Debug.LogWarning($"[Beeswax] choice.request '{cr.station}:{cr.choice}' is not in CHOICES; ignored."); break; }
                        if (OnChoiceRequest == null)
                        {
                            Debug.LogWarning($"[Beeswax] choice.request {cr.station}:{cr.choice} arrived but nothing handles it: subscribe to " +
                                             "BeeswaxBus.OnChoiceRequest, or turn on BeeswaxStations.applyChoiceRequests.");
                            // The Hive applies a choice.request itself only when no Quest is connected, so tell the Board.
                            if (!local) SendLog($"operator choice {cr.station}:{cr.choice} ignored: nothing on the Quest handles choice.request");
                        }
                        Raise(OnChoiceRequest, cr);
                        break;
                    case MsgType.Score:
                        LastScore = BeeswaxProtocol.Parse<Score>(json);
                        Raise(OnScore, LastScore);
                        break;
                    case MsgType.ListenStart: Raise(OnListenStart, BeeswaxProtocol.Parse<ListenStart>(json)); break;
                    case MsgType.ListenEnd: Raise(OnListenEnd, BeeswaxProtocol.Parse<ListenEnd>(json)); break;
                    case MsgType.Error:
                        var e = BeeswaxProtocol.Parse<ErrorMsg>(json);
                        Debug.LogWarning("[Beeswax] Hive error: " + e.message);
                        Raise(OnHiveError, e);
                        break;
                    case "":
                        Debug.LogWarning("[Beeswax] ignored frame without a type: " + (json.Length > 120 ? json.Substring(0, 120) : json));
                        break;
                    // Anything else (log, verdict.force, whatif.force, ...) is only visible through OnMessage.
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Beeswax] bad '{type}' message ({ex.Message}): {json}");
            }
        }

        // ---------------------------------------------------------------- send
        /// <summary>Tell the Hive the beat machine entered beat n (Unity is the only beat clock).</summary>
        public void SendBeatSet(int n, int? scoreSeconds = null)
        {
            if (n == Beat.Pre) _scoreRequiresNewRun = false;
            if (_scoreRequiresNewRun) scoreSeconds = null;
            LastBeat = n;
            _lastBeatScoreSeconds = scoreSeconds;
            SendBeatMessage(n, scoreSeconds);
        }

        void SendBeatMessage(int n, int? scoreSeconds)
        {
            if (Send(new BeatSet(n, scoreSeconds))) _beatSentAt = Time.realtimeSinceStartup; // re-sent after the next welcome when needed
        }

        void InvalidateGroupScore()
        {
            LastScore = null;
            _lastBeatScoreSeconds = null;
            // A local finale may still be running after an offline group reset. Do not let its next beat
            // publish the previous group's frozen time under the new ID. The next Pre clears the guard.
            _scoreRequiresNewRun = LastBeat >= Beat.Smoker;
        }

        public void SendSwarmArrived(int socket) => SendOrQueue(new SwarmArrived { socket = socket });
        public void SendHornetCooked(bool overshoot) => SendOrQueue(new HornetCooked { overshoot = overshoot });
        public void SendLatency(string hop, double ms) => Send(new Latency { hop = hop, ms = ms, source = Role.Quest });
        public void SendLog(string text) => Send(new LogMsg { text = text, source = Role.Quest });

        /// <summary>
        /// Tell the Hive the player chose `choice` at `station` (authoritative; `correct` is computed here).
        /// The Hive answers with the window scene, Beeswax's line (say) and fx. Returns true if it went out now;
        /// false if offline (queued briefly for the last known Hive group, with a local outcome from
        /// BeeswaxStations) or if the pair is not in CHOICES (logged, not sent).
        /// </summary>
        public bool SendChoiceMade(string station, string choice)
        {
            if (!BeeswaxProtocol.IsValidChoice(station, choice))
            {
                Debug.LogError($"[Beeswax] '{choice}' is not a choice of station '{station}'; not sent.");
                return false;
            }
            return SendOrQueue(new ChoiceMade(station, choice));
        }

        /// <summary>
        /// Open an utterance (mode: ListenMode.Ptt | ListenMode.Vad). MIC_PCM16 frames are accepted until
        /// SendListenEnd. An utterance still open is cancelled first. False when offline.
        /// BeeswaxVoiceInput does all of this for you.
        /// </summary>
        public bool SendListenStart(string mode = ListenMode.Ptt)
        {
            if (_listenOpen) SendListenEnd(cancel: true);
            if (mode != ListenMode.Vad) mode = ListenMode.Ptt;
            if (!Send(new ListenStart { source = Role.Quest, mode = mode })) return false;
            _listenOpen = true;
            CurrentListenMode = mode;
            return true;
        }

        /// <summary>One chunk of mic audio: PCM16 little-endian mono at 16 kHz (BeeswaxProtocol.MicSampleRate), sent as
        /// [0x03][pcm]. False (dropped) when offline or when no utterance is open on this link.</summary>
        public bool SendMicFrame(byte[] pcm16) => pcm16 != null && SendMicFrame(pcm16, 0, pcm16.Length);

        public bool SendMicFrame(byte[] pcm16, int offset, int count)
        {
            if (!_listenOpen || pcm16 == null || count <= 0) return false;
            if (count % 2 != 0) count--; // whole samples only
            if (offset < 0 || offset + count > pcm16.Length) return false;
            if (!_socket.SendBytes(BeeswaxProtocol.Frame(BinaryKind.MicPcm16, pcm16, offset, count))) return false;
            MicFramesSent++;
            MicBytesSent += count;
            return true;
        }

        /// <summary>Close the utterance (cancel = the Hive throws it away). False if none was open on this link.</summary>
        public bool SendListenEnd(bool cancel = false)
        {
            if (!_listenOpen) return false;
            _listenOpen = false;
            return Send(new ListenEnd { source = Role.Quest, cancel = cancel });
        }

        /// <summary>
        /// Ask the Hive for a window layer (OrbisWant.Live | Clips | Off; "" = leave the mode alone) and/or scene (one of
        /// SCENES; "" = keep). v2.1: a request with a scene/stage only switches the scene and only `want` changes the mode,
        /// but a v2 Hive rejects a request without want, so OrbisWindow.RequestScene still sends one. It wraps this and
        /// also switches the local fallback clip right away.
        /// </summary>
        public bool SendOrbisRequest(string want, string scene = "", int stage = -1)
        {
            want = want ?? "";
            if (want.Length > 0 && want != OrbisWant.Live && want != OrbisWant.Clips && want != OrbisWant.Off)
            {
                Debug.LogError($"[Beeswax] orbis.request want '{want}' is not live|clips|off; not sent.");
                return false;
            }
            if (want.Length == 0 && string.IsNullOrEmpty(scene) && stage < 0)
            {
                Debug.LogError("[Beeswax] orbis.request needs a want, a scene or a stage; not sent.");
                return false;
            }
            if (!string.IsNullOrEmpty(scene) && !BeeswaxProtocol.IsScene(scene))
            {
                Debug.LogError($"[Beeswax] '{scene}' is not one of SCENES; not sent.");
                return false;
            }
            return Send(new OrbisRequest { want = want, scene = scene ?? "", stage = stage });
        }

        // ---------------------------------------------------------------- fx timing (OnFxDue)
        /// <summary>
        /// Drop effects still waiting on their delay: all of them (name = null) or one name. A later embers_stop already
        /// cancels a waiting embers by itself; group.reset cancels all.
        /// </summary>
        public void CancelPendingFx(string name = null)
        {
            int removed = _pendingFx.RemoveAll(p => name == null || p.fx.name == name);
            if (removed > 0 && logTraffic) Debug.Log($"[Beeswax] cancelled {removed} waiting fx" + (name == null ? "" : " " + name));
        }

        /// <summary>The effect that undoes `name` (embers ↔ embers_stop, swarm_calm ↔ swarm_angry, hive_glow_red ↔ hive_glow_off), or "".</summary>
        public static string OppositeFx(string name)
        {
            switch (name)
            {
                case FxName.Embers: return FxName.EmbersStop;
                case FxName.EmbersStop: return FxName.Embers;
                case FxName.SwarmCalm: return FxName.SwarmAngry;
                case FxName.SwarmAngry: return FxName.SwarmCalm;
                case FxName.HiveGlowRed: return FxName.HiveGlowOff;
                case FxName.HiveGlowOff: return FxName.HiveGlowRed;
                default: return "";
            }
        }

        void DispatchFx(Fx f)
        {
            Raise(OnFx, f);
            // The newest instruction wins: a waiting fx of the same name (rescheduled) or its opposite is dropped.
            string opposite = OppositeFx(f.name);
            for (int i = _pendingFx.Count - 1; i >= 0; i--)
            {
                string pending = _pendingFx[i].fx.name;
                if (pending != f.name && pending != opposite) continue;
                _pendingFx.RemoveAt(i);
                Debug.Log(pending == f.name
                    ? $"[Beeswax] fx {pending} was still waiting on its delay; replaced by the newer one."
                    : $"[Beeswax] fx {pending} was still waiting on its delay; cancelled by {f.name}.");
            }
            if (f.HasDelay) _pendingFx.Add((f, Time.realtimeSinceStartup + f.delay_ms / 1000f));
            else Raise(OnFxDue, f);
        }

        void RaiseDueFx()
        {
            float now = Time.realtimeSinceStartup;
            while (true)
            {
                int next = -1;
                for (int i = 0; i < _pendingFx.Count; i++)
                    if (_pendingFx[i].due <= now && (next < 0 || _pendingFx[i].due < _pendingFx[next].due)) next = i;
                if (next < 0) return;
                var f = _pendingFx[next].fx;
                _pendingFx.RemoveAt(next);
                Raise(OnFxDue, f);
            }
        }

        /// <summary>
        /// Raise the events for a message as if the Hive had sent it (offline fallbacks, editor testing, debug menus).
        /// Nothing is sent. Don't inject welcome/group.reset unless you mean it.
        /// </summary>
        public void InjectLocal(BusMessage msg)
        {
            if (msg == null) return;
            if (logTraffic) Debug.Log("[Beeswax] (local) " + BeeswaxProtocol.ToJson(msg));
            HandleJson(BeeswaxProtocol.ToJson(msg), local: true);
        }

        /// <summary>Send any protocol message now. Returns false (and drops it) when not connected.</summary>
        public bool Send(BusMessage msg) => SendJson(BeeswaxProtocol.ToJson(msg));

        public bool SendJson(string json)
        {
            bool ok = _socket.SendText(json);
            if (logTraffic) Debug.Log((ok ? "[Beeswax] → " : "[Beeswax] (offline, dropped) → ") + json);
            return ok;
        }

        bool SendOrQueue(BusMessage msg)
        {
            string json = BeeswaxProtocol.ToJson(msg);
            if (_outboxGroupConfirmed && SendJson(json)) return true;
            // An action taken before ever joining a group has only its local outcome.
            if (string.IsNullOrEmpty(GroupId)) return false;
            if (_outbox.Count >= 16) _outbox.RemoveAt(0);
            _outbox.Add((Time.realtimeSinceStartup, GroupId, json));
            return false;
        }

        void FlushOutbox(string group, float now, Func<string, bool> send)
        {
            while (_outbox.Count != 0)
            {
                var entry = _outbox[0];
                if (entry.group == group && now - entry.at <= outboxMaxAgeSeconds && !send(entry.json)) return;
                _outbox.RemoveAt(0);
            }
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
