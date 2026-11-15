using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Puts the whole rig (hive + HONEYMOON-1 + window + stations, i.e. everything under rigAnchor) on the real table.
    /// Two ways, during pre (or anytime if onlyInPre is off):
    ///  1. Grab and drop: grab the base plate (pinch next to it, or ray + pinch/trigger, or mouse drag), move it onto
    ///     the table, let go. On release the rig turns to face the player.
    ///  2. Two-point calibration: pinch the table's front edge at two spots (left then right, where nothing
    ///     clickable is under the pointer). The rig sits `inset` behind the middle of that edge, facing the player.
    /// Desktop: C puts the rig in front of the camera at table height.
    /// Headset: on the first tracked frames the rig is put in front of the player (autoPlaceInHeadset), so it never
    /// starts inside them; then it goes onto the real table with 1. or 2. above.
    /// The pose is kept for this app session only (static), so a scene reload keeps it; an app restart doesn't.
    /// Put this on the base plate (it needs a collider).
    /// </summary>
    public class HivePlacement : MonoBehaviour, IGameGrabTarget
    {
        [SerializeField] Transform rigAnchor;
        [SerializeField] GameDirector director;
        [SerializeField] bool onlyInPre = true;
        [Tooltip("Pointers whose hover/grab blocks a calibration pinch (so clicking START doesn't drop a point).")]
        [SerializeField] GamePointer[] pointers = new GamePointer[0];
        [SerializeField] Transform markerA;
        [SerializeField] Transform markerB;
        [Tooltip("Hive distance behind the front edge (m).")]
        [SerializeField] float inset = 0.22f;
        [SerializeField] float pointTimeoutSeconds = 12f;

        [Header("Headset start")]
        [Tooltip("With an XR device active and no pose saved this session: once the head is tracked, put the rig in " +
                 "front of the player (headsetDistance ahead, headsetBelowEyes down) so it starts visible.")]
        [SerializeField] bool autoPlaceInHeadset = true;
        [SerializeField] float headsetDistance = 0.55f;
        [SerializeField] float headsetBelowEyes = 0.75f;
        [SerializeField] float headsetSettleSeconds = 0.75f;

        [Header("Desktop")]
        [SerializeField] KeyCode placeInFrontKey = KeyCode.C;
        [SerializeField] float desktopDistance = 0.6f;
        [SerializeField] float desktopBelowEyes = 0.6f;

        static bool s_hasSaved;
        static Pose s_saved;

        public bool Allowed => isActiveAndEnabled && (!onlyInPre || director == null || director.Beat == GameBeat.Pre);
        public bool Grabbable => Allowed;
        public bool Holding { get; private set; }
        public int PointsTaken => _points;

        bool _autoPlacePending;
        float _headTrackedSince = -1f;
        Vector3 _grabOffset;
        Vector3 _pointA;
        int _points;
        float _pointAAt, _markersHideAt = -1f;

        void Awake()
        {
            if (rigAnchor == null) rigAnchor = transform.parent != null ? transform.parent : transform;
            if (director == null) director = FindAnyObjectByType<GameDirector>();
            if (s_hasSaved) rigAnchor.SetPositionAndRotation(s_saved.position, s_saved.rotation);
            _autoPlacePending = autoPlaceInHeadset && !s_hasSaved;
            ShowMarkers(false, false);
        }

        // ------------------------------------------------------------ grab and drop
        public void BeginGrab(Vector3 grabPoint)
        {
            Holding = true;
            _grabOffset = rigAnchor.position - grabPoint;
        }

        public void UpdateGrab(Vector3 handPoint)
        {
            Vector3 position = handPoint + _grabOffset;
            if (Bloom.MeadowLayout.Active) position.y = Bloom.MeadowLayout.FloorY;
            rigAnchor.position = position;
        }

        public void EndGrab()
        {
            Holding = false;
            FacePlayer();
            PlaceAt(new Pose(rigAnchor.position, rigAnchor.rotation));
        }

        void FacePlayer()
        {
            var head = Head();
            if (head == null) return;
            Vector3 away = rigAnchor.position - head.position;
            away.y = 0f;
            if (away.sqrMagnitude > 1e-4f) rigAnchor.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
        }

        // ------------------------------------------------------------ two-point calibration + desktop key
        void Update()
        {
            if (_markersHideAt >= 0f && Time.time >= _markersHideAt)
            {
                _markersHideAt = -1f;
                ShowMarkers(false, false);
            }
            if (_autoPlacePending) TryAutoPlace();
            if (!Allowed) { _points = 0; _follow = false; return; } // pre is over (signed): stay put for the ride
            if (_follow && !Holding) FollowHead();

            if (GameInput.KeyDown(placeInFrontKey)) PlaceInFrontOfCamera();

            if (Holding || PointerBusy()) return;
            if (_points == 1 && Time.time - _pointAAt > pointTimeoutSeconds)
            {
                _points = 0;
                ShowMarkers(false, false);
            }
            for (int h = 0; h < 2; h++)
            {
                var hand = (GameHand)h;
                if (!GameHands.PinchDown(hand) || !GameHands.TryGetIndexTip(hand, out var tip)) continue;
                if (_points == 0)
                {
                    _pointA = tip;
                    _pointAAt = Time.time;
                    _points = 1;
                    if (markerA != null) markerA.position = tip;
                    ShowMarkers(true, false);
                }
                else
                {
                    if (markerB != null) markerB.position = tip;
                    ShowMarkers(true, true);
                    PlaceOnEdge(_pointA, tip);
                    _points = 0;
                    _markersHideAt = Time.time + 2.5f;
                }
                break;
            }
        }

        bool PointerBusy()
        {
            for (int i = 0; i < pointers.Length; i++)
                if (pointers[i] != null && (pointers[i].IsHoveringTarget || pointers[i].IsGrabbing)) return true;
            return false;
        }

        /// <summary>Front edge from a to b (any order): the rig goes `inset` behind its middle, facing the player.</summary>
        public void PlaceOnEdge(Vector3 a, Vector3 b)
        {
            Vector3 edge = b - a;
            edge.y = 0f;
            if (edge.sqrMagnitude < 0.0025f) return; // points < 5 cm apart: ignore
            Vector3 mid = (a + b) * 0.5f;
            Vector3 n = Vector3.Cross(Vector3.up, edge.normalized);
            var head = Head();
            if (head != null)
            {
                Vector3 fromHead = mid - head.position;
                fromHead.y = 0f;
                if (Vector3.Dot(n, fromHead) < 0f) n = -n; // point away from the player, into the table
            }
            PlaceAt(new Pose(mid + n * inset, Quaternion.LookRotation(n, Vector3.up)));
        }

        /// <summary>Aim and pinch (v3.1 V7, AR/ArRig): the rig goes exactly to this pose, saved for the session.</summary>
        public void PlaceAt(Pose pose)
        {
            _follow = false; // the player put it somewhere on purpose: stop following them
            pose = Bloom.MeadowLayout.Grounded(pose); // meadow: no table, the rig stands on the floor
            rigAnchor.SetPositionAndRotation(pose.position, pose.rotation);
            Save();
        }

        public void PlaceInFrontOfCamera() => PlaceInFront(desktopDistance, desktopBelowEyes);

        void PlaceInFront(float distance, float belowEyes)
        {
            var head = Head();
            if (head == null) return;
            if (Bloom.MeadowLayout.Active) // meadow: no table, the scaled-up rig stands on the floor ahead
            {
                var pose = Bloom.MeadowLayout.FreeStandPose(head);
                rigAnchor.SetPositionAndRotation(pose.position, pose.rotation);
                Save();
                Bloom.MeadowLayout.NoteAuto(pose, distance == headsetDistance ? "headset start" : "key " + placeInFrontKey);
                return;
            }
            Vector3 fwd = head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 p = head.position + fwd * distance;
            p.y = head.position.y - belowEyes;
            rigAnchor.SetPositionAndRotation(p, Quaternion.LookRotation(fwd, Vector3.up));
            Save();
        }

        /// <summary>Headset only: wait until the head pose is real (not the origin) for a moment, then place once.</summary>
        void TryAutoPlace()
        {
            if (s_hasSaved || !UnityEngine.XR.XRSettings.isDeviceActive) { if (s_hasSaved) _autoPlacePending = false; return; }
            var head = Head();
            if (head == null || head.position.sqrMagnitude < 1e-4f) { _headTrackedSince = -1f; return; }
            if (_headTrackedSince < 0f) { _headTrackedSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _headTrackedSince < headsetSettleSeconds) return;
            _autoPlacePending = false;
            PlaceInFront(headsetDistance, headsetBelowEyes);
            StartFollowing(head);
            Debug.Log("[VBS] rig placed in front of the player; drop it on the real table (grab the plate, or pinch the edge twice)");
        }

        // ------------------------------------------------------------ follow the player until they sign (dartiros)
        [Header("Follow until signed (headset, auto placement only)")]
        [Tooltip("The auto-placed setup re-places in front of the player when they walk or turn before signing SIGN HERE. " +
                 "Stops for good on a manual placement (aim + pinch, grab, two points) and when pre ends (signed).")]
        [SerializeField] bool followUntilSigned = true;
        [SerializeField] float followMoveMeters = 0.6f;
        [SerializeField] float followTurnDegrees = 40f;
        [Tooltip("Re-place only once the player has held still this long (no sliding while they walk).")]
        [SerializeField] float followSettleSeconds = 1.0f;
        bool _follow;
        Vector3 _followFrom, _followFwd, _lastHeadPos, _lastHeadFwd;
        float _stillSince = -1f;

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.zero;
        }

        void StartFollowing(Transform head)
        {
            _follow = followUntilSigned && head != null;
            if (!_follow) return;
            _followFrom = _lastHeadPos = head.position;
            _followFwd = _lastHeadFwd = Flat(head.forward);
            _stillSince = -1f;
        }

        void FollowHead()
        {
            var head = Head();
            if (head == null || !UnityEngine.XR.XRSettings.isDeviceActive) return;
            Vector3 p = head.position, f = Flat(head.forward);
            if (f == Vector3.zero) return;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-3f);
            bool moving = (p - _lastHeadPos).magnitude / dt > 0.25f || Vector3.Angle(f, _lastHeadFwd) / dt > 35f;
            _lastHeadPos = p;
            _lastHeadFwd = f;
            Vector3 d = p - _followFrom;
            d.y = 0f;
            bool away = d.magnitude > followMoveMeters || Vector3.Angle(f, _followFwd) > followTurnDegrees;
            if (!away || moving) { _stillSince = away ? Time.unscaledTime : -1f; return; }
            if (_stillSince < 0f) _stillSince = Time.unscaledTime;
            if (Time.unscaledTime - _stillSince < followSettleSeconds) return;
            PlaceInFront(headsetDistance, headsetBelowEyes);
            _followFrom = p;
            _followFwd = f;
            _stillSince = -1f;
            Debug.Log("[VBS] rig follows the player (moved or turned before signing)");
        }

        void Save()
        {
            s_saved = new Pose(rigAnchor.position, rigAnchor.rotation);
            s_hasSaved = true;
        }

        /// <summary>Forget the session pose (next scene load uses the scene's default).</summary>
        public static void ClearSaved() => s_hasSaved = false;

        void ShowMarkers(bool a, bool b)
        {
            if (markerA != null && markerA.gameObject.activeSelf != a) markerA.gameObject.SetActive(a);
            if (markerB != null && markerB.gameObject.activeSelf != b) markerB.gameObject.SetActive(b);
        }

        static Transform Head()
        {
            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }
    }
}
