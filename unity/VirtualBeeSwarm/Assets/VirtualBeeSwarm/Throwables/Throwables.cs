using System;
using System.Collections.Generic;
using UnityEngine;
using VirtualBeeSwarm.HandsOn;
using VirtualBeeSwarm.Juice;
using VirtualBeeSwarm.Pals;

namespace VirtualBeeSwarm.Throw
{
    /// <summary>
    /// Delivered props get real physics. Let go of a carried prop (HandsOn/CarryProp) with some hand speed and it flies:
    /// an invisible Rigidbody box sized to the prop's mesh bounds carries it, it bounces on the floor (the meadow floor,
    /// else the prop's own floor), tips over, can't pass through the hive (a box per hive part) and knocks over
    /// other props it hits. Once it settles (or after 4 s) it floats back to its parking spot with a sparkle and the
    /// carry takes over again, so the ordered item still has to be set up by hand and the sockets work as before.
    /// A hard tap (open hand or fingertip, not pinching, raw speed 1.8 m/s; the set-up prop only by an open-hand slap)
    /// on a resting prop knocks it over and it rights itself (a visual
    /// tilt only, put back before anyone else moves the prop each frame).
    /// Safety: throws only from a direct grab (a tracked hand at the grab point, not a ray); release speed = a 90 ms
    /// average of the hand point (not the smoothed prop), newest frame dropped, no tracking jump in it, one steady swing,
    /// 1.3-3 m/s; never toward the player's head (launch, flight and the float home), never through the hive, never lost (out of the room, under
    /// the floor or 6 s: it pops back to its spot). Any takeover (crew assist, the station moving on, a delivery
    /// flight, a catch by hand) ends the flight on the spot.
    /// Beeswax grumbles now and then (first time, then at most every 35 s, 3 a ride, only when he's free).
    /// Self-installing; hooks: CarryProp.Released / InFlight / HandPoint, HandsOnDirector.PlayerBusy, HiveClearance.HiveParts (internal). Off: env VBS_THROWABLES=0. The autopilot never throws or
    /// knocks (smoke runs stay as they were) unless VBS_THROW_DEMO=1 (ThrowDemo).
    /// Cost: one Rigidbody, a floor box and a box per hive part, only while a prop flies; a few bounds checks per frame near a hand.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class Throwables : MonoBehaviour
    {
        public static Throwables Instance { get; private set; }

        const float MinThrow = 1.3f, MaxThrow = 3.0f, SpikeSpeed = 9f, SampleWindow = 0.09f, DirectGrab = 0.15f;
        const float HeadSafe = 0.5f, LaunchHeadSafe = 1.1f, ReturnHeadSafe = 0.55f, SettleSeconds = 0.25f, RestSeconds = 0.6f, MaxFlight = 4f, LostSeconds = 6f;
        const float KnockSpeed = 1.8f, KnockReach = 0.015f, KnockCooldown = 2f, RoomRadius = 2.6f;

        enum S { Fly, Rest, Return }

        sealed class Flight
        {
            public CarryProp carry;
            public Transform item;
            public Pose home;
            public string choice;
            public S state;
            public float releasedAt, at, slowSince = -1f, prevVy, lastBounce;
            public bool landed;
            public int bounces, knocked, hiveHits;
            public bool onHive;
            public Vector3 from, ctrl, start, release;
            public Quaternion fromRot;
            public float dur, maxHeight, hiveMin = float.MaxValue;
            public readonly List<Transform> hit = new List<Transform>(4);
            public readonly List<Renderer> stakes = new List<Renderer>(2);
        }

        sealed class Knock
        {
            public Transform t;
            public float at, max;
            public bool tall, applied, puffed, righted;
            public Vector3 pivotLocal, axis, basePos;
            public Quaternion baseRot;
            public Vector3 wrotePos;
            public readonly List<Transform> stakes = new List<Transform>(2);
            public readonly List<Pose> stakeWorld = new List<Pose>(2), stakeLocal = new List<Pose>(2);
        }

        struct Cand
        {
            public Transform t;
            public CarryProp carry;
            public string choice;
        }

        // ------------------------------------------------------------ state
        GameObject _proxy, _floor, _hiveBox;
        PhysicsMaterial _hiveBounce;
        Rigidbody _rb;
        BoxCollider _box;
        JuiceParticles _fx;
        Flight _f;
        readonly List<Knock> _knocks = new List<Knock>(4);
        readonly List<Cand> _cands = new List<Cand>(16);
        readonly Dictionary<Transform, float> _knockedAt = new Dictionary<Transform, float>();
        readonly List<CarryProp> _carries = new List<CarryProp>(8);
        readonly Dictionary<CarryProp, string> _choiceOf = new Dictionary<CarryProp, string>();
        StationDelivery[] _deliveries;
        GameDirector _director;
        GameNet _net;
        GameAutopilot _pilot;
        BeeswaxController _bw;
        HiveBody _hive;
        float _nextScan, _nextLine;
        int _said;
        GameBeat _lastBeat = GameBeat.Pre;
        readonly float[] _handCool = new float[2];
        readonly bool[] _handPinched = new bool[2];
        readonly float[] _pinchUpAt = new float[2];
        // raw (unsmoothed) hand motion for knocks: PalHands.vel lags ~60 ms and jumps when the palm source drops in/out
        readonly Vector3[] _rawTip = new Vector3[2], _rawCentre = new Vector3[2], _rawTipV = new Vector3[2], _rawCentreV = new Vector3[2];
        readonly bool[] _rawHad = new bool[2], _rawPalm = new bool[2];
        readonly int[] _rawSteady = new int[2];
        float _nextFind;
        int _rawFrame = -10;
        readonly List<BoxCollider> _hiveParts = new List<BoxCollider>(8);
        static readonly List<Bounds> PartBuf = new List<Bounds>(16);

        // release velocity samples (the holding hand point)
        CarryProp _sampled;
        bool _heldLast;
        const int N = 12;
        readonly Vector3[] _sp = new Vector3[N];
        readonly float[] _st = new float[N];
        int _sn, _si;

        const string StakeName = "MeadowStake"; // Bloom/MeadowLayout: the garden stake under a price tag
        static readonly Color Sparkle = new Color(1f, 0.86f, 0.3f), Dust = new Color(0.86f, 0.8f, 0.68f);

        /// <summary>Demo / captures: throws and knocks even under the autopilot.</summary>
        public static bool Demo => _demo ??= Env("VBS_THROW_DEMO") == "1";
        public static bool Enabled => Env("VBS_THROWABLES") != "0";
        static bool? _demo;
        /// <summary>A prop is flying or floating home.</summary>
        public bool Busy => _f != null;
        public int Throws { get; private set; }
        public int Knocks { get; private set; }

        static string Env(string k) { try { return Environment.GetEnvironmentVariable(k); } catch (Exception) { return null; } }

        // ------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!Enabled || Instance != null) return;
            if (FindAnyObjectByType<GameDirector>() == null) return; // not the game scene
            var go = new GameObject("[VBS] Throwables");
            go.AddComponent<Throwables>();
            go.AddComponent<ThrowDemo>();
        }

        void Awake()
        {
            Instance = this;
            Build();
            CarryProp.Released = OnReleased; // THROWABLES hook
            CarryProp.InFlight = IsFlying;   // THROWABLES hook
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;
            CarryProp.Released = null;
            CarryProp.InFlight = null;
        }

        void Build()
        {
            const int ignoreRaycast = 2; // pointer rays never hit the proxy or the boxes
            _proxy = new GameObject("ThrowBody") { layer = ignoreRaycast };
            _proxy.transform.SetParent(transform, false);
            _rb = _proxy.AddComponent<Rigidbody>();
            _rb.isKinematic = true;
            _rb.useGravity = true;
            _rb.linearDamping = 0.35f;
            _rb.angularDamping = 0.6f;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _box = _proxy.AddComponent<BoxCollider>();
            _box.sharedMaterial = new PhysicsMaterial("VBS_ThrowProp") { bounciness = 0.3f, dynamicFriction = 0.55f, staticFriction = 0.6f };
            _proxy.SetActive(false);

            var bounce = new PhysicsMaterial("VBS_ThrowFloor")
            {
                bounciness = 0.35f, dynamicFriction = 0.8f, staticFriction = 0.9f, bounceCombine = PhysicsMaterialCombine.Average,
            };
            _floor = new GameObject("ThrowFloor") { layer = ignoreRaycast };
            _floor.transform.SetParent(transform, false);
            var fb = _floor.AddComponent<BoxCollider>();
            fb.size = new Vector3(12f, 0.2f, 12f);
            fb.sharedMaterial = bounce;
            _floor.SetActive(false);

            _hiveBox = new GameObject("ThrowHive") { layer = ignoreRaycast }; // one box per hive part (HiveClearance), in the hive's axes
            _hiveBox.transform.SetParent(transform, false);
            _hiveBounce = bounce;
            _hiveBox.SetActive(false);

            _fx = gameObject.AddComponent<JuiceParticles>();
        }

        // ------------------------------------------------------------ hooks
        bool IsFlying(CarryProp c) => _f != null && _f.carry == c && !c.Held;

        bool Allowed => Demo || _pilot == null || !_pilot.Running;

        bool OnReleased(CarryProp c)
        {
            if (c == null || !c.Live || _f != null || !Allowed) return false;
            if (_sampled != c || !ReleaseVelocity(out Vector3 v, out float raw, out int spikes)) return false;
            if (BeeDelivery.Instance != null && BeeDelivery.Instance.IsFlying(c.transform)) return false;
            if (!HandAtGrip(c.HandPoint)) return false; // a ray grab (or a lost hand): it drifts home as before
            return Launch(c, v, raw, spikes);
        }

        /// <summary>A tracked hand is right at the grab point: a direct grab. A ray grab's point sits up to 4 m out, where
        /// a wrist wobble on trigger release would read as a throw (and a lost hand isn't tracked at all).</summary>
        static bool HandAtGrip(Vector3 grip)
        {
            var ph = PalHands.Instance;
            if (ph == null) return false;
            for (int h = 0; h < 2; h++)
            {
                var hand = ph.Hands[h];
                if (!hand.tracked) continue;
                if ((hand.tip - grip).sqrMagnitude < DirectGrab * DirectGrab || (hand.centre - grip).sqrMagnitude < DirectGrab * DirectGrab) return true;
            }
            return false;
        }

        /// <summary>Average of the hand's last ~90 ms (the hand point, not the smoothed prop), leaving out the newest frame
        /// (tracking jumps at the moment of loss / pinch-open). False: too slow, a tracking jump in the window, or the
        /// motion isn't one steady swing (a jump decays in a single direction from one frame; a throw moves every frame).</summary>
        bool ReleaseVelocity(out Vector3 v, out float raw, out int spikes)
        {
            v = Vector3.zero;
            raw = 0f;
            spikes = 0;
            if (_sn < 5) return false;
            float now = _st[(_si - 1 + N) % N];
            if (Time.time - now > 0.1f) return false; // stale: it wasn't moving with the hand just now
            Vector3 sum = Vector3.zero;
            float wsum = 0f, lsum = 0f;
            int used = 0;
            for (int k = 2; k < _sn; k++) // k = 1 is the newest frame: dropped
            {
                int a = (_si - k + N) % N, b = (_si - k - 1 + N) % N;
                if (_st[(_si - 2 + N) % N] - _st[b] > SampleWindow + 0.02f) break;
                float dt = _st[a] - _st[b];
                if (dt < 1e-4f) continue;
                Vector3 fv = (_sp[a] - _sp[b]) / dt;
                if (fv.sqrMagnitude > SpikeSpeed * SpikeSpeed) { spikes++; continue; }
                sum += fv * dt;
                lsum += fv.magnitude * dt;
                wsum += dt;
                used++;
            }
            if (used < 3 || spikes > 0) return false; // a jump right before the release: a tracking glitch, not a throw
            v = sum / wsum;
            raw = v.magnitude;
            if (raw < MinThrow) return false;
            if (raw * wsum < 0.75f * lsum) return false; // the samples don't agree on a direction
            // every frame moved with the throw (a one-frame jump leaves the rest still)
            Vector3 dir = v / raw;
            int moving = 0;
            for (int k = 2; k < _sn; k++)
            {
                int a = (_si - k + N) % N, b = (_si - k - 1 + N) % N;
                if (_st[(_si - 2 + N) % N] - _st[b] > SampleWindow + 0.02f) break;
                float dt = _st[a] - _st[b];
                if (dt < 1e-4f) continue;
                if (Vector3.Dot((_sp[a] - _sp[b]) / dt, dir) > 0.35f * raw) moving++;
            }
            return moving * 3 >= used * 2;
        }

        // ------------------------------------------------------------ throw
        bool Launch(CarryProp c, Vector3 v, float raw, int spikes)
        {
            Transform item = c.transform;
            if (!LocalBounds(item, out Bounds lb)) return false;
            Vector3 s = item.lossyScale;
            Vector3 size = Vector3.Max(Vector3.Scale(lb.size, Abs(s)), Vector3.one * 0.03f);
            Vector3 head = HeadPos();

            v = Vector3.ClampMagnitude(v, MaxThrow);
            v = OffHead(item.position, v, head, LaunchHeadSafe);
            if (v.magnitude < MinThrow * 0.6f) v += Vector3.up * 0.8f; // flung at the face: it pops up instead

            _f = new Flight
            {
                carry = c, item = item, home = c.Home, choice = ChoiceOf(c), state = S.Fly,
                releasedAt = Time.time, start = item.position, release = v, maxHeight = item.position.y,
            };
            float floorY = FloorY(c);
            _floor.transform.position = new Vector3(head.x, floorY - 0.1f, head.z);
            _floor.SetActive(true);
            int parts = BuildHiveBoxes(out Vector3 bigPart);

            // the meadow's garden stake under its price tag stays out of the flight (a 1.2 m stick tumbling along)
            foreach (var r in item.GetComponentsInChildren<Renderer>())
                if (r.name == StakeName && r.enabled) { r.enabled = false; _f.stakes.Add(r); }
            _box.center = Vector3.Scale(lb.center, s);
            _box.size = size;
            _proxy.transform.SetPositionAndRotation(item.position, item.rotation);
            _proxy.SetActive(true);
            foreach (var col in item.GetComponentsInChildren<Collider>(true)) Physics.IgnoreCollision(_box, col, true);
            float volume = size.x * size.y * size.z;
            _rb.mass = Mathf.Clamp(volume * 350f, 0.15f, 0.9f);
            _rb.isKinematic = false;
            _rb.position = item.position;
            _rb.rotation = item.rotation;
            _rb.linearVelocity = v;
            Vector3 flat = new Vector3(v.x, 0f, v.z);
            _rb.angularVelocity = Vector3.Cross(Vector3.up, flat) * 2.2f + UnityEngine.Random.insideUnitSphere * 1.2f; // it tumbles
            if (PushOutOfHive(0f)) Log("released inside a hive part: moved out first");
            Throws++;
            HandsOnDirector.PlayerBusy(); // the crew assist waits while it flies
            GameSound.Cue("swatter_swoosh", item);
            Log("throw " + item.name + ": " + raw.ToString("0.00") + " m/s raw -> " + v.magnitude.ToString("0.00") + " m/s" +
                (spikes > 0 ? " (" + spikes + " spike samples dropped)" : "") + ", mass " + _rb.mass.ToString("0.00") + " kg, box " +
                (size.x * 100f).ToString("0") + "x" + (size.y * 100f).ToString("0") + "x" + (size.z * 100f).ToString("0") + " cm, floor y " + floorY.ToString("0.00") +
                ", hive " + (parts > 0 ? parts + " part boxes, biggest " + (bigPart.x * 100f).ToString("0") + "x" + (bigPart.y * 100f).ToString("0") + "x" + (bigPart.z * 100f).ToString("0") + " cm" : "none") +
                ", from " + Rel(item.position));
            React(true, _f.choice);
            return true;
        }

        /// <summary>One box per hive part (body, roof, lid, stand: HiveClearance.HiveParts), in the hive's own axes, so the
        /// throw bounces off the hive and not off the air in front of its door. Count; biggest part's size.</summary>
        int BuildHiveBoxes(out Vector3 biggest)
        {
            biggest = Vector3.zero;
            if (_hive == null) _hive = FindAnyObjectByType<HiveBody>();
            PartBuf.Clear();
            if (_hive == null || !_hive.gameObject.activeInHierarchy || !HiveClearance.HiveParts(_hive.transform, PartBuf)) return 0;
            Transform ht = _hive.transform;
            Vector3 hs = Abs(ht.lossyScale);
            int n = 0;
            for (int i = 0; i < PartBuf.Count; i++)
            {
                Vector3 size = Vector3.Scale(PartBuf[i].size, hs);
                if (size.x < 0.01f || size.y < 0.01f || size.z < 0.01f) continue;
                BoxCollider bc;
                if (n < _hiveParts.Count) bc = _hiveParts[n];
                else
                {
                    var go = new GameObject("ThrowHivePart") { layer = _hiveBox.layer };
                    go.transform.SetParent(_hiveBox.transform, false);
                    bc = go.AddComponent<BoxCollider>();
                    bc.sharedMaterial = _hiveBounce;
                    _hiveParts.Add(bc);
                }
                bc.transform.SetPositionAndRotation(ht.TransformPoint(PartBuf[i].center), ht.rotation);
                bc.size = size;
                bc.gameObject.SetActive(true);
                if (size.sqrMagnitude > biggest.sqrMagnitude) biggest = size;
                n++;
            }
            for (int i = n; i < _hiveParts.Count; i++) _hiveParts[i].gameObject.SetActive(false);
            _hiveBox.SetActive(n > 0);
            return n;
        }

        /// <summary>The throw body sits deeper than minDepth inside a hive part (a release next to the hive, a tunnel
        /// through a thin part): out the shortest way, moving away. Shallow contacts are left to the physics.</summary>
        bool PushOutOfHive(float minDepth)
        {
            if (!_hiveBox.activeSelf) return false;
            bool moved = false;
            for (int i = 0; i < _hiveParts.Count; i++)
            {
                var hp = _hiveParts[i];
                if (!hp.gameObject.activeSelf) continue;
                if (!Physics.ComputePenetration(_box, _rb.position, _rb.rotation, hp, hp.transform.position, hp.transform.rotation, out Vector3 dir, out float dist)) continue;
                if (dist <= minDepth) continue;
                _rb.position += dir * (dist + 0.01f);
                _proxy.transform.position = _rb.position;
                Vector3 v = _rb.linearVelocity;
                float into = Vector3.Dot(v, dir);
                if (into < 0f) _rb.linearVelocity = v - dir * into * 1.3f;
                moved = true;
            }
            return moved;
        }

        bool TouchingHive()
        {
            if (!_hiveBox.activeSelf) return false;
            Bounds mine = _box.bounds;
            mine.Expand(0.04f);
            float reach = mine.extents.magnitude * 0.6f + 0.02f;
            for (int i = 0; i < _hiveParts.Count; i++)
            {
                var hp = _hiveParts[i];
                if (!hp.gameObject.activeSelf || !hp.bounds.Intersects(mine)) continue;
                if ((hp.ClosestPoint(mine.center) - mine.center).sqrMagnitude < reach * reach) return true;
            }
            return false;
        }

        void StepFlight()
        {
            var f = _f;
            float now = Time.time;
            if (f.carry == null || f.item == null || !f.carry.Live || !f.item.gameObject.activeInHierarchy ||
                (BeeDelivery.Instance != null && BeeDelivery.Instance.IsFlying(f.item)))
            { End("taken over (the station moved on)"); return; }
            if (f.carry.Held) { End("caught"); return; }
            f.carry.Home = f.home; // HandsOn may re-aim Home at the item while it isn't parked: keep the spot

            if (f.state == S.Fly)
            {
                Vector3 p = _rb.position, v = _rb.linearVelocity;
                Vector3 head = HeadPos();
                if ((p - head).sqrMagnitude < HeadSafe * HeadSafe)
                {
                    // no speed toward the head, and a gentle drift away (set, not added up every frame)
                    v = OffHead(p, v, head, HeadSafe);
                    Vector3 n = (p - head).normalized;
                    float away = Vector3.Dot(v, n);
                    if (away < 0.3f) v += n * (0.3f - away);
                    _rb.linearVelocity = v;
                }
                float floorY = _floor.transform.position.y + 0.1f;
                f.maxHeight = Mathf.Max(f.maxHeight, p.y);
                // a floor bounce: falling, then rising, low down
                if (f.prevVy < -0.7f && v.y > 0.05f && p.y < floorY + 0.35f)
                {
                    f.bounces++;
                    f.landed = true;
                    if (now - f.lastBounce > 0.15f)
                    {
                        f.lastBounce = now;
                        _fx.Burst(new Vector3(p.x, floorY + 0.01f, p.z), QuestPerf.Particles(6), Dust, 0.45f, 0.35f, 0.02f, gravity: 0.4f, upBias: 0.6f);
                        GameSound.Cue("touchdown_light", f.item);
                    }
                }
                if (p.y < floorY + 0.25f && Mathf.Abs(v.y) < 0.3f) f.landed = true;
                if (f.landed && p.y < floorY + 0.3f) // grass: it skids to a stop instead of sliding across the room
                {
                    float keep = Mathf.Exp(-3f * Time.deltaTime);
                    _rb.linearVelocity = v = new Vector3(v.x * keep, v.y, v.z * keep);
                }
                f.prevVy = v.y;
                KnockOthers(f, p, v);
                if (Demo) // captures: how close it came to the hive's parts
                    for (int i = 0; i < _hiveParts.Count; i++)
                        if (_hiveParts[i].gameObject.activeSelf) f.hiveMin = Mathf.Min(f.hiveMin, (_hiveParts[i].ClosestPoint(p) - p).magnitude);
                if (_hiveBox.activeSelf)
                {
                    PushOutOfHive(0.03f); // tunnelled in: out again (shallow contacts are the physics' bounce)
                    bool on = TouchingHive();
                    if (on && !f.onHive) f.hiveHits++;
                    f.onHive = on;
                }

                Vector3 flatHome = p - f.home.position; flatHome.y = 0f;
                if (flatHome.magnitude > RoomRadius || p.y < floorY - 0.3f || p.y > head.y + 1.2f || now - f.releasedAt > LostSeconds)
                { Respawn(f, "out of the room"); return; }

                bool slow = v.sqrMagnitude < 0.15f * 0.15f && _rb.angularVelocity.sqrMagnitude < 1.2f * 1.2f;
                if (f.landed && slow) { if (f.slowSince < 0f) f.slowSince = now; } else f.slowSince = -1f;
                if (f.slowSince >= 0f && now - f.slowSince > SettleSeconds) { f.state = S.Rest; f.at = now; }
                else if (now - f.releasedAt > MaxFlight) BeginReturn(f);
            }
            else if (f.state == S.Rest)
            {
                if (now - f.at > RestSeconds) BeginReturn(f);
            }
        }

        void BeginReturn(Flight f)
        {
            float flew = Vector3.Distance(f.start, _rb.position);
            Log("settled " + f.item.name + " after " + (Time.time - f.releasedAt).ToString("0.0") + " s: " + flew.ToString("0.00") + " m from the release, " +
                f.bounces + " bounce" + (f.bounces == 1 ? "" : "s") + ", " + f.hiveHits + " off the hive, peak " + (f.maxHeight - f.start.y).ToString("+0.00;-0.00") +
                " m, knocked " + f.knocked + ", at " + Rel(_rb.position) + " from the eye" +
                (f.hiveMin < 100f ? ", nearest the hive " + (f.hiveMin * 100f).ToString("0") + " cm" : "") + "; floating home");
            _rb.isKinematic = true;
            _proxy.SetActive(false);
            _floor.SetActive(false);
            _hiveBox.SetActive(false);
            f.state = S.Return;
            f.at = Time.time;
            f.from = f.item.position;
            f.fromRot = f.item.rotation;
            float d = Vector3.Distance(f.from, f.home.position);
            f.dur = Mathf.Clamp(d / 1.3f, 0.6f, 1.3f);
            Vector3 mid = (f.from + f.home.position) * 0.5f + Vector3.up * (0.12f + 0.15f * d);
            if (HiveBounds(out Bounds hb) && SegmentNearBox(f.from, f.home.position, hb, 0.08f)) mid.y = Mathf.Max(mid.y, hb.max.y + 0.15f);
            f.ctrl = AvoidHead(f.from, mid, f.home.position);
            HandsOnDirector.PlayerBusy();
            _fx.Burst(f.from + Vector3.up * 0.04f, QuestPerf.Particles(8), Sparkle, 0.5f, 0.45f, 0.014f, gravity: -0.3f, upBias: 0.9f);
            GameSound.Cue("swirl_in", f.item);
        }

        /// <summary>The float home never passes through the player's head: the curve's control point moves away from a
        /// ~0.55 m head sphere (sideways and down first, so it goes round at chest height, not over the face).</summary>
        static Vector3 AvoidHead(Vector3 a, Vector3 ctrl, Vector3 b)
        {
            Vector3 head = HeadPos();
            if ((a - head).sqrMagnitude < ReturnHeadSafe * ReturnHeadSafe || (b - head).sqrMagnitude < ReturnHeadSafe * ReturnHeadSafe) return ctrl;
            for (int pass = 0; pass < 6; pass++)
            {
                float best = float.MaxValue;
                Vector3 near = ctrl;
                for (int i = 1; i < 16; i++)
                {
                    Vector3 p = Bezier(a, ctrl, b, i / 16f);
                    float d = (p - head).sqrMagnitude;
                    if (d < best) { best = d; near = p; }
                }
                float dist = Mathf.Sqrt(best);
                if (dist >= ReturnHeadSafe) break;
                Vector3 away = near - head;
                away.y = Mathf.Min(away.y, 0f); // never up over the face
                if (away.sqrMagnitude < 1e-4f)
                {
                    Vector3 ab = b - a; ab.y = 0f;
                    away = ab.sqrMagnitude > 1e-6f ? Vector3.Cross(Vector3.up, ab).normalized : Vector3.right;
                }
                ctrl += away.normalized * ((ReturnHeadSafe - dist) * 2f + 0.05f); // the curve moves half as far as its control point
            }
            return ctrl;
        }

        void StepReturn(Flight f)
        {
            float k = Mathf.Clamp01((Time.time - f.at) / f.dur);
            float e = k * k * (3f - 2f * k);
            Vector3 p = Bezier(f.from, f.ctrl, f.home.position, e);
            f.item.SetPositionAndRotation(p, Quaternion.Slerp(f.fromRot, f.home.rotation, e));
            if (Mathf.Repeat(Time.time, 0.07f) < Time.deltaTime) _fx.Burst(p, 1, Sparkle, 0.12f, 0.35f, 0.011f, gravity: -0.2f, upBias: 1f);
            if (k >= 1f)
            {
                _fx.Burst(f.home.position + Vector3.up * 0.05f, QuestPerf.Particles(10), Sparkle, 0.55f, 0.5f, 0.014f, gravity: -0.2f, upBias: 0.8f);
                HandsOnDirector.PlayerBusy(); // back on its spot: the player gets their turn before the crew helps
                End("home");
            }
        }

        void Respawn(Flight f, string why)
        {
            _fx.Burst(f.item.position, QuestPerf.Particles(8), Sparkle, 0.5f, 0.4f, 0.014f, gravity: -0.2f, upBias: 0.8f);
            f.item.SetPositionAndRotation(f.home.position, f.home.rotation);
            _fx.Burst(f.home.position + Vector3.up * 0.05f, QuestPerf.Particles(10), Sparkle, 0.55f, 0.5f, 0.014f, gravity: -0.2f, upBias: 0.8f);
            End("respawned at its spot (" + why + ")");
        }

        void End(string why)
        {
            var f = _f;
            _f = null;
            if (!_rb.isKinematic) { _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero; _rb.isKinematic = true; }
            _proxy.SetActive(false);
            _floor.SetActive(false);
            _hiveBox.SetActive(false);
            if (f != null && f.carry != null && why != "taken over (the station moved on)") f.carry.Home = f.home;
            // switched off mid-flight with nobody flying it away: it never stays lying in the grass
            else if (f != null && f.item != null && (f.carry == null || !f.carry.Live) &&
                     (BeeDelivery.Instance == null || !BeeDelivery.Instance.IsFlying(f.item)) &&
                     (f.item.position - f.home.position).sqrMagnitude > 0.05f * 0.05f)
            {
                f.item.SetPositionAndRotation(f.home.position, f.home.rotation);
                _fx.Burst(f.home.position + Vector3.up * 0.05f, QuestPerf.Particles(8), Sparkle, 0.5f, 0.45f, 0.014f, gravity: -0.2f, upBias: 0.8f);
                why += ", put back at its spot";
            }
            if (f != null) foreach (var r in f.stakes) if (r != null) r.enabled = true;
            Log((f != null && f.item != null ? f.item.name : "prop") + ": " + why + " (" + (f != null ? (Time.time - f.releasedAt).ToString("0.0") : "?") + " s after the throw)");
        }

        void KnockOthers(Flight f, Vector3 p, Vector3 v)
        {
            if (v.sqrMagnitude < 0.5f * 0.5f) return;
            Bounds mine = _box.bounds;
            for (int i = 0; i < _cands.Count; i++)
            {
                var c = _cands[i];
                if (c.t == null || c.t == f.item || f.hit.Contains(c.t)) continue;
                if (!WorldBounds(c.t, out Bounds b) || !b.Intersects(mine)) continue;
                f.hit.Add(c.t);
                Vector3 n = p - b.center; n.y = 0f;
                n = n.sqrMagnitude > 1e-6f ? n.normalized : -new Vector3(v.x, 0f, v.z).normalized;
                if (StartKnock(c.t, -n, "hit by the thrown " + f.item.name))
                {
                    f.knocked++;
                    float into = Vector3.Dot(v, n);
                    if (into < 0f) _rb.linearVelocity = v - n * (into * 1.5f); // it bounces off
                }
            }
        }

        // ------------------------------------------------------------ knock (a hard tap on a resting prop)
        void DetectKnocks()
        {
            var ph = PalHands.Instance;
            if (ph == null || _cands.Count == 0 || _rawFrame != Time.frameCount - 1)
            {
                // not read last frame: the raw history is stale, start it again (no jump from an old position)
                _rawHad[0] = _rawHad[1] = false;
                _rawSteady[0] = _rawSteady[1] = 0;
            }
            _rawFrame = Time.frameCount;
            if (ph == null || _cands.Count == 0) { _rawFrame = -10; return; }
            float now = Time.time, dt = Mathf.Max(Time.deltaTime, 1e-4f);
            for (int h = 0; h < 2; h++)
            {
                var hand = ph.Hands[h];
                // raw speed of the tip and the palm, averaged over two frames; not on the frame the palm source drops
                // in or out (centre jumps tip <-> palm, ~10 cm) or tracking just came back
                if (!hand.tracked) { _rawHad[h] = false; _rawSteady[h] = 0; }
                else
                {
                    if (_rawHad[h] && _rawPalm[h] == hand.palm)
                    {
                        Vector3 tv = (hand.tip - _rawTip[h]) / dt, cv = (hand.centre - _rawCentre[h]) / dt;
                        _rawTipV[h] = _rawSteady[h] > 0 ? (_rawTipV[h] + tv) * 0.5f : tv;
                        _rawCentreV[h] = _rawSteady[h] > 0 ? (_rawCentreV[h] + cv) * 0.5f : cv;
                        _rawSteady[h]++;
                    }
                    else _rawSteady[h] = 0;
                    _rawTip[h] = hand.tip;
                    _rawCentre[h] = hand.centre;
                    _rawPalm[h] = hand.palm;
                    _rawHad[h] = true;
                }
                bool pinch = hand.tracked && hand.pinch;
                if (_handPinched[h] && !pinch) _pinchUpAt[h] = now;
                _handPinched[h] = pinch;
                if (!hand.tracked || pinch || _rawSteady[h] < 2 || now < _handCool[h] || now - _pinchUpAt[h] < 0.4f) continue;
                float tipSpeed = _rawTipV[h].magnitude, palmSpeed = hand.palm ? _rawCentreV[h].magnitude : 0f;
                if (tipSpeed < KnockSpeed && palmSpeed < KnockSpeed) continue;
                for (int i = 0; i < _cands.Count; i++)
                {
                    var c = _cands[i];
                    if (c.t == null || (_f != null && _f.item == c.t)) continue;
                    if ((c.t.position - hand.tip).sqrMagnitude > 0.6f * 0.6f && (c.t.position - hand.centre).sqrMagnitude > 0.6f * 0.6f) continue;
                    if (!WorldBounds(c.t, out Bounds b)) continue;
                    b.Expand(KnockReach * 2f);
                    bool live = c.carry != null && c.carry.Live;
                    // the set-up prop is what the player reaches for: only an open-hand slap through it (palm inside the
                    // prop, fingers straight) knocks it, never a fingertip on the way to a pinch
                    bool tipIn = !live && tipSpeed >= KnockSpeed && b.Contains(hand.tip);
                    bool palmIn = hand.palm && palmSpeed >= KnockSpeed && b.Contains(hand.centre) && (!live || hand.open);
                    if (!tipIn && !palmIn) continue;
                    Vector3 vel = tipIn ? _rawTipV[h] : _rawCentreV[h];
                    float speed = vel.magnitude;
                    Vector3 to = b.center - (tipIn ? hand.tip : hand.centre);
                    if (Vector3.Dot(vel, to) < 0f && to.sqrMagnitude > 0.0004f) continue; // pulling out, not tapping in
                    Vector3 dir = new Vector3(vel.x, 0f, vel.z);
                    if (dir.sqrMagnitude < 0.04f) dir = new Vector3(to.x, 0f, to.z);
                    if (StartKnock(c.t, dir, (hand.ghost ? "ghost " : "") + (h == 0 ? "left" : "right") + " hand at " + speed.ToString("0.0") + " m/s"))
                    {
                        _handCool[h] = now + 0.5f;
                        React(false, c.choice);
                    }
                    break;
                }
            }
        }

        /// <summary>Tip a resting prop over (tall props fall flat, squat ones wobble) and right it again.</summary>
        public bool StartKnock(Transform t, Vector3 dir, string why)
        {
            if (t == null || !Allowed) return false;
            if (_knockedAt.TryGetValue(t, out float last) && Time.time - last < KnockCooldown) return false;
            for (int i = 0; i < _knocks.Count; i++) if (_knocks[i].t == t) return false;
            if (!WorldBounds(t, out Bounds b)) return false;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) dir = t.forward;
            dir.Normalize();
            float halfAlong = Mathf.Abs(dir.x) * b.extents.x + Mathf.Abs(dir.z) * b.extents.z;
            Vector3 pivot = new Vector3(b.center.x, b.min.y, b.center.z) + dir * halfAlong;
            float width = Mathf.Max(0.02f, 2f * halfAlong);
            bool tall = b.size.y > width * 1.15f;
            var k = new Knock
            {
                t = t, at = Time.time, tall = tall, max = tall ? Mathf.Clamp(Mathf.Atan2(b.size.y, width) * Mathf.Rad2Deg + 40f, 70f, 88f) : 24f,
                pivotLocal = t.InverseTransformPoint(pivot), axis = Vector3.Cross(Vector3.up, dir),
            };
            foreach (var tr in t.GetComponentsInChildren<Transform>())
                if (tr.name == StakeName) { k.stakes.Add(tr); k.stakeWorld.Add(default); k.stakeLocal.Add(default); }
            _knocks.Add(k);
            _knockedAt[t] = Time.time;
            Knocks++;
            GameSound.Cue("touchdown_light", t);
            Log("knock " + t.name + " (" + (tall ? "falls over, " + k.max.ToString("0") + " deg" : "wobbles") + ", " + why + ")");
            return true;
        }

        /// <summary>Degrees of tilt at time s; NaN = done.</summary>
        static float KnockAngle(Knock k, float s)
        {
            if (k.tall)
            {
                const float fall = 0.3f, bounce = 0.16f, lie = 0.65f, rise = 0.5f, wob = 0.35f;
                if (s < fall) { float x = s / fall; return k.max * x * x; }
                s -= fall;
                if (s < bounce) return k.max - 9f * Mathf.Sin(Mathf.PI * s / bounce);
                s -= bounce;
                if (s < lie) return k.max;
                s -= lie;
                if (s < rise) { float x = s / rise; return k.max * (1f - x * x * (3f - 2f * x)); }
                s -= rise;
                if (s < wob) return -6f * Mathf.Sin(Mathf.PI * 2f * s / wob) * (1f - s / wob);
                return float.NaN;
            }
            const float total = 0.85f;
            if (s >= total) return float.NaN;
            return k.max * Mathf.Sin(Mathf.Min(1f, s / 0.1f) * Mathf.PI * 0.5f) * Mathf.Cos(Mathf.Max(0f, s - 0.1f) * 18f) * Mathf.Exp(-Mathf.Max(0f, s - 0.1f) * 4.5f);
        }

        void ApplyKnocks()
        {
            float now = Time.time;
            for (int i = _knocks.Count - 1; i >= 0; i--)
            {
                var k = _knocks[i];
                var t = k.t;
                bool held = t != null && t.TryGetComponent<CarryProp>(out var cp) && cp.Held;
                if (t == null || !t.gameObject.activeInHierarchy || held || (_f != null && _f.item == t) ||
                    (BeeDelivery.Instance != null && BeeDelivery.Instance.IsFlying(t)))
                { _knocks.RemoveAt(i); continue; }
                float a = KnockAngle(k, now - k.at);
                if (float.IsNaN(a))
                {
                    if (k.tall) _fx.Burst(t.position + Vector3.up * 0.06f, QuestPerf.Particles(6), Sparkle, 0.4f, 0.4f, 0.012f, gravity: -0.2f, upBias: 0.9f);
                    _knocks.RemoveAt(i);
                    continue;
                }
                if (k.tall && !k.puffed && now - k.at >= 0.3f)
                {
                    k.puffed = true;
                    _fx.Burst(t.TransformPoint(k.pivotLocal), QuestPerf.Particles(6), Dust, 0.4f, 0.35f, 0.018f, gravity: 0.4f, upBias: 0.6f);
                }
                k.basePos = t.position;
                k.baseRot = t.rotation;
                for (int j = 0; j < k.stakes.Count; j++)
                {
                    var st = k.stakes[j];
                    if (st == null) continue;
                    k.stakeWorld[j] = new Pose(st.position, st.rotation);
                    k.stakeLocal[j] = new Pose(st.localPosition, st.localRotation);
                }
                Vector3 pivot = t.TransformPoint(k.pivotLocal);
                Quaternion q = Quaternion.AngleAxis(a, k.axis);
                t.SetPositionAndRotation(pivot + q * (k.basePos - pivot), q * k.baseRot);
                for (int j = 0; j < k.stakes.Count; j++) // the stake stays planted: only the prop tips
                    if (k.stakes[j] != null) k.stakes[j].SetPositionAndRotation(k.stakeWorld[j].position, k.stakeWorld[j].rotation);
                k.wrotePos = t.position;
                k.applied = true;
            }
        }

        /// <summary>The tilt is a visual offset: off again before anyone else (CarryProp, the delivery) moves the prop.</summary>
        void RemoveKnocks()
        {
            for (int i = 0; i < _knocks.Count; i++)
            {
                var k = _knocks[i];
                if (!k.applied || k.t == null) continue;
                k.applied = false;
                if ((k.t.position - k.wrotePos).sqrMagnitude < 1e-8f) k.t.SetPositionAndRotation(k.basePos, k.baseRot);
                for (int j = 0; j < k.stakes.Count; j++)
                    if (k.stakes[j] != null) k.stakes[j].SetLocalPositionAndRotation(k.stakeLocal[j].position, k.stakeLocal[j].rotation);
            }
        }

        // ------------------------------------------------------------ frame
        void Update() => RemoveKnocks();

        void LateUpdate()
        {
            if (_director == null) Find();
            if (_director != null && _director.Beat != _lastBeat)
            {
                _lastBeat = _director.Beat;
                if (_lastBeat == GameBeat.Pre) { _said = 0; _nextLine = 0f; }
            }
            if (Time.time >= _nextScan) Scan();
            SampleHeld();
            if (_f != null)
            {
                if (_f.state == S.Return)
                {
                    if (_f.carry == null || !_f.carry.Live || _f.carry.Held || !_f.item.gameObject.activeInHierarchy ||
                        (BeeDelivery.Instance != null && BeeDelivery.Instance.IsFlying(_f.item)))
                        End(_f.carry != null && _f.carry.Held ? "caught on the way home" : "taken over (the station moved on)");
                    else StepReturn(_f);
                }
                else
                {
                    StepFlight();
                    if (_f != null && _f.state != S.Return) _f.item.SetPositionAndRotation(_proxy.transform.position, _proxy.transform.rotation);
                }
            }
            if (Allowed) DetectKnocks();
            ApplyKnocks();
        }

        void Find()
        {
            _director = FindAnyObjectByType<GameDirector>();
            _net = FindAnyObjectByType<GameNet>();
            _pilot = FindAnyObjectByType<GameAutopilot>();
            _bw = FindAnyObjectByType<BeeswaxController>();
            _hive = FindAnyObjectByType<HiveBody>();
        }

        void SampleHeld()
        {
            CarryProp held = null;
            for (int i = 0; i < _carries.Count; i++)
                if (_carries[i] != null && _carries[i].Live && _carries[i].Held) { held = _carries[i]; break; }
            bool fresh = held != null && (held != _sampled || !_heldLast);
            _heldLast = held != null;
            if (held == null) return; // keep the last samples: EndGrab comes after the last held frame
            if (fresh) { _sampled = held; _sn = 0; _si = 0; } // a new grab: nothing from the last one counts
            _sp[_si] = held.HandPoint; // the hand, not the prop (CarryProp smooths the prop, which hides tracking jumps)
            _st[_si] = Time.time;
            _si = (_si + 1) % N;
            _sn = Mathf.Min(_sn + 1, N);
        }

        /// <summary>4x a second: which props are out (live carries, parked deliveries, set-down tools) and can be knocked.</summary>
        void Scan()
        {
            _nextScan = Time.time + 0.25f;
            if (_deliveries == null || Time.time >= _nextFind)
            {
                _deliveries = FindObjectsByType<StationDelivery>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                _nextFind = Time.time + (_deliveries.Length == 0 ? 2f : 4f); // none (delivery off): look again rarely
            }
            _cands.Clear();
            _carries.Clear();
            _choiceOf.Clear();
            var del = BeeDelivery.Instance;
            for (int d = 0; d < _deliveries.Length; d++)
            {
                var sd = _deliveries[d];
                if (sd == null) continue;
                var st = sd.GetComponent<StationBase>();
                var ids = GameIds.Choices[(int)sd.Station];
                for (int i = 0; i < sd.Count; i++)
                {
                    var t = sd.Item(i);
                    if (t == null) continue;
                    var cp = t.GetComponent<CarryProp>();
                    string choice = i < ids.Length ? ids[i] : "";
                    if (cp != null) { _carries.Add(cp); _choiceOf[cp] = choice; }
                    if (!t.gameObject.activeInHierarchy || t.lossyScale.x < 1e-3f) continue;
                    if (del != null && del.IsFlying(t)) continue;
                    if (cp != null && cp.Held) continue;
                    if (Docked(t)) continue; // the smoker in the lever cradle: a poke there pumps it, no knock
                    // a parked choice of the station that is choosing is a button (a poke orders it), in every mode:
                    // a fast poke must not order and knock at once
                    if ((cp == null || !cp.Live) && st != null && (st.Choosing || st.Busy)) continue;
                    _cands.Add(new Cand { t = t, carry = cp, choice = choice });
                }
            }
        }

        /// <summary>Sitting in the honey lever's cradle while the lever is live (HandsOn: a poke there is a pump).</summary>
        static bool Docked(Transform t)
        {
            var lever = HandsOnDirector.Instance != null ? HandsOnDirector.Instance.Lever : null;
            if (lever == null || !lever.Live || !lever.isActiveAndEnabled) return false;
            Vector3 d = t.position - lever.CradlePose.position;
            d.y = 0f;
            return d.sqrMagnitude < 0.18f * 0.18f;
        }

        string ChoiceOf(CarryProp c) => c != null && _choiceOf.TryGetValue(c, out var s) ? s : "";

        // ------------------------------------------------------------ Beeswax
        void React(bool thrown, string choice)
        {
            if (!Allowed || _net == null) return;
            float now = Time.time;
            if (_said >= 3 || now < _nextLine) return;
            if (!Free()) return;
            if (_said > 0 && UnityEngine.Random.value > 0.5f) { _nextLine = now + 8f; return; } // rare, not every time
            GameLine line;
            if (!thrown) line = RideLines.Get("room_ack_grievance", "Grievance filed.");
            else if (choice == GameIds.Choice.SugarSyrup) line = RideLines.Get("room_throw_syrup", "Careful! That's the GOOD syrup.");
            else line = _said % 2 == 0 ? RideLines.Get("room_throw_property", "Hey! That's union property!")
                                       : RideLines.Get("room_throw_writeup", "Who threw that? I'm writing it up.");
            _net.Say(line);
            _said++;
            _nextLine = now + 35f;
            Log("Beeswax: \"" + line.caption + "\" (" + line.id + ")");
        }

        /// <summary>He isn't mid-line, in a show moment, inside the hive, or in a beat that owns him.</summary>
        bool Free()
        {
            if (_net.IsSpeaking || BeeswaxMotion.ShowMoment) return false;
            if (_bw != null && (!_bw.Visible || _bw.Inside)) return false;
            if (HandsOnDirector.Instance != null && HandsOnDirector.Instance.WaitingForLaunch) return false;
            if (_director == null) return true;
            var b = _director.Beat;
            return b == GameBeat.Smoker || b == GameBeat.Feed || b == GameBeat.Wasps || b == GameBeat.Heat;
        }

        // ------------------------------------------------------------ geometry
        float FloorY(CarryProp c)
        {
            if (Bloom.MeadowLayout.Active) return Bloom.MeadowLayout.FloorY;
            if (!float.IsNegativeInfinity(c.FloorY)) return c.FloorY;
            return HiveBounds(out Bounds hb) ? hb.min.y : c.transform.position.y - 1f;
        }

        Bounds _hb;
        float _hbAt = -10f;
        bool _hbOk;

        bool HiveBounds(out Bounds b)
        {
            if (Time.time - _hbAt > 1f)
            {
                _hbAt = Time.time;
                _hbOk = false;
                if (_hive == null) _hive = FindAnyObjectByType<HiveBody>();
                PartBuf.Clear();
                if (_hive != null && HiveClearance.HiveParts(_hive.transform, PartBuf))
                {
                    // the hive's own parts (no glow, halo, shadow, text: HiveClearance skips them), as a world box
                    Matrix4x4 m = _hive.transform.localToWorldMatrix;
                    foreach (var pb in PartBuf)
                        for (int k = 0; k < 8; k++)
                        {
                            Vector3 p = m.MultiplyPoint3x4(pb.center + Vector3.Scale(pb.extents, new Vector3((k & 1) != 0 ? 1f : -1f, (k & 2) != 0 ? 1f : -1f, (k & 4) != 0 ? 1f : -1f)));
                            if (!_hbOk) { _hb = new Bounds(p, Vector3.zero); _hbOk = true; } else _hb.Encapsulate(p);
                        }
                }
            }
            b = _hb;
            return _hbOk;
        }

        static readonly List<Renderer> RBuf = new List<Renderer>(16);
        static readonly Vector3[] Corners = new Vector3[8];

        static bool Usable(Renderer r, Transform root)
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r is ParticleSystemRenderer) return false;
            if (r.GetComponentInParent<ChoiceButton>() != null) return false; // its price tag
            if (r.GetComponent<TextMesh>() != null) return false;
            return true;
        }

        /// <summary>The prop's mesh bounds in its own space (price tag and text left out).</summary>
        static bool LocalBounds(Transform root, out Bounds lb)
        {
            lb = default;
            bool any = false;
            root.GetComponentsInChildren(false, RBuf);
            Matrix4x4 toLocal = root.worldToLocalMatrix;
            for (int i = 0; i < RBuf.Count; i++)
            {
                var r = RBuf[i];
                if (!Usable(r, root)) continue;
                Bounds mb;
                if (r is SkinnedMeshRenderer sk) mb = sk.localBounds;
                else if (r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null) mb = mf.sharedMesh.bounds;
                else continue;
                Matrix4x4 m = toLocal * r.localToWorldMatrix;
                Vector3 c = mb.center, e = mb.extents;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 p = m.MultiplyPoint3x4(c + new Vector3((k & 1) != 0 ? e.x : -e.x, (k & 2) != 0 ? e.y : -e.y, (k & 4) != 0 ? e.z : -e.z));
                    if (!any) { lb = new Bounds(p, Vector3.zero); any = true; } else lb.Encapsulate(p);
                }
            }
            return any;
        }

        static bool WorldBounds(Transform root, out Bounds b)
        {
            b = default;
            bool any = false;
            root.GetComponentsInChildren(false, RBuf);
            for (int i = 0; i < RBuf.Count; i++)
            {
                var r = RBuf[i];
                if (!Usable(r, root)) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any;
        }

        static bool SegmentNearBox(Vector3 a, Vector3 b, Bounds box, float margin)
        {
            box.Expand(margin * 2f);
            for (int i = 0; i <= 8; i++) if (box.Contains(Vector3.Lerp(a, b, i / 8f))) return true;
            return false;
        }

        static Vector3 OffHead(Vector3 p, Vector3 v, Vector3 head, float radius)
        {
            Vector3 d = p - head;
            float m = d.magnitude;
            if (m > radius || m < 1e-4f) return v;
            Vector3 n = d / m;
            float toward = Vector3.Dot(v, n);
            return toward < 0f ? v - n * toward : v;
        }

        static Vector3 HeadPos()
        {
            var del = BeeDelivery.Instance;
            Transform h = del != null ? del.Head : (Camera.main != null ? Camera.main.transform : null);
            return h != null ? h.position : Vector3.up * 1.6f;
        }

        static Vector3 Bezier(Vector3 a, Vector3 c, Vector3 b, float t)
        {
            float u = 1f - t;
            return u * u * a + 2f * u * t * c + t * t * b;
        }

        /// <summary>"0.8 m ahead, 0.4 m left, 1.3 m down" from the player's eye (logs).</summary>
        static string Rel(Vector3 p)
        {
            var del = BeeDelivery.Instance;
            Transform h = del != null ? del.Head : (Camera.main != null ? Camera.main.transform : null);
            if (h == null) return p.ToString("0.00");
            Vector3 f = Vector3.ProjectOnPlane(h.forward, Vector3.up).normalized, r = Vector3.Cross(Vector3.up, f), d = p - h.position;
            float a = Vector3.Dot(d, f), s = Vector3.Dot(d, r);
            return a.ToString("0.00") + " m ahead, " + Mathf.Abs(s).ToString("0.00") + " m " + (s < 0f ? "left" : "right") + ", " + (-d.y).ToString("0.00") + " m down";
        }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        static void Log(string msg) => Debug.Log("[VBS][Throw] " + msg);

        // ------------------------------------------------------------ for ThrowDemo
        /// <summary>The live carried prop that isn't flying (the one the set-up step wants), or null.</summary>
        public CarryProp LiveCarry()
        {
            for (int i = 0; i < _carries.Count; i++)
            {
                var c = _carries[i];
                if (c != null && c.Live && !c.Held && (_f == null || _f.carry != c) && c.gameObject.activeInHierarchy) return c;
            }
            return null;
        }

        /// <summary>The middle of the hive's parts (the demo aims at it), false without a hive.</summary>
        public bool HiveCentre(out Vector3 c)
        {
            bool ok = HiveBounds(out Bounds b);
            c = b.center;
            return ok;
        }

        /// <summary>The knockable prop nearest to a point (desktop key), or null.</summary>
        public Transform NearestKnockable(Vector3 p)
        {
            Transform best = null;
            float bd = float.MaxValue;
            for (int i = 0; i < _cands.Count; i++)
            {
                var t = _cands[i].t;
                if (t == null || (_f != null && _f.item == t)) continue;
                float d = (t.position - p).sqrMagnitude;
                if (d < bd) { bd = d; best = t; }
            }
            return best;
        }
    }
}
