using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VirtualBeeSwarm.Dressing
{
    /// <summary>
    /// Table life, from dartiros' art (DressingArt): a wild bee nest beside the hive whose solitary bee pops out every
    /// 12-22 s, flies a little loop, lands, shakes and goes home (and dives back in when the dark swarm dives); a
    /// bumblebee that drifts in through the meadow window in the calm moments (meet, after a right pick, the unload),
    /// flies a lazy figure-8 over the table, sits on the nest or a tuft for 3 s and leaves ("twenty thousand kinds of
    /// bee"); a tiny wasp that sniffs the syrup once it's set down (it sets up the raid); grass tufts around the hive's
    /// foot, two more sprouting per fix; an optional shallow water dish and cavity-nesting block as habitat prompts;
    /// and the SEEDS MEADOW MIX packet that a crew of 3 clips onto the end card (the contract card when the build has
    /// one, else the score card).
    ///
    /// No edits elsewhere: installs itself into any scene with a GameDirector (like StakesDirector), listens to
    /// BeatChanged / GroupResetDone, GameNet.VoiceLineStarted, BeeDelivery.Landed and the "[VBS] choice ..." log line.
    /// Everything hangs off the TableAnchor (follows placement). Cheap: one instanced draw for the tufts, three skinned
    /// insects (renderers off while hidden), no colliders, no lights, nothing allocated per frame.
    /// Switches (tuning.json): dressing.on, dressing.nest, dressing.tufts, dressing.solitary, dressing.visitors,
    /// dressing.wasp, dressing.seed, dressing.habitat (all default true). Env VBS_DRESSING=off|on wins;
    /// VBS_DRESSING_FLAGS="nest=false,habitat=false,...".
    /// </summary>
    [DefaultExecutionOrder(60)]
    public class DressingDirector : MonoBehaviour
    {
        public static bool AutoInstall = true;
        public static DressingDirector Instance { get; private set; }

        public static bool On = true, Nest = true, Tufts = true, Solitary = true, Visitors = true, Wasp = true, Seed = true, Habitat = true;

        [Header("Layout (TableAnchor space; the player stands at -Z)")]
        [SerializeField] Vector3 nestPosition = new Vector3(-0.3f, 0f, 0.21f);
        [SerializeField] Vector3 waterDishPosition = new Vector3(0.29f, 0f, 0.18f);
        [SerializeField] Vector3 beeHotelPosition = new Vector3(-0.25f, 0f, -0.18f);
        [SerializeField] float tuftHeight = 0.06f;
        [Header("Insects")]
        [SerializeField] float solitaryScale = 0.8f;
        [Tooltip("x the art (5.8 cm): ~5 cm, a head bigger than the swarm's workers, so it reads as a different bee.")]
        [SerializeField] float bumbleScale = 0.88f;
        [SerializeField] float waspScale = 1.2f;
        [SerializeField] Vector2 solitaryGap = new Vector2(12f, 22f);
        [SerializeField] float visitCooldown = 22f;
        [Tooltip("The bumblebee's figure-8: centre over the table in front of the hive, lobe sizes.")]
        [SerializeField] Vector3 eightCenter = new Vector3(-0.04f, 0.36f, -0.05f);
        [SerializeField] Vector2 eightSize = new Vector2(0.22f, 0.07f);

        public const string Tag = "[VBS][Dressing] ";

        GameDirector _director;
        GameNet _net;
        HiveBody _hive;
        BeeDelivery _delivery;
        Transform _anchor, _root, _nest, _nestDoor, _seed;
        DressingArt _art;
        DressingCritter _solitary, _bumble, _wasp;
        DressingTufts _tufts;
        Bounds _foot;
        Vector3 _nestTop, _nestOut = Vector3.back;
        bool _subscribed, _ready, _hasContractLine, _waspDone, _live, _footMeasured;
        float _liveAt = -1f;
        int _fixes, _grievances, _layer;
        float _nextSolitary = -1f, _visitAt = -1f, _nextVisitOk, _waspAt = -1f, _seedAt = -1f;
        string _visitWhy = "";
        Transform _syrup;

        // seed packet on the end card
        Transform _seedCard;
        Vector3 _seedLocal;
        Quaternion _seedRot;
        bool _seedSent, _seedFollow, _seedOnContractLine;
        Transform _seedTarget;

        // ------------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!AutoInstall) return;
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<DressingDirector>() != null) return;
            LoadSwitches();
            if (!On) { Debug.Log(Tag + "off (dressing.on=false / VBS_DRESSING=off)"); return; }
            var go = new GameObject("Dressing");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<DressingDirector>();
        }

        static void LoadSwitches()
        {
            var flat = new Dictionary<string, string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            ReadTuning(Path.Combine(Application.streamingAssetsPath, "tuning.json"), flat);
#endif
            ReadTuning(Path.Combine(Application.persistentDataPath, "tuning.json"), flat);
            foreach (var kv in flat)
                if (kv.Key.StartsWith("dressing.", StringComparison.Ordinal)) Set(kv.Key.Substring(9), kv.Value);
            string env = null, flags = null;
            try { env = Environment.GetEnvironmentVariable("VBS_DRESSING"); flags = Environment.GetEnvironmentVariable("VBS_DRESSING_FLAGS"); }
            catch (Exception) { }
            if (!string.IsNullOrEmpty(env)) Set("on", env);
            if (!string.IsNullOrEmpty(flags))
                foreach (var pair in flags.Split(','))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0) Set(pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim());
                }
        }

        static void ReadTuning(string path, Dictionary<string, string> into)
        {
            try
            {
                if (File.Exists(path)) AR.ArTuning.FlatJson.Parse(File.ReadAllText(path), into);
            }
            catch (Exception e) { Debug.LogWarning(Tag + "tuning: could not read " + path + ": " + e.Message); }
        }

        /// <summary>One switch by its tuning name without the "dressing." prefix ("on", "nest", ...). False = unknown.</summary>
        public static bool Set(string key, string value)
        {
            string v = (value ?? "").Trim().Trim('"').ToLowerInvariant();
            bool b = v == "true" || v == "1" || v == "on" || v == "yes";
            if (!b && v != "false" && v != "0" && v != "off" && v != "no") return false;
            switch (key)
            {
                case "on": On = b; return true;
                case "nest": Nest = b; return true;
                case "tufts": Tufts = b; return true;
                case "solitary": Solitary = b; return true;
                case "visitors": Visitors = b; return true;
                case "wasp": Wasp = b; return true;
                case "seed": Seed = b; return true;
                case "habitat": Habitat = b; return true;
            }
            return false;
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { enabled = false; return; }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Start()
        {
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            _net = GameNet.Instance != null ? GameNet.Instance : FindAnyObjectByType<GameNet>();
            _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include); // hidden until a crew brings it in
            _delivery = BeeDelivery.Instance != null ? BeeDelivery.Instance : FindAnyObjectByType<BeeDelivery>();
            _art = Resources.Load<DressingArt>(DressingArt.ResourceName);
            _anchor = _hive != null ? _hive.transform.parent : null;
            if (_anchor == null && _delivery != null) _anchor = _delivery.TableAnchor;
            if (_director == null || _anchor == null || _art == null)
            {
                Debug.Log(Tag + "nothing to dress (" + (_art == null ? "no Resources/DressingArt" : "no hive / TableAnchor") + ")");
                enabled = false;
                return;
            }
            _layer = _hive != null ? _hive.gameObject.layer : 0;
            _root = new GameObject("[VBS] Dressing").transform;
            _root.SetParent(_anchor, false);
            _foot = new Bounds(new Vector3(0f, 0.18f, 0.15f), new Vector3(0.31f, 0.36f, 0.41f)); // measured when the hive has landed
            foreach (var id in GameLines.ClipIds()) if (id == "room_end_contract") _hasContractLine = true;

            var parts = new List<string>();
            if (Nest && _art.nest != null) parts.Add(BuildNest());
            if (Habitat) BuildHabitatProps(parts);
            if (Tufts) parts.Add("tufts round the hive (" + DressingTufts.BaseCount + " + " + DressingTufts.PerFix + " per fix, once it lands)");
            if (Solitary && _nest != null && _art.solitaryBee != null)
                _solitary = Spawn(_art.solitaryBee, "solitary bee", solitaryScale, 0.004f, 1f, parts);
            if (Visitors && _art.bumblebee != null) _bumble = Spawn(_art.bumblebee, "bumblebee", bumbleScale, 0.012f, 0.55f, parts);
            if (Wasp && _art.tinyWasp != null) _wasp = Spawn(_art.tinyWasp, "tiny wasp", waspScale, 0.003f, 1.6f, parts);
            if (Seed && _art.seedPacket != null) parts.Add(BuildSeed());
            if (_delivery != null) DressingCritter.Head = _delivery.Head;
            Subscribe();
            _ready = true;
            var contract = FindContractCard(false);
            Debug.Log(Tag + "installed: " + string.Join(", ", parts) + "; end card " +
                      (contract != null ? "= " + contract.name : "= the score card (no contract card in the scene" +
                      (_hasContractLine ? "; on room_end_contract the packet goes on the table)" : ")")));
            ResetRun("start");
        }

        void OnEnable() { if (_ready) Subscribe(); }

        void OnDisable()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (_director != null) { _director.BeatChanged -= HandleBeat; _director.GroupResetDone -= HandleReset; }
            if (_net != null) _net.VoiceLineStarted -= HandleLine;
            if (_delivery != null) _delivery.Landed -= HandleLanded;
            if (_net != null) _net.ChoiceMade -= HandleChoice;
        }

        void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            _director.BeatChanged += HandleBeat;
            _director.GroupResetDone += HandleReset;
            if (_net != null) _net.VoiceLineStarted += HandleLine;
            if (_delivery != null) _delivery.Landed += HandleLanded;
            if (_net != null) _net.ChoiceMade += HandleChoice;
        }

        // ------------------------------------------------------------------ build
        Bounds HiveFootprint()
        {
            var b = _foot;
            if (_hive == null) return b;
            bool any = false;
            Matrix4x4 toAnchor = _anchor.worldToLocalMatrix;
            foreach (var r in _hive.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || !r.enabled || r.name.Contains("Glow") || r.GetComponent<TextMesh>() != null) continue;
                var lb = r.localBounds;
                Matrix4x4 m = toAnchor * r.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 p = m.MultiplyPoint3x4(c);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return b;
        }

        string BuildNest()
        {
            var go = Instantiate(_art.nest, _root, false);
            go.name = "WildBeeNest";
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            _nest = go.transform;
            Vector3 toPlayer = new Vector3(0.05f, 0f, -0.7f) - nestPosition;
            toPlayer.y = 0f;
            _nest.localPosition = nestPosition;
            _nest.localRotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up); // entrance (+Z) toward the player
            _nestDoor = DressingCritter.FindDeep(_nest, "DoorPoint");
            _nestOut = _nest.localRotation * Vector3.forward;
            var nb = LocalBounds(_nest, _root);
            _nestTop = new Vector3(nb.center.x, nb.max.y, nb.center.z);
            go.SetActive(false); // shows up (with a pop) once the hive is on the table
            return "nest at (" + nestPosition.x.ToString("0.00") + ", " + nestPosition.z.ToString("0.00") + ") " +
                   nb.size.x.ToString("0.00") + " x " + nb.size.y.ToString("0.00") + " m" + (_nestDoor != null ? "" : " (no DoorPoint)");
        }

        string BuildTufts()
        {
            Mesh mesh = Resources.Load<Mesh>("Bloom_Grass");
            Material mat = null;
            string src = "Bloom_Grass";
            if (mesh != null) mat = _art.vertexColorMaterial;
            if (mesh == null || mat == null) { mesh = _art.grass; mat = _art.grassMaterial; src = "GrassTuft"; }
            if (mesh == null || mat == null) return "no tufts (no grass art)";
            if (!mat.enableInstancing) mat = new Material(mat) { name = mat.name + " (instanced)", enableInstancing = true };
            _tufts = new DressingTufts(mesh, mat, _anchor, _foot, tuftHeight, src);
            _tufts.SetFixes(_fixes);
            return DressingTufts.BaseCount + " tufts (+" + DressingTufts.PerFix + " per fix, " + src + ") round a hive foot of " +
                   _foot.size.x.ToString("0.00") + " x " + _foot.size.z.ToString("0.00") + " m";
        }

        void BuildHabitatProps(List<string> parts)
        {
            AddHabitatProp(parts, "pollinator water dish", "Habitat/pollinator_water_dish.obj", "Habitat/pollinator_water_dish.mtl",
                waterDishPosition, Quaternion.identity);
            // The block's openings face +Z in the source mesh; turn them toward the player at -Z.
            AddHabitatProp(parts, "solitary bee nest block", "Habitat/solitary_bee_nest_block.obj", "Habitat/solitary_bee_nest_block.mtl",
                beeHotelPosition, Quaternion.Euler(0f, 180f, 0f));
        }

        void AddHabitatProp(List<string> parts, string label, string objPath, string mtlPath, Vector3 position, Quaternion rotation)
        {
            var obj = Resources.Load<TextAsset>(objPath);
            var mtl = Resources.Load<TextAsset>(mtlPath);
            if (DressingHabitatMesh.Create(label, obj, mtl, _root, position, rotation) != null) parts.Add(label);
        }

        DressingCritter Spawn(GameObject prefab, string label, float scale, float wobble, float rate, List<string> parts)
        {
            var go = Instantiate(prefab, _root, false);
            go.name = "[VBS] " + label;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            var c = go.AddComponent<DressingCritter>();
            c.Label = label;
            c.Init(scale, wobble, rate);
            parts.Add(label);
            return c;
        }

        string BuildSeed()
        {
            var go = Instantiate(_art.seedPacket, _root, false);
            go.name = "SeedPacket";
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            _seed = go.transform;
            // home: on the table, front right, leaning back toward the player (used when no card is out)
            _seed.localPosition = new Vector3(0.17f, 0f, -0.12f);
            _seed.localRotation = Quaternion.Euler(-12f, 180f - 15f, 0f);
            var di = go.AddComponent<DeliveryItem>();
            di.castShadow = false;
            di.crewOverride = 3;
            di.weight = 0.03f;
            if (_delivery != null) _delivery.Register(_seed); // home recorded now, before any flight
            go.SetActive(false);
            return "seed packet";
        }

        // ------------------------------------------------------------------ events
        void HandleReset() => ResetRun("reset");

        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            if (beat == GameBeat.Pre) { ResetRun("pre"); return; }
            if (!CritterBeat(beat))
            {
                if (_solitary != null && _solitary.Visible) _solitary.Run(GoHome(_solitary, true));
                if (_bumble != null && _bumble.Visible) _bumble.Run(FlyOff(_bumble, 1.6f, "beat " + beat));
                if (_wasp != null && _wasp.Visible) _wasp.Run(FlyOff(_wasp, 2.2f, "beat " + beat));
                _visitAt = -1f;
            }
            if (CritterBeat(beat) && _nextSolitary < 0f) _nextSolitary = Time.time + 4f; // also after a Board jump past meet
            if (beat == GameBeat.Meet) QueueVisit(6f, "meet");
            if (beat == GameBeat.Map || beat == GameBeat.Field) QueueVisit(4f, "unload");
        }

        void HandleLine(GameNet.VoiceLineInfo info)
        {
            if (!_ready || _seed == null || info.id == null) return;
            // v2: the contract card comes with room_end_contract; it may not fly with BeeDelivery, so look for it a moment later
            if (info.id == "room_end_contract") { _seedOnContractLine = true; _seedAt = Time.time + 1.2f; }
            // delivery off: the score card pops in (no Landed); it is the end card when there's no contract card
            else if (info.id == "room_escape" && !_seedSent && _seedAt < 0f && FindContractCard(false) == null)
            {
                _seedTarget = FindScoreCard();
                if (_seedTarget != null) _seedAt = Time.time + 3f;
            }
        }

        void HandleLanded(Transform t)
        {
            if (!_ready || t == null) return;
            if (_seed != null && IsEndCard(t) && t != _seedCard) { _seedTarget = t; _seedAt = Time.time + 0.5f; }
            if (_wasp != null && !_waspDone && _director.Beat == GameBeat.Feed && t.name.IndexOf("Syrup", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _syrup = t;
                _waspAt = Time.time + 0.8f; // the beat often turns to wasps ~1 s after the set-down: it scouts in either
            }
        }

        void HandleChoice(GameStation station, string choice, bool correct)
        {
            if (!_ready) return;
            var beat = _director.Beat;
            if (beat < GameBeat.Smoker || beat >= GameBeat.Escape) return;
            if (correct)
            {
                _fixes++;
                if (_tufts != null)
                {
                    _tufts.SetFixes(_fixes);
                    Debug.Log(Tag + "fix " + _fixes + ": " + _tufts.Count + " tufts round the hive");
                }
                QueueVisit(3.5f, "fix " + _fixes);
            }
            else
            {
                _grievances++;
                // the dark swarm dives: the small ones take cover
                if (_solitary != null && _solitary.Visible) { _solitary.Run(GoHome(_solitary, true)); Debug.Log(Tag + "solitary bee dives home (grievance)"); }
                if (_bumble != null && _bumble.Visible) _bumble.Run(FlyOff(_bumble, 1.8f, "grievance"));
                _nextSolitary = Mathf.Max(_nextSolitary, Time.time + 8f);
                _visitAt = -1f;
            }
        }

        static bool CritterBeat(GameBeat b) =>
            b == GameBeat.Meet || b == GameBeat.Smoker || b == GameBeat.Feed || b == GameBeat.Wasps || b == GameBeat.Heat ||
            b == GameBeat.WhatIf || b == GameBeat.Escape || b == GameBeat.Map || b == GameBeat.Field;

        static bool VisitBeat(GameBeat b) => CritterBeat(b) && b != GameBeat.Escape;

        void QueueVisit(float delay, string why)
        {
            if (_bumble == null || _bumble.Busy) return;
            float at = Mathf.Max(Time.time + delay, _nextVisitOk);
            if (_visitAt < 0f || at < _visitAt) { _visitAt = at; _visitWhy = why; }
        }

        void ResetRun(string why)
        {
            _fixes = _grievances = 0;
            if (_tufts != null) _tufts.SetFixes(0);
            if (_solitary != null) _solitary.Stop(true);
            if (_bumble != null) _bumble.Stop(true);
            if (_wasp != null) _wasp.Stop(true);
            _nextSolitary = -1f;
            _visitAt = _waspAt = _seedAt = -1f;
            _seedOnContractLine = false;
            _nextVisitOk = 0f;
            _waspDone = false;
            _syrup = null;
            HideSeed();
            if (why != "start") Debug.Log(Tag + "reset (" + why + ")");
        }

        // ------------------------------------------------------------------ frame
        void Update()
        {
            if (!_ready) return;
            float now = Time.time;
            var beat = _director.Beat;

            bool live = TableLive(beat);
            if (live != _live) SetLive(live, now);
            if (_nest != null && _live) PopNest(now);
            if (!_live) return;
            if (_delivery != null && DressingCritter.Head == null) DressingCritter.Head = _delivery.Head;

            if (_solitary != null && !_solitary.Busy && _nextSolitary > 0f && now >= _nextSolitary)
            {
                if (CritterBeat(beat) && beat != GameBeat.Escape)
                {
                    _solitary.Run(NestTrip(_solitary));
                    _nextSolitary = now + UnityEngine.Random.Range(solitaryGap.x, solitaryGap.y);
                }
                else _nextSolitary = now + 3f;
            }

            if (_bumble != null && _visitAt > 0f && now >= _visitAt)
            {
                _visitAt = -1f;
                if (VisitBeat(beat) && !_bumble.Busy)
                {
                    _bumble.Run(Visit(_bumble, _visitWhy));
                    _nextVisitOk = now + visitCooldown;
                }
            }

            if (_wasp != null && _waspAt > 0f && now >= _waspAt)
            {
                _waspAt = -1f;
                if ((beat == GameBeat.Feed || beat == GameBeat.Wasps) && _syrup != null && _syrup.gameObject.activeInHierarchy && !_wasp.Busy)
                {
                    _waspDone = true;
                    _wasp.Run(Scout(_wasp, _syrup));
                }
            }

            if (_seed != null && _seedAt > 0f && now >= _seedAt)
            {
                _seedAt = -1f;
                // the contract line: its card if one is out now, else the table; a landed card: that card
                Transform card = _seedOnContractLine ? FindContractCard(true) : _seedTarget;
                if (!_seedOnContractLine && (card == null || !card.gameObject.activeInHierarchy)) card = null;
                if (card != null || _seedOnContractLine) SendSeed(card);
                _seedOnContractLine = false;
                _seedTarget = null;
            }
        }

        void LateUpdate()
        {
            if (!_ready) return;
            if (_tufts != null && _live) _tufts.Draw(_layer);
            if (_seed != null && _seedSent && _seedCard != null) FollowCard();
        }

        /// <summary>The hive is on the table (not in pre, not hidden, not still flying in): the dressing shows.</summary>
        bool TableLive(GameBeat beat) =>
            beat != GameBeat.Pre && _hive != null && _hive.gameObject.activeInHierarchy && (_delivery == null || !_delivery.IsFlying(_hive.transform));

        void SetLive(bool live, float now)
        {
            _live = live;
            if (live)
            {
                _liveAt = now;
                if (!_footMeasured)
                {
                    _footMeasured = true;
                    _foot = HiveFootprint();
                    if (Tufts) Debug.Log(Tag + BuildTufts());
                }
                if (_nest != null) { _nest.localScale = Vector3.zero; _nest.gameObject.SetActive(true); }
                if (_nextSolitary < 0f) _nextSolitary = now + 4f;
                Debug.Log(Tag + "table live (" + _director.Beat + "): nest " + (_nest != null ? "in" : "-") + ", " + (_tufts != null ? _tufts.Count : 0) + " tufts");
            }
            else
            {
                if (_nest != null) _nest.gameObject.SetActive(false);
                if (_solitary != null) _solitary.Stop(true);
                if (_bumble != null) _bumble.Stop(true);
                if (_wasp != null) _wasp.Stop(true);
                _visitAt = _waspAt = -1f;
            }
        }

        void PopNest(float now)
        {
            // 0.6 s after the hive lands: 0 -> 1.15 -> 1 in 0.4 s
            float u = (now - _liveAt - 0.6f) / 0.4f;
            float k = u <= 0f ? 0f : u >= 1f ? 1f : u < 0.6f ? Mathf.SmoothStep(0f, 1.15f, u / 0.6f) : Mathf.Lerp(1.15f, 1f, (u - 0.6f) / 0.4f);
            if (Mathf.Abs(_nest.localScale.x - k) > 1e-4f) _nest.localScale = new Vector3(k, k, k);
        }

        // ------------------------------------------------------------------ plans
        Vector3 Door => _nestDoor != null ? _root.InverseTransformPoint(_nestDoor.position) : _nestTop;

        Vector3 WindowLocal(out Vector3 inward)
        {
            inward = Vector3.back;
            if (_delivery != null)
            {
                Vector3 p = _delivery.PanePosition(), n = _delivery.PaneNormal();
                inward = _root.InverseTransformDirection(n);
                return _root.InverseTransformPoint(p + n * 0.05f);
            }
            return new Vector3(-0.8f, 0.6f, 0.9f);
        }

        /// <summary>Out of the nest, a short loop, lands on the table, walks, shakes, home again (~10 s).</summary>
        IEnumerator NestTrip(DressingCritter c)
        {
            Vector3 door = Door, out1 = door + _nestOut * 0.03f;
            c.Show(door - _nestOut * 0.01f, Quaternion.LookRotation(_nestOut), 0.05f);
            c.Perch();
            Debug.Log(Tag + "solitary bee out (" + _director.Beat + ")");
            yield return c.ScaleTo(1f, 0.35f);
            yield return c.WalkBy(0.025f, 0.45f);
            c.Accent(DressingCritter.TwitchClip);
            yield return new WaitForSeconds(0.7f);
            // a loop out over the table, away from the hive
            Vector3 side = Vector3.Cross(Vector3.up, _nestOut).normalized;
            float s = UnityEngine.Random.value < 0.5f ? 1f : -1f;
            Vector3 p1 = out1 + _nestOut * 0.1f + Vector3.up * 0.09f + side * s * 0.05f;
            Vector3 p2 = out1 + _nestOut * 0.18f + Vector3.up * 0.05f - side * s * 0.08f;
            Vector3 land = out1 + _nestOut * (0.1f + UnityEngine.Random.value * 0.05f) - side * s * 0.05f;
            land.y = 0f;
            land = KeepOffHive(land);
            yield return c.FlyTo(p1, 0.22f, _nestOut + Vector3.up, side * s, 0.8f);
            yield return c.FlyTo(p2, 0.22f, side * s, -side * s, 0.9f);
            yield return c.FlyTo(land + Vector3.up * 0.04f, 0.2f, -side * s, Vector3.down + -_nestOut, 0.7f);
            yield return c.Land(land, Yaw(_nestOut) + UnityEngine.Random.Range(-60f, 60f));
            yield return new WaitForSeconds(0.5f);
            yield return c.WalkBy(0.03f, 0.9f);
            c.Accent(DressingCritter.ShakeClip);
            yield return new WaitForSeconds(1.1f);
            c.Accent(DressingCritter.BlinkClip);
            yield return new WaitForSeconds(0.8f);
            yield return GoHome(c, false);
        }

        IEnumerator GoHome(DressingCritter c, bool dash)
        {
            Vector3 door = Door, front = door + _nestOut * 0.05f;
            float speed = dash ? 0.6f : 0.22f;
            yield return c.FlyTo(front, speed, Vector3.up, -_nestOut, dash ? 0.3f : 0.8f);
            yield return c.FlyTo(door - _nestOut * 0.005f, speed * 0.5f, -_nestOut, -_nestOut, 0.25f);
            yield return c.ScaleTo(0.05f, dash ? 0.15f : 0.3f);
            c.Hide();
            if (!dash) Debug.Log(Tag + "solitary bee home");
        }

        /// <summary>In through the window, a lazy figure-8 over the table, 3 s on the nest or a tuft, out again.</summary>
        IEnumerator Visit(DressingCritter c, string why)
        {
            Vector3 win = WindowLocal(out var inward);
            Vector3 entry = eightCenter + new Vector3(-eightSize.x * 1.2f, 0.06f, 0.18f);
            c.Show(win, Quaternion.LookRotation(Flat(inward)), 0.05f);
            yield return c.ScaleTo(1f, 0.25f);
            bool onNest = _nest != null && UnityEngine.Random.value < 0.6f;
            Debug.Log(Tag + "bumblebee visits (" + why + ", lands on the " + (onNest ? "nest" : "table by a tuft") + ")");
            yield return c.FlyTo(entry, 0.45f, inward, Vector3.right + Vector3.back, 1.2f, 0.15f);
            yield return c.FigureEight(eightCenter, eightSize.x, eightSize.y, 5.5f);
            Vector3 spot = onNest ? _nestTop + Vector3.down * 0.004f : KeepOffHive(new Vector3(_foot.max.x + 0.06f, 0f, _foot.min.z + 0.02f));
            yield return c.FlyTo(spot + Vector3.up * 0.06f, 0.25f, Vector3.up, Vector3.down, 1f);
            yield return c.Land(spot, Yaw(new Vector3(0f, 0f, -0.7f) - spot) + UnityEngine.Random.Range(-35f, 35f));
            yield return new WaitForSeconds(0.7f);
            c.Accent(DressingCritter.TwitchClip);
            yield return new WaitForSeconds(0.9f);
            c.Accent(DressingCritter.ShakeClip);
            yield return new WaitForSeconds(1.4f);
            yield return FlyOff(c, 0.45f, "done");
        }

        /// <summary>Tiny wasp: in through the window, sniffs round the syrup's cap for ~3 s, zips out.</summary>
        IEnumerator Scout(DressingCritter c, Transform syrup)
        {
            Vector3 win = WindowLocal(out var inward);
            c.Show(win, Quaternion.LookRotation(Flat(inward)), 0.05f);
            Debug.Log(Tag + "tiny wasp scouts the syrup");
            yield return c.ScaleTo(1f, 0.2f);
            var b = LocalBounds(syrup, _root);
            Vector3 cap = new Vector3(b.center.x, b.max.y + 0.03f, b.center.z);
            yield return c.FlyTo(cap + new Vector3(0.06f, 0.02f, -0.02f), 0.9f, inward, Vector3.down, 1f, 0.15f);
            for (int i = 0; i < 3; i++)
            {
                float a = i * 2.1f;
                Vector3 p = cap + new Vector3(Mathf.Cos(a) * 0.05f, 0.01f * i, Mathf.Sin(a) * 0.05f);
                yield return c.FlyTo(p, 0.35f, Vector3.right, Vector3.forward, 0.6f);
                c.Accent(DressingCritter.TwitchClip);
            }
            yield return new WaitForSeconds(0.4f);
            yield return FlyOff(c, 1.1f, "scouted");
        }

        IEnumerator FlyOff(DressingCritter c, float speed, string why)
        {
            Vector3 win = WindowLocal(out var inward);
            Vector3 up = c.transform.localPosition + Vector3.up * 0.08f;
            yield return c.FlyTo(up, 0.3f, Vector3.up, Vector3.up, 0.35f);
            yield return c.FlyTo(win, speed, Vector3.up, -inward, 0.8f, 0.15f);
            yield return c.ScaleTo(0.05f, 0.2f);
            c.Hide();
            Debug.Log(Tag + c.Label + " left (" + why + ")");
        }

        Vector3 KeepOffHive(Vector3 p)
        {
            var f = _foot;
            f.Expand(new Vector3(0.05f, 0f, 0.05f));
            if (p.x > f.min.x && p.x < f.max.x && p.z > f.min.z && p.z < f.max.z) p.z = f.min.z - 0.02f;
            return p;
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }
        static float Yaw(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

        // ------------------------------------------------------------------ seed packet
        // The end card is the contract card (script v2: room_end_contract, a card whose name has Contract / EndCard) when
        // the scene has one, else the score card at the escape. No card on the contract line: the crew sets the packet
        // down on the table in front of the player instead.
        static bool IsContractName(string n) =>
            n.IndexOf("Contract", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("EndCard", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The contract card anywhere in the scene (inactive too), or null. Allocates: only on the rare events.</summary>
        Transform FindContractCard(bool activeOnly)
        {
            var scene = gameObject.scene;
            if (!scene.IsValid()) return null;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t == _root || t.IsChildOf(_root) || !IsContractName(t.name)) continue;
                    if (activeOnly && !t.gameObject.activeInHierarchy) continue;
                    if (t.GetComponentInChildren<Renderer>(true) != null) return t;
                }
            return null;
        }

        Transform FindScoreCard()
        {
            var t = _anchor != null ? _anchor.Find("ScoreCard") : null;
            return t != null && t.gameObject.activeInHierarchy ? t : null;
        }

        bool IsEndCard(Transform t)
        {
            if (t == null) return false;
            if (IsContractName(t.name)) return true;
            return t.name == "ScoreCard" && FindContractCard(false) == null;
        }

        /// <summary>A card's renderers bounds: the packet clips to its lower right corner, on its front (-Z, like its text).</summary>
        void SendSeed(Transform card)
        {
            if (_seedSent && card == _seedCard) return; // already on it
            if (card == null)
            {
                if (_seedSent) return;
                _seedSent = true;
                _seedCard = null;
                _seed.localScale = Vector3.one;
                if (_delivery != null) _delivery.DeliverHome(_seed, DeliveryStyle.Crown, 3);
                else _seed.gameObject.SetActive(true);
                Debug.Log(Tag + "seed packet -> the table (no contract card out), crew of 3");
                return;
            }
            // tucked under the card's lower right corner, hanging below it: the card's text stays clear
            var b = LocalBounds(card, card);
            float s = Mathf.Max(1e-3f, Mathf.Abs(card.lossyScale.y));
            _seedLocal = new Vector3(b.max.x - 0.05f / s, b.min.y - 0.08f / s, b.min.z - 0.01f / s);
            _seedRot = Quaternion.Euler(0f, 180f, -9f);
            _seedCard = card;
            _seedSent = true;
            _seedFollow = false;
            var to = new Pose(card.TransformPoint(_seedLocal), card.rotation * _seedRot);
            _seed.localScale = Vector3.one;
            if (_delivery != null) _delivery.Deliver(_seed, to, DeliveryStyle.Crown, 3, () => _seedFollow = true);
            else { _seed.gameObject.SetActive(true); _seed.SetPositionAndRotation(to.position, to.rotation); _seedFollow = true; }
            Debug.Log(Tag + "seed packet -> " + card.name + " (crew of 3)");
        }

        void FollowCard()
        {
            if (_seedCard == null || !_seedCard.gameObject.activeInHierarchy)
            {
                Debug.Log(Tag + "seed packet hidden (end card gone)");
                HideSeed();
                return;
            }
            if (!_seedFollow || (_delivery != null && _delivery.IsFlying(_seed))) return;
            _seed.SetPositionAndRotation(_seedCard.TransformPoint(_seedLocal), _seedCard.rotation * _seedRot);
        }

        void HideSeed()
        {
            _seedSent = _seedFollow = false;
            _seedCard = _seedTarget = null;
            if (_seed == null) return;
            if (_delivery != null) _delivery.Release(_seed, true);
            if (_seed.gameObject.activeSelf) _seed.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ util
        /// <summary>Renderer bounds of root's subtree in space's local frame (text excluded).</summary>
        static Bounds LocalBounds(Transform root, Transform space)
        {
            var b = new Bounds(space.InverseTransformPoint(root.position), Vector3.zero);
            bool any = false;
            Matrix4x4 to = space.worldToLocalMatrix;
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (r is ParticleSystemRenderer || r.GetComponent<TextMesh>() != null || r.name.StartsWith("[VBS]")) continue;
                var lb = r.localBounds;
                Matrix4x4 m = to * r.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 p = m.MultiplyPoint3x4(c);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return b;
        }
    }
}
