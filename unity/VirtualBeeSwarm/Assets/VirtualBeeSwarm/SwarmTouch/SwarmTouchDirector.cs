using UnityEngine;
using VirtualBeeSwarm.Juice;
using VirtualBeeSwarm.Pals;

namespace VirtualBeeSwarm.SwarmTouch
{
    /// <summary>
    /// Petting the swarm. Each frame, before the swarms move, puts up to 4 spheres in SwarmTouchField: each tracked
    /// hand's index tip (2.5 cm; a controller tip is 5 cm) and, with hand tracking, its palm (4.5 cm) while it moves
    /// (a palm held out still, or with PalBees on it, is left alone), with smoothed velocities. Hands come from Pals/PalHands (GameHands tips + the [Pals hook] palm in Meta/OvrHandPoseSource, plus
    /// the desktop ghost hand), or straight from GameHands when Pals isn't there. After the swarms move it reads what
    /// they felt:
    ///   calm bees touched   the hive hum swells (its loop, louder and a touch higher; nothing new is played), a few gold
    ///                       motes trail the fingers, and once a ride, after ~1.2 s of petting with Beeswax free, he says
    ///                       room_pal_pet ("Gentle. They like you. ...", caption-only until voiced).
    ///   angry bees touched  (the smoker swarm before it's calmed) they dart off harder and buzz up: Spike(0.25) at most
    ///                       every 2.5 s (Mode Angry only), which the hum's pitch follows. No stinging, no line.
    ///   the dark swarm      parts, nothing else.
    /// No hand tracked = no spheres = the swarm hooks skip at once. The autopilot never has a hand, so smoke is unchanged.
    /// Desktop: SwarmTouchDemo (F11, env VBS_SWARMTOUCH_DEMO=1) sweeps the ghost hand through the swarm.
    /// </summary>
    [DefaultExecutionOrder(-20)] // after PalHands (-30), before SwarmSystem / DarkSwarm (0)
    public class SwarmTouchDirector : MonoBehaviour
    {
        public static SwarmTouchDirector Instance { get; private set; }

        const float TipRadius = 0.025f, ControllerRadius = 0.05f, PalmRadius = 0.045f;
        const float StillPalm = 0.1f; // m/s: a palm held out this still is an invitation (PalBees land on it), not a swipe

        SwarmSystem _swarm;
        HiveBody _hive;
        GameNet _net;
        GameDirector _director;
        BeeswaxController _bw;
        IntroSequence _intro;
        JuiceParticles _fx;
        PalBees _palBees;
        PalBeeswax _palBw;
        float _lastPet = -9f;
        AudioSource _hum;
        float _nextHumLook, _nextHive;
        Bounds _hiveBounds;
        bool _hasHive;
        readonly Vector3[] _prev = new Vector3[4], _vel = new Vector3[4];
        readonly bool[] _had = new bool[4];
        float _swell, _petSeconds, _quietSince, _nextMote, _nextBuzz;
        bool _said;
        GameBeat _lastBeat;

        // numbers for the log / captures
        public int Petted { get; private set; }  // calm bees touched this frame
        public int Riled { get; private set; }   // angry bees touched this frame
        public int BuzzUps { get; private set; }
        public float Swell => _swell;
        /// <summary>The hive's parts (world box, refreshed each second; false before it lands).</summary>
        public bool TryHiveBox(out Bounds b) { b = _hiveBounds; return _hasHive; }
        float _statFrom, _peakPet, _peakAngry, _nearest = 9f, _maxMs;
        int _frames, _putBack;
        double _sumMs;

        public static GameLine PetLine => RideLines.Get("room_pal_pet", "Gentle. They like you. Don't tell them I said that.");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (FindAnyObjectByType<GameDirector>() == null || FindAnyObjectByType<SwarmTouchDirector>() != null) return;
            var go = new GameObject("SwarmTouch");
            go.AddComponent<SwarmTouchDirector>();
            go.AddComponent<SwarmTouchDemo>();
        }

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        void Start()
        {
            _swarm = FindAnyObjectByType<SwarmSystem>(FindObjectsInactive.Include);
            _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            _net = FindAnyObjectByType<GameNet>();
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            _bw = FindAnyObjectByType<BeeswaxController>();
            _intro = FindAnyObjectByType<IntroSequence>();
            _fx = FindAnyObjectByType<JuiceParticles>();
            _palBees = FindAnyObjectByType<PalBees>();
            _palBw = FindAnyObjectByType<PalBeeswax>();
            Debug.Log("[VBS][SwarmTouch] on: tip " + TipRadius + " m, palm " + PalmRadius + " m, falloff " + SwarmTouchField.Falloff +
                      " m" + (QuestPerf.On ? " (quest profile: fewer motes)" : ""));
        }

        // ------------------------------------------------------------------ before the swarms
        void Update()
        {
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            var cam = Camera.main;
            if (Time.time >= _nextHive) { _nextHive = Time.time + 1f; _hasHive = HiveBox(out _hiveBounds); }
            SwarmTouchField.BeginFrame(Time.time, cam != null, cam != null ? cam.transform.position : Vector3.zero, _hasHive, _hiveBounds);

            var hands = PalHands.Instance;
            bool landed = _palBees != null && _palBees.Landed;
            for (int h = 0; h < 2; h++)
            {
                bool tracked, palm = false, controller;
                Vector3 tip, centre = default;
                if (hands != null)
                {
                    var ph = hands.Hands[h];
                    tracked = ph.tracked;
                    tip = ph.tip;
                    if (ph.palm) { palm = true; centre = ph.centre; }
                    controller = !ph.palm && !ph.ghost;
                }
                else
                {
                    tracked = GameHands.TryGetIndexTip((GameHand)h, out tip);
                    controller = GameHands.For((GameHand)h) is XRControllerHandSource;
                }
                Sphere(h * 2, tracked, tip, controller ? ControllerRadius : TipRadius, dt, true);
                // the palm only pushes while it moves: held out still (or with PalBees on it) it's where bees are welcome
                Sphere(h * 2 + 1, tracked && palm, centre, PalmRadius, dt, !landed);
            }
        }

        void Sphere(int slot, bool on, Vector3 at, float radius, float dt, bool stillPushes)
        {
            if (!on) { _had[slot] = false; _vel[slot] = Vector3.zero; return; }
            Vector3 v = _had[slot] ? (at - _prev[slot]) / dt : Vector3.zero;
            if (v.sqrMagnitude > 25f) v = v.normalized * 5f; // a tracking jump is not a swipe
            _vel[slot] = Vector3.Lerp(_vel[slot], v, 1f - Mathf.Exp(-dt / 0.05f));
            _prev[slot] = at;
            _had[slot] = true;
            if (radius == PalmRadius && (!stillPushes || _vel[slot].sqrMagnitude < StillPalm * StillPalm)) return;
            SwarmTouchField.Add(at, _vel[slot], radius);
        }

        /// <summary>The hive body (no stand) as a world box, from HiveBody's own body renderers. No allocations.</summary>
        bool HiveBox(out Bounds b)
        {
            b = default;
            if (_hive == null || !_hive.isActiveAndEnabled || !_hive.TryGetBodyBounds(out var frame, out var local)) return false;
            Matrix4x4 m = frame.localToWorldMatrix;
            Vector3 c = m.MultiplyPoint3x4(local.center), e = local.extents;
            Vector3 x = m.MultiplyVector(new Vector3(e.x, 0f, 0f)), y = m.MultiplyVector(new Vector3(0f, e.y, 0f)), z = m.MultiplyVector(new Vector3(0f, 0f, e.z));
            Vector3 w = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x), Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                                    Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            b = new Bounds(c, 2f * w);
            return true;
        }

        // ------------------------------------------------------------------ after the swarms
        void LateUpdate()
        {
            float dt = Time.deltaTime;
            bool swarmOn = _swarm != null && _swarm.Mode != SwarmMode.Hidden && _swarm.Mode != SwarmMode.Ball;
            bool angry = swarmOn && (_swarm.Mode == SwarmMode.Angry || _swarm.Agitation > 0.6f);
            int t0 = SwarmTouchField.Touched[0];
            Petted = swarmOn && !angry ? t0 : 0;
            Riled = angry ? t0 : 0;

            // the hum: its loop, a little louder and higher while bees are stirred
            float want = Mathf.Clamp01(Petted / 10f + Riled / 6f);
            _swell = Mathf.MoveTowards(_swell, want, dt / (want > _swell ? 0.15f : 0.8f));
            Hum(angry);

            if (Petted > 0) Motes();
            // a short buzz-up, never a spiral; only the truly angry swarm (an agitated one just darts, so it can calm down)
            if (Riled >= 3 && Time.time >= _nextBuzz && _swarm != null && _swarm.Mode == SwarmMode.Angry && _swarm.Agitation < 0.8f)
            {
                _nextBuzz = Time.time + 2.5f;
                _swarm.Spike(0.25f);
                BuzzUps++;
                Debug.Log("[VBS][SwarmTouch] angry swarm touched (" + Riled + " bees): buzz-up #" + BuzzUps);
            }
            Line(dt);
            Stats(dt);
        }

        void Hum(bool angry)
        {
            if (_hum == null)
            {
                if (Time.time < _nextHumLook) return;
                _nextHumLook = Time.time + 2f;
                var go = GameObject.Find("[VBS] Loop hive_hum"); // Sound/SoundDirector's loop; it sets volume in Update, we nudge it after
                if (go == null || !go.TryGetComponent(out _hum)) return;
            }
            if (_swell <= 0.001f || !_hum.isPlaying) return;
            _hum.volume = Mathf.Clamp01(_hum.volume * (1f + 0.8f * _swell));
            _hum.pitch = Mathf.Clamp(_hum.pitch * (1f + (angry ? 0.1f : 0.04f) * _swell), 0.5f, 2f);
        }

        void Motes()
        {
            if (_fx == null || Time.time < _nextMote) return;
            // the fastest hand sphere that's in among the bees
            int best = -1;
            float bs = 0.25f;
            for (int s = 0; s < 4; s++)
            {
                if (!_had[s]) continue;
                float sp = _vel[s].magnitude;
                if (sp > bs) { bs = sp; best = s; }
            }
            if (best < 0) return;
            _nextMote = Time.time + (QuestPerf.On ? 0.45f : 0.22f);
            _fx.Burst(_prev[best] - _vel[best] * 0.04f, QuestPerf.On ? 1 : 2, new Color(1f, 0.85f, 0.35f), 0.12f, 0.7f, 0.006f, 0f, 0.3f, 3f);
        }

        void Line(float dt)
        {
            var beat = _director != null ? _director.Beat : GameBeat.Pre;
            if (beat == GameBeat.Pre && _lastBeat != GameBeat.Pre) { _said = false; _petSeconds = 0f; } // a new ride
            _lastBeat = beat;
            bool talking = _net != null && _net.IsSpeaking;
            if (talking) _quietSince = Time.time;
            if (_said || _net == null) return;
            if (Petted >= 6) _lastPet = Time.time;
            // credit for petting now, capped: a long stroke during his dialogue can't fire the line a minute later
            _petSeconds = Petted >= 6 ? Mathf.Min(_petSeconds + dt, 1.5f) : Mathf.Max(0f, _petSeconds - dt * 0.5f);
            if (_petSeconds < 1.2f || Time.time - _lastPet > 0.5f || Time.time - _quietSince < 2f || !Free(beat)) return;
            _said = true;
            Debug.Log("[VBS][SwarmTouch] line room_pal_pet (" + Petted + " bees parting, beat " + beat + ")");
            if (_palBw != null) _palBw.HoldLines(6f); // [Pals] same quiet gap as his pal lines
            _net.Say(PetLine);
        }

        public bool Said => _said;

        /// <summary>Beeswax may talk: the Pals check when Pals is here (not mid high-five, attached, or inside its line
        /// gap), else: not mid-line, not in the hook / meet / escape / T-0 / field, not in a show moment.</summary>
        public bool Free(GameBeat b)
        {
            if (_palBw != null) return _palBw.FreeToTalk;
            if (_net == null || _net.IsSpeaking || BeeswaxMotion.ShowMoment) return false;
            if (_bw == null || !_bw.Visible || _bw.Inside || _bw.Climbing || _bw.IsAttached) return false;
            if (b == GameBeat.Hook || b == GameBeat.Meet || b == GameBeat.Escape || b == GameBeat.T0 || b == GameBeat.Field) return false;
            if (b == GameBeat.Pre) return _intro != null && _intro.SignHereReady; // clipboard handed over: he's just hovering
            return true;
        }

        void Stats(float dt)
        {
            if (SwarmTouchField.Count == 0 && _frames == 0) return;
            float ms = (float)(SwarmTouchField.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _frames++;
            _sumMs += ms;
            _maxMs = Mathf.Max(_maxMs, ms);
            _peakPet = Mathf.Max(_peakPet, Petted + SwarmTouchField.Touched[1]);
            _peakAngry = Mathf.Max(_peakAngry, Riled);
            _nearest = Mathf.Min(_nearest, SwarmTouchField.NearestShell);
            _putBack += SwarmTouchField.PutBack;
            if (Time.time - _statFrom < 1f) return;
            if (_peakPet + _peakAngry > 0)
                Debug.Log("[VBS][SwarmTouch] 1 s: " + SwarmTouchField.Count + " spheres, peak " + _peakPet + " bees parting" +
                          (_peakAngry > 0 ? ", " + _peakAngry + " angry" : "") + ", nearest bee " + (_nearest * 100f).ToString("0.0") +
                          " cm off a finger surface, " + _putBack + " put back out, swell " + _swell.ToString("0.00") +
                          ", cost avg " + (_sumMs / Mathf.Max(1, _frames)).ToString("0.000") + " ms max " + _maxMs.ToString("0.000") + " ms");
            _statFrom = Time.time;
            _frames = 0; _sumMs = 0; _maxMs = 0f; _peakPet = _peakAngry = 0f; _nearest = 9f; _putBack = 0;
        }
    }
}
