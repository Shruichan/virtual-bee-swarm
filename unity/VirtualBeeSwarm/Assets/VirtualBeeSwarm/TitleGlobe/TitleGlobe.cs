using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Video;
using VirtualBeeSwarm.Stakes;

namespace VirtualBeeSwarm.Title
{
    /// <summary>
    /// The Orbis globe on the title screen (tuning title.globe, default on; needs title.on). In pre a glass ball floats
    /// beside the logo (on the side away from Beeswax and his clipboard) with pre-recorded Orbis meadow footage inside it,
    /// a new shot every few seconds, a little honey ring under it and sticker bees orbiting it.
    ///   START     signing the clipboard, the START button, N / Space, the autopilot, a pinch or poke on the globe, or I
    ///             (the desktop key) all start the hook as before; the globe then grows round the player's head (0.55 s),
    ///             wraps the footage round them as an inside-out sphere at no more than 70% opacity (1.75 s, the room
    ///             shows through; the sphere stays put in the world, only the video moves), and bursts into petals and
    ///             pollen (0.65 s) onto the room the hook is already turning into the meadow. It runs alongside the hook:
    ///             it adds no time to it.
    ///   comfort   (StakesFlags.Comfort) no wrap: the globe just dissolves where it is, with a small burst.
    ///   clips     StreamingAssets/globe_*.mp4 (on Android the names in <see cref="AndroidClips"/>), else meadow.mp4 +
    ///             full_bloom.mp4; globe_flight.mp4 is kept for the dive (prepared in pre, it plays inside the sphere);
    ///             env VBS_GLOBE_CLIPS = a folder of .webm/.mp4 files instead (Linux captures: VP8 copies). A clip that
    ///             fails is dropped; with none left a painted meadow gradient shows.
    /// Installs itself next to the TitleDirector; reads the logo's spot through <see cref="TitleDirector.LogoSpot"/>.
    /// </summary>
    [DefaultExecutionOrder(-20)] // after TitleDirector (-25): the logo has moved this frame
    public class TitleGlobe : MonoBehaviour, IGameTarget
    {
        public static bool AutoInstall = true;
        public static TitleGlobe Instance { get; private set; }

        [SerializeField] float shotSeconds = 7.5f;
        [SerializeField] float popSeconds = 0.7f;
        [Tooltip("Gap between the logo's edge and the globe (m).")]
        [SerializeField] float gap = 0.08f;
        [Tooltip("Toward the player from the logo's plane (m).")]
        [SerializeField] float nearer = 0.12f;
        [Tooltip("Its centre at most this far to the side of the logo's centre, seen from the eyes (deg): the Quest lens edge blurs past ~40.")]
        [SerializeField] float maxSideDegrees = 31f;
        [Tooltip("This much nearer than the logo (m).")]
        [SerializeField] float pullIn = 0.1f;
        [Tooltip("Below the logo's centre (m): chest height.")]
        [SerializeField] float drop = 0.06f;
        [SerializeField] float fadeInSeconds = 0.6f;
        [SerializeField] float approachSeconds = 0.55f;
        [SerializeField] float insideSeconds = 1.75f;
        [SerializeField] float burstSeconds = 0.65f;
        [SerializeField] float comfortSeconds = 0.7f;
        [SerializeField, Range(0f, 0.7f)] float insideAlpha = 0.7f;
        [SerializeField] float insideRadius = 1.6f;
        [SerializeField] Vector2Int videoSize = new Vector2Int(832, 480);
        [SerializeField] KeyCode diveKey = KeyCode.I;
        [SerializeField] bool log = true;

        enum State { Hidden, Showing, Approach, Inside, Burst, Comfort }

        State _state = State.Hidden;
        GameDirector _director;
        bool _subscribed, _built;

        Transform _sphere, _ring, _ring2;
        Material _mat, _ringMat, _ring2Mat, _beeMat;
        Mesh _ringMesh, _ring2Mesh;
        SphereCollider _col;
        GlobeBurst _burst;
        readonly List<Transform> _bees = new List<Transform>();
        readonly List<Material> _beeMats = new List<Material>();
        readonly List<int> _beeFacing = new List<int>();
        Vector3[] _beePrev = new Vector3[0];

        // where it is
        Vector3 _pos, _from, _centre, _placeEye;
        float _radius = 0.21f, _fromRadius, _shownAt, _stateAt, _hover, _hoverGoal, _swap;
        bool _placed, _inside;

        // the footage
        VideoPlayer _vp;
        RenderTexture _rt;
        Texture2D _painted;
        Texture _poster; // a frame of the first Orbis shot (Resources/globe_poster), so the ball never opens on a blob
        Texture Still => _poster != null ? _poster : _painted;
        readonly List<string> _urls = new List<string>();
        readonly List<bool> _fallback = new List<bool>();
        readonly HashSet<int> _bad = new HashSet<int>();
        int _cur = -1, _loading = -1;
        float _shotAt;
        bool _hasFrame, _advance, _allBadSaid;
        // the dive's own shot (globe_flight: a bee's-eye flight over the meadow), prepared in pre, played on START
        VideoPlayer _flightVp;
        RenderTexture _flightRt;
        string _flightUrl;
        bool _flightReady, _flightBad, _flightOn, _flightShown;

        static readonly int TexId = Shader.PropertyToID("_MainTex");
        static readonly int AlphaId = Shader.PropertyToID("_Alpha");
        static readonly int InsideId = Shader.PropertyToID("_Inside");
        static readonly int DissolveId = Shader.PropertyToID("_Dissolve");
        static readonly int GlowId = Shader.PropertyToID("_Glow");
        static readonly int CullId = Shader.PropertyToID("_Cull");
        static readonly int AspectId = Shader.PropertyToID("_VideoAspect");
        static readonly int EdgeSoftId = Shader.PropertyToID("_EdgeSoft");

        /// <summary>Android can't list StreamingAssets: the clips the record lane makes (missing ones fail and drop).</summary>
        static readonly string[] AndroidClips = { "globe_hive", "globe_dandelion", "globe_clover" };
        const string FlightClip = "globe_flight";

        Transform Head
        {
            get
            {
                var d = BeeDelivery.Instance;
                if (d != null && d.Head != null) return d.Head;
                return Camera.main != null ? Camera.main.transform : null;
            }
        }

        bool Up => _state == State.Showing && _placed;

        // ------------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!AutoInstall) return;
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<TitleGlobe>() != null) return;
            var go = new GameObject("TitleGlobe");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<TitleGlobe>();
            Debug.Log("[VBS][Globe] installed (auto), title.globe=" + (GlobeFlags.On ? "on" : "off") + " (" + GlobeFlags.Source + ")");
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                enabled = false;
                return;
            }
            Instance = this;
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
        }

        void OnEnable()
        {
            if (Instance != this || _director == null) return;
            _director.BeatChanged += HandleBeat;
            _subscribed = true;
        }

        void Start()
        {
            if (_state == State.Hidden && _director != null && _director.Beat == GameBeat.Pre) Show("start");
        }

        void OnDisable()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (_director != null) _director.BeatChanged -= HandleBeat;
            Hide("disabled");
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_flightRt != null) { _flightRt.Release(); Destroy(_flightRt); }
            foreach (var o in new UnityEngine.Object[] { _mat, _ringMat, _ring2Mat, _beeMat, _ringMesh, _ring2Mesh, _painted })
                if (o != null) Destroy(o);
            foreach (var m in _beeMats) if (m != null) Destroy(m);
            _burst?.Destroy();
        }

        void Build()
        {
            if (_built) return;
            _built = true;
            var shader = Resources.Load<Shader>("VBS_TitleGlobe");
            if (shader == null) shader = Shader.Find("VirtualBeeSwarm/TitleGlobe");
            _painted = Painted(64);
            _poster = Resources.Load<Texture2D>("globe_poster");
            if (shader != null && shader.isSupported)
            {
                _mat = new Material(shader) { name = "VBS_TitleGlobe" };
                _mat.SetFloat(AspectId, videoSize.x / (float)Mathf.Max(1, videoSize.y));
            }
            else
            {
                Debug.LogWarning("[VBS][Globe] no TitleGlobe shader: a plain unlit ball");
                _mat = GameMaterials.Create("VBS_TitleGlobe", Color.white, GameMaterials.Blend.Alpha);
            }
            _mat.SetTexture(TexId, Still);

            var sphere = new GameObject("GlobeSphere");
            sphere.transform.SetParent(transform, false);
            sphere.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            var mr = sphere.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _sphere = sphere.transform;

            _col = gameObject.AddComponent<SphereCollider>();
            _col.isTrigger = true;
            _col.enabled = false;
            // the trigger moves every frame: a kinematic body keeps that cheap (a static collider would be rebuilt)
            var rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            // the stand: a honey ring and a thin pale one, floating under it
            _ringMesh = Torus(1f, 0.045f, 48, 8);
            _ring2Mesh = Torus(1f, 0.02f, 48, 6);
            _ringMat = GameMaterials.Create("VBS_GlobeRing", new Color(0.98f, 0.72f, 0.22f), doubleSided: true);
            _ring2Mat = GameMaterials.Create("VBS_GlobeRing2", new Color(1f, 0.93f, 0.7f), doubleSided: true);
            _ring = MeshChild("GlobeRing", _ringMesh, _ringMat);
            _ring2 = MeshChild("GlobeRing2", _ring2Mesh, _ring2Mat);

            // three of Daniel's sticker bees orbiting it
            var art = TitleArt.Load();
            int n = art != null && art.stickerBees != null ? Mathf.Min(3, art.stickerBees.Length) : 0;
            var quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            for (int i = 0; i < n; i++)
            {
                if (art.stickerBees[i] == null) continue;
                var m = GameMaterials.Create("VBS_GlobeBee" + i, Color.white, GameMaterials.Blend.Alpha, doubleSided: true);
                m.mainTexture = art.stickerBees[i];
                _beeMats.Add(m);
                _bees.Add(MeshChild("GlobeBee" + (i + 1), quad, m));
                _beeFacing.Add(art.stickerFacing != null && i < art.stickerFacing.Length && art.stickerFacing[i] < 0 ? -1 : 1);
            }
            _beePrev = new Vector3[_bees.Count];

            _burst = new GlobeBurst(transform);

            _rt = new RenderTexture(videoSize.x, videoSize.y, 0) { name = "VBS_GlobeVideo", wrapMode = TextureWrapMode.Clamp };
            _rt.Create();
            var vgo = new GameObject("GlobeVideo");
            vgo.transform.SetParent(transform, false);
            _vp = vgo.AddComponent<VideoPlayer>();
            _vp.playOnAwake = false;
            _vp.waitForFirstFrame = true;
            _vp.skipOnDrop = true;
            _vp.isLooping = true;
            _vp.audioOutputMode = VideoAudioOutputMode.None; // the footage is silent
            _vp.renderMode = VideoRenderMode.RenderTexture;
            _vp.targetTexture = _rt;
            _vp.source = VideoSource.Url;
            _vp.prepareCompleted += vp => { if (_state != State.Hidden) vp.Play(); };
            _vp.errorReceived += (vp, message) =>
            {
                // not the message: on Linux players it can arrive garbled (see ForecastMoment)
                if (_loading < 0 || _bad.Contains(_loading)) return;
                _bad.Add(_loading);
                Debug.LogWarning("[VBS][Globe] " + Path.GetFileName(_urls[_loading]) + ": video failed to play, dropped");
                _advance = true;
            };
            FindClips();
            if (_flightUrl != null)
            {
                _flightRt = new RenderTexture(videoSize.x, videoSize.y, 0) { name = "VBS_GlobeFlight", wrapMode = TextureWrapMode.Clamp };
                _flightRt.Create();
                var fgo = new GameObject("GlobeFlightVideo");
                fgo.transform.SetParent(transform, false);
                _flightVp = fgo.AddComponent<VideoPlayer>();
                _flightVp.playOnAwake = false;
                _flightVp.waitForFirstFrame = true;
                _flightVp.skipOnDrop = true;
                _flightVp.isLooping = true;
                _flightVp.audioOutputMode = VideoAudioOutputMode.None;
                _flightVp.renderMode = VideoRenderMode.RenderTexture;
                _flightVp.targetTexture = _flightRt;
                _flightVp.source = VideoSource.Url;
                _flightVp.prepareCompleted += vp => _flightReady = true;
                _flightVp.errorReceived += (vp, message) =>
                {
                    if (_flightBad) return;
                    _flightBad = true;
                    Debug.LogWarning("[VBS][Globe] " + FlightClip + ": video failed to play, the dive keeps the current shot");
                };
            }
            SetVisible(false);
        }

        Transform MeshChild(string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        void SetVisible(bool on)
        {
            if (_sphere != null) _sphere.gameObject.SetActive(on);
            if (_ring != null) _ring.gameObject.SetActive(on);
            if (_ring2 != null) _ring2.gameObject.SetActive(on);
            foreach (var b in _bees) b.gameObject.SetActive(on);
        }

        // ------------------------------------------------------------------ beats
        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            if (beat == GameBeat.Pre) Show(prev == GameBeat.Pre ? "pre" : "back to pre");
            else if (_state == State.Showing)
            {
                if (beat == GameBeat.Hook && prev == GameBeat.Pre) Dive();
                else Hide("jump to " + GameIds.BeatName(beat));
            }
            else if (_state != State.Hidden && beat != GameBeat.Hook)
            {
                // a jump mid-dive (N twice, the Board, a fast autopilot): no sphere left over the new beat
                _burst.Clear();
                Done();
            }
        }

        void Show(string why)
        {
            if (!GlobeFlags.On || !TitleFlags.On)
            {
                Hide(!TitleFlags.On ? "title.on=off" : "title.globe=off");
                return;
            }
            Build();
            _state = State.Showing;
            _placed = _inside = false;
            _hover = _hoverGoal = 0f;
            _radius = GlobeFlags.Size * 0.5f;
            _burst.Clear();
            SetVisible(false);
            SetLook(alpha: 0f, inside: false, dissolve: 0f);
            transform.rotation = Quaternion.identity;
            if (_mat != null) _mat.SetTexture(TexId, _hasFrame ? (Texture)_rt : Still);
            StartFootage();
            if (!StakesFlags.Comfort) PrepareFlight(); // comfort never wraps: no second decoder idling
            Log("up (" + why + "), " + Usable() + " clip(s)" + (_cur >= 0 ? ", first " + Path.GetFileName(_urls[_cur]) : ""));
        }

        void Hide(string why)
        {
            if (_state == State.Hidden) return;
            _state = State.Hidden;
            _placed = false;
            if (_col != null) _col.enabled = false;
            SetVisible(false);
            if (_vp != null) _vp.Stop();
            StopFlight();
            _loading = -1;
            Log("down (" + why + ")");
        }

        void SetLook(float alpha, bool inside, float dissolve, float edgeSoft = 0f)
        {
            if (_mat == null) return;
            _mat.SetFloat(AlphaId, inside ? Mathf.Min(alpha, insideAlpha) : alpha); // comfort cap: the room always shows through
            _mat.SetFloat(DissolveId, dissolve);
            _mat.SetFloat(EdgeSoftId, edgeSoft);
            if (inside != _inside || !_mat.HasProperty(InsideId) || _mat.GetFloat(InsideId) != (inside ? 1f : 0f))
            {
                _inside = inside;
                _mat.SetFloat(InsideId, inside ? 1f : 0f);
                _mat.SetFloat(CullId, (float)(inside ? CullMode.Front : CullMode.Back));
                // centred on the head it would sort last and veil every caption, sprite and petal: draw it first instead
                // (outside it stays at 3000 so the sticker bees still sort against the ball)
                _mat.renderQueue = inside ? 2990 : 3000;
            }
        }

        void OnApplicationPause(bool paused)
        {
            // headset off and on again: the held flight player can go stale
            if (!paused && _state == State.Showing && !StakesFlags.Comfort) PrepareFlight();
        }

        // ------------------------------------------------------------------ START
        public bool Interactable => Up && Time.time - _shownAt > popSeconds * 0.6f && _director != null && _director.Beat == GameBeat.Pre;
        public void SetHover(bool hovered) => _hoverGoal = hovered && Interactable ? 1f : 0f;
        public void Click() => Pinched("pointer");

        void OnTriggerEnter(Collider other)
        {
            if (other.GetComponent<GameFingertip>() != null && Interactable) Pinched("poke");
        }

        /// <summary>The player chose the globe: sign the clipboard if it is waiting (stamp, room_pre_signed), else START.</summary>
        void Pinched(string how)
        {
            if (!Interactable) return;
            Log("pinched (" + how + ")");
            var clip = FindAnyObjectByType<SignHereClipboard>();
            if (clip != null && clip.Sign("globe")) return;
            _director.Next("title globe (" + how + ")");
        }

        /// <summary>START happened (Pre -> Hook): into the globe, or (comfort) the globe just dissolves.</summary>
        void Dive()
        {
            if (!_placed)
            {
                Hide("started before it showed");
                return;
            }
            Transform head = Head;
            _col.enabled = false;
            _hoverGoal = 0f;
            _from = _sphere.position;
            _fromRadius = _sphere.localScale.x * 0.5f;
            _stateAt = Time.time;
            if (StakesFlags.Comfort || head == null)
            {
                _state = State.Comfort;
                _burst.Emit(_from, _fromRadius * 0.9f, 28);
                Log("start: comfort, the globe dissolves in place");
                return;
            }
            _state = State.Approach;
            if (_flightVp != null && _flightReady && !_flightBad)
            {
                _flightVp.Play();
                _flightOn = true;
            }
            // the sphere stays world-fixed from here; only its video moves. Its front faces where the player looks.
            Vector3 fwd = head.forward;
            fwd.y = 0f;
            transform.rotation = fwd.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(fwd.normalized, Vector3.up) : Quaternion.identity;
            Log("start: the globe swallows the player (" + Vector3.Distance(head.position, _from).ToString("0.00") + " m away)");
        }

        // ------------------------------------------------------------------ frame
        void Update()
        {
            if (_state == State.Hidden) return;
            float t = Time.time;
            Transform head = Head;
            if (_state == State.Showing)
            {
                if (!GlobeFlags.On) { Hide("title.globe=off"); return; }
                if (head != null) TickShowing(head, t);
                if (GameInput.KeyDown(diveKey) && Interactable) Pinched("key " + diveKey);
            }
            else TickDive(head, t);
            TickFootage(t);
        }

        void TickShowing(Transform head, float t)
        {
            if (TitleDirector.LogoSpot(out Vector3 logo, out Vector3 right, out Vector3 away, out float width))
            {
                Vector3 goal = logo - right * (width * 0.5f + _radius + gap) - away * nearer + Vector3.up * 0.02f;
                if (!_placed) _placeEye = head.position;
                goal = HoldIn(goal, logo, right, width);
                if (!_placed)
                {
                    _placed = true;
                    _pos = goal;
                    _shownAt = t;
                    _col.enabled = true;
                    SetVisible(true);
                    Log("beside the logo, " + Vector3.Distance(head.position, goal).ToString("0.00") + " m from the eyes, " + (_radius * 2f).ToString("0.00") + " m across");
                }
                _pos = Vector3.Lerp(_pos, goal, 1f - Mathf.Exp(-4f * Time.deltaTime));
            }
            if (!_placed) return;
            float pop = BackOut(Mathf.Clamp01((t - _shownAt) / popSeconds));
            Vector3 c = _pos + Vector3.up * (Mathf.Sin(t * 0.8f + 0.6f) * 0.012f);
            float r = _radius * pop;
            SetLook(Mathf.Clamp01((t - _shownAt) / fadeInSeconds), false, 0f);
            _sphere.position = c;
            _sphere.localScale = Vector3.one * (r * 2f);
            _col.center = transform.InverseTransformPoint(c);
            _col.radius = r;
            _hover = Mathf.MoveTowards(_hover, _hoverGoal, Time.deltaTime * 5f);
            _swap = Mathf.MoveTowards(_swap, 0f, Time.deltaTime * 2.5f);
            if (_mat != null) _mat.SetFloat(GlowId, _hover * 0.9f + _swap * 0.7f + (0.5f + 0.5f * Mathf.Sin(t * 2.2f)) * 0.12f);
            TickStand(c, r, t, 1f);
            TickBees(head, c, r, t, 0f);
        }

        /// <summary>Leave room for the globe's widest frame beside the logo's projected edge.</summary>
        Vector3 HoldIn(Vector3 goal, Vector3 logo, Vector3 right, float width)
        {
            Vector3 toLogo = logo - _placeEye, toGoal = goal - _placeEye;
            toLogo.y = toGoal.y = 0f;
            float flat = toLogo.magnitude;
            if (flat < 0.2f || toGoal.sqrMagnitude < 0.04f) return goal;
            float dist = Mathf.Max(0.6f, flat - pullIn);
            Vector3 toEdge = toLogo - right * (width * 0.5f);
            float edgeAngle = Vector3.SignedAngle(toLogo, toEdge, Vector3.up);
            // BackOut can overshoot the final radius by 10%.
            float radius = _radius * 1.11f + gap * 0.5f;
            float room = maxSideDegrees - Mathf.Abs(edgeAngle);
            if (room > 5f)
                dist = Mathf.Max(dist, Mathf.Min(flat + 0.15f, radius / Mathf.Sin(room * Mathf.Deg2Rad)));
            float ballHalf = Mathf.Asin(Mathf.Clamp01(radius / dist)) * Mathf.Rad2Deg;
            float side = Mathf.Abs(edgeAngle) + ballHalf;
            Vector3 dir = Quaternion.AngleAxis(Mathf.Sign(edgeAngle) * side, Vector3.up) * (toLogo / flat);
            return new Vector3(_placeEye.x + dir.x * dist, logo.y - drop, _placeEye.z + dir.z * dist);
        }

        void TickStand(Vector3 c, float r, float t, float k)
        {
            float s = r * k;
            _ring.gameObject.SetActive(s > 0.002f);
            _ring2.gameObject.SetActive(s > 0.002f);
            _ring.SetPositionAndRotation(c - Vector3.up * (r * 1.12f), Quaternion.Euler(0f, t * 25f, 0f) * Quaternion.Euler(4f, 0f, 0f));
            _ring.localScale = new Vector3(s * 0.62f, s * 0.62f, s * 0.62f);
            _ring2.SetPositionAndRotation(c - Vector3.up * (r * 1.2f), Quaternion.Euler(0f, -t * 40f, 0f) * Quaternion.Euler(-6f, 0f, 3f));
            _ring2.localScale = new Vector3(s * 0.78f, s * 0.78f, s * 0.78f);
        }

        /// <summary>Bees on tilted loops round the ball; scatter 0..1 flings them out and shrinks them.</summary>
        void TickBees(Transform head, Vector3 c, float r, float t, float scatter)
        {
            for (int i = 0; i < _bees.Count; i++)
            {
                var b = _bees[i];
                float speed = 0.9f + 0.25f * i, ph = i * 2.1f;
                float a = t * speed * (i % 2 == 0 ? 1f : -1f) + ph;
                Quaternion tiltQ = Quaternion.Euler(18f + 14f * i, 40f * i, -12f + 10f * i);
                float orbit = r * (1.28f + 0.1f * i) * (1f + scatter * 3f);
                Vector3 p = c + tiltQ * new Vector3(Mathf.Cos(a) * orbit, Mathf.Sin(t * 2.3f + i) * 0.02f, Mathf.Sin(a) * orbit);
                Vector3 vel = p - _beePrev[i];
                _beePrev[i] = p;
                float size = 0.075f * Mathf.Clamp01(r / Mathf.Max(0.01f, _radius)) * (1f - scatter);
                b.gameObject.SetActive(size > 0.004f);
                if (size <= 0.004f || head == null) continue;
                Vector3 look = p - head.position;
                Quaternion face = look.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(look, Vector3.up) : Quaternion.identity;
                float side = Vector3.Dot(vel, face * Vector3.right) >= 0f ? 1f : -1f; // the way it flies, on screen
                b.SetPositionAndRotation(p, face * Quaternion.Euler(0f, 0f, Mathf.Sin(t * 3f + i) * 8f));
                b.localScale = new Vector3(size * side * _beeFacing[i], size, 1f);
            }
        }

        void TickDive(Transform head, float t)
        {
            float k;
            switch (_state)
            {
                case State.Approach:
                {
                    // the ball comes to the head and grows round it (only the ball moves)
                    k = Mathf.Clamp01((t - _stateAt) / approachSeconds);
                    float e = k * k * (3f - 2f * k);
                    Vector3 eye = head != null ? head.position : _from;
                    _centre = Vector3.Lerp(_from, eye, e);
                    float r = Mathf.Lerp(_fromRadius, insideRadius * 0.75f, e * e);
                    _sphere.position = _centre;
                    _sphere.localScale = Vector3.one * (r * 2f);
                    bool inside = head != null && Vector3.Distance(head.position, _centre) < r * 0.97f;
                    SetLook(Mathf.Lerp(1f, insideAlpha, e), inside, 0f, e); // soft rim while it grows: no hard edge across the ceiling
                    if (_mat != null) _mat.SetFloat(GlowId, 0.3f * (1f - e));
                    TickStand(_from, _fromRadius, t, 1f - Mathf.Clamp01(k * 2.5f));
                    TickBees(head, _from, _fromRadius, t, Mathf.Clamp01(k * 1.8f));
                    if (k >= 1f)
                    {
                        _state = State.Inside;
                        _stateAt = t;
                        _centre = eye;
                        Log("inside the globe");
                    }
                    break;
                }
                case State.Inside:
                {
                    k = Mathf.Clamp01((t - _stateAt) / insideSeconds);
                    float r = Mathf.Lerp(insideRadius * 0.75f, insideRadius, k);
                    _sphere.position = _centre;
                    _sphere.localScale = Vector3.one * (r * 2f);
                    SetLook(insideAlpha, head == null || Vector3.Distance(head.position, _centre) < r * 0.97f, 0f);
                    if (k >= 1f)
                    {
                        _state = State.Burst;
                        _stateAt = t;
                        _burst.Emit(head != null ? head.position : _centre, 0.55f, 70);
                        Log("burst into the room");
                    }
                    break;
                }
                case State.Burst:
                {
                    k = Mathf.Clamp01((t - _stateAt) / burstSeconds);
                    float r = Mathf.Lerp(insideRadius, insideRadius * 1.5f, 1f - (1f - k) * (1f - k));
                    _sphere.position = _centre;
                    _sphere.localScale = Vector3.one * (r * 2f);
                    SetLook(insideAlpha * (1f - 0.3f * k), head == null || Vector3.Distance(head.position, _centre) < r * 0.97f, k);
                    if (k >= 1f) Done();
                    break;
                }
                case State.Comfort:
                {
                    k = Mathf.Clamp01((t - _stateAt) / comfortSeconds);
                    float r = _fromRadius * (1f + 0.15f * k);
                    _sphere.position = _from;
                    _sphere.localScale = Vector3.one * (r * 2f);
                    SetLook(1f - 0.4f * k, false, k);
                    TickStand(_from, _fromRadius, t, 1f - k);
                    TickBees(head, _from, _fromRadius, t, k);
                    if (k >= 1f) Done();
                    break;
                }
            }
        }

        void Done()
        {
            // the petals drift on by themselves (world-space particles)
            _state = State.Hidden;
            _placed = false;
            SetVisible(false);
            if (_vp != null) _vp.Stop();
            StopFlight();
            _loading = -1;
            Log("gone");
        }

        // ------------------------------------------------------------------ footage
        void FindClips()
        {
            _urls.Clear();
            _fallback.Clear();
            string dir = null;
            try { dir = Environment.GetEnvironmentVariable("VBS_GLOBE_CLIPS"); } catch (Exception) { }
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                var files = new List<string>();
                foreach (var f in Directory.GetFiles(dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".webm" || ext == ".mp4") files.Add(f);
                }
                files.Sort(ByHiveFirst);
                foreach (var f in files) Add(f, false);
            }
            if (_urls.Count == 0 && _flightUrl == null)
            {
                string sa = Application.streamingAssetsPath;
#if UNITY_ANDROID && !UNITY_EDITOR
                foreach (var n in AndroidClips) Add(Path.Combine(sa, n + ".mp4"), false); // missing ones fail and drop
                _flightUrl = Path.Combine(sa, FlightClip + ".mp4");
                Add(Path.Combine(sa, "meadow.mp4"), true);
                Add(Path.Combine(sa, "full_bloom.mp4"), true);
#else
                try
                {
                    var files = new List<string>(Directory.GetFiles(sa, "globe_*.mp4"));
                    files.Sort(ByHiveFirst);
                    foreach (var f in files) Add(f, false);
                }
                catch (Exception) { }
                foreach (var f in new[] { "meadow.mp4", "full_bloom.mp4" })
                {
                    string p = Path.Combine(sa, f);
                    if (File.Exists(p)) Add(p, true);
                }
#endif
            }
            Log("clips: " + _urls.Count + (_flightUrl != null ? " + " + FlightClip + " for the dive" : "") + (dir != null && dir.Length > 0 ? " (VBS_GLOBE_CLIPS " + dir + ")" : ""));
        }

        /// <summary>globe_hive first (the white hive in the clover: the shot that says what the game is), then by name, last first.</summary>
        static int ByHiveFirst(string a, string b)
        {
            bool ha = Path.GetFileNameWithoutExtension(a) == "globe_hive", hb = Path.GetFileNameWithoutExtension(b) == "globe_hive";
            return ha != hb ? (ha ? -1 : 1) : string.CompareOrdinal(b, a); // then dandelion (a sunset horizon), clover
        }

        void Add(string url, bool fallback)
        {
            if (Path.GetFileNameWithoutExtension(url) == FlightClip) { _flightUrl = url; return; }
            _urls.Add(url);
            _fallback.Add(fallback);
        }

        bool PrimaryLeft()
        {
            for (int i = 0; i < _urls.Count; i++) if (!_fallback[i] && !_bad.Contains(i)) return true;
            return false;
        }

        int Usable()
        {
            bool primary = PrimaryLeft();
            int n = 0;
            for (int i = 0; i < _urls.Count; i++) if (!_bad.Contains(i) && _fallback[i] != primary) n++;
            return n;
        }

        int NextClip(int after)
        {
            bool primary = PrimaryLeft();
            for (int s = 1; s <= _urls.Count; s++)
            {
                int i = ((after < 0 ? -1 : after) + s) % _urls.Count;
                if (i < 0) i += _urls.Count;
                if (!_bad.Contains(i) && _fallback[i] != primary) return i;
            }
            return -1;
        }

        void StartFootage()
        {
            int i = NextClip(_cur);
            if (i < 0) { NoFootage(); return; }
            Load(i);
        }

        void Load(int i)
        {
            if (_vp == null) return;
            _vp.Stop();
            _cur = _loading = i;
            _vp.url = _urls[i];
            _vp.Prepare();
            _shotAt = Time.time;
            _swap = 1f;
        }

        void PrepareFlight()
        {
            if (_flightVp == null) return;
            _flightVp.Stop();
            _flightReady = _flightOn = _flightShown = false;
            if (_flightBad) return;
            _flightVp.url = _flightUrl;
            _flightVp.Prepare();
        }

        void StopFlight()
        {
            if (_flightVp == null) return;
            _flightVp.Stop();
            _flightReady = _flightOn = _flightShown = false;
        }

        void NoFootage()
        {
            if (!_allBadSaid) Debug.LogWarning("[VBS][Globe] no clip plays here: the painted meadow shows");
            _allBadSaid = true;
            _cur = _loading = -1;
            if (_mat != null && !_hasFrame) _mat.SetTexture(TexId, Still);
        }

        void TickFootage(float t)
        {
            if (_vp == null) return;
            if (_advance)
            {
                _advance = false;
                int i = NextClip(_cur);
                if (i < 0) NoFootage();
                else Load(i);
                return;
            }
            if (_flightOn && !_flightShown && _flightVp.isPlaying && _flightVp.frame > 0)
            {
                _flightShown = true;
                _mat.SetTexture(TexId, _flightRt);
                _vp.Stop(); // one decoder at a time
            }
            if (!_hasFrame && _vp.isPlaying && _vp.frame > 0)
            {
                _hasFrame = true;
                _mat.SetTexture(TexId, _rt);
            }
            // a new shot every few seconds, only while it sits there (not mid-dive)
            if (_state == State.Showing && _cur >= 0 && t - _shotAt > shotSeconds && Usable() > 1) Load(NextClip(_cur));
        }

        // ------------------------------------------------------------------ helpers
        static float BackOut(float x)
        {
            const float c1 = 1.4f, c3 = c1 + 1f;
            float u = x - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        /// <summary>Until the first frame (or when no clip plays here): a painted meadow, sky over clover, a low sun.</summary>
        static Texture2D Painted(int s)
        {
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { name = "VBS_GlobePainted", wrapMode = TextureWrapMode.Clamp };
            var px = new Color[s * s];
            Color skyTop = new Color(0.45f, 0.68f, 0.95f), skyLow = new Color(0.88f, 0.93f, 0.98f);
            Color grassNear = new Color(0.24f, 0.52f, 0.18f), grassFar = new Color(0.56f, 0.75f, 0.32f);
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float v = (y + 0.5f) / s, u = (x + 0.5f) / s;
                float horizon = 0.42f + Mathf.Sin(u * 6.3f) * 0.025f;
                Color c = v > horizon ? Color.Lerp(skyLow, skyTop, (v - horizon) / (1f - horizon)) : Color.Lerp(grassNear, grassFar, v / horizon);
                float sun = Mathf.Clamp01(1f - Vector2.Distance(new Vector2(u, v), new Vector2(0.68f, 0.72f)) / 0.12f);
                c = Color.Lerp(c, new Color(1f, 0.96f, 0.8f), sun * sun);
                if (v <= horizon && ((x * 7 + y * 13) % 11 == 0)) c = Color.Lerp(c, new Color(1f, 0.85f, 0.95f), 0.6f); // clover
                px[y * s + x] = c;
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>A ring of major radius `major` and tube `tube` (in units of the major radius), lying flat (XZ).</summary>
        static Mesh Torus(float major, float tube, int seg, int sides)
        {
            var verts = new Vector3[(seg + 1) * (sides + 1)];
            var tris = new int[seg * sides * 6];
            int vi = 0, ti = 0;
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                for (int j = 0; j <= sides; j++)
                {
                    float b = j / (float)sides * Mathf.PI * 2f;
                    verts[vi++] = dir * (major + Mathf.Cos(b) * tube) + Vector3.up * (Mathf.Sin(b) * tube);
                }
            }
            for (int i = 0; i < seg; i++)
            for (int j = 0; j < sides; j++)
            {
                int a0 = i * (sides + 1) + j, a1 = a0 + sides + 1;
                tris[ti++] = a0; tris[ti++] = a0 + 1; tris[ti++] = a1;
                tris[ti++] = a1; tris[ti++] = a0 + 1; tris[ti++] = a1 + 1;
            }
            var m = new Mesh { name = "VBS_GlobeRing", vertices = verts, triangles = tris };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        void Log(string msg)
        {
            if (log) Debug.Log("[VBS][Globe] " + msg);
        }
    }
}
