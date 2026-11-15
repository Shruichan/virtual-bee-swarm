using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualBeeSwarm.AR;
using VirtualBeeSwarm.Stakes;

namespace VirtualBeeSwarm.ArFirst
{
    /// <summary>
    /// AR first: the player's real room stays the stage. This sets nothing up itself; it keeps the other systems from
    /// turning the room into VR, at runtime, without editing their files:
    ///   dims       the Moon's passthrough dim is capped (T0Sequence.passthroughDim -> arfirst.moonDim 0.4) and every
    ///              IPassthroughDimmer keeps at least arfirst.minOpacity 0.35 of the room, so the room is never hidden;
    ///              the stations' stakes dim is capped at arfirst.dimStations 0.08 (unless tuning.json sets its own).
    ///   Moon       the window's swell is clamped so the frame stays between the floor and the real ceiling
    ///              (FutureWindow.moonScale / growScale, from the room's ceiling), instead of 2.4x through the ceiling.
    ///   occlusion  Depth API occlusion is soft by default when the headset supports it and tuning.json doesn't say
    ///              otherwise (ArTuning.Set("ar.occlusion","soft")). A frame-rate guard steps it down soft -> hard ->
    ///              off if the headset can't hold ~80% of its refresh rate, so an untested default can't sink the demo.
    ///   wall       walls up to 5 m behind the hive and up to 35 degrees off count (venue halls); when no wall is found
    ///              the window goes back on the table (ar.windowOnWall off for this run) instead of floating in the air.
    ///   anchoring  the placement plate fades out when the ride starts (only its contact shadow stays); the window gets
    ///              a soft drop shadow on the wall so it reads as mounted, not a sticker.
    /// Flags (tuning.json, same files as ArTuning): arfirst.on, arfirst.occlusion auto|keep, arfirst.moonDim,
    /// arfirst.minOpacity, arfirst.dimStations, arfirst.wall, arfirst.plateFade, arfirst.windowShadow.
    /// Installs itself into any scene with a GameDirector. Log: "[VBS][ArFirst] ...".
    /// </summary>
    [DefaultExecutionOrder(80)]
    public class ArFirstDirector : MonoBehaviour
    {
        public static bool AutoInstall = true;
        public static ArFirstDirector Instance { get; private set; }

        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        // flags
        bool _occlusionAuto = true, _wall = true, _plateFade = true, _windowShadow = true;
        float _moonDim = 0.4f, _minOpacity = 0f, _dimStations = 0.08f; // one Moon cap (moonDim); minOpacity only if tuning.json asks
        readonly Dictionary<string, string> _file = new Dictionary<string, string>();

        GameDirector _director;
        FutureWindow _window;
        T0Sequence _t0;
        Renderer _plate;
        Color _plateColor = new Color(0.35f, 0.3f, 0.22f, 0.55f);
        float _plateAlpha = 1f, _plateTarget = 1f;
        bool _subscribed, _capsLogged, _occlusionTried, _windowFlipped, _flipWindow, _moonLogged;
        Transform _shadow;
        Bounds _frameLocal;
        bool _frameMeasured;
        float _moonScale0 = -1f, _growScale0 = -1f;

        List<MonoBehaviour> _dimmers;

        // frame-rate guard for the occlusion default
        bool _guard;
        float _guardFrom, _guardFrames, _guardSeconds;
        int _guardBad;

        public string OcclusionState { get; private set; } = "untouched";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
            AutoInstall = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!AutoInstall) return;
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<ArFirstDirector>() != null) return;
            var go = new GameObject("ArFirst");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<ArFirstDirector>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { enabled = false; return; }
            Instance = this;
            ReadFlags();
            if (_file.TryGetValue("arfirst.on", out var on) && IsOff(on))
            {
                Debug.Log("[VBS][ArFirst] off (tuning.json)");
                enabled = false;
                return;
            }
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            _window = FindAnyObjectByType<FutureWindow>(FindObjectsInactive.Include);
            _t0 = FindAnyObjectByType<T0Sequence>(FindObjectsInactive.Include);
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (r.name.StartsWith("BasePlate", StringComparison.Ordinal)) { _plate = r; break; }
            if (_plate != null && _plate.sharedMaterial != null && _plate.sharedMaterial.HasProperty(GameMaterials.ColorId))
                _plateColor = _plate.sharedMaterial.GetColor(GameMaterials.ColorId);
            ApplyCaps();
        }

        void OnEnable() => Subscribe();
        void Start() { Subscribe(); ApplyCaps(); BuildWindowShadow(); }

        void Subscribe()
        {
            if (_subscribed || !enabled) return;
            if (_director == null) _director = GameDirector.Instance;
            if (_director == null) return;
            _director.BeatChanged += HandleBeat;
            var rig = ArRig.Instance;
            if (rig != null) rig.RoomChanged += HandleRoom;
            _subscribed = true;
        }

        void OnDisable()
        {
            if (!_subscribed) return;
            if (_director != null) _director.BeatChanged -= HandleBeat;
            var rig = ArRig.Instance;
            if (rig != null) rig.RoomChanged -= HandleRoom;
            _subscribed = false;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ flags
        void ReadFlags()
        {
            _file.Clear();
#if !UNITY_ANDROID || UNITY_EDITOR
            ReadFile(Path.Combine(Application.streamingAssetsPath, "tuning.json"));
#endif
            ReadFile(Path.Combine(Application.persistentDataPath, "tuning.json"));
            if (_file.TryGetValue("arfirst.occlusion", out var o)) _occlusionAuto = o.Trim().ToLowerInvariant() != "keep" && !IsOff(o);
            if (_file.TryGetValue("arfirst.wall", out var w)) _wall = !IsOff(w);
            if (_file.TryGetValue("arfirst.plateFade", out var p)) _plateFade = !IsOff(p);
            if (_file.TryGetValue("arfirst.windowShadow", out var s)) _windowShadow = !IsOff(s);
            Num("arfirst.moonDim", 0f, 1f, ref _moonDim);
            Num("arfirst.minOpacity", 0f, 1f, ref _minOpacity);
            Num("arfirst.dimStations", 0f, 1f, ref _dimStations);
        }

        void ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var flat = new Dictionary<string, string>();
                ArTuning.FlatJson.Parse(File.ReadAllText(path), flat);
                foreach (var kv in flat) _file[kv.Key] = (kv.Value ?? "").Trim().Trim('"');
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VBS][ArFirst] tuning: could not read " + path + ": " + e.Message);
            }
        }

        static bool IsOff(string v)
        {
            switch ((v ?? "").Trim().ToLowerInvariant())
            {
                case "0": case "off": case "false": case "no": return true;
                default: return false;
            }
        }

        void Num(string key, float min, float max, ref float into)
        {
            if (_file.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, C, out var f)) into = Mathf.Clamp(f, min, max);
        }

        // ------------------------------------------------------------------ the room is never hidden
        void ApplyCaps()
        {
            string log = "";
            if (_t0 != null)
            {
                var f = typeof(T0Sequence).GetField("passthroughDim", F);
                if (f != null && f.GetValue(_t0) is float cur && cur > _moonDim)
                {
                    f.SetValue(_t0, _moonDim);
                    log += " moon dim " + cur.ToString("0.##", C) + " -> " + _moonDim.ToString("0.##", C) + ";";
                }
            }
            if (_dimmers == null)
            {
                _dimmers = new List<MonoBehaviour>();
                foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (mb is IPassthroughDimmer) _dimmers.Add(mb);
            }
            foreach (var mb in _dimmers)
            {
                if (mb == null || _minOpacity <= 0f) continue;
                var f = mb.GetType().GetField("minOpacity", F);
                if (f != null && f.GetValue(mb) is float cur && cur < _minOpacity)
                {
                    f.SetValue(mb, _minOpacity);
                    log += " " + mb.GetType().Name + " min opacity " + cur.ToString("0.##", C) + " -> " + _minOpacity.ToString("0.##", C) + ";";
                }
            }
            if (!_file.ContainsKey("stakes.dimStations") && StakesFlags.DimStations > _dimStations)
            {
                log += " stakes dim (stations) " + StakesFlags.DimStations.ToString("0.##", C) + " -> " + _dimStations.ToString("0.##", C) + ";";
                StakesFlags.DimStations = _dimStations;
            }
            if (_wall)
            {
                if (!_file.ContainsKey("ar.wallMaxDistance") && ArTuning.WallMaxDistance < 5f && ArTuning.Set("ar.wallMaxDistance", "5"))
                    log += " wall max distance -> 5 m;";
                var rig = ArRig.Instance;
                var f = typeof(ArRig).GetField("wallMaxAngle", F);
                if (rig != null && f != null && f.GetValue(rig) is float ang && ang < 35f)
                {
                    f.SetValue(rig, 35f);
                    log += " wall max angle " + ang.ToString("0", C) + " -> 35 deg;";
                }
            }
            if (log.Length > 0 || !_capsLogged) Debug.Log("[VBS][ArFirst] caps:" + (log.Length > 0 ? log : " nothing to change"));
            _capsLogged = true;
        }

        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            ApplyCaps();
            _plateTarget = beat == GameBeat.Pre ? 1f : 0f;
            if (beat == GameBeat.Pre && _windowFlipped)
            {
                // a new placement gets a new chance at a wall
                _windowFlipped = false;
                ArTuning.Set("ar.windowOnWall", "true");
            }
            if (beat == GameBeat.Escape || beat == GameBeat.T0) ClampWindowSwell();
        }

        void HandleRoom(RoomModel room)
        {
            var rig = ArRig.Instance;
            if (!_wall || rig == null || rig.DesktopMode || room == null) return;
            if (!room.HasWall && ArTuning.WindowOnWall && !_file.ContainsKey("ar.windowOnWall") &&
                rig.WindowState.StartsWith("free-standing", StringComparison.Ordinal))
            {
                _flipWindow = true; // next Update: not from inside the rig's own RoomChanged
            }
        }

        // ------------------------------------------------------------------ the Moon stays inside the room
        void MeasureFrame()
        {
            if (_frameMeasured || _window == null) return;
            Transform root = typeof(FutureWindow).GetField("portalRoot", F)?.GetValue(_window) as Transform;
            if (root == null) root = _window.transform;
            bool any = false;
            var b = new Bounds();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.transform == _shadow) continue;
                var rel = Matrix4x4.identity;
                for (Transform t = mf.transform; t != null && t != root; t = t.parent)
                    rel = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale) * rel;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 p = rel.MultiplyPoint3x4(corner);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            if (!any) return;
            _frameLocal = b;
            _frameMeasured = true;
        }

        /// <summary>World half-height of the window frame at size 1 (open, not grown).</summary>
        float FrameHalfHeight()
        {
            MeasureFrame();
            if (!_frameMeasured) return 0f;
            Transform root = typeof(FutureWindow).GetField("portalRoot", F)?.GetValue(_window) as Transform;
            if (root == null) root = _window.transform;
            Vector3 baseScale = Vector3.one;
            if (typeof(FutureWindow).GetField("_baseScale", F)?.GetValue(_window) is Vector3 bs) baseScale = bs;
            float parent = root.parent != null ? Mathf.Abs(root.parent.lossyScale.y) : 1f;
            return _frameLocal.extents.y * Mathf.Abs(baseScale.y) * parent;
        }

        void ClampWindowSwell()
        {
            if (_window == null) return;
            var fMoon = typeof(FutureWindow).GetField("moonScale", F);
            var fGrow = typeof(FutureWindow).GetField("growScale", F);
            if (fMoon == null || fGrow == null) return;
            if (_moonScale0 < 0f) { _moonScale0 = (float)fMoon.GetValue(_window); _growScale0 = (float)fGrow.GetValue(_window); }
            float half = FrameHalfHeight();
            if (half < 0.02f) return;
            var rig = ArRig.Instance;
            float ceiling = rig != null ? rig.Room.CeilingOrDefault : 2.4f, floor = rig != null ? rig.Room.FloorY : 0f;
            Transform root = typeof(FutureWindow).GetField("portalRoot", F)?.GetValue(_window) as Transform;
            float cy = (root != null ? root.position.y : _window.transform.position.y) + _frameLocal.center.y * (half / Mathf.Max(1e-4f, _frameLocal.extents.y));
            float fit = Mathf.Min((ceiling - 0.1f - cy) / half, (cy - floor - 0.25f) / half);
            float moon = Mathf.Clamp(fit, Mathf.Min(1.25f, _moonScale0), _moonScale0); // only ever shrinks
            float grow = Mathf.Clamp(fit, Mathf.Min(1.1f, _growScale0), _growScale0);
            fMoon.SetValue(_window, moon);
            fGrow.SetValue(_window, grow);
            if (!_moonLogged || moon < _moonScale0 - 0.01f)
                Debug.Log("[VBS][ArFirst] window swell: frame half-height " + half.ToString("0.00", C) + " m at y " + cy.ToString("0.00", C) +
                          ", ceiling " + ceiling.ToString("0.00", C) + " -> moon x" + moon.ToString("0.00", C) + " (was x" + _moonScale0.ToString("0.0", C) +
                          "), grow x" + grow.ToString("0.00", C) + " (was x" + _growScale0.ToString("0.0", C) + ")");
            _moonLogged = true;
        }

        // ------------------------------------------------------------------ the window is mounted, not a sticker
        void BuildWindowShadow()
        {
            if (!_windowShadow || _window == null || _shadow != null) return;
            MeasureFrame();
            if (!_frameMeasured) return;
            // its own object that follows the portal (not a child: whatever restyles the portal's renderers leaves it alone)
            var go = new GameObject("WindowDropShadow (ArFirst)");
            _shadow = go.transform;
            go.AddComponent<MeshFilter>().sharedMesh = SoftEllipse();
            var mat = GameMaterials.Create("VBS_WindowShadow (runtime)", Color.white, GameMaterials.Blend.Alpha, vertexColors: true, doubleSided: true);
            mat.renderQueue = (int)RenderQueue.Transparent - 10;
            ArMaterials.MarkNeverOccluded(mat);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            FollowWindow();
        }

        /// <summary>The shadow sits just behind the frame (the window's +Z goes into the wall), a little low and to the
        /// right like light from above, a rim wider than the frame.</summary>
        void FollowWindow()
        {
            if (_shadow == null || _window == null) return;
            Transform root = typeof(FutureWindow).GetField("portalRoot", F)?.GetValue(_window) as Transform;
            if (root == null) root = _window.transform;
            var rig = ArRig.Instance;
            bool show = root.gameObject.activeInHierarchy && (rig == null || (rig.Room.HasWall && ArTuning.WindowOnWall));
            if (_shadow.gameObject.activeSelf != show) _shadow.gameObject.SetActive(show);
            if (!show) return;
            Vector3 local = new Vector3(_frameLocal.center.x + _frameLocal.extents.x * 0.04f, _frameLocal.center.y - _frameLocal.extents.y * 0.06f,
                                        _frameLocal.center.z + 0.004f);
            Vector3 ls = root.lossyScale;
            _shadow.SetPositionAndRotation(root.TransformPoint(local), root.rotation);
            _shadow.localScale = new Vector3(_frameLocal.size.x * 1.16f * Mathf.Abs(ls.x), _frameLocal.size.y * 1.16f * Mathf.Abs(ls.y), 1f);
        }

        /// <summary>A unit ellipse in XY whose darkness is in the vertex colours: even in the middle, nothing at the rim.</summary>
        static Mesh SoftEllipse()
        {
            const int N = 28;
            var v = new List<Vector3>(1 + N * 2);
            var c = new List<Color32>(1 + N * 2);
            var t = new List<int>(N * 9);
            var dark = new Color32(0, 0, 0, 96);
            var none = new Color32(0, 0, 0, 0);
            v.Add(Vector3.zero); c.Add(dark);
            for (int i = 0; i < N; i++)
            {
                float a = i * Mathf.PI * 2f / N;
                var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * 0.5f;
                v.Add(d * 0.72f); c.Add(dark);
                v.Add(d); c.Add(none);
            }
            for (int i = 0; i < N; i++)
            {
                int i0 = 1 + i * 2, o0 = i0 + 1, i1 = 1 + ((i + 1) % N) * 2, o1 = i1 + 1;
                t.Add(0); t.Add(i0); t.Add(i1);
                t.Add(i0); t.Add(o0); t.Add(o1);
                t.Add(i0); t.Add(o1); t.Add(i1);
            }
            var m = new Mesh { name = "VBS_SoftEllipse" };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------ frame
        void Update()
        {
            if (!_subscribed) Subscribe();
            var rig = ArRig.Instance;

            if (_flipWindow)
            {
                _flipWindow = false;
                _windowFlipped = true;
                Debug.Log("[VBS][ArFirst] no wall found: the window goes on the table instead of floating (" + (rig != null ? rig.WindowState : "") + ")");
                ArTuning.Set("ar.windowOnWall", "false");
            }

            // the placement plate fades out with the ride
            if (_plate != null && _plateFade && !Mathf.Approximately(_plateAlpha, _plateTarget))
            {
                _plateAlpha = Mathf.MoveTowards(_plateAlpha, _plateTarget, Time.deltaTime / 0.8f);
                bool visible = _plateAlpha > 0.01f;
                if (_plate.enabled != visible) _plate.enabled = visible;
                if (_plateAlpha >= 0.999f) GameMaterials.ClearBlock(_plate);
                else GameMaterials.Tint(_plate, new Color(_plateColor.r, _plateColor.g, _plateColor.b, _plateColor.a * _plateAlpha));
            }

            // occlusion: soft by default, once the rig has its depth provider
            if (!_occlusionTried && rig != null && Time.timeSinceLevelLoad > 0.5f)
            {
                _occlusionTried = true;
                if (!_occlusionAuto) OcclusionState = "left alone (arfirst.occlusion keep)";
                else if (_file.ContainsKey("ar.occlusion")) OcclusionState = "left to tuning.json (ar.occlusion=" + _file["ar.occlusion"] + ")";
                else if (rig.Depth == null || !rig.Depth.IsSupported) OcclusionState = "not supported here";
                else if (ArTuning.OcclusionMode != ArTuning.Occlusion.Off) OcclusionState = "already " + ArTuning.OcclusionMode;
                else if (SetOcclusionQuiet("soft", rig))
                {
                    OcclusionState = "soft (default; frame-rate guard on)";
                    _guard = !rig.DesktopMode;
                    _guardFrom = Time.unscaledTime + 6f;
                }
                Debug.Log("[VBS][ArFirst] occlusion: " + OcclusionState);
            }
            if (_guard) GuardFrameRate();
        }

        void LateUpdate() => FollowWindow();

        static readonly MethodInfo TuningApply = typeof(ArTuning).GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(string), typeof(string) }, null);
        static readonly MethodInfo RigApplyOcclusion = typeof(ArRig).GetMethod("ApplyOcclusion", F, null, Type.EmptyTypes, null);

        /// <summary>
        /// Changes only the occlusion mode: ArTuning.Set would fire ArTuning.Changed, and the rig answers that with a whole
        /// re-placement (height snap, wall search, RoomChanged -> every flower relaid), a hitch mid-ride exactly when the
        /// frame rate is already low. This sets the mode and lets the rig apply just the depth mode and materials.
        /// </summary>
        bool SetOcclusionQuiet(string mode, ArRig rig)
        {
            if (TuningApply == null || RigApplyOcclusion == null || rig == null)
            {
                Debug.LogWarning("[VBS][ArFirst] occlusion: no quiet way to change it here, left as is");
                return false;
            }
            try
            {
                if (!(TuningApply.Invoke(null, new object[] { "ar.occlusion", mode }) is bool ok) || !ok) return false;
                RigApplyOcclusion.Invoke(rig, null);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VBS][ArFirst] occlusion: " + e.Message);
                return false;
            }
        }

        void GuardFrameRate()
        {
            float now = Time.unscaledTime;
            if (now < _guardFrom) return;
            _guardFrames += 1f;
            _guardSeconds += Time.unscaledDeltaTime;
            if (_guardSeconds < 4f) return;
            float fps = _guardFrames / _guardSeconds;
            float refresh = UnityEngine.XR.XRDevice.refreshRate;
            if (refresh < 30f) refresh = 72f;
            _guardFrames = _guardSeconds = 0f;
            // soft -> hard at 0.9 (a steady ~60-64 fps judders on Quest); hard -> off only at 0.8, so a heavy beat (stakes,
            // fire) doesn't switch occlusion off for the rest of the ride
            float bar = ArTuning.OcclusionMode == ArTuning.Occlusion.Soft ? 0.9f : 0.8f;
            _guardBad = fps < refresh * bar ? _guardBad + 1 : 0;
            if (_guardBad < 2) return;
            _guardBad = 0;
            _guardFrom = now + 6f;
            if (ArTuning.OcclusionMode == ArTuning.Occlusion.Soft)
            {
                if (!SetOcclusionQuiet("hard", ArRig.Instance)) { _guard = false; return; }
                OcclusionState = "hard (soft held only " + fps.ToString("0", C) + " fps of " + refresh.ToString("0", C) + ")";
            }
            else
            {
                if (!SetOcclusionQuiet("off", ArRig.Instance)) { _guard = false; return; }
                OcclusionState = "off (hard held only " + fps.ToString("0", C) + " fps of " + refresh.ToString("0", C) + ")";
                _guard = false;
            }
            Debug.LogWarning("[VBS][ArFirst] occlusion stepped down: " + OcclusionState);
        }
    }
}
