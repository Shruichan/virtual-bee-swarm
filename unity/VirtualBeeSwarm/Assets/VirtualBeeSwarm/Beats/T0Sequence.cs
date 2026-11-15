using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// T-0: "...I'm still going. Ticket's non-refundable." Beeswax boards HONEYMOON-1, "TAKE ME TO THE MOON!",
    /// 3-2-1, launch (through the ceiling), passthrough dims (optional IPassthroughDimmer), the window swells into the
    /// Moon while Moon + Earth placeholders appear in the room (Earth rising). "...Not one flower." He crashes back
    /// onto the real table, dusty. "Same time tomorrow? There are six more hives."
    /// V16: Beeswax first flies to his perch on the rocket (rocketPerch, else the rocket's "Perch"), then says room_t0_1.
    /// </summary>
    public class T0Sequence : BeatBehaviour
    {
        /// <summary>HandsOn: runs after 3-2-1, before LIFTOFF (the launch lever). Gets the rocket display.</summary>
        public static System.Func<TextMesh, IEnumerator> LaunchGate; // HANDSON hook

        [Header("Rocket")]
        [SerializeField] Transform rocket;
        [SerializeField] Transform rocketSeat;
        [Tooltip("Where Beeswax lands before room_t0_1 (V16). Empty: the rocket's child named Perch.")]
        [SerializeField] Transform rocketPerch;
        [SerializeField] float perchFlightMaxSeconds = 4f;
        [SerializeField] GameObject flame;
        [SerializeField] TextMesh rocketDisplay;
        [SerializeField] float launchHeight = 3f;
        [SerializeField] float launchSeconds = 3f;
        [Tooltip("Where it crashes back (on the table).")]
        [SerializeField] Transform crashSpot;
        [SerializeField] ParticleSystem dustPuff;

        [Header("Moon")]
        [SerializeField] GameObject moon;
        [SerializeField] Transform earth;
        [SerializeField] float earthRise = 0.45f;
        [SerializeField, Range(0f, 1f)] float passthroughDim = 0.75f;

        [Header("Others")]
        [SerializeField] BeeswaxController beeswax;
        [SerializeField] FutureWindow window;
        [SerializeField] SwarmSystem swarm;

        IPassthroughDimmer _dimmer;
        Vector3 _rocketHomePos, _earthHome, _moonHome, _moonScale = Vector3.one;
        Quaternion _rocketHomeRot;
        float _dim;
        bool _launched, _landed;
        bool _hasLaunchPose;
        Pose _launchPose, _crashPose;
        Transform _facingSeat;

        public bool MoonAwardVisible { get; private set; }

        public override bool Handles(GameBeat beat) => beat == GameBeat.T0;

        protected override void Awake()
        {
            base.Awake();
            if (beeswax == null) beeswax = FindAnyObjectByType<BeeswaxController>();
            if (window == null) window = FindAnyObjectByType<FutureWindow>();
            if (swarm == null) swarm = FindAnyObjectByType<SwarmSystem>();
            if (rocket != null) { _rocketHomePos = rocket.localPosition; _rocketHomeRot = rocket.localRotation; }
            if (earth != null) _earthHome = earth.localPosition;
            if (moon != null) { _moonScale = moon.transform.localScale; _moonHome = moon.transform.localPosition; }
            SetActive(flame, false);
            SetActive(moon, false);
        }

        void Start()
        {
            // Optional passthrough dimmer (VirtualBeeSwarm.Meta/OvrPassthroughDimmer or your own).
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length && _dimmer == null; i++) _dimmer = all[i] as IPassthroughDimmer;
        }

        void OnEnable() { if (net != null) net.HiveWelcomed += RestoreLaunchScene; }
        void OnDisable() { if (net != null) net.HiveWelcomed -= RestoreLaunchScene; }

        void RestoreLaunchScene()
        {
            if (IsActive) net.RequestScene(_launched && !_landed ? GameIds.Scene.Moon : GameIds.Scene.FullBloom, sendToHive: true);
        }

        protected override void OnEnter(GameBeat beat)
        {
            if (_hasLaunchPose)
            {
                // A Board replay starts from the launch pad again. Keep the crash pose through the normal finale.
                if (rocket != null)
                {
                    var item = rocket.GetComponent<DeliveryItem>();
                    if (item != null && item.hasHome)
                        rocket.SetPositionAndRotation(item.HomePosition, item.HomeRotation);
                    else
                        rocket.SetLocalPositionAndRotation(_launchPose.position, _launchPose.rotation);
                }
                if (earth != null) earth.localPosition = _earthHome;
                if (dustPuff != null) dustPuff.Clear();
                if (beeswax != null) beeswax.SetDusty(false);
            }
            else
            {
                if (rocket != null) _launchPose = new Pose(rocket.localPosition, rocket.localRotation);
                _hasLaunchPose = true;
            }
            _launched = _landed = MoonAwardVisible = false;
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            if (swarm != null && swarm.Mode == SwarmMode.PourOut) swarm.SetMode(SwarmMode.Calm);
            // V16: to his perch on the rocket first, then the line
            var perch = Perch();
            if (beeswax != null && perch != null)
            {
                beeswax.FlyTo(perch);
                float p0 = Time.time;
                while (!beeswax.Arrived && Time.time - p0 < perchFlightMaxSeconds) yield return null;
                beeswax.AttachTo(perch);
                Debug.Log("[VBS][T0] Beeswax on his perch after " + (Time.time - p0).ToString("0.0") + " s" +
                          (Time.time - p0 >= perchFlightMaxSeconds ? " (snapped)" : "") + ", then " + GameLines.T0Going.id);
            }
            else if (beeswax != null) beeswax.GoPresent();
            yield return SayAndWait(GameLines.T0Going, 2f, 6f);

            // board
            if (beeswax != null && rocketSeat != null)
            {
                Transform seat = FacingSeat();
                beeswax.FlyTo(seat);
                float t0 = Time.time;
                while (!beeswax.Arrived && Time.time - t0 < 3f) yield return null;
                beeswax.AttachTo(seat);
            }
            for (int n = 3; n >= 1; n--)
            {
                if (rocketDisplay != null) rocketDisplay.text = n.ToString();
                var stakes = VirtualBeeSwarm.Stakes.StakesDirector.Instance;
                if (stakes != null && stakes.Display != null) stakes.Display.ShowPassengerCount(n);
                yield return Wait(0.9f);
            }
            if (LaunchGate != null) yield return LaunchGate(rocketDisplay); // HANDSON hook
            if (net != null) net.Say(GameLines.T0Launch); // "TAKE ME TO THE MOON!" lands with the lever pull and the flame
            if (rocketDisplay != null) rocketDisplay.text = "LIFTOFF";

            // launch
            _launched = true;
            SetActive(flame, true);
            if (window != null) window.SwellToMoon();
            if (net != null) net.RequestScene(GameIds.Scene.Moon, sendToHive: true);
            PlaceMoonByWindow();
            if (moon != null)
            {
                moon.transform.localScale = Vector3.zero;
                SetActive(moon, true);
                StartCoroutine(ScaleTo(moon.transform, _moonScale, 2.5f));
            }
            if (rocket != null)
            {
                Vector3 from = rocket.position;
                float height = LaunchHeight(from.y);
                float start = Time.time;
                while (Time.time - start < launchSeconds)
                {
                    float k = (Time.time - start) / launchSeconds;
                    rocket.position = from + Vector3.up * (k * k * height) + Vector3.right * (Mathf.Sin(Time.time * 25f) * 0.003f);
                    SetDim(Mathf.Lerp(0f, passthroughDim, Mathf.Clamp01(k * 1.5f)));
                    if (earth != null) earth.localPosition = _earthHome + Vector3.up * (earthRise * k * 0.5f);
                    yield return null;
                }
                rocket.gameObject.SetActive(false);
                if (beeswax != null) beeswax.SetVisible(false); // he's up there with it
            }

            // the Moon (v2 optional #4 first: "Look at Earth. Up here it barely moves.")
            float moonStart = Time.time;
            var earthLine = RideLines.T0Earth;
            if (RidePacing.Optional(4, earthLine)) yield return SayAndWait(earthLine, 2.5f, LineTime.Seconds(net, earthLine) + 1f, unlessHive: false);
            yield return SayAndWait(GameLines.T0Moon, 2.5f, 6f, unlessHive: false);
            while (Time.time - moonStart < 4f)
            {
                if (earth != null) earth.localPosition = _earthHome + Vector3.up * (earthRise * (0.5f + 0.5f * (Time.time - moonStart) / 4f));
                yield return null;
            }

            // Accelerate into the crash, trigger the dust and audio at contact, then give the rocket one short bounce.
            if (rocket != null)
            {
                _crashPose = GetCrashPose();
                rocket.gameObject.SetActive(true);
                Vector3 from = _crashPose.position + Vector3.up * 1.2f;
                float start = Time.time;
                while (Time.time - start < 0.6f)
                {
                    float k = (Time.time - start) / 0.6f;
                    rocket.SetPositionAndRotation(Vector3.Lerp(from, _crashPose.position, k * k),
                        _crashPose.rotation * Quaternion.Euler(0f, 0f, 145f * (1f - k * k)));
                    yield return null;
                }
                rocket.SetPositionAndRotation(_crashPose.position, _crashPose.rotation);
            }
            _landed = true;
            SetActive(flame, false);
            if (dustPuff != null)
            {
                if (rocket != null) dustPuff.transform.position = _crashPose.position;
                dustPuff.Emit(38);
            }
            SetActive(moon, false);
            RestoreMeadow(); // the sound director plays the crash on the Moon-to-Meadow change
            if (rocket != null)
            {
                float start = Time.time;
                while (Time.time - start < 0.4f)
                {
                    float k = (Time.time - start) / 0.4f;
                    float bounce = Mathf.Sin(k * Mathf.PI) * (1f - k);
                    rocket.SetPositionAndRotation(_crashPose.position + Vector3.up * (0.19f * bounce),
                        _crashPose.rotation * Quaternion.Euler(0f, 0f, -22f * bounce));
                    yield return null;
                }
                rocket.SetPositionAndRotation(_crashPose.position, _crashPose.rotation);
            }
            if (beeswax != null)
            {
                beeswax.Detach();
                beeswax.SetVisible(true);
                beeswax.SetDusty(true);
                beeswax.GoPresent();
            }
            float undimStart = Time.time;
            float dim0 = _dim;
            while (Time.time - undimStart < 1f)
            {
                SetDim(Mathf.Lerp(dim0, 0f, Time.time - undimStart));
                yield return null;
            }
            SetDim(0f);
            if (rocketDisplay != null) rocketDisplay.text = "HONEYMOON-1";

            yield return SayAndWait(GameLines.T0Back, 2.5f, 7f);
            MoonAwardVisible = true;
            yield return SayAndWait(GameLines.RoomT0FakeMoon, 2.5f, LineTime.Seconds(net, GameLines.RoomT0FakeMoon) + 1f);
            Complete(0.7f);
        }

        Transform FacingSeat()
        {
            if (_facingSeat == null)
            {
                _facingSeat = new GameObject("Facing Seat").transform;
                _facingSeat.SetParent(rocketSeat, false);
            }
            Transform head = beeswax.HeadTransform;
            Vector3 forward = head != null ? head.position - rocketSeat.position : rocketSeat.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(rocketSeat.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            _facingSeat.rotation = Quaternion.LookRotation(forward, Vector3.up);
            return _facingSeat;
        }

        Transform Perch()
        {
            if (rocketPerch != null) return rocketPerch;
            if (rocket == null) return null;
            Transform found = null;
            foreach (var t in rocket.GetComponentsInChildren<Transform>(true))
                if (t.name == "Perch" && (found == null || t.gameObject.activeInHierarchy)) found = t; // the art's Perch wins over a leftover placeholder
            rocketPerch = found;
            return found;
        }

        /// <summary>V16: up to the ceiling (measured, else ar.ceilingY) minus 5 cm, never more than launchHeight.</summary>
        float LaunchHeight(float fromY)
        {
            var ar = VirtualBeeSwarm.AR.ArRig.Instance;
            if (ar == null) return launchHeight;
            return Mathf.Clamp(ar.Room.CeilingOrDefault - fromY - 0.05f, 0.8f, launchHeight);
        }

        /// <summary>V16: with the window on the real wall, the Moon sits 0.4 m in front of it (window +Z points into the wall), never inside it.</summary>
        void PlaceMoonByWindow()
        {
            if (moon == null || window == null || VirtualBeeSwarm.AR.ArRig.Instance == null || !VirtualBeeSwarm.AR.ArTuning.WindowOnWall) return;
            var w = window.transform;
            moon.transform.position = w.position - w.forward * 0.4f;
        }

        void SetDim(float amount)
        {
            _dim = amount;
            if (_dimmer != null) _dimmer.SetDim(amount);
        }

        protected override void OnExit(GameBeat beat)
        {
            MoonAwardVisible = false;
            SetDim(0f);
            SetActive(flame, false);
            SetActive(moon, false);
            if (rocket != null && !rocket.gameObject.activeSelf) rocket.gameObject.SetActive(true);
            if (_launched && !_landed)
            {
                if (rocket != null) PutRocketDown();
                RestoreMeadow();
            }
            else if (_landed && rocket != null)
                rocket.SetPositionAndRotation(_crashPose.position, _crashPose.rotation);
            if (beeswax != null) { beeswax.Detach(); beeswax.SetVisible(true); }
            if (rocketDisplay != null) rocketDisplay.text = "HONEYMOON-1";
        }

        void RestoreMeadow()
        {
            if (window != null) window.NormalSize();
            if (net != null) net.RequestScene(GameIds.Scene.FullBloom, sendToHive: true);
        }

        Pose GetCrashPose()
        {
            if (Bloom.MeadowLayout.Active && _hasLaunchPose)
            {
                // The launch pad is already clear of the hive at the current meadow scale.
                Transform anchor = rocket.parent;
                Vector3 position = anchor != null ? anchor.TransformPoint(_launchPose.position) : _launchPose.position;
                Quaternion upright = anchor != null ? anchor.rotation * _launchPose.rotation : _launchPose.rotation;
                Transform hive = anchor != null ? anchor.Find("Hive") : null;
                float side = hive == null || Vector3.Dot(position - hive.position, upright * Vector3.right) >= 0f ? 1f : -1f;
                Quaternion tilted = upright * Quaternion.Euler(0f, 0f, -18f * side);
                if (rocket.TryGetComponent(out DeliveryItem body) && body.measured)
                {
                    Vector3 scale = rocket.lossyScale;
                    Vector3 centre = Vector3.Scale(body.localBounds.center, scale);
                    Vector3 extent = Vector3.Scale(body.localBounds.extents, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                    position.y += LowestPoint(centre, extent, upright) - LowestPoint(centre, extent, tilted);
                }
                if (crashSpot != null) crashSpot.position = position; // the crash sound uses this anchor
                return new Pose(position, tilted);
            }
            if (crashSpot != null)
                return new Pose(crashSpot.position, crashSpot.rotation * Quaternion.Euler(0f, 0f, 28f));
            var parent = rocket.parent;
            return new Pose(parent != null ? parent.TransformPoint(_rocketHomePos) : _rocketHomePos,
                (parent != null ? parent.rotation * _rocketHomeRot : _rocketHomeRot) * Quaternion.Euler(0f, 0f, 28f));
        }

        static float LowestPoint(Vector3 centre, Vector3 extent, Quaternion rotation)
        {
            Vector3 up = Quaternion.Inverse(rotation) * Vector3.up;
            return Vector3.Dot(centre, up) - Vector3.Dot(extent, new Vector3(Mathf.Abs(up.x), Mathf.Abs(up.y), Mathf.Abs(up.z)));
        }

        void PutRocketDown()
        {
            _crashPose = GetCrashPose();
            rocket.SetPositionAndRotation(_crashPose.position, _crashPose.rotation);
            _landed = true;
        }

        protected override void OnResetState()
        {
            _hasLaunchPose = false;
            MoonAwardVisible = false;
            SetDim(0f);
            SetActive(flame, false);
            SetActive(moon, false);
            if (rocket != null)
            {
                rocket.gameObject.SetActive(true);
                rocket.localPosition = _rocketHomePos;
                rocket.localRotation = _rocketHomeRot;
            }
            if (earth != null) earth.localPosition = _earthHome;
            if (moon != null) moon.transform.localPosition = _moonHome;
            if (rocketDisplay != null) rocketDisplay.text = "HONEYMOON-1";
        }
    }
}
