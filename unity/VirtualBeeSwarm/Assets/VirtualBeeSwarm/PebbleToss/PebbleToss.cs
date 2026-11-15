using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using VirtualBeeSwarm.HandsOn;
using VirtualBeeSwarm.Juice;
using VirtualBeeSwarm.Pals;

namespace VirtualBeeSwarm.Pebbles
{
    /// <summary>
    /// Pebble toss (heat station, after the pebble line): a clay bowl of 6 pebbles appears beside the player. Pinch one,
    /// toss or drop it into the dish (real Rigidbody pebbles: they bounce, roll and settle in the water). Every pebble
    /// that settles in the dish becomes a landing spot: a bee flies down from the hive door, lands on it and drinks.
    /// After 3 pebbles it ends with a caption cheer; after 12 s (20 s while a pebble is in the hand or in the air) the
    /// missing pebbles hop in by themselves, so the heat station never stalls. Bees can't swim: they need a place to stand.
    /// Self-installing; hooks in through HeatStation.PebbleTossRun (before the optional lines). Hands: PalHands (hand
    /// tracking index tip + pinch, controllers' trigger, or the ghost hand). Desktop/autopilot: the ghost hand tosses
    /// (F11 tosses one off the headset); VBS_PEBBLE_DEMO=1 forces the ghost and frames the "pal" capture camera.
    /// Off: VBS_PEBBLES=0. Budget: 6 pebbles x 64 tris, a 190-tri bowl, 6 bees x ~100 tris, one material, 6 rigidbodies.
    /// </summary>
    [DefaultExecutionOrder(110)] // after PalHands (-30) and PalCam (100)
    public sealed class PebbleToss : MonoBehaviour
    {
        public static PebbleToss Instance { get; private set; }

        const int Max = 6, Need = 3;
        const float InviteSeconds = 12f, PlayerCapSeconds = 20f, HardCapSeconds = 30f;
        const float ThrowBoost = 1.25f, MaxThrow = 7f, FlightSeconds = 1.3f;

        enum P { Bowl, Held, Flying, InDish, Gone }

        struct Peb
        {
            public P state;
            public int slot, hand;
            public float at, still, entered;
            public bool splashed, ghost;
        }

        readonly Peb[] _p = new Peb[Max];
        readonly Transform[] _pt = new Transform[Max];
        readonly Rigidbody[] _rb = new Rigidbody[Max];
        readonly SphereCollider[] _col = new SphereCollider[Max];
        readonly Vector3[] _slotLocal = new Vector3[Max];

        // bees: one per pebble index
        readonly Transform[] _bee = new Transform[Max];
        readonly int[] _beeState = new int[Max]; // 0 off, 1 flying in, 2 drinking, 3 leaving
        readonly float[] _beeAt = new float[Max];
        readonly Vector3[] _beeFrom = new Vector3[Max], _beeFace = new Vector3[Max];

        // hands
        const int Hist = 10;
        readonly Vector3[,] _hPos = new Vector3[2, Hist];
        readonly float[,] _hTime = new float[2, Hist];
        readonly int[] _hN = new int[2], _hHeld = { -1, -1 };
        readonly bool[] _hPinch = new bool[2];
        readonly float[] _hOffSince = { -1f, -1f }, _hLostAt = { -1f, -1f };
        const float PinchDropSeconds = 0.05f, LostHoldSeconds = 0.25f; // hand tracking drops pinch / the hand for a frame or two
        readonly Vector3[] _landTo = new Vector3[Max], _aimTo = new Vector3[Max]; // dish-local settle / aim spots (kept clear of other bee spots)

        Transform _bowlRoot, _bowlCol, _dishRoot, _dish;
        GameObject _catch;
        Material _mat;
        Mesh _disc;
        PhysicsMaterial _pebMat, _dishMat, _floorMat;
        HandsOnPrompt _card;
        JuiceParticles _fx;
        GameDirector _director;
        GameNet _net;
        HiveBody _hive;
        HeatStation _station;
        Transform _palCam;

        bool _running, _pilot, _ghostAuto, _finished;
        int _landed, _thrown, _misses, _grabs, _autoFilled;
        float _capOrig = -1f, _builtR = -1f;
        float _startedAt, _r, _beeLen, _dishR, _floorTop, _rimH, _waterY, _bowlR, _catchY, _cardHideAt = -1f, _camUntil = -1f;
        Coroutine _ghostCo;
        static bool _enabled = true, _demo;

        // ------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            try
            {
                string e = Environment.GetEnvironmentVariable("VBS_PEBBLES");
                if (e == "0" || e == "off") _enabled = false;
                _demo = Environment.GetEnvironmentVariable("VBS_PEBBLE_DEMO") == "1";
            }
            catch (Exception) { }
            if (!_enabled || Instance != null || FindAnyObjectByType<GameDirector>() == null) return;
            new GameObject("[VBS] PebbleToss").AddComponent<PebbleToss>();
        }

        void Awake()
        {
            Instance = this;
            HeatStation.PebbleTossRun = Run; // PEBBLETOSS hook
            _mat = GameMaterials.Create("VBS_TossPebble (runtime)", Color.white, vertexColors: true, doubleSided: true);
            _disc = PebbleArt.Disc();
            _pebMat = new PhysicsMaterial("VBS_TossPebble") { bounciness = 0.45f, dynamicFriction = 0.55f, staticFriction = 0.6f, bounceCombine = PhysicsMaterialCombine.Average };
            _dishMat = new PhysicsMaterial("VBS_TossDish") { bounciness = 0.05f, dynamicFriction = 0.7f, staticFriction = 0.8f, bounceCombine = PhysicsMaterialCombine.Average };
            _floorMat = new PhysicsMaterial("VBS_TossFloor") { bounciness = 0.4f, dynamicFriction = 0.5f, staticFriction = 0.5f, bounceCombine = PhysicsMaterialCombine.Average };
            _fx = gameObject.AddComponent<JuiceParticles>();
            _card = HandsOnPrompt.Build(transform, "PebbleCard");
            _director = FindAnyObjectByType<GameDirector>();
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            RestoreCap();
            Instance = null;
            if (HeatStation.PebbleTossRun == (Func<HeatStation, IEnumerator>)Run) HeatStation.PebbleTossRun = null;
        }

        static readonly FieldInfo CapsField = typeof(GameDirector).GetField("beatCapSeconds", BindingFlags.NonPublic | BindingFlags.Instance);

        float[] Caps() => _director != null && CapsField != null ? CapsField.GetValue(_director) as float[] : null;

        /// <summary>The heat beat's own cap (65 s), or 0 when it can't be read.</summary>
        float HeatCap()
        {
            var caps = Caps();
            int h = (int)GameBeat.Heat;
            if (caps == null || h >= caps.Length) return 0f;
            return _capOrig >= 0f ? _capOrig : caps[h];
        }

        /// <summary>Only while the toss runs: the heat cap gets the toss's seconds, so a Solved timeout can't cut it. Restored on leaving heat.</summary>
        void ExtendCap()
        {
            var caps = Caps();
            int h = (int)GameBeat.Heat;
            if (caps == null || h >= caps.Length || caps[h] <= 0f) { Debug.LogWarning("[VBS][Pebble] could not extend the heat beat cap (GameDirector.beatCapSeconds not found)"); return; }
            if (_capOrig < 0f) _capOrig = caps[h];
            caps[h] = Mathf.Max(_capOrig, _director.BeatSeconds + HardCapSeconds + 24f);
            Debug.Log("[VBS][Pebble] heat beat cap " + _capOrig.ToString("0") + " -> " + caps[h].ToString("0") + " s for the toss");
        }

        void RestoreCap()
        {
            if (_capOrig < 0f) return;
            var caps = Caps();
            int h = (int)GameBeat.Heat;
            if (caps != null && h < caps.Length) caps[h] = _capOrig;
            _capOrig = -1f;
        }

        // ------------------------------------------------------------ the toss (runs in HeatStation.AfterSolved)
        IEnumerator Run(HeatStation st)
        {
            if (!_enabled || st == null) yield break;
            _station = st;
            _dish = st.PebbleDishTransform;
            if (_dish == null || !_dish.gameObject.activeInHierarchy) { Debug.Log("[VBS][Pebble] no dish out: skipped"); yield break; }
            if (_director == null) _director = FindAnyObjectByType<GameDirector>();
            float cap = HeatCap();
            if (_director != null && cap > 0f && _director.BeatSeconds >= cap - 1f) { Debug.Log("[VBS][Pebble] heat beat already timed out (Beeswax picked): skipped"); yield break; }
            // a delivered dish may still be settling: wait until it holds still (max 2 s)
            Vector3 last = _dish.position;
            float calm = 0f, t0 = Time.time;
            while (calm < 0.3f && Time.time - t0 < 2f)
            {
                yield return null;
                if (_dish == null) yield break;
                calm = (_dish.position - last).sqrMagnitude < 1e-6f ? calm + Time.deltaTime : 0f;
                last = _dish.position;
            }
            if (!Begin()) yield break;

            // invite (caption-only line, only when Beeswax is free; never on the autopilot's clip-checked path)
            if (!_pilot || _demo)
                yield return SayWhenFree(RideLines.Get("room_heat_pebble_toss", "Toss in more pebbles. Bees can't swim. They drink standing up, like at a bar."), 2.5f);
            if (_ghostAuto) _ghostCo = StartCoroutine(GhostTosses());

            string reason = "3 pebbles";
            while (_running && _landed < Need)
            {
                float age = Time.time - _startedAt;
                if (age > HardCapSeconds) { reason = "hard cap"; break; }
                if (age > InviteSeconds && (!AnyInPlay() || age > PlayerCapSeconds)) { reason = "timeout"; break; }
                yield return null;
            }
            if (!_running) yield break;
            if (_landed < Need)
            {
                StopGhost();
                yield return AutoFill();
                float w = Time.time;
                while (_running && _landed < Need && Time.time - w < 4f) yield return null;
                if (!_running) yield break;
            }
            Finish(reason);
            var cheer = RideLines.Get("room_heat_pebble_cheer", "Bar's open. First round's on the hive.");
            if (!_pilot || _demo) yield return SayWhenFree(cheer, 3f, wait: true);
            else yield return Wait(1.2f);
        }

        bool Begin()
        {
            if (_director == null) _director = FindAnyObjectByType<GameDirector>();
            _net = FindAnyObjectByType<GameNet>();
            _hive = FindAnyObjectByType<HiveBody>();
            var ap = FindAnyObjectByType<GameAutopilot>(); // every built scene has one (F2): only a running one counts
            _pilot = ap != null && ap.Running;
            _ghostAuto = _pilot || (_demo && PalHands.OffHeadset);
            Reclaim();
            if (!MeasureDish()) { Debug.LogWarning("[VBS][Pebble] dish has no visible mesh: skipped"); return false; }
            if (_bowlRoot != null && Mathf.Abs(_builtR - _r) > 0.0005f) DropBowl(); // a replay with another dish size
            BuildBowl();
            ExtendCap();
            BuildCatch();
            for (int i = 0; i < Max; i++) ToSlot(i, i, grow: true, delay: 0.08f * i);
            _landed = _thrown = _misses = _grabs = _autoFilled = 0;
            _hHeld[0] = _hHeld[1] = -1;
            _hOffSince[0] = _hOffSince[1] = _hLostAt[0] = _hLostAt[1] = -1f;
            _finished = false;
            _running = true;
            _startedAt = Time.time;
            _camUntil = _demo ? float.MaxValue : -1f;
            ShowCard("PINCH A PEBBLE.\nTOSS IT IN THE DISH.");
            Debug.Log("[VBS][Pebble] start: dish r " + (_dishR * 100f).ToString("0.0") + " cm at " + _dishRoot.position.ToString("F2") +
                      ", water y " + _waterY.ToString("0.000") + ", bowl at " + _bowlRoot.position.ToString("F2") + ", pebble r " +
                      (_r * 100f).ToString("0.0") + " cm, bee " + (_beeLen * 100f).ToString("0.0") + " cm, catch floor y " + _catchY.ToString("0.00") +
                      (_ghostAuto ? " (ghost hand)" : ""));
            return true;
        }

        /// <summary>The dish's world bounds, water level and an invisible basin (floor + rim) that follows it.</summary>
        bool MeasureDish()
        {
            var rs = _dish.GetComponentsInChildren<Renderer>(); // once per toss
            var b = new Bounds();
            bool any = false;
            float water = float.NaN;
            Bounds wb = default;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null || !r.enabled || r is ParticleSystemRenderer || r.transform.IsChildOf(transform)) continue;
                if (_dishRoot != null && r.transform.IsChildOf(_dishRoot)) continue;
                if (IsOurs(r.transform)) continue;
                if (r.name == "Water") { water = r.bounds.max.y; wb = r.bounds; continue; }
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any && float.IsNaN(water)) return false;
            // the water is the dish's footprint: the bounds of everything under the dish also hold its stamp card
            if (!float.IsNaN(water)) { b = wb; b.Encapsulate(new Vector3(wb.center.x, wb.min.y - 0.01f, wb.center.z)); }
            _dishR = Mathf.Clamp(Mathf.Max(b.extents.x, b.extents.z) * (float.IsNaN(water) ? 0.9f : 1.04f), 0.04f, 0.6f);
            float height = Mathf.Max(0.008f, b.size.y);
            _r = Mathf.Clamp(_dishR * 0.15f, 0.012f, 0.024f);
            _beeLen = Mathf.Clamp(_dishR * 0.26f, 0.02f, 0.05f);
            _waterY = float.IsNaN(water) ? b.min.y + height * 0.5f : water;
            float floorWorld = Mathf.Max(b.min.y + 0.002f, _waterY - _r * 0.9f);
            _rimH = Mathf.Max(_waterY - floorWorld + _r, _r * 1.4f) + _r;
            if (_dishRoot == null)
            {
                _dishRoot = new GameObject("PebbleTossBasin").transform;
            }
            _dishRoot.SetParent(null, false);
            for (int i = _dishRoot.childCount - 1; i >= 0; i--) Destroy(_dishRoot.GetChild(i).gameObject);
            _dishRoot.SetPositionAndRotation(new Vector3(b.center.x, floorWorld, b.center.z), Quaternion.Euler(0f, _dish.eulerAngles.y, 0f));
            _dishRoot.localScale = Vector3.one;
            _floorTop = 0f;
            PebbleArt.Basin(_dishRoot, _disc, _dishMat, _dishR * 0.97f, 0f, _rimH, Mathf.Max(0.008f, _r * 0.6f));
            _dishRoot.SetParent(_dish, true); // follows the dish (and the meadow's rig moves)
            _dishRoot.gameObject.SetActive(true);
            return true;
        }

        bool IsOurs(Transform t)
        {
            for (int i = 0; i < Max; i++) if (_pt[i] != null && t == _pt[i]) return true;
            return false;
        }

        // ------------------------------------------------------------ bowl + pebbles
        void BuildBowl()
        {
            Transform head = HandsOnKit.Head;
            Vector3 eye = head != null ? head.position : _dishRoot.position + Vector3.up * 0.6f - Vector3.forward * 0.6f;
            Vector3 toDish = _dishRoot.position - eye; toDish.y = 0f;
            Vector3 fwd = toDish.sqrMagnitude > 1e-4f ? toDish.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            float floor = Bloom.MeadowLayout.FloorY;
            if (floor > _dishRoot.position.y - 0.1f || floor < eye.y - 2.2f) floor = Mathf.Min(_dishRoot.position.y - 0.6f, eye.y - 1.5f);
            _catchY = floor;
            float y = Mathf.Max(eye.y - 0.5f, floor + 0.45f);
            Vector3 at = eye + fwd * 0.34f + right * 0.24f;
            at.y = y;

            if (_bowlRoot == null)
            {
                // under the heat station: BeeswaxBody treats the bowl, the stand and the pebbles as station obstacles
                _bowlRoot = new GameObject("PebbleBowl").transform;
                _builtR = _r;
                _bowlR = _r * 3.3f;
                var bowl = new GameObject("Bowl");
                bowl.transform.SetParent(_bowlRoot, false);
                bowl.transform.localScale = new Vector3(_bowlR, _bowlR * 0.9f, _bowlR);
                bowl.AddComponent<MeshFilter>().sharedMesh = PebbleArt.Bowl();
                var mr = bowl.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _bowlCol = new GameObject("BowlBasin").transform;
                _bowlCol.SetParent(_bowlRoot, false);
                PebbleArt.Basin(_bowlCol, _disc, _dishMat, _bowlR * 1.0f, _bowlR * 0.09f, _bowlR * 0.45f, 0.006f, 10);
                var stand = HandsOnKit.Prim(PrimitiveType.Cylinder, "Stand", _bowlRoot, Vector3.zero, Vector3.one, HandsOnKit.Wood);
                stand.layer = 2;
                for (int i = 0; i < Max; i++)
                {
                    float a = (i - 1) * Mathf.PI * 2f / (Max - 1);
                    _slotLocal[i] = i == 0 ? new Vector3(0f, _bowlR * 0.09f + _r * 0.6f, 0f)
                        : new Vector3(Mathf.Cos(a) * _bowlR * 0.55f, _bowlR * 0.17f + _r * 0.6f, Mathf.Sin(a) * _bowlR * 0.55f);
                    BuildPebble(i);
                    BuildBee(i);
                }
            }
            _bowlRoot.SetParent(_station != null ? _station.transform : null, false);
            _bowlRoot.SetPositionAndRotation(at, Quaternion.LookRotation(fwd, Vector3.up));
            var ls = _bowlRoot.parent != null ? _bowlRoot.parent.lossyScale : Vector3.one;
            _bowlRoot.localScale = new Vector3(1f / Mathf.Max(1e-4f, ls.x), 1f / Mathf.Max(1e-4f, ls.y), 1f / Mathf.Max(1e-4f, ls.z));
            var st = _bowlRoot.Find("Stand");
            if (st != null)
            {
                float h = Mathf.Max(0.05f, at.y - floor);
                st.localPosition = Vector3.down * (h * 0.5f);
                st.localScale = new Vector3(0.028f, h * 0.5f, 0.028f);
            }
            _bowlRoot.gameObject.SetActive(true);
        }

        /// <summary>Throw away the bowl, pebbles and bees (built for another pebble size); BuildBowl makes new ones.</summary>
        void DropBowl()
        {
            for (int i = 0; i < Max; i++)
            {
                if (_pt[i] != null) Destroy(_pt[i].gameObject);
                if (_bee[i] != null) Destroy(_bee[i].gameObject);
                _pt[i] = null; _bee[i] = null; _rb[i] = null; _col[i] = null;
                _beeState[i] = 0;
            }
            Destroy(_bowlRoot.gameObject);
            _bowlRoot = null;
            _bowlCol = null;
        }

        void BuildPebble(int i)
        {
            var go = new GameObject("TossPebble" + i) { layer = 2 };
            go.transform.SetParent(_bowlRoot, false);
            go.AddComponent<MeshFilter>().sharedMesh = PebbleArt.Pebble(i);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.mass = 0.03f;
            rb.linearDamping = 0.05f;
            rb.angularDamping = 1.5f;
            rb.maxDepenetrationVelocity = 1f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            var col = go.AddComponent<SphereCollider>();
            col.radius = 0.62f; // mesh units: the pebble is 1 wide, 0.6 tall
            col.sharedMaterial = _pebMat;
            _pt[i] = go.transform;
            _rb[i] = rb;
            _col[i] = col;
        }

        void BuildBee(int i)
        {
            var go = new GameObject("TossBee" + i);
            go.transform.SetParent(transform, false); // not under the station: tiny, never an obstacle for Beeswax
            go.AddComponent<MeshFilter>().sharedMesh = BeeMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.transform.localScale = Vector3.one * _beeLen;
            go.SetActive(false);
            _bee[i] = go.transform;
        }

        static Mesh _beeMesh;
        static Mesh BeeMesh => _beeMesh != null ? _beeMesh : (_beeMesh = GameMeshes.CreateBeeMesh());

        void BuildCatch()
        {
            if (_catch == null)
            {
                _catch = new GameObject("PebbleCatchFloor") { layer = 2 };
                _catch.transform.SetParent(transform, false);
                var bc = _catch.AddComponent<BoxCollider>();
                bc.size = new Vector3(5f, 0.2f, 5f);
                bc.sharedMaterial = _floorMat;
            }
            Vector3 c = _bowlRoot.position;
            _catch.transform.position = new Vector3(c.x, _catchY - 0.1f, c.z);
            _catch.SetActive(true);
        }

        /// <summary>Pebble i into bowl slot s (kinematic), optionally popping in.</summary>
        void ToSlot(int i, int s, bool grow, float delay = 0f)
        {
            var t = _pt[i];
            _rb[i].isKinematic = true;
            _rb[i].interpolation = RigidbodyInterpolation.None;
            t.SetParent(_bowlRoot, false);
            t.localPosition = _slotLocal[s];
            t.localRotation = Quaternion.Euler(0f, 37f * i, 0f);
            t.localScale = Vector3.one * (grow ? 0.001f : _r);
            t.gameObject.SetActive(true);
            _p[i] = new Peb { state = P.Bowl, slot = s, at = Time.time + delay, hand = -1 };
        }

        int FreeSlot()
        {
            for (int s = 0; s < Max; s++)
            {
                bool used = false;
                for (int i = 0; i < Max; i++) if (_p[i].state == P.Bowl && _p[i].slot == s && _pt[i] != null && _pt[i].gameObject.activeSelf) { used = true; break; }
                if (!used) return s;
            }
            return 0;
        }

        /// <summary>Pebbles left in the dish from an earlier run come home; bees off.</summary>
        void Reclaim()
        {
            for (int i = 0; i < Max; i++)
            {
                if (_pt[i] != null && _bowlRoot != null) { _pt[i].SetParent(_bowlRoot, false); _pt[i].gameObject.SetActive(false); _rb[i].isKinematic = true; }
                _p[i].state = P.Gone;
                if (_bee[i] != null) _bee[i].gameObject.SetActive(false);
                _beeState[i] = 0;
            }
        }

        // ------------------------------------------------------------ frame
        void Update()
        {
            UpdateBees();
            if (_cardHideAt > 0f && Time.time > _cardHideAt) { _cardHideAt = -1f; _card.Hide(); }
            bool inHeat = _director != null && _director.Beat == GameBeat.Heat;
            if (!inHeat && _capOrig >= 0f) RestoreCap();
            SlideLanded();
            if (!_running)
            {
                if (!inHeat && (_bowlRoot != null && _bowlRoot.gameObject.activeSelf || AnyBeeOut())) Cleanup();
                if (_director != null && _director.Beat == GameBeat.Pre && AnyInDish()) Reclaim(); // a new run
                return;
            }
            if (!inHeat || _station == null || _dish == null || !_dish.gameObject.activeInHierarchy) { Debug.Log("[VBS][Pebble] left the heat beat: cleanup"); EndPlay(); Cleanup(); return; }

            float now = Time.time;
            for (int i = 0; i < Max; i++)
            {
                ref var p = ref _p[i];
                var t = _pt[i];
                if (p.state == P.Bowl && t.localScale.x < _r * 0.999f)
                {
                    float k = Mathf.Clamp01((now - p.at) / 0.25f);
                    t.localScale = Vector3.one * Mathf.Max(0.001f, _r * k * (1f + 0.25f * Mathf.Sin(k * Mathf.PI)));
                    if (k >= 1f) t.localScale = Vector3.one * _r;
                }
                if (p.state == P.Gone && t.gameObject.activeSelf)
                {
                    float k = Mathf.Clamp01((now - p.at) / 0.25f);
                    t.localScale = Vector3.one * Mathf.Max(0.001f, _r * (1f - k));
                    if (k >= 1f) { t.gameObject.SetActive(false); ToSlot(i, FreeSlot(), grow: true, delay: 0.15f); }
                }
                if (p.state == P.Flying) TrackFlight(i, now);
            }
            Hands(now);
            if (PalHands.OffHeadset && GameInput.KeyDown(KeyCode.F11) && !_finished) { StopGhost(); _ghostCo = StartCoroutine(GhostToss()); }
        }

        void TrackFlight(int i, float now)
        {
            ref var p = ref _p[i];
            var t = _pt[i];
            Vector3 pos = t.position;
            bool over = InDishXZ(pos, 0.95f);
            float h = pos.y - _dishRoot.position.y;
            if (over && h < _rimH + _r * 2f)
            {
                if (p.entered <= 0f) p.entered = now;
                if (!p.splashed && pos.y < _waterY + _r && _rb[i].linearVelocity.y < 0.05f)
                {
                    p.splashed = true;
                    _fx.Ring(new Vector3(pos.x, _waterY + 0.002f, pos.z), Vector3.up, QuestPerf.Particles(10), HandsOnKit.Water, 0.25f, 0.45f, _r * 0.5f);
                    GameSound.Cue("bowl_splash", pos);
                }
            }
            else p.entered = 0f;
            float v = _rb[i].linearVelocity.magnitude;
            p.still = v < 0.06f ? p.still + Time.deltaTime : 0f;
            if (over && p.entered > 0f && (p.still > 0.2f || now - p.entered > 2.5f)) { Land(i); return; }
            bool lost = pos.y < _catchY - 0.3f || now - p.at > 6f || (!over && p.still > 0.45f);
            if (lost)
            {
                _misses++;
                Debug.Log("[VBS][Pebble] miss " + _misses + " (" + (pos.y < _catchY - 0.3f ? "fell through" : now - p.at > 6f ? "flew too long" : "settled outside the dish") + "): back to the bowl");
                Vanish(i);
            }
        }

        void Vanish(int i)
        {
            _rb[i].isKinematic = true;
            _rb[i].interpolation = RigidbodyInterpolation.None;
            _p[i].state = P.Gone;
            _p[i].at = Time.time;
        }

        bool InDishXZ(Vector3 p, float frac)
        {
            Vector3 d = p - _dishRoot.position;
            return d.x * d.x + d.z * d.z < (_dishR * frac) * (_dishR * frac);
        }

        void Land(int i)
        {
            ref var p = ref _p[i];
            p.state = P.InDish;
            _rb[i].isKinematic = true;
            _rb[i].interpolation = RigidbodyInterpolation.None;
            _pt[i].SetParent(_dish, true); // stays with the dish (the table keeps it after the station)
            _landTo[i] = _dish.InverseTransformPoint(ClearSpot(i, _pt[i].position));
            _landed++;
            if (!p.splashed) { p.splashed = true; _fx.Ring(_pt[i].position, Vector3.up, QuestPerf.Particles(8), HandsOnKit.Water, 0.2f, 0.4f, _r * 0.4f); }
            Debug.Log("[VBS][Pebble] landed " + _landed + "/" + Need + " after " + (Time.time - _startedAt).ToString("0.0") + " s" + (p.ghost ? " (ghost)" : ""));
            SendBee(i);
            if (!_finished) ShowCard("LANDING PADS  " + Mathf.Min(_landed, Need) + " / " + Need);
        }

        /// <summary>
        /// A settle spot near p that keeps a bee length clear of the other landed pebbles and the dish's own pebbles
        /// (so the bees don't pile up on one spot). Falls back to p.
        /// </summary>
        Vector3 ClearSpot(int i, Vector3 p)
        {
            float minD = _beeLen * 1.5f;
            Vector3 c = _dishRoot.position;
            float y = p.y;
            Vector3 best = p;
            float bestCost = float.MaxValue;
            for (int k = 0; k < 25; k++)
            {
                Vector3 q;
                if (k == 0) q = p;
                else
                {
                    float ring = (k <= 8 ? 0.35f : k <= 16 ? 0.58f : 0.8f) * _dishR * 0.9f;
                    float a = (k % 8) * Mathf.PI * 0.25f + (k > 8 ? 0.39f : 0f) + _dish.eulerAngles.y * Mathf.Deg2Rad;
                    q = new Vector3(c.x + Mathf.Cos(a) * ring, y, c.z + Mathf.Sin(a) * ring);
                }
                float clear = Clearance(i, q);
                float cost = (q - p).sqrMagnitude + (clear >= minD ? 0f : 100f + (minD - clear));
                if (cost < bestCost) { bestCost = cost; best = q; }
            }
            return best;
        }

        /// <summary>Where the aim assist sends pebble i: the open spot in the dish furthest from every other bee spot.</summary>
        Vector3 FreeSpot(int i)
        {
            Vector3 c = _dishRoot.position;
            Vector3 best = c;
            float bestD = -1f, off = UnityEngine.Random.value * Mathf.PI * 2f;
            for (int k = 0; k < 16; k++)
            {
                float ring = (k < 8 ? 0.4f : 0.62f) * _dishR;
                float a = off + k * Mathf.PI * 0.25f + (k >= 8 ? 0.39f : 0f);
                var q = new Vector3(c.x + Mathf.Cos(a) * ring, c.y + _r * 0.7f, c.z + Mathf.Sin(a) * ring);
                float d = Clearance(i, q);
                if (d > bestD) { bestD = d; best = q; }
            }
            return best;
        }

        float Clearance(int i, Vector3 q)
        {
            float d = float.MaxValue;
            for (int j = 0; j < Max; j++)
            {
                if (j == i || _pt[j] == null) continue;
                if (_p[j].state == P.InDish) d = Mathf.Min(d, FlatDist(q, _dish.TransformPoint(_landTo[j])));
                else if (_p[j].state == P.Flying) d = Mathf.Min(d, FlatDist(q, _dish.TransformPoint(_aimTo[j])));
            }
            for (int j = 0; j < _dish.childCount; j++)
            {
                var ch = _dish.GetChild(j); // the dish's own pebbles (authored landing spots for the drinkers)
                if (ch.name.StartsWith("Pebble") || ch.name.StartsWith("LandingSpot")) d = Mathf.Min(d, FlatDist(q, ch.position));
            }
            return d;
        }

        static float FlatDist(Vector3 a, Vector3 b) { a.y = b.y = 0f; return Vector3.Distance(a, b); }

        void SlideLanded()
        {
            if (_dish == null) return;
            float k = 1f - Mathf.Exp(-Time.deltaTime / 0.08f);
            for (int i = 0; i < Max; i++)
            {
                if (_p[i].state != P.InDish || _pt[i] == null || _pt[i].parent != _dish) continue;
                Vector3 lp = _pt[i].localPosition;
                if ((lp - _landTo[i]).sqrMagnitude < 1e-10f) continue;
                _pt[i].localPosition = Vector3.Lerp(lp, _landTo[i], k);
            }
        }

        // ------------------------------------------------------------ hands
        void Hands(float now)
        {
            var ph = PalHands.Instance;
            for (int h = 0; h < 2; h++)
            {
                bool tracked, pinch, ghost = false;
                Vector3 tip;
                if (ph != null)
                {
                    var hh = ph.Hands[h];
                    tracked = hh.tracked; tip = hh.tip; pinch = hh.pinch; ghost = hh.ghost;
                }
                else
                {
                    tracked = GameHands.TryGetIndexTip((GameHand)h, out tip);
                    pinch = tracked && GameHands.IsPinching((GameHand)h);
                }
                if (!tracked)
                {
                    // the wind-up behind the shoulder is where tracking loses the hand: keep the pebble at the last pose
                    // for a moment, then let it go with the hand's last speed
                    if (_hHeld[h] >= 0)
                    {
                        if (_hLostAt[h] < 0f) _hLostAt[h] = now;
                        else if (now - _hLostAt[h] > LostHoldSeconds) Release(h, now, dropOnly: false);
                    }
                    else { _hN[h] = 0; _hPinch[h] = false; _hOffSince[h] = -1f; }
                    continue;
                }
                if (_hLostAt[h] >= 0f) _hN[h] = 0; // back from a tracking gap: the old samples would read as a jump
                _hLostAt[h] = -1f;
                // pinch with a short off-debounce (hand tracking drops it for a frame during fast motion); ghost is exact
                if (pinch || ghost) _hOffSince[h] = -1f;
                else if (_hOffSince[h] < 0f) _hOffSince[h] = now;
                bool was = _hPinch[h];
                bool held = pinch || (_hOffSince[h] >= 0f && now - _hOffSince[h] < PinchDropSeconds);
                _hPinch[h] = held;
                // history (for the throw)
                int n = _hN[h];
                if (n == Hist) { for (int k = 1; k < Hist; k++) { _hPos[h, k - 1] = _hPos[h, k]; _hTime[h, k - 1] = _hTime[h, k]; } n--; }
                _hPos[h, n] = tip; _hTime[h, n] = now; _hN[h] = n + 1;

                if (_finished) { if (_hHeld[h] >= 0) Release(h, now, dropOnly: true); continue; }
                if (held && !was && _hHeld[h] < 0) Grab(h, tip, ghost);
                else if (!held && _hHeld[h] >= 0) Release(h, now, dropOnly: false);
                if (_hHeld[h] >= 0) _pt[_hHeld[h]].position = tip;
            }
        }

        void Grab(int h, Vector3 tip, bool ghost)
        {
            float reach = _r + (ghost ? 0.025f : 0.045f);
            int best = -1;
            float bd = reach * reach;
            for (int i = 0; i < Max; i++)
            {
                if (_p[i].state != P.Bowl || !_pt[i].gameObject.activeSelf || Time.time < _p[i].at) continue;
                float d = (_pt[i].position - tip).sqrMagnitude;
                if (d < bd) { bd = d; best = i; }
            }
            if (best < 0) return;
            _hHeld[h] = best;
            _grabs++;
            ref var p = ref _p[best];
            p.state = P.Held;
            p.hand = h;
            p.ghost = ghost;
            _pt[best].localScale = Vector3.one * _r;
            _pt[best].SetParent(_bowlRoot, true);
            Debug.Log("[VBS][Pebble] grab " + _grabs + ": pebble " + best + " in the " + (h == 0 ? "left" : "right") + " hand" + (ghost ? " (ghost)" : ""));
        }

        void Release(int h, float now, bool dropOnly)
        {
            int i = _hHeld[h];
            _hHeld[h] = -1;
            if (i < 0) return;
            Vector3 v = Vector3.zero;
            int n = _hN[h];
            if (!dropOnly && n >= 2)
            {
                // end the sample before the fingers spring open (the debounce frames and the release frame jump);
                // after a tracking loss the history already stops at the last tracked frame
                int e = n - 1;
                float cut = _hLostAt[h] >= 0f ? 0f : PinchDropSeconds + 0.012f;
                while (e > 1 && now - _hTime[h, e] < cut) e--;
                int k = 0;
                for (int j = e - 1; j >= 0; j--) { k = j; if (_hTime[h, e] - _hTime[h, j] >= 0.06f) break; }
                float dt = _hTime[h, e] - _hTime[h, k];
                if (dt > 1e-3f) v = (_hPos[h, e] - _hPos[h, k]) / dt;
                if (_hLostAt[h] >= 0f && _dishRoot != null && Vector3.Dot(v, _dishRoot.position - _pt[i].position) <= 0f) v = Vector3.zero; // lost mid wind-up: drop, don't throw it backwards
            }
            _hLostAt[h] = -1f;
            v *= ThrowBoost;
            if (v.magnitude > MaxThrow) v = v.normalized * MaxThrow;
            float assist = _p[i].ghost ? 1f : 0.85f;
            Vector3 aimed = v.magnitude < 0.35f ? v : Aim(i, _pt[i].position, v, assist, out _);
            _thrown++;
            Debug.Log("[VBS][Pebble] throw " + _thrown + ": " + v.magnitude.ToString("0.00") + " m/s" +
                      (aimed != v ? ", aim assist -> " + aimed.magnitude.ToString("0.00") + " m/s" : v.magnitude < 0.35f ? " (drop)" : ", no assist (wide)"));
            Launch(i, aimed);
        }

        void Launch(int i, Vector3 v)
        {
            ref var p = ref _p[i];
            var t = _pt[i];
            t.SetParent(_bowlRoot, true);
            t.localScale = Vector3.one * _r;
            p.state = P.Flying;
            p.at = Time.time;
            p.still = 0f;
            p.entered = 0f;
            p.splashed = false;
            var rb = _rb[i];
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.linearVelocity = v;
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 8f;
        }

        /// <summary>
        /// Throw help: if the pebble would land near the dish (within ~3.5 dish radii), bend its velocity toward a spot in
        /// the dish (strength 1 = exactly there). Wide throws fly as thrown.
        /// </summary>
        Vector3 Aim(int i, Vector3 from, Vector3 v, float strength, out bool helped)
        {
            helped = false;
            Vector3 target = FreeSpot(i);
            _aimTo[i] = _dish.InverseTransformPoint(target);
            float g = Mathf.Max(0.1f, -Physics.gravity.y);
            float dy = target.y - from.y;
            float disc = v.y * v.y - 2f * g * dy;
            float T;
            float err;
            if (disc < 0f)
            {
                Vector3 d = target - from; d.y = 0f;
                Vector3 hv = v; hv.y = 0f;
                T = d.magnitude / Mathf.Max(0.5f, hv.magnitude);
                err = strength >= 1f ? 0f : float.MaxValue;
            }
            else
            {
                T = (v.y + Mathf.Sqrt(disc)) / g;
                Vector3 land = from + v * T; land.y = target.y;
                Vector3 e = land - target; e.y = 0f;
                err = e.magnitude;
            }
            float range = Mathf.Max(0.35f, _dishR * 3.5f);
            float k = strength >= 1f ? 1f : err < range ? Mathf.Lerp(0.85f, 0.25f, err / range) * strength : 0f;
            if (k <= 0f) return v;
            T = Mathf.Clamp(T, 0.25f, 1.1f);
            Vector3 vs = (target - from - 0.5f * Physics.gravity * T * T) / T;
            helped = true;
            return Vector3.Lerp(v, vs, k);
        }

        bool AnyInPlay()
        {
            for (int i = 0; i < Max; i++) if (_p[i].state == P.Held || _p[i].state == P.Flying) return true;
            return false;
        }

        bool AnyBeeOut()
        {
            for (int i = 0; i < Max; i++) if (_beeState[i] == 1 || _beeState[i] == 2) return true;
            return false;
        }

        bool AnyInDish()
        {
            for (int i = 0; i < Max; i++) if (_p[i].state == P.InDish) return true;
            return false;
        }

        /// <summary>Time's up: the missing pebbles hop out of the bowl into the dish by themselves.</summary>
        IEnumerator AutoFill()
        {
            int inAir = 0;
            for (int i = 0; i < Max; i++) if (_p[i].state == P.Flying) inAir++;
            int want = Need - _landed - inAir;
            Debug.Log("[VBS][Pebble] timeout after " + (Time.time - _startedAt).ToString("0.0") + " s with " + _landed + " landed: " + Mathf.Max(0, want) + " hop in by themselves");
            for (int h = 0; h < 2; h++) if (_hHeld[h] >= 0) Release(h, Time.time, dropOnly: true);
            for (int n = 0; n < want && _running; n++)
            {
                int pick = -1;
                for (int i = 0; i < Max; i++) if (_p[i].state == P.Bowl && _pt[i].gameObject.activeSelf) { pick = i; break; }
                if (pick < 0) { yield return Wait(0.4f); continue; }
                _p[pick].ghost = true;
                _autoFilled++;
                Vector3 from = _pt[pick].position + Vector3.up * _r;
                _pt[pick].position = from;
                Launch(pick, Aim(pick, from, Vector3.up, 1f, out _));
                yield return Wait(0.3f);
            }
        }

        void Finish(string reason)
        {
            _finished = true;
            StopGhost();
            for (int h = 0; h < 2; h++) if (_hHeld[h] >= 0) Release(h, Time.time, dropOnly: true);
            ShowCard("BAR'S OPEN.", 3f, onDish: true);
            Debug.Log("[VBS][Pebble] done (" + reason + "): " + _landed + " in the dish, " + _thrown + " thrown, " + _grabs + " grabs, " +
                      _misses + " misses, " + _autoFilled + " hopped in by themselves, " + (Time.time - _startedAt).ToString("0.0") + " s");
            StartCoroutine(PutAwayBowl());
        }

        IEnumerator PutAwayBowl()
        {
            yield return Wait(0.8f);
            for (int i = 0; i < Max; i++)
                if (_p[i].state != P.InDish) { _rb[i].isKinematic = true; _p[i].state = P.Gone; _pt[i].gameObject.SetActive(false); }
            Vector3 s0 = _bowlRoot.localScale;
            for (float t = 0f; t < 0.35f; t += Time.deltaTime)
            {
                _bowlRoot.localScale = s0 * (1f - Mathf.SmoothStep(0f, 1f, t / 0.35f));
                yield return null;
            }
            _bowlRoot.gameObject.SetActive(false);
            _bowlRoot.localScale = s0;
            if (_catch != null) _catch.SetActive(false);
            if (_dishRoot != null) _dishRoot.gameObject.SetActive(false);
            _running = false;
            _camUntil = _demo ? Time.time + 6f : -1f;
        }

        void EndPlay()
        {
            _running = false;
            _finished = true;
            _camUntil = -1f;
            StopGhost();
            if (PalHands.Instance != null && _ghostAuto) PalHands.Instance.GhostDown();
            RestoreCap();
        }

        void Cleanup()
        {
            for (int i = 0; i < Max; i++)
                if (_p[i].state != P.InDish && _pt[i] != null) { _rb[i].isKinematic = true; _p[i].state = P.Gone; _pt[i].gameObject.SetActive(false); }
            if (_bowlRoot != null) _bowlRoot.gameObject.SetActive(false);
            if (_catch != null) _catch.SetActive(false);
            if (_dishRoot != null) _dishRoot.gameObject.SetActive(false);
            _card.Hide();
            for (int i = 0; i < Max; i++)
                if (_beeState[i] == 1 || _beeState[i] == 2) { _beeState[i] = 3; _beeAt[i] = Time.time; _beeFrom[i] = _bee[i].position; }
        }

        void ShowCard(string text, float seconds = 0f, bool onDish = false)
        {
            if (_bowlRoot == null) return;
            if (onDish && _dish != null) _card.Show(text, _dish, Vector3.up * (_rimH + 0.22f)); // the bowl shrinks away under it
            else _card.Show(text, _bowlRoot, Vector3.up * 0.13f);
            _cardHideAt = seconds > 0f ? Time.time + seconds : -1f;
        }

        // ------------------------------------------------------------ bees
        void SendBee(int i)
        {
            var b = _bee[i];
            if (b == null) return;
            _beeFrom[i] = _hive != null ? _hive.DoorPoint.position : _dishRoot.position + Vector3.up * 0.5f;
            Vector3 d = _dishRoot.position - _pt[i].position; d.y = 0f;
            _beeFace[i] = d.sqrMagnitude > 1e-5f ? d.normalized : Quaternion.Euler(0f, 67f * i, 0f) * Vector3.forward;
            _beeAt[i] = Time.time + 0.15f;
            _beeState[i] = 1;
            b.localScale = Vector3.one * _beeLen;
            b.position = _beeFrom[i];
            b.gameObject.SetActive(true);
        }

        Vector3 BeeSpot(int i) => _pt[i].position + Vector3.up * (_r * 0.6f + _beeLen * 0.16f);

        void UpdateBees()
        {
            float now = Time.time;
            for (int i = 0; i < Max; i++)
            {
                int s = _beeState[i];
                if (s == 0) continue;
                var b = _bee[i];
                if (s == 1)
                {
                    if (_pt[i] == null || !_pt[i].gameObject.activeInHierarchy) { _beeState[i] = 3; _beeAt[i] = now; _beeFrom[i] = b.position; continue; }
                    float k = Mathf.Clamp01((now - _beeAt[i]) / FlightSeconds);
                    Vector3 to = BeeSpot(i);
                    Vector3 ctrl = Vector3.Lerp(_beeFrom[i], to, 0.5f) + Vector3.up * Mathf.Max(0.08f, Vector3.Distance(_beeFrom[i], to) * 0.3f);
                    float e = k * k * (3f - 2f * k);
                    Vector3 p = Bez(_beeFrom[i], ctrl, to, e);
                    Vector3 ahead = Bez(_beeFrom[i], ctrl, to, Mathf.Min(1f, e + 0.05f)) - p;
                    b.position = p;
                    Vector3 look = Vector3.Slerp(ahead.sqrMagnitude > 1e-8f ? ahead.normalized : _beeFace[i], _beeFace[i], Mathf.InverseLerp(0.8f, 1f, k));
                    if (look.sqrMagnitude > 1e-6f) b.rotation = Quaternion.LookRotation(look, Vector3.up);
                    if (k >= 1f)
                    {
                        _beeState[i] = 2;
                        _beeAt[i] = now;
                        GameSound.Cue("bee_land", b);
                        if (_landed <= 1) GameSound.Cue("pebble_sip", b, 0.3f);
                    }
                }
                else if (s == 2)
                {
                    if (_pt[i] == null || !_pt[i].gameObject.activeInHierarchy) { _beeState[i] = 3; _beeAt[i] = now; _beeFrom[i] = b.position; continue; }
                    float bob = Mathf.Max(0f, Mathf.Sin((now - _beeAt[i]) * 5f + i)) * _beeLen * 0.06f;
                    b.position = BeeSpot(i) + Vector3.up * bob;
                    b.rotation = Quaternion.LookRotation(_beeFace[i], Vector3.up) * Quaternion.Euler(16f + 10f * bob / Mathf.Max(1e-4f, _beeLen * 0.06f), 0f, 0f);
                }
                else
                {
                    Vector3 home = _hive != null ? _hive.DoorPoint.position : _beeFrom[i] + Vector3.up * 0.5f;
                    float k = Mathf.Clamp01((now - _beeAt[i]) / 1f);
                    Vector3 ctrl = Vector3.Lerp(_beeFrom[i], home, 0.5f) + Vector3.up * 0.12f;
                    Vector3 p = Bez(_beeFrom[i], ctrl, home, k);
                    Vector3 dir = Bez(_beeFrom[i], ctrl, home, Mathf.Min(1f, k + 0.05f)) - p;
                    b.position = p;
                    if (dir.sqrMagnitude > 1e-8f) b.rotation = Quaternion.LookRotation(dir, Vector3.up);
                    b.localScale = Vector3.one * (_beeLen * (1f - 0.6f * k));
                    if (k >= 1f) { _beeState[i] = 0; b.gameObject.SetActive(false); }
                }
            }
        }

        static Vector3 Bez(Vector3 a, Vector3 c, Vector3 b, float t) { float u = 1f - t; return u * u * a + 2f * u * t * c + t * t * b; }

        // ------------------------------------------------------------ ghost hand (desktop, autopilot, captures)
        IEnumerator GhostTosses()
        {
            yield return Wait(0.6f);
            for (int n = 0; n < 8 && _running && !_finished && _landed < Need; n++)
            {
                int pending = 0;
                for (int i = 0; i < Max; i++) if (_p[i].state == P.Flying) pending++;
                if (_landed + pending >= Need) { yield return Wait(0.3f); continue; }
                yield return GhostToss();
                yield return Wait(0.35f);
            }
            _ghostCo = null;
        }

        IEnumerator GhostToss()
        {
            var hands = PalHands.Instance;
            if (hands == null || _bowlRoot == null) yield break;
            int pick = -1;
            for (int i = 0; i < Max; i++) if (_p[i].state == P.Bowl && _pt[i].gameObject.activeSelf && Time.time >= _p[i].at) { pick = i; break; }
            if (pick < 0) yield break;
            Transform head = HandsOnKit.Head;
            Vector3 eye = head != null ? head.position : _bowlRoot.position + Vector3.up * 0.5f;
            Vector3 peb = _pt[pick].position;
            Vector3 toDish = _dishRoot.position - peb; toDish.y = 0f;
            toDish = toDish.sqrMagnitude > 1e-4f ? toDish.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, toDish);
            Vector3 n = Vector3.down;
            Vector3 rest = eye + toDish * 0.25f - Vector3.up * 0.55f + right * 0.25f;
            Vector3 above = peb + Vector3.up * 0.07f;
            yield return Move(rest, above, toDish, n, 0.4f, false);
            yield return Move(above, peb, toDish, n, 0.15f, false);
            yield return Hold(peb, toDish, n, 0.12f, true);
            if (_p[pick].state != P.Held) { hands.GhostDown(); yield break; } // the pinch missed (pebble moved)
            Vector3 back = peb - toDish * 0.05f + Vector3.up * 0.06f;
            yield return Move(peb, back, toDish, n, 0.2f, true);
            Vector3 rel = peb + toDish * 0.2f + Vector3.up * 0.16f;
            yield return Move(back, rel, toDish, n, 0.16f, true, easeIn: true);
            yield return Hold(rel, toDish, n, 0.05f, false); // let go
            yield return Move(rel, rest, toDish, n, 0.4f, false);
            hands.GhostDown();
        }

        IEnumerator Move(Vector3 a, Vector3 b, Vector3 fingers, Vector3 normal, float seconds, bool pinch, bool easeIn = false)
        {
            float t0 = Time.time;
            while (true)
            {
                float k = Mathf.Clamp01((Time.time - t0) / Mathf.Max(0.01f, seconds));
                float e = easeIn ? k * k : k * k * (3f - 2f * k);
                Pose(Vector3.Lerp(a, b, e), fingers, normal, pinch);
                if (k >= 1f) yield break;
                yield return null;
            }
        }

        IEnumerator Hold(Vector3 at, Vector3 fingers, Vector3 normal, float seconds, bool pinch)
        {
            float end = Time.time + seconds;
            do { Pose(at, fingers, normal, pinch); yield return null; } while (Time.time < end);
        }

        static void Pose(Vector3 tip, Vector3 fingers, Vector3 normal, bool pinch)
        {
            var hands = PalHands.Instance;
            if (hands == null) return;
            Vector3 f = Vector3.ProjectOnPlane(fingers, normal).normalized;
            hands.Ghost(tip - PalHands.GhostTip(Vector3.zero, normal, f), normal, f, false, pinch);
        }

        void StopGhost()
        {
            if (_ghostCo != null) { StopCoroutine(_ghostCo); _ghostCo = null; if (PalHands.Instance != null) PalHands.Instance.GhostDown(); }
        }

        // ------------------------------------------------------------ lines + capture camera
        IEnumerator SayWhenFree(GameLine line, float maxWait, bool wait = false)
        {
            if (_net == null) yield break;
            float until = Time.time + maxWait;
            var pal = PalBeeswax.Instance; // [Pals] the same "free to talk" check (and quiet gap) as his pal lines
            while ((_net.IsSpeaking || pal != null && !pal.FreeToTalk) && Time.time < until) yield return null;
            if (_net.IsSpeaking || pal != null && !pal.FreeToTalk) { Debug.Log("[VBS][Pebble] Beeswax busy: skipped " + line.id); yield break; }
            if (pal != null) pal.HoldLines(6f);
            _net.Say(line);
            if (!wait) yield break;
            float end = Time.time + Mathf.Min(7f, LineTime.Seconds(_net, line) + 0.3f);
            while (Time.time < end) yield return null;
        }

        static IEnumerator Wait(float s)
        {
            float end = Time.time + s;
            while (Time.time < end) yield return null;
        }

        /// <summary>VBS_PEBBLE_DEMO captures: the "pal" view frames the bowl, the hand and the dish while the toss runs.</summary>
        void LateUpdate()
        {
            if (!_demo || Time.time > _camUntil || _dishRoot == null || _bowlRoot == null) return;
            if (_palCam == null)
            {
                var go = GameObject.Find("PalCamera");
                if (go == null) return;
                _palCam = go.transform;
            }
            if (!_running && !_finished) return;
            Transform head = HandsOnKit.Head;
            if (head == null) return;
            Vector3 dish = _dishRoot.position, bowl = _bowlRoot.gameObject.activeSelf ? _bowlRoot.position : dish;
            Vector3 focus = Vector3.Lerp(bowl, dish, 0.55f);
            Vector3 to = focus - head.position; to.y = 0f;
            Vector3 fwd = to.sqrMagnitude > 1e-4f ? to.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            float span = Mathf.Clamp(Vector3.Distance(bowl, dish), 0.3f, 1.6f);
            Vector3 pos = focus - fwd * (span * 1.25f) - right * (span * 0.35f) + Vector3.up * (span * 0.55f);
            _palCam.SetPositionAndRotation(pos, Quaternion.LookRotation(focus - pos, Vector3.up));
        }
    }
}
