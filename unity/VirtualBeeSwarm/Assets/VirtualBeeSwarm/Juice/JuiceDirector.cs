using System;
using UnityEngine;
using VirtualBeeSwarm.Stakes;

namespace VirtualBeeSwarm.Juice
{
    /// <summary>
    /// JUICE lane (feel audit of v33): the room answers every pick at the moment of the pick. Needs no scene edits:
    /// installs itself next to the GameDirector (AutoInstall), hears gameplay through GameSound.Cued (stamp cues carry the
    /// picked prop), GameNet.ChoiceMade, BeatChanged, the window's SceneChanged and HiveBody.Lifted.
    ///   stamps   at the stamp's hit: the prop squashes (1.07, 0.88, 1.07) and an ink ring spreads; a wrong pick also
    ///            shakes the prop 1 cm and blips the room dim (key "juice").
    ///   sting    right: a pollen burst from the prop and 12 gold bees stream from the dark band out of the window;
    ///            wrong: a sooty puff with red sparks. The window bulbs chase gold / blink red. (JuiceMarquee)
    ///   hex      a gold hex token flies from the prop to its slot on the plaque at the hive's foot. (JuiceHexPlaque)
    ///   beeswax  reacts at the slam, fills the gaps between lines, listens and thinks visibly. (JuiceBeeswax)
    ///   captions a dark plate behind Beeswax's caption, so it reads over the swarm and the bursts. (JuiceCaptionPlate)
    ///   beats    station cleared: plaque flourish + bulb chase; escape: strobe + the hexes pulse + a gold stream out;
    ///            lift invited: a gold ring pulses round the hive foot until it's lifted, then bees tumble out.
    /// Switches: tuning.json juice.* (JuiceSwitches). Everything is pooled / instanced, opaque, no lights, no per-frame
    /// allocations.
    /// </summary>
    [DefaultExecutionOrder(40)]
    public class JuiceDirector : MonoBehaviour
    {
        public static bool AutoInstall = true;
        public static JuiceDirector Instance { get; private set; }
        public const string DimKey = "juice";

        static readonly Color Pollen = new Color(1f, 0.84f, 0.18f, 1f);
        static readonly Color GoldBee = new Color(1f, 0.82f, 0.3f, 1f);
        static readonly Color Soot = new Color(0.2f, 0.18f, 0.17f, 1f);
        static readonly Color Spark = new Color(1f, 0.2f, 0.1f, 1f);
        static readonly Color InkApproved = new Color(0.16f, 0.62f, 0.32f, 1f);
        static readonly Color InkGrievance = new Color(0.85f, 0.12f, 0.1f, 1f);
        static readonly Color White = new Color(1f, 0.97f, 0.85f, 1f);

        GameDirector _director;
        GameNet _net;
        HiveBody _hive;
        FutureWindow _window;
        BeeDelivery _delivery;
        bool _cardDone;
        JuiceParticles _fx;
        JuiceHexPlaque _plaque;
        JuiceMarquee _marquee;
        JuiceBeeswax _bw;
        bool _subscribed;
        float _nextFind;

        // a pick: GameNet.ChoiceMade and the stamp cue arrive in either order within a frame or two
        int _pending;                 // +1 right, -1 wrong, 0 none
        float _pendingAt = -9f;
        Transform _stampT;
        bool _stampApproved;
        float _stampAt = -9f;
        Transform _hitT;              // stamp hit (squash + ink) scheduled at the slam
        bool _hitApproved;
        float _hitAt = -9f;
        float _dimReleaseAt = -9f;
        float _wrongAt = -99f;
        bool _liftedThisBeat;
        float _nextLiftInvite;

        public int Picks { get; private set; }

        // ------------------------------------------------------------------ cooperative punches (other code may own the transform)
        struct Punch { public Transform t; public bool shake; public float start, dur; public Vector3 written, applied; }
        readonly Punch[] _punch = new Punch[8];
        int _np;

        // ------------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!AutoInstall) return;
            JuiceSwitches.Load();
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<JuiceDirector>() != null) return;
            if (!JuiceSwitches.On) { Debug.Log("[VBS][Juice] off (juice.on = false)"); return; }
            var go = new GameObject("Juice");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<JuiceDirector>();
            Debug.Log("[VBS][Juice] installed (auto): " + JuiceSwitches.Describe());
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { enabled = false; return; }
            Instance = this;
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            _net = GameNet.Instance != null ? GameNet.Instance : FindAnyObjectByType<GameNet>();
            _fx = gameObject.AddComponent<JuiceParticles>();
            if (JuiceSwitches.Hex) { _plaque = gameObject.AddComponent<JuiceHexPlaque>(); _plaque.Init(_fx, _director); }
            if (JuiceSwitches.Window) { _marquee = gameObject.AddComponent<JuiceMarquee>(); _marquee.Bind(_net); }
            if (JuiceSwitches.Beeswax) { _bw = gameObject.AddComponent<JuiceBeeswax>(); _bw.Bind(_net, _director); }
            if (JuiceSwitches.Captions) gameObject.AddComponent<JuiceCaptionPlate>().Bind(_net);
        }

        void OnEnable()
        {
            if (Instance != this) return;
            if (_director != null) { _director.BeatChanged += HandleBeat; _director.GroupResetDone += HandleReset; }
            if (_net != null) { _net.SceneChanged += HandleScene; _net.ChoiceMade += HandleChoice; }
            GameSound.Cued += HandleCue;
            _subscribed = true;
        }

        void OnDisable()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (_director != null) { _director.BeatChanged -= HandleBeat; _director.GroupResetDone -= HandleReset; }
            if (_net != null) { _net.SceneChanged -= HandleScene; _net.ChoiceMade -= HandleChoice; }
            if (_hive != null) _hive.Lifted -= HandleLifted;
            if (_delivery != null) _delivery.Landed -= HandleLanded;
            GameSound.Cued -= HandleCue;
            PassthroughDim.Release(DimKey, 0f);
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        // ------------------------------------------------------------------ events
        void HandleCue(string evt, Transform at, Vector3 pos, float delay)
        {
            switch (evt)
            {
                case "stamp_approved":
                case "stamp_grievance":
                    _stampT = at;
                    _stampApproved = evt == "stamp_approved";
                    _stampAt = Time.time;
                    // the squash + ink ring belong to a station prop; other stamps (the ASK BEESWAX card) do their own
                    bool station = _director != null && _director.Beat >= GameBeat.Smoker && _director.Beat < GameBeat.Escape;
                    if (JuiceSwitches.Stamps && at != null && station) { _hitT = at; _hitApproved = _stampApproved; _hitAt = Time.time + 0.08f; }
                    if (_pending != 0 && Time.time - _pendingAt < 0.6f && (_pending > 0) == _stampApproved) Resolve();
                    break;
                case "dark_dive":
                    if (_bw != null) _bw.Flinch("dark dive");
                    break;
            }
        }

        /// <summary>GameNet.MakeChoice raises this on every pick (button, key, prop, Board), before the outcome line.</summary>
        void HandleChoice(GameStation station, string choice, bool right)
        {
            // a right pick right after a wrong one: his "...pick again" line and caption stop at once, so the room
            // never says APPROVED and "pick again" together (the outcome line follows)
            if (right && _net != null && _net.IsSpeaking && Time.time - _wrongAt < 10f) _net.StopVoice();
            _wrongAt = right ? -99f : Time.time;
            _pending = right ? 1 : -1;
            _pendingAt = Time.time;
            if (_stampT != null && Time.time - _stampAt < 0.6f && _stampApproved == right) Resolve();
        }

        void HandleScene(string scene)
        {
            if (_marquee != null) _marquee.Chase(new Color(1f, 0.9f, 0.62f, 1f), 0.45f, 1f);
            if (_bw != null && _director != null && _director.Beat >= GameBeat.Meet && _director.Beat < GameBeat.T0) _bw.Curious("window: " + scene);
        }

        void HandleLifted(float h)
        {
            if (_liftedThisBeat || _hive == null) return;
            _liftedThisBeat = true;
            if (JuiceSwitches.Sting)
            {
                Vector3 door = _hive.DoorPoint.position;
                for (int k = 0; k < 6; k++)
                {
                    float a = k * 1.047f + UnityEngine.Random.Range(-0.3f, 0.3f);
                    Vector3 to = door + new Vector3(Mathf.Cos(a) * 0.18f, UnityEngine.Random.Range(-0.04f, 0.1f), Mathf.Sin(a) * 0.18f);
                    _fx.Fly(door, to, 0.05f, 0.6f, k * 0.05f, GoldBee, 0.018f);
                }
                _fx.Burst(door, 10, Pollen, 0.5f, 0.45f, 0.01f);
            }
            if (_bw != null) _bw.Flinch("hive lifted");
        }

        /// <summary>Escape: the score card lands (a crown of 6 brings it): squash, gold burst, shock ring, bulbs chase.</summary>
        void HandleLanded(Transform t)
        {
            if (t == null || _cardDone || _director == null || _director.Beat != GameBeat.Escape || !JuiceSwitches.Beats) return;
            if (t.name.IndexOf("Score", StringComparison.OrdinalIgnoreCase) < 0) return;
            _cardDone = true;
            AddPunch(t, false, 0.3f);
            var cam = Camera.main;
            Vector3 c = ItemCenter(t);
            Vector3 toHead = cam != null ? (cam.transform.position - c).normalized : Vector3.back;
            _fx.Burst(c + toHead * 0.03f, 26, GoldBee, 1.2f, 0.6f, 0.014f, 1f, 0.5f, 3f);
            _fx.Ring(c + toHead * 0.04f, toHead, 22, White, 1.6f, 0.4f, 0.012f);
            if (_marquee != null) _marquee.Chase(new Color(1f, 0.95f, 0.66f, 1f), 0.7f, 2f);
            Debug.Log("[VBS][Juice] score card landed: reveal");
        }

        void HandleReset()
        {
            _pending = 0;
            _stampT = _hitT = null;
            _fx.Clear();
            if (_plaque != null) _plaque.ResetAll();
            if (_marquee != null) _marquee.ResetAll();
            PassthroughDim.Release(DimKey, 0f);
        }

        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            _liftedThisBeat = false;
            if (beat != GameBeat.Escape) _cardDone = false;
            if (beat == GameBeat.Pre) { HandleReset(); return; }
            if (_plaque != null) _plaque.OnBeat(prev, beat);
            if (!JuiceSwitches.Beats) return;
            bool leftStation = prev >= GameBeat.Smoker && prev <= GameBeat.Heat && beat > prev;
            if (leftStation && _plaque != null) _plaque.Cleared();
            if (leftStation && _marquee != null) _marquee.Chase(new Color(1f, 0.95f, 0.66f, 1f), 0.6f, 1f);
            if (beat == GameBeat.Escape && prev != GameBeat.Escape)
            {
                // comfort mode: no strobe, a slow gold chase instead
                if (_marquee != null) { if (StakesFlags.Comfort) _marquee.Chase(new Color(1f, 0.95f, 0.66f, 1f), 3.2f, 2f); else _marquee.Strobe(3.2f); }
                Vector3 from = _plaque != null && _plaque.Root != null && _plaque.Root.gameObject.activeInHierarchy ? _plaque.Root.position
                             : _hive != null ? _hive.DoorPoint.position : Vector3.zero;
                if (WindowPoint(out Vector3 win) && from != Vector3.zero)
                    for (int k = 0; k < 16; k++)
                        _fx.Fly(from, win, 0.3f, 1.2f, 0.4f + k * 0.07f, GoldBee, 0.02f, UnityEngine.Random.insideUnitSphere * 0.12f);
            }
        }

        // ------------------------------------------------------------------ the pick
        void Resolve()
        {
            bool right = _pending > 0;
            Transform t = _stampT != null && Time.time - _stampAt < 0.6f && _stampApproved == right ? _stampT : null;
            _pending = 0;
            _stampT = null;
            Outcome(right, t);
        }

        void Outcome(bool right, Transform prop)
        {
            if (_director == null) return;
            var beat = _director.Beat;
            if (beat < GameBeat.Smoker || beat >= GameBeat.Escape) return;
            Picks++;
            FindWorld();
            Vector3 origin = prop != null ? ItemCenter(prop) : FallbackOrigin();
            int slot = beat == GameBeat.Smoker ? 0 : beat == GameBeat.Feed ? 1 : beat == GameBeat.Wasps ? 2 : beat == GameBeat.Heat ? 3 : -1;
            Debug.Log("[VBS][Juice] " + (right ? "right" : "wrong") + " pick at " + beat + (prop != null ? " on " + prop.name : " (no prop: hive door)"));

            if (JuiceSwitches.Sting)
            {
                if (right)
                {
                    _fx.Burst(origin, 34, Pollen, 1.0f, 0.7f, 0.016f, 1.2f, 0.7f, 2.5f);
                    _fx.Burst(origin, 8, White, 1.3f, 0.35f, 0.008f, 0f, 0.3f, 5f);
                    var stakes = StakesDirector.Instance;
                    var swarm = stakes != null ? stakes.Swarm : null;
                    Vector3 from = swarm != null && swarm.Mode == DarkSwarmMode.Deck ? swarm.BandCenter : origin + Vector3.up * 0.1f;
                    if (WindowPoint(out Vector3 win))
                        for (int k = 0; k < 12; k++)
                            _fx.Fly(from + UnityEngine.Random.insideUnitSphere * 0.25f, win + UnityEngine.Random.insideUnitSphere * 0.06f,
                                    0.15f, 0.85f, k * 0.03f, GoldBee, 0.022f);
                }
                else
                {
                    _fx.Burst(origin, 16, Soot, 0.4f, 0.8f, 0.04f, -0.35f, 0.85f, 3f);
                    _fx.Burst(origin, 4, Spark, 0.3f, 0.55f, 0.012f, 2.4f, 0.4f, 3f); // a few pieces fall: no red confetti
                }
            }
            if (!right && JuiceSwitches.Stamps)
            {
                if (prop != null && !StakesFlags.Comfort) AddPunch(prop, true, 0.25f);
                if (!StakesFlags.Comfort) // comfort mode: the room never blinks
                {
                    PassthroughDim.Request(DimKey, 0.3f, 0.05f);
                    _dimReleaseAt = Time.time + 0.12f;
                }
            }
            if (right && slot >= 0 && _plaque != null) _plaque.Fly(slot, origin);
            if (_marquee != null) _marquee.Sting(right);
            if (_bw != null) _bw.Pick(right);
        }

        static Vector3 ItemCenter(Transform t)
        {
            var di = t.GetComponent<DeliveryItem>();
            if (di != null && di.measured) return t.TransformPoint(di.localBounds.center);
            return t.position + Vector3.up * 0.05f;
        }

        Vector3 FallbackOrigin()
        {
            if (_hive != null) return _hive.DoorPoint.position;
            var cam = Camera.main;
            return cam != null ? cam.transform.position + cam.transform.forward * 0.6f : Vector3.zero;
        }

        bool WindowPoint(out Vector3 p)
        {
            p = default;
            if (_window == null || !_window.isActiveAndEnabled) return false;
            var pane = _window.PaneTransform;
            if (pane == null) return false;
            p = pane.position;
            return true;
        }

        void FindWorld()
        {
            if (_window == null) _window = FindAnyObjectByType<FutureWindow>();
            if (_delivery == null)
            {
                _delivery = BeeDelivery.Instance != null ? BeeDelivery.Instance : FindAnyObjectByType<BeeDelivery>();
                if (_delivery != null) _delivery.Landed += HandleLanded;
            }
            if (_hive == null)
            {
                _hive = FindAnyObjectByType<HiveBody>();
                if (_hive != null) _hive.Lifted += HandleLifted;
            }
        }

        // ------------------------------------------------------------------ punches
        void AddPunch(Transform t, bool shake, float dur)
        {
            for (int i = 0; i < _np; i++)
                if (_punch[i].t == t && _punch[i].shake == shake) { _punch[i].start = Time.time; _punch[i].dur = dur; return; }
            if (_np >= _punch.Length) return;
            _punch[_np++] = new Punch
            {
                t = t, shake = shake, start = Time.time, dur = dur,
                written = shake ? t.localPosition : t.localScale, applied = shake ? Vector3.zero : Vector3.one,
            };
        }

        static readonly Vector3 Squash = new Vector3(1.07f, 0.88f, 1.07f), Stretch = new Vector3(0.97f, 1.04f, 0.97f);

        void RunPunches(float now)
        {
            var cam = Camera.main;
            Vector3 side = cam != null ? cam.transform.right : Vector3.right;
            for (int i = 0; i < _np;)
            {
                ref var p = ref _punch[i];
                if (p.t == null) { _punch[i] = _punch[--_np]; continue; }
                float k = Mathf.Clamp01((now - p.start) / Mathf.Max(0.01f, p.dur));
                if (p.shake)
                {
                    Vector3 cur = p.t.localPosition;
                    Vector3 basePos = (cur - p.written).sqrMagnitude < 1e-12f ? cur - p.applied : cur;
                    Vector3 off = Vector3.zero;
                    if (k < 1f)
                    {
                        Vector3 w = side * (Mathf.Sin(k * Mathf.PI * 7f) * 0.01f * (1f - k));
                        off = p.t.parent != null ? p.t.parent.InverseTransformVector(w) : w;
                    }
                    p.t.localPosition = basePos + off;
                    p.written = p.t.localPosition;
                    p.applied = off;
                }
                else
                {
                    Vector3 cur = p.t.localScale;
                    Vector3 baseS = (cur - p.written).sqrMagnitude < 1e-12f ? Div(cur, p.applied) : cur;
                    Vector3 f = k < 0.35f ? Vector3.Lerp(Vector3.one, Squash, k / 0.35f)
                              : k < 0.7f ? Vector3.Lerp(Squash, Stretch, (k - 0.35f) / 0.35f)
                              : Vector3.Lerp(Stretch, Vector3.one, (k - 0.7f) / 0.3f);
                    if (k >= 1f) f = Vector3.one;
                    p.t.localScale = Vector3.Scale(baseS, f);
                    p.written = p.t.localScale;
                    p.applied = f;
                }
                if (k >= 1f) { _punch[i] = _punch[--_np]; continue; }
                i++;
            }
        }

        static Vector3 Div(Vector3 a, Vector3 b) =>
            new Vector3(a.x / (Mathf.Abs(b.x) > 1e-4f ? b.x : 1f), a.y / (Mathf.Abs(b.y) > 1e-4f ? b.y : 1f), a.z / (Mathf.Abs(b.z) > 1e-4f ? b.z : 1f));

        // ------------------------------------------------------------------ frame
        void Update()
        {
            float now = Time.time;
            if (now >= _nextFind && (_window == null || _hive == null || _delivery == null)) { _nextFind = now + 2f; FindWorld(); }

            // a pick with no stamp (delivery off, buttons, Board): resolve from the hive door
            if (_pending != 0 && now - _pendingAt > 0.25f) Resolve();

            if (_hitT != null && now >= _hitAt)
            {
                var t = _hitT;
                _hitT = null;
                AddPunch(t, false, 0.22f);
                var cam = Camera.main;
                Vector3 c = ItemCenter(t);
                Vector3 toHead = cam != null ? (cam.transform.position - c).normalized : Vector3.back;
                _fx.Ring(c + toHead * 0.07f, toHead, 16, _hitApproved ? InkApproved : InkGrievance, 1.1f, 0.32f, 0.011f);
            }
            if (_dimReleaseAt > 0f && now >= _dimReleaseAt) { _dimReleaseAt = -9f; PassthroughDim.Release(DimKey, 0.4f); }

            // the lift invitation: a gold ring round the hive foot while it may be lifted and isn't yet
            if (JuiceSwitches.Sting && _hive != null && _hive.LiftEnabled && !_hive.Held && !_liftedThisBeat && now >= _nextLiftInvite)
            {
                _nextLiftInvite = now + 1.6f;
                Vector3 foot = _hive.transform.position + Vector3.up * 0.01f;
                _fx.Ring(foot, Vector3.up, 18, new Color(1f, 0.78f, 0.2f, 1f), 1.4f, 0.6f, 0.012f);
            }
        }

        void LateUpdate() => RunPunches(Time.time);
    }
}
