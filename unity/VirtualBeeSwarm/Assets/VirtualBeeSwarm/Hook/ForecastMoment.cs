using System;
using System.IO;
using UnityEngine;
using UnityEngine.Video;
using VirtualBeeSwarm.Stakes;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The forecast in the meadow window while Beeswax says room_hook_1 ("Let my hive die, and angry swarms ravage the
    /// Earth. You humans get the Moon."). From the moment the window opens it shows a peaceful Paris park
    /// (forecast_peaceful.mp4, looping); on "angry swarms" (StakesDirector.Poured, the moment the dark swarm pours) it
    /// cuts to the ravaged one-shot (forecast_ravaged.mp4: red sky, dark swarms, a giant laser-eyed bee slices the Eiffel
    /// Tower, it topples, a tiny tourist is flung), which runs straight into its loop (forecast_aftermath.mp4) until meet
    /// moves the window on to the meadow. Both clips are prepared ahead on their own VideoPlayers, so the cut is
    /// frame-exact, and shown with FutureWindow.SetOverride in front of the Hive's picture.
    /// Live Orbis: each scene is also requested from the Hive (GameNet.RequestScene), so a live stream morphs along. The
    /// clip covers while live catches up (liveCatchUpSeconds after each request; the ravaged gag always plays from the clip,
    /// it is timed to the SFX), then the live picture shows, and the clip comes back whenever live drops.
    /// SFX, spatial from the window and ducked under Beeswax's voice: the swarm roar on the cut, the laser, the tower's
    /// groan and crash, and a Wilhelm-STYLE scream (a generated sound-alike, not the 1951 recording) as the tourist is
    /// flung. The offsets match art/blender/scripts/window_clips.py (laser 0.10-0.62 s, fling 1.35 s, the top lands 1.55 s).
    /// Switch: StakesFlags.Forecast (stakes.forecast). Missing clip files: the window's tint shows the two scenes instead.
    /// </summary>
    public class ForecastMoment : MonoBehaviour
    {
        [SerializeField] FutureWindow window;
        [SerializeField] GameNet net;
        [SerializeField] GameDirector director;

        [Header("Clips (StreamingAssets)")]
        [SerializeField] string peacefulClip = "forecast_peaceful.mp4";
        [SerializeField] string ravagedClip = "forecast_ravaged.mp4";
        [SerializeField] string aftermathClip = "forecast_aftermath.mp4";
        [SerializeField] Vector2Int size = new Vector2Int(832, 480);

        [Header("Live Orbis")]
        [Tooltip("After a scene request, live must have had this long to morph there (Orbis takes 2-4 s) before it replaces the clip.")]
        [SerializeField] float liveCatchUpSeconds = 3.5f;
        [Tooltip("The ravaged one-shot plays at least this long from the clip: the gag is timed to the SFX.")]
        [SerializeField] float gagSeconds = 3f;
        [Tooltip("Peaceful: no hand-over to live when the cut is due sooner than this.")]
        [SerializeField] float minLiveSeconds = 1.5f;
        [Tooltip("When meet moves the window on (meadow), the aftermath stays this long while that clip loads.")]
        [SerializeField] float handBackSeconds = 0.4f;

        [Header("SFX: seconds after the cut, volume")]
        [SerializeField] AudioClip swarmSfx;
        [SerializeField] float swarmAt = 0f, swarmVolume = 0.45f;
        [SerializeField] AudioClip laserSfx;
        [SerializeField] float laserAt = 0.08f, laserVolume = 0.4f;
        [SerializeField] AudioClip towerSfx;
        [Tooltip("The file's crash starts 1.08 s in, so 0.47 lands it on the clip's impact at 1.55 s.")]
        [SerializeField] float towerAt = 0.47f, towerVolume = 0.55f;
        [SerializeField] AudioClip screamSfx;
        [Tooltip("The tourist leaves the top at 1.35 s. 1.5 starts the scream on the last of \"...the Earth.\" so it fills the pause before \"You humans\" (room_hook_1 3.1-3.8 s).")]
        [SerializeField] float screamAt = 1.5f, screamVolume = 0.7f;

        [Header("Ducking under Beeswax (sidechain on GameNet.VoiceAmplitude)")]
        [SerializeField] float duckTo = 0.4f;
        [SerializeField] float duckThreshold = 0.05f;
        [SerializeField] float duckHoldSeconds = 0.15f, duckAttackSeconds = 0.05f, duckReleaseSeconds = 0.12f;
        [Tooltip("Linear rolloff: full level within sfxMinDistance of the window, silent at sfxMaxDistance.")]
        [SerializeField] float sfxMinDistance = 3f, sfxMaxDistance = 15f;
        [SerializeField] bool log = true;

        const string HookLineId = "room_hook_1";

        enum Phase { Off, Peaceful, Ravaged }

        class Player
        {
            public VideoPlayer vp;
            public RenderTexture rt;
            public string file = "";
            public bool loaded, failed, ended;
        }

        class Sfx
        {
            public string name;
            public AudioClip clip;
            public float at, volume, startAt = -1f;
            public AudioSource src;
        }

        Phase _phase;
        Player _a, _b, _want, _shown; // a: peaceful, then the aftermath loop; b: the ravaged one-shot
        Sfx[] _sfx = new Sfx[0];
        float _shownAt = -1f, _openedAt = -1f, _cutAt = -1f, _cutDueAt = -1f, _requestAt = -1f, _lineAt = -1f;
        float _fallbackCutAt = -1f, _endAt = -1f, _screamAt = -1f, _screamGain = 1f, _duck = 1f, _duckHoldUntil;
        bool _liveShown, _subscribed;
        string _cutHow = "", _endWhy = "";

        /// <summary>The window shows the forecast (peaceful or ravaged).</summary>
        public bool Active => _phase != Phase.Off;
        public bool Enabled => StakesFlags.Forecast && isActiveAndEnabled;
        /// <summary>One line on the last forecast: how long peaceful showed, where the cut and the scream fell in room_hook_1.</summary>
        public string Summary { get; private set; } = "";

        void Awake()
        {
            if (window == null) window = GetComponent<FutureWindow>();
            if (window == null) window = FindAnyObjectByType<FutureWindow>();
            if (net == null) net = GameNet.Instance != null ? GameNet.Instance : FindAnyObjectByType<GameNet>();
            if (director == null) director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            _sfx = new[]
            {
                MakeSfx("swarm", swarmSfx, swarmAt, swarmVolume),
                MakeSfx("laser", laserSfx, laserAt, laserVolume),
                MakeSfx("tower", towerSfx, towerAt, towerVolume),
                MakeSfx("scream", screamSfx, screamAt, screamVolume),
            };
        }

        void OnEnable()
        {
            StakesDirector.Poured += HandlePour;
            if (net != null)
            {
                net.VoiceLineStarted += HandleVoiceLine;
                net.SceneChanged += HandleScene;
            }
            if (director != null) director.BeatChanged += HandleBeat;
            _subscribed = true;
        }

        void OnDisable()
        {
            if (!_subscribed) return;
            _subscribed = false;
            StakesDirector.Poured -= HandlePour;
            if (net != null)
            {
                net.VoiceLineStarted -= HandleVoiceLine;
                net.SceneChanged -= HandleScene;
            }
            if (director != null) director.BeatChanged -= HandleBeat;
            Stop("disabled");
        }

        void OnDestroy()
        {
            foreach (var p in new[] { _a, _b })
                if (p != null && p.rt != null) { p.rt.Release(); Destroy(p.rt); }
        }

        // ------------------------------------------------------------ API (IntroSequence)
        /// <summary>Load both clips now (pre and the start of the hook), so the window shows them without a wait.</summary>
        public void Prepare()
        {
            if (!Enabled || _phase != Phase.Off) return;
            if (_a == null) _a = MakePlayer("ForecastA");
            if (_b == null) _b = MakePlayer("ForecastB");
            Load(_a, peacefulClip, loop: true);
            Load(_b, ravagedClip, loop: false);
        }

        /// <summary>The window is opening: show the peaceful forecast (the cut is due in about cutDueIn seconds).</summary>
        public void ShowPeaceful(float cutDueIn)
        {
            if (!Enabled || _phase != Phase.Off) return;
            Prepare();
            _phase = Phase.Peaceful;
            _openedAt = _shownAt = _requestAt = Time.time;
            _cutDueAt = Time.time + Mathf.Max(0f, cutDueIn);
            _cutAt = _lineAt = _fallbackCutAt = _endAt = _screamAt = -1f;
            _cutHow = _endWhy = "";
            _liveShown = false;
            _shown = null;
            _want = _a;
            if (!_a.failed) _a.vp.Play();
            foreach (var s in _sfx) s.startAt = -1f;
            if (net != null) net.RequestScene(GameIds.Scene.ForecastPeaceful, sendToHive: true);
            Log("peaceful Paris in the window (" + ClipState(_a) + "), cut due in " + cutDueIn.ToString("0.00") + " s");
        }

        /// <summary>Back to nothing (reset): the window shows the Hive's picture again.</summary>
        public void ResetForecast() => Stop("reset");

        // ------------------------------------------------------------ events
        void HandlePour()
        {
            if (_phase == Phase.Peaceful) Cut("on the pour");
        }

        void HandleVoiceLine(GameNet.VoiceLineInfo info)
        {
            if (info.id != HookLineId || _phase == Phase.Off) return;
            _lineAt = Time.time;
            // Without a StakesDirector pour (none installed, or a jump back to the hook) cut on the words anyway.
            if (_phase == Phase.Peaceful) _fallbackCutAt = Time.time + HookTiming.ThreatOffset(info, HookTiming.DefaultThreatOffset) + 0.3f;
        }

        void HandleScene(string scene)
        {
            if (_phase == Phase.Off || IsForecast(scene) || (director != null && director.Beat == GameBeat.Hook)) return;
            float at = Time.time + handBackSeconds;
            if (_endAt < 0f || at < _endAt) { _endAt = at; _endWhy = "window scene " + scene; }
        }

        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            if (_phase == Phase.Off || beat == GameBeat.Hook) return;
            if (beat == GameBeat.Meet)
            {
                // meet asks for the meadow 0.8 s in; whatever happens, the forecast is gone 3 s into meet
                if (_endAt < 0f) { _endAt = Time.time + 3f; _endWhy = "meet"; }
                if (net != null && !IsForecast(net.CurrentScene)) HandleScene(net.CurrentScene);
                return;
            }
            Stop("beat " + GameIds.BeatName(beat));
        }

        static bool IsForecast(string scene) => scene == GameIds.Scene.ForecastPeaceful || scene == GameIds.Scene.ForecastRavaged;

        void Cut(string how)
        {
            if (_phase != Phase.Peaceful) return;
            _phase = Phase.Ravaged;
            _cutAt = _requestAt = Time.time;
            _cutHow = how;
            _fallbackCutAt = -1f;
            _want = _b;
            if (!_b.failed) _b.vp.Play();
            foreach (var s in _sfx) s.startAt = s.clip != null ? _cutAt + s.at : -1f;
            if (net != null) net.RequestScene(GameIds.Scene.ForecastRavaged, sendToHive: true);
            Log("cut to the ravaged world " + how + ": peaceful showed " + (_cutAt - _shownAt).ToString("0.00") + " s" +
                (_lineAt >= 0f ? ", " + (_cutAt - _lineAt).ToString("0.00") + " s into " + HookLineId : "") + " (" + ClipState(_b) + ")");
        }

        void Stop(string why)
        {
            if (_phase == Phase.Off) return;
            Summary = "forecast: peaceful " + (_cutAt >= 0f ? (_cutAt - _shownAt).ToString("0.00") + " s" : "only (no cut)") +
                      (_cutAt >= 0f && _lineAt >= 0f ? ", cut " + (_cutAt - _lineAt).ToString("0.00") + " s into " + HookLineId + " (" + _cutHow + ")" : "") +
                      (_screamAt >= 0f ? ", scream at cut +" + (_screamAt - _cutAt).ToString("0.00") +
                                         (_lineAt >= 0f ? " = " + HookLineId + " +" + (_screamAt - _lineAt).ToString("0.00") : "") +
                                         " s (gain " + _screamGain.ToString("0.00") + ")" : "") +
                      (_cutAt >= 0f ? ", ravaged " + (Time.time - _cutAt).ToString("0.00") + " s" : "") +
                      ", live " + (_liveShown ? "shown" : "not shown") + "; ended: " + why;
            Log(Summary);
            _phase = Phase.Off;
            _want = _shown = null;
            _endAt = _fallbackCutAt = -1f;
            if (window != null) window.SetOverride(null);
            foreach (var p in new[] { _a, _b })
            {
                if (p == null) continue;
                p.vp.Stop();
                p.loaded = false;
            }
            foreach (var s in _sfx)
            {
                s.startAt = -1f;
                if (s.src != null && s.src.isPlaying) s.src.Stop();
            }
        }

        // ------------------------------------------------------------ frame
        void Update()
        {
            float now = Time.time;
            if (_phase == Phase.Off) return;
            if (!StakesFlags.Forecast) { Stop("stakes.forecast off"); return; }
            if (_phase == Phase.Peaceful && _fallbackCutAt >= 0f && now >= _fallbackCutAt) Cut("on the words (no pour)");
            if (_endAt >= 0f && now >= _endAt) { Stop(_endWhy); return; }

            // the one-shot ran out (or never loaded): the aftermath loop, prepared on player A since the cut
            if (_phase == Phase.Ravaged)
            {
                if (_a.file != aftermathClip && (_shown == _b || _b.failed)) Load(_a, aftermathClip, loop: true);
                if (_want == _b && (_b.ended || _b.failed) && _a.file == aftermathClip && !_a.failed)
                {
                    _want = _a;
                    _a.vp.Play();
                    Log("aftermath loop (" + (_b.failed ? "the one-shot didn't load" : "one-shot done after " + (now - _cutAt).ToString("0.00") + " s") + ")");
                }
            }

            // keep showing the previous picture until the wanted player has a frame (no black flash on the cut)
            if (_want != null && !_want.failed && _want.vp.isPlaying && _want.vp.frame >= 0) _shown = _want;
            bool live = LiveCanShow(now);
            if (live != _liveShown && live) Log("live Orbis takes over the " + (_phase == Phase.Peaceful ? "peaceful" : "ravaged") + " forecast");
            if (live) _liveShown = true;
            Texture tex = live || _shown == null || _shown.failed ? null : _shown.rt;
            if (window != null) window.SetOverride(tex);

            UpdateSfx(now);
        }

        bool LiveCanShow(float now)
        {
            if (net == null || !net.OrbisIsLive || now - _requestAt < liveCatchUpSeconds) return false;
            if (_phase == Phase.Peaceful) return _cutDueAt - now >= minLiveSeconds;
            return now - _cutAt >= gagSeconds;
        }

        void UpdateSfx(float now)
        {
            bool loud = net != null && net.IsSpeaking && net.VoiceAmplitude > duckThreshold;
            if (loud) _duckHoldUntil = now + duckHoldSeconds;
            float target = now < _duckHoldUntil ? duckTo : 1f;
            float seconds = target < _duck ? duckAttackSeconds : duckReleaseSeconds;
            _duck = Mathf.MoveTowards(_duck, target, Time.deltaTime * (1f - duckTo) / Mathf.Max(0.01f, seconds));
            foreach (var s in _sfx)
            {
                if (s.src == null) continue;
                if (s.startAt >= 0f && now >= s.startAt)
                {
                    s.startAt = -1f;
                    s.src.Play();
                    if (s.name == "scream") { _screamAt = now; _screamGain = _duck; }
                    Log(s.name + " SFX at cut +" + (now - _cutAt).ToString("0.00") + " s" +
                        (_lineAt >= 0f ? " (" + HookLineId + " +" + (now - _lineAt).ToString("0.00") + " s)" : "") +
                        ", gain " + _duck.ToString("0.00") + (net != null && net.IsSpeaking ? " (Beeswax talking)" : ""));
                }
                s.src.volume = s.volume * _duck;
            }
        }

        // ------------------------------------------------------------ helpers
        Player MakePlayer(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var p = new Player { rt = new RenderTexture(size.x, size.y, 0) { name = name } };
            p.rt.Create();
            p.vp = go.AddComponent<VideoPlayer>();
            p.vp.playOnAwake = false;
            p.vp.waitForFirstFrame = true;
            p.vp.skipOnDrop = true;
            p.vp.audioOutputMode = VideoAudioOutputMode.None; // window clips are silent
            p.vp.renderMode = VideoRenderMode.RenderTexture;
            p.vp.targetTexture = p.rt;
            p.vp.source = VideoSource.Url;
            p.vp.errorReceived += (vp, message) =>
            {
                if (p.failed) return;
                p.failed = true;
                // not the message: on Linux players it can arrive garbled and touching it threw ArrayTypeMismatchException at start-up
                Debug.LogWarning("[VBS][Forecast] " + p.file + ": video failed to play (the window's tint shows this scene)");
            };
            p.vp.loopPointReached += vp => { if (!vp.isLooping) p.ended = true; };
            return p;
        }

        void Load(Player p, string file, bool loop)
        {
            if (p.loaded && p.file == file) return; // a file that failed is tried again next run (Stop unloads)
            p.vp.Stop();
            p.file = file;
            p.failed = p.ended = false;
            p.loaded = true;
            p.vp.isLooping = loop;
            p.vp.url = Path.Combine(Application.streamingAssetsPath, file);
            p.vp.Prepare();
        }

        Sfx MakeSfx(string name, AudioClip clip, float at, float volume)
        {
            var s = new Sfx { name = name, clip = clip, at = at, volume = volume };
            if (clip == null) return s;
            var go = new GameObject("ForecastSfx_" + name);
            go.transform.SetParent(transform, false);
            s.src = go.AddComponent<AudioSource>();
            s.src.playOnAwake = false;
            s.src.loop = false;
            s.src.clip = clip;
            s.src.spatialBlend = 1f;
            s.src.dopplerLevel = 0f;
            s.src.rolloffMode = AudioRolloffMode.Linear;
            s.src.minDistance = sfxMinDistance;
            s.src.maxDistance = sfxMaxDistance;
            s.src.volume = volume;
            if (!string.IsNullOrEmpty(AudioSettings.GetSpatializerPluginName())) s.src.spatialize = true;
            return s;
        }

        static string ClipState(Player p) =>
            p == null ? "no player" : p.failed ? p.file + " failed" : p.vp.isPrepared ? p.file + " ready" : p.file + " still loading";

        void Log(string msg)
        {
            if (log) Debug.Log("[VBS][Forecast] " + msg);
        }
    }
}
