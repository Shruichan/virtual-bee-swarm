using System;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;
using VirtualBeeSwarm.AR;
using Debug = UnityEngine.Debug;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>
    /// The room bloom: at the escape (hive saved, the swarm turns gold, the window blooms) a field of real bee flowers
    /// spreads out from the hive across the real floor and vines climb the real walls, pillars and table legs. It stays
    /// through T-0, the map and the end card (the lesson: plant native flowers), folds away on "...Not one flower."
    /// and regrows on the crash back. Three draws (flowers instanced, vines, visiting bees), ~100k tris, opaque.
    ///   beats   pre: the kit loads (art meshes, clump, visiting bees) and both bloom shader variants are drawn once,
    ///           collapsed, so the GPU pipelines exist; what-if: the layout is built (hidden), so the escape costs no
    ///           build; escape: the wave starts 0.3 s in, from the window wall behind the hive (centre of view);
    ///           t0 / map / anything later: shown (grown at once if jumped to); field (glasses only), pre, reset,
    ///           anything before the escape: hidden.
    ///   room    BloomSurfaces: scene source (MRUK with bloom.mruk, BloomFakeRoom on the desktop) -> BloomProbes from
    ///           `pre` -> a ring round the table; vines from all of them + the table legs + both sides of the window.
    ///   flags   BloomTuning (tuning.json bloom.*, env VBS_BLOOM_FLAGS), re-read at every escape.
    ///   test    V cycles hidden -> grow -> grown -> hidden (desktop; B is the director's previous-beat key);
    ///           RoomBloom.TestGrow() / TestSnap() / TestClear() from code or the Board; env VBS_BLOOM_TEST="grow@6"
    ///           (or snap@6) grows it 6 s after start without playing the show. A test bloom stays up whatever the beat
    ///           until the next escape, V, TestClear or a reset.
    ///   log     "[VBS][Bloom] built ..." with the source tiers, counts, tris, draws and build ms.
    /// Installs itself into any scene with a GameDirector (like StakesDirector); no other file changes.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public class RoomBloom : MonoBehaviour
    {
        public static bool AutoInstall = true;
        public static RoomBloom Instance { get; private set; }
        public const string MaterialResource = "VBS_Bloom";
        public const float EscapeDelay = 0.3f;

        public enum State { Hidden, Shown }

        [SerializeField] KeyCode debugKey = KeyCode.V; // free in both input backends (GameInput maps letters, not F4+)

        public State Current { get; private set; } = State.Hidden;
        public BloomSurfaces Surfaces { get; private set; }
        public FlowerField Flowers => _flowers;
        public BloomProbes Probes => _probes;
        public string LastStats { get; private set; } = "";
        /// <summary>Seconds since the wave started (huge when snapped).</summary>
        public float BloomT => Current == State.Shown ? Time.time - _clockStart : 0f;

        GameDirector _director;
        GameNet _net;
        HiveBody _hive;
        FutureWindow _window;
        BloomProbes _probes;
        IBloomSceneSource _scene;
        BloomFakeRoom _fake;
        BloomScene _sceneData = new BloomScene();
        FlowerField _flowers;
        BloomVisitors _visitors;
        Material _template, _vineMat;
        Mesh _vineMesh;
        MaterialPropertyBlock _vineMpb;
        bool _subscribed, _sceneBegun, _wilted, _statsLogged, _pinned, _prebuilt;
        int _warmFrames;
        float _preloadAt = -1f;
        double _buildMs;
        Vector3 _builtTable;
        float _clockStart, _growAt = -1f, _testAt = -1f, _scale = 1f, _scaleTarget = 1f, _done;
        string _testMode;
        int _seed = 1;
        double _frameMicros;
        int _frames;

        static readonly int BloomTId = Shader.PropertyToID("_BloomT");
        static readonly int ClockId = Shader.PropertyToID("_BloomClock");
        static readonly int ScaleId = Shader.PropertyToID("_BloomScale");
        static readonly int FeetId = Shader.PropertyToID("_FeetFold");
        static readonly int DataId = Shader.PropertyToID("_BloomData");
        static readonly int PlayerId = Shader.PropertyToID("_VbsBloomPlayer");
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!AutoInstall) return;
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<RoomBloom>() != null) return;
            var go = new GameObject("RoomBloom");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<RoomBloom>();
            Debug.Log("[VBS][Bloom] installed (auto)");
        }

        public static void TestGrow() { if (Instance != null) { Instance.Grow("test"); Instance._pinned = true; } }
        public static void TestSnap() { if (Instance != null) { Instance.Show(snap: true, why: "test"); Instance._pinned = true; } }
        public static void TestClear() { if (Instance != null) { Instance._pinned = false; Instance.Hide("test"); } }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[VBS][Bloom] a second RoomBloom was disabled.");
                enabled = false;
                return;
            }
            Instance = this;
            BloomTuning.EnsureLoaded();
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            _net = GameNet.Instance != null ? GameNet.Instance : FindAnyObjectByType<GameNet>();
            _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            _window = FindAnyObjectByType<FutureWindow>(FindObjectsInactive.Include);
            var pg = new GameObject("BloomProbes");
            pg.transform.SetParent(transform, false);
            _probes = pg.AddComponent<BloomProbes>();
            _template = Resources.Load<Material>(MaterialResource);
            if (_template == null)
            {
                var sh = Shader.Find("VirtualBeeSwarm/Bloom");
                if (sh != null) _template = new Material(sh) { name = "VBS_Bloom (runtime)" };
            }
            if (_template != null)
            {
                _vineMat = new Material(_template) { name = "VBS_BloomVines (runtime)", enableInstancing = false };
                _vineMat.SetFloat(FeetId, 0f);
                _vineMpb = new MaterialPropertyBlock();
                _vineMpb.SetVector(DataId, new Vector4(0f, 0f, 0f, 4095f));
            }
            string env = null;
            try { env = Environment.GetEnvironmentVariable("VBS_BLOOM_TEST"); } catch (Exception) { }
            if (!string.IsNullOrEmpty(env))
            {
                var parts = env.Split('@');
                _testMode = parts[0].Trim().ToLowerInvariant();
                float at = 5f;
                if (parts.Length > 1) float.TryParse(parts[1], NumberStyles.Float, C, out at);
                _testAt = Time.time + at;
            }
            Debug.Log("[VBS][Bloom] flags " + BloomTuning.Describe() + (_template == null ? " | NO SHADER: nothing will draw" : "") +
                      (_testMode != null ? " | test " + _testMode + " in " + (_testAt - Time.time).ToString("0.0", C) + " s" : ""));
        }

        void Start()
        {
            var rig = ArRig.Instance;
            bool desktop = rig == null || rig.DesktopMode;
            if (desktop) _scene = _fake = new BloomFakeRoom();
            else if (BloomTuning.Mruk) _scene = BloomPlatform.Scene;
            if (rig != null) rig.RoomChanged += OnRoomChanged;
            Debug.Log("[VBS][Bloom] scene source: " + (_scene != null ? _scene.Name : desktop ? "none" : BloomTuning.Mruk ? "none on this platform" : "off (bloom.mruk)") +
                      ", probes " + (BloomTuning.Probes ? "on in pre" : "off"));
        }

        void OnEnable()
        {
            if (Instance != this) return;
            if (_director != null)
            {
                _director.BeatChanged += HandleBeat;
                _director.GroupResetDone += HandleReset;
            }
            if (_net != null)
            {
                _net.VoiceLineStarted += HandleVoiceLine;
                _net.SceneChanged += HandleScene;
            }
            _subscribed = true;
        }

        void OnDisable()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (_director != null)
            {
                _director.BeatChanged -= HandleBeat;
                _director.GroupResetDone -= HandleReset;
            }
            if (_net != null)
            {
                _net.VoiceLineStarted -= HandleVoiceLine;
                _net.SceneChanged -= HandleScene;
            }
        }

        void OnDestroy()
        {
            var rig = ArRig.Instance;
            if (rig != null) rig.RoomChanged -= OnRoomChanged;
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ events
        void OnRoomChanged(RoomModel room)
        {
            _probes.OnRoom(room);
            if (_prebuilt && room != null && room.HasTable && (room.Table.center - _builtTable).sqrMagnitude > 0.01f)
            {
                _prebuilt = false; // the table moved after the what-if build: build again at the escape
                Debug.Log("[VBS][Bloom] the table moved since the prebuild: rebuilding at the escape");
            }
        }

        /// <summary>Loads what every build needs (art meshes, the clump, the visiting bees) and queues the shader warm-up.
        /// Once per run, in `pre`, so none of it lands on the escape.</summary>
        void Preload()
        {
            if (_template == null || _flowers != null) return;
            var sw = Stopwatch.StartNew();
            BloomTuning.Load();
            bool art = false;
            string artWhy = "bloom.art=off";
            if (BloomTuning.Art != BloomTuning.ArtMode.Off) art = BloomMeshes.ArtKitPresent(out artWhy);
            _flowers = new FlowerField(_template, art);
            _artWhy = artWhy;
            _visitors = new BloomVisitors();
            if (BloomTuning.Art != BloomTuning.ArtMode.Off) // read the vine art once too
            {
                BloomMeshes.Art("Bloom_VineLeaf", "bloom_vine_leaf");
                BloomMeshes.Art("Bloom_VineBlossom", "bloom_vine_blossom");
            }
            _warmFrames = 3;
            Debug.Log("[VBS][Bloom] preloaded (" + _artWhy + ", " + _flowers.TrisPerClump + " tris a clump, up to " + _flowers.MaxClumps +
                      " clumps) in " + sw.Elapsed.TotalMilliseconds.ToString("0.0", C) + " ms; warming the shader");
        }

        /// <summary>Builds the layout ahead of the escape (hidden); Show then only starts the clock.</summary>
        void Prebuild(string why)
        {
            if (_template == null) return;
            BloomTuning.Load();
            if (!BloomTuning.On) return;
            Preload();
            var sw = Stopwatch.StartNew();
            Build();
            _buildMs = sw.Elapsed.TotalMilliseconds;
            _prebuilt = true;
            var room = ArRig.Instance != null ? ArRig.Instance.Room : null;
            _builtTable = room != null && room.HasTable ? room.Table.center : Vector3.zero;
            Debug.Log("[VBS][Bloom] prebuilt (" + why + ") in " + _buildMs.ToString("0.0", C) + " ms: " + _flowers.Count + " clumps, " +
                      Surfaces.Vines.Count + " vines, " + _vineTris + " vine tris");
        }

        void HandleReset()
        {
            _pinned = false;
            Hide("reset");
        }

        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            if (_pinned && beat != GameBeat.Escape) return; // a test bloom stays up
            _pinned = false;
            if (beat == GameBeat.Pre)
            {
                Hide(prev == GameBeat.Pre ? "start" : "pre");
                // a few seconds in, not at scene start: allocating then races the window videos' native error
                // callbacks (ForecastMoment, ArrayTypeMismatchException in the smoke player)
                if (_flowers == null && _preloadAt < 0f) _preloadAt = Time.time + 3f;
                if (!_sceneBegun && _scene != null && BloomTuning.On)
                {
                    _sceneBegun = true;
                    try { _scene.Begin(); }
                    catch (Exception e) { Debug.LogWarning("[VBS][Bloom] scene source " + _scene.Name + ": " + e.Message); }
                }
                return;
            }
            if (beat == GameBeat.Escape)
            {
                _growAt = Time.time + EscapeDelay;
                return;
            }
            if (beat == GameBeat.Field) { Hide("field (glasses only)"); return; }
            if (beat == GameBeat.WhatIf)
            {
                Hide("what-if");
                Prebuild("what-if");
                return;
            }
            if (beat < GameBeat.Escape) { Hide("back before the escape"); return; }
            // t0, map and anything later
            _growAt = -1f;
            if (Current == State.Hidden) Show(snap: true, why: "jumped to " + GameIds.BeatName(beat));
            if (_wilted && beat != GameBeat.T0) Regrow("left t0");
        }

        void HandleVoiceLine(GameNet.VoiceLineInfo info)
        {
            if (!BloomTuning.Wilt || Current != State.Shown || _wilted) return;
            if (info.id != GameLines.T0Moon.id) return;
            _wilted = true;
            _scaleTarget = 0f;
            Debug.Log("[VBS][Bloom] wilt on " + info.id + " (\"Not one flower.\")");
        }

        void HandleScene(string scene)
        {
            if (_wilted && scene == GameIds.Scene.FullBloom) Regrow("crash back (" + scene + ")");
        }

        void Regrow(string why)
        {
            _wilted = false;
            _scale = 1f;
            _scaleTarget = 1f;
            _clockStart = Time.time; // the wave runs again
            if (_visitors != null && _flowers != null && BloomTuning.Bees && _flowers.Count > 0)
                _visitors.Begin(Stakes.StakesFlags.Comfort ? BloomVisitors.Comfort : BloomVisitors.Max,
                    _hive != null ? _hive.DoorPoint.position : Surfaces.HiveFloor + Vector3.up * 0.8f, 0f);
            Debug.Log("[VBS][Bloom] regrow: " + why);
        }

        // ------------------------------------------------------------------ build / show / hide
        void Grow(string why) => Show(snap: false, why: why);

        void Show(bool snap, string why)
        {
            _growAt = -1f;
            bool prebuilt = _prebuilt;
            _prebuilt = false;
            if (!prebuilt) BloomTuning.Load();
            if (!BloomTuning.On)
            {
                Hide("bloom.on=false");
                return;
            }
            if (_template == null) { Debug.LogWarning("[VBS][Bloom] no Bloom shader in this build: nothing to draw"); return; }
            double ms = _buildMs;
            if (!prebuilt)
            {
                var sw = Stopwatch.StartNew();
                Build();
                ms = sw.Elapsed.TotalMilliseconds;
            }
            _clockStart = snap ? Time.time - 1000f : Time.time;
            _scale = _scaleTarget = 1f;
            _wilted = false;
            Current = State.Shown;
            _statsLogged = false;
            _frameMicros = 0;
            _frames = 0;
            if (_visitors != null)
            {
                if (BloomTuning.Bees && _flowers.Count > 0)
                    _visitors.Begin(Stakes.StakesFlags.Comfort ? BloomVisitors.Comfort : BloomVisitors.Max,
                        _hive != null ? _hive.DoorPoint.position : Surfaces.HiveFloor + Vector3.up * 0.8f, snap ? 1000f : 0f);
                else _visitors.Clear();
            }
            if (_fake != null && ArRig.Instance != null && ArRig.Instance.DesktopMode) _fake.ShowBoxes(_sceneData, transform, true);
            int vineTris = _vineTris;
            int beeTris = _visitors != null ? _visitors.Count * _visitors.TrisPerBee : 0;
            int draws = (_flowers.Count > 0 ? 1 : 0) + (_vineMesh != null ? 1 : 0) + (_visitors != null && _visitors.Count > 0 ? 1 : 0);
            LastStats = "source floor=" + Surfaces.FloorTier + " vines=" + Surfaces.VineTiers + " | flowers=" + _flowers.Count +
                        " clumps (" + _flowers.TrisPerClump + " tris each, " + Surfaces.Spacing.ToString("0.00", C) + " m apart, " + Surfaces.Candidates + " candidates, scene " + Surfaces.SceneCount +
                        " probes " + Surfaces.ProbeCount + " ring " + Surfaces.RingCount + ") vines=" + Surfaces.Vines.Count + " bees=" +
                        (_visitors != null ? _visitors.Count : 0) + " | tris flowers=" + _flowers.Tris + " vines=" + vineTris + " bees=" + beeTris +
                        " total=" + (_flowers.Tris + vineTris + beeTris) + " | draws=" + draws + " | buildMs=" + ms.ToString("0.0", C) + (prebuilt ? " (prebuilt)" : "") +
                        " | floorY=" + Surfaces.FloorY.ToString("0.00", C) + " window=" + (Surfaces.HasWindow ? "excluded" : "none") +
                        " | flowers " + BloomMeshes.ArtUsed + " (" + _artWhy + "), vines " + Vines.ArtUsed;
            Debug.Log("[VBS][Bloom] built (" + why + (snap ? ", grown" : ", wave from the hive at " + BloomTuning.Wave.ToString("0.0", C) + " m/s") +
                      "): " + LastStats);
            Debug.Log("[VBS][Bloom] " + _probes.Describe() + (_scene != null ? " | scene " + _scene.Name + ": " + _scene.Status : ""));
        }

        int _vineTris;
        string _artWhy = "";

        void Build()
        {
            var rig = ArRig.Instance;
            var room = rig != null ? rig.Room : null;
            Preload();
            bool wantArt = BloomTuning.Art != BloomTuning.ArtMode.Off;
            if (_flowers.Art && !wantArt) { _flowers = new FlowerField(_template, false); _artWhy = "bloom.art=off"; }
            else if (!_flowers.Art && wantArt && BloomMeshes.ArtKitPresent(out var artWhy)) { _flowers = new FlowerField(_template, true); _artWhy = artWhy; }

            BloomScene scene = null;
            if (_scene != null)
            {
                try { if (_scene.TryFill(_sceneData)) scene = _sceneData; }
                catch (Exception e) { Debug.LogWarning("[VBS][Bloom] scene source " + _scene.Name + ": " + e.Message); }
            }
            var cam = Camera.main;
            Vector3 hive = _hive != null ? _hive.transform.position : room != null && room.HasTable ? room.Table.center : Vector3.forward;
            var inp = new BloomSurfaces.Inputs
            {
                room = room,
                scene = scene,
                probes = BloomTuning.Probes ? _probes : null,
                hive = hive,
                player = cam != null ? cam.transform.position : hive - Vector3.forward,
                budget = Mathf.RoundToInt(_flowers.MaxClumps * Mathf.Clamp01(BloomTuning.Density)), // density = share of the tri budget
                spacing = Mathf.Clamp(0.32f / Mathf.Sqrt(Mathf.Max(0.3f, BloomTuning.Density)), 0.18f, 0.5f), // the widest spacing (big rooms)
                vines = BloomTuning.Vines,
                seed = 1000 + _seed,
            };
            inp.hasWindow = WindowRect(out inp.window);
            Surfaces = BloomSurfaces.Build(inp);
            _flowers.Build(Surfaces, BloomTuning.Wave, 77 + _seed);
            if (_vineMesh != null) Destroy(_vineMesh);
            _vineMesh = null;
            _vineTris = 0;
            float vineDone = 0f;
            if (BloomTuning.Vines && Surfaces.Vines.Count > 0) _vineMesh = Vines.Build(Surfaces, BloomTuning.Wave, 31 + _seed, out _vineTris, out vineDone);
            _done = Mathf.Max(_flowers.Done, vineDone);
            _seed++;
        }

        /// <summary>The window pane as an exclusion: 1.2 x its scale, i.e. its T-0 moon size (FutureWindow.moonScale 2.4 = 1.2 x
        /// the full width) even while it is still at size 1; the vines keep a further 0.2 m off it.</summary>
        bool WindowRect(out BloomRect r)
        {
            r = default;
            if (_window == null || !_window.gameObject.activeInHierarchy) return false;
            var t = _window.PaneTransform;
            if (t == null) return false;
            Vector3 s = t.lossyScale;
            r.center = t.position;
            r.right = t.right;
            r.up = t.up;
            r.normal = -t.forward;
            r.half = new Vector2(Mathf.Abs(s.x) * 1.2f, Mathf.Abs(s.y) * 1.2f);
            return r.half.x > 0.01f;
        }

        void Hide(string why)
        {
            _growAt = -1f;
            _prebuilt = false;
            bool was = Current == State.Shown;
            Current = State.Hidden;
            _wilted = false;
            if (_visitors != null) _visitors.Clear();
            if (_fake != null) _fake.ShowBoxes(null, transform, false);
            if (was) Debug.Log("[VBS][Bloom] hidden (" + why + ")" + (_visitors != null ? ", " + _visitors.Visits + " flower visits, closest bee to the face " + (float.IsInfinity(_visitors.MinFace) ? "n/a" : _visitors.MinFace.ToString("0.00", C) + " m") : ""));
        }

        // ------------------------------------------------------------------ frame
        void Update()
        {
            if (GameInput.KeyDown(debugKey))
            {
                if (Current == State.Hidden) { Grow("key " + debugKey); _pinned = true; }
                else if (BloomT < _done) { Show(snap: true, why: "key " + debugKey); _pinned = true; }
                else { _pinned = false; Hide("key " + debugKey); }
            }
            if (_testAt >= 0f && Time.time >= _testAt)
            {
                _testAt = -1f;
                if (_testMode == "snap") TestSnap();
                else if (_testMode == "clear") { _pinned = false; Hide("VBS_BLOOM_TEST"); }
                else TestGrow();
            }
            if (_preloadAt >= 0f && Time.time >= _preloadAt)
            {
                _preloadAt = -1f;
                Preload();
            }
            if (_growAt >= 0f && Time.time >= _growAt)
            {
                _growAt = -1f;
                if (_director == null || _director.Beat == GameBeat.Escape) Grow("escape");
            }
        }

        void LateUpdate()
        {
            if (Current != State.Shown)
            {
                if (_warmFrames > 0 && _flowers != null && _vineMat != null)
                {
                    _warmFrames--;
                    var c = Camera.main;
                    Vector3 at = c != null ? c.transform.position + c.transform.forward : Vector3.forward;
                    _flowers.Warm(at, gameObject.layer);
                    _vineMpb.SetFloat(BloomTId, -1000f);
                    _vineMpb.SetFloat(ScaleId, 0f);
                    Graphics.DrawMesh(_flowers.Mesh, Matrix4x4.Translate(at), _vineMat, gameObject.layer, null, 0, _vineMpb,
                        UnityEngine.Rendering.ShadowCastingMode.Off, false);
                }
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            var cam = Camera.main;
            Vector3 face = cam != null ? cam.transform.position : Vector3.up * 1.6f;
            Shader.SetGlobalVector(PlayerId, new Vector4(face.x, face.z, BloomSurfaces.FeetRadius, 0.2f));
            float k = 1f - Mathf.Exp(-Time.deltaTime * (_scaleTarget < _scale ? 2.2f : 4f));
            _scale = Mathf.Abs(_scale - _scaleTarget) < 0.002f ? _scaleTarget : Mathf.Lerp(_scale, _scaleTarget, k);
            float bt = Time.time - _clockStart, clock = Time.time;
            int layer = gameObject.layer;
            bool gone = _scale < 0.001f && _scaleTarget == 0f; // wilted all the way: nothing to submit
            if (!gone) _flowers.Draw(bt, clock, _scale, layer);
            if (!gone && _vineMesh != null && _vineMat != null)
            {
                _vineMpb.SetFloat(BloomTId, bt);
                _vineMpb.SetFloat(ClockId, clock);
                _vineMpb.SetFloat(ScaleId, _scale);
                Vines.Draw(_vineMesh, _vineMat, _vineMpb, layer);
            }
            if (_visitors != null && _scale > 0.5f) _visitors.Tick(_flowers, bt, clock, face, layer);
            _frameMicros += (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
            _frames++;
            if (!_statsLogged && bt >= _done + 6f)
            {
                _statsLogged = true;
                Debug.Log("[VBS][Bloom] grown: CPU " + (_frameMicros / Mathf.Max(1, _frames)).ToString("0", C) + " us/frame over " + _frames +
                          " frames, " + (_visitors != null ? _visitors.Visits : 0) + " flower visits so far, closest bee to the face " +
                          (_visitors == null || float.IsInfinity(_visitors.MinFace) ? "n/a" : _visitors.MinFace.ToString("0.00", C) + " m"));
            }
        }
    }
}
