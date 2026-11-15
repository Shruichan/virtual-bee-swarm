using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Beeswax has a body: he never flies through the hive, the rocket, the window, the cards, the hands-on kit or the
    /// props, and keeps out of the player's head and hands. Everything is measured from the real meshes at runtime
    /// (BeeswaxBody.Obstacles), so it holds at any rig scale.
    ///   goals    every mark he is sent to is moved to the nearest free spot (margin = beeswax.margin x his length),
    ///            on the player's side of the thing in the way; a perch/roof/lid mark only moves up
    ///   flights  BeeswaxMotion's flights are swept with his capsule when they start and go over / around what is in
    ///            the way (BeeswaxMotion.Avoid); with the motion module off, the controller steers by waypoints
    ///   body     a critically damped separation spring keeps the margin (a prop flown into him, the lid opening, the
    ///            rig moving); a hard limit means his surface never goes inside anything; a new touch gives a bump
    ///            (squash, wobble, a little knock-back); a light hover inertia eases starts and stops
    ///   allowed  climbing out of the hive door (inside the hive, by design) and riding the rocket (on the seat)
    /// The visual offset is undone at the start of every frame (BeeswaxBodyEarly), so flight code never sees it.
    /// Off: tuning.json beeswax.collide=false or env VBS_BEESWAX_COLLIDE=off. Logs "[VBS][Body] ..." (gaps every 0.5 s,
    /// a summary at the end). No physics engine, no per-frame allocation.
    /// </summary>
    [DefaultExecutionOrder(9000)]
    [DisallowMultipleComponent]
    public partial class BeeswaxBody : MonoBehaviour, BeeswaxController.IGoalFilter
    {
        public static BeeswaxBody Instance { get; private set; }

        // ------------------------------------------------------------ config
        static bool _cfgOn = true;
        static float _cfgMargin = 0.15f;
        static string _cfgFrom = "defaults";
        const float HeadKeepOut = 0.45f, HandKeepOut = 0.06f;

        static void LoadConfig()
        {
            var flat = new Dictionary<string, string>();
            var from = new List<string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            ReadTuning(Path.Combine(Application.streamingAssetsPath, "tuning.json"), flat, from);
#endif
            ReadTuning(Path.Combine(Application.persistentDataPath, "tuning.json"), flat, from);
            _cfgFrom = from.Count > 0 ? string.Join(" + ", from) : "defaults";
            _cfgOn = true;
            _cfgMargin = 0.15f;
            if (flat.TryGetValue("beeswax.collide", out var on)) _cfgOn = !(on == "false" || on == "0" || on == "off");
            if (flat.TryGetValue("beeswax.margin", out var m) && float.TryParse(m, NumberStyles.Float, CultureInfo.InvariantCulture, out float mv))
                _cfgMargin = Mathf.Clamp(mv, 0f, 1f);
            string env = Environment.GetEnvironmentVariable("VBS_BEESWAX_COLLIDE");
            if (!string.IsNullOrEmpty(env)) _cfgOn = !(env == "off" || env == "0" || env == "false");
            string envM = Environment.GetEnvironmentVariable("VBS_BEESWAX_MARGIN");
            if (!string.IsNullOrEmpty(envM) && float.TryParse(envM, NumberStyles.Float, CultureInfo.InvariantCulture, out float em)) _cfgMargin = Mathf.Clamp(em, 0f, 1f);
        }

        static void ReadTuning(string path, Dictionary<string, string> into, List<string> from)
        {
            try
            {
                if (!File.Exists(path)) return;
                VirtualBeeSwarm.AR.ArTuning.FlatJson.Parse(File.ReadAllText(path), into);
                from.Add(path);
            }
            catch (Exception e) { Debug.LogWarning("[VBS][Body] tuning: could not read " + path + ": " + e.Message); }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            LoadConfig();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            InstallNow();
        }

        static void OnSceneLoaded(Scene s, LoadSceneMode m) => InstallNow();

        static void InstallNow()
        {
            var b = FindAnyObjectByType<BeeswaxController>(FindObjectsInactive.Include);
            if (b == null || b.GetComponent<BeeswaxBody>() != null) return;
            var body = b.gameObject.AddComponent<BeeswaxBody>();
            body.collide = _cfgOn;
            body.marginOfLength = _cfgMargin;
            body.logGaps = MeasureByDefault;
            b.gameObject.AddComponent<BeeswaxBodyEarly>();
        }

        // ------------------------------------------------------------ inspector
        [Tooltip("Off = he measures (the log still shows his gaps) but nothing moves him.")]
        public bool collide = true;
        [Tooltip("Clearance he keeps, as a fraction of his length.")]
        [Range(0f, 1f)] public float marginOfLength = 0.15f;
        [Tooltip("Hover inertia (rad/s; higher = tighter).")]
        public float inertiaOmega = 26f;
        [Tooltip("Separation spring (rad/s).")]
        public float springOmega = 14f;
        [Tooltip("Gap log every 0.5 s and the exact per-frame measuring (the summary). On in the editor, batch runs and VBS_BODY_DUMP=1.")]
        public bool logGaps;
        static readonly bool _dump = Environment.GetEnvironmentVariable("VBS_BODY_DUMP") == "1";
        static bool MeasureByDefault => Application.isEditor || Application.isBatchMode || _dump;

        public bool Active => collide && enabled && _ready && !_failed;

        /// <summary>A prop was spawned or switched on (DeliveryItem, CarryProp): rescan soon (at most 5 times a second).</summary>
        public static void MarkDirty()
        {
            var b = Instance;
            if (b != null) b._nextScan = Mathf.Min(b._nextScan, b._lastScanAt + 0.2f);
        }
        /// <summary>His capsule's half length + radius (world m): about half his size.</summary>
        public float Reach => _reach;
        public float Margin => _margin;
        /// <summary>What a flight path must not go through (the head keeps its own distance rule in BeeswaxMotion's governor).</summary>
        public int SweepMask => MaskAll & ~(Bit(Group.Hand) | Bit(Group.Head));

        // ------------------------------------------------------------ scene
        BeeswaxController _b;
        Transform _bodyT;
        GameDirector _dir;
        bool _ready, _failed;
        Vector3 _capA, _capB;   // root-local (unscaled by the root), his rest capsule
        float _capR;
        Vector3 _bodyCapA, _bodyCapB; // body-local
        float _bodyCapR;
        float _reach = 0.1f, _margin = 0.02f, _size = 0.2f, _tiny = 0.002f;

        const int WarmFrames = 6;

        void Awake()
        {
            _b = GetComponent<BeeswaxController>();
            _dir = FindAnyObjectByType<GameDirector>();
            _anchor = transform.parent;
            for (int g = 0; g < _gWin.Length; g++) _gWin[g] = float.PositiveInfinity;
        }

        void OnEnable()
        {
            Instance = this;
            if (_b == null) return;
            _b.GoalFilter = this;
            _b.Ordered += OnOrdered;
            if (_dir != null) _dir.BeatChanged += OnBeat;
        }

        void OnDisable()
        {
            UndoVisual();
            if (_b != null)
            {
                if (ReferenceEquals(_b.GoalFilter, this)) _b.GoalFilter = null;
                _b.Ordered -= OnOrdered;
                _b.ClimbScale = 1f;
                _b.ClimbInScale = 1f;
                _b.AttachOffset = Vector3.zero;
            }
            if (_dir != null) _dir.BeatChanged -= OnBeat;
            if (Instance == this) Instance = null;
        }

        void OnDestroy() => LogSummary("end");
        void OnApplicationQuit() => LogSummary("quit");

        void Fail(string where, Exception e)
        {
            if (_failed) return;
            _failed = true;
            Debug.LogWarning("[VBS][Body] " + where + " threw: collision off for this run. " + e);
            UndoVisual();
            if (_b != null) { _b.ClimbScale = 1f; _b.ClimbInScale = 1f; _b.AttachOffset = Vector3.zero; }
        }

        bool InitBody()
        {
            _bodyT = _b.BodyTransform != null ? _b.BodyTransform : transform;
            var rs = _bodyT.GetComponentsInChildren<Renderer>(true);
            Matrix4x4 w2l = _bodyT.worldToLocalMatrix;
            bool any = false;
            Vector3 mn = Vector3.zero, mx = Vector3.zero;
            int used = 0;
            foreach (var r in rs)
            {
                Vector3 lc, le;
                Transform t;
                if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    lc = mf.sharedMesh.bounds.center; le = mf.sharedMesh.bounds.extents; t = r.transform;
                }
                else if (r is SkinnedMeshRenderer smr)
                {
                    // the bind-pose vertices (localBounds is padded for animation): mesh space = the renderer's transform
                    if (smr.sharedMesh == null) continue;
                    lc = smr.sharedMesh.bounds.center; le = smr.sharedMesh.bounds.extents; t = smr.transform;
                }
                else continue;
                if (Skip(r) || r is ParticleSystemRenderer) continue;
                if (_dump) Debug.Log("[VBS][Body] his part " + r.name + " (" + r.GetType().Name + ") local box " + V(lc) + " +- " + V(le) + " scale " + V(t.lossyScale));
                used++;
                Matrix4x4 m = w2l * t.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = lc + new Vector3((i & 1) == 0 ? -le.x : le.x, (i & 2) == 0 ? -le.y : le.y, (i & 4) == 0 ? -le.z : le.z);
                    Vector3 p = m.MultiplyPoint3x4(c);
                    if (!any) { mn = mx = p; any = true; } else { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
                }
            }
            if (!any) { mn = new Vector3(-0.05f, -0.045f, -0.07f); mx = -mn; }
            Vector3 cen = (mn + mx) * 0.5f, ext = (mx - mn) * 0.5f;
            // a bee: a capsule along his facing (+Z); round over his height and (most of) his width, wings are thin
            float rad = Mathf.Min(Mathf.Max(ext.y, 0.8f * ext.x), ext.z);
            float hl = Mathf.Max(0f, ext.z - rad);
            _bodyCapA = cen - Vector3.forward * hl;
            _bodyCapB = cen + Vector3.forward * hl;
            _bodyCapR = rad;
            CaptureRest();
 UpdateSize();
            Debug.Log("[VBS][Body] his body: " + used + " renderers, body-local box " + V(cen) + " +- " + V(ext) + " -> capsule r " + F(rad) +
                      " half " + F(hl) + " (body local) = r " + F(_capR) + " m, " + F(_size) + " m long in the world; collide " + (collide ? "on" : "off") + ", margin " + F(marginOfLength) + " of his length, tuning " + _cfgFrom);
            return true;
        }

        // ------------------------------------------------------------ his capsule
        void BodyCapsule(out Vector3 a, out Vector3 b, out float r)
        {
            a = _bodyT.TransformPoint(_bodyCapA);
            b = _bodyT.TransformPoint(_bodyCapB);
            Vector3 s = _bodyT.lossyScale;
            r = _bodyCapR * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y));
        }

        /// <summary>
        /// His capsule as it is now, relative to his root's position and turn (world metres): the shape used for poses he is
        /// not in yet (goals, flights, the climb). Taken from the real body whenever he flies free (it follows a rescale).
        /// </summary>
        void CaptureRest()
        {
            Vector3 a, b, o;
            float r;
            Transform par = _bodyT.parent;
            if (_bodyT != transform && par != null)
            {
                // his body at its base turn and scale (no lean, squash, talk pulse, climb squeeze or bump): a steady size
                Matrix4x4 m = par.localToWorldMatrix * Matrix4x4.TRS(_bodyT.localPosition, _b.BodyBaseRot, _b.BodyBaseScale);
                a = m.MultiplyPoint3x4(_bodyCapA);
                b = m.MultiplyPoint3x4(_bodyCapB);
                o = m.MultiplyPoint3x4(Vector3.zero);
                r = _bodyCapR * Mathf.Max(new Vector3(m.m00, m.m10, m.m20).magnitude, new Vector3(m.m01, m.m11, m.m21).magnitude);
            }
            else
            {
                BodyCapsule(out a, out b, out r);
                o = _bodyT.position;
            }
            Quaternion inv = Quaternion.Inverse(transform.rotation);
            Vector3 ra = inv * (a - o), rb = inv * (b - o);
            Vector3 c = 0.5f * (ra + rb), h = 0.5f * (rb - ra);
            _capA = new Vector3(0f, c.y, c.z) - h; // centred left-right: he faces along +Z
            _capB = new Vector3(0f, c.y, c.z) + h;
            _capR = r;
        }

        /// <summary>His capsule if his root stood at `at`, turned `rot`.</summary>
        void CapsuleAt(Vector3 at, Quaternion rot, out Vector3 a, out Vector3 b, out float r)
        {
            a = at + rot * _capA;
            b = at + rot * _capB;
            r = _capR;
        }

        void UpdateSize()
        {
            CapsuleAt(Vector3.zero, Quaternion.identity, out var a, out var b, out float r);
            float len = Vector3.Distance(a, b);
            _reach = 0.5f * len + r;
            _size = len + 2f * r;
            _margin = marginOfLength * _size;
            _tiny = 0.06f * r;
        }

        // ------------------------------------------------------------ frame: undo (early) ... resolve (late)
        // The offset lives in his parent's space (the rig): when the rig moves under him (the hive dragged, a placement,
        // an anchor correction) it is still taken back off exactly and never baked into his root.
        Vector3 _appliedL, _appliedAtL;
        bool _hasApplied;
        Vector3 _bodyScaleSet, _bodyScaleWas;
        Quaternion _bodyRotSet, _bodyRotWas;
        bool _bodyTouched;

        Vector3 ToLocalV(Vector3 w) { var p = transform.parent; return p != null ? p.InverseTransformVector(w) : w; }
        Vector3 ToWorldV(Vector3 l) { var p = transform.parent; return p != null ? p.TransformVector(l) : l; }
        Vector3 ToLocalP(Vector3 w) { var p = transform.parent; return p != null ? p.InverseTransformPoint(w) : w; }

        /// <summary>BeeswaxBodyEarly, before anything moves him: take last frame's visual offset back off.</summary>
        public void UndoVisual()
        {
            if (_hasApplied)
            {
                if ((transform.localPosition - _appliedAtL).sqrMagnitude < 1e-10f) transform.localPosition = _appliedAtL - _appliedL;
                _hasApplied = false;
            }
            if (_bodyTouched && _bodyT != null)
            {
                if (_bodyT.localScale == _bodyScaleSet) _bodyT.localScale = _bodyScaleWas;
                if (_bodyT.localRotation == _bodyRotSet) _bodyT.localRotation = _bodyRotWas;
                _bodyTouched = false;
            }
        }

        Vector3 _inertPL, _inertVL, _sep, _sepV, _prevRootL, _prevOff;
        bool _haveInert, _teleport = true, _prevShown;
        int _lastFreeFrame = -10;
        float _rocketClr = float.PositiveInfinity;
        float _nextHardLog, _nextJumpLog;
        bool _hardPass;
        const float NewGrace = 0.25f;
        int _hardLogs; // landing on a rocket seat: the rocket's clearance (the rest keep the margin)

        void LateUpdate()
        {
            if (_failed || _b == null || Time.frameCount <= WarmFrames) return;
            try { Frame(); } catch (Exception e) { _rocketClr = float.PositiveInfinity; Fail("frame", e); }
        }

        void Frame()
        {
            if (!_ready)
            {
                _ready = InitBody();
                UpdateSize();
                ScanObstacles();
                _nextScan = Time.time + 1f;
            }
            float dt = Mathf.Min(Time.deltaTime, 1f / 20f);
            if (Time.time >= _nextScan) { _nextScan = Time.time + 1f; ScanObstacles(); }
            CaptureRest(); // every frame, in every state: a rescale while he is inside or seated is followed too
            UpdateSize();
            UpdateObstacles(Time.deltaTime);

            Vector3 root = transform.position;
            Vector3 rootL = ToLocalP(root);
            bool visible = _b.Visible && _bodyT.gameObject.activeInHierarchy;
            bool inside = _b.Inside, climbing = _b.Climbing;
            Transform att = _b.AttachedTransform;
            bool ride = att != null && !inside && IsRocket(att);
            bool attached = att != null;
            bool free = !attached && !climbing && !inside;
            if (!free || !visible) _teleport = true;
            // seated, riding, climbing: whatever he touches as that ends (the seat, the door) is not a bump
            if (!free) for (int g = 0; g < _touchAt.Length; g++) _touchAt[g] = Time.time;

            // his own motion, in the rig's space (the rig moving under him is not him moving); a real jump (snap, reset,
            // the end of a ride) carries no inertia and the push starts over
            Vector3 dRoot = ToWorldV(rootL - _prevRootL);
            Vector3 vRoot = dt > 1e-5f ? dRoot / Mathf.Max(Time.deltaTime, 1e-5f) : Vector3.zero;
            bool jumped = dRoot.magnitude > Mathf.Max(0.6f * _size, 3f * Time.deltaTime);
            if (free && jumped) _teleport = true;
            _prevRootL = rootL;
            if (jumped) _sep = _sepV = Vector3.zero;

            // hover inertia: the body trails his root a little (eases starts and stops), only while he flies free
            Vector3 lag = Vector3.zero;
            if (free && visible && collide)
            {
                if (_teleport || !_haveInert || Time.frameCount - _lastFreeFrame > 1) { _inertPL = rootL; _inertVL = Vector3.zero; _haveInert = true; }
                BodyMath.Crit(ref _inertPL, ref _inertVL, rootL, inertiaOmega, dt);
                lag = Vector3.ClampMagnitude(ToWorldV(_inertPL - rootL), 0.25f * _size);
                _inertPL = rootL + ToLocalV(lag);
                _lastFreeFrame = Time.frameCount;
                _teleport = false;
            }
            else _haveInert = false;

            if (!visible || inside)
            {
                _sep = _sepV = Vector3.zero;
                _prevShown = false;
                return;
            }

            // what may touch him now: climbing out = the hive (and the table under the door), not the rest
            int mask = MaskAll;
            if (climbing) mask &= ~(Bit(Group.Hive) | Bit(Group.Surface));
            // on a rocket seat he rests on the rocket (a thin skin, he never sinks into the nose); landing on one, the
            // rocket only needs the seat's gap (SeatSpot), so the ride starts without a hop
            Transform gt = _b.GoalTransform;
            if (ride && att != _seatDone && collide) PlaceOnSeat(att); // put on a seat before the body was ready (the start, a reset)
            if (ride) _rocketClr = 0.01f * _size;
            else _rocketClr = !attached && gt != null && IsRocket(gt) && (root - gt.position).sqrMagnitude < Sq(2f * _reach + _margin)
                ? SeatClearance(gt) : float.PositiveInfinity;

            ApplyBump(); // his shape this frame (squash, wobble) before he is measured
            BodyCapsule(out Vector3 a0, out Vector3 b0, out float r);
            a0 += lag; b0 += lag;
            _holdingClip = attached && !ride;
            if (logGaps) _nomGap = MinGap(a0, b0, r, mask, 0f, out _nomGroup);

            Vector3 sep;
            if (collide)
            {
                // soft: the spring keeps the margin; hard: never inside (velocity into the obstacle is cancelled)
                Vector3 soft = Resolve(a0, b0, r, _margin, mask, out int sg, out Vector3 sn);
                Bump(sg, sn, soft, vRoot);
                BodyMath.Crit(ref _sep, ref _sepV, soft, springOmega, dt);
                // a thin skin: things that move later this frame (a window, a lid) cannot get under it. Something that just
                // appeared on top of him (a station's props coming out) only gets the spring: a quick push, not a teleport
                _hardPass = true;
                Vector3 hard = Resolve(a0 + _sep, b0 + _sep, r, 0.02f * _size, mask, out int hg, out Vector3 hn);
                _hardPass = false;
                if (hard.sqrMagnitude > 1e-12f)
                {
                    _sep += hard;
                    float into = Vector3.Dot(_sepV, hn);
                    if (into < 0f) _sepV -= hn * into;
                    _hardFrames++;
                    if (hard.magnitude > _maxHard) { _maxHard = hard.magnitude; _maxHardGroup = hg; }
                    if (logGaps && hard.magnitude > 0.1f * _size && Time.time >= _nextHardLog && _hardLogs < 40)
                    {
                        _nextHardLog = Time.time + 1f;
                        _hardLogs++;
                        Debug.Log("[VBS][Body] hard stop " + F(hard.magnitude * 1000f) + " mm out of the " + GroupName(hg) + " [beat " + BeatName + "] " +
                                  (climbing ? "climbing" : ride ? "riding" : attached ? "seated on " + att.name : "free") + ", goal " + (gt != null ? gt.name : "point") + " t=" + F(Time.time));
                    }
                }
                if (soft.sqrMagnitude > 1e-10f) _softFrames++;
                sep = _sep;
            }
            else
            {
                BodyMath.Crit(ref _sep, ref _sepV, Vector3.zero, springOmega, dt); // let go gently
                sep = _sep;
            }
            _rocketClr = float.PositiveInfinity;

            Vector3 off = lag + sep;
            // the biggest one-frame jump of what the player sees (a root jump is the root's, not the body's)
            if (_prevShown && !jumped)
            {
                float j = (off - _prevOff).magnitude;
                if (j > _maxJump) { _maxJump = j; _maxJumpBeat = BeatName; }
                if (j > 0.08f * _size)
                {
                    _jumpFrames++;
                    if (j > 0.15f * _size && Time.time >= _nextJumpLog) { _nextJumpLog = Time.time + 1f; Debug.Log("[VBS][Body] jump " + F(j * 1000f) + " mm [beat " + BeatName + "] " + (ride ? "riding" : attached ? "seated" : climbing ? "climbing" : "free") + " lag " + F(lag.magnitude) + " sep " + F(sep.magnitude) + " t=" + F(Time.time)); }
                }
            }
            _prevOff = off;
            _prevShown = true;
            if (off.sqrMagnitude > 1e-12f)
            {
                transform.position = root + off;
                _appliedL = ToLocalV(off);
                _appliedAtL = transform.localPosition;
                _hasApplied = true;
            }
            Measure(true, mask, ride, climbing);
            if (_dump && Time.time >= _nextDump) { _nextDump = Time.time + 5f; DumpParts(); }
        }

        float _nextDump;
        void DumpParts()
        {
            BodyCapsule(out var a, out var b, out float r);
            Debug.Log("[VBS][Body] dump: his capsule " + V(a) + "-" + V(b) + " r " + F(r) + " root " + V(transform.position) + " anchor scale " + F(_anchor != null ? _anchor.lossyScale.x : 1f));
            for (int i = 0; i < _parts.Count; i++)
            {
                var p = _parts[i];
                if (!p.on) continue;
                Debug.Log("[VBS][Body] dump " + GroupName(p.group) + " " + (p.r != null ? p.r.name : p.kind.ToString()) + (p.clip ? " (clipboard)" : "") + " c " + V(p.box.c) + " h " + V(p.box.h) +
                          " gap " + F(Gap(p, a, b, r, out _)));
            }
        }

        bool IsRocket(Transform t)
        {
            if (_rocket == null) _rocket = FindRocket();
            return _rocket != null && t.IsChildOf(_rocket);
        }

        // ------------------------------------------------------------ bumps (BeeswaxBody.Bumps)
        int _bumps;
        readonly float[] _touchAt = new float[(int)Group.Count];

        /// <summary>A new touch (soft = the push the spring wants): squash, wobble, a little knock-back.</summary>
        partial void Bump(int group, Vector3 n, Vector3 soft, Vector3 vRoot);
        /// <summary>His shape this frame while a bump plays out (taken back off in UndoVisual).</summary>
        partial void ApplyBump();

        // ------------------------------------------------------------ goals (BeeswaxController.IGoalFilter)
        Vector3 _gRaw, _gOut;
        float _gNext;
        int _gFrame = -1;
        Transform _gT;
        int _goalsMoved;
        float _goalMaxMove;

        static bool UpOnly(Transform t)
        {
            if (t == null) return false;
            string n = t.name;
            return n.Contains("Perch") || n.Contains("Roof") || n.Contains("Lid");
        }

        int GoalMask => MaskAll & ~Bit(Group.Hand);

        public Vector3 FilterGoal(Vector3 raw)
        {
            if (!Active) return raw;
            try
            {
                if (Time.frameCount == _gFrame && raw == _gRaw) return _gOut;
                Transform gt = _b.GoalTransform;
                bool same = gt == _gT && (raw - _gRaw).sqrMagnitude < Sq(0.1f * _margin + 1e-4f);
                if (same && Time.time < _gNext) { _gFrame = Time.frameCount; return _gOut; }
                if (same && ClearAt(_gOut, FaceRot(_gOut), 0.8f * _margin, GoalMask)) { _gNext = Time.time + 0.25f; _gFrame = Time.frameCount; return _gOut; }
                if (!same && gt != null && gt == _gT && (_gOut - _gRaw).sqrMagnitude > 1e-8f)
                {
                    // a mark that moves (a held prop, the rocket): keep the side it was moved to while that stays clear
                    Vector3 keep = raw + (_gOut - _gRaw);
                    if (ClearAt(keep, FaceRot(keep), 0.8f * _margin, GoalMask)) { _gRaw = raw; _gOut = keep; _gFrame = Time.frameCount; _gNext = Time.time + 0.25f; return keep; }
                }
                Vector3 g = gt != null && IsRocket(gt) ? SeatSpot(gt, raw, out int why) : Project(raw, UpOnly(gt), GoalMask, out why);
                float moved = Vector3.Distance(g, raw);
                bool news = !same || (g - _gOut).sqrMagnitude > Sq(0.25f * _margin);
                _gRaw = raw; _gOut = g; _gT = gt; _gFrame = Time.frameCount; _gNext = Time.time + 0.25f;
                if (moved > 1e-4f && news)
                {
                    _goalsMoved++;
                    _goalMaxMove = Mathf.Max(_goalMaxMove, moved);
                    if (_goalsMoved <= 60)
                        Debug.Log("[VBS][Body] goal " + (gt != null ? gt.name : "point") + " moved " + F(moved * 100f) + " cm clear of " + GroupName(why) + " [beat " + BeatName + "]");
                }
                return g;
            }
            catch (Exception e) { Fail("goal", e); return raw; }
        }

        Quaternion FaceRot(Vector3 at)
        {
            var head = _b.HeadTransform;
            Vector3 to = head != null ? head.position - at : -transform.forward;
            to.y = 0f;
            return to.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(to, Vector3.up) : transform.rotation;
        }

        bool ClearAt(Vector3 at, Quaternion rot, float clearance, int mask)
        {
            CapsuleAt(at, rot, out var a, out var b, out float r);
            return MinGap(a, b, r, mask, clearance, out _) >= clearance;
        }

        static readonly Vector3[] Dirs = MakeDirs();
        static Vector3[] MakeDirs()
        {
            var l = new List<Vector3>();
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                if (x != 0 || y != 0 || z != 0) l.Add(new Vector3(x, y, z).normalized);
            return l.ToArray();
        }

        /// <summary>The nearest free spot (his margin clear of everything in mask), on the player's side by preference.</summary>
        Vector3 Project(Vector3 raw, bool upOnly, int mask, out int why) => Project(raw, upOnly, mask, out why, FaceRot(raw), _margin);

        Vector3 Project(Vector3 raw, bool upOnly, int mask, out int why, Quaternion rot, float clearance)
        {
            CapsuleAt(raw, rot, out var a0, out var b0, out float r0);
            float gap = MinGap(a0, b0, r0, mask, clearance, out why);
            if (gap >= clearance) return raw;
            var head = _b.HeadTransform;
            Vector3 toEye = head != null ? head.position - raw : -transform.forward;
            toEye.y = 0f;
            toEye = toEye.sqrMagnitude > 1e-6f ? toEye.normalized : Vector3.back;
            float step = 0.3f * _reach;
            Vector3 best = raw;
            float bestCost = float.PositiveInfinity;
            int foundRing = -1;
            for (int k = 1; k <= 40; k++)
            {
                float rad = k * step;
                for (int i = 0; i < Dirs.Length; i++)
                {
                    Vector3 d = Dirs[i];
                    if (upOnly && d.y < 0.5f) continue;
                    Vector3 c = raw + d * rad;
                    if (!ClearAt(c, rot, clearance, mask)) continue;
                    Vector3 df = new Vector3(d.x, 0f, d.z);
                    float side = df.sqrMagnitude > 1e-6f ? Vector3.Dot(df.normalized, toEye) : 0f;
                    float cost = rad * (1f + 0.5f * (1f - side) * 0.5f + (d.y < -0.1f ? 0.4f : 0f));
                    if (cost < bestCost) { bestCost = cost; best = c; }
                    if (foundRing < 0) foundRing = k;
                }
                if (foundRing > 0 && k >= foundRing + 1) break;
            }
            if (foundRing < 0)
            {
                // nothing free nearby: at least out of contact, by the push
                Vector3 push = Resolve(a0, b0, r0, clearance, mask, out _, out _);
                return raw + push;
            }
            // tighten: the closest clear point on the way out
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 7; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (ClearAt(Vector3.Lerp(raw, best, mid), rot, clearance, mask)) hi = mid; else lo = mid;
            }
            return Vector3.Lerp(raw, best, hi);
        }

        /// <summary>
        /// Where he sits on a rocket seat or perch: the nearest spot where his body only just touches the rocket (no gap to
        /// speak of, nothing inside). A perch turns him to the player; a seat holds him as it is turned. The flight there
        /// lands on the same spot, so the ride starts without a hop.
        /// </summary>
        Vector3 SeatSpot(Transform seat, Vector3 raw, out int why)
        {
            bool perch = seat.name.Contains("Perch");
            Quaternion rot = perch ? FaceRot(raw) : seat.rotation; // on a perch his body turns to the player (BeeswaxMotion)
            return Project(raw, true, GoalMask & ~Bit(Group.Head), out why, rot, SeatClearance(seat));
        }

        /// <summary>His gap to the rocket on a seat; on a perch he sits a little higher (on the cap, legs and all).</summary>
        float SeatClearance(Transform seat) => 0.12f * _margin + (seat.name.Contains("Perch") ? 0.3f * _capR : 0f);

        // ------------------------------------------------------------ paths
        /// <summary>
        /// His capsule swept along points (oriented along the path). Returns the smallest gap; i0/i1 = first/last point
        /// closer than `thr` (-1: clear). Where he starts already touching something, that contact is ignored until it eases.
        /// </summary>
        public float SweepGap(Vector3[] pts, int n, float thr, out int i0, out int i1, out int group)
        {
            i0 = i1 = -1;
            group = -1;
            if (n <= 0) return float.PositiveInfinity;
            float best = float.PositiveInfinity, start = float.PositiveInfinity;
            int mask = SweepMask;
            float r = _capR;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = pts[Mathf.Min(i + 1, n - 1)] - pts[Mathf.Max(i - 1, 0)];
                d.y = Mathf.Clamp(d.y, -0.7f * new Vector2(d.x, d.z).magnitude, 0.7f * new Vector2(d.x, d.z).magnitude);
                Quaternion rot = d.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(d, Vector3.up) : FaceRot(pts[i]);
                CapsuleAt(pts[i], rot, out var a, out var b, out _);
                float gap = MinGap(a, b, r, mask, Mathf.Max(thr, 2f * _margin), out int g);
                if (i == 0) start = gap;
                if (i > 0 && (pts[i] - pts[0]).sqrMagnitude < Sq(2f * _reach) && gap >= start - 1e-3f) continue;
                // the landing: where he arrives was made clear (the goal); only a path that cuts into something on the way counts
                if (i > 0 && (pts[i] - pts[n - 1]).sqrMagnitude < Sq(1.2f * _reach) && gap >= 0f) continue;
                if (gap < best) { best = gap; if (gap < thr) group = g; }
                if (gap < thr) { if (i0 < 0) i0 = i; i1 = i; }
            }
            return best;
        }

        public float PathThreshold => 0.35f * _margin;

        /// <summary>The thing in the way of p..q, as a world box (the whole of it: the hive, not just the roof).</summary>
        public bool Obstruction(Vector3 p, Vector3 q, out Bounds b) => BoundsNear(p, q, _reach + _margin, SweepMask, out b, true);

        /// <summary>Only the parts of `group` in the way, not grown (a second try when the whole cluster is too big).</summary>
        public bool ObstructionOf(int group, Vector3 p, Vector3 q, out Bounds b) => BoundsNear(p, q, _reach + _margin, group >= 0 ? 1 << group : SweepMask, out b, false);

        /// <summary>
        /// Up to 9 detours around box hb for a path from..to blocked between a and b: over (1 and 2 waypoints), left and
        /// right (1 and 2). w1/w2 per candidate; w2 == w1 means a single waypoint. False for one the governor would not
        /// allow (over the brow).
        /// </summary>
        public int Detours(Vector3 from, Vector3 to, Vector3 a, Vector3 b, Bounds hb, Vector3[] w1, Vector3[] w2)
        {
            int n = 0;
            float clr = _reach + 1.5f * _margin;
            var head = _b.HeadTransform;
            float brow = head != null ? head.position.y + MotionGovernor.MaxAboveEye : float.PositiveInfinity;
            Vector3 mid = 0.5f * (a + b);
            float top = hb.max.y + clr;
            // not over the player's brow, unless he is flying from or to up there anyway (the perch on the rocket's nose)
            if (top <= Mathf.Max(brow, Mathf.Max(from.y, to.y) + clr))
            {
                w1[n] = new Vector3(mid.x, Mathf.Max(top, mid.y), mid.z); w2[n] = w1[n]; n++;
                w1[n] = new Vector3(a.x, Mathf.Max(top, a.y), a.z); w2[n] = new Vector3(b.x, Mathf.Max(top, b.y), b.z); n++;
                // a hop: straight up where he is, across, straight down onto the mark
                w1[n] = new Vector3(from.x, Mathf.Max(top, from.y), from.z); w2[n] = new Vector3(to.x, Mathf.Max(top, to.y), to.z); n++;
            }
            Vector3 dir = to - from;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) dir = b - a;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            Vector3 perp = Vector3.Cross(Vector3.up, dir.normalized);
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 ps = perp * s;
                float ext = float.NegativeInfinity;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = hb.center + Vector3.Scale(hb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    ext = Mathf.Max(ext, Vector3.Dot(c - mid, ps));
                }
                float off = ext + clr;
                w1[n] = mid + ps * off; w2[n] = w1[n]; n++;
                float ea = ext + Vector3.Dot(mid - a, ps), eb = ext + Vector3.Dot(mid - b, ps);
                w1[n] = a + ps * (ea + clr); w2[n] = b + ps * (eb + clr); n++;
                w1[n] = a + ps * (ea + 2.5f * clr); w2[n] = b + ps * (eb + 2.5f * clr); n++; // wider (something else sits beside it)
            }
            return n;
        }

        /// <summary>Extra cost for a detour that hides him behind the thing (the player should see him go round).</summary>
        public float BehindPenalty(Vector3 w, Bounds hb)
        {
            var head = _b.HeadTransform;
            if (head == null) return 0f;
            Vector3 toEye = head.position - hb.center;
            toEye.y = 0f;
            Vector3 d = w - hb.center;
            d.y = 0f;
            return Vector3.Dot(d, toEye) < 0f ? 0.35f : 0f;
        }

        int _routed, _routeFail;
        public void NoteRoute(string kind, int candidates, bool ok, float gapBefore, float gapAfter, string via)
        {
            if (ok) _routed++; else _routeFail++;
            if (_routed + _routeFail <= 60)
                Debug.Log("[VBS][Body] route " + kind + ": " + (ok ? "around via " + via : "no clear detour (" + candidates + " tried), best " + via) +
                          ", gap " + F(gapBefore) + " -> " + F(gapAfter) + " m [beat " + BeatName + "]");
        }

        // default flight code (motion module off): waypoints
        Vector3 _stGoal, _stWay;
        bool _stHasWay;
        float _stNext;
        readonly Vector3[] _stPts = new Vector3[96];
        readonly Vector3[] _w1 = new Vector3[10], _w2 = new Vector3[10];

        public Vector3 Steer(Vector3 from, Vector3 goal)
        {
            if (!Active) return goal;
            try
            {
                if ((goal - _stGoal).sqrMagnitude > Sq(0.5f * _margin) || Time.time >= _stNext)
                {
                    _stGoal = goal;
                    _stNext = Time.time + 0.3f;
                    int n = SampleLine(from, goal, goal, _stPts);
                    SweepGap(_stPts, n, PathThreshold, out int i0, out int i1, out _);
                    _stHasWay = false;
                    if (i0 >= 0 && Obstruction(_stPts[i0], _stPts[i1], out Bounds hb))
                    {
                        int c = Detours(from, goal, _stPts[i0], _stPts[i1], hb, _w1, _w2);
                        float bestCost = float.PositiveInfinity;
                        for (int i = 0; i < c; i++)
                        {
                            if (_w1[i] != _w2[i]) continue; // one waypoint is enough for a straight line
                            int m = SampleLine(from, _w1[i], goal, _stPts);
                            SweepGap(_stPts, m, PathThreshold, out int j0, out _, out _);
                            if (j0 >= 0) continue;
                            float cost = (Vector3.Distance(from, _w1[i]) + Vector3.Distance(_w1[i], goal)) * (1f + BehindPenalty(_w1[i], hb));
                            if (cost < bestCost) { bestCost = cost; _stWay = _w1[i]; _stHasWay = true; }
                        }
                        NoteRoute("straight", c, _stHasWay, 0f, 0f, _stHasWay ? V(_stWay) : "-");
                    }
                }
                if (_stHasWay)
                {
                    if ((from - _stWay).sqrMagnitude < Sq(0.5f * _reach)) _stHasWay = false;
                    else return _stWay;
                }
                return goal;
            }
            catch (Exception e) { Fail("steer", e); return goal; }
        }

        int SampleLine(Vector3 a, Vector3 w, Vector3 b, Vector3[] into)
        {
            float l1 = Vector3.Distance(a, w), l2 = Vector3.Distance(w, b);
            float step = Mathf.Max(0.5f * _capR, 1e-3f);
            int n = Mathf.Clamp(Mathf.CeilToInt((l1 + l2) / step) + 1, 2, into.Length);
            for (int i = 0; i < n; i++)
            {
                float s = (l1 + l2) * i / (n - 1);
                into[i] = s <= l1 ? Vector3.Lerp(a, w, l1 > 1e-6f ? s / l1 : 1f) : Vector3.Lerp(w, b, l2 > 1e-6f ? (s - l1) / l2 : 1f);
            }
            return n;
        }

        // ------------------------------------------------------------ orders: climb exit, rocket seat, teleports
        void OnOrdered(string what)
        {
            try { OnOrderedCore(what); } catch (Exception e) { Fail("order " + what, e); }
        }

        Transform _seatDone; // the seat AttachOffset was worked out for

        void PlaceOnSeat(Transform seat)
        {
            _seatDone = seat;
            Vector3 p = SeatSpot(seat, seat.position, out int why);
            Vector3 off = p - seat.position;
            _b.AttachOffset = off.sqrMagnitude > 1e-8f ? Quaternion.Inverse(seat.rotation) * off : Vector3.zero;
            if (off.sqrMagnitude > 1e-8f) Debug.Log("[VBS][Body] seat " + seat.name + ": he sits " + F(off.magnitude * 100f) + " cm clear of the " + GroupName(why));
        }

        void OnOrderedCore(string what)
        {
            if (what != "fly" && what != "present" && what != "home" && what != "whip") _teleport = true;
            if (!_ready) return;
            if (what == "hide") { _b.AttachOffset = Vector3.zero; _seatDone = null; return; }
            if (what == "attach")
            {
                _b.AttachOffset = Vector3.zero;
                _seatDone = _b.AttachedTransform;
                var seat = _b.AttachedTransform;
                if (!collide || seat == null || _b.Inside || !IsRocket(seat)) return;
                UpdateObstacles(0f);
                PlaceOnSeat(seat);
                return;
            }

            if (what == "climb")
            {
                // the pop and the shake end where his whole body is out of the hive (the stock 4.7 cm is for a small body)
                _b.ClimbScale = 1f;
                if (!collide) return;
                Transform door = _b.DoorTransform;
                Vector3 outward = _b.ClimbOutward;
                if (door == null) return;
                UpdateObstacles(0f);
                float baseExit = _b.ClimbExitDistance;
                _b.ClimbInScale = _anchor != null ? Mathf.Abs(_anchor.lossyScale.x) : 1f; // the squeeze starts as deep as the rig is big
                Quaternion rot = Quaternion.LookRotation(outward, Vector3.up);
                int hive = Bit(Group.Hive);
                float s = baseExit;
                for (int i = 0; i < 80; i++, s += 0.1f * _reach)
                {
                    float k = s / baseExit;
                    Vector3 at = door.position + outward * s + Vector3.up * (0.012f * k);
                    if (ClearAt(at, rot, _margin, hive)) break; // the full margin: no push (and no bump) when the climb ends
                }
                _b.ClimbScale = Mathf.Max(1f, s / baseExit);
                Debug.Log("[VBS][Body] climb: he pops out " + F(s * 100f) + " cm from the door (x" + F(_b.ClimbScale) + ")");
            }
        }

        // ------------------------------------------------------------ measuring + logs
        readonly float[] _gMin = new float[(int)Group.Count];
        readonly float[] _gWin = new float[(int)Group.Count];
        float _nomGap;
        int _nomGroup;
        float _nextLog;
        int _frames, _violFrames, _allowFrames, _climbFrames, _rideFrames, _softFrames, _hardFrames, _nomPenFrames;
        float _maxPen, _maxAllowed, _maxHard, _maxNomPen, _maxRideRocket, _maxJump;
        int _jumpFrames;
        string _maxJumpBeat = "";
        int _maxPenGroup = -1, _maxHardGroup = -1, _maxNomGroup = -1;
        string _maxPenBeat = "", _maxNomBeat = "";
        int _beatViol, _beatFrames, _beatNomPen;
        float _beatMaxPen, _beatMaxNom;
        const float Tol = 0.0005f;
        readonly StringBuilder _sb = new StringBuilder(512);

        string BeatName => _dir != null ? GameIds.BeatName(_dir.Beat) : "-";

        void Measure(bool shown, int mask, bool ride, bool climbing)
        {
            if (!shown || !logGaps) return;
            BodyCapsule(out var a, out var b, out float r);
            for (int g = 0; g < _gMin.Length; g++) _gMin[g] = float.PositiveInfinity;
            Vector3 mid = (a + b) * 0.5f;
            float reach = 0.5f * Vector3.Distance(a, b) + r;
            for (int i = 0; i < _parts.Count; i++)
            {
                var p = _parts[i];
                if (Ignored(p)) continue;
                if (Time.time - p.onSince < NewGrace) continue; // just appeared on him: the spring's job
                float d;
                if (Far(p, mid, reach, 4f * _size)) d = (mid - p.box.c).magnitude - p.bound - reach; // far: a lower bound is enough for the log
                else d = Gap(p, a, b, r, out _);
                if (d < _gMin[p.group]) _gMin[p.group] = d;
            }
            _frames++;
            _beatFrames++;
            float viol = 0f, allowed = 0f;
            int vg = -1;
            for (int g = 0; g < _gMin.Length; g++)
            {
                float d = _gMin[g];
                if (d < _gWin[g]) _gWin[g] = d;
                if (ride && g == (int)Group.Rocket && -d > _maxRideRocket) _maxRideRocket = -d;
                if (d >= -Tol) continue;
                bool ok = (mask & (1 << g)) == 0;
                if (ok) allowed = Mathf.Max(allowed, -d);
                else if (-d > viol) { viol = -d; vg = g; }
            }
            if (allowed > 0f) { _allowFrames++; if (climbing) _climbFrames++; if (ride) _rideFrames++; _maxAllowed = Mathf.Max(_maxAllowed, allowed); }
            if (vg >= 0)
            {
                _violFrames++;
                _beatViol++;
                _beatMaxPen = Mathf.Max(_beatMaxPen, viol);
                if (viol > _maxPen) { _maxPen = viol; _maxPenGroup = vg; _maxPenBeat = BeatName; }
                if (_violFrames <= 30) Debug.Log("[VBS][Body] PENETRATION " + F(viol * 1000f) + " mm into " + GroupName(vg) + " [beat " + BeatName + "] t=" + F(Time.time) + " wedgeFails=" + _wedgeFails + " mask=" + mask + " sep=" + V(_sep) +
                                                 " attached=" + (_b.AttachedTransform != null ? _b.AttachedTransform.name : "-") + " climbing=" + climbing);
            }
            if (_nomGap < -Tol && !ride)
            {
                _nomPenFrames++;
                _beatNomPen++;
                _beatMaxNom = Mathf.Max(_beatMaxNom, -_nomGap);
                if (-_nomGap > _maxNomPen) { _maxNomPen = -_nomGap; _maxNomGroup = _nomGroup; _maxNomBeat = BeatName; }
            }
            if (logGaps && Time.time >= _nextLog)
            {
                _nextLog = Time.time + 0.5f;
                _sb.Clear();
                _sb.Append("[VBS][Body] gaps (m, - = inside) beat ").Append(BeatName).Append(' ')
                   .Append(climbing ? "climbing" : ride ? "riding" : _b.AttachedTransform != null ? "seated" : "free").Append(':');
                float all = float.PositiveInfinity;
                int allG = -1;
                for (int g = 0; g < _gWin.Length; g++)
                {
                    if (float.IsPositiveInfinity(_gWin[g])) continue;
                    _sb.Append(' ').Append(GroupNames[g]).Append('=').Append(_gWin[g].ToString("0.000", CultureInfo.InvariantCulture));
                    if (_gWin[g] < all) { all = _gWin[g]; allG = g; }
                    _gWin[g] = float.PositiveInfinity;
                }
                _sb.Append(" | min ").Append(GroupName(allG)).Append(' ').Append(all.ToString("0.000", CultureInfo.InvariantCulture))
                   .Append(", push ").Append((_sep.magnitude * 100f).ToString("0.0", CultureInfo.InvariantCulture)).Append(" cm, size ")
                   .Append(_size.ToString("0.000", CultureInfo.InvariantCulture)).Append(" t=").Append(Time.time.ToString("0.0", CultureInfo.InvariantCulture));
                Debug.Log(_sb.ToString());
            }
        }

        void OnBeat(GameBeat from, GameBeat to)
        {
            if (_beatFrames > 0)
                Debug.Log("[VBS][Body] beat " + GameIds.BeatName(from) + ": " + _beatFrames + " frames, penetrating " + _beatViol + " (max " + F(_beatMaxPen * 1000f) +
                          " mm), without the body " + _beatNomPen + " (max " + F(_beatMaxNom * 1000f) + " mm)");
            _beatFrames = _beatViol = _beatNomPen = 0;
            _beatMaxPen = _beatMaxNom = 0f;
            _nextScan = 0f; // new beat, new props
        }

        bool _summaryLogged;
        void LogSummary(string why)
        {
            if (_summaryLogged || _frames == 0) return;
            _summaryLogged = true;
            Debug.Log("[VBS][Body] summary (" + why + "): collide " + (collide ? "on" : "off") + ", size " + F(_size) + " m, margin " + F(_margin) + " m; " +
                      _frames + " frames measured, PENETRATING " + _violFrames + " (max " + F(_maxPen * 1000f) + " mm" + (_maxPenGroup >= 0 ? " into " + GroupName(_maxPenGroup) + " in " + _maxPenBeat : "") +
                      "); allowed contact " + _allowFrames + " (climb " + _climbFrames + ", ride " + _rideFrames + ", max " + F(_maxAllowed * 1000f) + " mm, ride into the rocket max " + F(_maxRideRocket * 1000f) + " mm); " +
                      "without the body " + _nomPenFrames + " frames would clip (max " + F(_maxNomPen * 1000f) + " mm" + (_maxNomGroup >= 0 ? " " + GroupName(_maxNomGroup) + " in " + _maxNomBeat : "") +
                      "); visual jumps over 8% of his size " + _jumpFrames + " (max " + F(_maxJump * 1000f) + " mm in " + _maxJumpBeat +
                      "); spring " + _softFrames + " frames, hard stop " + _hardFrames + " (max " + F(_maxHard * 1000f) + " mm" + (_maxHardGroup >= 0 ? " " + GroupName(_maxHardGroup) : "") +
                      "); bumps " + _bumps + "; goals moved " + _goalsMoved + " (max " + F(_goalMaxMove * 100f) + " cm); routed flights " + _routed + ", no detour " + _routeFail +
                      "; obstacle scans " + _scanCount + ", parts " + _parts.Count);
        }

        static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";
    }
}
