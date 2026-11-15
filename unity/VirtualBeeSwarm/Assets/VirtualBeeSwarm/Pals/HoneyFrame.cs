using System;
using System.Collections;
using UnityEngine;
using VirtualBeeSwarm.Juice;

namespace VirtualBeeSwarm.Pals
{
    /// <summary>
    /// Bonus honey harvest after the ride (the map beat, once the unload is done and Beeswax is quiet; never while the
    /// autopilot runs, never twice in a run). A honey frame rises out of the hive and floats to arm's reach, glowing:
    ///   1 pinch its top bar to take it (it follows the pinch),
    ///   2 scrape the wax caps off the comb with a fingertip of the other hand (they pop off in flakes); hold it a few
    ///     seconds without scraping and Beeswax flies over and scrapes it for you,
    ///   3 hold it over the jar: honey drips and the jar fills (~3.5 s),
    ///   4 Beeswax: leave the rest, the bees eat it all winter; the frame floats back into the hive, the jar stays.
    /// Let go (stop pinching) and the frame waits in the air where you left it. Nothing here touches the ride.
    /// Env VBS_HONEY_DEMO=1 (captures) offers it as soon as the map beat starts, autopilot or not, and the ghost hand
    /// plays it (PalDemo); F9 off the headset offers it in any beat after the hook. Logs "[VBS][Honey] ...".
    /// </summary>
    public class HoneyFrame : MonoBehaviour
    {
        public static HoneyFrame Instance { get; private set; }
        const int CapCols = 6, CapRows = 3;
        const float W = 0.32f, H = 0.19f, FillSeconds = 3.5f;

        enum State { Off, Rising, Waiting, Held, Done }
        State _state;
        bool _offeredThisRun;
        Transform _root, _frame, _jar, _fill, _glow;
        readonly Transform[] _caps = new Transform[CapCols * CapRows];
        Material _wood, _honey, _wax, _glass, _glowMat;
        int _scraped, _hand = -1;
        float _stateAt, _fillK, _nextDrip, _heldSince, _helpAt = -1f;
        bool _helped, _helping;
        Vector3 _grabOffset, _from, _reach;
        Quaternion _frameRot;
        GameDirector _director;
        GameNet _net;
        BeeswaxController _bw;
        JuiceParticles _fx;
        LevelMap _map;
        GameAutopilot _autopilot;

        public bool Active => _state != State.Off && _state != State.Done;
        public bool Held => _state == State.Held;
        public bool Ready => _state == State.Waiting;
        public float Scraped => _scraped / (float)_caps.Length;
        public float Fill => _fillK;
        public Vector3 TopBar => _frame != null ? _frame.TransformPoint(new Vector3(0f, H * 0.5f + 0.012f, 0f)) : Vector3.zero;
        public Vector3 JarTop => _jar != null ? _jar.position + Vector3.up * 0.13f : Vector3.zero;
        public Vector3 Position => _frame != null ? _frame.position : Vector3.zero;
        static bool Demo => Environment.GetEnvironmentVariable("VBS_HONEY_DEMO") == "1";

        void Awake() => Instance = this;

        void Start()
        {
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            _net = FindAnyObjectByType<GameNet>();
            _bw = FindAnyObjectByType<BeeswaxController>();
            _fx = FindAnyObjectByType<JuiceParticles>();
            _map = FindAnyObjectByType<LevelMap>();
            _autopilot = FindAnyObjectByType<GameAutopilot>(FindObjectsInactive.Include);
            if (_director != null) _director.BeatChanged += OnBeat;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_director != null) _director.BeatChanged -= OnBeat;
            foreach (var m in new[] { _wood, _honey, _wax, _glass, _glowMat }) if (m != null) Destroy(m);
        }

        void OnBeat(GameBeat prev, GameBeat beat)
        {
            if (beat == GameBeat.Pre) _offeredThisRun = false;
            if (beat != GameBeat.Map && beat != GameBeat.Field && _root != null && _root.gameObject.activeSelf) Hide("beat " + GameIds.BeatName(beat));
        }

        // ------------------------------------------------------------------ offer
        void Update()
        {
            if (_director == null) return;
            if (_state == State.Off)
            {
                if (PalHands.OffHeadset && GameInput.KeyDown(KeyCode.F9) && _director.Beat > GameBeat.Hook) Offer("key F9");
                else if (!_offeredThisRun && _director.Beat == GameBeat.Map && PalBeeswax.On)
                {
                    bool auto = _autopilot != null && _autopilot.Running;
                    if (Demo) Offer("demo");
                    else if (!auto && _map != null && _map.UnloadDone && (_net == null || !_net.IsSpeaking)) Offer("unload done");
                }
                return;
            }
            Step();
        }

        public void Offer(string why)
        {
            var hive = FindAnyObjectByType<HiveBody>();
            if (hive == null || !hive.isActiveAndEnabled) return;
            Build();
            _offeredThisRun = true;
            Transform head = Head();
            Vector3 eye = head != null ? head.position : Vector3.up * 1.5f;
            var del = BeeDelivery.Instance;
            float top = del != null ? del.Register(hive.transform).Top : 0.3f;
            Vector3 hp = hive.transform.position;
            Vector3 toHive = hp - eye;
            toHive.y = 0f;
            Vector3 dir = toHive.sqrMagnitude > 1e-4f ? toHive.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            _from = hp + Vector3.up * (top - H * 0.3f);
            _reach = eye + dir * 0.48f - right * 0.08f - Vector3.up * 0.2f;
            _frameRot = Quaternion.LookRotation(dir, Vector3.up);
            _frame.SetPositionAndRotation(_from, _frameRot);
            // the jar: a little to the right, at waist height, between you and the hive
            _jar.position = eye + dir * 0.45f + right * 0.24f - Vector3.up * 0.62f;
            _jar.rotation = Quaternion.LookRotation(dir, Vector3.up);
            _root.gameObject.SetActive(true);
            for (int i = 0; i < _caps.Length; i++) _caps[i].gameObject.SetActive(true);
            _scraped = 0;
            _fillK = 0f;
            _fill.localScale = new Vector3(0.08f, 0.0001f, 0.08f);
            _helped = _helping = false;
            _helpAt = -1f;
            _hand = -1;
            Set(State.Rising);
            Say(PalLines.HoneyOffer);
            Log("offered (" + why + "): the frame rises out of the hive");
        }

        void Hide(string why)
        {
            if (_root != null) _root.gameObject.SetActive(false);
            _state = State.Off;
            Log("put away (" + why + ")");
        }

        void Set(State s)
        {
            _state = s;
            _stateAt = Time.time;
        }

        // ------------------------------------------------------------------ frame
        void Step()
        {
            float t = Time.time, since = t - _stateAt;
            var hands = PalHands.Instance;
            Transform head = Head();
            if (head != null && _state != State.Held)
            {
                Vector3 f = _frame.position - head.position;
                f.y = 0f;
                if (f.sqrMagnitude > 1e-4f) _frameRot = Quaternion.Slerp(_frameRot, Quaternion.LookRotation(f.normalized, Vector3.up), 1f - Mathf.Exp(-Time.deltaTime * 3f));
            }
            _glow.gameObject.SetActive(_state == State.Rising || _state == State.Waiting);
            if (_glow.gameObject.activeSelf) _glowMat.color = new Color(1f, 0.85f, 0.35f, 0.25f + 0.2f * Mathf.Sin(t * 4f));

            switch (_state)
            {
                case State.Rising:
                {
                    // up out of the hive, then over to arm's reach
                    float k1 = Mathf.Clamp01(since / 1.2f), k2 = Mathf.Clamp01((since - 1.2f) / 1.5f);
                    Vector3 up = _from + Vector3.up * (H + 0.08f) * Mathf.SmoothStep(0f, 1f, k1);
                    Vector3 p = Vector3.Lerp(up, _reach, Mathf.SmoothStep(0f, 1f, k2)) + Vector3.up * (Mathf.Sin(k2 * Mathf.PI) * 0.06f);
                    _frame.SetPositionAndRotation(p, _frameRot);
                    if (k2 >= 1f) Set(State.Waiting);
                    TryGrab(hands);
                    break;
                }
                case State.Waiting:
                    _frame.SetPositionAndRotation(_frame.position + Vector3.up * (Mathf.Sin(t * 1.6f) * 0.0006f), _frameRot);
                    TryGrab(hands);
                    break;
                case State.Held:
                    Held_(hands);
                    break;
                case State.Done:
                {
                    // back into the hive
                    float k = Mathf.Clamp01(since / 1.4f);
                    _frame.SetPositionAndRotation(Vector3.Lerp(_frame.position, _from, Mathf.SmoothStep(0f, 1f, k) * 0.2f), _frameRot);
                    _frame.localScale = Vector3.one * (1f - k * k);
                    if (k >= 1f) { _frame.gameObject.SetActive(false); Set(State.Off); _state = State.Off; }
                    break;
                }
            }
            if (_state != State.Off) Scrape(hands);
            Drip();
        }

        void TryGrab(PalHands hands)
        {
            if (hands == null) return;
            for (int h = 0; h < 2; h++)
            {
                var hand = hands.Hands[h];
                if (!hand.tracked || !hand.pinch) continue;
                Vector3 local = _frame.InverseTransformPoint(hand.tip);
                bool onBar = Mathf.Abs(local.y - (H * 0.5f + 0.012f)) < 0.06f && Mathf.Abs(local.x) < W * 0.5f + 0.06f && Mathf.Abs(local.z) < 0.08f;
                if (!onBar) continue;
                _hand = h;
                _grabOffset = _frame.position - hand.tip;
                _heldSince = Time.time;
                if (_helpAt < 0f) _helpAt = Time.time + 5f;
                Set(State.Held);
                if (_scraped == 0) Say(PalLines.HoneyCaps);
                Log("frame taken (" + (hand.ghost ? "ghost" : ((GameHand)h).ToString().ToLowerInvariant()) + ")");
                return;
            }
        }

        void Held_(PalHands hands)
        {
            var hand = hands != null && _hand >= 0 ? hands.Hands[_hand] : default;
            if (!hand.tracked || !hand.pinch)
            {
                Set(State.Waiting); // let go: it waits in the air
                Log("frame let go");
                return;
            }
            Transform head = Head();
            Vector3 p = hand.tip + _grabOffset;
            if (head != null)
            {
                Vector3 f = p - head.position;
                f.y = 0f;
                if (f.sqrMagnitude > 1e-4f) _frameRot = Quaternion.LookRotation(f.normalized, Vector3.up);
            }
            _frame.SetPositionAndRotation(p, _frameRot);
            // stuck scraping? Beeswax does it
            if (!_helped && _helpAt > 0f && Time.time >= _helpAt && Scraped < 0.5f && _bw != null && !_bw.Inside) StartCoroutine(Help());
        }

        /// <summary>Beeswax flies over and sweeps the caps off, row by row.</summary>
        IEnumerator Help()
        {
            _helped = _helping = true;
            Say(PalLines.HoneyHelp, force: true);
            Log("Beeswax scrapes the caps for you");
            var goalT = _bw.GoalTransform;
            Vector3 goal = _bw.Goal;
            float r = PalBeeswax.Radius();
            for (int row = 0; row < CapRows && _state == State.Held; row++)
            {
                for (int c = 0; c < CapCols && _state == State.Held; c++)
                {
                    int i = row * CapCols + (row % 2 == 0 ? c : CapCols - 1 - c);
                    var cap = _caps[i];
                    _bw.FlyTo(cap.position + Vector3.up * (r * 0.7f + 0.03f) + _frame.forward * 0.02f); // just over the comb, sweeping along
                    float until = Time.time + 0.22f;
                    while (Time.time < until) yield return null;
                    PopCap(i);
                }
            }
            if (goalT != null) _bw.FlyTo(goalT);
            else _bw.FlyTo(goal);
            _helping = false;
        }

        void Scrape(PalHands hands)
        {
            if (hands == null || _scraped >= _caps.Length) return;
            for (int h = 0; h < 2; h++)
            {
                if (_state == State.Held && h == _hand) continue; // the holding hand can't scrape its own frame
                var hand = hands.Hands[h];
                if (!hand.tracked) continue;
                Vector3 local = _frame.InverseTransformPoint(hand.tip);
                if (Mathf.Abs(local.z) > 0.05f || Mathf.Abs(local.x) > W * 0.5f || Mathf.Abs(local.y) > H * 0.5f) continue;
                for (int i = 0; i < _caps.Length; i++)
                    if (_caps[i].gameObject.activeSelf && (Vector3.Distance(_caps[i].position, hand.tip) < 0.035f)) PopCap(i);
            }
        }

        void PopCap(int i)
        {
            var cap = _caps[i];
            if (!cap.gameObject.activeSelf) return;
            cap.gameObject.SetActive(false);
            _scraped++;
            if (_fx != null) _fx.Burst(cap.position, 3, new Color(0.98f, 0.92f, 0.7f), 0.3f, 0.5f, 0.008f, 2f);
            if (_scraped == _caps.Length) Log("all caps off: hold it over the jar");
        }

        void Drip()
        {
            if (_state == State.Off || _state == State.Done || _jar == null) return;
            if (Scraped < 0.7f) return;
            Vector3 bottom = _frame.TransformPoint(new Vector3(0f, -H * 0.5f, 0f));
            Vector3 jarTop = _jar.position + Vector3.up * 0.12f;
            Vector3 d = bottom - jarTop;
            float flat = new Vector2(d.x, d.z).magnitude;
            if (flat > 0.12f || d.y < 0.0f || d.y > 0.5f) return;
            float t = Time.time;
            if (t >= _nextDrip && _fx != null)
            {
                _nextDrip = t + 0.07f;
                _fx.Burst(bottom + new Vector3(UnityEngine.Random.Range(-0.04f, 0.04f), 0f, UnityEngine.Random.Range(-0.01f, 0.01f)), 1,
                          new Color(1f, 0.66f, 0.1f), 0.02f, Mathf.Clamp(Mathf.Sqrt(2f * Mathf.Max(0.02f, d.y) / 3f), 0.15f, 0.6f), 0.012f, 3f);
            }
            _fillK = Mathf.Min(1f, _fillK + Time.deltaTime / FillSeconds);
            _fill.localScale = new Vector3(0.08f, Mathf.Max(0.0001f, 0.1f * _fillK), 0.08f);
            _fill.localPosition = new Vector3(0f, 0.012f + 0.05f * _fillK, 0f);
            if (_fillK >= 1f)
            {
                Set(State.Done);
                Say(PalLines.HoneyWinter, force: true);
                if (_fx != null) _fx.Ring(jarTop, Vector3.up, 12, new Color(1f, 0.8f, 0.3f), 0.5f, 0.6f, 0.012f);
                GameSound.Cue("honey_jar_full", _jar);
                Log("jar full: the frame goes back to the hive (" + _scraped + "/" + _caps.Length + " caps off)");
            }
        }

        // ------------------------------------------------------------------ build (primitives, a few draws)
        void Build()
        {
            if (_root != null) { _frame.gameObject.SetActive(true); _frame.localScale = Vector3.one; return; }
            _wood = GameMaterials.Create("VBS_HoneyWood (runtime)", new Color(0.78f, 0.6f, 0.38f));
            _honey = GameMaterials.Create("VBS_Honey (runtime)", new Color(0.96f, 0.62f, 0.08f));
            _wax = GameMaterials.Create("VBS_HoneyCap (runtime)", new Color(0.99f, 0.93f, 0.7f));
            _glass = GameMaterials.Create("VBS_HoneyJar (runtime)", new Color(0.85f, 0.93f, 1f, 0.35f), GameMaterials.Blend.Alpha, doubleSided: true);
            _glowMat = GameMaterials.Create("VBS_HoneyGlow (runtime)", new Color(1f, 0.85f, 0.35f, 0.3f), GameMaterials.Blend.Alpha, doubleSided: true);
            _root = new GameObject("HoneyHarvest").transform;
            _root.SetParent(transform, false);
            _frame = new GameObject("HoneyFrame").transform;
            _frame.SetParent(_root, false);
            Box(_frame, new Vector3(0f, H * 0.5f + 0.01f, 0f), new Vector3(W + 0.07f, 0.02f, 0.028f), _wood);   // top bar with lugs
            Box(_frame, new Vector3(-W * 0.5f, 0f, 0f), new Vector3(0.014f, H, 0.024f), _wood);
            Box(_frame, new Vector3(W * 0.5f, 0f, 0f), new Vector3(0.014f, H, 0.024f), _wood);
            Box(_frame, new Vector3(0f, -H * 0.5f, 0f), new Vector3(W, 0.012f, 0.024f), _wood);
            Box(_frame, Vector3.zero, new Vector3(W - 0.014f, H - 0.012f, 0.018f), _honey);                    // comb
            float cw = (W - 0.02f) / CapCols, ch = (H - 0.02f) / CapRows;
            for (int r = 0; r < CapRows; r++)
            for (int c = 0; c < CapCols; c++)
            {
                Vector3 p = new Vector3(-W * 0.5f + 0.01f + cw * (c + 0.5f), -H * 0.5f + 0.01f + ch * (r + 0.5f), -0.011f); // the side facing you
                _caps[r * CapCols + c] = Box(_frame, p, new Vector3(cw - 0.006f, ch - 0.006f, 0.005f), _wax);
            }
            _glow = Box(_frame, Vector3.zero, new Vector3(W + 0.06f, H + 0.06f, 0.002f), _glowMat);
            _glow.localPosition = new Vector3(0f, 0f, 0.02f);
            // the jar
            _jar = new GameObject("HoneyJar").transform;
            _jar.SetParent(_root, false);
            var coaster = Prim(PrimitiveType.Cylinder, _jar, new Vector3(0f, -0.004f, 0f), new Vector3(0.14f, 0.004f, 0.14f), _wood);
            coaster.name = "Coaster";
            Prim(PrimitiveType.Cylinder, _jar, new Vector3(0f, 0.06f, 0f), new Vector3(0.095f, 0.06f, 0.095f), _glass);
            _fill = Prim(PrimitiveType.Cylinder, _jar, new Vector3(0f, 0.012f, 0f), new Vector3(0.08f, 0.0001f, 0.08f), _honey);
            Prim(PrimitiveType.Cylinder, _jar, new Vector3(0f, 0.122f, 0f), new Vector3(0.1f, 0.006f, 0.1f), _wood); // the rim
            _root.gameObject.SetActive(false);
        }

        Transform Box(Transform parent, Vector3 pos, Vector3 size, Material m) => Prim(PrimitiveType.Cube, parent, pos, size, m);

        Transform Prim(PrimitiveType type, Transform parent, Vector3 pos, Vector3 scale, Material m)
        {
            var go = GameObject.CreatePrimitive(type);
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        // ------------------------------------------------------------------ helpers
        float _nextLine;
        void Say(GameLine line, bool force = false)
        {
            if (_net == null) return;
            if (!force && (_net.IsSpeaking || Time.time < _nextLine)) return;
            _nextLine = Time.time + 4f;
            _net.Say(line);
        }

        Transform Head()
        {
            var del = BeeDelivery.Instance;
            if (del != null && del.Head != null) return del.Head;
            return Camera.main != null ? Camera.main.transform : null;
        }

        static void Log(string msg) => Debug.Log("[VBS][Honey] " + msg);
    }
}
