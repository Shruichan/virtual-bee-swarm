using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualBeeSwarm.AR;
using VirtualBeeSwarm.Stakes;
using Debug = UnityEngine.Debug;

namespace VirtualBeeSwarm.FlowerLife
{
    /// <summary>
    /// Flower life: the real table is a small garden for the whole ride, and it shows how the hive is doing.
    ///   where    clusters round the hive (beside and behind it, never in the choice arc), along the table's rim, a
    ///            row spilling out along the bottom of the meadow window, and a thin ring on the real floor round the
    ///            table (the room bloom, Bloom/, grows the big field on top of it at the escape).
    ///   progress `hook`: a few sick, drooping, dry-brown plants. Each solved station (HexagonPanel) heals a share of
    ///            them and sprouts new ones in a wave that runs out from the hive, with a pop of petals. Escape:
    ///            everything is out and bright, plus the escape-only plants. "...Not one flower." (the window's moon
    ///            scene): all of it wilts, and it springs back on the crash. Reset / `pre`: gone.
    ///   motion   every plant sways on its own phase with a gust that travels across the room; heads lean toward the
    ///            player; blooms bend away from a fingertip; bad news in the window (fire, wasps, heat) makes them
    ///            cower and an angry swarm makes them tremble; FlowerBees visit healthy blooms and dip them;
    ///            FlowerPetals drifts petals and pollen in from the window.
    ///   cost     one instanced draw per species (8), one for the soft shade under the plants, one for the bees, one
    ///            for the petals: ~25k tris at density 1, opaque (the shade quads are the only blended pixels),
    ///            no lights, no colliders, no GameObject per plant. FlowerLife.shader takes Depth API occlusion.
    ///   flags    FlowerLifeTuning (tuning.json flowerlife.*, env VBS_FLOWERLIFE_FLAGS).
    ///   test     env VBS_FLOWERLIFE_TEST="2" lights that many stations' worth at the start, "escape" shows it all.
    ///   log      "[VBS][FlowerLife] laid out ..." with counts, tris, draws and build ms.
    /// Installs itself into any scene with a GameDirector (like StakesDirector / RoomBloom); no other file changes.
    /// </summary>
    [DefaultExecutionOrder(70)]
    public class FlowerLifeDirector : MonoBehaviour
    {
        public static bool AutoInstall = true;
        public static FlowerLifeDirector Instance { get; private set; }
        public const string MaterialResource = "VBS_FlowerLife";
        public const int MaxPlants = 3072;

        public enum Zone : byte { Table, Rim, Sill, Floor }

        struct Plant
        {
            public int species;
            public Zone zone;
            public Vector3 pos;
            public float yaw, scale;
            public int tier;      // 0 from the start, 1-4 sprouts with that station, 5 escape only
            public int heal;      // tier 0: the station that heals it
            public float delay;   // s after an event (the wave from the hive)
            public float baseVit; // tier 0: how sick it starts
            public float phase;
            public float sillU;   // sill: -1..1 along the window's bottom edge
        }

        // ---- plants (struct of arrays for the per-frame state)
        readonly Plant[] _p = new Plant[MaxPlants];
        readonly float[] _vit = new float[MaxPlants], _vitV = new float[MaxPlants], _vitT = new float[MaxPlants];
        readonly float[] _grow = new float[MaxPlants], _growV = new float[MaxPlants], _growT = new float[MaxPlants];
        readonly float[] _load = new float[MaxPlants], _loadT = new float[MaxPlants];
        int _n;

        // ---- draws
        FlowerLifeMeshes.Species[] _species;
        Matrix4x4[][] _m;
        Vector4[][] _data;
        int[][] _index;
        int[] _count;
        MaterialPropertyBlock[] _mpb;
        Material _mat, _shadeMat;
        Mesh _shadeMesh;
        const int MaxShade = 200;
        readonly Matrix4x4[] _shadeM = new Matrix4x4[1023];
        readonly Vector4[] _shadeC = new Vector4[1023];
        MaterialPropertyBlock _shadeMpb;
        bool _instancing;

        // ---- scene
        GameDirector _director;
        HexagonPanel _hexagons;
        HiveBody _hive;
        FutureWindow _window;
        SwarmSystem _swarm;
        FlowerBees _bees;
        FlowerPetals _petals;
        Renderer _pane;
        bool _subscribed, _laidOut, _active, _escaped, _moon;
        readonly bool[] _lit = new bool[4];
        float _eventAt = -100f, _stress, _tremble, _relayoutAt = -1f;
        Vector3 _waveFrom, _player;
        Vector4 _wind;
        float _windAmp = 0.07f, _windHz = 1.7f, _lean = 0.14f, _droop = 0.6f;
        int _testLit = -1;
        bool _testEscape;
        string _stats = "";

        static readonly int DataId = Shader.PropertyToID("_FlData");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int ClockId = Shader.PropertyToID("_VbsFlClock");
        static readonly int WindId = Shader.PropertyToID("_VbsFlWind");
        static readonly int PlayerId = Shader.PropertyToID("_VbsFlPlayer");
        static readonly int Hand0Id = Shader.PropertyToID("_VbsFlHand0");
        static readonly int Hand1Id = Shader.PropertyToID("_VbsFlHand1");
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        // table height (m) each species is scaled to; floor plants are FloorScale times that
        static readonly float[] TableHeight = { 0.1f, 0.06f, 0.135f, 0.125f, 0.095f, 0.175f, 0.085f, 0.07f };
        const float FloorScale = 4f;

        public int Count => _n;
        public string Stats => _stats;
        public bool Escaped => _escaped;
        public int LitCount { get; private set; }
        public float Stress => _stress;
        public Vector4 Wind => _wind;

        // ------------------------------------------------------------------ install
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
            FlowerLifeTuning.EnsureLoaded();
            if (!FlowerLifeTuning.On) { Debug.Log("[VBS][FlowerLife] off (" + FlowerLifeTuning.LoadedFrom + ")"); return; }
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<FlowerLifeDirector>() != null) return;
            var go = new GameObject("FlowerLife");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<FlowerLifeDirector>();
            Debug.Log("[VBS][FlowerLife] installed (auto)");
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[VBS][FlowerLife] a second FlowerLifeDirector was disabled.");
                enabled = false;
                return;
            }
            Instance = this;
            FlowerLifeTuning.EnsureLoaded();
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            _hexagons = FindAnyObjectByType<HexagonPanel>(FindObjectsInactive.Include);
            _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            _window = FindAnyObjectByType<FutureWindow>(FindObjectsInactive.Include);
            _swarm = FindAnyObjectByType<SwarmSystem>(FindObjectsInactive.Include);
            _instancing = SystemInfo.supportsInstancing;

            var template = Resources.Load<Material>(MaterialResource);
            if (template == null)
            {
                var sh = Shader.Find("VirtualBeeSwarm/FlowerLife");
                if (sh != null) template = new Material(sh) { name = "VBS_FlowerLife (runtime)" };
            }
            if (template != null)
            {
                _mat = new Material(template) { name = "VBS_FlowerLife (instanced)", enableInstancing = true };
                _windAmp = _mat.GetFloat("_Wind");
                _windHz = _mat.GetFloat("_WindHz");
                _lean = _mat.GetFloat("_Lean");
                _droop = _mat.GetFloat("_Droop");
            }
            _species = FlowerLifeMeshes.Build(FlowerLifeTuning.Art, out int art);
            var artBlooms = new List<int>(4);
            var plainBlooms = new List<int>(8);
            for (int s = 0; s < _species.Length; s++)
                if (_species[s].bloom) (_species[s].art ? artBlooms : plainBlooms).Add(s);
            _artPick = artBlooms.ToArray();
            _plainPick = plainBlooms.ToArray();
            int k = _species.Length;
            _m = new Matrix4x4[k][];
            _data = new Vector4[k][];
            _index = new int[k][];
            _count = new int[k];
            _mpb = new MaterialPropertyBlock[k];
            for (int s = 0; s < k; s++)
            {
                _m[s] = new Matrix4x4[MaxPlants];
                _data[s] = new Vector4[MaxPlants];
                _index[s] = new int[MaxPlants];
                _mpb[s] = new MaterialPropertyBlock();
            }
            _shadeMesh = FlowerLifeMeshes.BuildShadeQuad();
            _shadeMat = GameMaterials.Create("VBS_FlowerShade (runtime)", Color.black, GameMaterials.Blend.Alpha);
            _shadeMat.SetTexture(GameMaterials.MainTexId, GameMeshes.CreateDotTexture());
            _shadeMat.renderQueue = (int)RenderQueue.Transparent - 5;
            ArMaterials.SetContactBias(_shadeMat, ArTuning.DepthBiasContact);
            _shadeMpb = new MaterialPropertyBlock();
            _bees = new FlowerBees();
            _petals = new FlowerPetals();

            string env = null;
            try { env = System.Environment.GetEnvironmentVariable("VBS_FLOWERLIFE_TEST"); } catch (System.Exception) { }
            if (!string.IsNullOrEmpty(env))
            {
                if (env.Trim().ToLowerInvariant() == "escape") _testEscape = true;
                else if (int.TryParse(env.Trim(), out int lit)) _testLit = Mathf.Clamp(lit, 0, 4);
            }
            Debug.Log("[VBS][FlowerLife] flags " + FlowerLifeTuning.Describe() + " | species " + k + " (" + art + " from the art kit)" +
                      (_mat == null ? " | NO SHADER: nothing will draw" : "") + (env != null ? " | test " + env : ""));
        }

        void OnEnable() => Subscribe();

        void Start() => Subscribe();

        void Subscribe()
        {
            if (_subscribed) return;
            if (_director == null) _director = GameDirector.Instance;
            if (_director == null) return;
            _director.BeatChanged += HandleBeat;
            _director.GroupResetDone += HandleReset;
            var rig = ArRig.Instance;
            if (rig != null) rig.RoomChanged += HandleRoom;
            _subscribed = true;
        }

        void OnDisable()
        {
            if (!_subscribed) return;
            if (_director != null)
            {
                _director.BeatChanged -= HandleBeat;
                _director.GroupResetDone -= HandleReset;
            }
            var rig = ArRig.Instance;
            if (rig != null) rig.RoomChanged -= HandleRoom;
            _subscribed = false;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ game events
        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            if (beat == GameBeat.Field) { Clear(); return; }
            if (beat == GameBeat.Pre)
            {
                // Let the room feel like a meadow from the title onward. Most plants start small and
                // drooped; each station then grows its share of the field as the hive recovers.
                Clear();
                _active = true;
                Layout();
                return;
            }
            if (!_laidOut) Layout();
            _active = true;
            bool escaped = beat >= GameBeat.Escape;
            if (escaped != _escaped)
            {
                _escaped = escaped;
                Event();
            }
        }

        void HandleReset() => Clear();

        void HandleRoom(RoomModel room)
        {
            // The table or wall moved (placement, height snap): lay the garden out again once things settle.
            if (_laidOut) _relayoutAt = Time.time + 0.4f;
        }

        void Clear()
        {
            _active = false;
            _escaped = false;
            _moon = false;
            _laidOut = false;
            for (int i = 0; i < 4; i++) _lit[i] = false;
            LitCount = 0;
            for (int i = 0; i < _n; i++)
            {
                _grow[i] = _growV[i] = _growT[i] = 0f;
                _vit[i] = _vitT[i] = _p[i].baseVit;
                _vitV[i] = 0f;
                _load[i] = _loadT[i] = 0f;
            }
            _bees?.Clear();
            _petals?.Clear();
        }

        /// <summary>Something changed (a station, the escape, the Moon): the plants answer in a wave from the hive.</summary>
        void Event()
        {
            _eventAt = Time.time;
            _waveFrom = _hive != null ? _hive.transform.position : transform.position;
        }

        // ------------------------------------------------------------------ layout
        void Layout()
        {
            var sw = Stopwatch.StartNew();
            _n = 0;
            for (int s = 0; s < _count.Length; s++) _count[s] = 0;
            var rig = ArRig.Instance;
            var room = rig != null ? rig.Room : null;
            Transform anchor = rig != null ? rig.TableAnchor : null;
            if (anchor == null)
            {
                var go = GameObject.Find("TableAnchor");
                anchor = go != null ? go.transform : (_hive != null ? _hive.transform.parent : transform);
            }
            Vector3 aPos = anchor.position;
            Vector3 fwd = anchor.forward; fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            float tableY = room != null && room.HasTable ? room.Table.Y : aPos.y;
            float floorY = room != null ? room.FloorY : 0f;
            Vector3 hivePos = _hive != null ? _hive.transform.position : aPos + fwd * 0.15f;
            Vector2 hiveL = new Vector2(Vector3.Dot(hivePos - aPos, right), Vector3.Dot(hivePos - aPos, fwd));
            var cam = Camera.main;
            _player = cam != null ? cam.transform.position : aPos - fwd * 0.8f + Vector3.up * 0.85f;
            var obstacles = Obstacles(anchor, tableY);
            AddUnionCardSpot(obstacles, anchor);
            var rnd = new System.Random(11);
            float density = FlowerLifeTuning.Density;

            // table half-size in the anchor's frame (the room's table, else 1.0 x 0.6 behind the front edge)
            float xMin = -0.5f, xMax = 0.5f, zMin = -0.26f, zMax = 0.5f;
            if (room != null && room.HasTable)
            {
                Vector3 c = room.Table.center - aPos;
                float cx = Vector3.Dot(c, right), cz = Vector3.Dot(c, fwd);
                xMin = cx - room.Table.half.x; xMax = cx + room.Table.half.x;
                zMin = cz - room.Table.half.y; zMax = cz + room.Table.half.y;
            }
            const float margin = 0.035f;

            // ---- clusters on the table: beside / behind the hive and along the rim, never in the choice arc
            var centres = new List<Vector2>(24);
            var centreRim = new List<bool>(24);
            int wantCentres = Mathf.RoundToInt(26 * Mathf.Sqrt(Mathf.Max(0.01f, density)));
            for (int tries = 0; tries < 900 && centres.Count < wantCentres && density > 0f; tries++)
            {
                float x = Mathf.Lerp(xMin + margin, xMax - margin, (float)rnd.NextDouble());
                float z = Mathf.Lerp(zMin + margin, zMax - margin, (float)rnd.NextDouble());
                bool arc = Mathf.Abs(x) < 0.42f && z < 0.0f;                       // choices, deliveries, the clipboard
                bool hive = Mathf.Abs(x - hiveL.x) < 0.2f && Mathf.Abs(z - hiveL.y) < 0.21f;
                if (arc || hive) continue;
                bool rim = Mathf.Abs(x) > 0.42f || z > 0.42f;
                // keep the front of the table mostly clear: the player's hands work there
                if (z < -0.02f && rnd.NextDouble() < 0.45) continue;
                Vector3 w = aPos + right * x + fwd * z;
                if (Blocked(obstacles, w, 0.05f)) continue;
                bool close = false;
                for (int j = 0; j < centres.Count && !close; j++) close = (centres[j] - new Vector2(x, z)).sqrMagnitude < 0.09f * 0.09f;
                if (close) continue;
                centres.Add(new Vector2(x, z));
                centreRim.Add(rim);
            }
            int tierCycle = 0;
            for (int ci = 0; ci < centres.Count; ci++)
            {
                // 7 of every 17 clusters are there (sick) from the start; the rest sprout with stations 1-4
                int clusterTier = ci % 17 < 7 ? 0 : 1 + (tierCycle++ % 4);
                int plants = 7 + rnd.Next(5);
                Vector2 c = centres[ci];
                bool back = c.y > hiveL.y + 0.05f;
                for (int k = 0; k < plants + 5; k++)
                {
                    bool extra = k >= plants; // escape-only blooms that thicken the cluster
                    float ang = (float)rnd.NextDouble() * Mathf.PI * 2f, rad = 0.012f + 0.06f * Mathf.Sqrt((float)rnd.NextDouble());
                    float x = c.x + Mathf.Cos(ang) * rad, z = c.y + Mathf.Sin(ang) * rad;
                    if (x < xMin + 0.015f || x > xMax - 0.015f || z < zMin + 0.015f || z > zMax - 0.015f) continue;
                    if (Mathf.Abs(x - hiveL.x) < 0.165f && Mathf.Abs(z - hiveL.y) < 0.175f) continue;
                    Vector3 w = aPos + right * x + fwd * z;
                    w.y = tableY;
                    if (Blocked(obstacles, w, 0.012f)) continue;
                    int sp = PickSpecies(rnd, k, back, false);
                    Add(new Plant
                    {
                        species = sp, zone = centreRim[ci] ? Zone.Rim : Zone.Table, pos = w,
                        yaw = (float)rnd.NextDouble() * 360f,
                        scale = TableHeight[sp] / _species[sp].height * (0.82f + 0.4f * (float)rnd.NextDouble()),
                        tier = extra ? 5 : clusterTier, heal = rnd.Next(4), baseVit = 0.14f + 0.2f * (float)rnd.NextDouble(),
                        phase = (float)rnd.NextDouble() * 6.2832f,
                    }, hivePos, rnd);
                }
            }
            int tablePlants = _n;

            // ---- the window sill: a row along the bottom of the meadow window (positions follow the pane each frame)
            _pane = null;
            if (_window != null)
            {
                var pt = _window.PaneTransform;
                _pane = pt != null ? pt.GetComponent<Renderer>() : null;
            }
            int sillStart = _n;
            if (FlowerLifeTuning.Sill && _pane != null)
            {
                int sill = Mathf.RoundToInt(22 * density);
                for (int k = 0; k < sill; k++)
                {
                    int sp = PickSpecies(rnd, k, false, false);
                    if (sp == (int)FlowerLifeMeshes.Kind.Sunflower) sp = (int)FlowerLifeMeshes.Kind.Poppy;
                    Add(new Plant
                    {
                        species = sp, zone = Zone.Sill, pos = _pane.bounds.center, yaw = (float)rnd.NextDouble() * 360f,
                        scale = TableHeight[sp] / _species[sp].height * (1.25f + 0.5f * (float)rnd.NextDouble()),
                        tier = k % 5 == 4 ? 5 : 1 + k % 4, heal = 0, baseVit = 0.3f, phase = (float)rnd.NextDouble() * 6.2832f,
                        sillU = sill > 1 ? Mathf.Lerp(-0.92f, 0.92f, (k + 0.3f * (float)rnd.NextDouble()) / (sill - 1f)) : 0f,
                    }, hivePos, rnd);
                }
            }
            int sillPlants = _n - sillStart;

            // ---- the floor meadow: grass under a broad, station-by-station flower spread
            int floorStart = _n;
            if (FlowerLifeTuning.Floor)
            {
                // A dense layer of grass is always present, while blooms fill in as stations are fixed.
                // At desktop density: 600 flowers plus 1,200 grass tufts; headset default scales to 450 + 900.
                int want = Mathf.RoundToInt(600 * density);
                int grassWant = Mathf.RoundToInt(1200 * density);
                Vector3 tc = room != null && room.HasTable ? room.Table.center : aPos + fwd * 0.1f;
                var placed = new List<Vector2>(want);
                for (int tries = 0; tries < want * 30 && placed.Count < want; tries++)
                {
                    float ang = (float)rnd.NextDouble() * Mathf.PI * 2f;
                    float rad = Mathf.Lerp(0.38f, 3.6f, Mathf.Pow((float)rnd.NextDouble(), 0.82f));
                    Vector3 w = new Vector3(tc.x + Mathf.Cos(ang) * rad, floorY, tc.z + Mathf.Sin(ang) * rad);
                    float lx = Vector3.Dot(w - aPos, right), lz = Vector3.Dot(w - aPos, fwd);
                    if (room != null && room.HasTable && lx > xMin - 0.1f && lx < xMax + 0.1f && lz > zMin - 0.1f && lz < zMax + 0.1f) continue; // under an actual table
                    Vector2 feet = new Vector2(w.x - _player.x, w.z - _player.z);
                    if (feet.sqrMagnitude < 0.55f * 0.55f) continue;                                             // keep the player's step clear
                    if (room != null && room.HasWall && Vector3.Dot(w - room.WindowWall.point, room.WindowWall.normal) < 0.14f) continue;
                    Vector2 f2 = new Vector2(w.x, w.z);
                    bool close = false;
                    for (int j = 0; j < placed.Count && !close; j++) close = (placed[j] - f2).sqrMagnitude < 0.115f * 0.115f;
                    if (close) continue;
                    placed.Add(f2);
                    int k = placed.Count;
                    int sp = PickSpecies(rnd, k, true, true);
                    Add(new Plant
                    {
                        species = sp, zone = Zone.Floor, pos = w, yaw = (float)rnd.NextDouble() * 360f,
                        scale = TableHeight[sp] * FloorScale / _species[sp].height * (0.8f + 0.45f * (float)rnd.NextDouble()),
                        tier = k % 20 < 5 ? 0 : k % 20 < 13 ? 1 + k % 4 : 5, heal = rnd.Next(4),
                        baseVit = 0.1f + 0.18f * (float)rnd.NextDouble(), phase = (float)rnd.NextDouble() * 6.2832f,
                    }, hivePos, rnd);
                }

                // Tufts fill the gaps between the blooms so the floor reads as grassland, not isolated pots.
                // They remain a little dry at first, then brighten with the rest of the meadow.
                var grassPlaced = new List<Vector2>(grassWant);
                for (int tries = 0; tries < grassWant * 24 && grassPlaced.Count < grassWant; tries++)
                {
                    float ang = (float)rnd.NextDouble() * Mathf.PI * 2f;
                    float rad = Mathf.Lerp(0.36f, 3.62f, Mathf.Sqrt((float)rnd.NextDouble()));
                    Vector3 w = new Vector3(tc.x + Mathf.Cos(ang) * rad, floorY, tc.z + Mathf.Sin(ang) * rad);
                    float lx = Vector3.Dot(w - aPos, right), lz = Vector3.Dot(w - aPos, fwd);
                    if (room != null && room.HasTable && lx > xMin - 0.1f && lx < xMax + 0.1f && lz > zMin - 0.1f && lz < zMax + 0.1f) continue;
                    Vector2 feet = new Vector2(w.x - _player.x, w.z - _player.z);
                    if (feet.sqrMagnitude < 0.55f * 0.55f) continue;
                    if (room != null && room.HasWall && Vector3.Dot(w - room.WindowWall.point, room.WindowWall.normal) < 0.14f) continue;
                    Vector2 f2 = new Vector2(w.x, w.z);
                    bool close = false;
                    for (int j = 0; j < grassPlaced.Count && !close; j++) close = (grassPlaced[j] - f2).sqrMagnitude < 0.095f * 0.095f;
                    if (close) continue;
                    grassPlaced.Add(f2);
                    int sp = (int)FlowerLifeMeshes.Kind.Grass;
                    Add(new Plant
                    {
                        species = sp, zone = Zone.Floor, pos = w, yaw = (float)rnd.NextDouble() * 360f,
                        scale = TableHeight[sp] * FloorScale / _species[sp].height * (0.9f + 0.28f * (float)rnd.NextDouble()),
                        tier = 0, heal = rnd.Next(4), baseVit = 0.62f + 0.14f * (float)rnd.NextDouble(), phase = (float)rnd.NextDouble() * 6.2832f,
                    }, hivePos, rnd);
                }
            }
            int floorPlants = _n - floorStart;

            // ---- per-species draw lists
            int tris = 0;
            for (int i = 0; i < _n; i++)
            {
                int s = _p[i].species;
                _index[s][_count[s]] = i;
                _m[s][_count[s]] = Matrix4x4.TRS(_p[i].pos, Quaternion.Euler(0f, _p[i].yaw, 0f), Vector3.one * _p[i].scale);
                _count[s]++;
                tris += _species[s].tris;
                _grow[i] = _growV[i] = _growT[i] = 0f;
                _vit[i] = _vitT[i] = _p[i].baseVit;
                _vitV[i] = 0f;
                _load[i] = _loadT[i] = 0f;
            }
            int draws = 0;
            for (int s = 0; s < _count.Length; s++) if (_count[s] > 0) draws++;
            _laidOut = true;
            _relayoutAt = -1f;
            Event();
            _bees.Begin(StakesFlags.Comfort ? FlowerLifeTuning.Bees / 2 : FlowerLifeTuning.Bees);
            sw.Stop();
            _stats = _n + " plants (table " + tablePlants + " in " + centres.Count + " clusters, sill " + sillPlants + ", floor " + floorPlants +
                     "), " + tris + " tris, " + draws + " flower draws, " + sw.Elapsed.TotalMilliseconds.ToString("0.0", C) + " ms";
            Debug.Log("[VBS][FlowerLife] laid out " + _stats + (room != null ? " | room " + room.Source : " | no ArRig") +
                      (_instancing ? "" : " | no instancing here (null device): not drawn"));
        }

        void Add(Plant p, Vector3 hivePos, System.Random rnd)
        {
            if (_n >= MaxPlants) return;
            Vector2 d = new Vector2(p.pos.x - hivePos.x, p.pos.z - hivePos.z);
            p.delay = d.magnitude / 0.9f + 0.25f * (float)rnd.NextDouble(); // the wave runs out from the hive at 0.9 m/s
            _p[_n++] = p;
        }

        static int[] _artPick, _plainPick;

        static int PickSpecies(System.Random rnd, int k, bool tallOk, bool floor)
        {
            // grass under every cluster, then a mix; sunflowers only behind the hive / on the floor
            if (k % 4 == 3 || (floor && k % 3 == 0)) return (int)FlowerLifeMeshes.Kind.Grass;
            // Daniel's models, where they are baked in: 4 in 10 on the table, a few on the floor (they are ~750 tris each)
            bool art = _artPick != null && _artPick.Length > 0 && rnd.NextDouble() < (floor ? 0.08 : 0.4);
            if (art) return _artPick[rnd.Next(_artPick.Length)];
            for (int tries = 0; tries < 6; tries++)
            {
                int s = _plainPick != null && _plainPick.Length > 0 ? _plainPick[rnd.Next(_plainPick.Length)] : rnd.Next(FlowerLifeMeshes.KindCount - 1);
                if (s == (int)FlowerLifeMeshes.Kind.Sunflower && (!tallOk || rnd.NextDouble() < 0.55)) continue;
                return s;
            }
            return _plainPick != null && _plainPick.Length > 0 ? _plainPick[0] : (int)FlowerLifeMeshes.Kind.Daisy;
        }

        struct Rect { public float x0, x1, z0, z1; }

        /// <summary>World XZ boxes of the things already standing on the table (rocket, thermometer, dressing props).</summary>
        List<Rect> Obstacles(Transform anchor, float tableY)
        {
            var list = new List<Rect>(16);
            if (anchor == null) return list;
            foreach (var r in anchor.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer || r is LineRenderer || r.GetComponent<TextMesh>() != null) continue;
                var t = r.transform;
                if (_hive != null && t.IsChildOf(_hive.transform)) continue;
                if (_window != null && t.IsChildOf(_window.transform)) continue;
                string n = t.name;
                if (n.StartsWith("BasePlate") || n.StartsWith("Grass") || n.StartsWith("grass")) continue;
                var b = r.bounds;
                if (b.min.y > tableY + 0.05f || b.max.y < tableY - 0.02f) continue; // not standing on the table
                if (b.size.x > 0.5f || b.size.z > 0.5f) continue;                 // a backdrop, not a prop
                list.Add(new Rect { x0 = b.min.x, x1 = b.max.x, z0 = b.min.z, z1 = b.max.z });
            }
            return list;
        }

        /// <summary>
        /// The union card (Stickers: StickerDirector.cardSpot, -0.375 / 0.03 in the table anchor's frame, ~0.3 x 0.17 m)
        /// is still hidden at layout time; keep its spot clear so stems don't grow through the progress tracker.
        /// </summary>
        static void AddUnionCardSpot(List<Rect> list, Transform anchor)
        {
            if (anchor == null) return;
            var c = new Vector3(-0.375f, 0f, 0.03f);
            float x0 = float.PositiveInfinity, x1 = float.NegativeInfinity, z0 = float.PositiveInfinity, z1 = float.NegativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                Vector3 w = anchor.TransformPoint(c + new Vector3((i & 1) == 0 ? -0.18f : 0.18f, 0f, (i & 2) == 0 ? -0.11f : 0.11f));
                x0 = Mathf.Min(x0, w.x); x1 = Mathf.Max(x1, w.x); z0 = Mathf.Min(z0, w.z); z1 = Mathf.Max(z1, w.z);
            }
            list.Add(new Rect { x0 = x0, x1 = x1, z0 = z0, z1 = z1 });
        }

        static bool Blocked(List<Rect> list, Vector3 w, float pad)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (w.x > r.x0 - pad && w.x < r.x1 + pad && w.z > r.z0 - pad && w.z < r.z1 + pad) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ frame
        void Update()
        {
            if (!_subscribed) Subscribe();
            if (_director == null) return;
            var beat = _director.Beat;
            if (!_active && beat != GameBeat.Field) HandleBeat(beat, beat); // installed mid-show, including the title
            if (!_active || !_laidOut) return;
            if (_relayoutAt >= 0f && Time.time >= _relayoutAt)
            {
                Layout();
                for (int i = 0; i < 4; i++) _lit[i] = false; // re-read below: the new plants sprout again
                LitCount = 0;
            }
            float now = Time.time, dt = Mathf.Min(Time.deltaTime, 0.05f);

            // ---- what the game says
            bool changed = false;
            int lit = 0;
            for (int i = 0; i < 4; i++)
            {
                bool on = _testEscape || i < _testLit || (_hexagons != null && _hexagons.IsLit(i));
                if (on != _lit[i]) { _lit[i] = on; changed = true; }
                if (on) lit++;
            }
            LitCount = lit;
            bool escaped = _testEscape || beat >= GameBeat.Escape;
            if (escaped != _escaped) { _escaped = escaped; changed = true; }
            string scene = _window != null ? _window.Scene : "";
            bool moon = beat == GameBeat.T0 && scene == GameIds.Scene.Moon;
            if (moon != _moon) { _moon = moon; changed = true; }
            if (changed) Event();
            bool windowOpen = _window != null && _window.IsOpen;
            float stressT = !_escaped && IsBadNews(scene) && windowOpen ? 1f : 0f;
            _stress = Mathf.MoveTowards(_stress, stressT, dt * (stressT > _stress ? 1.2f : 0.6f));
            float agitation = _swarm != null ? Mathf.Clamp01(_swarm.Agitation + (_swarm.Mode == SwarmMode.Angry ? 0.5f : 0f)) : 0f;
            _tremble = Mathf.MoveTowards(_tremble, Mathf.Max(agitation, _stress * 0.6f), dt * 2f);

            var cam = Camera.main;
            if (cam != null) _player = cam.transform.position;
            float windAngle = 0.7f + 0.5f * Mathf.Sin(now * 0.07f);
            _wind = new Vector4(Mathf.Cos(windAngle), Mathf.Sin(windAngle), _tremble, 0f);
            Shader.SetGlobalFloat(ClockId, now);
            Shader.SetGlobalVector(WindId, _wind);
            Shader.SetGlobalVector(PlayerId, new Vector4(_player.x, _player.y, _player.z, Mathf.Clamp(FlowerLifeTuning.Lean, 0f, 2f)));
            Shader.SetGlobalVector(Hand0Id, Hand(GameHand.Left));
            Shader.SetGlobalVector(Hand1Id, Hand(GameHand.Right));

            // ---- the window sill follows the pane (it grows at the escape and swells for the Moon)
            if (_pane != null) PlaceSill();

            // ---- plants: targets (answered in a wave from the hive), springs
            float since = now - _eventAt;
            float cower = 1f - 0.38f * _stress;
            for (int i = 0; i < _n; i++)
            {
                ref Plant p = ref _p[i];
                if (since >= p.delay)
                {
                    bool shown = p.tier == 0 || (p.tier <= 4 ? _lit[p.tier - 1] || _escaped : _escaped);
                    if (p.zone == Zone.Sill && !windowOpen) shown = false;
                    float vt = _escaped ? 1.18f : p.tier == 0 ? (_lit[p.heal] ? 1f : Mathf.Min(0.75f, p.baseVit + 0.1f * lit)) : 1f;
                    if (_moon) vt = 0.05f;
                    if (FlowerLifeTuning.Petals && shown && vt >= 0.95f && _vitT[i] < 0.95f && _growT[i] > 0.5f && p.zone != Zone.Floor && (i & 3) == 0)
                        _petals.Burst(Head(i), 3, p.species);
                    _growT[i] = shown ? 1f : 0f;
                    _vitT[i] = vt;
                }
                // critically-ish damped springs: a little overshoot reads as a pop
                float gt = _growT[i], g = _grow[i], gv = _growV[i];
                gv += ((gt - g) * 70f - gv * 9.5f) * dt;
                g += gv * dt;
                if (gt <= 0f && g < 0.002f) { g = 0f; gv = 0f; }
                _grow[i] = Mathf.Max(0f, g);
                _growV[i] = gv;
                float vtgt = _vitT[i] * (_vitT[i] > 0.9f ? Mathf.Lerp(1f, cower, 0.8f) : cower), v = _vit[i], vv = _vitV[i];
                vv += ((vtgt - v) * 26f - vv * 6.5f) * dt;
                v += vv * dt;
                _vit[i] = Mathf.Clamp(v, 0f, 1.3f);
                _vitV[i] = vv;
                _load[i] = Mathf.MoveTowards(_load[i], _loadT[i], dt * 5f);
            }

            // ---- bees and petals
            Vector3 door = _hive != null ? _hive.DoorPoint.position : _waveFrom;
            _bees.Tick(this, door, _player, now, dt, gameObject.layer);
            if (FlowerLifeTuning.Petals) _petals.Tick(this, _pane, windowOpen && !_moon && _stress < 0.5f, lit, _escaped, now, dt, gameObject.layer);

            Draw();
        }

        static bool IsBadNews(string scene)
        {
            switch (scene)
            {
                case GameIds.Scene.SmokerAngry:
                case GameIds.Scene.FeedSick:
                case GameIds.Scene.WaspsRaid:
                case GameIds.Scene.HeatStress:
                case GameIds.Scene.WhatIfFire:
                case GameIds.Scene.WhatIfSpray:
                case GameIds.Scene.WhatIfWinter:
                    return true;
                default:
                    return scene != null && scene.Contains("ravaged");
            }
        }

        static Vector4 Hand(GameHand hand)
        {
            return GameHands.TryGetIndexTip(hand, out var tip) ? new Vector4(tip.x, tip.y, tip.z, 0.13f) : Vector4.zero;
        }

        void PlaceSill()
        {
            var b = _pane.bounds;
            if (b.size.sqrMagnitude < 1e-6f) return;
            Transform pt = _pane.transform;
            Vector3 r = pt.right; r.y = 0f;
            r = r.sqrMagnitude > 1e-6f ? r.normalized : Vector3.right;
            Vector3 outward = -pt.forward; outward.y = 0f;
            outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : Vector3.back;
            float half = Mathf.Abs(Vector3.Dot(b.extents, new Vector3(Mathf.Abs(r.x), 0f, Mathf.Abs(r.z))));
            for (int s = 0; s < _count.Length; s++)
                for (int j = 0; j < _count[s]; j++)
                {
                    int i = _index[s][j];
                    if (_p[i].zone != Zone.Sill) continue;
                    float u = _p[i].sillU;
                    // the frame is a hexagon: its bottom edge is the middle half; the plants further out sit up its sides
                    float rise = Mathf.Max(0f, Mathf.Abs(u) - 0.5f) * 1.2f * b.extents.y;
                    Vector3 pos = new Vector3(b.center.x, b.min.y + rise - 0.01f, b.center.z) + r * (u * half * 0.86f) + outward * (0.035f + 0.012f * Mathf.Sin(_p[i].phase * 3f));
                    _p[i].pos = pos;
                    _m[s][j] = Matrix4x4.TRS(pos, Quaternion.Euler(0f, _p[i].yaw, 0f), Vector3.one * _p[i].scale);
                }
        }

        void Draw()
        {
            if (!_instancing || _mat == null) return;
            int layer = gameObject.layer;
            int shade = 0;
            for (int s = 0; s < _count.Length; s++)
            {
                int n = _count[s];
                if (n == 0) continue;
                var data = _data[s];
                var idx = _index[s];
                float h = _species[s].height;
                for (int j = 0; j < n; j++)
                {
                    int i = idx[j];
                    data[j] = new Vector4(_vit[i], _p[i].phase, _load[i], _grow[i]);
                    if (_p[i].zone != Zone.Sill && _p[i].zone != Zone.Floor && _grow[i] > 0.05f && shade < MaxShade) // floor plants: big overlapping blended quads, skipped
                    {
                        float size = h * _p[i].scale * (s == (int)FlowerLifeMeshes.Kind.Grass ? 1.5f : 0.95f) * Mathf.Min(1f, _grow[i]);
                        _shadeM[shade] = Matrix4x4.TRS(_p[i].pos + Vector3.up * 0.0015f, Quaternion.identity, new Vector3(size, 1f, size));
                        _shadeC[shade] = new Vector4(0f, 0f, 0f, 0.34f * Mathf.Min(1f, _grow[i]));
                        shade++;
                    }
                }
                _mpb[s].SetVectorArray(DataId, data);
                Graphics.DrawMeshInstanced(_species[s].mesh, 0, _mat, _m[s], n, _mpb[s], ShadowCastingMode.Off, false, layer);
            }
            if (shade > 0 && ArTuning.Shadows)
            {
                _shadeMpb.SetVectorArray(ColorId, _shadeC);
                Graphics.DrawMeshInstanced(_shadeMesh, 0, _shadeMat, _shadeM, shade, _shadeMpb, ShadowCastingMode.Off, false, layer);
            }
        }

        // ------------------------------------------------------------------ for the bees and petals
        public bool IsBloom(int i) => i >= 0 && i < _n && _species[_p[i].species].bloom;
        public Zone ZoneOf(int i) => _p[i].zone;
        public Vector3 Root(int i) => _p[i].pos;
        public int SpeciesOf(int i) => _p[i].species;

        /// <summary>Out, upright and healthy: a bee wants it.</summary>
        public bool Landable(int i)
        {
            return i >= 0 && i < _n && _species[_p[i].species].bloom && _grow[i] > 0.9f && _vit[i] > 0.8f && _growT[i] > 0.5f && _vitT[i] > 0.8f;
        }

        public void SetLoad(int i, float load)
        {
            if (i >= 0 && i < _n) _loadT[i] = load;
        }

        /// <summary>Where the bloom's head is right now: the shader's vertex maths for the head point (weight 1).</summary>
        public Vector3 Head(int i)
        {
            ref Plant p = ref _p[i];
            var sp = _species[p.species];
            float vit = Mathf.Clamp01(_vit[i]), wilt = 1f - vit;
            Vector3 local = sp.head * (_grow[i] * Mathf.Lerp(0.74f, 1f, vit));
            Vector3 wp = p.pos + Quaternion.Euler(0f, p.yaw, 0f) * (local * p.scale);
            float hgt = Mathf.Max(0f, wp.y - p.pos.y);
            float clock = Time.time;
            wp.x += Mathf.Cos(p.phase * 1.7f) * hgt * _droop * wilt;
            wp.z += Mathf.Sin(p.phase * 1.7f) * hgt * _droop * wilt;
            wp.y -= hgt * (0.42f * wilt + 0.16f * _load[i]);
            float t = clock * _windHz + p.phase;
            float gust = 0.5f + 0.5f * Mathf.Sin((wp.x * _wind.x + wp.z * _wind.y) * 2.6f - clock * 1.3f);
            float amp = _windAmp * (0.4f + 0.6f * gust) * Mathf.Lerp(0.4f, 1f, vit);
            float sx = Mathf.Sin(t + wp.x * 5f), sz = Mathf.Cos(t * 0.83f + wp.z * 4f);
            wp.x += hgt * amp * sx;
            wp.z += hgt * amp * sz;
            wp.x += _wind.x * hgt * amp * gust * 0.8f;
            wp.z += _wind.y * hgt * amp * gust * 0.8f;
            Vector2 to = new Vector2(_player.x - p.pos.x, _player.z - p.pos.z);
            if (to.sqrMagnitude > 1e-8f)
            {
                to.Normalize();
                float k = hgt * _lean * vit * Mathf.Clamp(FlowerLifeTuning.Lean, 0f, 2f);
                wp.x += to.x * k;
                wp.z += to.y * k;
            }
            return wp;
        }
    }
}
