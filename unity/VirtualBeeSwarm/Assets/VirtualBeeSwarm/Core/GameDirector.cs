using System;
using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// THE beat clock (Unity is the only one). Enters/exits BeatBehaviours, reports beat.set on every change,
    /// accepts beat.request from the Hive/Board, auto-advances when a beat runs past its max duration (no network
    /// needed), skips the hornet unless the run is ahead of schedule, and times the run for the score card.
    /// Desktop keys: N / Space next, B previous, Shift+R reset to pre, K cycle hornet mode (auto/always/never).
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public class GameDirector : MonoBehaviour
    {
        public enum AutoAdvance { Always, OnlyWhenOffline, Never }
        public enum HornetMode { Auto, Always, Never }

        public static GameDirector Instance { get; private set; }

        [SerializeField] GameNet net;
        [Tooltip("Filled by the scene builder; found at runtime if empty.")]
        [SerializeField] BeatBehaviour[] behaviours = new BeatBehaviour[0];

        [Header("Clock")]
        [Tooltip("Max seconds per beat (index = beat number). 0 = no limit. Then the beat's OnBeatTimeout runs and the " +
                 "director advances at most timeoutGraceSeconds later.")]
        [SerializeField] float[] beatCapSeconds = { 0f, 25f, 48f, 55f, 85f, 50f, 40f, 50f, 60f, 25f, 45f, 0f, 0f };
        // ride script v2 (typical run, then the cap): hook 8 s (25), meet 32 s (48: six lines, one of them the forecast),
        // smoker 36 s (55: hint 8 s + no-pick timeout 20 s), feed 43 s (85: lift timeout 18 s, the honey mistake is 11 s), wasps 23 s +10 per wrong
        // pick (50), heat 18 s +9 per wrong pick (50), what-if 33 s (60: hint, put-out hint, 25 s silence), escape 14 s (25),
        // T-0 24 s (45), map/unload 27 s (no cap: the ride ends there).
        [SerializeField] AutoAdvance autoAdvance = AutoAdvance.Always;
        [Tooltip("After a beat's max: how long its wrap-up may take (a station's own \"Noted for the minutes\" + outcome line " +
                 "is ~5-14 s) before the director moves on anyway. A wrap-up that calls Complete() advances sooner.")]
        [SerializeField] float timeoutGraceSeconds = 15f;
        [Tooltip("The stakes: fix the hive in this long (the rocket's countdown).")]
        [SerializeField] float timeLimitSeconds = 240f;

        [Header("Hornet (only if ahead)")]
        [SerializeField] HornetMode hornetMode = HornetMode.Never; // v3.1: the hornet is cut (K cycles it on the desktop)
        [Tooltip("Auto: play the hornet when the run clock is below this when the wasps station ends (script: wasps end 2:20, hornet costs ~30 s).")]
        [SerializeField] float hornetIfRunSecondsBelow = 125f;

        [Header("Display (optional)")]
        [Tooltip("Countdown on HONEYMOON-1 (T-m:ss).")]
        [SerializeField] TextMesh countdownText;

        [Header("Desktop keys")]
        [SerializeField] KeyCode nextKey = KeyCode.N;
        [SerializeField] KeyCode nextKeyAlt = KeyCode.Space;
        [SerializeField] KeyCode previousKey = KeyCode.B;
        [SerializeField] KeyCode resetKey = KeyCode.R; // with Shift
        [SerializeField] KeyCode hornetModeKey = KeyCode.K;

        /// <summary>(previous, current)</summary>
        public event Action<GameBeat, GameBeat> BeatChanged;
        public event Action GroupResetDone;

        public GameBeat Beat { get; private set; } = GameBeat.Pre;
        public float BeatSeconds => Time.time - _beatStartedAt;
        /// <summary>Seconds since the first station started (v3.1: the clock starts at smoker, not in the intro; v2: frozen
        /// when hexagon 4 lights, so the talk after the last station costs no score; at escape at the latest).</summary>
        public float RunSeconds => _runStartedAt < 0f ? 0f : (_runEndedAt >= 0f ? _runEndedAt : Time.time) - _runStartedAt;
        public bool RunFinished => _runEndedAt >= 0f;
        public float TimeLimitSeconds => timeLimitSeconds;
        public HornetMode Hornet { get => hornetMode; set => hornetMode = value; }
        public int SolvedStations => _solvedStationBeats.Count;
        public bool PlayedHornet { get; private set; }
        /// <summary>Seconds since the run started (the signature / START), -1 in pre. The ride script's clock.</summary>
        public float ShowSeconds => Beat == GameBeat.Pre || _showStartedAt < 0f ? -1f : Time.time - _showStartedAt;

        float _beatStartedAt, _runStartedAt = -1f, _runEndedAt = -1f, _showStartedAt = -1f; // show = since START (for timing logs)
        readonly HashSet<GameBeat> _solvedStationBeats = new HashSet<GameBeat>();
        float _advanceAt = -1f;
        GameBeat _advanceFrom;
        bool _timedOut;
        float _forceAdvanceAt = -1f;
        int _lastCountdownSecond = int.MinValue;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[VBS] a second GameDirector was disabled.");
                enabled = false;
                return;
            }
            Instance = this;
            if (net == null) net = FindAnyObjectByType<GameNet>();
            if (behaviours == null || behaviours.Length == 0)
                behaviours = FindObjectsByType<BeatBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        void OnEnable()
        {
            if (net == null) return;
            net.BeatRequested += HandleBeatRequest;
            net.GroupReset += ResetToPre;
        }

        void OnDisable()
        {
            if (net == null) return;
            net.BeatRequested -= HandleBeatRequest;
            net.GroupReset -= ResetToPre;
        }

        void Start()
        {
            _beatStartedAt = Time.time;
            EnterBeat(GameBeat.Pre);
            if (net != null) net.SendBeat(GameBeat.Pre);
            BeatChanged?.Invoke(GameBeat.Pre, GameBeat.Pre);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            HandleKeys();

            // Completed beats advance after their delay.
            if (_advanceAt >= 0f && Time.time >= _advanceAt)
            {
                _advanceAt = -1f;
                if (Beat == _advanceFrom) Next("complete");
            }

            // Max durations: let the beat wrap up, then move on regardless.
            float max = (int)Beat < beatCapSeconds.Length ? beatCapSeconds[(int)Beat] : 0f;
            if (max > 0f && AutoAdvanceAllowed())
            {
                if (!_timedOut && BeatSeconds >= max)
                {
                    _timedOut = true;
                    _forceAdvanceAt = Time.time + timeoutGraceSeconds;
                    Debug.Log("[VBS] beat " + GameIds.BeatName(Beat) + " hit its max (" + max + " s)");
                    for (int i = 0; i < behaviours.Length; i++)
                        if (behaviours[i] != null && behaviours[i].IsActive) behaviours[i].Timeout();
                }
                if (_timedOut && _forceAdvanceAt >= 0f && Time.time >= _forceAdvanceAt && _advanceAt < 0f)
                {
                    _forceAdvanceAt = -1f;
                    Next("timeout");
                }
            }

            UpdateCountdown();
        }

        bool AutoAdvanceAllowed()
        {
            switch (autoAdvance)
            {
                case AutoAdvance.Always: return true;
                case AutoAdvance.OnlyWhenOffline: return net == null || !net.Online;
                default: return false;
            }
        }

        void HandleKeys()
        {
            if (GameInput.KeyDown(nextKey) || GameInput.KeyDown(nextKeyAlt)) Next("key");
            if (GameInput.KeyDown(previousKey)) Previous();
            if (GameInput.KeyDown(resetKey) && GameInput.Shift) ResetToPre();
            if (GameInput.KeyDown(hornetModeKey))
            {
                hornetMode = (HornetMode)(((int)hornetMode + 1) % 3);
                Debug.Log("[VBS] hornet mode = " + hornetMode);
            }
        }

        // ---------------------------------------------------------------- beat control
        /// <summary>The beat after the current one (hornet only if ahead / forced; map -> field; field stays).</summary>
        public GameBeat NextBeat(GameBeat from)
        {
            switch (from)
            {
                case GameBeat.Wasps: return ShouldPlayHornet() ? GameBeat.Hornet : GameBeat.Heat;
                case GameBeat.Field: return GameBeat.Field;
                default: return (GameBeat)Mathf.Min((int)from + 1, GameIds.BeatCount - 1);
            }
        }

        public bool ShouldPlayHornet()
        {
            switch (hornetMode)
            {
                case HornetMode.Always: return true;
                case HornetMode.Never: return false;
                default: return RunSeconds < hornetIfRunSecondsBelow;
            }
        }

        public void Next(string reason = "")
        {
            var to = NextBeat(Beat);
            if (to == Beat) return;
            GoTo(to, reason);
        }

        public void Previous()
        {
            if (Beat == GameBeat.Pre) return;
            var to = (GameBeat)((int)Beat - 1);
            if (to == GameBeat.Hornet && !PlayedHornet) to = GameBeat.Wasps;
            GoTo(to, "previous");
        }

        public void GoTo(GameBeat beat, string reason = "")
        {
            if (beat == Beat) return;
            if (beat == GameBeat.Pre)
            {
                ResetToPre();
                return;
            }
            var prev = Beat;
            for (int i = 0; i < behaviours.Length; i++)
                if (behaviours[i] != null && behaviours[i].IsActive) behaviours[i].Exit(prev);

            Beat = beat;
            _beatStartedAt = Time.time;
            _advanceAt = -1f;
            _timedOut = false;
            _forceAdvanceAt = -1f;

            if (_runStartedAt < 0f && beat >= GameBeat.Smoker) _runStartedAt = Time.time; // v3.1: the clock starts at the first station (or wherever we jumped in past it)
            if (beat < GameBeat.Smoker) { _runStartedAt = -1f; _runEndedAt = -1f; } // back in the intro: the clock waits for smoker again
            if (beat <= GameBeat.Heat) _runEndedAt = -1f; // back in the stations: the clock runs again
            if (beat == GameBeat.Escape && _runEndedAt < 0f) _runEndedAt = Time.time;
            if (beat == GameBeat.Hornet) PlayedHornet = true;
            if (prev == GameBeat.Pre || _showStartedAt < 0f) _showStartedAt = Time.time;

            Debug.Log("[VBS] " + GameIds.BeatName(prev) + " -> " + GameIds.BeatName(beat) +
                      (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")") + " run " + GameMaterials.Clock(RunSeconds) +
                      " show " + GameMaterials.Clock(Time.time - _showStartedAt));
            if (net != null) net.SendBeat(beat);
            EnterBeat(beat);
            BeatChanged?.Invoke(prev, beat);
        }

        void EnterBeat(GameBeat beat)
        {
            for (int i = 0; i < behaviours.Length; i++)
            {
                var b = behaviours[i];
                if (b != null && b.isActiveAndEnabled && b.Handles(beat))
                {
                    try { b.Enter(beat); }
                    catch (Exception e) { Debug.LogException(e, b); }
                }
            }
        }

        /// <summary>A behaviour finished its beat. Advance after `delay` (only if we're still in that beat).</summary>
        public void NotifyComplete(GameBeat beat, float delay)
        {
            if (beat != Beat) return;
            float at = Time.time + Mathf.Max(0f, delay);
            if (_advanceAt < 0f || at < _advanceAt)
            {
                _advanceAt = at;
                _advanceFrom = beat;
            }
        }

        public void NotifyStationSolved()
        {
            if (Beat < GameBeat.Smoker || Beat > GameBeat.Heat || Beat == GameBeat.Hornet) return;
            _solvedStationBeats.Add(Beat);
            // Replay and jumps can change station order. Stop timing once all four different stations are solved.
            if (SolvedStations == GameIds.Stations.Length && _runStartedAt >= 0f && _runEndedAt < 0f)
            {
                _runEndedAt = Time.time;
                Debug.Log("[VBS] score clock stopped at " + GameMaterials.Clock(RunSeconds) + " (all four stations solved)");
            }
        }

        /// <summary>group.reset or Shift+R: everything back to pre.</summary>
        public void ResetToPre()
        {
            var prev = Beat;
            for (int i = 0; i < behaviours.Length; i++)
                if (behaviours[i] != null) behaviours[i].ResetForGroup();
            Beat = GameBeat.Pre;
            _beatStartedAt = Time.time;
            _advanceAt = -1f;
            _timedOut = false;
            _forceAdvanceAt = -1f;
            _runStartedAt = -1f;
            _runEndedAt = -1f;
            _solvedStationBeats.Clear();
            PlayedHornet = false;
            if (net != null)
            {
                net.StopVoice();
                net.ResetHum();
                net.CalibrateHum();
                net.SendBeat(GameBeat.Pre);
            }
            Debug.Log("[VBS] reset to pre");
            EnterBeat(GameBeat.Pre);
            GroupResetDone?.Invoke();
            BeatChanged?.Invoke(prev, GameBeat.Pre);
        }

        void HandleBeatRequest(int n, bool next)
        {
            if (n < 0 || next)
            {
                Next("beat.request next");
                return;
            }
            if (!GameIds.TryParseBeat(n, out var beat))
            {
                Debug.LogWarning("[VBS] ignored beat.request for unknown beat " + n);
                return;
            }
            if (beat == GameBeat.Pre) ResetToPre();
            else GoTo(beat, "beat.request");
        }

        void UpdateCountdown()
        {
            if (countdownText == null) return;
            int remaining = Mathf.CeilToInt(timeLimitSeconds - RunSeconds);
            if (Beat == GameBeat.Pre) remaining = Mathf.CeilToInt(timeLimitSeconds);
            if (RunFinished) remaining = int.MaxValue; // saved
            if (remaining == _lastCountdownSecond) return;
            _lastCountdownSecond = remaining;
            if (remaining == int.MaxValue) countdownText.text = "SAVED";
            else countdownText.text = remaining > 0 ? "T-" + GameMaterials.Clock(remaining) : "T-0:00";
        }
    }
}
