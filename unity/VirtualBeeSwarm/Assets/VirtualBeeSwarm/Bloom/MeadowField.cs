using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualBeeSwarm.AR;
using Debug = UnityEngine.Debug;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>
    /// The meadow: the player's whole floor becomes a wild-flower meadow with the hive standing in it, from the title
    /// screen to the end card. The hive is sick, so at the start the meadow near it is thin, pale and drooping; every
    /// solved station brings colour and flowers back, and the escape is full bloom (RoomBloom's big flowers and vines
    /// then grow on top of it).
    ///   walls   MeadowWalls: Bloom's vines up every known wall, climbing higher with the hive's health (2 draws max)
    ///   yard    a mown yard round the big hive and its props (anchor frame), so props and the hive stand stay clear
    ///   draw    one 0.45 m tile mesh (~180 grass blades of 3 tris, 16 flowers, sometimes a tall lupine spike; ~700 tris)
    ///           GPU-instanced over the floor: one draw per 1023 tiles (Meadow.shader, opaque, unlit, no lights);
    ///           + the stand (1 draw) + the bees (1 draw). Capped at TriCap tris; distance thinning in the shader.
    ///   room    the room scan's floor outline and furniture (MRUK, meadow.mruk; BloomFakeRoom on the desktop), else a
    ///           meadow.extent square around the player and the hive, cut at a known wall; floor y from the scan or 0.
    ///   clear   a 0.45 m circle round the player (live), the stand's trunk, a trodden (shorter) path from where the
    ///           player started to the hive, tall spikes only 1.6 m+ from the start. Plants max ~0.45 m (spikes 0.7 m).
    ///   health  _VbsMeadow.x: pre 0.12, hook 0.15, +0.19 a solved station, what-if 0.8, escape and later 1.45 (full
    ///           even next to the hive, whose tiles carry up to -0.5).
    ///   hook    MeadowLayout.SetHivePose (room-scan placement); the meadow re-lays itself whenever the hive moves.
    ///   flags   MeadowTuning (meadow.*); "[VBS][Meadow] ..." log lines carry the stats.
    /// Installs itself into any scene with a GameDirector (like RoomBloom).
    /// </summary>
    [DefaultExecutionOrder(55)]
    public class MeadowField : MonoBehaviour
    {
        public static bool AutoInstall = true;
        public static MeadowField Instance { get; private set; }
        public const string MaterialResource = "VBS_Meadow";
        public const float Tile = 0.45f, ClearRadius = 0.45f, PathHalf = 0.28f, StandClear = 0.15f;
        public const int MaxTiles = 2046, TriCap = 240000;
        /// <summary>Wall to wall: the nearest tiles (up to NearTris) get the full tile, the rest of the floor (up to FarTris)
        /// a lighter tile (40% of the grass, every flower): two instanced draws, ~300k tris for a 6 x 6 m room.</summary>
        public const int NearTris = 165000, FarTris = 150000;

        public bool HasFloor { get; private set; }
        public float FloorY { get; private set; }
        public float Health => _health;
        public int TileCount => _n;
        /// <summary>Tiles drawn with the full tile (the nearest ones, sorted by distance from the player); bees visit these.</summary>
        public int NearCount => _nNear;
        public MeadowWalls Walls => _walls;
        public int TrisPerTile { get; private set; }
        public string LastStats { get; private set; } = "";

        GameDirector _director;
        HiveBody _hive;
        Transform _anchor;
        MeadowWalls _walls;
        Material _mat;
        Mesh _tile, _tileFar;
        int _nNear, _nFar, _trisFar;
        readonly List<Vector4> _heads = new List<Vector4>(32); // xyz local head, w threshold
        readonly Matrix4x4[][] _m = { new Matrix4x4[1023], new Matrix4x4[1023] };
        readonly Vector4[][] _data = { new Vector4[1023], new Vector4[1023] };
        // what is drawn this frame: only the tiles in view (Quest: the GPU runs only what is submitted)
        readonly Matrix4x4[][] _dm = { new Matrix4x4[1023], new Matrix4x4[1023] };
        readonly Vector4[][] _dd = { new Vector4[1023], new Vector4[1023] };
        readonly int[] _dn = new int[2];
        MaterialPropertyBlock[] _mpb; // made in Awake (not allowed in a field initializer)
        Vector2 _roomMin, _roomMax;
        float _nextStatLog;
        /// <summary>Tiles are culled to Camera.main's view before drawing. The capture tool renders several cameras a frame
        /// and turns this off.</summary>
        public static bool CullToView = true;
        /// <summary>Floor triangles submitted to the GPU last frame (view-culled), the number the Quest budget is about.</summary>
        public int SubmittedTris { get; private set; }
        /// <summary>The meadow floor's extent (xz), valid after the first layout.</summary>
        public bool RoomRect(out Vector2 min, out Vector2 max) { min = _roomMin; max = _roomMax; return _n > 0; }
        int _n;
        IBloomSceneSource _scene;
        readonly BloomScene _sceneData = new BloomScene();
        bool _sceneBegun, _sceneUsed, _subscribed;
        float _health = 0.3f, _nextSceneTry, _layoutAt = -1f;
        Vector3 _layoutHive, _start;
        string _source = "none";
        MeadowBees _bees;
        float _builtDensity = -1f;

        static readonly int MeadowId = Shader.PropertyToID("_VbsMeadow");
        static readonly int PlayerId = Shader.PropertyToID("_VbsMeadowPlayer");
        static readonly int HiveId = Shader.PropertyToID("_VbsMeadowHive");
        static readonly int StartId = Shader.PropertyToID("_VbsMeadowStart");
        static readonly int YardId = Shader.PropertyToID("_VbsMeadowYard");
        static readonly int YardFwdId = Shader.PropertyToID("_VbsMeadowYardFwd");
        static readonly int DataId = Shader.PropertyToID("_MeadowData");
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!AutoInstall) return;
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<MeadowField>() != null) return;
            var go = new GameObject("Meadow");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<MeadowField>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { enabled = false; return; }
            Instance = this;
            _mpb = new[] { new MaterialPropertyBlock(), new MaterialPropertyBlock(), new MaterialPropertyBlock(), new MaterialPropertyBlock() };
            MeadowTuning.Load();
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            var placement = FindAnyObjectByType<HivePlacement>(FindObjectsInactive.Include);
            _anchor = ArRig.Instance != null && ArRig.Instance.TableAnchor != null ? ArRig.Instance.TableAnchor
                : placement != null ? (placement.transform.parent != null ? placement.transform.parent : placement.transform) : null;
            var template = Resources.Load<Material>(MaterialResource);
            if (template != null) _mat = new Material(template) { name = "VBS_Meadow (runtime)", enableInstancing = true };
            else
            {
                var sh = Shader.Find("VirtualBeeSwarm/Meadow");
                if (sh != null) _mat = new Material(sh) { name = "VBS_Meadow (shader only)", enableInstancing = true };
            }
            bool xr = UnityEngine.XR.XRSettings.isDeviceActive;
#if UNITY_ANDROID && !UNITY_EDITOR
            xr = true;
#endif
            MeadowLayout.ApplyRig(_anchor, !xr); // no table: the rig scaled up, on the floor
            if (_anchor != null && _hive != null) _hiveLocal = _anchor.InverseTransformPoint(_hive.transform.position);
            if (MeadowTuning.Walls) _walls = new MeadowWalls();
            Debug.Log("[VBS][Meadow] installed: " + MeadowTuning.Describe() + (_mat == null ? " | NO SHADER: no meadow will draw" : ""));
        }

        void Start()
        {
            var rig = ArRig.Instance;
            bool desktop = rig == null || rig.DesktopMode;
            if (desktop) _scene = new BloomFakeRoom();
            else if (MeadowTuning.Mruk) _scene = BloomPlatform.Scene;
            if (rig != null) rig.RoomChanged += OnRoomChanged;
            _layoutAt = Time.time + 0.2f;
        }

        void OnEnable()
        {
            if (Instance != this || _director == null) return;
            _director.BeatChanged += OnBeat;
            _subscribed = true;
        }

        void OnDisable()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (_director != null) _director.BeatChanged -= OnBeat;
        }

        void OnDestroy()
        {
            var rig = ArRig.Instance;
            if (rig != null) rig.RoomChanged -= OnRoomChanged;
            if (Instance == this) Instance = null;
        }

        void OnRoomChanged(RoomModel room) => _layoutAt = Time.time; // after every placement

        /// <summary>The room scan (or desktop fake room) with a floor outline, null when none is in yet.</summary>
        public BloomScene CurrentScene()
        {
            if (_scene == null) return null;
            try { if (_scene.TryFill(_sceneData) && _sceneData.FloorPolygon.Count >= 3) return _sceneData; }
            catch (Exception) { }
            return null;
        }

        void OnBeat(GameBeat prev, GameBeat beat)
        {
            if (beat == GameBeat.Pre && !_sceneBegun && _scene != null && MeadowTuning.On)
            {
                _sceneBegun = true;
                try { _scene.Begin(); }
                catch (Exception e) { Debug.LogWarning("[VBS][Meadow] scene source " + _scene.Name + ": " + e.Message); }
            }
            if (beat == GameBeat.Escape || beat == GameBeat.Pre)
                Debug.Log("[VBS][Meadow] beat " + GameIds.BeatName(beat) + ": health " + _health.ToString("0.00", C) + " -> " + TargetHealth().ToString("0.00", C));
        }

        float TargetHealth()
        {
            if (_director == null) return 0.3f;
            var b = _director.Beat;
            if (b == GameBeat.Pre) return 0.3f;
            if (b >= GameBeat.Escape) return 1.5f;
            float h = 0.32f + 0.16f * Mathf.Clamp(_director.SolvedStations, 0, 4);
            if (b == GameBeat.WhatIf) h = Mathf.Max(h, 0.95f);
            return h;
        }

        // ------------------------------------------------------------------ layout
        void Layout(string why)
        {
            var sw = Stopwatch.StartNew();
            MeadowTuning.Load();
            _n = 0;
            _nNear = _nFar = 0;
            if (!MeadowTuning.On || _mat == null) { LastStats = "off"; return; }
            if (_tile == null || _tileFar == null || Mathf.Abs(_builtDensity - MeadowTuning.Density) > 0.01f)
            {
                _tileFar = BuildTile(MeadowTuning.Density, true, _tileFar, out _trisFar);
                _tile = BuildTile(MeadowTuning.Density, false, _tile, out var nearTris);
                TrisPerTile = nearTris;
            }

            var rig = ArRig.Instance;
            var room = rig != null ? rig.Room : null;
            BloomScene scene = null;
            if (_scene != null)
            {
                try { if (_scene.TryFill(_sceneData)) scene = _sceneData; }
                catch (Exception e) { Debug.LogWarning("[VBS][Meadow] scene source " + _scene.Name + ": " + e.Message); }
            }
            _sceneUsed = scene != null && scene.FloorPolygon.Count >= 3;
            FloorY = scene != null ? scene.FloorY : room != null ? room.FloorY : 0f;
            HasFloor = true;

            var cam = Camera.main;
            Vector3 hive = HivePos();
            _layoutHive = hive;
            Vector3 player = cam != null ? cam.transform.position : hive - Vector3.forward * 0.8f;
            if ((Flat(player) - Flat(hive)).magnitude < 0.2f) player = hive - Vector3.forward * 0.8f;
            _start = player;

            Vector2 min, max;
            if (_sceneUsed)
            {
                min = new Vector2(float.MaxValue, float.MaxValue);
                max = new Vector2(float.MinValue, float.MinValue);
                foreach (var p in scene.FloorPolygon) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
                _source = "scan (" + scene.Name + ", " + scene.FloorPolygon.Count + "-point floor, " + scene.Volumes.Count + " furniture)";
            }
            else
            {
                Vector2 c = (Flat(player) + Flat(hive)) * 0.5f;
                float e = MeadowTuning.Extent * 0.5f;
                min = c - new Vector2(e, e);
                max = c + new Vector2(e, e);
                _source = "default " + MeadowTuning.Extent.ToString("0.#", C) + " m square" + (room != null && room.HasWall ? ", cut at the wall" : "");
            }

            var rnd = new System.Random(4242);
            float step = Tile * 0.9f;
            var cand = new List<(float d, Vector3 p)>(1024);
            for (float x = min.x + step * 0.5f; x < max.x; x += step)
                for (float z = min.y + step * 0.5f; z < max.y; z += step)
                {
                    var p = new Vector3(x + ((float)rnd.NextDouble() - 0.5f) * 0.1f, FloorY, z + ((float)rnd.NextDouble() - 0.5f) * 0.1f);
                    if (_sceneUsed && (!scene.InFloor(p) || scene.InVolume(p + Vector3.up * 0.1f, 0.05f))) continue;
                    if (!_sceneUsed && room != null && room.HasWall && Vector3.Dot(p - room.WindowWall.point, room.WindowWall.normal) < 0.15f) continue;
                    cand.Add(((Flat(p) - Flat(player)).sqrMagnitude, p));
                }
            cand.Sort((a, b) => a.d.CompareTo(b.d));
            int nearCap = Mathf.Min(1023, NearTris / Mathf.Max(1, TrisPerTile)), farCap = Mathf.Min(1023, FarTris / Mathf.Max(1, _trisFar));
            _nNear = Mathf.Min(cand.Count, nearCap);
            _nFar = Mathf.Min(cand.Count - _nNear, farCap);
            // a big room (venue hall): rather than dropping the far floor, the far tiles go on a coarser grid, stretched to
            // close the gaps (sparser there, never bare)
            float farStretch = 1f;
            string capped = "";
            if (cand.Count - _nNear > farCap && _nNear > 0)
            {
                farStretch = Mathf.Min(2.5f, Mathf.Sqrt((cand.Count - _nNear) / (float)farCap) * 1.08f);
                float cut = cand[_nNear - 1].d;
                var far = new List<(float d, Vector3 p)>(1024);
                float fs = step * farStretch;
                for (float x = min.x + fs * 0.5f; x < max.x; x += fs)
                    for (float z = min.y + fs * 0.5f; z < max.y; z += fs)
                    {
                        var p = new Vector3(x, FloorY, z);
                        float d = (Flat(p) - Flat(player)).sqrMagnitude;
                        if (d <= cut) continue;
                        if (_sceneUsed && (!scene.InFloor(p) || scene.InVolume(p + Vector3.up * 0.1f, 0.05f))) continue;
                        if (!_sceneUsed && room != null && room.HasWall && Vector3.Dot(p - room.WindowWall.point, room.WindowWall.normal) < 0.15f) continue;
                        far.Add((d, p));
                    }
                far.Sort((a, b) => a.d.CompareTo(b.d));
                capped = ", capped: far tiles x" + farStretch.ToString("0.00", C) + " on a coarser grid (" + far.Count + " spots for " + farCap + ")";
                cand.RemoveRange(_nNear, cand.Count - _nNear);
                cand.AddRange(far);
                _nFar = Mathf.Min(cand.Count - _nNear, farCap);
            }
            int n = _nNear + _nFar;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = cand[i].p;
                int b = i < _nNear ? 0 : 1, k = i < _nNear ? i : i - _nNear;
                float yaw = (float)rnd.NextDouble() * 360f;
                float st = b == 1 ? farStretch : 1f;
                _m[b][k] = Matrix4x4.TRS(p, Quaternion.Euler(0f, yaw, 0f), new Vector3(st, 1f, st));
                float pal = Mathf.PerlinNoise(p.x * 0.6f + 11f, p.z * 0.6f + 3f);
                float tall = 0.82f + 0.3f * Mathf.PerlinNoise(p.x * 0.9f + 40f, p.z * 0.9f + 7f);
                float toHive = (Flat(p) - Flat(hive)).magnitude;
                float sick = -0.4f * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 2.6f, toHive)));
                _data[b][k] = new Vector4(pal, (float)rnd.NextDouble() * 6.2832f, tall, sick);
            }
            _n = n;
            _roomMin = min;
            _roomMax = max;
            for (int b = 0; b < 2; b++) _mpb[b].SetVectorArray(DataId, _data[b]);

            // in view now (frustum test per tile), the number the Quest budget is about
            int inView = 0, inViewTris = 0;
            if (cam != null)
            {
                var planes = GeometryUtility.CalculateFrustumPlanes(cam);
                for (int i = 0; i < n; i++)
                    if (GeometryUtility.TestPlanesAABB(planes, new Bounds(cand[i].p + Vector3.up * 0.25f, new Vector3(Tile, 0.5f, Tile))))
                    {
                        inView++;
                        inViewTris += i < _nNear ? TrisPerTile : _trisFar;
                    }
            }
            if (_bees == null) _bees = new MeadowBees();
            if (_walls != null && _walls.Ok)
            {
                BloomScene wallScene = null;
                if (_scene != null) { try { if (_scene.TryFill(_sceneData) && _sceneData.Walls.Count > 0) wallScene = _sceneData; } catch (Exception) { } }
                float ceil = room != null ? room.CeilingOrDefault : FloorY + 2.4f;
                _walls.Build(wallScene, room, FloorY, ceil, hive, player, 4711);
            }
            int meadowDraws = (_nNear > 0 ? 1 : 0) + (_nFar > 0 ? 1 : 0);
            int draws = meadowDraws + (_walls != null ? _walls.Draws : 0) + (MeadowTuning.Bees ? 1 : 0);
            LastStats = "tiles=" + n + " (near " + _nNear + " x " + TrisPerTile + " tris, far " + _nFar + " x " + _trisFar + " tris" + (cand.Count > n ? ", of " + cand.Count + " capped" : "") + capped + ")" +
                        " tris=" + (_nNear * TrisPerTile + _nFar * _trisFar) + " + walls " + (_walls != null ? _walls.Tris : 0) +
                        " inView~" + inViewTris + " (" + inView + " tiles) draws=" + draws + " (meadow " + meadowDraws + ", walls " + (_walls != null ? _walls.Draws : 0) + ", room bees " + MeadowTuning.RoomBees + ")" +
                        " | floor " + _source + " y=" + FloorY.ToString("0.00", C) + " | start " + V(player) + " hive " + V(hive) +
                        " | walls: " + (_walls != null ? _walls.Describe() : "off") + " | " + MeadowLayout.RigStats + " | " + MeadowLayout.LastPose + " | buildMs=" + sw.Elapsed.TotalMilliseconds.ToString("0.0", C);
            Debug.Log("[VBS][Meadow] laid out (" + why + "): " + LastStats);
        }

        /// <summary>Where the hive rests on the stand (the anchor + its scene offset), not where it is flying in the hook.</summary>
        Vector3 HivePos()
        {
            if (_anchor != null) return _anchor.TransformPoint(_hiveLocal);
            if (_hive != null) return _hive.transform.position;
            return Vector3.forward;
        }

        Vector3 _hiveLocal = new Vector3(0f, 0f, 0.15f);

        // ------------------------------------------------------------------ frame
        void Update()
        {
            KeepOnFloor();
            if (_layoutAt >= 0f && Time.time >= _layoutAt)
            {
                _layoutAt = -1f;
                Layout("start / placement");
            }
            else if (_mat != null && MeadowTuning.On && (Flat(HivePos()) - Flat(_layoutHive)).sqrMagnitude > 0.25f * 0.25f && !Holding())
                Layout("the hive moved"); // not while the hidden re-place grab is dragging it (RoomChanged covers the drop)
            else if (!_sceneUsed && _scene != null && _sceneBegun && Time.time >= _nextSceneTry && _director != null && _director.Beat == GameBeat.Pre)
            {
                _nextSceneTry = Time.time + 2f;
                bool ok = false;
                try { ok = _scene.TryFill(_sceneData) && _sceneData.FloorPolygon.Count >= 3; } catch (Exception) { }
                if (ok) Layout("room scan loaded");
            }
            float target = TargetHealth();
            float k = 1f - Mathf.Exp(-Time.deltaTime * (target > _health ? 0.9f : 3f));
            _health = Mathf.Abs(target - _health) < 0.002f ? target : Mathf.Lerp(_health, target, k);
        }

        bool Holding()
        {
            var rig = ArRig.Instance;
            return rig != null && rig.Placement != null && rig.Placement.Holding;
        }

        /// <summary>Placement code that sets the TableAnchor itself (a table or furniture hit) still gets a rig standing on
        /// the floor, upright: MeadowLayout.SetHivePose is the hook, this is the safety net.</summary>
        void KeepOnFloor()
        {
            if (!MeadowLayout.Active || _anchor == null || !HasFloor || Holding()) return;
            Vector3 p = _anchor.position;
            Vector3 f = _anchor.forward;
            bool tilted = Mathf.Abs(f.y) > 0.02f || Vector3.Dot(_anchor.up, Vector3.up) < 0.999f;
            if (Mathf.Abs(p.y - FloorY) < 0.03f && !tilted) return;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            _anchor.SetPositionAndRotation(new Vector3(p.x, FloorY, p.z), Quaternion.LookRotation(f.normalized, Vector3.up));
            Debug.Log("[VBS][Meadow] the hive rig was set " + (p.y - FloorY).ToString("0.00", C) + " m off the floor" + (tilted ? " / tilted" : "") +
                      ": stood back on the floor (use MeadowLayout.SetHivePose)");
        }

        /// <summary>Copies the tiles in view (a cone round the view direction, padded for a tile's size and both eyes)
        /// into the draw arrays.</summary>
        void Cull(Camera cam)
        {
            Vector3 eye = cam.transform.position, fwd = cam.transform.forward;
            float half;
            if (UnityEngine.XR.XRSettings.isDeviceActive) half = 68f;
            else
            {
                float t = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                half = Mathf.Atan(t * Mathf.Sqrt(1f + cam.aspect * cam.aspect)) * Mathf.Rad2Deg + 4f;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            half = Mathf.Max(half, 68f);
#endif
            float cos = Mathf.Cos(Mathf.Clamp(half, 30f, 89f) * Mathf.Deg2Rad);
            int tris = 0;
            for (int b = 0; b < 2; b++)
            {
                int count = b == 0 ? _nNear : _nFar, dn = 0;
                var m = _m[b];
                var data = _data[b];
                for (int k = 0; k < count; k++)
                {
                    Vector3 c = m[k].GetColumn(3);
                    c.y += 0.15f;
                    Vector3 o = c - eye;
                    float dist = o.magnitude;
                    float pad = 0.45f * new Vector3(m[k].m00, m[k].m10, m[k].m20).magnitude + 0.1f; // the tile's reach (stretched far tiles reach further)
                    if (dist > pad + 0.6f && Vector3.Dot(o, fwd) < cos * dist - pad) continue;
                    _dm[b][dn] = m[k];
                    _dd[b][dn] = data[k];
                    dn++;
                }
                _dn[b] = dn;
                tris += dn * (b == 0 ? TrisPerTile : _trisFar);
            }
            SubmittedTris = tris;
        }

        void LateUpdate()
        {
            if (_n == 0 || _mat == null) return;
            var cam = Camera.main;
            Vector3 face = cam != null ? cam.transform.position : _start;
            Vector3 trunk = HivePos();
            float sc = MeadowLayout.Active ? MeadowLayout.Scale : 1f;
            Shader.SetGlobalVector(MeadowId, new Vector4(_health, Time.time, 0f, 0f));
            Shader.SetGlobalVector(PlayerId, new Vector4(face.x, face.z, ClearRadius, 0.15f));
            Shader.SetGlobalVector(HiveId, new Vector4(trunk.x, trunk.z, MeadowLayout.Active ? StandClear * sc : 0f, PathHalf));
            // the mown yard round the hive and its props (anchor frame, metres): short grass so nothing is buried
            if (MeadowLayout.Active && _anchor != null)
            {
                Vector3 f = _anchor.forward; f.y = 0f; f = f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
                Vector3 yc = _anchor.position + f * (0.075f * sc);
                Shader.SetGlobalVector(YardId, new Vector4(yc.x, yc.z, 0.37f * sc, 0.23f * sc));
                Shader.SetGlobalVector(YardFwdId, new Vector4(f.x, f.z, MeadowTuning.Yard, 0.3f));
            }
            else Shader.SetGlobalVector(YardId, new Vector4(0f, 0f, -1f, -1f));
            Shader.SetGlobalVector(StartId, new Vector4(_start.x, _start.z, 0f, 0f));
            if (SystemInfo.supportsInstancing)
            {
                if (CullToView && cam != null)
                {
                    Cull(cam);
                    for (int b = 0; b < 2; b++) if (_dn[b] > 0) _mpb[2 + b].SetVectorArray(DataId, _dd[b]);
                    if (_dn[0] > 0) Graphics.DrawMeshInstanced(_tile, 0, _mat, _dm[0], _dn[0], _mpb[2], ShadowCastingMode.Off, false, gameObject.layer);
                    if (_dn[1] > 0 && _tileFar != null) Graphics.DrawMeshInstanced(_tileFar, 0, _mat, _dm[1], _dn[1], _mpb[3], ShadowCastingMode.Off, false, gameObject.layer);
                }
                else
                {
                    if (cam != null) Cull(cam); // the stat only
                    if (_nNear > 0) Graphics.DrawMeshInstanced(_tile, 0, _mat, _m[0], _nNear, _mpb[0], ShadowCastingMode.Off, false, gameObject.layer);
                    if (_nFar > 0 && _tileFar != null) Graphics.DrawMeshInstanced(_tileFar, 0, _mat, _m[1], _nFar, _mpb[1], ShadowCastingMode.Off, false, gameObject.layer);
                }
                if (Time.time >= _nextStatLog)
                {
                    _nextStatLog = Time.time + 15f;
                    Debug.Log("[VBS][Meadow] submitted floor tris " + SubmittedTris + " (" + _dn[0] + "/" + _nNear + " near, " + _dn[1] + "/" + _nFar +
                              " far tiles in view" + (CullToView ? "" : ", capture: all drawn") + ") + walls " + (_walls != null ? _walls.Tris : 0) +
                              ", room bees " + (_bees != null ? _bees.Shown : 0));
                }
            }
            if (_walls != null) _walls.Draw(_health, gameObject.layer);
            if (_bees != null && MeadowTuning.Bees)
                _bees.Tick(this, face, HivePos(), Stakes.StakesFlags.Comfort, gameObject.layer);
        }

        /// <summary>A flower head a bee can visit: tile i, flower f (world, grown at the current health). False if it isn't out.</summary>
        public bool Head(int i, int f, out Vector3 world)
        {
            world = default;
            if (i < 0 || i >= _nNear || f < 0 || f >= _heads.Count) return false;
            var h = _heads[f];
            var d = _data[0][i];
            float health = Mathf.Clamp01(_health + d.w);
            if (health < h.w + 0.1f) return false;
            float tall = d.z * Mathf.Lerp(0.62f, 1f, health);
            world = _m[0][i].MultiplyPoint3x4(new Vector3(h.x, h.y * tall, h.z));
            return true;
        }

        public int HeadCount => _heads.Count;

        // ------------------------------------------------------------------ the tile mesh
        static readonly Color[] GrassTips =
        {
            new Color(0.42f, 0.70f, 0.20f), new Color(0.33f, 0.62f, 0.16f), new Color(0.50f, 0.76f, 0.26f),
            new Color(0.28f, 0.56f, 0.18f), new Color(0.56f, 0.74f, 0.30f), new Color(0.64f, 0.74f, 0.30f),
        };
        // sick, not dead: pale straw-green (it is still a meadow, just a tired one)
        static readonly Color[] GrassWilt =
        {
            new Color(0.62f, 0.64f, 0.34f), new Color(0.56f, 0.60f, 0.30f), new Color(0.66f, 0.62f, 0.36f), new Color(0.50f, 0.56f, 0.28f),
        };

        struct Species
        {
            public Color petal, alt, centre;
            public float rMin, rMax;
            public int petals;
            public Species(Color p, Color a, Color c, float r0, float r1, int n) { petal = p; alt = a; centre = c; rMin = r0; rMax = r1; petals = n; }
        }

        static readonly Species[] Flowers =
        {
            new Species(new Color(0.96f, 0.96f, 0.92f), new Color(0.98f, 0.78f, 0.86f), new Color(1f, 0.8f, 0.15f), 0.022f, 0.03f, 7),  // daisy
            new Species(new Color(1f, 0.84f, 0.12f), new Color(1f, 0.66f, 0.1f), new Color(0.9f, 0.68f, 0.1f), 0.016f, 0.022f, 5),     // buttercup
            new Species(new Color(0.28f, 0.44f, 0.96f), new Color(0.55f, 0.34f, 0.92f), new Color(0.22f, 0.18f, 0.5f), 0.02f, 0.026f, 6), // cornflower
            new Species(new Color(0.96f, 0.24f, 0.14f), new Color(1f, 0.52f, 0.14f), new Color(0.14f, 0.1f, 0.1f), 0.028f, 0.036f, 4),   // poppy
            new Species(new Color(0.92f, 0.46f, 0.72f), new Color(0.78f, 0.4f, 0.88f), new Color(0.9f, 0.55f, 0.78f), 0.015f, 0.019f, 6), // clover
            new Species(new Color(0.62f, 0.40f, 0.88f), new Color(0.84f, 0.56f, 0.96f), new Color(0.96f, 0.86f, 0.4f), 0.017f, 0.021f, 5), // violet
        };
        static readonly Color[] Spikes = { new Color(0.55f, 0.38f, 0.92f), new Color(0.95f, 0.5f, 0.75f), new Color(0.42f, 0.5f, 0.95f), new Color(0.98f, 0.72f, 0.2f) };

        sealed class TileBuild
        {
            public readonly List<Vector3> v = new List<Vector3>(4096), pivot = new List<Vector3>(4096), wilt = new List<Vector3>(4096), alt = new List<Vector3>(4096);
            public readonly List<Color> col = new List<Color>(4096);
            public readonly List<Vector4> info = new List<Vector4>(4096);
            public readonly List<int> t = new List<int>(8192);

            public int V(Vector3 p, Vector3 root, Color c, float petalMask, Color w, Color a, float threshold, float sway, float rank, float accent)
            {
                if (QualitySettings.activeColorSpace == ColorSpace.Linear) { c = c.linear; w = w.linear; a = a.linear; } // picked as sRGB
                v.Add(p);
                pivot.Add(root);
                col.Add(new Color(c.r, c.g, c.b, petalMask));
                wilt.Add(new Vector3(w.r, w.g, w.b));
                alt.Add(new Vector3(a.r, a.g, a.b));
                info.Add(new Vector4(threshold, sway, rank, accent));
                return v.Count - 1;
            }

            public void T(int a, int b, int c) { t.Add(a); t.Add(b); t.Add(c); }
        }

        /// <summary>The tile mesh. far: 60% of the grass (the far floor), no bee heads recorded.</summary>
        Mesh BuildTile(float density, bool far, Mesh into, out int tris)
        {
            _builtDensity = density;
            var rnd = new System.Random(77);
            float R() => (float)rnd.NextDouble();
            var b = new TileBuild();
            float half = Tile * 0.5f;
            if (!far) _heads.Clear();

            // grass: a jittered grid so the tile is evenly full
            // the ground: a low mossy disc under every tile (overlapping its neighbours), so the floor never shows through
            {
                const int sides = 8;
                const float rad = 0.5f; // 8-gon inner radius 0.46: closes the grid's corner gaps (0.29 + jitter) even under trampled grass
                var moss = new Color(0.22f, 0.40f, 0.12f);
                var mossWilt = new Color(0.42f, 0.44f, 0.22f);
                int c0 = b.V(new Vector3(0f, 0.004f, 0f), Vector3.zero, moss * 1.05f, 0f, mossWilt, moss, -1f, 0f, 0f, -1f);
                for (int i = 0; i < sides; i++)
                {
                    float a = i * Mathf.PI * 2f / sides;
                    b.V(new Vector3(Mathf.Cos(a) * rad, 0.004f, Mathf.Sin(a) * rad), Vector3.zero, moss * 0.9f, 0f, mossWilt * 0.9f, moss, -1f, 0f, 0f, -1f);
                }
                for (int i = 0; i < sides; i++) b.T(c0, c0 + 1 + (i + 1) % sides, c0 + 1 + i);
            }

            int blades = Mathf.RoundToInt(250 * density * (far ? 0.6f : 1f));
            int g = Mathf.CeilToInt(Mathf.Sqrt(blades));
            int made = 0;
            for (int gx = 0; gx < g && made < blades; gx++)
                for (int gz = 0; gz < g && made < blades; gz++, made++)
                {
                    var root = new Vector3(-half + (gx + R()) * Tile / g, 0f, -half + (gz + R()) * Tile / g);
                    float h = R() < 0.1f ? 0.3f + R() * 0.1f : 0.09f + Mathf.Pow(R(), 0.8f) * 0.2f;
                    float w = 0.016f + R() * 0.014f;
                    float ang = R() * Mathf.PI * 2f;
                    var side = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (w * 0.5f);
                    float la = R() * Mathf.PI * 2f;
                    var lean = new Vector3(Mathf.Cos(la), 0f, Mathf.Sin(la)) * (h * (0.12f + R() * 0.35f));
                    Color tip = GrassTips[rnd.Next(GrassTips.Length)] * (0.9f + R() * 0.2f);
                    Color wt = GrassWilt[rnd.Next(GrassWilt.Length)] * (0.9f + R() * 0.2f);
                    float thr = R() < 0.22f ? 0.2f + R() * 0.6f : -1f; // most grass is always there; some comes back with health
                    float rank = R();
                    Vector3 mid = root + Vector3.up * (h * 0.5f) + lean * 0.3f, top = root + Vector3.up * h + lean;
                    int s = b.V(root - side, root, tip * 0.38f, 0f, wt * 0.5f, tip, thr, 0f, rank, 0f);
                    b.V(root + side, root, tip * 0.38f, 0f, wt * 0.5f, tip, thr, 0f, rank, 0f);
                    b.V(mid - side * 0.7f, root, tip * 0.72f, 0f, wt * 0.8f, tip, thr, 0.3f, rank, 0f);
                    b.V(mid + side * 0.7f, root, tip * 0.72f, 0f, wt * 0.8f, tip, thr, 0.3f, rank, 0f);
                    b.V(top, root, tip, 0f, wt, tip, thr, 1f, rank, 0f);
                    b.T(s, s + 1, s + 3);
                    b.T(s, s + 3, s + 2);
                    b.T(s + 2, s + 3, s + 4);
                }

            // ground cover: low three-leaf clover rosettes, so the floor never shows through
            int clovers = Mathf.RoundToInt(32 * density);
            for (int i = 0; i < clovers; i++)
            {
                var root = new Vector3(-half + R() * Tile, 0f, -half + R() * Tile);
                Color leaf = new Color(0.20f + R() * 0.08f, 0.46f + R() * 0.12f, 0.12f + R() * 0.05f);
                Color lw = new Color(0.50f, 0.46f, 0.26f);
                float r = 0.03f + R() * 0.02f, rot = R() * 6.28f, y = 0.01f + R() * 0.03f;
                int c0 = b.V(root + Vector3.up * y, root, leaf * 0.7f, 0f, lw * 0.7f, leaf, -1f, 0.2f, 0f, 0f);
                for (int k = 0; k < 3; k++)
                {
                    float a = rot + k * 2.094f;
                    Vector3 d1 = new Vector3(Mathf.Cos(a - 0.45f), 0.25f, Mathf.Sin(a - 0.45f)) * r, d2 = new Vector3(Mathf.Cos(a + 0.45f), 0.25f, Mathf.Sin(a + 0.45f)) * r;
                    int l1 = b.V(root + Vector3.up * y + d1, root, leaf, 0f, lw, leaf, -1f, 0.35f, 0f, 0f);
                    int l2 = b.V(root + Vector3.up * y + d2, root, leaf * 1.1f, 0f, lw, leaf, -1f, 0.35f, 0f, 0f);
                    b.T(c0, l1, l2);
                }
            }

            // wild flowers
            int flowers = Mathf.Max(8, Mathf.RoundToInt(22 * Mathf.Sqrt(density)));
            var stem = new Color(0.24f, 0.5f, 0.15f);
            var stemWilt = new Color(0.46f, 0.4f, 0.22f);
            for (int f = 0; f < flowers; f++)
            {
                var sp = Flowers[rnd.Next(Flowers.Length)];
                var root = new Vector3(-half + R() * Tile, 0f, -half + R() * Tile);
                float h = 0.14f + R() * 0.24f;
                float thr = R() < 0.15f ? R() * 0.12f : 0.15f + R() * 0.8f;
                float rank = R() * 0.5f;
                float la = R() * Mathf.PI * 2f;
                var leanDir = new Vector3(Mathf.Cos(la), 0f, Mathf.Sin(la));
                var lean = leanDir * (h * (0.05f + R() * 0.2f));
                Vector3 top = root + Vector3.up * h + lean;
                var side = Vector3.Cross(Vector3.up, leanDir) * 0.0028f;
                int s = b.V(root - side, root, stem * 0.6f, 0f, stemWilt * 0.6f, stem, thr, 0f, rank, 0f);
                b.V(root + side, root, stem * 0.6f, 0f, stemWilt * 0.6f, stem, thr, 0f, rank, 0f);
                b.V(top - side, root, stem, 0f, stemWilt, stem, thr, 1f, rank, 0f);
                b.V(top + side, root, stem, 0f, stemWilt, stem, thr, 1f, rank, 0f);
                b.T(s, s + 1, s + 3);
                b.T(s, s + 3, s + 2);
                // a leaf
                Vector3 lr = root + Vector3.up * (h * 0.35f) + lean * 0.35f;
                var ld = new Vector3(-leanDir.z, 0.5f, leanDir.x).normalized;
                int l = b.V(lr, root, stem * 0.8f, 0f, stemWilt, stem, thr, 0.35f, rank, 0f);
                b.V(lr + ld * 0.05f + Vector3.up * 0.01f, root, stem * 1.1f, 0f, stemWilt, stem, thr, 0.5f, rank, 0f);
                b.V(lr + Vector3.up * 0.02f, root, stem, 0f, stemWilt, stem, thr, 0.4f, rank, 0f);
                b.T(l, l + 1, l + 2);
                // the head: petals round a tilted centre
                Vector3 n = (Vector3.up + leanDir * (0.3f + R() * 0.5f)).normalized;
                Vector3 u = Vector3.Cross(n, Vector3.forward).normalized, v = Vector3.Cross(n, u);
                float r = Mathf.Lerp(sp.rMin, sp.rMax, R()) * 1.6f; // read from 2-3 m away
                float rot = R() * 6.28f;
                var petalWilt = Color.Lerp(new Color(0.55f, 0.45f, 0.3f), sp.petal, 0.2f);
                int c0 = b.V(top, root, sp.petal * 0.85f, 1f, petalWilt * 0.8f, sp.alt * 0.85f, thr, 1f, rank, 0f);
                for (int p = 0; p < sp.petals; p++)
                {
                    float a = rot + p * Mathf.PI * 2f / sp.petals, dA = Mathf.PI / sp.petals * 0.75f;
                    Vector3 Dir(float t) => u * Mathf.Cos(t) + v * Mathf.Sin(t);
                    int pl = b.V(top + Dir(a - dA) * r * 0.5f, root, sp.petal, 1f, petalWilt, sp.alt, thr, 1f, rank, 0f);
                    int pt = b.V(top + Dir(a) * r + n * 0.004f, root, sp.petal * 1.05f, 1f, petalWilt, sp.alt, thr, 1f, rank, 0f);
                    int pr = b.V(top + Dir(a + dA) * r * 0.5f, root, sp.petal, 1f, petalWilt, sp.alt, thr, 1f, rank, 0f);
                    b.T(c0, pl, pt);
                    b.T(c0, pt, pr);
                }
                Vector3 cc = top + n * 0.005f;
                float cr = r * 0.33f;
                int k0 = b.V(cc + u * cr, root, sp.centre, 0f, petalWilt * 0.6f, sp.centre, thr, 1f, rank, 0f);
                b.V(cc + (-u * 0.5f + v * 0.87f) * cr, root, sp.centre, 0f, petalWilt * 0.6f, sp.centre, thr, 1f, rank, 0f);
                b.V(cc + (-u * 0.5f - v * 0.87f) * cr, root, sp.centre, 0f, petalWilt * 0.6f, sp.centre, thr, 1f, rank, 0f);
                b.T(k0, k0 + 1, k0 + 2);
                if (!far) _heads.Add(new Vector4(top.x, top.y, top.z, thr));
            }

            // a tall lupine spike (accent: never near the player)
            {
                var root = new Vector3((R() - 0.5f) * Tile * 0.6f, 0f, (R() - 0.5f) * Tile * 0.6f);
                float h = 0.52f + R() * 0.18f;
                float thr = 0.35f + R() * 0.45f;
                Color sc = Spikes[rnd.Next(Spikes.Length)];
                Color sa = Spikes[rnd.Next(Spikes.Length)];
                var side = new Vector3(0.003f, 0f, 0f);
                int s = b.V(root - side, root, stem * 0.6f, 0f, stemWilt * 0.6f, stem, thr, 0f, 0.1f, 1f);
                b.V(root + side, root, stem * 0.6f, 0f, stemWilt * 0.6f, stem, thr, 0f, 0.1f, 1f);
                b.V(root + Vector3.up * h - side, root, stem, 0f, stemWilt, stem, thr, 1f, 0.1f, 1f);
                b.V(root + Vector3.up * h + side, root, stem, 0f, stemWilt, stem, thr, 1f, 0.1f, 1f);
                b.T(s, s + 1, s + 3);
                b.T(s, s + 3, s + 2);
                const int tiers = 7;
                for (int i = 0; i < tiers; i++)
                {
                    float f = i / (float)(tiers - 1);
                    float y = h * (0.58f + 0.42f * f);
                    float w = 0.022f * (1f - f * 0.6f);
                    float sway = 0.5f + 0.5f * f;
                    var c = Color.Lerp(sc, Color.white, f * 0.15f);
                    for (int x = 0; x < 2; x++)
                    {
                        float a = x * Mathf.PI * 0.5f + i * 0.7f;
                        var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * w;
                        Vector3 cy = root + Vector3.up * y;
                        int q = b.V(cy - d, root, c * 0.85f, 1f, new Color(0.5f, 0.42f, 0.3f), sa, thr, sway, 0.1f, 1f);
                        b.V(cy + d, root, c * 0.85f, 1f, new Color(0.5f, 0.42f, 0.3f), sa, thr, sway, 0.1f, 1f);
                        b.V(cy + Vector3.up * (h * 0.075f), root, c, 1f, new Color(0.55f, 0.45f, 0.32f), sa, thr, sway, 0.1f, 1f);
                        b.T(q, q + 1, q + 2);
                    }
                }
            }

            var mesh = into != null ? into : new Mesh { name = far ? "MeadowTileFar" : "MeadowTile" };
            mesh.Clear();
            mesh.indexFormat = b.v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(b.v);
            mesh.SetColors(b.col);
            mesh.SetUVs(0, b.info);
            mesh.SetUVs(1, b.pivot);
            mesh.SetUVs(2, b.wilt);
            mesh.SetUVs(3, b.alt);
            mesh.SetTriangles(b.t, 0);
            mesh.bounds = new Bounds(new Vector3(0f, 0.35f, 0f), new Vector3(Tile + 0.3f, 0.8f, Tile + 0.3f));
            tris = b.t.Count / 3;
            Debug.Log("[VBS][Meadow] " + (far ? "far" : "near") + " tile mesh: 8-tri ground disc, " + made + " blades, " + clovers + " clovers, " + flowers + " flowers, 1 spike, " + tris + " tris, " + b.v.Count + " verts (density " +
                      density.ToString("0.##", C) + ")");
            return mesh;
        }

        static Vector2 Flat(Vector3 v) => new Vector2(v.x, v.z);
        static string V(Vector3 v) => "(" + v.x.ToString("0.00", C) + "," + v.y.ToString("0.00", C) + "," + v.z.ToString("0.00", C) + ")";
    }

    /// <summary>The room bees: meadow.roomBees bees working the whole room, flying arcs between the meadow's open flowers
    /// (anywhere on the floor) and the wall vines (up to how far the green has climbed), resting 1.5-4 s on each.
    /// A third of them are out while the hive is sick, all of them at full bloom (comfort: half). Never within 0.6 m of
    /// the face. One instanced draw with the low-poly bee (~100 tris; 1.4x the swarm bee so it reads across the room).</summary>
    public sealed class MeadowBees
    {
        const float BeeLength = 0.07f, FaceClear = 0.6f, Speed = 0.85f;
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int WingFlapId = Shader.PropertyToID("_WingFlap");

        readonly int Max;
        Mesh _mesh;
        Material _mat;
        readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        readonly Matrix4x4[] _m;
        readonly Vector4[] _col, _tint;
        readonly float[] _flap;
        readonly Vector3[] _pos, _from, _to, _rest;
        readonly float[] _t0, _dur, _until, _arc;
        readonly bool[] _perched, _live;
        readonly System.Random _rnd = new System.Random(9);
        public int Visits { get; private set; }
        public int Shown { get; private set; }

        public MeadowBees()
        {
            Max = Mathf.Clamp(MeadowTuning.RoomBees, 0, 600);
            _m = new Matrix4x4[Mathf.Max(1, Max)];
            _col = new Vector4[_m.Length];
            _tint = new Vector4[_m.Length];
            _flap = new float[_m.Length];
            _pos = new Vector3[_m.Length]; _from = new Vector3[_m.Length]; _to = new Vector3[_m.Length]; _rest = new Vector3[_m.Length];
            _t0 = new float[_m.Length]; _dur = new float[_m.Length]; _until = new float[_m.Length]; _arc = new float[_m.Length];
            _perched = new bool[_m.Length]; _live = new bool[_m.Length];
            _mesh = BeeVat.AuthoredWorkerMesh() ?? GameMeshes.CreateBeeMesh();
            _mat = GameMaterials.Create("VBS_MeadowBee (runtime)", Color.white, vertexColors: true, doubleSided: true);
            _mat.enableInstancing = true;
            WingClock.EnableOn(_mat, _mesh); // use the static worker with shader wings for the full room pool
            Debug.Log("[VBS][Meadow] room bees use " + _mesh.name + " (" + _mesh.GetIndexCount(0) / 3 + " tris)");
            for (int i = 0; i < _m.Length; i++)
            {
                float k = 0.88f + 0.24f * (float)_rnd.NextDouble();
                _tint[i] = new Vector4(1f * k, 0.82f * k, 0.3f * k, 1f);
            }
        }

        public void Tick(MeadowField field, Vector3 face, Vector3 hive, bool comfort, int layer)
        {
            if (Max == 0 || _mesh == null || _mat == null || field.NearCount == 0 || field.HeadCount == 0) return;
            float now = Time.time;
            float h01 = Mathf.Clamp01((field.Health - 0.3f) / 1.15f);
            int want = Mathf.RoundToInt(Max * Mathf.Lerp(0.6f, 1f, h01) * (comfort ? 0.5f : 1f)); // plenty from the title on
            int shown = 0;
            for (int i = 0; i < Max; i++)
            {
                if (!_live[i])
                {
                    if (i >= want) continue;
                    // out at once all over the room (not a queue from the hive): start resting on a spot
                    if (!Spot(field, face, out var at, out var rest)) continue;
                    _live[i] = true;
                    _pos[i] = _to[i] = at;
                    _rest[i] = rest;
                    _perched[i] = true;
                    _until[i] = now + (float)_rnd.NextDouble() * 3f;
                }
                Quaternion rot;
                if (!_perched[i])
                {
                    float k = Mathf.Clamp01((now - _t0[i]) / _dur[i]);
                    float e = k * k * (3f - 2f * k);
                    Vector3 mid = (_from[i] + _to[i]) * 0.5f + Vector3.up * _arc[i];
                    Vector3 p = Vector3.Lerp(Vector3.Lerp(_from[i], mid, e), Vector3.Lerp(mid, _to[i], e), e);
                    p += new Vector3(Mathf.Sin(now * 7f + i), Mathf.Sin(now * 9f + i * 2f), Mathf.Cos(now * 8f + i)) * (0.015f * (1f - e));
                    Vector3 vel = p - _pos[i];
                    _pos[i] = p;
                    rot = vel.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(new Vector3(vel.x, vel.y * 0.4f, vel.z), Vector3.up) : Quaternion.identity;
                    _flap[shown] = 1f;
                    if (k >= 1f) { _perched[i] = true; _until[i] = now + 1.5f + (float)_rnd.NextDouble() * 2.5f; Visits++; }
                }
                else
                {
                    _pos[i] = _to[i] + _rest[i] * (0.012f + 0.004f * Mathf.Sin(now * 11f + i));
                    Vector3 up = _rest[i];
                    Vector3 f = Vector3.Cross(up, Vector3.right);
                    if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
                    rot = Quaternion.AngleAxis(i * 47f + Mathf.Sin(now * 0.7f + i) * 25f, up) * Quaternion.LookRotation(f, up);
                    _flap[shown] = 0f; // resting: wings still
                    if (now >= _until[i])
                    {
                        if (i >= want || !Spot(field, face, out var next, out var rest)) { _live[i] = false; continue; }
                        _perched[i] = false;
                        _from[i] = _pos[i];
                        _to[i] = next;
                        _rest[i] = rest;
                        _t0[i] = now;
                        float dist = Vector3.Distance(_pos[i], next);
                        _dur[i] = Mathf.Clamp(dist / Speed, 0.7f, 5f);
                        _arc[i] = Mathf.Min(0.6f, 0.12f + dist * 0.2f);
                    }
                }
                Vector3 off = _pos[i] - face;
                float d = off.magnitude;
                if (d < FaceClear) _pos[i] = face + (d > 1e-4f ? off / d : Vector3.forward) * FaceClear;
                _m[shown] = Matrix4x4.TRS(_pos[i], rot, new Vector3(BeeLength, BeeLength, BeeLength));
                _col[shown] = _tint[i];
                shown++;
            }
            Shown = shown;
            if (shown == 0 || !SystemInfo.supportsInstancing) return;
            _mpb.SetVectorArray(ColorId, _col);
            _mpb.SetFloatArray(WingFlapId, _flap);
            Graphics.DrawMeshInstanced(_mesh, 0, _mat, _m, shown, _mpb, ShadowCastingMode.Off, false, layer);
        }

        /// <summary>An open flower anywhere on the meadow floor, or (a third of the time) a spot on a wall vine.</summary>
        bool Spot(MeadowField field, Vector3 face, out Vector3 at, out Vector3 rest)
        {
            at = default;
            rest = Vector3.up;
            var walls = field.Walls;
            for (int tries = 0; tries < 12; tries++)
            {
                if (walls != null && _rnd.NextDouble() < 0.35 && walls.Spot(_rnd, out var wp, out var wn))
                {
                    if (Vector3.Distance(wp, face) < FaceClear + 0.2f) continue;
                    at = wp;
                    rest = (wn + Vector3.up * 0.3f).normalized;
                    return true;
                }
                int t = _rnd.Next(field.NearCount), f = _rnd.Next(field.HeadCount);
                if (!field.Head(t, f, out var head)) continue;
                if (Vector3.Distance(head, face) < FaceClear + 0.15f) continue;
                if (new Vector2(head.x - face.x, head.z - face.z).magnitude < MeadowField.ClearRadius + 0.3f) continue;
                at = head;
                rest = Vector3.up;
                return true;
            }
            return false;
        }
    }
}
