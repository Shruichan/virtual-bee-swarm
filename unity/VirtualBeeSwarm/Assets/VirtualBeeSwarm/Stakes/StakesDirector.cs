using System;
using UnityEngine;

namespace VirtualBeeSwarm.Stakes
{
    /// <summary>
    /// The stakes, beat by beat (PLAN.md Concept v3.1, script in final spec §2; tasks V13, V16 escape part, V19, the
    /// rocket display of V14). Owns the dark swarm, the room dim ("stakes" key) and HONEYMOON-1's display:
    ///   hook     on "angry swarms" (1.3 s into room_hook_1, or 16 s into the beat if that line never starts) 150 dark
    ///            bees pour out of the window over the player into the band; the room dims to 25 %.
    ///   meet     the band thins to 80; the dim eases to 12 % over 3 s.
    ///   smoker…  the boarding clock starts at the first station. Each wrong pick +30 bees, a tendril to the table and a
    ///            6-bee dive (stops 40 cm from the face); the first one also plays room_strike_lookup once Beeswax is
    ///            quiet. Each fix −20 (never below 24 before the escape, so there is something to send home).
    ///   escape   the swarm turns gold and streams home through the window; the dim goes to 0 over 2 s; the rocket
    ///            reads EVACUATION CANCELLED.
    ///   t0 …     nothing of the stakes left; the display shows PASSENGER: BEESWAX · 3 · 2 · 1.
    ///   pre      everything off (reset between judges).
    /// Any jump (Board, B key, timeout) snaps to that beat's end state: no bee is left in the air, no dim is left on.
    ///
    /// It needs no edits elsewhere: it installs itself into any scene with a GameDirector (AutoInstall), hears
    /// room_hook_1 through GameNet.VoiceLineStarted, and reads the choices from GameNet's "[VBS] choice …" log line until
    /// StationBase calls <see cref="ReportChoice"/> (INTEGRATION.md has the exact hooks).
    /// Desktop: F3 toggles comfort mode.
    /// </summary>
    [DefaultExecutionOrder(-30)]
    public class StakesDirector : MonoBehaviour
    {
        /// <summary>Install into every scene that has a GameDirector (off: only scenes that add it themselves).</summary>
        public static bool AutoInstall = true;
        public static StakesDirector Instance { get; private set; }

        [SerializeField] GameDirector director;
        [SerializeField] GameNet net;
        [SerializeField] DarkSwarm swarm;
        [SerializeField] StakesRocketDisplay display;

        [Header("Swarm sizes")]
        [SerializeField] int hookCount = 150;
        [SerializeField] int meetCount = 80;
        [SerializeField] int perGrievance = 30;
        [SerializeField] int perFix = 20;
        [SerializeField] int minDuringStations = 24;
        [SerializeField] int maxCount = 150;
        [SerializeField] int divePerGrievance = 6;

        [Header("Timing")]
        [Tooltip("The line that carries the threat; the swarm pours on its words \"angry swarms\".")]
        [SerializeField] string stakesLineId = "room_hook_1";
        [SerializeField] float pourDelayIntoLine = 1.3f;
        [Tooltip("Pour anyway this long into the hook if the line never started (V14's hook says it at ~0:14).")]
        [SerializeField] float hookFallbackSeconds = 16f;
        [SerializeField] float dimHookSeconds = 1.5f;
        [SerializeField] float dimStationsSeconds = 3f;
        [SerializeField] float escapeDimSeconds = 2f;
        [Tooltip("room_strike_lookup: wait this long after the first grievance, then for Beeswax to be quiet (at most strikeMaxWait).")]
        [SerializeField] float strikeDelay = 2f;
        [SerializeField] float strikeMaxWait = 14f;

        [Header("Room")]
        [Tooltip("Ceiling (above the floor) handed to the band when the room model has none: band centre <= ~2.25 m.")]
        [SerializeField] float defaultCeilingAboveFloor = 2.55f;

        [Header("Desktop")]
        [SerializeField] KeyCode comfortKey = KeyCode.F3;

        public const string DimKey = "stakes";

        /// <summary>The swarm pours: the words "angry swarms" are now (or the fallback timer). The window's forecast cuts here.</summary>
        public static event Action Poured;

        /// <summary>Wrong picks this run (the score card's "GRIEVANCES FILED: N").</summary>
        public int Grievances { get; private set; }
        public int Fixes { get; private set; }
        public DarkSwarm Swarm => swarm;
        public StakesRocketDisplay Display => display;
        /// <summary>The meter: meet size + 30 per grievance − 20 per fix, clamped.</summary>
        public int MeterCount => Mathf.Clamp(meetCount + Grievances * perGrievance - Fixes * perFix, minDuringStations, maxCount);
        /// <summary>Ceiling height for the band (NaN = unknown). Every frame: the AR room model's ceiling when it has
        /// one, else floor + defaultCeilingAboveFloor (see <see cref="RoomCeiling"/>). Setting it overrides that.</summary>
        public float CeilingY
        {
            get => swarm != null ? swarm.CeilingY : float.NaN;
            set { _ceilingOverride = value; if (swarm != null) swarm.CeilingY = value; }
        }

        float _ceilingOverride = float.NaN, _loggedCeiling = float.NaN;

        /// <summary>The ceiling the band stays under: measured (RoomModel.CeilingY), else a safe default above the floor.</summary>
        public float RoomCeiling(out bool measured)
        {
            var rig = AR.ArRig.Instance;
            var room = rig != null ? rig.Room : null;
            measured = room != null && !float.IsNaN(room.CeilingY);
            if (measured) return room.CeilingY;
            return (room != null ? room.FloorY : 0f) + defaultCeilingAboveFloor;
        }

        void ApplyCeiling()
        {
            if (swarm == null) return;
            bool measured = false, set = !float.IsNaN(_ceilingOverride);
            float c = set ? _ceilingOverride : RoomCeiling(out measured);
            if (swarm.CeilingY != c) swarm.CeilingY = c;
            if (float.IsNaN(_loggedCeiling) || Mathf.Abs(c - _loggedCeiling) > 0.005f) // only when it changes (placement)
            {
                _loggedCeiling = c;
                Debug.Log("[VBS][Stakes] swarm ceiling y=" + c.ToString("0.00") + " (" +
                          (set ? "set by code" : measured ? "room model" : "default: no ceiling in the room model") + ")");
            }
        }

        bool _poured, _explicitChoices, _subscribed;
        float _hookEnteredAt = -1f, _pourAt = -1f, _strikeAt = -1f, _strikeDeadline = -1f;
        bool _strikeDone;

        // ------------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!AutoInstall) return;
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<StakesDirector>() != null) return;
            var go = new GameObject("Stakes");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<StakesDirector>();
            Debug.Log("[VBS][Stakes] installed (auto)");
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[VBS][Stakes] a second StakesDirector was disabled.");
                enabled = false;
                return;
            }
            Instance = this;
            if (director == null) director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (net == null) net = GameNet.Instance != null ? GameNet.Instance : FindAnyObjectByType<GameNet>();
            if (swarm == null)
            {
                var go = new GameObject("DarkSwarm");
                go.transform.SetParent(transform, false);
                swarm = go.AddComponent<DarkSwarm>();
            }
            if (display == null) display = FindAnyObjectByType<StakesRocketDisplay>();
            if (display == null)
            {
                display = gameObject.AddComponent<StakesRocketDisplay>();
                if (!display.enabled) { Destroy(display); display = null; }
            }
            if (display != null) display.Bind(director);
            PassthroughDim.ReleaseAll();
            ApplyEnvFlags();
        }

        void OnEnable()
        {
            if (Instance != this) return;
            if (director != null)
            {
                director.BeatChanged += HandleBeat;
                director.GroupResetDone += HandleReset;
            }
            if (net != null) net.VoiceLineStarted += HandleVoiceLine;
            Application.logMessageReceived += HandleLog;
            StakesFlags.ComfortChanged += HandleComfort;
            _subscribed = true;
        }

        void OnDisable()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (director != null)
            {
                director.BeatChanged -= HandleBeat;
                director.GroupResetDone -= HandleReset;
            }
            if (net != null) net.VoiceLineStarted -= HandleVoiceLine;
            Application.logMessageReceived -= HandleLog;
            StakesFlags.ComfortChanged -= HandleComfort;
        }

        void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                PassthroughDim.Release(DimKey, 0f);
            }
        }

        /// <summary>Desktop / smoke runs: VBS_STAKES_FLAGS="comfort=true,stakes.dimHook=0.3" sets flags at start.</summary>
        static void ApplyEnvFlags()
        {
            string env = null;
            try { env = Environment.GetEnvironmentVariable("VBS_STAKES_FLAGS"); } catch (Exception) { }
            if (string.IsNullOrEmpty(env)) return;
            foreach (var pair in env.Split(','))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string k = pair.Substring(0, eq).Trim(), v = pair.Substring(eq + 1).Trim();
                Debug.Log("[VBS][Stakes] flag " + k + "=" + v + (StakesFlags.TrySet(k, v) ? "" : " (ignored)"));
            }
        }

        // ------------------------------------------------------------------ hooks (INTEGRATION.md)
        /// <summary>StationBase: a pick was made (call it where choice.made is sent). Turns off the log fallback.</summary>
        public void ReportChoice(GameStation station, string choice, bool correct)
        {
            _explicitChoices = true;
            HandleChoice(correct, GameIds.StationName(station) + "/" + choice);
        }

        /// <summary>IntroSequence (V14): the words "angry swarms" are now. Pours at once instead of guessing from the line start.</summary>
        public void StakesLineNow()
        {
            if (director != null && director.Beat != GameBeat.Hook) return;
            _pourAt = Time.time;
        }

        // ------------------------------------------------------------------ events
        void HandleReset() => ResetRun("reset");

        void HandleComfort(bool on)
        {
            if (swarm == null) return;
            swarm.Comfort = on;
            if (swarm.Mode == DarkSwarmMode.Deck) swarm.Snap(director != null && director.Beat == GameBeat.Hook ? hookCount : MeterCount);
        }

        void HandleVoiceLine(GameNet.VoiceLineInfo info)
        {
            if (director == null || director.Beat != GameBeat.Hook || _poured || _pourAt >= 0f) return;
            if (info.id != stakesLineId) return;
            // V14: on the words "angry swarms", from the clip length and where they sit in the line; else the fixed delay
            float delay = HookTiming.ThreatOffset(info, pourDelayIntoLine);
            _pourAt = Time.time + delay;
            Debug.Log("[VBS][Stakes] " + stakesLineId + " started: pour in " + delay.ToString("0.00") + " s" +
                      (delay != pourDelayIntoLine ? " (on \"" + HookTiming.ThreatWords + "\", clip " + info.clipSeconds.ToString("0.00") + " s)" : " (fixed delay)"));
        }

        void HandleLog(string message, string stack, LogType type)
        {
            // GameNet.MakeChoice logs "[VBS] choice smoker/spray_water (wrong)" on every pick (button, key, prop,
            // Board choice.request, timeout). Used only until StationBase calls ReportChoice.
            if (_explicitChoices || type != LogType.Log || message == null) return;
            if (!message.StartsWith("[VBS] choice ", StringComparison.Ordinal)) return;
            bool wrong = message.EndsWith("(wrong)", StringComparison.Ordinal);
            bool right = !wrong && message.EndsWith("(correct)", StringComparison.Ordinal);
            if (!wrong && !right) return;
            int end = message.LastIndexOf(" (", StringComparison.Ordinal);
            HandleChoice(right, end > 13 ? message.Substring(13, end - 13) : "?");
        }

        void HandleChoice(bool correct, string what)
        {
            if (director == null) return;
            var beat = director.Beat;
            if (beat < GameBeat.Smoker || beat >= GameBeat.Escape) return;
            if (correct) Fixes++;
            else Grievances++;
            int n = MeterCount;
            Debug.Log("[VBS][Stakes] " + (correct ? "fix" : "grievance #" + Grievances) + " (" + what + ") -> swarm " + n);
            if (swarm == null || !StakesFlags.Swarm) return;
            swarm.SetCount(n);
            if (!correct)
            {
                swarm.Tendril();
                swarm.Dive(divePerGrievance);
                if (!_strikeDone && StakesFlags.StrikeLine)
                {
                    _strikeDone = true;
                    _strikeAt = Time.time + strikeDelay;
                    _strikeDeadline = _strikeAt + strikeMaxWait;
                }
            }
        }

        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            ApplyCeiling();
            if (beat == GameBeat.Pre) { ResetRun(prev == GameBeat.Pre ? "start" : "pre"); return; }
            bool showSwarm = StakesFlags.Swarm && swarm != null;

            if (beat == GameBeat.Hook)
            {
                _hookEnteredAt = Time.time;
                _pourAt = -1f;
                if (prev == GameBeat.Pre) { _poured = false; return; } // pours on the line (Update)
                // came back from later: the hook's end state
                _poured = true;
                if (showSwarm) swarm.Snap(hookCount);
                PassthroughDim.Request(DimKey, StakesFlags.DimHook, 0.3f);
                return;
            }

            if (beat == GameBeat.Meet)
            {
                if (_poured && showSwarm && swarm.Mode == DarkSwarmMode.Deck) swarm.SetCount(meetCount);
                else if (showSwarm) swarm.Snap(meetCount);
                PassthroughDim.Request(DimKey, StakesFlags.DimStations, _poured ? dimStationsSeconds : 0.5f);
                _poured = true;
                return;
            }

            if (beat >= GameBeat.Smoker && beat < GameBeat.Escape)
            {
                _poured = true;
                if (display != null) display.StartClock();
                bool fromStakes = prev >= GameBeat.Hook && prev < GameBeat.Escape;
                if (showSwarm && (!fromStakes || swarm.Mode != DarkSwarmMode.Deck)) swarm.Snap(MeterCount);
                else if (showSwarm && prev < GameBeat.Smoker) swarm.SetCount(MeterCount);
                if (!fromStakes || PassthroughDim.Target(DimKey) != StakesFlags.DimStations)
                    PassthroughDim.Request(DimKey, StakesFlags.DimStations, fromStakes ? dimStationsSeconds : 0.5f);
                return;
            }

            if (display != null) display.StopClock();
            _strikeAt = -1f;
            if (beat == GameBeat.Escape)
            {
                if (swarm != null)
                {
                    if (swarm.Mode == DarkSwarmMode.Deck) swarm.Home();
                    else if (prev != GameBeat.Escape) swarm.Clear();
                }
                PassthroughDim.Release(DimKey, prev >= GameBeat.Smoker && prev < GameBeat.Escape ? escapeDimSeconds : 0f);
                Debug.Log("[VBS][Stakes] escape: grievances " + Grievances + ", fixes " + Fixes +
                          (swarm != null ? " | swarm safety this run: closest to face " + DarkSwarm.Fmt(swarm.MinFaceDistance) +
                                           " m, min in front " + DarkSwarm.Fmt(swarm.MinFront) + " m, overhead pass clearance " +
                                           DarkSwarm.Fmt(swarm.MinOverheadClearance) + " m" : ""));
                return;
            }

            // t0, map, field: nothing of the stakes is left
            if (swarm != null && swarm.Mode != DarkSwarmMode.Hidden && !(prev == GameBeat.Escape && swarm.Mode == DarkSwarmMode.Home))
                swarm.Clear();
            if (prev != GameBeat.Escape) PassthroughDim.Release(DimKey, 0f);
        }

        void ResetRun(string why)
        {
            Grievances = Fixes = 0;
            _poured = false;
            _pourAt = _hookEnteredAt = _strikeAt = -1f;
            _strikeDone = false;
            if (swarm != null) swarm.Clear();
            PassthroughDim.Release(DimKey, 0f);
            if (display != null) display.ResetDisplay();
            Debug.Log("[VBS][Stakes] " + why + ": swarm off, dim 0, rocket EARTH -> MOON 4:00");
        }

        void Pour()
        {
            _poured = true;
            _pourAt = -1f;
            if (StakesFlags.Swarm && swarm != null) swarm.PourOut(hookCount);
            PassthroughDim.Request(DimKey, StakesFlags.DimHook, dimHookSeconds);
            try { Poured?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
        }

        // ------------------------------------------------------------------ frame
        void Update()
        {
            if (GameInput.KeyDown(comfortKey)) StakesFlags.Comfort = !StakesFlags.Comfort;
            if (swarm == null || director == null) return;
            ApplyCeiling(); // before any pour / snap this frame
            if (swarm.Comfort != StakesFlags.Comfort) swarm.Comfort = StakesFlags.Comfort;
            if (!StakesFlags.Swarm && swarm.Mode != DarkSwarmMode.Hidden) swarm.Clear();

            float now = Time.time;
            var beat = director.Beat;
            if (beat == GameBeat.Hook && !_poured)
            {
                if (_pourAt < 0f && _hookEnteredAt >= 0f && now - _hookEnteredAt >= hookFallbackSeconds)
                {
                    Debug.Log("[VBS][Stakes] " + stakesLineId + " not heard: pouring on the fallback timer");
                    _pourAt = now;
                }
                if (_pourAt >= 0f && now >= _pourAt) Pour();
            }

            if (_strikeAt >= 0f && now >= _strikeAt)
            {
                bool inStation = beat >= GameBeat.Smoker && beat < GameBeat.Escape;
                bool quiet = net == null || !net.IsSpeaking;
                if (!inStation || now > _strikeDeadline) _strikeAt = -1f;
                else if (quiet)
                {
                    _strikeAt = -1f;
                    if (net != null) net.Say(GameLines.RoomStrikeLookup);
                    Debug.Log("[VBS][Stakes] first grievance: room_strike_lookup");
                }
            }
        }
    }
}
