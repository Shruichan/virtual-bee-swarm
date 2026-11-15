using System;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using VirtualBeeSwarm.AR;
using VirtualBeeSwarm.Bloom;
using VirtualBeeSwarm.Stakes;

namespace VirtualBeeSwarm.Title
{
    /// <summary>
    /// The load-in screen (tuning title.wall, default on; env VBS_TITLE_WALL=0 = straight into the title). Before the
    /// player ever sees the title, they stand inside an opaque 360 documentary wall: the bee-monitor cylinder
    /// (<see cref="TitleWall"/>, 1.4 m out, rows sliding round them) closed above and below by a dark honeycomb dome
    /// (Resources/VBS_LoadDome) and a small "Loading the meadow..." sticker with a honey progress ring. All of it is
    /// drawn after everything else with no depth test and writes alpha 1, so no room, passthrough, hive, vine or caption
    /// shows anywhere while it covers. It follows the eyes (position only; it never turns with the head).
    ///   when      app start (from the very first frame, already opaque); the headset going back on in pre
    ///             (userPresence 1 after 0, the app resuming from a pause, focus back after 5 s or more: it closes the
    ///             moment the headset comes off, so the player never sees the room first); a group reset to pre (fades in).
    ///   holds     at least minHoldSeconds after the headset is on, then until the setup reports ready: head tracked,
    ///             rig placed in the room (ArRig.RoomChanged / MeadowLayout.HivePoseSet), meadow floor, title logo
    ///             placed, voice clips preloaded, the Hive link (or netWaitSeconds), and frame times settled (10 in a row
    ///             under 20 ms). Never longer than maxHoldSeconds. The autopilot and batch players (smoke, tests,
    ///             captures) get a short version that ignores readiness; VBS_LOAD_WALL_HOLD=s holds s seconds anyway,
    ///             VBS_LOAD_WALL_FULL=1 runs the headset rules there too.
    ///   reveal    the rows fold in, the dome burns away from the same noise, petals and pollen burst round the player
    ///             into the title room. START (or any jump out of pre) while it covers: a quick reveal. Comfort mode: a
    ///             calm fade in and out, still screens, no fold and no burst.
    ///   opening   while it covers, IntroSequence.BringClipboard holds SIGN HERE back (the crew bee's or Beeswax's
    ///             hand-over), so it flies in after the reveal, in front of where the player then looks, not behind the
    ///             wall. Not for the autopilot / batch / desktop players (short version). The hold delegate is set only
    ///             while it covers and put back as it lifts. The title globe can't be pinched and keeps its footage
    ///             paused while it covers (<see cref="Covering"/>).
    ///   cost      while it holds fully opaque, the head camera draws only the wall's layer (31): the room isn't
    ///             rendered at all behind it (VBS_LOAD_WALL_CULL=0 keeps the full scene).
    /// Logs "[VBS][LoadWall] ...".
    /// </summary>
    [DefaultExecutionOrder(-30)]
    public class LoadWall : MonoBehaviour
    {
        public static bool AutoInstall = true;
        public static LoadWall Instance { get; private set; }

        /// <summary>The room is hidden (closing or held). False from the moment it starts to lift.</summary>
        public static bool Covering => Instance != null && (Instance._state == State.Closing || Instance._state == State.Holding);

        /// <summary>Anything of it on screen (also while it lifts).</summary>
        public static bool Up => Instance != null && Instance._state != State.Off;

        [SerializeField] float minHoldSeconds = 4f;
        [SerializeField] float maxHoldSeconds = 10f;
        [SerializeField] float quickHoldSeconds = 1.2f;
        [SerializeField] float quickMaxSeconds = 3f;
        [Tooltip("Ready only after this long with everything ready (no reveal on a one-frame blip).")]
        [SerializeField] float readyHoldSeconds = 0.25f;
        [SerializeField] float netWaitSeconds = 6f;
        [SerializeField] float settleFrameMs = 20f;
        [SerializeField] int settleFrames = 10;
        [SerializeField] float closeSeconds = 0.5f;
        [SerializeField] float comfortFadeSeconds = 1.2f;
        [SerializeField] float revealSeconds = 1.15f;
        [SerializeField] float quickRevealSeconds = 0.5f;
        [SerializeField] float rowsSeconds = 0.8f;
        [SerializeField] float wallRadius = 1.4f;
        [SerializeField] int wallRows = 11;
        [SerializeField] float domeRadius = 1.75f;
        [SerializeField] float badgeDistance = 1.0f;
        [SerializeField] float badgeDownDegrees = 20f;
        [SerializeField] float badgeWidth = 0.5f;
        [Tooltip("The sticker turns after the head once it is this far off to the side (deg).")]
        [SerializeField] float badgeLazyDegrees = 22f;
        [SerializeField] bool log = true;

        const int DomeQueue = 4990, WallQueue = 4991, BadgeQueue = 4992, BurstQueue = 4993; // after everything (TMP overlay text is 4000)
        const int WallLayer = 31; // unnamed in TagManager: only the load wall uses it
        const float FocusLostSeconds = 5f, FocusRaiseSeconds = 1.5f, WaitMountCapSeconds = 30f;

        enum State { Off, Closing, Holding, Revealing }

        static readonly string[] SignalNames = { "head", "room", "meadow", "title", "voice", "hive link", "frames" };
        const int SHead = 0, SRoom = 1, SMeadow = 2, STitle = 3, SVoice = 4, SNet = 5, SFrames = 6;
        static bool s_roomSeen;

        State _state = State.Off;
        GameDirector _director;
        GameNet _net;
        GameAutopilot _autopilot;
        ArRig _rig;
        bool _built, _subscribed, _comfort, _quickReveal, _burstDone, _waitMount, _wrapped, _focusRaised, _noCull, _noDome;
        float _waitMountAt = -1f;
        Camera _culledCam;
        int _savedMask;
        System.Reflection.EventInfo _ovrMounted, _ovrUnmounted;
        Action _onMounted, _onUnmounted;
        Transform _root, _dome, _badge;
        Material _domeMat, _badgeMat;
        Texture2D _badgeTex;
        TitleWall _wall;
        GlobeBurst _burst;
        float _alpha, _fromAlpha, _stateAt, _shownAt, _holdFrom, _readySince = -1f, _progress, _badgeYaw, _clock, _focusLostAt = -1f;
        float _headSince = -1f;
        int _calmFrames, _presence = -1;
        float _presenceAt;
        readonly float[] _readyAt = new float[SignalNames.Length];
        string _why = "";
        float _envHold = -1f;
        bool _envFull;

        Func<IntroSequence, SignHereClipboard, System.Collections.IEnumerator> _innerBring, _wrappedBring;

        static readonly int AlphaId = Shader.PropertyToID("_Alpha");
        static readonly int CollapseId = Shader.PropertyToID("_Collapse");
        static readonly int ClockId = Shader.PropertyToID("_Clock");
        static readonly int ProgressId = Shader.PropertyToID("_Progress");
        static readonly int EyeHeightId = Shader.PropertyToID("_EyeHeight");

        // ------------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_roomSeen = false;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!AutoInstall || !GlobeFlags.Wall) return;
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<LoadWall>() != null) return;
            var go = new GameObject("LoadWall");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<LoadWall>();
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
            _net = FindAnyObjectByType<GameNet>();
            _autopilot = FindAnyObjectByType<GameAutopilot>(FindObjectsInactive.Include);
            _wrappedBring = BringAfterLoad;
            try
            {
                string h = Environment.GetEnvironmentVariable("VBS_LOAD_WALL_HOLD");
                if (!string.IsNullOrEmpty(h) && float.TryParse(h, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float hs))
                    _envHold = Mathf.Clamp(hs, 0f, 60f);
                _envFull = Environment.GetEnvironmentVariable("VBS_LOAD_WALL_FULL") == "1";
                _noCull = Environment.GetEnvironmentVariable("VBS_LOAD_WALL_CULL") == "0";
            }
            catch (Exception) { }
            Raise("app start", instant: true); // before the first frame renders: the room is never seen first
        }

        void OnEnable()
        {
            if (Instance != this) return;
            Application.onBeforeRender += Follow;
            if (_director != null && !_subscribed)
            {
                _director.BeatChanged += HandleBeat;
                _director.GroupResetDone += HandleGroupReset;
                _subscribed = true;
            }
            MeadowLayout.HivePoseSet += HandlePose;
            HookRig();
            HookOvr(true);
        }

        void OnDisable()
        {
            Application.onBeforeRender -= Follow;
            if (_subscribed && _director != null)
            {
                _director.BeatChanged -= HandleBeat;
                _director.GroupResetDone -= HandleGroupReset;
            }
            _subscribed = false;
            MeadowLayout.HivePoseSet -= HandlePose;
            if (_rig != null) _rig.RoomChanged -= HandleRoom;
            _rig = null;
            HookOvr(false);
            if (_state != State.Off) Off("disabled");
            Uncull();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_wrapped && IntroSequence.BringClipboard == _wrappedBring) IntroSequence.BringClipboard = _innerBring;
            Uncull();
            _wall?.Destroy();
            _burst?.Destroy();
            if (_domeMat != null) Destroy(_domeMat);
            if (_badgeMat != null) Destroy(_badgeMat);
        }

        void HookRig()
        {
            if (_rig != null || ArRig.Instance == null) return;
            _rig = ArRig.Instance;
            _rig.RoomChanged += HandleRoom;
        }

        void HandleRoom(RoomModel room) => s_roomSeen = true;
        void HandlePose(Pose pose) => s_roomSeen = true;

        /// <summary>The rig is in the room: an event seen, or the state it leaves behind (an event before we subscribed).</summary>
        static bool RoomPlaced()
        {
            if (s_roomSeen) return true;
            var rig = ArRig.Instance;
            if (rig == null) return true;
            if (rig.Room != null && rig.Room.Source != "none" && rig.Room.Source != "fake") return s_roomSeen = true;
            string pose = MeadowLayout.LastPose;
            if (MeadowLayout.Active && !string.IsNullOrEmpty(pose) && pose.IndexOf("scene default", StringComparison.Ordinal) < 0) return s_roomSeen = true;
            return false;
        }

        /// <summary>OVRManager.HMDMounted / HMDUnmounted (Meta assembly, not referenced here: by reflection).</summary>
        void HookOvr(bool on)
        {
            try
            {
                if (on && _ovrMounted == null)
                {
                    Type t = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        string n = asm.GetName().Name;
                        if (!n.StartsWith("Oculus", StringComparison.Ordinal) && !n.StartsWith("Meta", StringComparison.Ordinal)) continue;
                        t = asm.GetType("OVRManager", false);
                        if (t != null) break;
                    }
                    if (t == null) return;
                    _ovrMounted = t.GetEvent("HMDMounted");
                    _ovrUnmounted = t.GetEvent("HMDUnmounted");
                    if (_ovrMounted == null || _ovrUnmounted == null || _ovrMounted.EventHandlerType != typeof(Action)) { _ovrMounted = _ovrUnmounted = null; return; }
                    _onMounted = () => HeadsetOn("OVRManager.HMDMounted");
                    _onUnmounted = () => HeadsetOff("OVRManager.HMDUnmounted");
                    _ovrMounted.AddEventHandler(null, _onMounted);
                    _ovrUnmounted.AddEventHandler(null, _onUnmounted);
                }
                else if (!on && _ovrMounted != null)
                {
                    _ovrMounted.RemoveEventHandler(null, _onMounted);
                    _ovrUnmounted.RemoveEventHandler(null, _onUnmounted);
                    _ovrMounted = _ovrUnmounted = null;
                }
            }
            catch (Exception e)
            {
                _ovrMounted = _ovrUnmounted = null;
                Log("no OVRManager mount events (" + e.GetType().Name + ")");
            }
        }

        void HeadsetOff(string why)
        {
            if (InPre) Raise(why, instant: true, waitMount: true);
        }

        void HeadsetOn(string why)
        {
            if (Up) Mounted(why);
            else if (InPre) Raise(why, instant: true);
        }

        void Build()
        {
            if (_built) return;
            _built = true;
            var root = new GameObject("LoadWallRoot");
            root.transform.SetParent(transform, false);
            _root = root.transform;

            var domeShader = Resources.Load<Shader>("VBS_LoadDome");
            if (domeShader == null) domeShader = Shader.Find("VirtualBeeSwarm/LoadDome");
            if (domeShader != null && domeShader.isSupported)
            {
                _domeMat = new Material(domeShader) { name = "VBS_LoadDome", renderQueue = DomeQueue };
                var sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
                var go = new GameObject("LoadDome");
                go.transform.SetParent(_root, false);
                go.AddComponent<MeshFilter>().sharedMesh = sphere;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _domeMat;
                Quiet(mr);
                float meshR = sphere != null ? Mathf.Max(0.01f, sphere.bounds.extents.x) : 0.5f; // the built-in one is radius 1
                go.transform.localScale = Vector3.one * (domeRadius / meshR);
                _dome = go.transform;
            }
            else
            {
                _noDome = true; // without its caps the ring would leave the room showing above and below: no load wall at all
                Debug.LogWarning("[VBS][LoadWall] no LoadDome shader: no load wall (straight into the title)");
            }

            _wall = new TitleWall(_root, wallRadius, wallRows, WallQueue, Log);

            var badgeShader = Resources.Load<Shader>("VBS_LoadBadge");
            if (badgeShader == null) badgeShader = Shader.Find("VirtualBeeSwarm/LoadBadge");
            _badgeTex = Resources.Load<Texture2D>("load_badge");
            if (badgeShader != null && badgeShader.isSupported && _badgeTex != null)
            {
                _badgeMat = new Material(badgeShader) { name = "VBS_LoadBadge", renderQueue = BadgeQueue, mainTexture = _badgeTex };
                var go = new GameObject("LoadBadge");
                go.transform.SetParent(_root, false);
                go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _badgeMat;
                Quiet(mr);
                go.transform.localScale = new Vector3(badgeWidth, badgeWidth * _badgeTex.height / Mathf.Max(1f, _badgeTex.width), 1f);
                _badge = go.transform;
            }

            _burst = new GlobeBurst(transform, BurstQueue); // over the wall that's still burning away
            foreach (var t in _root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = WallLayer;
            _root.gameObject.SetActive(false);
        }

        static void Quiet(MeshRenderer mr)
        {
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        // ------------------------------------------------------------------ when
        bool InPre => _director == null || _director.Beat == GameBeat.Pre;

        /// <summary>Autopilot, smoke and test players, and desktop play without a headset: a short wall that doesn't wait
        /// for readiness or hold the opening (the Play-mode smoke test waits 10 s for SIGN HERE).</summary>
        bool Quick => !_envFull && (Application.isBatchMode || (_autopilot != null && _autopilot.Running) ||
                                    (!XRSettings.isDeviceActive && Application.platform != RuntimePlatform.Android));

        float MinHold => _envHold >= 0f ? _envHold : Quick ? quickHoldSeconds : minHoldSeconds;
        float MaxHold => Mathf.Max(Quick ? quickMaxSeconds : maxHoldSeconds, _envHold >= 0f ? _envHold + 3f : 0f);

        void HandleGroupReset() => Raise("group reset", instant: false);

        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            if (beat != GameBeat.Pre && Covering) Reveal("start during the load (" + GameIds.BeatName(beat) + ")", quick: true);
        }

        void OnApplicationPause(bool paused)
        {
            if (!XRSettings.isDeviceActive) return; // desktop: an unfocused window isn't a headset coming off
            if (paused) HeadsetOff("app paused (headset off)");
            else if (Up) Mounted("app resumed");
        }

        void OnApplicationFocus(bool focus)
        {
            if (!XRSettings.isDeviceActive) return;
            if (!focus) { _focusLostAt = Time.realtimeSinceStartup; return; }
            float lost = _focusLostAt >= 0f ? Time.realtimeSinceStartup - _focusLostAt : 0f;
            if (_focusLostAt >= 0f && InPre)
            {
                if (Up && (_focusRaised || lost >= FocusLostSeconds)) Mounted("focus back after " + lost.ToString("0.0") + " s");
                else if (!Up && lost >= FocusLostSeconds) Raise("focus back after " + lost.ToString("0") + " s", instant: true);
            }
            _focusLostAt = -1f;
            _focusRaised = false;
        }

        static int ReadPresence()
        {
            var dev = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!dev.isValid) return -1;
            return dev.TryGetFeatureValue(CommonUsages.userPresence, out bool on) ? (on ? 1 : 0) : -1;
        }

        void TrackPresence()
        {
            if (Now < _presenceAt) return;
            _presenceAt = Now + 0.2f;
            int p = ReadPresence();
            if (p == _presence) return;
            if (p == 0 && _presence == 1) HeadsetOff("headset off");
            else if (p == 1 && _presence == 0) HeadsetOn("headset on");
            if (p != 0) _waitMount = false;
            _presence = p;
        }

        /// <summary>The headset is on again: the load clock starts now (min hold, readiness that can change).</summary>
        void Mounted(string why)
        {
            _waitMount = false;
            if (_state == State.Revealing || _state == State.Off) return;
            _holdFrom = Now;
            _readySince = -1f;
            _headSince = -1f;
            _calmFrames = 0;
            _readyAt[SHead] = _readyAt[SFrames] = _readyAt[SNet] = -1f;
            Log(why + ": holding again (at least " + MinHold.ToString("0.0") + " s)");
        }

        /// <summary>Close round the player: at once (app start, headset off: already opaque) or with a short fade.</summary>
        public void Raise(string why, bool instant, bool waitMount = false)
        {
            if (!GlobeFlags.Wall || !InPre) return;
            Build();
            if (_noDome || _wall == null || !_wall.Usable) return;
            if (waitMount) _waitMount = true;
            if (_state == State.Closing || _state == State.Holding)
            {
                if (instant) { _alpha = 1f; _state = State.Holding; }
                return;
            }
            bool wasOff = _state == State.Off;
            _comfort = StakesFlags.Comfort;
            _why = why;
            _fromAlpha = wasOff ? 0f : _alpha;
            _alpha = instant ? 1f : _fromAlpha;
            _state = instant ? State.Holding : State.Closing;
            float now = Now;
            _stateAt = _holdFrom = now;
            if (wasOff) _shownAt = now;
            _readySince = -1f;
            _headSince = -1f;
            _calmFrames = 0;
            _progress = 0f;
            _burstDone = false;
            for (int i = 0; i < _readyAt.Length; i++) _readyAt[i] = -1f;
            if (_root == null) return;
            _root.gameObject.SetActive(true);
            Transform head = Head;
            Vector3 eye = head != null ? head.position : Vector3.up * 1.5f;
            _badgeYaw = head != null ? Yaw(head.forward) : 0f;
            _root.SetPositionAndRotation(eye, Quaternion.identity);
            if (_wall != null && _wall.Usable)
            {
                if (wasOff) { _wall.Prepare(); _wall.Play(); }
                _wall.Show(eye, Quaternion.Euler(0f, _badgeYaw, 0f));
            }
            Apply(0f);
            Follow();
            SyncOpening();
            SyncCulling();
            Log("up (" + why + (instant ? ", opaque at once" : ", closing") + (_comfort ? ", comfort" : "") + (Quick ? ", short: " + (Application.isBatchMode ? "batch player" : _autopilot != null && _autopilot.Running ? "autopilot" : "desktop") : "") +
                "): holds " + MinHold.ToString("0.0") + "-" + MaxHold.ToString("0.0") + " s" + (_waitMount ? ", from when the headset is back on" : ""));
        }

        void Reveal(string why, bool quick)
        {
            if (_state != State.Closing && _state != State.Holding) return;
            _state = State.Revealing;
            _stateAt = Now;
            _quickReveal = quick;
            _fromAlpha = _alpha;
            var sb = new StringBuilder("lift (" + why + ") " + (Now - _shownAt).ToString("0.00") + " s after it went up, held " +
                                       (Now - _holdFrom).ToString("0.00") + " s; ready (s into the hold):");
            for (int i = 0; i < SignalNames.Length; i++)
                sb.Append(" " + SignalNames[i] + (_readyAt[i] >= 0f ? " +" + (_readyAt[i] - _holdFrom).ToString("0.0") : " NO"));
            Log(sb.ToString());
            SyncCulling();
            SyncOpening();
        }

        void Off(string why)
        {
            _state = State.Off;
            if (_root != null) _root.gameObject.SetActive(false);
            _wall?.Stop();
            SyncCulling();
            SyncOpening();
            Log("gone (" + why + ")");
        }

        // ------------------------------------------------------------------ the room isn't drawn behind it
        void SyncCulling()
        {
            Camera want = !_noCull && _state == State.Holding && _alpha >= 0.999f && _root != null ? Camera.main : null;
            if (_culledCam != want) Uncull();
            if (want == null || _culledCam != null) return;
            _savedMask = want.cullingMask;
            want.cullingMask = 1 << WallLayer;
            _culledCam = want;
        }

        void Uncull()
        {
            if (_culledCam != null && _culledCam.cullingMask == 1 << WallLayer) _culledCam.cullingMask = _savedMask;
            _culledCam = null;
        }

        // ------------------------------------------------------------------ the opening waits
        /// <summary>While it covers (full version only), IntroSequence.BringClipboard is ours: PreClipboard waits in it,
        /// then runs the opening's own delegate, or (none: the crew bee) parks SIGN HERE itself once ours is put back.</summary>
        void SyncOpening()
        {
            var cur = IntroSequence.BringClipboard;
            if (Covering && !Quick)
            {
                if (cur == _wrappedBring) return;
                _innerBring = cur; // OpeningDirector's Beeswax hand-over, or null (the crew bee brings it)
                IntroSequence.BringClipboard = _wrappedBring;
                _wrapped = true;
            }
            else if (_wrapped)
            {
                if (cur == _wrappedBring) IntroSequence.BringClipboard = _innerBring;
                _wrapped = false;
            }
        }

        System.Collections.IEnumerator BringAfterLoad(IntroSequence intro, SignHereClipboard clipboard)
        {
            float from = Time.realtimeSinceStartup;
            bool waited = false;
            while (Covering && !Quick && Time.realtimeSinceStartup - from < MaxHold + 6f)
            {
                waited = true;
                yield return null;
            }
            if (waited) Log("SIGN HERE comes after the load (" + (Time.realtimeSinceStartup - from).ToString("0.0") + " s held)");
            var inner = _innerBring;
            if (inner != null) yield return inner(intro, clipboard);
        }

        // ------------------------------------------------------------------ frame
        void Update()
        {
            SyncOpening();
            HookRig();
            TrackPresence();
            if (_focusLostAt >= 0f && !Up && InPre && XRSettings.isDeviceActive && Time.realtimeSinceStartup - _focusLostAt >= FocusRaiseSeconds)
            {
                _focusRaised = true; // close while the app still renders: the last frame before a pause is the wall, not the room
                Raise("focus lost", instant: true, waitMount: true);
            }
            if (_state == State.Off) return;
            float now = Now, dt = Dt;
            _clock += dt;
            if (!InPre && Covering) Reveal("not in pre", quick: true);
            if (!_waitMount) _waitMountAt = -1f;
            else if (_waitMountAt < 0f) _waitMountAt = now;
            else if (now - _waitMountAt > WaitMountCapSeconds)
            {
                _waitMount = false; // presence stuck at "off" while the app runs: load on rather than hold for ever
                Log("no headset-on after " + WaitMountCapSeconds.ToString("0") + " s: holding from now");
            }
            if (_waitMount) _holdFrom = now;

            switch (_state)
            {
                case State.Closing:
                {
                    float k = Mathf.Clamp01((now - _stateAt) / (_comfort ? comfortFadeSeconds : closeSeconds));
                    _alpha = Mathf.Lerp(_fromAlpha, 1f, k * k * (3f - 2f * k));
                    if (k >= 1f)
                    {
                        _state = State.Holding;
                        _holdFrom = now;
                    }
                    TickReadiness(now, dt);
                    Apply(0f);
                    break;
                }
                case State.Holding:
                {
                    _alpha = 1f;
                    bool ready = TickReadiness(now, dt);
                    float held = now - _holdFrom;
                    if (_waitMount) { }
                    else if (held >= MaxHold) Reveal("cap " + MaxHold.ToString("0.0") + " s", quick: false);
                    else if (held >= MinHold && (Quick || ready && _readySince >= 0f && now - _readySince >= readyHoldSeconds))
                        Reveal(Quick ? "short version" : "all ready", quick: false);
                    Apply(0f);
                    SyncCulling();
                    break;
                }
                case State.Revealing:
                {
                    float dur = _comfort ? comfortFadeSeconds : _quickReveal ? quickRevealSeconds : revealSeconds;
                    float k = Mathf.Clamp01((now - _stateAt) / dur);
                    _progress = Mathf.MoveTowards(_progress, 1f, dt * 4f);
                    if (_comfort) _alpha = _fromAlpha * (1f - k * k * (3f - 2f * k));
                    else _alpha = _fromAlpha;
                    if (!_comfort && !_burstDone && k >= 0.35f) // drawn over the wall (BurstQueue): petals out of the burning screens
                    {
                        _burstDone = true;
                        Transform head = Head;
                        if (head != null) _burst.Emit(head.position, 0.55f, 70);
                    }
                    Apply(_comfort ? 0f : k);
                    if (k >= 1f) Off("revealed the room");
                    break;
                }
            }
        }

        /// <summary>Which setup steps report ready; true once all do. Also moves the progress ring.</summary>
        bool TickReadiness(float now, float dt)
        {
            bool xr = XRSettings.isDeviceActive;
            Transform head = Head;
            bool tracked = head != null && head.position.sqrMagnitude > 1e-4f;
            if (!tracked) _headSince = -1f;
            else if (_headSince < 0f) _headSince = now;
            bool capture = Time.captureDeltaTime > 0f;
            if (!capture && dt * 1000f < settleFrameMs) _calmFrames++;
            else if (!capture) _calmFrames = 0;
            float held = now - _holdFrom;

            int n = 0;
            for (int i = 0; i < SignalNames.Length; i++)
            {
                bool ok;
                switch (i)
                {
                    case SHead: ok = !xr || (_headSince >= 0f && now - _headSince >= 1f); break;
                    case SRoom: ok = !xr || RoomPlaced(); break;
                    case SMeadow: ok = !MeadowLayout.Active || MeadowField.Instance == null || MeadowField.Instance.HasFloor; break;
                    case STitle: ok = !TitleFlags.On || TitleDirector.LogoSpot(out _, out _, out _, out _); break;
                    case SVoice: ok = _net == null || _net.VoicePreloadDone; break;
                    case SNet: ok = _net == null || _net.ForceOffline || _net.Online || held >= netWaitSeconds; break;
                    default: ok = capture || _calmFrames >= settleFrames; break;
                }
                if (ok && _readyAt[i] < 0f) _readyAt[i] = now;
                if (!ok) _readyAt[i] = -1f; // can drop again (a hitch, the head lost)
                if (ok) n++;
            }
            bool all = n == SignalNames.Length;
            if (!all) _readySince = -1f;
            else if (_readySince < 0f) _readySince = now;

            // the ring: never ahead of the minimum hold, never back; ready steps carry it to the end
            float min = Mathf.Max(0.1f, MinHold);
            float goal = Mathf.Min(Mathf.Clamp01(held / min), Quick ? 1f : 0.12f + 0.88f * n / SignalNames.Length);
            if (!Quick && !all) goal = Mathf.Min(goal, 0.94f);
            _progress = Mathf.Max(_progress, Mathf.MoveTowards(_progress, goal, dt * 0.9f));
            return all;
        }

        /// <summary>Push alpha, collapse, progress and the clock into the materials.</summary>
        void Apply(float collapse)
        {
            float speed = _comfort ? 0f : 1f + 5f * collapse;
            float rows = _comfort ? 1f : Mathf.Clamp01((Now - _shownAt) / rowsSeconds); // comfort: no motion
            rows = rows * rows * (3f - 2f * rows);
            float fold = collapse * collapse * (3f - 2f * collapse);
            if (_wall != null) _wall.Tick(Dt, _alpha, rows, fold, speed);
            if (_domeMat != null)
            {
                _domeMat.SetFloat(AlphaId, _alpha);
                _domeMat.SetFloat(CollapseId, Mathf.Clamp01(collapse * 1.08f)); // burns a touch ahead of the rows
                _domeMat.SetFloat(ClockId, _clock);
                Transform head = Head;
                float floorY = MeadowLayout.FloorY;
                if (head != null) _domeMat.SetFloat(EyeHeightId, Mathf.Clamp(head.position.y - floorY, 0.9f, 2.1f));
            }
            if (_badgeMat != null)
            {
                float badgeA = _alpha * (1f - Mathf.Clamp01(collapse / 0.3f));
                _badgeMat.SetFloat(AlphaId, badgeA);
                _badgeMat.SetFloat(ProgressId, _progress);
                _badgeMat.SetFloat(ClockId, _clock);
                if (_badge != null) _badge.gameObject.SetActive(badgeA > 0.002f);
            }
        }

        /// <summary>Right before rendering (after the XR pose update): round the eyes, the sticker in front of them.</summary>
        void Follow()
        {
            if (_state == State.Off || _root == null) return;
            Transform head = Head;
            if (head == null) return;
            Vector3 eye = head.position;
            _root.SetPositionAndRotation(eye, Quaternion.identity);
            _wall?.Follow(eye);
            if (_badge == null) return;
            float yaw = Yaw(head.forward);
            float off = Mathf.DeltaAngle(_badgeYaw, yaw);
            if (Mathf.Abs(off) > badgeLazyDegrees) _badgeYaw += (Mathf.Abs(off) - badgeLazyDegrees) * Mathf.Sign(off);
            else _badgeYaw = Mathf.MoveTowardsAngle(_badgeYaw, yaw, Dt * 12f * Mathf.Abs(off) / badgeLazyDegrees);
            Quaternion look = Quaternion.Euler(badgeDownDegrees, _badgeYaw, 0f);
            _badge.SetPositionAndRotation(eye + look * Vector3.forward * badgeDistance, look);
        }

        static float Yaw(Vector3 fwd)
        {
            fwd.y = 0f;
            return fwd.sqrMagnitude > 1e-6f ? Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg : 0f;
        }

        /// <summary>Real time on the headset; the capture clock in captures (fixed frame rate, slower than real time).</summary>
        static float Now => Time.captureDeltaTime > 0f ? Time.time : Time.unscaledTime;
        static float Dt => Time.captureDeltaTime > 0f ? Time.deltaTime : Time.unscaledDeltaTime;

        Transform Head
        {
            get
            {
                var d = BeeDelivery.Instance;
                if (d != null && d.Head != null) return d.Head;
                return Camera.main != null ? Camera.main.transform : null;
            }
        }

        void Log(string msg)
        {
            if (log) Debug.Log("[VBS][LoadWall] " + msg);
        }
    }
}
