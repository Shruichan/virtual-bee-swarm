using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace VirtualBeeSwarm.AR
{
    /// <summary>
    /// The AR layer's one entry point (PLAN.md Concept v3.1 "AR first", tasks V2-V5, V7-V10). The scene builder adds it
    /// at GameRig/Systems/AR; everything else is created at runtime, and every piece has a tuning.json switch (ArTuning).
    ///   Room:       RoomModel from FakeRoomSource (desktop/Linux) or PlacementRoomSource (headset).
    ///   Placement:  InstantPlacement (aim + pinch, EnvironmentRaycastManager) on top of the old HivePlacement
    ///               (grab the plate, two-point edge, C key), which stays the fallback and the override.
    ///   Height:     after ANY placement, one downward raycast from 0.3 m above the hive; a flat hit (normal.y > 0.9)
    ///               within 5 cm of the placed height becomes the height (ar.heightSnap).
    ///   Window:     after any placement, one horizontal raycast from the eye toward the hive; a wall within
    ///               ar.wallMaxDistance (3 m) of the hive, facing the player within 20 deg, gets the meadow window
    ///               (4 cm off it, centre at clamp(top + 0.55, 1.2, 1.7), width clamp(0.5 x distance, 0.9, 1.6));
    ///               no wall = free-standing 0.7 m behind the hive, 1.0 m wide (ar.windowOnWall; off = today's layout).
    ///   Shadows:    ContactShadows + Touchdown (ar.shadows).
    ///   Landing:    TableLanding (ar.landing).
    ///   Occlusion:  IArDepth (EnvironmentDepthManager) per ar.occlusion, only if supported; ArMaterials sets the
    ///               per-material _Occlude / bias once the shader has the occlusion edit.
    ///   Self-test:  "[VBS][AR] ..." lines (model, USE_SCENE, depth, raycast, placement path, window wall, flags);
    ///               SelfTestText has the same for the HUD / Board. On Linux every device line says n/a.
    /// The Meta parts (raycaster, depth, headset info) come from VirtualBeeSwarm.AR (AR/Meta) through ArPlatform; the
    /// core never names a Meta type, so the Linux player and the desktop editor run all of this against the fakes.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class ArRig : MonoBehaviour
    {
        [SerializeField] GameDirector director;
        [SerializeField] HivePlacement placement;
        [SerializeField] FutureWindow window;
        [SerializeField] HiveBody hive;
        [SerializeField] SwarmSystem swarm;
        [SerializeField] DesktopTestCamera desktopCamera;
        [Tooltip("Desktop only: the fake table (empty = found by name 'FakeTable').")]
        [SerializeField] Transform fakeTable;

        [Header("Height snap")]
        [SerializeField] float snapCastAbove = 0.3f;
        [SerializeField] float snapWindow = 0.05f;
        [SerializeField] float snapRetrySeconds = 1.5f;

        [Header("Window on the wall")]
        [SerializeField] float wallRetrySeconds = 3f;
        [SerializeField] float wallMaxAngle = 20f;
        [SerializeField] float wallOffset = 0.04f;
        [SerializeField] float freeStandingBehind = 0.7f;
        [SerializeField] float freeStandingWidth = 1.0f;

        public static ArRig Instance { get; private set; }

        public RoomModel Room { get; } = new RoomModel();
        public IArProvider Provider { get; private set; }
        public IEnvRaycaster Raycaster { get; private set; }
        public IArDepth Depth { get; private set; }
        public HivePlacement Placement => placement;
        public GameDirector Director => director;
        public Transform TableAnchor => _anchor;
        public bool DesktopMode { get; private set; }
        public string PlacementPath { get; private set; } = "none";
        public string WindowState { get; private set; } = "table (not placed yet)";
        public string HeightSnapState { get; private set; } = "not run";
        public string SelfTestText { get; private set; } = "";
        public ContactShadows Shadows { get; private set; }
        public TableLanding Landing { get; private set; }
        public InstantPlacement Instant { get; private set; }

        /// <summary>After every placement (room rebuilt, window placed).</summary>
        public event Action<RoomModel> RoomChanged;

        Transform _anchor;
        float _inset = 0.22f;
        Pose _lastPose;
        bool _sawHolding;
        int _lastPoints;
        bool _pointsWentTwo;
        float _settleAt = -1f;
        string _pendingPath;
        float _snapUntil = -1f, _wallUntil = -1f;
        RoomModel.Wall _foundWall;
        Transform _windowHome, _wallAnchor;
        Vector3 _winLocalPos, _winLocalScale;
        Quaternion _winLocalRot;
        Vector2 _pane = new Vector2(0.5f, 0.33f);
        float _startedAt;
        bool _deviceLinesLogged;
        ArHit _ahead;
        string _aheadState = "n/a";
        string _depthState = "n/a";
        MethodInfo _placeAt;
        FieldInfo _pointsField, _markerAField, _markerBField;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------ setup
        void Awake()
        {
            Instance = this;
            ArTuning.EnsureLoaded();
            if (director == null) director = FindAnyObjectByType<GameDirector>();
            if (placement == null) placement = FindAnyObjectByType<HivePlacement>();
            if (window == null) window = FindAnyObjectByType<FutureWindow>(FindObjectsInactive.Include);
            if (hive == null) hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            if (swarm == null) swarm = FindAnyObjectByType<SwarmSystem>(FindObjectsInactive.Include);
            if (desktopCamera == null) desktopCamera = FindAnyObjectByType<DesktopTestCamera>(FindObjectsInactive.Include);
            if (fakeTable == null)
            {
                var go = GameObject.Find("FakeTable");
                if (go != null) fakeTable = go.transform;
            }
            _anchor = placement != null ? (placement.transform.parent != null ? placement.transform.parent : placement.transform) : null;

            var t = typeof(HivePlacement);
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            _placeAt = t.GetMethod("PlaceAt", inst, null, new[] { typeof(Pose) }, null);
            _pointsField = t.GetField("_points", inst);
            _markerAField = t.GetField("markerA", inst);
            _markerBField = t.GetField("markerB", inst);
            var insetField = t.GetField("inset", inst);
            if (insetField != null && placement != null) _inset = (float)insetField.GetValue(placement);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            ArTuning.Changed -= OnTuningChanged;
            if (director != null) director.BeatChanged -= OnBeat;
        }

        void Start()
        {
            _startedAt = Time.realtimeSinceStartup;
            Provider = ArPlatform.Provider;
            bool xr = XRSettings.isDeviceActive;
#if UNITY_ANDROID && !UNITY_EDITOR
            xr = true;
#endif
            DesktopMode = desktopCamera != null ? desktopCamera.DesktopMode : !xr;

            if (DesktopMode) Raycaster = new FakeEnvRaycaster(Room);
            else if (Provider != null)
            {
                try { Raycaster = Provider.CreateRaycaster(gameObject); }
                catch (Exception e) { Debug.LogWarning("[VBS][AR] raycaster unavailable: " + e.Message); }
                try { Depth = Provider.CreateDepth(gameObject); }
                catch (Exception e) { Debug.LogWarning("[VBS][AR] depth unavailable: " + e.Message); }
            }

            // window home (today's layout) + its pane size
            if (window != null)
            {
                var wt = window.transform;
                _windowHome = wt.parent;
                _winLocalPos = wt.localPosition;
                _winLocalRot = wt.localRotation;
                _winLocalScale = wt.localScale;
                var pane = wt.Find("Portal/FallbackPane");
                if (pane != null) _pane = new Vector2(Mathf.Abs(pane.localScale.x), Mathf.Abs(pane.localScale.y));
                var wa = new GameObject("WallAnchor");
                wa.transform.SetParent(_anchor != null && _anchor.parent != null ? _anchor.parent : null, false);
                _wallAnchor = wa.transform;
            }

            var shadowsGo = new GameObject("ContactShadows");
            shadowsGo.transform.SetParent(transform, false);
            Shadows = shadowsGo.AddComponent<ContactShadows>();
            Shadows.Configure(_anchor);
            Shadows.RegisterDefaults();

            var landingGo = new GameObject("TableLanding");
            landingGo.transform.SetParent(transform, false);
            Landing = landingGo.AddComponent<TableLanding>();
            Landing.Init(this, swarm, hive);

            var instantGo = new GameObject("InstantPlacement");
            instantGo.transform.SetParent(transform, false);
            Instant = instantGo.AddComponent<InstantPlacement>();
            Instant.Init(this);

            ArTuning.Changed += OnTuningChanged;
            if (director != null) director.BeatChanged += OnBeat;

            RebuildRoom();
            ApplyOcclusion();
            if (_anchor != null) _lastPose = new Pose(_anchor.position, _anchor.rotation);
            LogLine("model " + HeadsetLine());
            LogLine("USE_SCENE granted=" + PermissionLine());
            LogLine("flags " + ArTuning.Describe());
            LogLine("room " + Room.Describe());
            if (DesktopMode) AfterPlacement("desktop (fake table)");
            else if (_anchor != null) AfterPlacement("initial headset pose");
            UpdateSelfTestText();
        }

        // ------------------------------------------------------------ frame
        void Update()
        {
            if (_anchor == null) return;
            float now = Time.time;
            bool pre = director == null || director.Beat == GameBeat.Pre;

            if (Raycaster != null)
            {
                bool want = pre || _snapUntil >= 0f || _wallUntil >= 0f;
                if (Raycaster.Enabled != want)
                {
                    try { Raycaster.Enabled = want; }
                    catch (Exception e) { Debug.LogWarning("[VBS][AR] raycaster enable failed: " + e.Message); Raycaster = null; }
                }
            }

            WatchPlacement(now);
            if (_snapUntil >= 0f) TrySnap(now);
            if (_snapUntil < 0f && _wallUntil >= 0f) TryWall(now);
            DeviceSelfTest();
        }

        void WatchPlacement(float now)
        {
            if (placement == null) return;
            if (placement.Holding) _sawHolding = true;
            int pts = placement.PointsTaken;
            if (_lastPoints == 1 && pts == 0) _pointsWentTwo = true;
            _lastPoints = pts;

            var p = new Pose(_anchor.position, _anchor.rotation);
            if ((p.position - _lastPose.position).sqrMagnitude > 1e-6f || Quaternion.Angle(p.rotation, _lastPose.rotation) > 0.5f)
            {
                _lastPose = p;
                _settleAt = now + 0.2f;
            }
            if (_settleAt >= 0f && now >= _settleAt && !placement.Holding)
            {
                _settleAt = -1f;
                string path = _pendingPath ??
                              (_sawHolding ? "grab" : _pointsWentTwo ? "two-point" : DesktopMode ? "desktop key C" : "auto (in front of the player)");
                AfterPlacement(path);
            }
        }

        /// <summary>A new table pose: rebuild the room, then (raycasts permitting) snap the height and find the wall.</summary>
        void AfterPlacement(string path)
        {
            PlacementPath = path;
            _pendingPath = null;
            _sawHolding = false;
            _pointsWentTwo = false;
            float now = Time.time;
            bool canCast = Raycaster != null && Raycaster.IsSupported;
            _snapUntil = ArTuning.HeightSnap && canCast ? now + snapRetrySeconds : -1f;
            HeightSnapState = !ArTuning.HeightSnap ? "off (flag)" : canCast ? "pending" : "n/a (no raycast)";
            _foundWall = default;
            _wallUntil = now + (canCast && ArTuning.WindowOnWall ? wallRetrySeconds : 0f);
            RebuildRoom();
            if (Bloom.MeadowLayout.Active && (!canCast || !ArTuning.WindowOnWall))
            {
                _wallUntil = -1f;
                PlaceWindow("no wall placement");
                RoomChanged?.Invoke(Room);
            }
            LogLine("placement path=" + path + " at " + V(_anchor.position));
        }

        // ------------------------------------------------------------ height snap (V4)
        void TrySnap(float now)
        {
            if (Raycaster == null || !Raycaster.Enabled) { if (now > _snapUntil) Finish("raycaster off"); return; }
            Vector3 c = hive != null ? hive.transform.position : _anchor.position;
            float placedY = _anchor.position.y;
            var ray = new Ray(new Vector3(c.x, placedY + snapCastAbove, c.z), Vector3.down);
            Raycaster.Raycast(ray, snapCastAbove + 0.5f, out var hit);
            if (hit.status == ArHitStatus.NotReady) { if (now > _snapUntil) Finish("not ready after " + snapRetrySeconds.ToString("0.0", C) + " s"); return; }
            if (hit.status != ArHitStatus.Hit) { Finish("no hit (" + hit.status + ")"); return; }
            if (hit.normal.y <= 0.9f) { Finish("hit not flat (normal.y=" + hit.normal.y.ToString("0.00", C) + ")"); return; }
            float dy = hit.point.y - placedY;
            if (Mathf.Abs(dy) > snapWindow) { Finish("hit " + (dy * 100f).ToString("0.0", C) + " cm away: kept the placed height"); return; }
            if (Mathf.Abs(dy) > 0.001f)
                ApplyPose(new Pose(new Vector3(_anchor.position.x, hit.point.y, _anchor.position.z), _anchor.rotation), announce: false);
            Finish("snapped " + (dy * 100f).ToString("+0.0;-0.0;0.0", C) + " cm to y=" + hit.point.y.ToString("0.000", C));

            void Finish(string what)
            {
                _snapUntil = -1f;
                HeightSnapState = what;
                LogLine("height snap: " + what);
                if (_wallUntil >= 0f) _wallUntil = Mathf.Max(_wallUntil, Time.time + 0.5f);
            }
        }

        // ------------------------------------------------------------ window on the wall (V8)
        void TryWall(float now)
        {
            bool done = true;
            string why = "";
            if (ArTuning.WindowOnWall && Raycaster != null && Raycaster.IsSupported && Raycaster.Enabled)
            {
                var cam = Camera.main;
                Vector3 eye = cam != null ? cam.transform.position : _anchor.position + Vector3.up * 0.6f - _anchor.forward * 0.6f;
                eye.y = Mathf.Clamp(eye.y, 1.1f, 1.7f);
                Vector3 hivePos = hive != null ? hive.transform.position : _anchor.position;
                Vector3 dir = hivePos - eye;
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-4f) dir = Flat(_anchor.forward);
                dir.Normalize();
                float toHive = Vector3.Distance(Flat2(eye), Flat2(hivePos));
                Raycaster.Raycast(new Ray(eye, dir), toHive + ArTuning.WallMaxDistance + 0.5f, out var hit);
                if (hit.status == ArHitStatus.NotReady && now < _wallUntil) done = false;
                else if (hit.status != ArHitStatus.Hit) why = "no wall hit (" + hit.status + ")";
                else
                {
                    Vector3 n = hit.normal;
                    float angle = Vector3.Angle(-dir, new Vector3(n.x, 0f, n.z));
                    float fromHive = Vector3.Distance(Flat2(hit.point), Flat2(hivePos));
                    if (Mathf.Abs(n.y) >= 0.2f) why = "hit not vertical (normal.y=" + n.y.ToString("0.00", C) + ")";
                    else if (hit.normalConfidence <= 0.5f) why = "normal confidence " + hit.normalConfidence.ToString("0.00", C);
                    else if (angle > wallMaxAngle) why = "wall at " + angle.ToString("0", C) + " deg (max " + wallMaxAngle.ToString("0", C) + ")";
                    else if (fromHive > ArTuning.WallMaxDistance) why = "wall " + fromHive.ToString("0.00", C) + " m behind the hive (max " + ArTuning.WallMaxDistance.ToString("0.0", C) + ")";
                    else
                    {
                        float dist = Vector3.Distance(Flat2(eye), Flat2(hit.point));
                        float width = Mathf.Clamp(0.5f * dist, 0.9f, 1.6f);
                        _foundWall = new RoomModel.Wall
                        {
                            valid = true, point = hit.point, normal = new Vector3(n.x, 0f, n.z).normalized,
                            halfWidth = width * 0.5f, distance = dist,
                        };
                        _foundWall.right = Vector3.Cross(Vector3.up, _foundWall.normal).normalized;
                        why = "found: " + dist.ToString("0.00", C) + " m from the eye, " + fromHive.ToString("0.00", C) + " m behind the hive, angle " + angle.ToString("0", C) + " deg";
                    }
                }
            }
            else if (!ArTuning.WindowOnWall) why = "flag off";
            else why = Raycaster == null || !Raycaster.IsSupported ? "no raycast" : "raycaster off";

            if (!done) return;
            _wallUntil = -1f;
            RebuildRoom();
            PlaceWindow(why);
            RoomChanged?.Invoke(Room);
            LogLine("room " + Room.Describe());
            UpdateSelfTestText();
        }

        void RebuildRoom()
        {
            if (DesktopMode)
            {
                FakeRoomSource.Build(Room, fakeTable, _anchor);
                if (_foundWall.valid) Room.WindowWall = _foundWall; // what the (fake) raycast saw; else the fake wall stays
            }
            else PlacementRoomSource.Build(Room, _anchor, _inset, _foundWall, 0f);
        }

        void PlaceWindow(string why)
        {
            if (window == null || _wallAnchor == null) return;
            var wt = window.transform;
            bool meadow = Bloom.MeadowLayout.Active;
            if (!ArTuning.WindowOnWall && !meadow)
            {
                if (wt.parent != _windowHome)
                {
                    wt.SetParent(_windowHome, false);
                    wt.localPosition = _winLocalPos;
                    wt.localRotation = _winLocalRot;
                    wt.localScale = _winLocalScale;
                }
                WindowState = "on the table (ar.windowOnWall off)";
                LogLine("window " + WindowState);
                return;
            }
            float top = _anchor.position.y;
            float cy = Mathf.Clamp(top + 0.55f, 1.2f, 1.7f);
            if (meadow) cy = Bloom.MeadowLayout.WindowCentreY;
            Vector3 pos;
            Quaternion rot;
            float width;
            bool usingWall = _foundWall.valid && ArTuning.WindowOnWall;
            if (usingWall)
            {
                Vector3 n = _foundWall.normal;
                pos = _foundWall.point + n * wallOffset;
                pos.y = cy;
                rot = Quaternion.LookRotation(-n, Vector3.up); // +Z into the wall, away from the player (as on the table)
                width = _foundWall.halfWidth * 2f;
                if (meadow) width = Bloom.MeadowLayout.WindowWidth;
                WindowState = "on the wall (" + why + "), width " + width.ToString("0.00", C) + " m";
            }
            else
            {
                Vector3 fwd = Flat(_anchor.forward);
                Vector3 hivePos = hive != null ? hive.transform.position : _anchor.position;
                pos = hivePos + fwd * (meadow ? Bloom.MeadowLayout.WindowBehindHive : freeStandingBehind);
                pos.y = cy;
                rot = Quaternion.LookRotation(fwd, Vector3.up);
                width = meadow ? Bloom.MeadowLayout.WindowWidth : freeStandingWidth;
                WindowState = "free-standing " + freeStandingBehind.ToString("0.0", C) + " m behind the hive (" + why + ")";
            }
            if (wt.parent != _wallAnchor) wt.SetParent(_wallAnchor, false);
            wt.SetPositionAndRotation(pos, rot);
            float k = width / Mathf.Max(0.05f, _pane.x);
            if (meadow)
            {
                float floor = Bloom.MeadowLayout.FloorY + 0.3f;
                float ceiling = Mathf.Max(floor + 0.1f, Room.CeilingOrDefault - 0.12f);
                float paneHeight = Mathf.Max(0.05f, _pane.y);
                float clearBottom = Mathf.Max(floor, Bloom.MeadowLayout.WindowBottomY(pos));
                float clearScale = (ceiling - clearBottom) / paneHeight;
                if (clearScale >= 0.9f / Mathf.Max(0.05f, _pane.x))
                {
                    k = Mathf.Min(k, clearScale);
                    float half = k * paneHeight * 0.5f;
                    pos.y = Mathf.Clamp(pos.y, clearBottom + half, Mathf.Max(clearBottom + half, ceiling - half));
                    WindowState += ", above the hive";
                }
                else
                {
                    // A side position beside the hive is closer and asks for a smaller head turn than a far wall shift.
                    Vector3 fwd = Flat(_anchor.forward);
                    Quaternion nearRotation = Quaternion.LookRotation(fwd, Vector3.up);
                    Vector3 side = nearRotation * Vector3.right;
                    Vector3 hivePos = hive != null ? hive.transform.position : _anchor.position;
                    Vector3 near = hivePos + fwd * 0.2f;
                    var camera = Camera.main;
                    float eyeY = camera != null ? camera.transform.position.y : cy;
                    float paneWidth = Mathf.Max(0.05f, _pane.x);
                    k = Mathf.Min(k, 1.2f / paneWidth, (ceiling - floor) / paneHeight);
                    float half = k * paneHeight * 0.5f;
                    near.y = Mathf.Clamp(eyeY + 0.1f, floor + half, Mathf.Max(floor + half, ceiling - half));
                    if (Bloom.MeadowLayout.TryWindowBesideHive(near, side, paneWidth * k, half, out var visible))
                    {
                        pos = visible;
                        rot = nearRotation;
                        usingWall = false;
                        WindowState = "free-standing beside the hive (wall or roof clearance)";
                    }
                    else
                    {
                        pos.y = Mathf.Clamp(pos.y, floor + half, Mathf.Max(floor + half, ceiling - half));
                        WindowState += ", limited by room clearance";
                    }
                }
                wt.SetPositionAndRotation(pos, rot);
            }
            wt.localScale = _winLocalScale * k;
            if (Room.HasWall && usingWall)
                Room.ComputeWallSpots(pos, wt.right, wt.up, _pane * k * 0.5f, _foundWall.normal);
            LogLine("window " + WindowState);
        }

        // ------------------------------------------------------------ placement plumbing
        /// <summary>InstantPlacement: the hive rig goes to this pose (front of the hive facing away from the player).</summary>
        public void PlaceFromRaycast(Pose pose)
        {
            _pendingPath = "raycast (aim + pinch)";
            ApplyPose(pose, announce: true);
            _settleAt = Time.time; // no need to wait: it's one jump
        }

        void ApplyPose(Pose pose, bool announce)
        {
            if (_anchor == null) return;
            pose = Bloom.MeadowLayout.Grounded(pose);
            if (_placeAt != null && placement != null)
            {
                _placeAt.Invoke(placement, new object[] { pose });
            }
            else if (placement != null)
            {
                // HivePlacement has no PlaceAt yet: the same pose through its public two-point method (it saves the pose
                // for the session), then fix rotation/position if its head-facing rule flipped it.
                Vector3 fwd = Flat(pose.rotation * Vector3.forward);
                Vector3 right = Vector3.Cross(Vector3.up, fwd);
                Vector3 edgeMid = pose.position - fwd * _inset;
                placement.PlaceOnEdge(edgeMid - right * 0.2f, edgeMid + right * 0.2f);
            }
            if ((_anchor.position - pose.position).sqrMagnitude > 1e-6f || Quaternion.Angle(_anchor.rotation, pose.rotation) > 0.5f)
                _anchor.SetPositionAndRotation(pose.position, pose.rotation);
            if (!announce) _lastPose = new Pose(_anchor.position, _anchor.rotation);
        }

        /// <summary>The aim-and-pinch pinch must not also count as a two-point calibration point.</summary>
        public void ConsumeCalibrationPinch()
        {
            if (placement == null) return;
            if (_pointsField != null && (int)_pointsField.GetValue(placement) == 1)
            {
                _pointsField.SetValue(placement, 0);
                HideMarker(_markerAField);
                HideMarker(_markerBField);
            }
            _lastPoints = 0;
        }

        void HideMarker(FieldInfo f)
        {
            if (f?.GetValue(placement) is Transform m && m != null && m.gameObject.activeSelf) m.gameObject.SetActive(false);
        }

        public Vector3 HeadForward()
        {
            var cam = Camera.main;
            return cam != null ? cam.transform.forward : Vector3.forward;
        }

        // ------------------------------------------------------------ beats, tuning, occlusion
        void OnBeat(GameBeat prev, GameBeat beat)
        {
            // one line per beat: what the AR layer did during the beat that just ended (the autopilot log reads it)
            LogLine("beat " + prev + " -> " + beat + ": landing gate=" + (Landing != null && Landing.Gate) +
                    " landed=" + (Landing != null ? Landing.LandedCount : 0) + " flying=" + (Landing != null ? Landing.FlyingCount : 0) +
                    " landings=" + (Landing != null ? Landing.TotalLandings : 0) + " shadows=" + (Shadows != null ? Shadows.VisibleCount : 0) +
                    " touchdowns=" + Touchdown.Count + " raycaster=" + (Raycaster != null && Raycaster.Enabled ? "on" : "off"));
            if (beat == GameBeat.Pre && Landing != null) Landing.ResetAll();
        }

        void OnTuningChanged()
        {
            ApplyOcclusion();
            if (_anchor != null) AfterPlacement(PlacementPath.Replace(" (tuning changed)", "") + " (tuning changed)");
            LogLine("flags " + ArTuning.Describe());
            UpdateSelfTestText();
        }

        void ApplyOcclusion()
        {
            if (Depth == null) { _depthState = "n/a (no depth provider on this platform)"; return; }
            try
            {
                if (!Depth.IsSupported) { _depthState = "not supported"; return; }
                Depth.SetMode(ArTuning.OcclusionMode);
                int n = ArMaterials.Apply(_anchor != null && _anchor.parent != null ? _anchor.parent : transform.root, ArTuning.DepthBiasContact);
                _depthState = "mode " + ArTuning.OcclusionMode.ToString().ToLowerInvariant() + ", " + n + " material(s) set" +
                              (ArMaterials.ShaderSupportsOcclusion ? "" : " (GameUnlit.shader has no occlusion edit yet)");
            }
            catch (Exception e)
            {
                _depthState = "error: " + e.Message;
                Debug.LogWarning("[VBS][AR] occlusion: " + e.Message);
            }
        }

        // ------------------------------------------------------------ self-test (V2)
        void DeviceSelfTest()
        {
            if (_deviceLinesLogged || Time.realtimeSinceStartup - _startedAt < 5f) return;
            bool pre = director == null || director.Beat == GameBeat.Pre;
            if (Raycaster != null && Raycaster.Enabled && pre && !DesktopMode)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    Raycaster.Raycast(new Ray(cam.transform.position, cam.transform.forward), 5f, out _ahead);
                    _aheadState = _ahead.status + (_ahead.status == ArHitStatus.Hit
                        ? " at " + Vector3.Distance(cam.transform.position, _ahead.point).ToString("0.00", C) + " m" : "");
                }
            }
            else if (DesktopMode) _aheadState = "n/a (desktop: fake room)";
            _deviceLinesLogged = true;
            LogLine("depth " + DepthLine());
            LogLine("raycast " + RaycastLine());
            UpdateSelfTestText();
        }

        string HeadsetLine()
        {
            if (Provider == null) return "n/a (" + SystemInfo.deviceModel + ", no Meta provider)";
            try { return Provider.HeadsetName; } catch (Exception e) { return "error: " + e.Message; }
        }

        string PermissionLine()
        {
            if (Provider == null) return "n/a";
            try
            {
                var g = Provider.ScenePermissionGranted;
                return g.HasValue ? (g.Value ? "true" : "false (manual placement, no occlusion)") : "n/a";
            }
            catch (Exception e) { return "error: " + e.Message; }
        }

        string DepthLine()
        {
            if (Depth == null) return "supported=n/a available=n/a firstFrameMs=n/a (" + _depthState + ")";
            try
            {
                return "supported=" + Depth.IsSupported + " available=" + Depth.IsDepthAvailable + " firstFrameMs=" +
                       (Depth.FirstDepthMs >= 0f ? Depth.FirstDepthMs.ToString("0", C) : "n/a") + " (" + _depthState + ")";
            }
            catch (Exception e) { return "error: " + e.Message; }
        }

        string RaycastLine()
        {
            if (Raycaster == null) return "supported=n/a ahead=n/a";
            return "supported=" + Raycaster.IsSupported + " (" + Raycaster.Name + ") ahead=" + _aheadState;
        }

        void UpdateSelfTestText()
        {
            var sb = new StringBuilder(512);
            sb.Append("AR model: ").Append(HeadsetLine()).Append('\n');
            sb.Append("AR USE_SCENE: ").Append(PermissionLine()).Append('\n');
            sb.Append("AR depth: ").Append(DepthLine()).Append('\n');
            sb.Append("AR raycast: ").Append(RaycastLine()).Append('\n');
            sb.Append("AR placement: ").Append(PlacementPath).Append(", height snap ").Append(HeightSnapState).Append('\n');
            sb.Append("AR window: ").Append(WindowState).Append('\n');
            sb.Append("AR room: ").Append(Room.Describe()).Append('\n');
            sb.Append("AR flags: ").Append(ArTuning.Describe());
            SelfTestText = sb.ToString();
        }

        static void LogLine(string s) => Debug.Log("[VBS][AR] " + s);

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        static Vector2 Flat2(Vector3 v) => new Vector2(v.x, v.z);

        static string V(Vector3 v) => "(" + v.x.ToString("0.00", C) + "," + v.y.ToString("0.00", C) + "," + v.z.ToString("0.00", C) + ")";
    }
}
