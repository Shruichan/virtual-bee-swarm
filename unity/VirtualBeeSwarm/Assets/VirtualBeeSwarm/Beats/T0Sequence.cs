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
        Vector3 _crashSpotHome, _dustHome;
        Quaternion _rocketHomeRot;
        float _dim;
        bool _launched, _landed;
        bool _hasLaunchPose;
        Pose _launchPose, _crashPose;
        Vector3 _launchScale;
        Transform _facingSeat;
        HiveBody _hive;

        const float BoardScale = 2.35f;
        const float FlightScale = 3.4f;

        public bool MoonAwardVisible { get; private set; }

        public override bool Handles(GameBeat beat) => beat == GameBeat.T0;

        protected override void Awake()
        {
            base.Awake();
            if (beeswax == null) beeswax = FindAnyObjectByType<BeeswaxController>();
            if (window == null) window = FindAnyObjectByType<FutureWindow>();
            if (swarm == null) swarm = FindAnyObjectByType<SwarmSystem>();
            _hive = FindAnyObjectByType<HiveBody>();
            if (rocket != null) { _rocketHomePos = rocket.localPosition; _rocketHomeRot = rocket.localRotation; }
            if (crashSpot != null) _crashSpotHome = crashSpot.localPosition;
            if (dustPuff != null) _dustHome = dustPuff.transform.localPosition;
            if (earth != null) _earthHome = earth.localPosition;
            if (moon != null) { _moonScale = moon.transform.localScale; _moonHome = moon.transform.localPosition; }
            SetActive(flame, false);
            SetActive(moon, false);
        }

        void Start()
        {
            PrepareRocketHome();
            // Optional passthrough dimmer (VirtualBeeSwarm.Meta/OvrPassthroughDimmer or your own).
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length && _dimmer == null; i++) _dimmer = all[i] as IPassthroughDimmer;
        }

        void PrepareRocketHome()
        {
            if (rocket == null) return;
            var body = rocket.GetComponent<DeliveryItem>();
            if (body == null) body = rocket.gameObject.AddComponent<DeliveryItem>();
            body.Measure();
            if (!Bloom.MeadowLayout.Active)
            {
                float height = body.localBounds.size.y * Mathf.Abs(rocket.lossyScale.y);
                rocket.localScale *= Bloom.MeadowTuning.DefaultRocketHeight / Mathf.Max(0.01f, height);
                var location = rocket.localPosition;
                location.z -= 0.12f;
                rocket.localPosition = location;
                body.restScale = rocket.localScale;
            }
            Vector3 home = rocket.position;
            HiveClearance.Push(rocket, ref home, rocket.rotation, body);
            rocket.position = home;
            body.RecordHome();
            _rocketHomePos = rocket.localPosition;
            _rocketHomeRot = rocket.localRotation;
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
                    rocket.localScale = _launchScale;
                }
                if (earth != null) earth.localPosition = _earthHome;
                if (dustPuff != null) dustPuff.Clear();
                if (beeswax != null) beeswax.SetDusty(false);
            }
            else
            {
                if (rocket != null)
                {
                    var item = rocket.GetComponent<DeliveryItem>();
                    bool home = item != null && item.hasHome && item.homeParent == rocket.parent;
                    _launchPose = home ? new Pose(item.homeLocalPos, item.homeLocalRot)
                        : new Pose(rocket.localPosition, rocket.localRotation);
                    _launchScale = home ? item.restScale : rocket.localScale;
                    rocket.SetLocalPositionAndRotation(_launchPose.position, _launchPose.rotation);
                    rocket.localScale = _launchScale;
                }
                _hasLaunchPose = true;
            }
            _launched = _landed = MoonAwardVisible = false;
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            if (swarm != null && swarm.Mode == SwarmMode.PourOut) swarm.SetMode(SwarmMode.Calm);
            Vector3 flyAway = Vector3.forward;
            if (rocket != null)
            {
                Vector3 boardPosition = RevealPosition(out flyAway);
                rocket.gameObject.SetActive(true);
                RevealBurst(flyAway);
                yield return Wait(0.25f);
                Vector3 startPosition = rocket.position;
                float growAt = Time.time;
                while (Time.time - growAt < 0.8f)
                {
                    float k = Mathf.Clamp01((Time.time - growAt) / 0.8f);
                    rocket.position = Vector3.Lerp(startPosition, boardPosition, Mathf.SmoothStep(0f, 1f, k));
                    rocket.localScale = _launchScale * Mathf.Lerp(1f, BoardScale, k * k);
                    yield return null;
                }
                rocket.position = boardPosition;
                rocket.localScale = _launchScale * BoardScale;
            }
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
            if (rocket != null) yield return ScaleTo(rocket, _launchScale * FlightScale, 0.65f);

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
                    float travel = k * k;
                    rocket.position = from + (Vector3.up * height + flyAway * 0.55f) * travel;
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
                rocket.localScale = _launchScale;
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
            // The award line belongs to this reveal, so clear any earlier narration still queued.
            if (net != null) net.StopVoice();
            MoonAwardVisible = true;
            StartCoroutine(AwardTrophy());
            yield return SayAndWait(GameLines.RoomT0FakeMoon, 2.5f, LineTime.Seconds(net, GameLines.RoomT0FakeMoon) + 1f);
            Complete(0.7f);
        }

        // ------------------------------------------------------------ the Moon itself on the award card (dartiros: "and there's the flag")
        Transform _trophy;

        /// <summary>A little copy of the Moon with its union flag, standing on StickerDirector's "You got the Moon" card,
        /// so it pops in, follows and hides with the card.</summary>
        IEnumerator AwardTrophy()
        {
            ClearTrophy();
            yield return Wait(0.4f); // the card turns on next update and pops to full size in 0.32 s: measure it after that
            var card = GameObject.Find("[VBS] Moon Award");
            if (card == null || moon == null) yield break;
            var moonBall = moon.transform.Find("Moon");
            var src = moonBall != null ? moonBall.gameObject : moon;
            var mini = Instantiate(src, card.transform, true);
            mini.name = "[VBS] MoonTrophy";
            mini.SetActive(true);
            foreach (var c in mini.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var mb in mini.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false; // a trophy, not a second Moon
            var own = mini.transform.Find("NP_MoonFlag");
            if (own != null) own.gameObject.SetActive(true);
            mini.transform.rotation = card.transform.rotation;
            FitOnCard(mini.transform, card.transform, 0.1f);
            if (own == null) PlantTrophyFlag(mini.transform, card.transform);
            _trophy = mini.transform;
        }

        /// <summary>Scale an object to `size` (largest side) and stand it on top of the card's centre.</summary>
        static void FitOnCard(Transform t, Transform card, float size)
        {
            if (!Bounds(t, out var b)) return;
            float big = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (big > 1e-4f) t.localScale *= size / big;
            if (!Bounds(card, out var cb, t) || !Bounds(t, out b)) return;
            t.position += new Vector3(cb.center.x - b.center.x, cb.max.y + 0.005f - b.min.y, cb.center.z - b.center.z);
        }

        /// <summary>No flag on the Moon (it was planted on the table's Moon ground): copy the planted one onto the trophy.</summary>
        void PlantTrophyFlag(Transform mini, Transform card)
        {
            Transform src = null;
            foreach (var t in transform.root.GetComponentsInChildren<Transform>(true)) // once per ride
                if (t.name == "NP_MoonFlag" && !t.IsChildOf(mini)) { src = t; break; }
            if (src == null || !Bounds(mini, out var mb)) return;
            var flag = Instantiate(src.gameObject, mini, true);
            flag.name = "[VBS] MoonTrophyFlag";
            flag.SetActive(true);
            flag.transform.rotation = card.rotation; // first: rotating moves the bounds
            foreach (var c in flag.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            if (!Bounds(flag.transform, out var fb)) { Destroy(flag); return; }
            if (fb.size.y > 1e-4f) flag.transform.localScale *= (mb.size.y * 0.7f) / fb.size.y;
            if (!Bounds(flag.transform, out fb)) return;
            Vector3 top = new Vector3(mb.center.x, mb.max.y - mb.size.y * 0.08f, mb.center.z) + card.right * (mb.size.x * 0.15f);
            flag.transform.position += top - new Vector3(fb.center.x, fb.min.y, fb.center.z);
        }

        /// <summary>World bounds of t's renderers (minus `skip`'s), false when there are none.</summary>
        static bool Bounds(Transform t, out Bounds b, Transform skip = null)
        {
            b = default;
            bool any = false;
            foreach (var r in t.GetComponentsInChildren<Renderer>())
            {
                if (skip != null && r.transform.IsChildOf(skip)) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any;
        }

        void ClearTrophy()
        {
            if (_trophy != null)
            {
                _trophy.gameObject.SetActive(false);
                Destroy(_trophy.gameObject);
            }
            _trophy = null;
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

        Vector3 RevealPosition(out Vector3 flyAway)
        {
            var cam = Camera.main;
            Transform head = beeswax != null ? beeswax.HeadTransform : null;
            if (head == null && cam != null) head = cam.transform;
            Vector3 top = _hive != null ? _hive.SwarmCenter.position + Vector3.up * 0.35f
                                        : rocket.position + Vector3.up * 0.5f;
            if (_hive != null && _hive.TryGetBodyBounds(out var frame, out var bounds))
            {
                Matrix4x4 toWorld = frame.localToWorldMatrix;
                top = toWorld.MultiplyPoint3x4(bounds.center);
                top.y += Mathf.Abs(toWorld.m10) * bounds.extents.x +
                         Mathf.Abs(toWorld.m11) * bounds.extents.y +
                         Mathf.Abs(toWorld.m12) * bounds.extents.z;
            }

            Vector3 offset = head != null ? Vector3.ProjectOnPlane(top - head.position, Vector3.up) : Vector3.forward;
            flyAway = offset.sqrMagnitude > 1e-4f ? offset.normalized : Vector3.forward;
            // A small viewer-relative depth offset keeps the rocket over the hive when the player leans in.
            top += flyAway * Mathf.Clamp(0.9f - offset.magnitude, -0.10f, 0.25f);
            Vector3 forward = Vector3.ProjectOnPlane(rocket.forward, Vector3.up);
            rocket.rotation = Quaternion.LookRotation(forward.sqrMagnitude > 1e-4f ? forward : flyAway, Vector3.up);
            var item = rocket.GetComponent<DeliveryItem>();
            if (item != null && !item.measured) item.Measure();
            float floorOffset = item != null && item.measured
                ? Mathf.Max(0f, -item.localBounds.min.y * Mathf.Abs(rocket.lossyScale.y) * FlightScale)
                : 0.08f;
            return top + Vector3.up * (floorOffset + 0.14f);
        }

        void RevealBurst(Vector3 flyAway)
        {
            var fx = FindAnyObjectByType<Juice.JuiceParticles>();
            Vector3 origin = rocket.position - flyAway * 0.16f + Vector3.up * 0.07f;
            if (fx != null)
            {
                fx.Burst(origin, 30, new Color(1f, 0.74f, 0.22f), 0.95f, 0.85f, 0.024f, 0.45f);
                fx.Ring(origin, -flyAway, 18, new Color(1f, 0.94f, 0.70f), 1.15f, 0.55f, 0.022f);
            }
            else if (dustPuff != null)
            {
                dustPuff.transform.position = origin;
                dustPuff.Emit(24);
            }
            Debug.Log("[VBS][T0] small rocket revealed beside hive at " + rocket.position.ToString("F2"));
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
            if (rocket != null) rocket.localScale = _launchScale;
            if (_launched && !_landed)
            {
                if (rocket != null) PutRocketDown();
                RestoreMeadow();
            }
            else if (_landed && rocket != null)
                rocket.SetPositionAndRotation(_crashPose.position, _crashPose.rotation);
            else if (rocket != null)
                rocket.SetLocalPositionAndRotation(_launchPose.position, _launchPose.rotation);
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
            Transform anchor = rocket.parent;
            var body = rocket.GetComponent<DeliveryItem>();
            if (_hasLaunchPose)
            {
                // Use the launch pad in the current room layout and lean away from the hive.
                Vector3 position = anchor != null ? anchor.TransformPoint(_launchPose.position) : _launchPose.position;
                Quaternion upright = anchor != null ? anchor.rotation * _launchPose.rotation : _launchPose.rotation;
                Vector3 right = upright * Vector3.right;
                float side = _hive == null || Vector3.Dot(position - _hive.transform.position, right) >= 0f ? 1f : -1f;
                Quaternion tilted = upright * Quaternion.Euler(0f, 0f, -18f * side);
                if (body != null)
                {
                    if (!body.measured) body.Measure();
                    Vector3 scale = rocket.lossyScale;
                    Vector3 centre = Vector3.Scale(body.localBounds.center, scale);
                    Vector3 extent = Vector3.Scale(body.localBounds.extents, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                    position.y += LowestPoint(centre, extent, upright, Vector3.up) - LowestPoint(centre, extent, tilted, Vector3.up);
                    Vector3 checkedPosition = position;
                    if (HiveClearance.Push(rocket, ref checkedPosition, tilted, body))
                    {
                        // Keep floor contact when the generic clearance would place it on top of the hive.
                        Vector3 outward = right * side;
                        if (_hive != null && _hive.TryGetBodyBounds(out var frame, out var bounds))
                        {
                            Vector3 axis = frame.localToWorldMatrix.transpose.MultiplyVector(outward);
                            float hullEdge = Vector3.Dot(frame.TransformPoint(bounds.center), outward) +
                                Vector3.Dot(bounds.extents, new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z)));
                            float rocketEdge = Vector3.Dot(position, outward) + LowestPoint(centre, extent, tilted, outward);
                            position += outward * Mathf.Max(0f, hullEdge - rocketEdge + 0.03f);
                        }
                        HiveClearance.Push(rocket, ref position, tilted, body);
                    }
                }
                if (crashSpot != null) crashSpot.position = position; // the crash sound uses this anchor
                return new Pose(position, tilted);
            }
            Vector3 fallbackPosition = crashSpot != null ? crashSpot.position
                : anchor != null ? anchor.TransformPoint(_rocketHomePos) : _rocketHomePos;
            Quaternion rotation = crashSpot != null ? crashSpot.rotation
                : anchor != null ? anchor.rotation * _rocketHomeRot : _rocketHomeRot;
            rotation *= Quaternion.Euler(0f, 0f, 28f);
            HiveClearance.Push(rocket, ref fallbackPosition, rotation, body);
            return new Pose(fallbackPosition, rotation);
        }

        static float LowestPoint(Vector3 centre, Vector3 extent, Quaternion rotation, Vector3 axis)
        {
            Vector3 up = Quaternion.Inverse(rotation) * axis;
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
            if (rocket != null && _hasLaunchPose) rocket.localScale = _launchScale;
            if (beeswax != null && _hasLaunchPose)
            {
                beeswax.Detach();
                beeswax.SetDusty(false);
            }
            _hasLaunchPose = false;
            _launched = _landed = false;
            MoonAwardVisible = false;
            ClearTrophy();
            SetDim(0f);
            if (window != null) window.NormalSize();
            SetActive(flame, false);
            SetActive(moon, false);
            if (dustPuff != null)
            {
                dustPuff.Clear(true);
                dustPuff.transform.localPosition = _dustHome;
            }
            if (crashSpot != null) crashSpot.localPosition = _crashSpotHome;
            _crashPose = default;
            if (rocket != null)
            {
                rocket.gameObject.SetActive(true);
                rocket.localPosition = _rocketHomePos;
                rocket.localRotation = _rocketHomeRot;
            }
            if (earth != null) earth.localPosition = _earthHome;
            if (moon != null)
            {
                moon.transform.localPosition = _moonHome;
                moon.transform.localScale = _moonScale;
            }
            if (rocketDisplay != null) rocketDisplay.text = "HONEYMOON-1";
        }
    }
}
