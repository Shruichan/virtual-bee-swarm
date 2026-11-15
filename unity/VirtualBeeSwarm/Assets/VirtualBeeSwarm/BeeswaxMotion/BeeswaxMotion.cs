using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// How Beeswax moves: quizmaster staging (Trivia Bot inspired, done as a union shop steward). Script/beats decide WHERE he
    /// goes (FlyTo, GoPresent, AttachTo); this decides HOW: arrival flights with a hanging settle, taking the floor for a
    /// question, tabling the choices, holding the floor while the player decides, the take on a pick, motion carried /
    /// grievance, adjourning. Root motion through BeeswaxController.RootDriver, in-place moves through Body* (the caption,
    /// on the root, stays steady). Every frame the comfort governor (MotionGovernor) runs last.
    /// Installs itself on the BeeswaxController after the scene loads. Off: env VBS_BEESWAX_MOTION=off, tuning.json
    /// motion.on=false, or disabling the component (= today's flight code). motion.intensity scales the in-place moves.
    /// VBS_MOTION_TRACE=1 writes a per-frame CSV (VBS_MOTION_TRACE_OUT, default persistentDataPath/motion-trace.csv).
    /// Logs "[VBS][Motion] ...". Never moves the camera, never delays or gates a game event.
    /// </summary>
    [DefaultExecutionOrder(-5)]
    [DisallowMultipleComponent]
    public partial class BeeswaxMotion : MonoBehaviour, BeeswaxController.IRootDriver
    {
        // ------------------------------------------------------------ static API
        /// <summary>A big moment is playing (final answer, take, verdict, picture it, the lap, the map): skip idle rig moves.</summary>
        public static bool ShowMoment { get; private set; }
        public static BeeswaxMotion Instance { get; private set; }

        static readonly List<KeyValuePair<Transform, float>> KeepClearList = new List<KeyValuePair<Transform, float>>();

        /// <summary>Something the player must see (a card): Beeswax never parks in front of it. radius &lt;= 0 removes it.</summary>
        public static void KeepClear(Transform t, float radius)
        {
            for (int i = KeepClearList.Count - 1; i >= 0; i--)
                if (KeepClearList[i].Key == null || KeepClearList[i].Key == t) KeepClearList.RemoveAt(i);
            if (t != null && radius > 0f) KeepClearList.Add(new KeyValuePair<Transform, float>(t, radius));
        }

        // ------------------------------------------------------------ config + install
        static bool _cfgOn = true, _cfgTrace;
        static float _cfgIntensity = 1f;
        static string _cfgFrom = "defaults";

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
            _cfgIntensity = 1f;
            if (flat.TryGetValue("motion.on", out var on)) _cfgOn = !(on == "false" || on == "0" || on == "off");
            if (flat.TryGetValue("motion.intensity", out var k) && float.TryParse(k, NumberStyles.Float, CultureInfo.InvariantCulture, out float kv))
                _cfgIntensity = Mathf.Clamp(kv, 0f, 1.5f);
            if (flat.TryGetValue("motion.trace", out var tr)) _cfgTrace = tr == "true" || tr == "1";
            string env = Environment.GetEnvironmentVariable("VBS_BEESWAX_MOTION");
            if (!string.IsNullOrEmpty(env)) _cfgOn = !(env == "off" || env == "0" || env == "false");
            string envK = Environment.GetEnvironmentVariable("VBS_MOTION_INTENSITY");
            if (!string.IsNullOrEmpty(envK) && float.TryParse(envK, NumberStyles.Float, CultureInfo.InvariantCulture, out float ek)) _cfgIntensity = Mathf.Clamp(ek, 0f, 1.5f);
            if (Environment.GetEnvironmentVariable("VBS_MOTION_TRACE") == "1") _cfgTrace = true;
        }

        static void ReadTuning(string path, Dictionary<string, string> into, List<string> from)
        {
            try
            {
                if (!File.Exists(path)) return;
                VirtualBeeSwarm.AR.ArTuning.FlatJson.Parse(File.ReadAllText(path), into);
                from.Add(path);
            }
            catch (Exception e) { Debug.LogWarning("[VBS][Motion] tuning: could not read " + path + ": " + e.Message); }
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
            if (b == null || b.GetComponent<BeeswaxMotion>() != null) return;
            if (!_cfgOn)
            {
                Debug.Log("[VBS][Motion] off (VBS_BEESWAX_MOTION=off or motion.on=false in " + _cfgFrom + "): today's flight code");
                return;
            }
            var m = b.gameObject.AddComponent<BeeswaxMotion>();
            m.intensity = _cfgIntensity;
            m._trace = _cfgTrace;
        }

        // ------------------------------------------------------------ inspector
        [Range(0f, 1.5f)] public float intensity = 1f;
        [SerializeField] bool _trace;
        [SerializeField] bool logPrimitives = true;

        // ------------------------------------------------------------ scene
        BeeswaxController _b;
        GameNet _net;
        GameDirector _dir;
        BeeswaxRig _rig;
        HiveBody _hive;
        Transform _anchor, _bodyT, _window, _scoreCard, _map, _rocket;
        StationBase[] _stations = new StationBase[0];
        ChoiceButton[] _buttons = new ChoiceButton[0];

        readonly MotionTrackPlayer _player = new MotionTrackPlayer();
        float _mt;                 // motion clock (stops during the hitstop)
        float _hitstopUntil = -1f;
        MotionSample _cur, _ambient;
        MotionGovernor.Ctx _ctx;
        bool _hasEye, _warnedEye;
        float _eyeDist = 9f;
        float _bobPhase, _bobAmp, _bobValue;
        Vector3 _offPrev;
        bool _active = true, _dropAct, _perchFace;
        float _perchYaw, _perchYawV, _perchLevel;
        Vector3 _perchLast;
        bool _perchSeen;
        Transform _lastGoalT;

        // ------------------------------------------------------------ lifecycle
        void Awake()
        {
            _b = GetComponent<BeeswaxController>();
            _net = FindAnyObjectByType<GameNet>();
            _dir = FindAnyObjectByType<GameDirector>();
            _rig = GetComponentInChildren<BeeswaxRig>(true);
            _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            _anchor = transform.parent;
            _bodyT = _b != null && _b.BodyTransform != null ? _b.BodyTransform : transform.Find("Body");
            var w = FindAnyObjectByType<FutureWindow>(FindObjectsInactive.Include);
            _window = w != null ? w.PaneTransform : null;
            _scoreCard = FindUnder(_anchor, "ScoreCard");
            _map = FindUnder(_anchor, "LevelMap");
            var perch = FindUnder(_anchor, "Perch");
            _rocket = perch != null ? perch.parent : FindUnder(_anchor, "HONEYMOON-1");
            _stations = FindObjectsByType<StationBase>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            _buttons = FindObjectsByType<ChoiceButton>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            _yaw = transform.eulerAngles.y;
            _pitch = Mathf.DeltaAngle(0f, transform.eulerAngles.x);
            OpenTrace();
        }

        static Transform FindUnder(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var f = FindUnder(root.GetChild(i), name);
                if (f != null) return f;
            }
            return null;
        }

        void OnEnable()
        {
            Instance = this;
            if (_b == null) return;
            _b.RootDriver = this;
            _b.Ordered += OnOrdered;
            if (_net != null)
            {
                _net.VoiceLineStarted += OnLine;
                _net.SceneChanged += OnScene;
                _net.ChoiceRequested += OnChoiceRequested;
            }
            if (_dir != null) _dir.BeatChanged += OnBeat;
            for (int i = 0; i < _buttons.Length; i++) if (_buttons[i] != null) _buttons[i].Pressed += OnPressed;
            Application.logMessageReceived += OnLog;
            _lastDriveFrame = -10;
            Vector3 eye = _b.HeadTransform != null ? _b.HeadTransform.position : Vector3.zero;
            Vector3 hive = _hive != null ? _hive.transform.position : transform.position;
            Debug.Log("[VBS][Motion] on: Beeswax moves like a quizmaster (intensity " + intensity.ToString("0.00", CultureInfo.InvariantCulture) +
                      ", tuning " + _cfgFrom + ", trace " + (_trace ? TracePath : "off") + "); eye to hive " +
                      (_b.HeadTransform != null ? Vector3.Distance(eye, hive).ToString("0.00", CultureInfo.InvariantCulture) + " m" : "? (no head)") +
                      ", rig " + (_rig != null ? "found" : "none") + ", " + _stations.Length + " stations, " + _buttons.Length + " choice buttons");
        }

        void OnDisable()
        {
            if (_b != null)
            {
                if (ReferenceEquals(_b.RootDriver, this)) _b.RootDriver = null;
                _b.Ordered -= OnOrdered;
                Neutral();
            }
            if (_net != null)
            {
                _net.VoiceLineStarted -= OnLine;
                _net.SceneChanged -= OnScene;
                _net.ChoiceRequested -= OnChoiceRequested;
            }
            if (_dir != null) _dir.BeatChanged -= OnBeat;
            for (int i = 0; i < _buttons.Length; i++) if (_buttons[i] != null) _buttons[i].Pressed -= OnPressed;
            Application.logMessageReceived -= OnLog;
            ShowMoment = false;
            if (Instance == this) Instance = null;
        }

        void OnDestroy()
        {
            LogSummary("end");
            CloseTrace();
        }

        void OnApplicationQuit() { LogSummary("quit"); CloseTrace(); }

        void Neutral()
        {
            _b.BodyOffset = Vector3.zero;
            _b.BodyTilt = Quaternion.identity;
            _b.BodySquash = Vector3.one;
            _b.BobScale = 1f;
            _offPrev = Vector3.zero;
        }

        GameBeat Beat => _dir != null ? _dir.Beat : GameBeat.Pre;
        string BeatName => GameIds.BeatName(Beat);
        bool Flying => _fl != null;
        bool Free => _b != null && !_b.IsAttached && !_b.Climbing && !_b.Inside;

        // ------------------------------------------------------------ guards: a throw here never reaches the game
        bool _failed;
        int _orderSerial;
        bool _choosingNow;

        void Fail(string where, Exception e)
        {
            if (_failed) return;
            _failed = true;
            Debug.LogWarning("[VBS][Motion] " + where + " threw: module off for this run (today's flight code). " + e);
            enabled = false; // OnDisable hands the root back and neutralises the body
        }

        void OnOrdered(string what) { try { OnOrderedCore(what); } catch (Exception e) { Fail("order " + what, e); } }
        void OnBeat(GameBeat from, GameBeat to) { try { OnBeatCore(from, to); } catch (Exception e) { Fail("beat", e); } }
        void OnLine(GameNet.VoiceLineInfo info) { try { OnLineCore(info); } catch (Exception e) { Fail("line", e); } }
        void OnScene(string scene) { try { OnSceneCore(scene); } catch (Exception e) { Fail("scene", e); } }

        // ------------------------------------------------------------ frame (before the controller's Update)
        void Update()
        {
            try { Frame(); } catch (Exception e) { Fail("update", e); }
        }

        // the first frames belong to the game's start-up (a GC there garbles the forecast video error callback): stock code until then
        const int WarmFrames = 6;
        static bool Warming => Time.frameCount <= WarmFrames;

        void Frame()
        {
            if (_b == null || Warming) return;
            float dt = Time.deltaTime;
            bool on = intensity >= 0f && Beat != GameBeat.Field;
            if (on != _active)
            {
                _active = on;
                if (!on) { _player.CancelAll(_mt, true); _fl = null; Neutral(); Log("off", "beat " + BeatName + " (module off: today's flight code)"); }
            }
            if (!_active) { ShowMoment = false; return; }

            UpdateContext();
            bool hitstop = Time.time < _hitstopUntil;
            if (!hitstop) _mt += dt;

            Poll(dt);
            RunScheduled();

            if (!hitstop)
            {
                var s = default(MotionSample);
                var amb = default(MotionSample);
                _player.Evaluate(_mt, ref s, ref amb, _eyeDist);
                _cur = s;
                _ambient = amb;
            }
            FlushFinished();
            UpdateBob(dt, hitstop);
            ShowMoment = _dropAct || _player.Any(t => t.name == "FinalAnswer" || t.name == "Take" || t.name == "Carried" ||
                                                       t.name == "Grievance" || t.name == "PictureIt" || t.name == "FourForFour") ||
                         (_fl != null && _fl.kind == "lap");
            // the controller calls Drive (which applies the body) only when he is free; otherwise the body gets its tilt here
            if (!Free) ApplyBody(dt);
        }

        void UpdateContext()
        {
            var head = _b.HeadTransform;
            _hasEye = head != null;
            if (!_hasEye) { _eyeDist = 9f; return; }
            Vector3 hive = _hive != null ? _hive.transform.position : (_anchor != null ? _anchor.position : transform.position);
            _ctx = MotionGovernor.Ctx.Make(head.position, head.forward, hive, _ctxValid ? _ctx.forward : Vector3.zero);
            _ctxValid = true;
            _ctx.attached = _b.IsAttached;
            if (Vector3.Distance(head.position, hive) > 3f)
            {
                _hasEye = false;
                if (!_warnedEye) { _warnedEye = true; Debug.Log("[VBS][Motion] the head is " + Vector3.Distance(head.position, hive).ToString("0.0") + " m from the hive: comfort rules wait for a real player"); }
            }
            _eyeDist = Vector3.Distance(BodyPos, head.position);
        }

        bool _ctxValid;
        static bool ComfortMode => VirtualBeeSwarm.Stakes.StakesFlags.Comfort;
        Vector3 BodyPos => _bodyT != null ? _bodyT.position : transform.position;

        void UpdateBob(float dt, bool hitstop)
        {
            float amp = _b.BobAmplitude, hz = _b.BobHz;
            if (_dropAct || _perched || _b.IsAttached || _b.Inside) amp = 0f;
            else if (_player.Playing("Wilt")) { amp = 0.018f; hz = 0.7f; }
            else if (_player.Playing("Deflate")) { amp *= 0.5f; hz = 0.7f; }
            else if (_player.Playing("HoldFloor")) { amp *= 0.35f; hz = 0.9f; } // near-still: the tap and the "well?" lean read against it
            amp *= Mathf.Max(0f, 1f + _cur.bob);
            if (hitstop) return; // frozen bob
            _bobAmp = Mathf.Lerp(_bobAmp, amp, 1f - Mathf.Exp(-dt / 0.2f));
            _bobPhase = Mathf.Repeat(_bobPhase + 2f * Mathf.PI * hz * dt, 2f * Mathf.PI);
            _bobValue = Mathf.Sin(_bobPhase) * _bobAmp;
        }

        // ------------------------------------------------------------ body (in-place moves + G1/G2/G3 on the body point)
        void ApplyBody(float dt)
        {
            if (!_active) return;
            dt = Mathf.Min(dt, 1f / 30f);
            var s = _dropAct ? default : _cur;
            float k = Mathf.Clamp(intensity, 0f, 1.5f);
            if (ComfortMode) k *= 0.5f; // the game's comfort flag (F3 / VBS_STAKES_FLAGS): half the moves, no whole turns
            Vector3 p = transform.position;
            Vector3 z = _hasEye ? _ctx.eye - p : -transform.forward;
            z.y = 0f;
            z = z.sqrMagnitude > 1e-6f ? z.normalized : Vector3.back;
            Vector3 x = Vector3.Cross(z, Vector3.up);
            Vector3 amb = MotionGovernor.SteadyCaption(_ambient.stage, _net != null && _net.IsSpeaking) - _ambient.stage; // G6
            Vector3 off = (x * (s.stage.x + amb.x) + Vector3.up * (s.stage.y + amb.y) + z * (s.stage.z + amb.z) + s.world) * k;
            off.y += _bobValue;
            bool free = Free;
            if (!free) off = Vector3.zero;
            if (free && _hasEye)
            {
                float rootDist = Vector3.Distance(p, _ctx.eye);
                Vector3 q = MotionGovernor.Keep(p + off, _ctx, Mathf.Clamp(rootDist, MotionGovernor.HardCentre, MotionGovernor.SoftDistance), out string rule, p.y + 0.10f);
                if (rule != null && (q - (p + off)).sqrMagnitude > 1e-6f) Clamp("lean", rule, Vector3.Distance(q, p + off));
                q = MotionGovernor.LimitStep(p + _offPrev, q, dt, _ctx, MotionGovernor.MaxSpeed);
                // the distance rule runs last and is never speed-limited: backing away from the eye is always allowed
                Vector3 q2 = MotionGovernor.KeepDistance(q, _ctx, MotionGovernor.HardCentre);
                if ((q2 - q).sqrMagnitude > 1e-8f) { Clamp("lean", "distance-hard", Vector3.Distance(q2, q)); q = q2; }
                off = q - p;
            }
            _offPrev = off;
            _b.BodyOffset = transform.InverseTransformVector(off);
            Vector3 r = s.rot * k;
            r.x = Mathf.Clamp(r.x, -30f, 30f);
            r.z = Mathf.Clamp(r.z, -25f, 25f);
            float spinYaw = ComfortMode ? 0f : s.spinYaw, spinRoll = ComfortMode ? 0f : s.spinRoll;
            Vector3 squash = s.squash * k;
            if (!free)
            {
                // squeezing through the door / inside: the climb owns his shape. On a perch: pitch only (and the turn to the player)
                bool seated = _b.IsAttached && !_b.Climbing && !_b.Inside;
                r = seated ? new Vector3(r.x, 0f, 0f) : Vector3.zero;
                spinYaw = spinRoll = 0f;
                squash = Vector3.zero;
            }
            // sitting on a perch (the controller copies the perch's rotation): his body still turns to the player
            float perchYaw = 0f;
            if (!free && _perchFace && _b.IsAttached && _hasEye)
            {
                Vector3 l = transform.InverseTransformDirection(_ctx.eye - BodyPos);
                perchYaw = Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg;
            }
            _perchYaw = MotionCurves.SpringAngle(_perchYaw, ref _perchYawV, perchYaw, 8f, 1f, 240f, dt);
            Quaternion tilt = Quaternion.Euler(r.x, r.y + spinYaw + _perchYaw, r.z + spinRoll);
            // a perch that sits tipped (the parked rocket): he sits level to within 10 deg, so he does not look like sliding off.
            // Only while the seat is still: on the ride he leans with the rocket.
            float level = 0f;
            if (!free && _perchFace && _b.IsAttached && _bodyT != null)
            {
                float tipped = Vector3.Angle(transform.up, Vector3.up);
                bool still = _perchSeen && (transform.position - _perchLast).sqrMagnitude < (0.05f * dt) * (0.05f * dt) + 1e-8f;
                level = still && tipped > 10f ? 1f - 10f / tipped : 0f;
                _perchLast = transform.position; _perchSeen = true;
            }
            else _perchSeen = false;
            _perchLevel = Mathf.MoveTowards(_perchLevel, level, 1.5f * Mathf.Min(dt, 1f / 30f));
            if (_perchLevel > 1e-3f && _bodyT != null && _bodyT.parent == transform)
            {
                // world correction W, written in the body's own frame: tilt' = M^-1 W M tilt (M = root * the body's base rotation)
                Quaternion m = transform.rotation * (_bodyT.localRotation * Quaternion.Inverse(_b.BodyTilt));
                Quaternion w = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(transform.up, Vector3.up), _perchLevel);
                tilt = Quaternion.Inverse(m) * w * m * tilt;
            }
            _b.BodyTilt = tilt;
            _b.BodySquash = Vector3.one + squash;
            _b.BobScale = 0f; // the module owns the bob (frozen in the hitstop, slower while waiting)
        }

        // ------------------------------------------------------------ root (called by BeeswaxController.Update)
        int _lastDriveFrame = -10;
        float _yaw, _pitch, _yawV, _pitchV, _snapUntil = -1f;
        Vector3 _lastRoot;

        public bool Drive(BeeswaxController b, Vector3 goal, float dt, out bool arrived)
        {
            arrived = false;
            if (!_active || !enabled || _failed || Warming) return false;
            try { return DriveCore(b, goal, dt, out arrived); }
            catch (Exception e) { Fail("drive", e); arrived = false; return false; }
        }

        Vector3 _govOff; // the rate-limited part of the comfort correction (behind / above), on top of the flight or the mark

        bool DriveCore(BeeswaxController b, Vector3 goal, float dt, out bool arrived)
        {
            arrived = false;
            if (Time.frameCount - _lastDriveFrame > 1)
            {
                _govOff = Vector3.zero;
                // he was attached / climbing / snapped: pick up from where the controller left him
                _yaw = transform.eulerAngles.y;
                _pitch = Mathf.DeltaAngle(0f, transform.eulerAngles.x);
                _yawV = _pitchV = 0f;
                _lastRoot = transform.position;
            }
            _lastDriveFrame = Time.frameCount;

            Vector3 g = Govern(goal);
            Vector3 p = transform.position - _govOff;
            if (b.GoalTransform != null) _lastGoalT = b.GoalTransform;
            if (_fl == null && !b.Arrived)
            {
                if (Plan(goal, g)) { arrived = true; p = transform.position; _govOff = Vector3.zero; }
            }
            else if (_fl != null && _fl.order != _orderSerial && (goal - _fl.rawGoal).sqrMagnitude > 0.02f * 0.02f)
            {
                _fl = null; // a new order mid-flight: from here
                if (Plan(goal, g)) { arrived = true; p = transform.position; _govOff = Vector3.zero; }
            }

            if (_fl != null)
            {
                // a goal that drifts (a moving rocket, the held hive) just moves the landing: no restart
                _fl.order = _orderSerial;
                _fl.rawGoal = goal;
                // the landing spot eases over (a side-step solved mid-flight never yanks the path); the park glide finishes it
                _fl.end = Vector3.MoveTowards(_fl.end, g, MotionGovernor.BackOffSpeed * Mathf.Min(dt, 1f / 30f));
                p = StepFlight(_fl, dt, out bool done);
                if (done) { Landed(_fl); _fl = null; arrived = true; }
            }
            else
            {
                float gap = Vector3.Distance(p, g);
                // stepping aside for what is on show is a small flight (hop + settle); going back is a quiet glide
                if (gap > 0.10f && b.Arrived && Time.time - _parkedSince > 0.3f && _clearOff != Vector3.zero)
                {
                    StartFlight(MakeP1(transform.position, goal, g, "reposition", false), "reposition (" + _lastGovRule + ")");
                    p = StepFlight(_fl, dt, out _);
                }
                else
                {
                    Vector3 want = Vector3.Lerp(p, g, 1f - Mathf.Exp(-dt / 0.15f));
                    p = _hasEye ? MotionGovernor.LimitStep(p, want, dt, _ctx, MotionGovernor.BackOffSpeed) : want;
                    arrived = gap < 0.02f || b.Arrived;
                }
            }
            if (_hasEye)
            {
                // behind / above: eased in at 0.6 m/s (never a dart across the table); only true distance may snap
                Vector3 want = MotionGovernor.Keep(p, _ctx, MotionGovernor.HardCentre, out string rule, goal.y) - p;
                if (rule != null && want.sqrMagnitude > 1e-6f) Clamp("root", rule, want.magnitude); else want = Vector3.zero;
                _govOff = Vector3.MoveTowards(_govOff, want, MotionGovernor.BackOffSpeed * Mathf.Min(dt, 1f / 30f));
                p += _govOff;
                Vector3 d1 = MotionGovernor.KeepDistance(p, _ctx, MotionGovernor.HardCentre);
                if ((d1 - p).sqrMagnitude > 1e-8f) { Clamp("root", "distance-hard", Vector3.Distance(d1, p)); _govOff += d1 - p; p = d1; }
            }
            else _govOff = Vector3.zero;
            transform.position = p;
            if (arrived && _parkedSince < 0f) _parkedSince = Time.time;
            if (_fl != null) _parkedSince = -1f;

            Face(dt);
            ApplyBody(dt);
            _lastRoot = p;
            return true;
        }

        void Face(float dt)
        {
            Vector3 p = transform.position;
            Vector3 want = _hasEye ? _ctx.eye - p : transform.forward;
            if (_fl != null && _fl.FaceTravel && _fl.vel.sqrMagnitude > 1e-4f)
            {
                want = _fl.vel;
                float vy = Mathf.Clamp(want.y, -0.4f * new Vector2(want.x, want.z).magnitude, 0.4f * new Vector2(want.x, want.z).magnitude);
                want.y = vy;
            }
            else if (!_b.LooksAtPlayer) want = transform.forward;
            if (!_dropAct && _cur.lookW > 0.001f)
            {
                Vector3 to = _cur.lookAt - p;
                if (to.sqrMagnitude > 1e-6f && want.sqrMagnitude > 1e-6f)
                    want = Vector3.Slerp(want.normalized, to.normalized, Mathf.Clamp01(_cur.lookW));
            }
            if (want.sqrMagnitude < 1e-8f) return;
            float tYaw = Mathf.Atan2(want.x, want.z) * Mathf.Rad2Deg;
            if (_hasEye && _b.LooksAtPlayer && !(_fl != null && _fl.FaceTravel))
            {
                // a look at something never shows the player his back: at most 60 deg off them, one eye stays visible
                Vector3 toP = _ctx.eye - p;
                float pYaw = Mathf.Atan2(toP.x, toP.z) * Mathf.Rad2Deg;
                tYaw = pYaw + Mathf.Clamp(Mathf.DeltaAngle(pYaw, tYaw), -60f, 60f);
            }
            float tPitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(want.normalized.y, -1f, 1f)) * Mathf.Rad2Deg, -45f, 45f);
            float zeta = Time.time < _snapUntil ? 0.55f : 1f;
            _yaw = MotionCurves.SpringAngle(_yaw, ref _yawV, tYaw, 10f, zeta, 300f, dt);
            _pitch = MotionCurves.SpringAngle(_pitch, ref _pitchV, tPitch, 10f, 1f, 300f, dt);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        // ------------------------------------------------------------ G4/G5 goal
        string _lastGovRule = "";
        readonly List<MotionGovernor.Disc> _discs = new List<MotionGovernor.Disc>();

        Vector3 Govern(Vector3 raw)
        {
            Vector3 g = raw;
            var podium = _b.PresenterPoint;
            if (_hive != null && podium != null)
            {
                Vector3 hc = _hive.transform.position + Vector3.up * 0.15f;
                Vector3 h = MotionGovernor.HeldHive(g, hc, podium.position, _hive.Held);
                if (h != g) { g = h; _lastGovRule = "G5 held hive"; }
            }
            if (!_hasEye) return g;
            g = MotionGovernor.Keep(g, _ctx, MotionGovernor.SoftDistance, out string r1, raw.y);
            if (r1 != null) _lastGovRule = "G1/G2";
            // G4, latched: the side-step is solved when the mark or what is on show changes, or after 0.5 s of real
            // overlap. It does not follow the head frame by frame (no sliding with head sway, no flip-flop).
            ProtectedDiscs(_discs);
            int sig = _discs.Count + (_choosingNow ? 100 : 0);
            if (_discs.Count == 0) { _clearOff = Vector3.zero; _coverSince = -1f; _clearSig = sig; _clearRaw = raw; return g; }
            bool covers = MotionGovernor.Covers(g + _clearOff, _ctx, _discs, out _);
            if (!covers) _coverSince = -1f;
            else if (_coverSince < 0f) _coverSince = Time.time;
            if (sig != _clearSig || (raw - _clearRaw).sqrMagnitude > 0.02f * 0.02f || (covers && Time.time - _coverSince > 0.5f))
            {
                Vector3 c = MotionGovernor.ClearView(g, _ctx, _discs, out string what);
                if ((c - g - _clearOff).sqrMagnitude < 0.01f * 0.01f) what = null; // the same answer: nothing to say
                _clearOff = c - g;
                _clearSig = sig;
                _clearRaw = raw;
                _coverSince = -1f;
                if (what != null) { _lastGovRule = "G4 clear"; if (logPrimitives) Debug.Log("[VBS][Motion] side-step " + (_clearOff.magnitude * 100f).ToString("0.0", CultureInfo.InvariantCulture) + " cm: clear of " + what + " [beat " + BeatName + "]"); }
            }
            if (_clearOff != Vector3.zero) g = MotionGovernor.Keep(g + _clearOff, _ctx, MotionGovernor.SoftDistance, out _, raw.y);
            return g;
        }

        Transform _stampProp;
        Vector3 _clearOff, _clearRaw;
        int _clearSig = -1;
        float _coverSince = -1f;

        readonly Dictionary<Transform, Renderer[]> _rendererCache = new Dictionary<Transform, Renderer[]>();

        bool DiscOf(Transform t, float extra, string name, out MotionGovernor.Disc d)
        {
            d = default;
            if (t == null || !t.gameObject.activeInHierarchy) return false;
            if (!_rendererCache.TryGetValue(t, out var rs)) { rs = t.GetComponentsInChildren<Renderer>(false); _rendererCache[t] = rs; }
            bool any = false;
            Bounds b = default;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r is ParticleSystemRenderer) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any) b = new Bounds(t.position, Vector3.one * 0.1f);
            d = new MotionGovernor.Disc(b.center, Mathf.Min(0.35f, b.extents.magnitude) + extra, name);
            return true;
        }

        /// <summary>What the player must see right now (G4).</summary>
        void ProtectedDiscs(List<MotionGovernor.Disc> into)
        {
            into.Clear();
            float m = MotionGovernor.ClearMargin;
            if (_st != null && _del != null && _del.Active && !_st.Solved && (_del.Parked || _st.Choosing))
                for (int i = 0; i < _del.Count; i++)
                    if (DiscOf(_del.Item(i), m, "a choice", out var d)) { d.both = !_hiding; into.Add(d); } // a choice must not hide him either (unless he is hiding on purpose)
            if (_stampProp != null && Time.time - _takeAt < 0.6f && _stampProp.gameObject.activeInHierarchy)
                into.Add(new MotionGovernor.Disc(_stampProp.position, 0.18f + m, "the stamp", !_hiding)); // the APPROVED / GRIEVANCE stamp never lands on him
            bool windowTime = Beat == GameBeat.WhatIf || _forecastLine;
            if (windowTime && DiscOf(_window, m, "window", out var wd)) into.Add(wd);
            if (DiscOf(_scoreCard, m, "score card", out var sd)) into.Add(sd);
            if (DiscOf(_map, m, "map", out var md)) into.Add(md);
            for (int i = 0; i < KeepClearList.Count; i++)
            {
                var kv = KeepClearList[i];
                if (kv.Key != null && kv.Key.gameObject.activeInHierarchy) into.Add(new MotionGovernor.Disc(kv.Key.position, kv.Value + m, kv.Key.name));
            }
        }

        // ------------------------------------------------------------ logging
        readonly Dictionary<string, int> _counts = new Dictionary<string, int>();
        readonly Dictionary<string, float> _clampLogAt = new Dictionary<string, float>();
        int _clamps;

        void Log(string prim, string why)
        {
            if (!_counts.ContainsKey(prim)) _counts[prim] = 0;
            _counts[prim]++;
            if (!logPrimitives) return;
            Debug.Log("[VBS][Motion] " + prim + " <- " + why + " [beat " + BeatName + "] t=" + Time.time.ToString("0.00", CultureInfo.InvariantCulture) +
                      " eye=" + (_hasEye ? _eyeDist.ToString("0.000", CultureInfo.InvariantCulture) : "-"));
        }

        void Clamp(string part, string rule, float by)
        {
            _clamps++;
            _beatClamps++;
            if (_clampLogAt.TryGetValue(rule, out float at) && Time.time - at < 1f) return;
            _clampLogAt[rule] = Time.time;
            Debug.Log("[VBS][Motion] governor: " + part + " " + rule + " " + by.ToString("0.000", CultureInfo.InvariantCulture) + " m [beat " + BeatName + "]");
        }

        void FlushFinished()
        {
            var fin = _player.Finished;
            for (int i = 0; i < fin.Count; i++)
            {
                var t = fin[i];
                if (logPrimitives && t.why != null)
                    Debug.Log("[VBS][Motion] " + t.name + " done after " + (_mt - t.start).ToString("0.00", CultureInfo.InvariantCulture) + " s, min eye " +
                              (float.IsPositiveInfinity(t.minEye) ? "-" : t.minEye.ToString("0.000", CultureInfo.InvariantCulture)) + " m");
            }
            fin.Clear();
        }

        // ------------------------------------------------------------ per-beat stats + trace (after everything moved)
        float _beatMinEye = 9f, _beatMaxSpeed, _beatFast, _fastRun, _beatFastRunMax, _runMinEye = 9f;
        int _beatOverlap, _beatBehind, _beatClamps, _beatFrames, _beatJumps, _runJumps;
        bool _freePrev;
        Vector3 _lastBody;
        bool _haveLastBody;
        StreamWriter _traceW;
        string TracePath
        {
            get
            {
                string p = Environment.GetEnvironmentVariable("VBS_MOTION_TRACE_OUT");
                return string.IsNullOrEmpty(p) ? Path.Combine(Application.persistentDataPath, "motion-trace.csv") : p;
            }
        }

        void OpenTrace()
        {
            if (!_trace && !_cfgTrace) return;
            _trace = true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TracePath));
                _traceW = new StreamWriter(TracePath) { AutoFlush = false };
                _traceW.WriteLine("t,frame,beat,free,flight,tracks,x,y,z,eye,speed,gaze,overlap,behind,above,choosing,speaking");
            }
            catch (Exception e) { Debug.LogWarning("[VBS][Motion] trace: " + e.Message); _traceW = null; }
        }

        void CloseTrace()
        {
            try { _traceW?.Flush(); _traceW?.Dispose(); } catch (Exception) { }
            _traceW = null;
        }

        readonly System.Text.StringBuilder _sb = new System.Text.StringBuilder(256);

        void LateUpdate()
        {
            if (_b == null || !_active || Warming) return;
            Vector3 q = BodyPos;
            float dt = Time.deltaTime;
            bool free = Free && _b.Visible;
            float v = _haveLastBody && dt > 1e-5f ? Vector3.Distance(q, _lastBody) / dt : 0f;
            // an ordered teleport (attach, snap, reset, beat jump) is not motion; any other jump is a comfort violation
            bool teleport = Time.frameCount - _teleportFrame <= 1 && Time.frameCount >= _teleportFrame;
            bool jump = free && !teleport && v > 3f && _freePrev;
            if (free && !_freePrev) teleport = true; // first free frame after a perch / the climb
            _freePrev = free;
            if (jump) { _beatJumps++; _runJumps++; if (logPrimitives && _runJumps <= 20) Debug.Log("[VBS][Motion] JUMP " + v.ToString("0.0", CultureInfo.InvariantCulture) + " m/s in one frame [beat " + BeatName + "] t=" + Time.time.ToString("0.00", CultureInfo.InvariantCulture)); }
            _lastBody = q;
            _haveLastBody = true;
            if (!_hasEye) return;
            float eye = Vector3.Distance(q, _ctx.eye);
            float gaze = MotionGovernor.GazeAngle(q, _ctx);
            bool choosing = _st != null && _st.Choosing;
            string over = null;
            if (free) { ProtectedDiscs(_discs); MotionGovernor.Covers(q, _ctx, _discs, out over); }
            bool behind = free && Vector3.Dot(q - _ctx.eye, _ctx.forward) < MotionGovernor.MinForward - 0.005f;
            bool above = free && q.y > _ctx.eye.y + MotionGovernor.MaxAboveEye + 0.005f;
            if (free && !teleport)
            {
                _beatFrames++;
                if (eye < _beatMinEye) _beatMinEye = eye;
                if (eye < _runMinEye) _runMinEye = eye;
                if (v > _beatMaxSpeed && !jump) _beatMaxSpeed = v;
                if (v > MotionGovernor.MaxSpeed) { _beatFast += dt; _fastRun += dt; if (_fastRun > _beatFastRunMax) _beatFastRunMax = _fastRun; }
                else _fastRun = 0f;
                if (over != null && choosing) _beatOverlap++;
                if (behind || above) _beatBehind++;
            }
            if (_traceW == null) return;
            _sb.Clear();
            var ci = CultureInfo.InvariantCulture;
            _sb.Append(Time.time.ToString("0.000", ci)).Append(',').Append(Time.frameCount).Append(',').Append(BeatName).Append(',')
               .Append(free && !teleport ? 1 : 0).Append(',').Append(_fl != null ? _fl.kind : "").Append(',');
            var tr = _player.Tracks;
            for (int i = 0; i < tr.Count; i++) { if (i > 0) _sb.Append('+'); _sb.Append(tr[i].name); }
            _sb.Append(',').Append(q.x.ToString("0.0000", ci)).Append(',').Append(q.y.ToString("0.0000", ci)).Append(',').Append(q.z.ToString("0.0000", ci))
               .Append(',').Append(eye.ToString("0.0000", ci)).Append(',').Append(v.ToString("0.000", ci)).Append(',').Append(gaze.ToString("0.0", ci))
               .Append(',').Append(over ?? "").Append(',').Append(behind ? 1 : 0).Append(',').Append(above ? 1 : 0)
               .Append(',').Append(choosing ? 1 : 0).Append(',').Append(_net != null && _net.IsSpeaking ? 1 : 0);
            try { _traceW.WriteLine(_sb.ToString()); } catch (Exception) { _traceW = null; }
        }

        void LogBeatStats(GameBeat beat)
        {
            if (_beatFrames > 0)
                Debug.Log("[VBS][Motion] beat " + GameIds.BeatName(beat) + ": min eye " + _beatMinEye.ToString("0.000", CultureInfo.InvariantCulture) +
                          " m, max speed " + _beatMaxSpeed.ToString("0.00", CultureInfo.InvariantCulture) + " m/s, above 0.9 m/s " +
                          _beatFast.ToString("0.00", CultureInfo.InvariantCulture) + " s (longest " + _beatFastRunMax.ToString("0.00", CultureInfo.InvariantCulture) +
                          " s), overlap while choosing " + _beatOverlap + " frames, behind/above " + _beatBehind + " frames, clamps " + _beatClamps + ", jumps " + _beatJumps);
            _beatMinEye = 9f; _beatMaxSpeed = _beatFast = _fastRun = _beatFastRunMax = 0f; _beatOverlap = _beatBehind = _beatClamps = _beatFrames = _beatJumps = 0;
        }

        bool _summaryLogged;
        void LogSummary(string why)
        {
            if (_summaryLogged || _counts.Count == 0) return;
            _summaryLogged = true;
            var keys = new List<string>(_counts.Keys);
            keys.Sort(StringComparer.Ordinal);
            var parts = new List<string>();
            foreach (var k in keys) parts.Add(k + "=" + _counts[k]);
            Debug.Log("[VBS][Motion] summary (" + why + "): " + string.Join(" ", parts) + "; min eye " + _runMinEye.ToString("0.000", CultureInfo.InvariantCulture) +
                      " m, governor clamps " + _clamps + ", jumps " + _runJumps);
        }
    }
}
