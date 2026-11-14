using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using VirtualBeeSwarm.Stakes;

namespace VirtualBeeSwarm.Sound
{
    /// <summary>
    /// Plays Satie's soundscape (SatieScene.Voices, takes in Resources/SatieTakes) on the game's own events, states and
    /// signals. Installed by itself after the scene loads (like StakesDirector); nothing to wire in the builder.
    ///   * Events: GameSound.Cue from gameplay (stamps, touchdowns, crews, station outcomes, swarm moves), the director's
    ///     beats, GameNet window scenes, the clipboard, the hive lift, StationDelivery.BuzzCue, and state the game already
    ///     shows (window open, swarm pour-out, dark swarm home, hexagons, the rocket's clock / display / countdown).
    ///   * States: the beat ("hornet" counts as heat, "field" as map; "moon" while the Moon fills the window in t0) gate
    ///     the voices' zones. Loops also need their object: no hive, no hum; closed window, no meadow.
    ///   * Signals: beeswax_talking, hive_agitation, hive_sick, hive_heat, dark_swarm_count, room_dim, boarding_left, fire.
    ///   * Mix: gain = 10^((spl - levelReferenceDb)/20) x follows x duck x mic x sound.volume, Unity 3D (Meta XR spatializer
    ///     when set), inverse distance beyond 1 m. Everything ducks to 0.22 under Beeswax (the script's group) and goes
    ///     silent within 20 ms while the mic listens or the talk input is held (push-to-talk online or offline / hum).
    ///     12 one-shot voices: stamps, touchdowns, the dark swarm drone and the hive hum are never dropped; meadow air,
    ///     birds and crew buzz go first. Outside the dark swarm's own moments its drone stays 5 dB under the hive hum.
    ///   * tuning.json: sound.on (default true), sound.volume (0..2, default 1). Env VBS_SOUND=off|on, VBS_SOUND_VOLUME win.
    /// Every trigger is logged "[VBS][Sound] t=&lt;Time.time&gt; &lt;event&gt; -> &lt;voice&gt;: ..." (played or why not), so a
    /// -nosound smoke player shows the whole sound track. Credit: "Sound by Satie" on the level map (Satie's licence).
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class SoundDirector : MonoBehaviour
    {
        public static bool AutoInstall = true;
        public static SoundDirector Instance { get; private set; }
        public const string TakesFolder = "SatieTakes";

        [Header("Mix")]
        [Tooltip("A source received at this level (dB SPL) plays as loud as Beeswax's lines (the takes are baked to their loudness).")]
        [SerializeField] float levelReferenceDb = 66f;
        [SerializeField] int oneShotVoices = 12;
        [SerializeField] float zoneFadeSeconds = 0.8f;
        [SerializeField] float duckAttackSeconds = 0.08f;
        [SerializeField] float duckReleaseSeconds = 0.5f;
        [Tooltip("Mute ramp when the mic opens: short, so the mic hears no game sound (only a de-click).")]
        [SerializeField] float micFadeSeconds = 0.02f;
        [SerializeField] float signalSmoothSeconds = 0.1f;
        [SerializeField] float maxDistance = 30f;
        [Tooltip("Outside the dark swarm's own moments (the hook's pour, a few seconds after each tendril/dive) its drone stays this far under the hive hum.")]
        [SerializeField] float droneUnderHumDb = 5f;
        [SerializeField] float darkMomentSeconds = 4f;

        [Header("Log / credit")]
        [SerializeField] bool logTriggers = true;
        [SerializeField] bool logDuck = true;
        [SerializeField] bool credit = true;

        public bool SoundOn { get; private set; } = true;
        public float Volume { get; private set; } = 1f;
        public string State { get; private set; } = "pre";
        public bool ClipsReady { get; private set; }
        public int Played { get; private set; }
        public int Skipped { get; private set; }

        sealed class Loop
        {
            public SoundVoice v;
            public AudioSource src;
            public float gate;
            public bool on;
        }

        sealed class Shot
        {
            public AudioSource src;
            public SoundVoice v;
            public Transform follow;
            public float started = -1f, endAt = -1f;
        }

        struct Delayed
        {
            public float at;
            public string evt;
            public Transform t;
            public Vector3 p;
            public bool hasPos;
        }

        readonly Dictionary<string, List<SoundVoice>> _byEvent = new Dictionary<string, List<SoundVoice>>();
        readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        readonly Dictionary<SoundVoice, float> _lastFire = new Dictionary<SoundVoice, float>();
        readonly Dictionary<SoundVoice, int> _lastClip = new Dictionary<SoundVoice, int>();
        readonly Dictionary<string, float> _signals = new Dictionary<string, float>();
        readonly Dictionary<string, Transform> _named = new Dictionary<string, Transform>();
        readonly Dictionary<string, float> _namedMiss = new Dictionary<string, float>();
        readonly Dictionary<string, int> _played = new Dictionary<string, int>(), _skipped = new Dictionary<string, int>();
        readonly List<Loop> _loops = new List<Loop>();
        readonly List<Delayed> _delayed = new List<Delayed>();
        Shot[] _shots = new Shot[0];

        GameDirector _director;
        GameNet _net;
        HiveBody _hive;
        SwarmSystem _swarm;
        FutureWindow _window;
        EmbersFx _embers;
        HexagonPanel _hex;
        TextMesh _countdown;
        Transform[] _wasps = new Transform[0];
        readonly List<SignHereClipboard> _clipboards = new List<SignHereClipboard>();
        bool _subscribed, _started;

        float _talk, _mic = 1f, _darkMomentUntil = -1f;
        bool _talkWas, _micWas, _wasOpen, _wasPourOut, _wasHome, _wasClock, _wasCancelled, _droneHeld;
        Loop _humLoop, _droneLoop;
        int _lastLit;
        string _lastCountdown, _lastScene = "";

        // ------------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!AutoInstall) return;
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<SoundDirector>() != null) return;
            var go = new GameObject("Sound");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<SoundDirector>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[VBS][Sound] a second SoundDirector was disabled.");
                enabled = false;
                return;
            }
            Instance = this;
            ReadTuning();
            foreach (var v in SatieScene.Voices)
            {
                if (v.loop || string.IsNullOrEmpty(v.evt)) continue;
                if (!_byEvent.TryGetValue(v.evt, out var list)) _byEvent[v.evt] = list = new List<SoundVoice>();
                list.Add(v);
            }
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            _net = GameNet.Instance != null ? GameNet.Instance : FindAnyObjectByType<GameNet>();
            if (!SoundOn)
            {
                Debug.Log("[VBS][Sound] sound.on = false: no soundscape this run");
                enabled = false;
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void OnEnable()
        {
            if (Instance != this || !SoundOn) return;
            GameSound.Sink = HandleCue;
            if (_subscribed) return;
            _subscribed = true;
            if (_director != null)
            {
                _director.BeatChanged += HandleBeat;
                _director.GroupResetDone += HandleReset;
            }
            if (_net != null) _net.SceneChanged += HandleScene;
            StationDelivery.BuzzCue += HandleBuzz;
            if (_started) Subscribe2();
        }

        void OnDisable()
        {
            if (GameSound.Sink == (Action<string, Transform, Vector3, bool, float>)HandleCue) GameSound.Sink = null;
            if (_subscribed)
            {
                _subscribed = false;
                if (_director != null)
                {
                    _director.BeatChanged -= HandleBeat;
                    _director.GroupResetDone -= HandleReset;
                }
                if (_net != null) _net.SceneChanged -= HandleScene;
                StationDelivery.BuzzCue -= HandleBuzz;
                if (_hive != null) _hive.Lifted -= HandleLifted;
                foreach (var c in _clipboards) if (c != null) c.Signed -= HandleSigned;
            }
            StopAll();
        }

        void Start()
        {
            _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            _swarm = FindAnyObjectByType<SwarmSystem>(FindObjectsInactive.Include);
            _window = FindAnyObjectByType<FutureWindow>(FindObjectsInactive.Include);
            _embers = FindAnyObjectByType<EmbersFx>(FindObjectsInactive.Include);
            _hex = FindAnyObjectByType<HexagonPanel>(FindObjectsInactive.Include);
            foreach (var tm in FindObjectsByType<TextMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (tm.name == "CountdownDisplay") { _countdown = tm; break; }
            var wasps = new List<Transform>();
            for (int i = 0; i < 8; i++) { var w = Named("Wasp" + i); if (w != null) wasps.Add(w); }
            _wasps = wasps.ToArray();
            _clipboards.AddRange(FindObjectsByType<SignHereClipboard>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            _started = true;
            if (_subscribed) Subscribe2();

            // the voices: one source per loop, a pool for the rest
            foreach (var v in SatieScene.Voices)
            {
                if (!v.loop || v.muted) continue;
                var l = new Loop { v = v, src = NewSource("[VBS] Loop " + v.id) };
                l.src.loop = true;
                l.src.minDistance = Mathf.Max(1f, v.radius);
                l.src.priority = v.priority == 0 ? 4 : v.priority == 2 ? 200 : 96;
                _loops.Add(l);
                if (v.id == "hive_hum") _humLoop = l;
                else if (v.id == "dark_swarm_drone") _droneLoop = l;
            }
            _shots = new Shot[Mathf.Max(1, oneShotVoices)];
            for (int i = 0; i < _shots.Length; i++) _shots[i] = new Shot { src = NewSource("[VBS] Sfx " + i) };

            // nothing fires for the state the scene starts in
            _wasOpen = _window != null && _window.IsOpen;
            _wasPourOut = _swarm != null && _swarm.Mode == SwarmMode.PourOut;
            _lastLit = _hex != null ? _hex.LitCount : 0;
            _lastCountdown = _countdown != null ? _countdown.text : null;
            _lastScene = _net != null ? _net.CurrentScene : "";
            UpdateState();
            if (credit) AddCredit();
            StartCoroutine(LoadClips());
            Log("on: " + SatieScene.Voices.Length + " voices (" + _loops.Count + " loops, " + _byEvent.Count + " events), volume " +
                Volume.ToString("0.00", CultureInfo.InvariantCulture) + ", reference " + levelReferenceDb.ToString("0", CultureInfo.InvariantCulture) +
                " dB, spatializer " + (string.IsNullOrEmpty(AudioSettings.GetSpatializerPluginName()) ? "none" : AudioSettings.GetSpatializerPluginName()) +
                (_hive == null ? ", no hive" : "") + (_window == null ? ", no window" : "") + (_countdown == null ? ", no CountdownDisplay" : ""));
        }

        void Subscribe2()
        {
            if (_hive != null) { _hive.Lifted -= HandleLifted; _hive.Lifted += HandleLifted; }
            foreach (var c in _clipboards) if (c != null) { c.Signed -= HandleSigned; c.Signed += HandleSigned; }
        }

        AudioSource NewSource(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.dopplerLevel = 0f;
            s.rolloffMode = AudioRolloffMode.Logarithmic; // free_field_v1: 1/r beyond minDistance (1 m)
            s.minDistance = 1f;
            s.maxDistance = maxDistance;
            if (!string.IsNullOrEmpty(AudioSettings.GetSpatializerPluginName())) s.spatialize = true;
            return s;
        }

        IEnumerator LoadClips()
        {
            float t0 = Time.realtimeSinceStartup;
            var names = new List<string>();
            foreach (var v in SatieScene.Voices)
                foreach (var c in v.clips)
                    if (!names.Contains(c)) names.Add(c);
            var reqs = new ResourceRequest[names.Count];
            for (int i = 0; i < names.Count; i++) reqs[i] = Resources.LoadAsync<AudioClip>(TakesFolder + "/" + names[i]);
            var missing = new List<string>();
            for (int i = 0; i < names.Count; i++)
            {
                while (!reqs[i].isDone) yield return null;
                var clip = reqs[i].asset as AudioClip;
                if (clip == null) { missing.Add(names[i]); continue; }
                _clips[names[i]] = clip;
            }
            ClipsReady = true;
            Log("takes: " + _clips.Count + "/" + names.Count + " from Resources/" + TakesFolder + " in " +
                ((Time.realtimeSinceStartup - t0) * 1000f).ToString("0", CultureInfo.InvariantCulture) + " ms" +
                (missing.Count > 0 ? "; MISSING: " + string.Join(", ", missing) : ""));
        }

        // ------------------------------------------------------------------ tuning
        void ReadTuning()
        {
            var flat = new Dictionary<string, string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            ReadFile(Path.Combine(Application.streamingAssetsPath, "tuning.json"), flat);
#endif
            ReadFile(Path.Combine(Application.persistentDataPath, "tuning.json"), flat);
            if (flat.TryGetValue("sound.on", out var on)) SoundOn = ParseBool(on, SoundOn);
            if (flat.TryGetValue("sound.volume", out var vol) && TryFloat(vol, out var f)) Volume = Mathf.Clamp(f, 0f, 2f);
            try
            {
                var env = Environment.GetEnvironmentVariable("VBS_SOUND");
                if (!string.IsNullOrEmpty(env)) SoundOn = ParseBool(env, SoundOn);
                var envVol = Environment.GetEnvironmentVariable("VBS_SOUND_VOLUME");
                if (!string.IsNullOrEmpty(envVol) && TryFloat(envVol, out var ev)) Volume = Mathf.Clamp(ev, 0f, 2f);
            }
            catch (Exception) { }
        }

        static void ReadFile(string path, Dictionary<string, string> into)
        {
            try
            {
                if (File.Exists(path)) AR.ArTuning.FlatJson.Parse(File.ReadAllText(path), into);
            }
            catch (Exception e) { Debug.LogWarning("[VBS][Sound] tuning: could not read " + path + ": " + e.Message); }
        }

        static bool ParseBool(string s, bool fallback)
        {
            s = s.Trim().Trim('"').ToLowerInvariant();
            if (s == "true" || s == "on" || s == "1" || s == "yes") return true;
            if (s == "false" || s == "off" || s == "0" || s == "no") return false;
            return fallback;
        }

        static bool TryFloat(string s, out float f) =>
            float.TryParse(s.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out f);

        // ------------------------------------------------------------------ game events
        void HandleCue(string evt, Transform t, Vector3 p, bool hasPos, float delay)
        {
            if (delay > 0f)
            {
                _delayed.Add(new Delayed { at = Time.time + delay, evt = evt, t = t, p = p, hasPos = hasPos || t != null });
                return;
            }
            Fire(evt, t, p, hasPos);
        }

        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            _delayed.Clear(); // a station's pending outcome sounds belong to the beat that just ended
            UpdateState();
            Fire("beat_" + GameIds.BeatName(beat), null, Vector3.zero, false); // the script keeps beats silent: logged only
            if (beat == GameBeat.Escape && prev != GameBeat.Escape) Fire("hexagon_light", HiveT, Vector3.zero, false); // all four flash
            if (beat == GameBeat.Map && prev != GameBeat.Map) Fire("map_reveal", Named("LevelMap"), Vector3.zero, false);
        }

        void HandleReset()
        {
            _delayed.Clear();
            for (int i = 0; i < _shots.Length; i++) { if (_shots[i].src != null) _shots[i].src.Stop(); _shots[i].endAt = -1f; }
            _lastFire.Clear();
            Log("group reset: one-shots stopped (" + Summary() + ")");
        }

        void HandleScene(string scene)
        {
            string prev = _lastScene;
            _lastScene = scene ?? "";
            if (_director != null && _director.Beat == GameBeat.T0 && prev == GameIds.Scene.Moon && _lastScene != GameIds.Scene.Moon)
            {
                var spot = Named("CrashSpot");
                Fire("crash", spot != null ? spot : Named("HONEYMOON-1"), Vector3.zero, false); // the rocket is back on the table
            }
            Fire("window_scene", WindowT, Vector3.zero, false);
        }

        void HandleSigned(SignHereClipboard clip, string how) => Fire("start_signed", clip != null ? clip.transform : null, Vector3.zero, false);

        void HandleLifted(float height) => Fire("hive_tip", HiveT, Vector3.zero, false);

        void HandleBuzz(Transform item, float pitch, string why)
        {
            if (why == "heavy") Fire("crew_heavy", item, Vector3.zero, false);
            else Fire("crew_depart", item, Vector3.zero, false, pitch); // the V19 hint: the right pick's crew buzzes eagerly
        }

        // ------------------------------------------------------------------ frame
        void Update()
        {
            float dt = Time.deltaTime;
            UpdateSignals(dt);
            UpdateState();
            Poll();
            for (int i = 0; i < _delayed.Count; i++)
            {
                var d = _delayed[i];
                if (Time.time < d.at) continue;
                _delayed.RemoveAt(i--);
                if (d.t != null) Fire(d.evt, d.t, d.t.position, false);
                else Fire(d.evt, null, d.p, d.hasPos); // its object went away: where it was
            }
            UpdateLoops(dt);
            UpdateShots();
        }

        void UpdateSignals(float dt)
        {
            bool talking = _net != null && _net.IsSpeaking;
            _talk = Mathf.MoveTowards(_talk, talking ? 1f : 0f, dt / Mathf.Max(0.01f, talking ? duckAttackSeconds : duckReleaseSeconds));
            bool mic = MicNow();
            _mic = Mathf.MoveTowards(_mic, mic ? 0f : 1f, dt / Mathf.Max(0.01f, micFadeSeconds));
            if (mic != _micWas) { _micWas = mic; Log("mic " + (mic ? "listening: everything silent" : "closed: sound back")); }
            if (talking != _talkWas) { _talkWas = talking; if (logDuck) Log("beeswax " + (talking ? "talking: duck to 0.22" : "quiet: duck released")); }

            _signals["beeswax_talking"] = _talk;
            Smooth("hive_agitation", _swarm != null ? _swarm.Agitation : 0f, dt, signalSmoothSeconds);
            Smooth("hive_sick", _hive != null ? _hive.Sick : 0f, dt, signalSmoothSeconds);
            Smooth("hive_heat", _hive != null ? _hive.HeatGlow : 0f, dt, signalSmoothSeconds);
            var ds = Dark;
            Smooth("dark_swarm_count", ds != null && ds.Mode != DarkSwarmMode.Hidden ? ds.Active : 0f, dt, 0.3f);
            Smooth("room_dim", PassthroughDim.Applied, dt, signalSmoothSeconds);
            float limit = _director != null ? _director.TimeLimitSeconds : 240f;
            _signals["boarding_left"] = _director != null && _director.RunSeconds > 0f ? Mathf.Max(0f, limit - _director.RunSeconds) : limit;
            bool fire = (_net != null && _net.CurrentScene == GameIds.Scene.WhatIfFire) || (_embers != null && _embers.Emitting);
            Smooth("fire", fire ? 1f : 0f, dt, 0.5f);
        }

        void Smooth(string name, float target, float dt, float tau)
        {
            if (!_signals.TryGetValue(name, out var cur)) cur = target;
            float k = tau <= 0f ? 1f : 1f - Mathf.Exp(-dt / tau);
            _signals[name] = cur + (target - cur) * k;
        }

        float Sig(string name) => _signals.TryGetValue(name, out var v) ? v : 0f;

        /// <summary>Live, not last frame's: a cue fired by gameplay in the very frame the mic opens is skipped too.</summary>
        bool MicNow() => (_net != null && _net.MicListening) || WhatIfBeat.TalkHeld;

        void UpdateState()
        {
            string s = BeatState();
            if (s == State) return;
            State = s;
            Log("state " + s);
        }

        string BeatState()
        {
            if (_director == null) return "pre";
            switch (_director.Beat)
            {
                case GameBeat.Hornet: return "heat";  // cut beat between wasps and heat: the hive's beds carry on
                case GameBeat.Field: return "map";
                case GameBeat.T0: return _net != null && _net.CurrentScene == GameIds.Scene.Moon ? "moon" : "t0";
                default: return GameIds.BeatName(_director.Beat);
            }
        }

        void Poll()
        {
            if (_window != null)
            {
                bool open = _window.IsOpen;
                if (open && !_wasOpen) Fire("window_open", WindowT, Vector3.zero, false);
                _wasOpen = open;
            }
            if (_swarm != null)
            {
                bool pour = _swarm.Mode == SwarmMode.PourOut;
                if (pour && !_wasPourOut) Fire("swarm_pourout", _hive != null ? _hive.DoorPoint : _swarm.transform, Vector3.zero, false);
                _wasPourOut = pour;
            }
            var ds = Dark;
            bool home = ds != null && ds.Mode == DarkSwarmMode.Home;
            if (home && !_wasHome) Fire("dark_home", null, ds.BandCenter, true);
            _wasHome = home;
            if (_hex != null)
            {
                int lit = _hex.LitCount;
                if (lit > _lastLit) Fire("hexagon_light", HiveT, Vector3.zero, false);
                _lastLit = lit;
            }
            var disp = StakesDirector.Instance != null ? StakesDirector.Instance.Display : null;
            if (disp != null)
            {
                bool clock = disp.ClockSeconds >= 0f;
                if (clock && !_wasClock) Fire("clock_start", Named("HONEYMOON-1"), Vector3.zero, false);
                _wasClock = clock;
                string text = disp.Text;
                bool cancelled = text != null && text.IndexOf("CANCELLED", StringComparison.Ordinal) >= 0;
                if (cancelled && !_wasCancelled) Fire("evacuation_cancelled", Named("HONEYMOON-1"), Vector3.zero, false);
                _wasCancelled = cancelled;
            }
            if (_countdown != null)
            {
                string t = _countdown.text;
                if (!ReferenceEquals(t, _lastCountdown) && t != _lastCountdown)
                {
                    _lastCountdown = t;
                    if (_director != null && _director.Beat == GameBeat.T0 && t != null)
                    {
                        if (t.Length == 1 && t[0] >= '1' && t[0] <= '9') Fire("countdown_beep", Named("HONEYMOON-1"), Vector3.zero, false);
                        else if (t == "LIFTOFF") Fire("launch", Named("HONEYMOON-1"), Vector3.zero, false);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ voices
        void Fire(string evt, Transform t, Vector3 p, bool hasPos, float pitchScale = 1f)
        {
            if (string.IsNullOrEmpty(evt)) return;
            if (evt == "dark_tendril" || evt == "dark_dive") _darkMomentUntil = Time.time + darkMomentSeconds;
            UpdateState();
            if (!_byEvent.TryGetValue(evt, out var list))
            {
                Count(_skipped, evt);
                Skipped++;
                if (logTriggers) Log(evt + ": no voice in the scene");
                return;
            }
            for (int i = 0; i < list.Count; i++) FireVoice(list[i], evt, t, p, hasPos, pitchScale);
        }

        void FireVoice(SoundVoice v, string evt, Transform t, Vector3 p, bool hasPos, float pitchScale)
        {
            float now = Time.time;
            string skip = null;
            if (v.muted) skip = "silent in the script";
            else if (!v.InZone(State)) skip = "not in " + State;
            else if (_lastFire.TryGetValue(v, out var last) && now - last < v.cooldown) skip = "cooldown " + v.cooldown.ToString("0.##", CultureInfo.InvariantCulture) + " s";
            else if (MicNow()) skip = "mic listening";
            string clipName = null;
            AudioClip clip = null;
            if (skip == null)
            {
                clip = PickClip(v, out clipName);
                if (clip == null) skip = "take not loaded: " + clipName;
            }
            if (skip != null)
            {
                Count(_skipped, evt);
                Skipped++;
                if (logTriggers) Log(evt + " -> " + v.id + ": skip (" + skip + ")");
                return;
            }

            Transform follow = t;
            Vector3 pos = p;
            if (follow == null && !hasPos) follow = HostOf(v.host);
            if (follow != null) pos = follow.position;
            else if (!hasPos) pos = ListenerPos();

            var shot = Allocate(v, out string stole);
            if (shot == null)
            {
                Count(_skipped, evt);
                Skipped++;
                if (logTriggers) Log(evt + " -> " + v.id + ": drop (every voice busy with a more important sound)");
                return;
            }
            _lastFire[v] = now;
            var src = shot.src;
            src.Stop();
            src.clip = clip;
            src.loop = false;
            src.pitch = Mathf.Clamp(PitchOf(v) * pitchScale, 0.5f, 2f);
            src.minDistance = Mathf.Max(1f, v.radius);
            src.priority = v.priority == 0 ? 16 : v.priority == 2 ? 220 : 128;
            src.transform.position = pos;
            float gain = Gain(v);
            src.volume = gain;
            src.Play();
            shot.v = v;
            shot.follow = follow;
            shot.started = now;
            float len = clip.length > 0.01f ? clip.length : Mathf.Max(0.2f, v.duration);
            shot.endAt = now + len / Mathf.Max(0.1f, src.pitch);
            Played++;
            Count(_played, evt);
            if (logTriggers)
                Log(evt + " -> " + v.id + ": " + clipName + " at " + (follow != null ? follow.name : "point") + " " +
                    Vector3.Distance(pos, ListenerPos()).ToString("0.00", CultureInfo.InvariantCulture) + " m, " +
                    Db(gain) + (Math.Abs(src.pitch - 1f) > 0.005f ? ", pitch " + src.pitch.ToString("0.00", CultureInfo.InvariantCulture) : "") +
                    (stole != null ? ", took the voice of " + stole : ""));
        }

        AudioClip PickClip(SoundVoice v, out string name)
        {
            name = v.clips.Length > 0 ? v.clips[0] : "(none)";
            if (v.clips.Length == 0) return null;
            int i = 0;
            if (v.clips.Length > 1)
            {
                int last = _lastClip.TryGetValue(v, out var l) ? l : -1;
                i = UnityEngine.Random.Range(0, v.clips.Length - (last >= 0 ? 1 : 0));
                if (last >= 0 && i >= last) i++; // never the same take twice in a row
                _lastClip[v] = i;
            }
            name = v.clips[i];
            return _clips.TryGetValue(name, out var c) ? c : null;
        }

        Shot Allocate(SoundVoice v, out string stole)
        {
            stole = null;
            float now = Time.time;
            Shot victim = null;
            for (int i = 0; i < _shots.Length; i++)
            {
                var s = _shots[i];
                if (s.v == null || now >= s.endAt) return s;
                if (victim == null || s.v.priority > victim.v.priority || (s.v.priority == victim.v.priority && s.started < victim.started)) victim = s;
            }
            if (victim == null) return null;
            // a less important sound gives its voice up; the never-dropped ones take the oldest voice whatever it plays
            if (victim.v.priority > v.priority || v.priority == 0)
            {
                stole = victim.v.id;
                victim.src.Stop();
                return victim;
            }
            return null;
        }

        float Gain(SoundVoice v)
        {
            float spl = v.splFollow != null ? v.splFollow.Map(Sig(v.splFollow.signal)) : v.spl;
            float g = Mathf.Pow(10f, (spl - levelReferenceDb) / 20f);
            if (v.volume != null) g *= v.volume.Map(Sig(v.volume.signal));
            g *= SatieScene.GroupDuck.Map(Sig(SatieScene.GroupDuck.signal));
            g *= _mic * Volume;
            return Mathf.Clamp01(g);
        }

        float PitchOf(SoundVoice v) => v.pitch != null ? v.pitch.Map(Sig(v.pitch.signal)) : 1f;

        void UpdateLoops(float dt)
        {
            for (int i = 0; i < _loops.Count; i++)
            {
                var l = _loops[i];
                var v = l.v;
                _clips.TryGetValue(v.clips.Length > 0 ? v.clips[0] : "", out var clip);
                string why = null;
                if (!v.InZone(State)) why = "not in " + State;
                else if (!Present(v)) why = "no " + (v.host ?? "host");
                else if (clip == null) why = "take not loaded";
                bool want = why == null;
                float rise = v.fadeIn > 0f ? v.fadeIn : zoneFadeSeconds;
                l.gate = Mathf.MoveTowards(l.gate, want ? 1f : 0f, dt / Mathf.Max(0.01f, want ? rise : zoneFadeSeconds));
                if (want && !l.on)
                {
                    l.src.clip = clip;
                    l.src.Play();
                    l.on = true;
                    Count(_played, "loop:" + v.id);
                    Log("loop " + v.id + " on (" + State + ") at " + v.host + ", " + Db(Gain(v)) + " before fades");
                }
                if (!l.on) continue;
                l.src.transform.position = LoopPosition(v);
                l.src.pitch = Mathf.Clamp(PitchOf(v), 0.5f, 2f);
                l.src.volume = Mathf.Clamp01(l == _droneLoop ? DroneVolume(l) : Gain(v) * l.gate);
                if (!want && l.gate <= 0f)
                {
                    l.src.Stop();
                    l.on = false;
                    Log("loop " + v.id + " off (" + why + ")");
                }
            }
        }

        /// <summary>
        /// The drone follows dark_swarm_count, but outside the swarm's own moments (the hook's pour, darkMomentSeconds after a
        /// tendril/dive) it stays droneUnderHumDb under the hive hum: at count 120 the script's levels put it over a sick
        /// hive's hum (49 dB) in the feed beat.
        /// </summary>
        float DroneVolume(Loop l)
        {
            float vol = Gain(l.v) * l.gate;
            bool own = State == "hook" || Time.time < _darkMomentUntil;
            bool held = false;
            if (!own && _humLoop != null && _humLoop.on)
            {
                float cap = Gain(_humLoop.v) * _humLoop.gate * Mathf.Pow(10f, -droneUnderHumDb / 20f);
                if (vol > cap) { vol = cap; held = true; }
            }
            if (held != _droneHeld)
            {
                _droneHeld = held;
                Log("loop dark_swarm_drone " + (held ? "held " + droneUnderHumDb.ToString("0.#", CultureInfo.InvariantCulture) + " dB under the hive hum" : "at its own level"));
            }
            return vol;
        }

        void UpdateShots()
        {
            float now = Time.time;
            for (int i = 0; i < _shots.Length; i++)
            {
                var s = _shots[i];
                if (s.v == null || now >= s.endAt) continue;
                if (s.follow != null) s.src.transform.position = s.follow.position;
                s.src.volume = Gain(s.v); // duck and mic act on sounds already playing
            }
        }

        void StopAll()
        {
            foreach (var l in _loops) { if (l.src != null) l.src.Stop(); l.on = false; l.gate = 0f; }
            foreach (var s in _shots) { if (s.src != null) s.src.Stop(); s.endAt = -1f; }
            _delayed.Clear();
        }

        // ------------------------------------------------------------------ where
        DarkSwarm Dark => StakesDirector.Instance != null ? StakesDirector.Instance.Swarm : null;
        Transform HiveT => _hive != null ? _hive.transform : Named("Hive");
        Transform WindowT => _window != null ? _window.PaneTransform : Named("FutureWindow");

        bool Present(SoundVoice v)
        {
            switch (v.host)
            {
                case "Hive":
                    if (_hive == null || !_hive.gameObject.activeInHierarchy) return false;
                    if (v.id == "shared_wasp_buzz" && _wasps.Length > 0)
                    {
                        for (int i = 0; i < _wasps.Length; i++) if (_wasps[i] != null && _wasps[i].gameObject.activeInHierarchy) return true;
                        return false; // they raided in or flew off
                    }
                    return true;
                case "FutureWindow": return _window == null || _window.IsOpen;
                case "DarkSwarm": var ds = Dark; return ds != null && ds.Mode != DarkSwarmMode.Hidden;
                default: return true;
            }
        }

        Vector3 LoopPosition(SoundVoice v)
        {
            switch (v.host)
            {
                case "Hive":
                    if (_hive == null) break;
                    return v.id == "shared_wasp_buzz" ? _hive.DoorPoint.position : _hive.SwarmCenter.position;
                case "FutureWindow":
                    var w = WindowT;
                    if (w != null) return w.position;
                    break;
                case "DarkSwarm":
                    var ds = Dark;
                    if (ds != null) return ds.BandCenter;
                    break;
            }
            var h = HostOf(v.host);
            return h != null ? h.position : ListenerPos();
        }

        Transform HostOf(string host)
        {
            switch (host)
            {
                case null: return null;
                case "Hive": return HiveT;
                case "FutureWindow": return WindowT;
                case "Swarm": return _hive != null ? _hive.DoorPoint : Named("Swarm");
                case "DarkSwarm": return null; // a point (band centre), resolved by the caller
                case "BeeDelivery": return null; // the crew is wherever its item is: cues pass the item
                default: return Named(host);
            }
        }

        /// <summary>An object by name (props share names with their landing spots: prefer the delivered item, then an active one).</summary>
        Transform Named(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_named.TryGetValue(name, out var t) && t != null) return t;
            if (_namedMiss.TryGetValue(name, out var missAt) && Time.time - missAt < 5f) return null;
            Transform best = null;
            int bestScore = -1;
            foreach (var c in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (c.name != name) continue;
                int score = (c.GetComponent<DeliveryItem>() != null ? 2 : 0) + (c.gameObject.activeInHierarchy ? 1 : 0);
                if (score > bestScore) { best = c; bestScore = score; }
            }
            if (best != null) _named[name] = best;
            else _namedMiss[name] = Time.time;
            return best;
        }

        Vector3 ListenerPos()
        {
            var cam = Camera.main;
            return cam != null ? cam.transform.position : Vector3.zero;
        }

        // ------------------------------------------------------------------ credit (Satie's licence: "Sound by Satie" in the credits)
        void AddCredit()
        {
            var map = Named("LevelMap");
            if (map == null) { Log("no LevelMap: the 'Sound by Satie' credit is not shown"); return; }
            var go = new GameObject("[VBS] SoundCredit");
            go.transform.SetParent(map, false);
            go.transform.localPosition = new Vector3(0.2f, -0.18f, -0.002f);
            go.layer = map.gameObject.layer;
            var tm = go.AddComponent<TextMesh>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            tm.font = font;
            tm.fontSize = 64;
            tm.characterSize = 0.0018f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = new Color(1f, 1f, 1f, 0.75f);
            tm.text = "Sound by Satie";
            var r = go.GetComponent<MeshRenderer>();
            if (font != null) r.sharedMaterial = font.material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        // ------------------------------------------------------------------ log
        static void Count(Dictionary<string, int> d, string k) => d[k] = (d.TryGetValue(k, out var n) ? n : 0) + 1;

        string Summary()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Played).Append(" played, ").Append(Skipped).Append(" skipped");
            foreach (var kv in _played) sb.Append(", ").Append(kv.Key).Append(' ').Append(kv.Value);
            return sb.ToString();
        }

        void OnApplicationQuit()
        {
            if (Instance == this && SoundOn) Log("summary: " + Summary());
        }

        static string Db(float g) => (g <= 1e-5f ? "-inf" : (20f * Mathf.Log10(g)).ToString("0.0", CultureInfo.InvariantCulture)) + " dB";

        void Log(string s) => Debug.Log("[VBS][Sound] t=" + Time.time.ToString("0.00", CultureInfo.InvariantCulture) + " " + s);
    }
}
