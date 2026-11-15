using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm
{
    /// <summary>tuning delivery.mode: on (crews fly everything in), swirl (items scale in inside a spinning ring at their spot), off (today's SetActive + buttons).</summary>
    public enum DeliveryMode { On = 0, Swirl = 1, Off = 2 }

    /// <summary>Crown: 3-12 bees in a ring (12 cm or less) above the item, which hangs 4 cm below. Festoon: the hive, 6 chains of 8 bees.</summary>
    public enum DeliveryStyle { Crown = 0, Festoon = 1 }

    /// <summary>
    /// "Everything is delivered" (PLAN v3.1, V12). One pool of 96 bees in ONE instanced draw (no per-bee GameObjects,
    /// no physics, no per-frame allocations) carries items in from the meadow window, parks station choices in an arc,
    /// sets things down on the table (contact shadow, squash, dust, optional thunk), hauls wrong picks back out with a
    /// red repo crew, returns unpicked choices, and cuts the window ring.
    ///
    /// Rules:
    /// - A delivery never holds a beat. Every flight snaps to its end state on a beat change (BeatChanged, flights
    ///   started in that same frame are kept), on FinishAll(), and is cancelled without callbacks on a group reset.
    /// - Every command works from wherever the item is now (pane, parked, mid-flight), so commands can follow each
    ///   other at any time. onLanded runs exactly once per Deliver, also when the flight is snapped.
    /// - Mode Off: Deliver/Park snap the item into place (active), HaulAway/Return hide it, CutRing calls back at once.
    /// - Own clock: Step(dt) drives everything (Update calls it with Time.deltaTime), so the editor test steps it
    ///   without play mode.
    /// </summary>
    [DefaultExecutionOrder(-20)]
    public class BeeDelivery : MonoBehaviour
    {
        public const int MaxBees = 288; // meadow: crew.scale (tuning.json) makes crews up to 3x bigger
        public const int MaxCrew = 144;
        /// <summary>Meadow: crews x crew.scale; slots spread with the rig (the props are ~3.5x bigger on the floor).</summary>
        static float CrewK => Bloom.MeadowLayout.Active ? Bloom.MeadowTuning.CrewScale : 1f; // only with the big rig on the floor
        static float SlotK => Bloom.MeadowLayout.Active ? Mathf.Max(1f, Bloom.MeadowLayout.Scale * 0.6f) : 1f;
        static int Crewed(int n) => n == 1 ? 1 : Mathf.Clamp(Mathf.RoundToInt(n * CrewK), 1, MaxCrew);
        const int MaxJobs = 32;
        const int TrailPoints = 49;

        public static BeeDelivery Instance { get; private set; }

        static DeliveryMode _mode = DeliveryMode.On;
        static bool _modeSet;
        /// <summary>
        /// delivery.mode. First read: command line -deliveryMode, else env VBS_DELIVERY_MODE, else tuning.json
        /// (persistentDataPath, then StreamingAssets outside Android) "delivery.mode" or {"delivery":{"mode":..}}, else On.
        /// Desktop: Shift+M cycles on -> swirl -> off (stations switch at their next start).
        /// </summary>
        public static DeliveryMode Mode
        {
            get { if (!_modeSet) { _modeSet = true; _mode = ModeFromEnvironment(ModeFromTuningFiles(DeliveryMode.On)); Debug.Log("[VBS][Delivery] mode = " + ModeName(_mode)); } return _mode; }
            set { _modeSet = true; if (_mode != value) Debug.Log("[VBS][Delivery] mode = " + ModeName(value)); _mode = value; }
        }

        /// <summary>
        /// Optional: the AR layer's landing beat replaces the built-in one (squash, dust, thunk), e.g.
        /// BeeDelivery.TouchdownHook = VirtualBeeSwarm.AR.Touchdown.Play; (weight in kg-ish, 0..1).
        /// </summary>
        public static Action<Transform, float> TouchdownHook;
        /// <summary>
        /// Optional: the AR layer's ContactShadows draws the shadows instead (one system for everything). Called once
        /// per item BeeDelivery sees, e.g. BeeDelivery.ShadowHook = t => VirtualBeeSwarm.AR.ContactShadows.Instance?.Register(t);
        /// While set, the built-in blob shadows stay off.
        /// Without either hook, BeeDelivery uses the AR layer by itself as soon as ArRig's ContactShadows exists
        /// (useArLayer), and its own blob shadows + touchdown when it doesn't (no AR in the scene, edit-mode tests).
        /// </summary>
        public static Action<Transform> ShadowHook;

        [Header("Scene")]
        [SerializeField] FutureWindow window;
        [Tooltip("Optional: start/end point of every flight instead of FutureWindow.PaneTransform (tests, a wall window).")]
        [SerializeField] Transform paneOverride;
        [Tooltip("Player's head (empty = main camera). Used for the arc and to face the pane normal into the room.")]
        [SerializeField] Transform head;
        [Tooltip("TableAnchor: table-top height for shadows/dust, and world targets are kept relative to it.")]
        [SerializeField] Transform tableAnchor;
        [Tooltip("Optional: the hive swarm (read by SwarmSystem: orbit pulls in while a delivery flies).")]
        [SerializeField] SwarmSystem hiveSwarm;

        [Header("Bees")]
        [SerializeField] Mesh beeMesh;
        [Tooltip("Instanced, unlit, vertex colors (VBS_Bee). Per-instance _Color.")]
        [SerializeField] Material beeMaterial;
        [SerializeField] float beeLength = 0.035f;
        [SerializeField] Color courierColor = new Color(1f, 0.68f, 0.12f, 1f);
        [SerializeField] Color repoColor = new Color(0.95f, 0.13f, 0.08f, 1f);
        [SerializeField] float brightness = 1.25f;

        [Header("Flights")]
        [SerializeField] float crownSpeed = 1.2f;
        [SerializeField] float crownMinSeconds = 1.2f;
        [SerializeField] float crownMaxSeconds = 1.5f;
        [Tooltip("Heavy items (DeliveryItem.Heavy): m/s and max seconds.")]
        [SerializeField] float heavySpeed = 0.6f;
        [SerializeField] float heavyMaxSeconds = 3.2f;
        [SerializeField] float festoonSeconds = 4.5f;
        [SerializeField] float relocateMinSeconds = 0.8f;
        [SerializeField] float approachSeconds = 0.7f;
        [SerializeField] float departSeconds = 1.1f;
        [SerializeField] float swirlSeconds = 0.5f;
        [SerializeField] float ringSeconds = 2f;
        [SerializeField] int ringBees = 20;
        [SerializeField] float exitScale = 0.9f;
        [SerializeField] float maxTiltDegrees = 20f;
        [SerializeField] float heavyDip = 0.07f;
        [SerializeField] float repoDrag = 0.15f;
        [SerializeField] float repoHeavySpeedScale = 0.8f;

        [Header("Touchdown")]
        [SerializeField] float shadowMaxAlpha = 0.45f;
        [SerializeField] float shadowFadeHeight = 0.4f;
        [SerializeField] Vector3 squash = new Vector3(1.06f, 0.92f, 1.06f);
        [SerializeField] float squashSeconds = 0.07f;
        [SerializeField] int dustParticles = 8;
        [Tooltip("When ArRig's ContactShadows is in the scene, use its contact shadows and Touchdown.Play (one shadow system for everything; ar.shadows switches them).")]
        [SerializeField] bool useArLayer = true;
        [Tooltip("Optional sounds (none by default): thunk on touchdown (pitch drops with weight), ka-chunk on a stamp.")]
        [SerializeField] AudioClip thunkClip;
        [SerializeField] AudioClip stampClip;
        [SerializeField] bool logEvents = true;
        [Tooltip("Desktop: Shift + this key cycles delivery.mode on -> swirl -> off.")]
        [SerializeField] KeyCode cycleModeKey = KeyCode.M;

        // ------------------------------------------------------------ public state
        /// <summary>Flights in the air (not parked/holding). SwarmSystem pulls its orbit in while > 0.</summary>
        public int InFlight { get; private set; }
        /// <summary>Bees in use right now (drawn every frame).</summary>
        public int ActiveBees
        {
            get
            {
                if (_bJob == null) return 0;
                int n = 0;
                for (int b = 0; b < MaxBees; b++) if (_bJob[b] >= 0) n++;
                return n;
            }
        }
        public float Now => _now;
        public event Action<Transform> Landed;

        // ------------------------------------------------------------ pool
        enum Phase { Free, Approach, Inbound, Hold, Outbound, Drag, Depart, SwirlIn, SwirlOut, Ring }

        sealed class Job
        {
            public Phase phase;
            public DeliveryItem item;
            public DeliveryStyle style;
            public bool repo, land, heavy, swirlHold;
            public int crew;
            public readonly int[] bees = new int[MaxCrew];
            public float start, duration, speedScale = 1f;
            public Vector3 p0, p1;               // path start + first control (p2/p3 follow the target)
            public Bounds? avoidHive;
            public bool parkArrival;
            public Transform targetParent;
            public Vector3 targetPos;            // local to targetParent, or world
            public Quaternion targetRot;
            public Quaternion startRot;
            public Vector3 startScale;
            public Action onDone;
            public Vector3 tilt, tiltVel, prevPos, prevVel;
            public int prevValid;
            public int createdFrame;
            public Vector3 ringCenter, ringRight, ringUp;
            public float ringRadius;
            public Phase after;                  // Approach -> Drag/Outbound
            public Vector3 dragTo;
        }

        readonly Job[] _jobs = new Job[MaxJobs];
        Vector3[] _bPos, _bFrom, _bVel;
        float[] _bYaw;
        Vector4[] _bColor;
        int[] _bJob, _bSlot;
        float[] _bPhase, _bDelay;
        Matrix4x4[] _matrices;
        Vector4[] _colors;
        MaterialPropertyBlock _mpb;
        Bloom.BeeLod _lod; // meadow
        static readonly int ColorId = Shader.PropertyToID("_Color");
        bool _instancing, _ready;
        float _now;
        GameDirector _director;

        // visuals
        Transform _fxRoot;
        LineRenderer _trail;
        readonly Vector3[] _trailPts = new Vector3[TrailPoints];
        float _trailFadeStart = -1f;
        ParticleSystem _dust;
        Material _shadowMat, _dotMat;
        Texture2D _dot;
        Font _font;
        AudioSource _audio;
        readonly DeliveryItem[] _items = new DeliveryItem[64];
        int _itemCount;
        bool _warnedPool;

        // ------------------------------------------------------------ lifecycle
        void Awake()
        {
            if (Instance != null && Instance != this) Debug.LogWarning("[VBS][Delivery] a second BeeDelivery exists; Instance = the newest.");
            Instance = this;
            Init();
        }

        void Start()
        {
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (_director != null)
            {
                _director.BeatChanged += HandleBeatChanged;
                _director.GroupResetDone += HandleGroupReset;
            }
        }

        void OnDestroy()
        {
            if (_director != null)
            {
                _director.BeatChanged -= HandleBeatChanged;
                _director.GroupResetDone -= HandleGroupReset;
            }
            if (Instance == this) Instance = null;
        }

        void HandleBeatChanged(GameBeat prev, GameBeat beat) => FinishAll(Time.frameCount);
        void HandleGroupReset() => CancelAll();

        /// <summary>Allocates the pool and the visuals. Safe to call again (edit-mode tests call it directly).</summary>
        public void Init()
        {
            if (_ready) return;
            _ready = true;
            if (Instance == null) Instance = this;
            if (window == null) window = FindAnyObjectByType<FutureWindow>();
            if (hiveSwarm == null) hiveSwarm = FindAnyObjectByType<SwarmSystem>();
            beeMesh = BeeVat.AuthoredWorkerMesh(beeMesh) ?? beeMesh ?? GameMeshes.CreateBeeMesh();
            if (beeMaterial == null) beeMaterial = GameMaterials.Create("VBS_DeliveryBee (runtime)", Color.white, vertexColors: true, doubleSided: true);
            beeMaterial.enableInstancing = true;
            WingClock.EnableOn(beeMaterial, beeMesh); // wings flap in the shader (Shaders/GameUnlit.shader)
            _lod = Bloom.BeeLod.Make(beeMesh, beeMaterial); // meadow: far bees low-poly (Quest budget)
            BeeVat.Apply(ref beeMesh, ref beeMaterial, "delivery"); // swarm.vat: Daniel's animated bee (BeeVAT/), replaces the flap above
            _instancing = SystemInfo.supportsInstancing;

            _bPos = new Vector3[MaxBees];
            _bFrom = new Vector3[MaxBees];
            _bVel = new Vector3[MaxBees];
            _bYaw = new float[MaxBees];
            _bColor = new Vector4[MaxBees];
            _bJob = new int[MaxBees];
            _bSlot = new int[MaxBees];
            _bPhase = new float[MaxBees];
            _bDelay = new float[MaxBees];
            _matrices = new Matrix4x4[MaxBees];
            _colors = new Vector4[MaxBees];
            _mpb = new MaterialPropertyBlock();
            var rnd = new System.Random(4321);
            for (int i = 0; i < MaxBees; i++)
            {
                _bJob[i] = -1;
                _bPhase[i] = (float)rnd.NextDouble() * 6.2832f;
            }
            for (int j = 0; j < MaxJobs; j++) _jobs[j] = new Job();

            _fxRoot = new GameObject("[VBS] DeliveryFx").transform;
            _fxRoot.SetParent(transform, false);

            _dot = GameMeshes.CreateDotTexture();
            _shadowMat = GameMaterials.Create("VBS_DeliveryShadow (runtime)", new Color(0f, 0f, 0f, 0.45f), GameMaterials.Blend.Alpha);
            _shadowMat.mainTexture = _dot;
            _dotMat = GameMaterials.Create("VBS_DeliveryDust (runtime)", Color.white, GameMaterials.Blend.Alpha, vertexColors: true);
            _dotMat.mainTexture = _dot;
            var trailMat = GameMaterials.Create("VBS_DeliveryTrail (runtime)", new Color(1f, 0.8f, 0.35f, 0.9f), GameMaterials.Blend.Additive, doubleSided: true);

            var trailGo = new GameObject("[VBS] RingTrail");
            trailGo.transform.SetParent(_fxRoot, false);
            _trail = trailGo.AddComponent<LineRenderer>();
            _trail.useWorldSpace = true;
            _trail.positionCount = 0;
            _trail.widthMultiplier = 0.012f;
            _trail.sharedMaterial = trailMat;
            _trail.shadowCastingMode = ShadowCastingMode.Off;
            _trail.receiveShadows = false;
            _trail.enabled = false;

            var dustGo = new GameObject("[VBS] Dust");
            dustGo.transform.SetParent(_fxRoot, false);
            dustGo.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            _dust = dustGo.AddComponent<ParticleSystem>();
            _dust.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            EmbersFx.ConfigurePuff(_dust, _dotMat, new Color(0.7f, 0.64f, 0.56f, 0.8f), 0.25f, 0.03f, 0.9f, 0.08f);
            var dustMain = _dust.main;
            dustMain.maxParticles = QuestPerf.Particles(72);

            _audio = gameObject.GetComponent<AudioSource>();
            if (_audio == null) _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0.8f;
        }

        void Update()
        {
            if (cycleModeKey != KeyCode.None && GameInput.Shift && GameInput.KeyDown(cycleModeKey))
                Mode = (DeliveryMode)(((int)Mode + 1) % 3);
            Step(Time.deltaTime);
        }

        // ------------------------------------------------------------ geometry helpers
        public Transform Head
        {
            get
            {
                if (head != null) return head;
                var cam = Camera.main;
                return cam != null ? cam.transform : null;
            }
        }

        public Transform TableAnchor => tableAnchor;

        /// <summary>Wire from the builder or a test.</summary>
        public void Configure(FutureWindow win, Transform pane, Transform headT, Transform table, SwarmSystem swarm)
        {
            window = win;
            paneOverride = pane;
            head = headT;
            tableAnchor = table;
            hiveSwarm = swarm;
        }

        public Vector3 PanePosition()
        {
            if (paneOverride != null) return paneOverride.position;
            if (window != null) return window.PaneTransform.position;
            return tableAnchor != null ? tableAnchor.position + new Vector3(-0.5f, 0.45f, 0.3f) : transform.position;
        }

        /// <summary>Out of the window, into the room (toward the head).</summary>
        public Vector3 PaneNormal()
        {
            Transform t = paneOverride != null ? paneOverride : window != null ? window.PaneTransform : null;
            Vector3 n = t != null ? -t.forward : Vector3.back;
            Transform h = Head;
            if (h != null && Vector3.Dot(n, h.position - PanePosition()) < 0f) n = -n;
            return n;
        }

        float TableY(Job j)
        {
            if (tableAnchor != null) return tableAnchor.position.y;
            return TargetPos(j).y;
        }

        float TableY() => tableAnchor != null ? tableAnchor.position.y : 0f;

        Vector3 TargetPos(Job j) => j.targetParent != null ? j.targetParent.TransformPoint(j.targetPos) : j.targetPos;
        Quaternion TargetRot(Job j) => j.targetParent != null ? j.targetParent.rotation * j.targetRot : j.targetRot;

        void SetTarget(Job j, Transform parent, Vector3 worldPos, Quaternion worldRot)
        {
            if (parent == null) parent = tableAnchor;
            j.targetParent = parent;
            if (parent != null)
            {
                j.targetPos = parent.InverseTransformPoint(worldPos);
                j.targetRot = Quaternion.Inverse(parent.rotation) * worldRot;
            }
            else
            {
                j.targetPos = worldPos;
                j.targetRot = worldRot;
            }
        }

        // ------------------------------------------------------------ registration
        public DeliveryItem Register(Transform t)
        {
            if (t == null) return null;
            Init();
            var item = t.GetComponent<DeliveryItem>();
            if (item == null) item = t.gameObject.AddComponent<DeliveryItem>();
            if (!item.measured) item.Measure();
            if (!item.hasHome) item.RecordHome();
            bool known = false;
            for (int i = 0; i < _itemCount; i++) if (_items[i] == item) { known = true; break; }
            if (!known && _itemCount < _items.Length)
            {
                _items[_itemCount++] = item;
                if (ShadowHook != null && item.castShadow) ShadowHook(t);
                else if (item.castShadow && ArActive) ArShadow(t);
            }
            if (item.shadow == null && item.castShadow && ShadowHook == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "[VBS] Shadow " + t.name;
                DestroyCollider(go);
                go.transform.SetParent(_fxRoot, false);
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = _shadowMat;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.enabled = false;
                item.shadow = r;
            }
            return item;
        }

        /// <summary>The item's rest scale changed (normalized / restored by StationDelivery): re-measure.</summary>
        public void Remeasure(Transform t)
        {
            var item = Register(t);
            if (item != null && item.job < 0) item.Measure();
        }

        public static void DestroyCollider(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (c == null) return;
            DestroyImmediate(c);
        }

        // ------------------------------------------------------------ API
        /// <summary>Fly the item in (from the pane, or from wherever it is now) and set it down at `to` (world pose; kept relative to TableAnchor).</summary>
        public void Deliver(Transform item, Pose to, DeliveryStyle style = DeliveryStyle.Crown, int crew = 0, Action onLanded = null)
            => Command(item, null, to.position, to.rotation, style, crew, true, onLanded);

        /// <summary>Deliver onto a spot transform (followed while flying).</summary>
        public void Deliver(Transform item, Transform spot, DeliveryStyle style = DeliveryStyle.Crown, int crew = 0, Action onLanded = null,
            Bounds? avoidHive = null)
        {
            if (spot == null) { Deliver(item, new Pose(item.position, item.rotation), style, crew, onLanded); return; }
            Command(item, spot, spot.position, spot.rotation, style, crew, true, onLanded, avoidHive);
        }

        /// <summary>
        /// Deliver the item to its home: the pose it had under its parent the first time BeeDelivery saw it (the hive,
        /// the rocket, the score card are built in place by the scene builder). Register() them early (e.g. in pre)
        /// so a later flight can't be taken for home.
        /// </summary>
        public void DeliverHome(Transform item, DeliveryStyle style = DeliveryStyle.Crown, int crew = 0, Action onLanded = null)
        {
            var di = Register(item);
            if (di == null) return;
            Command(item, di.homeParent, di.HomePosition, di.HomeRotation, style, crew, true, onLanded);
        }

        /// <summary>
        /// Make sure the item is home and delivered (a Board jump past the beat that delivers it): flies nothing, snaps,
        /// and runs onLanded once if it wasn't delivered yet. A flight in progress is snapped instead.
        /// </summary>
        public void EnsureHome(Transform item, DeliveryStyle style = DeliveryStyle.Crown, int crew = 0, Action onLanded = null)
        {
            var di = Register(item);
            if (di == null) return;
            if (di.job >= 0) { Finish(item); return; }
            if (di.delivered && item.gameObject.activeSelf) return;
            DeliverHome(item, style, crew, onLanded);
            Finish(item);
        }

        /// <summary>Put the item back at its home pose at once (no flight, no callback); hide = SetActive(false).</summary>
        public void SnapHome(Transform item, bool hide)
        {
            var di = Register(item);
            if (di == null) return;
            Release(item, hide);
            item.SetPositionAndRotation(di.HomePosition, di.HomeRotation);
            item.localScale = di.restScale;
        }

        /// <summary>Fly in and hover, perfectly still, at `slot` (world pose). The crew hovers above it until the next command.</summary>
        public void Park(Transform item, Pose slot, int crew = 0)
            => Command(item, null, slot.position, slot.rotation, DeliveryStyle.Crown, crew, false, null);

        /// <summary>Carry a station prop around the hive to its handoff and give it a small arrival cue.</summary>
        public void ParkStationChoice(Transform item, Pose slot, Bounds hive, bool cueArrival = true)
            => Command(item, null, slot.position, slot.rotation, DeliveryStyle.Crown, 0, false, null, hive, cueArrival);

        /// <summary>A red repo crew flies in and hauls it out through the window. Heavy: 8 bees, drags 15 cm first, leaves at 0.8x.</summary>
        public void HaulAway(Transform item, bool repo = true) => TakeOut(item, repo);

        /// <summary>The crew that holds it (or a fresh courier crew) flies it back out through the window.</summary>
        public void Return(Transform item) => TakeOut(item, false);

        /// <summary>20 bees trace the window ring with a glowing trail over 2 s; onCut runs at the end (or at once when Off / snapped).</summary>
        public void CutRing(Pose windowPose, float radius, Action onCut = null)
        {
            Init();
            if (Mode == DeliveryMode.Off) { onCut?.Invoke(); return; }
            int ji = AllocJob();
            if (ji < 0) { onCut?.Invoke(); return; }
            var j = _jobs[ji];
            j.phase = Phase.Ring;
            j.item = null;
            j.repo = false;
            j.start = _now;
            j.duration = ringSeconds;
            j.onDone = onCut;
            j.ringCenter = windowPose.position;
            j.ringRight = windowPose.rotation * Vector3.right;
            j.ringUp = windowPose.rotation * Vector3.up;
            j.ringRadius = Mathf.Max(0.05f, radius);
            j.crew = AllocBees(ji, Crewed(ringBees), windowPose.position); // meadow: crew.scale
            for (int k = 0; k < j.crew; k++) _bDelay[j.bees[k]] = k * 0.02f;
            _trail.positionCount = 0;
            _trail.enabled = true;
            _trailFadeStart = -1f;
            Log("ring cut r=" + radius.ToString("0.00"));
            GameSound.Cue("window_cut", windowPose.position);
        }

        /// <summary>CutRing around the window pane (radius = half the pane's larger side, else 0.3 m).</summary>
        public void CutRing(Action onCut = null)
        {
            Transform t = paneOverride != null ? paneOverride : window != null ? window.PaneTransform : null;
            float r = 0.3f;
            if (t != null) r = 0.5f * Mathf.Max(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.y)) + 0.03f;
            if (r < 0.1f) r = 0.3f; // portal closed (scaled to 0): use the default
            Quaternion rot = Quaternion.LookRotation(-PaneNormal(), Vector3.up);
            CutRing(new Pose(PanePosition(), rot), r, onCut);
        }

        /// <summary>Is the item being moved by a crew right now (not parked/holding)?</summary>
        public bool IsFlying(Transform item)
        {
            var di = item != null ? item.GetComponent<DeliveryItem>() : null;
            if (di == null || di.job < 0) return false;
            var p = _jobs[di.job].phase;
            return p != Phase.Hold && p != Phase.Free;
        }

        /// <summary>Has a crew (flying or holding)?</summary>
        public bool HasCrew(Transform item)
        {
            var di = item != null ? item.GetComponent<DeliveryItem>() : null;
            return di != null && di.job >= 0;
        }

        /// <summary>Every flight jumps to its end state now (Board jump, beat exit). Parked items stay parked.</summary>
        public void FinishAll() => FinishAll(int.MaxValue);

        void FinishAll(int keepCreatedFrame)
        {
            for (int ji = 0; ji < MaxJobs; ji++)
            {
                var j = _jobs[ji];
                if (j.phase == Phase.Free || j.createdFrame == keepCreatedFrame) continue;
                Snap(ji, invokeCallbacks: true);
            }
            RecountInFlight();
        }

        /// <summary>Snap one item's flight to its end state.</summary>
        public void Finish(Transform item)
        {
            var di = item != null ? item.GetComponent<DeliveryItem>() : null;
            if (di != null && di.job >= 0) Snap(di.job, invokeCallbacks: true);
            RecountInFlight();
        }

        /// <summary>Give a prop set down by hand its contact effects. Station completion stays with the station.</summary>
        public void PlayManualTouchdown(Transform item)
        {
            var di = Register(item);
            if (di == null || di.delivered) return;
            di.delivered = true;
            di.parked = false;
            Touchdown(di);
        }

        /// <summary>Notify listeners that a tool is seated without creating a second crew flight.</summary>
        public void FinishManualPlacement(Transform item, Pose pose)
        {
            var data = Register(item);
            if (data == null) return;
            if (data.job >= 0) Release(item, hide: false);
            item.SetPositionAndRotation(pose.position, pose.rotation);
            PlayManualTouchdown(item); // the dish already made contact before the pump step
            Landed?.Invoke(item);
        }

        /// <summary>A carried tool touches its cradle without leaving the parked delivery state.</summary>
        public void PlayDockTouchdown(Transform item)
        {
            if (item == null || _dust == null || Mode == DeliveryMode.Off) return;
            AR.Touchdown.EmitCloud(_dust, item, 0.7f, dustParticles);
            GameSound.Cue("touchdown_light", item);
        }

        /// <summary>Drop the item from the system: flights snap (no callback), its crew leaves at once. hide: SetActive(false).</summary>
        public void Release(Transform item, bool hide)
        {
            var di = item != null ? item.GetComponent<DeliveryItem>() : null;
            if (di == null) return;
            if (di.job >= 0)
            {
                int ji = di.job;
                Snap(ji, invokeCallbacks: false);
                if (di.job >= 0) FreeJob(di.job);
            }
            ClearStamp(di);
            item.localScale = di.restScale;
            di.squashUntil = -1f;
            if (hide)
            {
                di.delivered = false;
                di.parked = false;
                if (item.gameObject.activeSelf) item.gameObject.SetActive(false);
            }
            RecountInFlight();
        }

        /// <summary>Group reset: everything stops, no callbacks, no bees left. Items keep their visibility (owners reset them).</summary>
        public void CancelAll()
        {
            for (int ji = 0; ji < MaxJobs; ji++)
                if (_jobs[ji].phase != Phase.Free) Snap(ji, invokeCallbacks: false);
            for (int ji = 0; ji < MaxJobs; ji++)
                if (_jobs[ji].phase != Phase.Free) FreeJob(ji);
            for (int i = 0; i < _itemCount; i++)
            {
                var it = _items[i];
                if (it == null) continue;
                ClearStamp(it);
                it.squashUntil = -1f;
                it.transform.localScale = it.restScale;
                it.delivered = false;
                it.parked = false;
            }
            if (_trail != null) { _trail.enabled = false; _trail.positionCount = 0; }
            if (_dust != null) _dust.Clear();
            AR.Touchdown.ClearDust();
            RecountInFlight();
        }

        // ------------------------------------------------------------ stamp
        /// <summary>APPROVED (green) or GRIEVANCE (red) on the item, facing the player: scale 0 -> 1.2 -> 1 in 120 ms (+ optional ka-chunk).</summary>
        public void Stamp(Transform item, bool approved)
        {
            var di = Register(item);
            if (di == null) return;
            if (di.stamp == null) BuildStamp(di);
            di.stampText.text = approved ? "APPROVED" : "GRIEVANCE";
            di.stampText.color = approved ? new Color(0.1f, 0.55f, 0.15f) : new Color(0.75f, 0.08f, 0.05f);
            GameMaterials.Tint(di.stampBacking, approved ? new Color(0.85f, 1f, 0.85f, 0.95f) : new Color(1f, 0.88f, 0.85f, 0.95f));
            PlaceStamp(di, approved);
            di.stamp.gameObject.SetActive(true);
            di.stamp.localScale = Vector3.zero;
            di.stampAt = _now;
            if (stampClip != null && _audio != null) _audio.PlayOneShot(stampClip, 0.8f);
            Log((approved ? "APPROVED " : "GRIEVANCE ") + item.name);
            GameSound.Cue(approved ? "stamp_approved" : "stamp_grievance", item);
        }

        public void ClearStamp(Transform item)
        {
            var di = item != null ? item.GetComponent<DeliveryItem>() : null;
            if (di != null) ClearStamp(di);
        }

        void ClearStamp(DeliveryItem di)
        {
            di.stampAt = -1f;
            if (di.stamp != null && di.stamp.gameObject.activeSelf) di.stamp.gameObject.SetActive(false);
        }

        void BuildStamp(DeliveryItem di)
        {
            if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var root = new GameObject("[VBS] Stamp").transform;
            root.SetParent(di.transform, false);
            var back = GameObject.CreatePrimitive(PrimitiveType.Quad);
            back.name = "[VBS] StampBacking";
            DestroyCollider(back);
            back.transform.SetParent(root, false);
            back.transform.localPosition = new Vector3(0f, 0f, 0.001f);
            back.transform.localScale = new Vector3(0.11f, 0.03f, 1f);
            var br = back.GetComponent<MeshRenderer>();
            br.sharedMaterial = GameMaterials.Create("VBS_Stamp (runtime)", Color.white, GameMaterials.Blend.Alpha, doubleSided: true);
            br.shadowCastingMode = ShadowCastingMode.Off;
            var textGo = new GameObject("[VBS] StampText");
            textGo.transform.SetParent(root, false);
            var tm = textGo.AddComponent<TextMesh>();
            tm.font = _font;
            tm.fontSize = 64;
            tm.characterSize = 0.0042f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.fontStyle = FontStyle.Bold;
            var tr = textGo.GetComponent<MeshRenderer>();
            if (_font != null) tr.sharedMaterial = _font.material;
            tr.shadowCastingMode = ShadowCastingMode.Off;
            di.stamp = root;
            di.stampText = tm;
            di.stampBacking = br;
            root.gameObject.SetActive(false);
        }

        void PlaceStamp(DeliveryItem di, bool approved)
        {
            // In front of the item, at mid height, facing the head; world size fixed whatever the item's scale.
            Transform t = di.transform;
            Transform h = Head;
            Vector3 c = t.TransformPoint(di.localBounds.center);
            Vector3 toHead = h != null ? h.position - c : -t.forward;
            toHead.y = 0f;
            if (toHead.sqrMagnitude < 1e-6f) toHead = -t.forward;
            toHead.Normalize();
            Vector3 world = c + toHead * (di.Radius + 0.012f);
            di.stamp.position = world;
            di.stamp.rotation = Quaternion.LookRotation(-toHead, Vector3.up) * Quaternion.Euler(0f, 0f, approved ? -8f : 8f);
        }

        // ------------------------------------------------------------ commands
        void Command(Transform t, Transform targetParent, Vector3 worldPos, Quaternion worldRot, DeliveryStyle style, int crew,
            bool land, Action onLanded, Bounds? avoidHive = null, bool parkArrival = false)
        {
            if (t == null) return;
            Init();
            var item = Register(t);
            if (Mode == DeliveryMode.Off)
            {
                if (item.job >= 0) { Snap(item.job, false); if (item.job >= 0) FreeJob(item.job); }
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                t.SetPositionAndRotation(worldPos, worldRot);
                t.localScale = item.restScale;
                item.delivered = land;
                item.parked = !land;
                if (land) { Landed?.Invoke(t); onLanded?.Invoke(); }
                RecountInFlight();
                return;
            }

            int want = style == DeliveryStyle.Festoon ? Mathf.Min(MaxCrew, Mathf.Max(48, Mathf.RoundToInt(48 * CrewK / 8f) * 8)) : Crewed(crew > 0 ? crew : item.Crew); // meadow: crew.scale
            bool wasActive = t.gameObject.activeInHierarchy && (item.job >= 0 || item.delivered || item.parked);
            int ji = item.job;
            Job j;
            if (ji >= 0)
            {
                j = _jobs[ji];
                // previous flight: finish its callback first (a pending onLanded must still run once)
                if (j.phase == Phase.Inbound && j.land && j.onDone != null) { var cb = j.onDone; j.onDone = null; cb(); }
                if (j.repo || j.style != style || Mode == DeliveryMode.Swirl)
                    DetachCrewToDepart(ji); // a different crew takes over: the old one leaves, the job stays
            }
            else
            {
                j = NewJobFor(item, out ji);
                if (j == null) { FallbackSnap(item, worldPos, worldRot, land, onLanded); return; }
            }

            j.item = item;
            j.style = style;
            j.repo = false;
            j.land = land;
            j.heavy = item.Heavy;
            j.onDone = onLanded;
            j.avoidHive = avoidHive;
            j.parkArrival = parkArrival;
            j.start = _now;
            j.createdFrame = Time.frameCount;
            SetTarget(j, targetParent, worldPos, worldRot);
            j.tilt = j.tiltVel = Vector3.zero;
            j.prevValid = 0;
            item.delivered = false;
            item.parked = false;
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);

            if (Mode == DeliveryMode.Swirl)
            {
                if (j.crew == 0) j.crew = AllocBees(ji, want == 1 ? 1 : Mathf.Clamp(want, 6, 12), worldPos);
                j.phase = Phase.SwirlIn;
                j.duration = swirlSeconds;
                t.SetPositionAndRotation(worldPos, worldRot);
                t.localScale = Vector3.zero;
                Log((land ? "swirl-deliver " : "swirl-park ") + t.name);
                GameSound.Cue("swirl_in", t);
                RecountInFlight();
                return;
            }

            Vector3 from;
            Vector3 pane = PanePosition(), n = PaneNormal();
            if (wasActive)
            {
                from = t.position;
                j.p0 = from;
                j.p1 = from + Vector3.up * 0.12f;
                j.startRot = t.rotation;
                j.startScale = t.localScale;
            }
            else
            {
                from = pane;
                j.p0 = pane;
                j.p1 = pane + n * 0.4f + Vector3.up * 0.2f;
                j.startRot = worldRot;
                j.startScale = item.restScale * exitScale;
                t.SetPositionAndRotation(pane, worldRot);
                t.localScale = j.startScale;
            }
            item.squashUntil = -1f;
            if (j.crew == 0) j.crew = AllocBees(ji, want, from + Vector3.up * (item.Top + 0.04f));
            j.phase = Phase.Inbound;
            float dist = Vector3.Distance(from, worldPos);
            if (style == DeliveryStyle.Festoon) j.duration = festoonSeconds;
            else if (wasActive) j.duration = Mathf.Clamp(dist / crownSpeed, relocateMinSeconds, crownMaxSeconds);
            else if (j.heavy) j.duration = Mathf.Clamp(dist / heavySpeed, crownMinSeconds, heavyMaxSeconds);
            else j.duration = Mathf.Clamp(dist / crownSpeed, crownMinSeconds, crownMaxSeconds);
            Log((land ? "deliver " : "park ") + t.name + " " + StyleName(style) + " crew " + j.crew + " " + j.duration.ToString("0.00") + " s" + (wasActive ? " (from here)" : ""));
            GameSound.Cue(style == DeliveryStyle.Festoon ? "hive_festoon" : "crew_depart", t);
            RecountInFlight();
        }

        void FallbackSnap(DeliveryItem item, Vector3 pos, Quaternion rot, bool land, Action onLanded)
        {
            WarnPool("no free job");
            var t = item.transform;
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            t.SetPositionAndRotation(pos, rot);
            t.localScale = item.restScale;
            item.delivered = land;
            item.parked = !land;
            if (land) { Landed?.Invoke(t); onLanded?.Invoke(); }
        }

        void TakeOut(Transform t, bool repo)
        {
            if (t == null) return;
            Init();
            var item = Register(t);
            if (Mode == DeliveryMode.Off || !t.gameObject.activeInHierarchy)
            {
                Release(t, hide: true);
                return;
            }
            int ji = item.job;
            Job j;
            bool sameCrew = ji >= 0 && !repo && !_jobs[ji].repo && _jobs[ji].crew > 0 && _jobs[ji].style == DeliveryStyle.Crown && Mode == DeliveryMode.On;
            if (ji >= 0)
            {
                j = _jobs[ji];
                if (j.phase == Phase.Inbound && j.land && j.onDone != null) { var cb = j.onDone; j.onDone = null; cb(); }
                if (!sameCrew) DetachCrewToDepart(ji);
            }
            else j = NewJobFor(item, out ji);
            if (j == null) { Release(t, hide: true); return; }

            j.item = item;
            j.style = DeliveryStyle.Crown;
            j.repo = repo;
            j.land = false;
            j.heavy = item.Heavy;
            j.onDone = null;
            j.start = _now;
            j.createdFrame = Time.frameCount;
            j.tilt = j.tiltVel = Vector3.zero;
            j.prevValid = 0;
            item.delivered = false;
            item.parked = false;
            int want = Crewed(repo && item.Heavy ? 8 : item.Crew); // meadow: crew.scale

            if (Mode == DeliveryMode.Swirl)
            {
                if (j.crew == 0) j.crew = AllocBees(ji, want == 1 ? 1 : Mathf.Clamp(want, 6, 12), t.position);
                j.phase = Phase.SwirlOut;
                j.duration = swirlSeconds;
                Log((repo ? "swirl-repo " : "swirl-return ") + t.name);
                GameSound.Cue("swirl_in", t);
                RecountInFlight();
                return;
            }

            j.speedScale = repo && item.Heavy ? repoHeavySpeedScale : 1f;
            j.after = repo && item.Heavy ? Phase.Drag : Phase.Outbound;
            if (sameCrew)
            {
                BeginOutbound(j);
            }
            else
            {
                // a fresh crew (red for a repo) flies out of the window to the item first
                j.crew = AllocBees(ji, want, PanePosition());
                for (int k = 0; k < j.crew; k++) { int b = j.bees[k]; _bFrom[b] = PanePosition(); _bDelay[b] = k * 0.03f; }
                j.phase = Phase.Approach;
                j.duration = approachSeconds;
            }
            Log((repo ? "repo " : "return ") + t.name + " crew " + j.crew);
            GameSound.Cue(repo ? "repo_haul" : "choices_return", t);
            RecountInFlight();
        }

        void BeginOutbound(Job j)
        {
            var t = j.item.transform;
            j.item.squashUntil = -1f;
            j.start = _now;
            if (j.after == Phase.Drag && j.phase != Phase.Drag)
            {
                Vector3 toPane = PanePosition() - t.position;
                toPane.y = 0f;
                if (toPane.sqrMagnitude < 1e-6f) toPane = Vector3.left;
                j.p0 = t.position;
                j.dragTo = t.position + toPane.normalized * repoDrag;
                j.startRot = t.rotation;
                j.phase = Phase.Drag;
                j.duration = 0.8f;
                return;
            }
            j.p0 = t.position;
            j.p1 = t.position + Vector3.up * 0.3f;
            j.startRot = t.rotation;
            j.startScale = t.localScale;
            j.phase = Phase.Outbound;
            float dist = Vector3.Distance(t.position, PanePosition());
            float speed = (j.heavy ? heavySpeed : crownSpeed) * j.speedScale;
            j.duration = Mathf.Clamp(dist / speed, crownMinSeconds, j.heavy ? heavyMaxSeconds / j.speedScale : crownMaxSeconds);
        }

        // ------------------------------------------------------------ jobs and bees
        Job NewJobFor(DeliveryItem item, out int ji)
        {
            ji = AllocJob();
            if (ji < 0) { item.job = -1; return null; }
            item.job = ji;
            var j = _jobs[ji];
            j.item = item;
            j.crew = 0;
            return j;
        }

        int AllocJob()
        {
            for (int i = 0; i < MaxJobs; i++)
                if (_jobs[i].phase == Phase.Free)
                {
                    var j = _jobs[i];
                    j.phase = Phase.Hold; // reserved; caller sets the real phase
                    j.crew = 0;
                    j.item = null;
                    j.onDone = null;
                    j.repo = false;
                    j.speedScale = 1f;
                    j.createdFrame = Time.frameCount;
                    return i;
                }
            WarnPool("all " + MaxJobs + " jobs busy");
            return -1;
        }

        int AllocBees(int ji, int want, Vector3 at)
        {
            var j = _jobs[ji];
            int got = 0;
            for (int b = 0; b < MaxBees && got < want; b++)
            {
                if (_bJob[b] >= 0) continue;
                _bJob[b] = ji;
                _bSlot[b] = got;
                _bPos[b] = at;
                _bFrom[b] = at;
                _bVel[b] = Vector3.zero;
                _bYaw[b] = float.NaN;
                _bDelay[b] = 0f;
                j.bees[got++] = b;
            }
            if (got < want) WarnPool("pool short: wanted " + want + ", got " + got);
            return got;
        }

        void FreeBees(Job j)
        {
            for (int k = 0; k < j.crew; k++) { int b = j.bees[k]; if (b >= 0 && b < MaxBees) _bJob[b] = -1; }
            j.crew = 0;
        }

        void FreeJob(int ji)
        {
            var j = _jobs[ji];
            FreeBees(j);
            if (j.item != null && j.item.job == ji) j.item.job = -1;
            j.item = null;
            j.onDone = null;
            j.phase = Phase.Free;
        }

        /// <summary>The job's bees become a Depart job (fly home), the job keeps the item with no crew.</summary>
        void DetachCrewToDepart(int ji, bool keepWaypoints = false)
        {
            var j = _jobs[ji];
            if (j.crew == 0) return;
            int di = AllocJob();
            if (di < 0) { FreeBees(j); return; }
            var d = _jobs[di];
            d.phase = Phase.Depart;
            d.item = null;
            d.repo = j.repo;
            d.style = j.style;
            d.start = _now;
            d.duration = departSeconds;
            d.ringCenter = j.item != null ? j.item.transform.position : PanePosition();
            for (int k = 0; k < j.crew; k++)
            {
                int b = j.bees[k];
                d.bees[k] = b;
                _bJob[b] = di;
                _bSlot[b] = k;
                _bFrom[b] = _bPos[b];
                if (!keepWaypoints) _bVel[b] = Vector3.zero;
                _bDelay[b] = (k % 6) * 0.04f;
            }
            d.crew = j.crew;
            j.crew = 0;
        }

        void WarnPool(string what)
        {
            if (_warnedPool) return;
            _warnedPool = true;
            Debug.LogWarning("[VBS][Delivery] " + what + " (items still arrive, some with fewer bees)");
        }

        // ------------------------------------------------------------ snapping
        void Snap(int ji, bool invokeCallbacks)
        {
            var j = _jobs[ji];
            var item = j.item;
            Transform t = item != null ? item.transform : null;
            switch (j.phase)
            {
                case Phase.Inbound:
                case Phase.SwirlIn:
                    if (t != null)
                    {
                        t.SetPositionAndRotation(TargetPos(j), TargetRot(j));
                        t.localScale = item.restScale;
                    }
                    if (j.land)
                    {
                        if (item != null) item.delivered = true;
                        var cb = j.onDone;
                        j.onDone = null;
                        FreeJob(ji);
                        if (invokeCallbacks) { if (t != null) Landed?.Invoke(t); cb?.Invoke(); }
                    }
                    else if (j.phase == Phase.SwirlIn) { if (item != null) item.parked = true; FreeJob(ji); }
                    else { if (item != null) item.parked = true; j.phase = Phase.Hold; } // parked: crew snaps into its hover ring (computed from the item each frame)
                    break;
                case Phase.Approach:
                case Phase.Outbound:
                case Phase.Drag:
                case Phase.SwirlOut:
                    if (t != null)
                    {
                        t.localScale = item.restScale;
                        if (t.gameObject.activeSelf) t.gameObject.SetActive(false);
                        item.delivered = false;
                        item.parked = false;
                        ClearStamp(item);
                    }
                    FreeJob(ji);
                    break;
                case Phase.Depart:
                    FreeJob(ji);
                    break;
                case Phase.Ring:
                {
                    var cb = j.onDone;
                    j.onDone = null;
                    FreeJob(ji);
                    if (_trail != null) { _trail.enabled = false; _trail.positionCount = 0; }
                    if (invokeCallbacks) cb?.Invoke();
                    break;
                }
                case Phase.Hold:
                    break; // parked items stay parked
            }
        }

        void RecountInFlight()
        {
            int n = 0;
            for (int i = 0; i < MaxJobs; i++)
            {
                var p = _jobs[i].phase;
                if (p == Phase.Inbound || p == Phase.Outbound || p == Phase.Drag || p == Phase.Approach) n++;
            }
            InFlight = n;
        }

        // ------------------------------------------------------------ frame
        /// <summary>Advance everything by dt seconds and draw. Update calls this; tests call it directly.</summary>
        public void Step(float dt)
        {
            if (!_ready) Init();
            dt = Mathf.Clamp(dt, 0f, 0.1f);
            _now += dt;
            float now = _now;

            for (int ji = 0; ji < MaxJobs; ji++)
            {
                var j = _jobs[ji];
                if (j.phase == Phase.Free) continue;
                if (j.item != null && j.item.transform == null) { FreeJob(ji); continue; }
                switch (j.phase)
                {
                    case Phase.Inbound: StepInbound(ji, j, dt); break;
                    case Phase.Hold: StepHold(ji, j, now); break;
                    case Phase.Approach: StepApproach(j, now); break;
                    case Phase.Drag: StepDrag(j, now); break;
                    case Phase.Outbound: StepOutbound(ji, j, dt); break;
                    case Phase.Depart: StepDepart(ji, j, now); break;
                    case Phase.SwirlIn:
                    case Phase.SwirlOut: StepSwirl(ji, j, now); break;
                    case Phase.Ring: StepRing(ji, j, now); break;
                }
            }
            RecountInFlight();
            StepItems(now);
            Draw(now, dt);
        }

        float K(Job j) => j.duration <= 0f ? 1f : Mathf.Clamp01((_now - j.start) / j.duration);

        static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float u)
        {
            float v = 1f - u;
            return v * v * v * a + 3f * v * v * u * b + 3f * v * u * u * c + u * u * u * d;
        }

        static float Bump(float u, float c) { float x = (u - c) / 0.06f; return Mathf.Exp(-x * x); }

        void StepInbound(int ji, Job j, float dt)
        {
            var item = j.item;
            var t = item.transform;
            float k = K(j);
            float u = k * k * (3f - 2f * k);
            Vector3 p3 = TargetPos(j);
            Vector3 p2 = p3 + Vector3.up * 0.3f;
            Vector3 pivot = Bezier(j.p0, j.p1, p2, p3, u);
            if (j.heavy && j.style == DeliveryStyle.Crown) pivot.y -= heavyDip * (Bump(u, 0.45f) + Bump(u, 0.75f));
            Quaternion rot = Quaternion.Slerp(j.startRot, TargetRot(j), u);
            float scaleK = Mathf.SmoothStep(0f, 1f, k / 0.2f);
            t.localScale = Vector3.LerpUnclamped(j.startScale, item.restScale, scaleK);
            if (j.avoidHive is Bounds hive)
            {
                Vector3 d = pivot - hive.center;
                float edge = Mathf.Max(Mathf.Abs(d.x) - hive.extents.x, Mathf.Abs(d.z) - hive.extents.z);
                float clear = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.02f, 0.07f, edge - item.Radius));
                pivot.y = Mathf.Lerp(pivot.y, Mathf.Max(pivot.y, hive.max.y + 0.06f), clear);
            }
            ApplySwing(j, item, pivot, rot, dt, 1f - Mathf.SmoothStep(0f, 1f, (u - 0.8f) / 0.2f));
            PlaceCrew(j, now: _now, carrying: true);
            if (k >= 1f)
            {
                t.SetPositionAndRotation(p3, TargetRot(j));
                t.localScale = item.restScale;
                if (j.land)
                {
                    item.delivered = true;
                    Touchdown(item);
                    var cb = j.onDone;
                    j.onDone = null;
                    bool skitter = j.style == DeliveryStyle.Festoon;
                    if (skitter) SkitterOnLanding(j);
                    DetachCrewToDepart(ji, skitter);
                    FreeJob(ji);
                    Log("landed " + t.name);
                    Landed?.Invoke(t);
                    cb?.Invoke();
                }
                else
                {
                    j.phase = Phase.Hold;
                    item.parked = true;
                    if (j.parkArrival) PlayParkArrival(item);
                    Log("parked " + t.name);
                }
            }
        }

        void SkitterOnLanding(Job j)
        {
            // every other chain bee skitters across the table before flying home (Depart reads _bVel as a waypoint flag)
            float y = TableY(j) + 0.012f;
            Vector3 c = j.item.transform.position;
            for (int k = 0; k < j.crew; k++)
            {
                int b = j.bees[k];
                if ((k & 1) == 0) { _bVel[b] = Vector3.zero; continue; }
                float a = _bPhase[b] + k * 0.7f;
                float r = 0.18f + 0.12f * Mathf.Abs(Mathf.Sin(_bPhase[b] * 3f));
                _bVel[b] = new Vector3(c.x + Mathf.Cos(a) * r, y, c.z + Mathf.Sin(a) * r); // waypoint
            }
        }

        void ApplySwing(Job j, DeliveryItem item, Vector3 pivot, Quaternion rot, float dt, float fade)
        {
            var t = item.transform;
            // pendulum: a damped spring on the path's lateral acceleration, tilt capped
            if (dt > 1e-5f && j.prevValid >= 1)
            {
                Vector3 v = (pivot - j.prevPos) / dt;
                if (j.prevValid >= 2)
                {
                    Vector3 a = (v - j.prevVel) / dt;
                    Vector3 lat = a - Vector3.up * Vector3.Dot(a, Vector3.up);
                    float mag = lat.magnitude;
                    Vector3 target = Vector3.zero;
                    if (mag > 1e-3f)
                        target = Vector3.Cross(Vector3.up, lat / mag) * Mathf.Min(maxTiltDegrees, Mathf.Atan2(mag, 9.81f) * Mathf.Rad2Deg);
                    j.tiltVel += ((target - j.tilt) * 45f - j.tiltVel * 7f) * dt;
                    j.tilt += j.tiltVel * dt;
                    float tm = j.tilt.magnitude;
                    if (tm > maxTiltDegrees) j.tilt *= maxTiltDegrees / tm;
                }
                j.prevVel = v;
                j.prevValid = 2;
            }
            else j.prevValid = Mathf.Max(j.prevValid, 1);
            j.prevPos = pivot;

            Vector3 tilt = j.tilt * fade;
            float ang = tilt.magnitude;
            if (ang > 0.01f && j.style == DeliveryStyle.Crown)
            {
                Quaternion r = Quaternion.AngleAxis(ang, tilt / ang);
                Vector3 crown = pivot + Vector3.up * (item.Top + 0.04f);
                t.SetPositionAndRotation(crown + r * (pivot - crown), r * rot);
            }
            else t.SetPositionAndRotation(pivot, rot);
        }

        void StepHold(int ji, Job j, float now)
        {
            if (j.item == null) { FreeJob(ji); return; }
            if (!j.item.gameObject.activeInHierarchy) { j.item.parked = false; FreeJob(ji); return; }
            PlaceCrew(j, now, carrying: false);
        }

        void StepApproach(Job j, float now)
        {
            var t = j.item.transform;
            bool arrived = true;
            for (int k = 0; k < j.crew; k++)
            {
                int b = j.bees[k];
                Vector3 slot = CrewSlot(j, k, now);
                float kk = Mathf.Clamp01((now - j.start - _bDelay[b]) / Mathf.Max(0.05f, j.duration));
                if (kk < 1f) arrived = false;
                float e = kk * kk * (3f - 2f * kk);
                Vector3 mid = (_bFrom[b] + slot) * 0.5f + Vector3.up * 0.15f;
                Vector3 p = Vector3.LerpUnclamped(Vector3.LerpUnclamped(_bFrom[b], mid, e), Vector3.LerpUnclamped(mid, slot, e), e);
                _bVel[b] = p - _bPos[b];
                _bPos[b] = p;
            }
            if (arrived || now - j.start > j.duration + 0.5f)
            {
                j.phase = j.after == Phase.Drag ? Phase.Hold : Phase.Outbound; // marker; BeginOutbound sets the real one
                BeginOutbound(j);
            }
            _ = t;
        }

        void StepDrag(Job j, float now)
        {
            var t = j.item.transform;
            float k = K(j);
            float e = k * k * (3f - 2f * k);
            Vector3 p = Vector3.LerpUnclamped(j.p0, j.dragTo, e);
            p += Vector3.up * (0.006f * Mathf.Abs(Mathf.Sin(k * 18f))); // struggling bumps
            t.position = p;
            PlaceCrew(j, now, carrying: true);
            if (k >= 1f)
            {
                j.after = Phase.Outbound;
                BeginOutbound(j);
            }
        }

        void StepOutbound(int ji, Job j, float dt)
        {
            var item = j.item;
            var t = item.transform;
            float k = K(j);
            float u = k * k * (3f - 2f * k);
            Vector3 pane = PanePosition();
            Vector3 c2 = pane + PaneNormal() * 0.4f + Vector3.up * 0.2f;
            Vector3 pivot = Bezier(j.p0, j.p1, c2, pane, u);
            if (j.heavy) pivot.y -= heavyDip * 0.6f * Bump(u, 0.5f);
            float scaleK = Mathf.SmoothStep(0f, 1f, (u - 0.7f) / 0.3f);
            t.localScale = Vector3.LerpUnclamped(j.startScale, item.restScale * exitScale, scaleK);
            ApplySwing(j, item, pivot, j.startRot, dt, 1f);
            PlaceCrew(j, _now, carrying: true);
            if (k >= 1f)
            {
                t.localScale = item.restScale;
                if (t.gameObject.activeSelf) t.gameObject.SetActive(false);
                ClearStamp(item);
                Log((j.repo ? "repossessed " : "returned ") + t.name);
                FreeJob(ji);
            }
        }

        void StepDepart(int ji, Job j, float now)
        {
            Vector3 pane = PanePosition();
            bool done = true;
            for (int k = 0; k < j.crew; k++)
            {
                int b = j.bees[k];
                float local = now - j.start - _bDelay[b];
                Vector3 from = _bFrom[b];
                bool skitter = j.style == DeliveryStyle.Festoon && _bVel[b] != Vector3.zero;
                if (skitter)
                {
                    // 0..0.9 s: hop to the table waypoint and run on it; then fly home
                    Vector3 wp = _bVel[b];
                    if (local < 0.9f)
                    {
                        float kk = Mathf.Clamp01(local / 0.5f);
                        Vector3 p = Vector3.Lerp(from, wp, kk * kk * (3f - 2f * kk)) + Vector3.up * (Mathf.Sin(kk * Mathf.PI) * 0.03f);
                        _bPos[b] = p;
                        done = false;
                        continue;
                    }
                    from = wp;
                    local -= 0.9f;
                }
                float kd = Mathf.Clamp01(local / j.duration);
                if (kd < 1f) done = false;
                float e = kd * kd;
                Vector3 mid = (from + pane) * 0.5f + Vector3.up * 0.25f;
                _bPos[b] = Vector3.LerpUnclamped(Vector3.LerpUnclamped(from, mid, e), Vector3.LerpUnclamped(mid, pane, e), e);
            }
            if (done) FreeJob(ji);
        }

        void StepSwirl(int ji, Job j, float now)
        {
            var item = j.item;
            var t = item.transform;
            float k = K(j);
            bool inward = j.phase == Phase.SwirlIn;
            float s;
            if (inward)
            {
                // ease-out-back to rest scale
                float c1 = 1.70158f, c3 = c1 + 1f, x = k - 1f;
                s = 1f + c3 * x * x * x + c1 * x * x;
            }
            else s = 1f - k * k;
            t.localScale = item.restScale * Mathf.Max(0f, s);
            Vector3 center = t.position + Vector3.up * Mathf.Max(0.03f, item.Top * 0.5f);
            float r = Mathf.Clamp(item.Radius + 0.05f, 0.08f, 0.16f);
            for (int q = 0; q < j.crew; q++)
            {
                int b = j.bees[q];
                float a = q * 6.2832f / Mathf.Max(1, j.crew) + now * 12f;
                _bPos[b] = center + new Vector3(Mathf.Cos(a) * r, Mathf.Sin(now * 9f + q) * 0.025f, Mathf.Sin(a) * r);
            }
            if (k >= 1f)
            {
                if (inward)
                {
                    t.localScale = item.restScale;
                    if (j.land)
                    {
                        item.delivered = true;
                        Touchdown(item);
                        var cb = j.onDone;
                        j.onDone = null;
                        FreeJob(ji);
                        Log("landed " + t.name + " (swirl)");
                        Landed?.Invoke(t);
                        cb?.Invoke();
                    }
                    else
                    {
                        item.parked = true;
                        if (j.parkArrival) PlayParkArrival(item);
                        FreeJob(ji);
                    }
                }
                else
                {
                    t.localScale = item.restScale;
                    if (t.gameObject.activeSelf) t.gameObject.SetActive(false);
                    ClearStamp(item);
                    FreeJob(ji);
                }
            }
        }

        void StepRing(int ji, Job j, float now)
        {
            float elapsed = now - j.start;
            float lead = Mathf.Clamp01(elapsed / j.duration);
            for (int k = 0; k < j.crew; k++)
            {
                int b = j.bees[k];
                float my = Mathf.Clamp01((elapsed - _bDelay[b]) / j.duration);
                float a = my * 6.2832f + 1.5708f; // start at the top
                Vector3 onRing = j.ringCenter + (j.ringRight * Mathf.Cos(a) + j.ringUp * Mathf.Sin(a)) * j.ringRadius;
                if (elapsed < _bDelay[b] + 0.25f)
                {
                    float kk = Mathf.Clamp01((elapsed - _bDelay[b]) / 0.25f);
                    onRing = Vector3.Lerp(j.ringCenter, onRing, kk);
                }
                _bPos[b] = onRing + j.ringUp * (0.006f * Mathf.Sin(now * 13f + k));
            }
            int pts = Mathf.Clamp(Mathf.CeilToInt(lead * (TrailPoints - 1)) + 1, 2, TrailPoints);
            for (int i = 0; i < pts; i++)
            {
                float a = (lead * i / (pts - 1)) * 6.2832f + 1.5708f;
                _trailPts[i] = j.ringCenter + (j.ringRight * Mathf.Cos(a) + j.ringUp * Mathf.Sin(a)) * j.ringRadius;
            }
            if (_trail != null)
            {
                _trail.enabled = true;
                _trail.positionCount = pts;
                _trail.SetPositions(_trailPts);
            }
            float last = j.crew > 0 ? _bDelay[j.bees[j.crew - 1]] : 0f;
            if (elapsed >= j.duration + last)
            {
                var cb = j.onDone;
                j.onDone = null;
                // the ring crew flies into the pane, the trail fades out
                DetachCrewToDepart(ji);
                FreeJob(ji);
                _trailFadeStart = now;
                Log("ring cut done");
                cb?.Invoke();
            }
        }

        /// <summary>Crew slot k (world) around the item's crown, or on its chain for the hive.</summary>
        Vector3 CrewSlot(Job j, int k, float now)
        {
            var item = j.item;
            var t = item.transform;
            int n = Mathf.Max(1, j.crew);
            int b = j.bees[k];
            float ph = _bPhase[b];
            if (j.style == DeliveryStyle.Festoon)
            {
                int chains = Mathf.Max(6, (n + 7) / 8), chain = k / 8, link = k % 8; // meadow: more chains with crew.scale
                float a = (chain * 360f / chains + 30f) * Mathf.Deg2Rad;
                float rx = Mathf.Min(0.13f * SlotK, item.Radius * 0.85f), rz = rx * 0.9f;
                Vector3 baseP = t.position + t.rotation * new Vector3(Mathf.Cos(a) * rx, item.Top, Mathf.Sin(a) * rz);
                Vector3 inward = t.position + Vector3.up * item.Top - baseP;
                inward.y = 0f;
                Vector3 sway = t.right * (Mathf.Sin(now * 3f + chain + link * 0.4f) * 0.004f * link);
                return baseP + Vector3.up * (0.022f * SlotK * (link + 1)) + inward * (0.06f * link) + sway;
            }
            if (n == 1)
                return t.position + Vector3.up * (item.Top + 0.02f + 0.006f * Mathf.Sin(now * 11f + ph));

            // meadow: big crews stack in rings of ~12 (the bottom ring widest), spread with the rig
            int rings = Mathf.Max(1, (n + 11) / 12), ring = k % rings, inRing = Mathf.Max(1, (n - ring + rings - 1) / rings);
            float r = Mathf.Clamp(item.Radius + 0.015f, 0.035f, 0.12f * SlotK) * (1f - 0.18f * ring);
            float ang = (k / rings) * 6.2832f / inRing + ring * 0.6f + now * 0.8f;
            Vector3 crown = t.position + Vector3.up * (item.Top + 0.04f + ring * 0.03f * SlotK);
            return crown + new Vector3(Mathf.Cos(ang) * r, 0.006f * Mathf.Sin(now * 11f + ph), Mathf.Sin(ang) * r);
        }

        void PlaceCrew(Job j, float now, bool carrying)
        {
            for (int k = 0; k < j.crew; k++)
            {
                int b = j.bees[k];
                Vector3 p = CrewSlot(j, k, now);
                _bVel[b] = p - _bPos[b];
                _bPos[b] = p;
            }
        }

        // ------------------------------------------------------------ touchdown, shadows, stamps
        bool _arSynced;
        /// <summary>The AR layer's ContactShadows is in the scene and no explicit hook overrides it.</summary>
        bool ArActive => useArLayer && ShadowHook == null && TouchdownHook == null && AR.ContactShadows.Instance != null;

        /// <summary>The hive: ArRig already shadows Hive/LiftRoot/Body, so the root would double it.</summary>
        static Transform ArCaster(Transform t)
        {
            if (t.GetComponent<HiveBody>() != null)
            {
                var body = t.Find("LiftRoot/Body");
                if (body != null) return body;
            }
            return t;
        }

        static void ArShadow(Transform t)
        {
            var cs = AR.ContactShadows.Instance;
            if (cs != null) cs.Register(ArCaster(t));
        }

        void Touchdown(DeliveryItem item)
        {
            var t = item.transform;
            Log("touchdown " + t.name);
            if (!item.castShadow) return; // menus and held props have no support surface
            GameSound.Cue(item.Heavy ? "touchdown_heavy" : "touchdown_light", t);
            if (TouchdownHook != null) { TouchdownHook(t, item.weight); return; }
            if (ArActive && Mode != DeliveryMode.Off)
            {
                // AR weight: 1 = a prop, 2-3 = the hive or the jar
                AR.Touchdown.Play(ArCaster(t), item.Heavy ? 2.5f : 1f + Mathf.Clamp01(item.weight));
                return;
            }
            item.squashUntil = _now + squashSeconds;
            t.localScale = Vector3.Scale(item.restScale, squash);
            if (_dust != null && Mode != DeliveryMode.Off)
                AR.Touchdown.EmitCloud(_dust, t, item.Heavy ? 2.5f : 1f + Mathf.Clamp01(item.weight), dustParticles);
            if (thunkClip != null && _audio != null)
            {
                _audio.transform.position = t.position;
                _audio.pitch = Mathf.Lerp(1.15f, 0.7f, Mathf.Clamp01(item.weight));
                _audio.PlayOneShot(thunkClip, 0.9f);
            }
        }

        void PlayParkArrival(DeliveryItem item)
        {
            var t = item.transform;
            GameSound.Cue("touchdown_light", t);
            if (_dust == null) return;
            _dust.transform.position = t.position + Vector3.up * 0.01f;
            _dust.Emit(Mathf.Max(1, dustParticles / 3));
        }

        void StepItems(float now)
        {
            float tableY = TableY();
            bool ar = ArActive;
            if (ar && !_arSynced)
            {
                // ArRig makes ContactShadows in its Start: hand it the items registered before that
                _arSynced = true;
                for (int i = 0; i < _itemCount; i++)
                    if (_items[i] != null && _items[i].castShadow) ArShadow(_items[i].transform);
                if (logEvents) Debug.Log("[VBS][Delivery] shadows + touchdown: AR layer (ContactShadows)");
            }
            bool shadows = Mode != DeliveryMode.Off && ShadowHook == null && !ar;
            for (int i = 0; i < _itemCount; i++)
            {
                var it = _items[i];
                if (it == null) continue;
                var t = it.transform;
                if (it.squashUntil >= 0f && now >= it.squashUntil)
                {
                    it.squashUntil = -1f;
                    t.localScale = it.restScale;
                }
                if (it.stampAt >= 0f && it.stamp != null)
                {
                    float e = now - it.stampAt;
                    float s = e < 0.06f ? Mathf.Lerp(0f, 1.2f, e / 0.06f) : e < 0.12f ? Mathf.Lerp(1.2f, 1f, (e - 0.06f) / 0.06f) : 1f;
                    // world size fixed: undo the item's scale
                    Vector3 ls = t.lossyScale;
                    it.stamp.localScale = new Vector3(s / Mathf.Max(1e-4f, Mathf.Abs(ls.x)), s / Mathf.Max(1e-4f, Mathf.Abs(ls.y)), s / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));
                    if (e >= 0.12f) it.stampAt = -2f; // settled (keeps showing)
                }
                var sh = it.shadow;
                if (sh == null) continue;
                bool show = shadows && it.castShadow && t.gameObject.activeInHierarchy && (it.job >= 0 || it.delivered);
                if (!show)
                {
                    if (sh.enabled) sh.enabled = false;
                    it.shadowAlpha = -1f;
                    continue;
                }
                Vector3 p = t.position;
                float h = Mathf.Max(0f, p.y - tableY);
                float alpha = shadowMaxAlpha * (1f - Mathf.Clamp01(h / shadowFadeHeight));
                if (alpha <= 0.005f)
                {
                    if (sh.enabled) sh.enabled = false;
                    it.shadowAlpha = -1f;
                    continue;
                }
                if (!sh.enabled) sh.enabled = true;
                float size = it.Radius * 2.4f * (1f + h);
                var st = sh.transform;
                st.position = new Vector3(p.x, tableY + 0.002f, p.z);
                st.localScale = new Vector3(size, size, 1f);
                if (Mathf.Abs(alpha - it.shadowAlpha) > 0.01f)
                {
                    it.shadowAlpha = alpha;
                    GameMaterials.Tint(sh, new Color(0f, 0f, 0f, alpha));
                }
            }
            if (_trail != null && _trailFadeStart >= 0f)
            {
                float f = (now - _trailFadeStart) / 0.6f;
                if (f >= 1f) { _trail.enabled = false; _trail.positionCount = 0; _trailFadeStart = -1f; }
                else _trail.widthMultiplier = 0.012f * (1f - f);
            }
            else if (_trail != null && _trail.enabled) _trail.widthMultiplier = 0.012f;
        }

        // ------------------------------------------------------------ draw
        void Draw(float now, float dt)
        {
            Vector4 courier = new Vector4(courierColor.r * brightness, courierColor.g * brightness, courierColor.b * brightness, 1f);
            Vector4 repo = new Vector4(repoColor.r * brightness, repoColor.g * brightness, repoColor.b * brightness, 1f);
            float turnBlend = 1f - Mathf.Exp(-12f * dt);
            int n = 0;
            for (int b = 0; b < MaxBees; b++)
            {
                int ji = _bJob[b];
                if (ji < 0) continue;
                var j = _jobs[ji];
                Vector3 p = _bPos[b];
                Vector3 fwd = _bVel[b];
                if (j.phase == Phase.Depart || j.phase == Phase.Ring) fwd = p - _bFrom[b];
                Quaternion rot;
                if (j.crew == 1 && j.style == DeliveryStyle.Crown && j.item != null &&
                    j.phase != Phase.SwirlIn && j.phase != Phase.SwirlOut)
                {
                    // The solo courier should face along its flight, then turn toward the player while hovering.
                    // Use yaw only so the small vertical bob cannot flip its facing near a stop.
                    if (j.phase == Phase.Hold) fwd = -j.item.transform.forward;
                    if (fwd.x * fwd.x + fwd.z * fwd.z > 1e-8f)
                    {
                        float targetYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                        _bYaw[b] = float.IsNaN(_bYaw[b]) ? targetYaw : Mathf.LerpAngle(_bYaw[b], targetYaw, turnBlend);
                    }
                    rot = Quaternion.Euler(0f, float.IsNaN(_bYaw[b]) ? 0f : _bYaw[b], 0f);
                }
                else
                {
                    if (fwd.sqrMagnitude < 1e-8f || j.phase == Phase.Hold)
                    {
                        Vector3 c = j.item != null ? j.item.transform.position : p;
                        Vector3 radial = p - c;
                        radial.y = 0f;
                        fwd = radial.sqrMagnitude > 1e-8f ? Vector3.Cross(Vector3.up, radial) : Vector3.forward;
                    }
                    rot = Quaternion.LookRotation(fwd, Vector3.up);
                }
                float ph = _bPhase[b];
                float scale = beeLength * (Bloom.MeadowLayout.Active ? 1.4f : 1f); // meadow: bees that read across the room
                if (j.phase == Phase.Depart)
                {
                    // shrink into the pane over the last 15% of the way
                    float kd = Mathf.Clamp01((now - j.start - _bDelay[b]) / j.duration);
                    scale *= 1f - Mathf.Clamp01((kd - 0.85f) / 0.15f) * 0.8f;
                }
                _matrices[n] = Matrix4x4.TRS(p, rot * InsectBodySway.Offset(now, ph, 0.55f), new Vector3(scale, scale, scale)); // wings flap in the shader
                float shade = 0.85f + 0.3f * (ph / 6.2832f);
                Vector4 c4 = j.repo ? repo : courier;
                _colors[n] = new Vector4(c4.x * shade, c4.y * shade, c4.z * shade, 1f);
                n++;
            }
            if (n == 0 || beeMesh == null || beeMaterial == null) return;
            if (_instancing)
            {
                if (_lod == null || !_lod.Draw(beeMesh, beeMaterial, _matrices, _colors, n, gameObject.layer)) // meadow: near VAT / far low-poly
                {
                _mpb.SetVectorArray(ColorId, _colors);
                Graphics.DrawMeshInstanced(beeMesh, 0, beeMaterial, _matrices, n, _mpb, ShadowCastingMode.Off, false, gameObject.layer);
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    _mpb.SetColor(ColorId, _colors[i]);
                    Graphics.DrawMesh(beeMesh, _matrices[i], beeMaterial, gameObject.layer, null, 0, _mpb, ShadowCastingMode.Off, false);
                }
            }
        }

        // ------------------------------------------------------------ misc
        void Log(string s)
        {
            if (logEvents) Debug.Log("[VBS][Delivery] " + s);
        }

        public static string ModeName(DeliveryMode m) => m == DeliveryMode.Swirl ? "swirl" : m == DeliveryMode.Off ? "off" : "on";
        static string StyleName(DeliveryStyle s) => s == DeliveryStyle.Festoon ? "festoon" : "crown";

        /// <summary>"on" | "swirl" | "off" (tuning.json delivery.mode). Anything else: fallback.</summary>
        public static DeliveryMode ParseMode(string s, DeliveryMode fallback = DeliveryMode.On)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim().ToLowerInvariant();
            if (s == "on" || s == "true" || s == "1") return DeliveryMode.On;
            if (s == "swirl") return DeliveryMode.Swirl;
            if (s == "off" || s == "false" || s == "0") return DeliveryMode.Off;
            return fallback;
        }

        /// <summary>tuning.json: "delivery.mode": "swirl" or "delivery": { "mode": "swirl" } (persistentDataPath wins over StreamingAssets).</summary>
        public static DeliveryMode ModeFromTuningFiles(DeliveryMode fallback)
        {
            var mode = fallback;
            try
            {
#if !UNITY_ANDROID || UNITY_EDITOR
                mode = ModeFromJson(ReadIfExists(System.IO.Path.Combine(Application.streamingAssetsPath, "tuning.json")), mode);
#endif
                mode = ModeFromJson(ReadIfExists(System.IO.Path.Combine(Application.persistentDataPath, "tuning.json")), mode);
            }
            catch (Exception e) { Debug.LogWarning("[VBS][Delivery] tuning.json: " + e.Message); }
            return mode;
        }

        static string ReadIfExists(string path) => System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;

        public static DeliveryMode ModeFromJson(string json, DeliveryMode fallback)
        {
            if (string.IsNullOrEmpty(json)) return fallback;
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"delivery\\.mode\"\\s*:\\s*\"(\\w+)\"");
            if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(json, "\"delivery\"\\s*:\\s*\\{[^}]*?\"mode\"\\s*:\\s*\"(\\w+)\"");
            return m.Success ? ParseMode(m.Groups[1].Value, fallback) : fallback;
        }

        public static DeliveryMode ModeFromEnvironment(DeliveryMode fallback)
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                    if (args[i] == "-deliveryMode") return ParseMode(args[i + 1], fallback);
                return ParseMode(Environment.GetEnvironmentVariable("VBS_DELIVERY_MODE"), fallback);
            }
            catch (Exception) { return fallback; }
        }
    }
}
