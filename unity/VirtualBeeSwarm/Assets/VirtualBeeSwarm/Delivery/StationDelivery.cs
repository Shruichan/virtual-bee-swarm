using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// A station's choices, delivered (PLAN v3.1 V15). Sits next to a StationBase (same GameObject); StationBase calls
    /// it at six points (INTEGRATION.md). With delivery.mode off it does nothing and puts every prop, scale and button
    /// back exactly as the scene builder made them, so today's buttons work unchanged.
    ///
    /// On: the choices fly in together when the prompt starts and park, perfectly still, in an arc centred on the hive
    /// (20+ cm apart, within +-25 deg of gaze). v3.3: the arc sits between the player and the hive, ~0.2 m in front of
    /// its face and high enough that every prop is seen above the roof (not over the hive's hexagons / sign, not over
    /// the rocket), at most 18 cm below the eye; without a head, 25 cm over the table's front edge. The props are the
    /// buttons: each ChoiceButton becomes a price tag at the foot of its prop, and the prop gets a trigger collider (24 cm
    /// max) that forwards pokes and clicks to it. Props are normalized to a 17 cm box. On the poke: APPROVED / GRIEVANCE
    /// stamp, 1.3x, facing the eye, in front of the alarm bees.
    /// Wrong: the station's own consequence plays, then a red repo crew hauls it out. Right: its crew sets it down on
    /// its spot (TableAnchor/Spots/Smoker|Feeder|Door|Dish) and the leftovers fly back out.
    /// Missing props (Swatter, SprayBottle, Crowbar, Reducer, NothingTag) and spots are built at runtime as placeholders.
    ///
    /// v3.2 polish: a choice accepts a poke only once it is parked (AcceptsPoke / AllParked; StationBase waits for it
    /// before Choosing). The stamps are the StampApproved / StampGrievance art (slammed down by code), text stamp if the
    /// prefab is missing. Heavy items (the jar, V17) shudder while a crew carries them and raise BuzzCue (pitch 1.3).
    /// V19 hint: 8 s after the choices are offered without a pick, the right one leans toward the hive (BuzzCue 1.15).
    /// Every visual offset is applied in LateUpdate and taken off again before anyone else moves the item (Update at
    /// -25, before BeeDelivery, and at the top of every hook), so BeeDelivery and the stations never see it.
    /// </summary>
    [DefaultExecutionOrder(-25)]
    public class StationDelivery : MonoBehaviour
    {
        /// <summary>Sound hook (no audio here): (item, pitch scale, why). Heavy flight = 1.3, eager hint = 1.15.</summary>
        public static event Action<Transform, float, string> BuzzCue;

        [SerializeField] GameStation station;
        [Tooltip("Read the station from a StationBase on this GameObject (else use the field above).")]
        [SerializeField] bool stationFromComponent = true;
        [SerializeField] BeeDelivery delivery;
        [Tooltip("The hive root (TableAnchor/Hive). Empty: the HiveBody in the scene.")]
        [SerializeField] Transform hiveRoot;
        [Tooltip("Parent of this station's props (empty: child 'Props').")]
        [SerializeField] Transform propsRoot;
        [Tooltip("One per choice, in GameIds.Choices order. Empty entries: found under Props by name, else a placeholder.")]
        [SerializeField] Transform[] items = new Transform[0];
        [Tooltip("Where the right choice is set down. Empty: TableAnchor/Spots/<name>, made if missing.")]
        [SerializeField] Transform spot;

        [Header("Arc")]
        [Tooltip("Height over the table when there is no head to place the arc against.")]
        [SerializeField] float arcHeight = 0.25f;
        [SerializeField] float arcSpacing = 0.21f;
        [SerializeField] float minSpacing = 0.2f;
        [Tooltip("v3.3: the arc's centre moves this far to the player's right (-: left, away from Beeswax's presenter spot).")]
        [SerializeField] float arcSideShift = -0.03f;
        [Tooltip("v3.3: the arc sits this far in front of the hive's face, toward the player...")]
        [SerializeField] float arcInFrontOfHive = 0.2f;
        [Tooltip("...but never nearer the eye than this (horizontal).")]
        [SerializeField] float minArcDistance = 0.42f;
        [Tooltip("Hive roof (plus the bees on it) above the table, and half its depth: the props clear that line of sight.")]
        [SerializeField] float hiveTop = 0.34f;
        [SerializeField] float hiveHalfDepth = 0.13f;
        [SerializeField] float clearAboveRoof = 0.05f;
        [SerializeField] float minArcHeight = 0.3f;
        [SerializeField] float maxBelowEyeMin = 0.18f;
        [Tooltip("Front edge of the table, from the hive centre toward the player (HivePlacement puts the hive 22 cm in).")]
        [SerializeField] float frontEdge = 0.22f;
        [SerializeField] float maxGazeDegrees = 25f;
        [SerializeField] float arcBend = 0.04f;

        [Header("Props")]
        [Tooltip("v3.3: 17 cm (was 12): readable from the player's spot.")]
        [SerializeField] float normalizeTo = 0.17f;
        [SerializeField] float maxCollider = 0.24f;
        [Tooltip("World scale of the price tag (the ChoiceButton) under each prop.")]
        [SerializeField] float tagScale = 0.8f;
        [Tooltip("v3.3: the price tag's centre sits this far above the prop's bottom: it covers only the prop's foot, and never the hive's face.")]
        [SerializeField] float tagLift = 0.012f;

        [Header("Order")]
        [Tooltip("Right pick: set down on the spot right away (the outcome plays on the spot). Off: after the outcome visuals (feed: the pour).")]
        [SerializeField] bool rightLandsFirst = true;
        [SerializeField] bool rightLandsFirstFromStation = true;

        [Header("Stamps (art)")]
        [Tooltip("Art/StampApproved/StampApproved.prefab (auto-filled in the editor). Empty: the text stamp.")]
        [SerializeField] GameObject approvedStampPrefab;
        [Tooltip("Art/StampGrievance/StampGrievance.prefab (auto-filled in the editor). Empty: the text stamp.")]
        [SerializeField] GameObject grievanceStampPrefab;
        [SerializeField] float stampSlamSeconds = 0.08f;
        [SerializeField] float stampSettleSeconds = 0.1f;
        [SerializeField] float stampStartScale = 1.6f;
        [Tooltip("v3.3: the stamp art's settled size (x its prefab size).")]
        [SerializeField] float stampScale = 1.3f;
        [Tooltip("v3.3: the stamp sits this far in front of the prop, toward the eye (clear of the alarm bees).")]
        [SerializeField] float stampForward = 0.05f;

        [Header("Heavy (V17)")]
        [SerializeField] float heavyBuzzPitch = 1.3f;
        [SerializeField] float strainDegrees = 4f;
        [SerializeField] float strainHz = 7f;

        [Header("Hint (V19)")]
        [Tooltip("Seconds without a pick (from when the choices accept pokes) before the right one leans toward the hive. 0 = off.")]
        [SerializeField] float hintAfterSeconds = 8f;
        [SerializeField] float hintLean = 0.035f;
        [SerializeField] float hintTiltDegrees = 14f;
        [SerializeField] float hintEaseSeconds = 0.6f;
        [SerializeField] float eagerBuzzPitch = 1.15f;

        public GameStation Station { get { Resolve(); return station; } }
        public bool Active => BeeDelivery.Mode != DeliveryMode.Off && Delivery != null;
        public bool Parked => _parked;
        public bool StampArtReady => approvedStampPrefab != null && grievanceStampPrefab != null;
        /// <summary>A heavy choice (the jar) is being flown in right now by a crew (delivery on).</summary>
        public bool HeavyInbound
        {
            get
            {
                if (!Active || BeeDelivery.Mode != DeliveryMode.On) return false;
                Resolve();
                for (int i = 0; i < _n; i++)
                {
                    if (_gone[i] || _item[i] == null) continue;
                    var di = _item[i].GetComponent<DeliveryItem>();
                    if (di != null && di.Heavy && Delivery.IsFlying(_item[i])) return true;
                }
                return false;
            }
        }
        /// <summary>The V19 hint is showing now.</summary>
        public bool Hinting => _hintOn;
        public int Count { get { Resolve(); return _n; } }
        public Transform Item(int i) { Resolve(); return i >= 0 && i < _n ? _item[i] : null; }
        public Transform Spot { get { Resolve(); return spot; } }

        BeeDelivery Delivery => delivery != null ? delivery : (delivery = BeeDelivery.Instance != null ? BeeDelivery.Instance : FindAnyObjectByType<BeeDelivery>());

        bool _resolved, _attached, _parked, _rightSent;
        int _n;
        string[] _choice;
        Transform[] _item;
        bool[] _placeholder, _gone, _legacyActive;
        Vector3[] _legacyPos, _legacyScale;
        Quaternion[] _legacyRot;
        ChoiceButton[] _button;
        Transform[] _buttonParent, _tag;
        // v3.2: stamps (art), visual offsets (hint lean, heavy strain), heavy flights
        Transform[] _stampMount, _stampArtA, _stampArtG;
        float[] _stampAt;
        bool[] _offApplied, _heavyCued;
        Vector3[] _offBasePos, _offAppliedPos, _lastBasePos;
        Quaternion[] _offBaseRot, _offAppliedRot;
        float _hintArmedAt = -1f, _hintStartedAt = -1f, _hintEndAt = -1f, _hintW;
        bool _hintOn, _hintFiredThisOffer;
        int _hintIndex = -1;
        Vector3[] _buttonPos, _buttonScale;
        Quaternion[] _buttonRot;
        BoxCollider[] _collider;
        ChoicePropTarget[] _target;
        readonly Pose[] _slots = new Pose[4];

        static readonly Dictionary<string, string> PropNames = new Dictionary<string, string>
        {
            { GameIds.Choice.Swat, "Swatter" }, { GameIds.Choice.SprayWater, "SprayBottle" }, { GameIds.Choice.Smoker, "Smoker" },
            { GameIds.Choice.StoreHoney, "HoneyJar" }, { GameIds.Choice.SugarSyrup, "SyrupBottle" },
            { GameIds.Choice.BiggerDoor, "Crowbar" }, { GameIds.Choice.SmallerDoor, "Reducer" },
            { GameIds.Choice.DeepBowl, "DeepBowl" }, { GameIds.Choice.PebbleDish, "PebbleDish" }, { GameIds.Choice.Nothing, "NothingTag" },
        };

        public static string PropName(string choice) => PropNames.TryGetValue(choice, out var n) ? n : choice;
        public static string SpotName(GameStation s) => s == GameStation.Smoker ? "Smoker" : s == GameStation.Feed ? "Feeder" : s == GameStation.Wasps ? "Door" : "Dish";

        /// <summary>Builder / test wiring.</summary>
        public void Configure(GameStation s, BeeDelivery d, Transform hive, Transform props, Transform[] choiceItems, Transform landingSpot)
        {
            station = s;
            stationFromComponent = false;
            delivery = d;
            hiveRoot = hive;
            propsRoot = props;
            items = choiceItems ?? new Transform[0];
            spot = landingSpot;
            _resolved = false;
        }

        // ------------------------------------------------------------ resolve (lazy: StationBase sets its station in Awake)
        void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            if (stationFromComponent)
            {
                var sb = GetComponent<StationBase>();
                if (sb != null) station = sb.Station;
            }
            if (rightLandsFirstFromStation) rightLandsFirst = station != GameStation.Feed; // feed: the pour first, then the set-down
            if (hiveRoot == null)
            {
                var hb = FindAnyObjectByType<HiveBody>();
                if (hb != null) hiveRoot = hb.transform;
            }
            if (propsRoot == null) propsRoot = transform.Find("Props");
            if (propsRoot == null) propsRoot = transform;

            _choice = GameIds.Choices[(int)station];
            _n = _choice.Length;
            _item = new Transform[_n];
            _placeholder = new bool[_n];
            _gone = new bool[_n];
            _legacyActive = new bool[_n];
            _legacyPos = new Vector3[_n];
            _legacyScale = new Vector3[_n];
            _legacyRot = new Quaternion[_n];
            _button = new ChoiceButton[_n];
            _buttonParent = new Transform[_n];
            _tag = new Transform[_n];
            _buttonPos = new Vector3[_n];
            _buttonScale = new Vector3[_n];
            _buttonRot = new Quaternion[_n];
            _collider = new BoxCollider[_n];
            _target = new ChoicePropTarget[_n];
            _stampMount = new Transform[_n];
            _stampArtA = new Transform[_n];
            _stampArtG = new Transform[_n];
            _stampAt = new float[_n];
            _offApplied = new bool[_n];
            _heavyCued = new bool[_n];
            _offBasePos = new Vector3[_n];
            _offAppliedPos = new Vector3[_n];
            _lastBasePos = new Vector3[_n];
            _offBaseRot = new Quaternion[_n];
            _offAppliedRot = new Quaternion[_n];
            for (int i = 0; i < _n; i++) _stampAt[i] = -1f;

            var buttons = GetComponentsInChildren<ChoiceButton>(true);
            for (int i = 0; i < _n; i++)
            {
                Transform t = i < items.Length ? items[i] : null;
                if (t == null) t = propsRoot.Find(PropName(_choice[i]));
                if (t == null)
                {
                    t = DeliveryPlaceholders.Build(_choice[i], propsRoot);
                    _placeholder[i] = true;
                    t.gameObject.SetActive(false);
                }
                _item[i] = t;
                _legacyActive[i] = t.gameObject.activeSelf;
                _legacyPos[i] = t.localPosition;
                _legacyRot[i] = t.localRotation;
                _legacyScale[i] = t.localScale;
                for (int b = 0; b < buttons.Length; b++)
                    if (buttons[b] != null && buttons[b].ChoiceId == _choice[i]) { _button[i] = buttons[b]; break; }
                if (_button[i] != null)
                {
                    _buttonParent[i] = _button[i].transform.parent;
                    _buttonPos[i] = _button[i].transform.localPosition;
                    _buttonRot[i] = _button[i].transform.localRotation;
                    _buttonScale[i] = _button[i].transform.localScale;
                }
                var di = t.GetComponent<DeliveryItem>();
                if (di == null) { di = t.gameObject.AddComponent<DeliveryItem>(); di.weight = DeliveryPlaceholders.Weight(_choice[i]); }
            }
            if (spot == null) spot = FindOrMakeSpot();
        }

        Transform FindOrMakeSpot()
        {
            if (hiveRoot == null) return null;
            Transform table = hiveRoot.parent != null ? hiveRoot.parent : hiveRoot;
            var spots = table.Find("Spots");
            if (spots == null)
            {
                spots = new GameObject("Spots").transform;
                spots.SetParent(table, false);
            }
            string name = SpotName(station);
            var s = spots.Find(name);
            if (s != null) return s;
            s = new GameObject(name).transform;
            s.SetParent(spots, false);
            Vector3 local;
            switch (station)
            {
                case GameStation.Smoker: local = new Vector3(-0.2f, 0f, -0.08f); break;      // left of the door
                case GameStation.Feed: local = new Vector3(0f, 0.3125f, 0f); break;          // the top feeder, on the roof
                case GameStation.Wasps: local = new Vector3(0f, 0.025f, -0.135f); break;     // in the door
                default: local = new Vector3(0.2f, 0f, -0.1f); break;                        // right of the door
            }
            s.position = hiveRoot.TransformPoint(local);
            s.rotation = hiveRoot.rotation;
            return s;
        }

        // ------------------------------------------------------------ hooks from StationBase
        /// <summary>StationBase.OnEnter, before OnStationEnter: delivery on = props become buttons and wait outside; off = today's layout.</summary>
        public void Prepare()
        {
            Resolve();
            RestoreAll();
            ClearHint(false);
            HideStamps();
            _parked = false;
            _rightSent = false;
            for (int i = 0; i < _n; i++) _gone[i] = false;
            if (Active)
            {
                Attach();
                for (int i = 0; i < _n; i++) Delivery.Release(_item[i], hide: true);
            }
            else Detach();
        }

        /// <summary>The prompt line starts: every choice flies in and parks in the arc. Idempotent.</summary>
        public void ParkChoices()
        {
            Resolve();
            if (!Active || _parked) return;
            RestoreAll();
            if (!_attached) Attach();
            _parked = true;
            for (int i = 0; i < _n; i++) _heavyCued[i] = false;
            ComputeArc();
            int slot = 0;
            for (int i = 0; i < _n; i++)
            {
                if (_gone[i]) { slot++; continue; }
                Delivery.Park(_item[i], _slots[slot++]);
            }
            Debug.Log("[VBS][Delivery] " + GameIds.StationName(station) + ": " + _n + " choices on their way to the arc");
        }

        /// <summary>OfferChoices: make sure the choices are out (a subclass intro that never called ParkChoices). True = all parked.</summary>
        public bool EnsureParked()
        {
            if (!Active) return true;
            if (!_parked) ParkChoices();
            return AllParked;
        }

        /// <summary>Every choice still in play is parked and still (not flying in, not mid-swirl). Delivery off: true.</summary>
        public bool AllParked
        {
            get
            {
                if (!Active) return true;
                Resolve();
                for (int i = 0; i < _n; i++) if (!_gone[i] && !ParkedAt(i)) return false;
                return true;
            }
        }

        /// <summary>A poke on this choice counts: delivery off, or the prop is parked (not mid-flight).</summary>
        public bool AcceptsPoke(string choice)
        {
            if (!Active) return true;
            int i = IndexOf(choice);
            return i < 0 || ParkedAt(i);
        }

        public bool AcceptsPokeAt(int i) => !Active || (i >= 0 && i < _n && ParkedAt(i));

        bool ParkedAt(int i)
        {
            var t = _item[i];
            if (t == null || !t.gameObject.activeInHierarchy) return false;
            var di = t.GetComponent<DeliveryItem>();
            return di != null && di.parked && !Delivery.IsFlying(t);
        }

        /// <summary>StationBase: the choices accept pokes now (log + arm the V19 hint). waited = seconds spent waiting for the arc.</summary>
        public void OnChoicesOffered(float waited)
        {
            Resolve();
            if (Active)
                Debug.Log("[VBS][Delivery] " + GameIds.StationName(station) + " choices accept pokes (all parked" +
                          (waited > 0.005f ? ", waited " + waited.ToString("0.00") + " s for the arc" : "") + ")");
            _hintArmedAt = hintAfterSeconds > 0f ? Time.time : -1f;
            _hintFiredThisOffer = false;
        }

        /// <summary>Same frame as the poke / choice.request / timeout pick: stamp it; a right pick lands first (except feed).</summary>
        public void OnChoiceMade(string choice, bool correct)
        {
            Resolve();
            RestoreAll();
            ClearHint(true);
            if (!Active) return;
            int i = IndexOf(choice);
            if (i < 0) return;
            if (!_parked) ParkChoices(); // picked before the arc (key, operator): the others still come and go
            if (!StampArt(i, correct)) Delivery.Stamp(_item[i], correct);
            if (correct && rightLandsFirst) LandRight(i);
        }

        /// <summary>The station's ShowOutcome coroutine finished: wrong = the repo crew hauls it out; right (feed) = set down now.</summary>
        public void OnOutcomeShown(string choice, bool correct)
        {
            RestoreAll();
            if (!Active) return;
            int i = IndexOf(choice);
            if (i < 0) return;
            if (!correct)
            {
                _gone[i] = true;
                Delivery.HaulAway(_item[i], repo: true);
            }
            else if (!_rightSent) LandRight(i);
        }

        void LandRight(int i)
        {
            if (_rightSent) return;
            _rightSent = true;
            string id = _choice[i];
            Delivery.Deliver(_item[i], spot, DeliveryStyle.Crown, 0, () => Debug.Log("[VBS][Delivery] " + GameIds.StationName(station) + ": " + id + " set down"));
            for (int k = 0; k < _n; k++)
            {
                if (k == i || _gone[k]) continue;
                _gone[k] = true;
                if (_button[k] != null) _button[k].SetInteractable(false);
                Delivery.Return(_item[k]);
            }
        }

        /// <summary>StationBase.OnExit (before props are hidden): the right one ends on its spot if solved, the rest are gone, no bees left.</summary>
        public void OnStationExit(bool solved)
        {
            Resolve();
            RestoreAll();
            ClearHint(false);
            if (!_attached) return;
            int right = IndexOf(GameIds.CorrectChoice[(int)station]);
            for (int i = 0; i < _n; i++)
            {
                if (i == right && solved && Delivery != null)
                {
                    if (!_rightSent) LandRight(i);
                    Delivery.Finish(_item[i]);
                    Delivery.Release(_item[i], hide: false); // keeps it on its spot, crew gone
                }
                else if (Delivery != null) { Delivery.Release(_item[i], hide: true); HideStamp(i); }
            }
            _parked = false;
        }

        /// <summary>StationBase.OnResetState (before OnStationReset): everything back to the builder's layout.</summary>
        public void ResetItems()
        {
            Resolve();
            RestoreAll();
            ClearHint(false);
            HideStamps();
            // v3.3: only items this delivery took over. With delivery off they were never attached, and releasing them
            // (hide) left the builder's props (the smoker, the jar) hidden from the second run on.
            if (Delivery != null && _attached)
                for (int i = 0; i < _n; i++) Delivery.Release(_item[i], hide: true);
            _parked = false;
            _rightSent = false;
            for (int i = 0; i < _n; i++) _gone[i] = false;
            if (!Active) Detach();
        }

        int IndexOf(string choice)
        {
            Resolve();
            for (int i = 0; i < _n; i++) if (_choice[i] == choice) return i;
            return -1;
        }

        // ------------------------------------------------------------ attach / detach (mode on/swirl <-> off)
        void Attach()
        {
            if (_attached) return;
            _attached = true;
            for (int i = 0; i < _n; i++)
            {
                var t = _item[i];
                var di = t.GetComponent<DeliveryItem>();
                // measure at the builder's scale, then normalize to a 12 cm box
                t.localScale = _legacyScale[i];
                di.Measure();
                Vector3 size = Vector3.Scale(di.localBounds.size, t.lossyScale);
                float max = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                if (normalizeTo > 0f && max > 1e-4f) t.localScale = _legacyScale[i] * (normalizeTo / max);
                di.Measure();

                // the button becomes a price tag under the prop
                if (_button[i] != null)
                {
                    if (_tag[i] == null)
                    {
                        _tag[i] = new GameObject("[VBS] PriceTag").transform;
                        _tag[i].SetParent(t, false);
                    }
                    _tag[i].gameObject.SetActive(true);
                    Vector3 ls = t.lossyScale;
                    float inv = 1f / Mathf.Max(1e-4f, Mathf.Abs(ls.x));
                    _tag[i].localScale = new Vector3(tagScale * inv, tagScale / Mathf.Max(1e-4f, Mathf.Abs(ls.y)), tagScale / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));
                    Bounds lb = di.localBounds;
                    _tag[i].localPosition = new Vector3(lb.center.x, lb.min.y + tagLift / Mathf.Max(1e-4f, Mathf.Abs(ls.y)), lb.min.z - 0.012f / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));
                    _tag[i].localRotation = Quaternion.Euler(20f, 0f, 0f);
                    var bt = _button[i].transform;
                    bt.SetParent(_tag[i], false);
                    bt.localPosition = Vector3.zero;
                    bt.localRotation = Quaternion.identity;
                    bt.localScale = _buttonScale[i];
                    if (!bt.gameObject.activeSelf) bt.gameObject.SetActive(true);
                }

                // the prop itself is the button: one trigger collider, capped
                if (_collider[i] == null)
                {
                    _collider[i] = t.GetComponent<BoxCollider>();
                    if (_collider[i] == null) _collider[i] = t.gameObject.AddComponent<BoxCollider>();
                }
                var col = _collider[i];
                col.isTrigger = true;
                Vector3 lossy = t.lossyScale;
                Vector3 bsize = di.localBounds.size;
                col.center = di.localBounds.center;
                col.size = new Vector3(
                    Mathf.Min(bsize.x, maxCollider / Mathf.Max(1e-4f, Mathf.Abs(lossy.x))),
                    Mathf.Min(bsize.y, maxCollider / Mathf.Max(1e-4f, Mathf.Abs(lossy.y))),
                    Mathf.Min(bsize.z, maxCollider / Mathf.Max(1e-4f, Mathf.Abs(lossy.z))));
                col.enabled = true;
                if (_target[i] == null)
                {
                    _target[i] = t.GetComponent<ChoicePropTarget>();
                    if (_target[i] == null) _target[i] = t.gameObject.AddComponent<ChoicePropTarget>();
                }
                _target[i].Bind(_button[i], this, i);
                _target[i].enabled = true;
                if (Delivery != null) Delivery.Remeasure(t);
            }
        }

        void Detach()
        {
            if (!_attached)
            {
                // the builder's layout: its props shown (whatever hid them), the runtime placeholders hidden
                for (int i = 0; i < _n; i++)
                {
                    bool on = _legacyActive[i] && !_placeholder[i];
                    if (_item[i].gameObject.activeSelf != on) _item[i].gameObject.SetActive(on);
                }
                return;
            }
            _attached = false;
            for (int i = 0; i < _n; i++)
            {
                var t = _item[i];
                if (_button[i] != null)
                {
                    var bt = _button[i].transform;
                    bt.SetParent(_buttonParent[i], false);
                    bt.localPosition = _buttonPos[i];
                    bt.localRotation = _buttonRot[i];
                    bt.localScale = _buttonScale[i];
                }
                if (_tag[i] != null) _tag[i].gameObject.SetActive(false);
                HideStamp(i);
                if (_collider[i] != null) _collider[i].enabled = false;
                if (_target[i] != null) _target[i].enabled = false;
                t.localPosition = _legacyPos[i];
                t.localRotation = _legacyRot[i];
                t.localScale = _legacyScale[i];
                bool on = _legacyActive[i] && !_placeholder[i];
                if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
                var di = t.GetComponent<DeliveryItem>();
                if (di != null) { di.Measure(); di.delivered = false; di.parked = false; }
            }
        }

        // ------------------------------------------------------------ the arc
        void ComputeArc()
        {
            Transform head = Delivery.Head;
            Transform hive = hiveRoot != null ? hiveRoot : transform;
            Vector3 h = hive.position;
            Vector3 f = head != null ? h - head.position : hive.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) f = hive.forward;
            f.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, f);
            float tableY = Delivery.TableAnchor != null ? Delivery.TableAnchor.position.y : h.y;
            Vector3 c = h - f * frontEdge;
            c.y = tableY + arcHeight;
            if (head != null)
            {
                // v3.3: closer and higher: between the eye and the hive, every prop above the sight line over the roof
                Vector3 e = head.position;
                Vector3 toH = h - e;
                toH.y = 0f;
                float dFace = Mathf.Max(0.2f, toH.magnitude - hiveHalfDepth);
                float dArc = Mathf.Clamp(dFace - arcInFrontOfHive, Mathf.Min(minArcDistance, dFace), dFace);
                float roof = tableY + hiveTop;
                float sight = e.y - (e.y - roof) * dArc / dFace;
                float y = sight + clearAboveRoof; // props stand on their pivot (the builder's props sit on y = 0)
                float lo = tableY + minArcHeight;
                y = Mathf.Clamp(y, lo, Mathf.Max(lo, e.y - maxBelowEyeMin));
                c = new Vector3(e.x, y, e.z) + f * dArc + Vector3.Cross(Vector3.up, f) * arcSideShift;
            }
            int n = _n;
            float s = arcSpacing;
            if (head != null && n > 1)
            {
                Vector3 toC = c - head.position;
                toC.y = 0f;
                float d = Mathf.Max(0.2f, toC.magnitude - arcBend); // the bend brings the outer ones closer
                float maxHalf = Mathf.Tan(maxGazeDegrees * Mathf.Deg2Rad) * d;
                float half = (n - 1) * 0.5f * s;
                if (half > maxHalf) s = Mathf.Max(minSpacing, maxHalf / ((n - 1) * 0.5f));
            }
            Quaternion rot = Quaternion.LookRotation(f, Vector3.up);
            for (int i = 0; i < n && i < _slots.Length; i++)
            {
                float x = (i - (n - 1) * 0.5f) * s;
                float bend = s > 1e-4f ? arcBend * (x * x) / (s * s) : 0f;
                _slots[i] = new Pose(c + right * x - f * bend, rot);
            }
        }

        /// <summary>The arc slots (world) the last ParkChoices used.</summary>
        public Pose Slot(int i) => i >= 0 && i < _slots.Length ? _slots[i] : default;

        // ------------------------------------------------------------ editor: find the stamp art by its slot path
#if UNITY_EDITOR
        const string ApprovedStampPath = "Assets/VirtualBeeSwarm/Art/StampApproved/StampApproved.prefab";
        const string GrievanceStampPath = "Assets/VirtualBeeSwarm/Art/StampGrievance/StampGrievance.prefab";

        void Reset() => FindStampArt();
        void OnValidate() { if (!UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) FindStampArt(); }

        void FindStampArt()
        {
            if (approvedStampPrefab == null) approvedStampPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ApprovedStampPath);
            if (grievanceStampPrefab == null) grievanceStampPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(GrievanceStampPath);
        }
#endif

        /// <summary>Builder / test wiring of the stamp art (null = text stamp).</summary>
        public void SetStampArt(GameObject approved, GameObject grievance)
        {
            approvedStampPrefab = approved;
            grievanceStampPrefab = grievance;
        }

        // ------------------------------------------------------------ stamps (art + slam-down)
        /// <summary>The art stamp on item i: slams down onto its face toward the player. False = no prefab (use the text stamp).</summary>
        bool StampArt(int i, bool approved)
        {
            var prefab = approved ? approvedStampPrefab : grievanceStampPrefab;
            var t = _item[i];
            if (prefab == null || t == null)
            {
                Debug.Log("[VBS][Stations] stamp " + (approved ? "APPROVED" : "GRIEVANCE") + " on " + (t != null ? t.name : "?") +
                          ": text stamp (no " + (approved ? "StampApproved" : "StampGrievance") + " prefab)");
                return false;
            }
            Delivery.ClearStamp(t); // never both
            if (_stampMount[i] == null)
            {
                _stampMount[i] = new GameObject("[VBS] StampMount").transform; // [VBS]: DeliveryItem.Measure skips it
                _stampMount[i].SetParent(t, false);
            }
            Transform art = approved ? _stampArtA[i] : _stampArtG[i];
            if (art == null)
            {
                var go = Instantiate(prefab, _stampMount[i], false);
                go.name = "[VBS] " + prefab.name;
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) // never eats a poke
                    if (Application.isPlaying) Destroy(c); else DestroyImmediate(c);
                foreach (var r in go.GetComponentsInChildren<Renderer>(true)) { r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false; }
                art = go.transform;
                if (approved) _stampArtA[i] = art; else _stampArtG[i] = art;
            }
            var other = approved ? _stampArtG[i] : _stampArtA[i];
            if (other != null && other.gameObject.activeSelf) other.gameObject.SetActive(false);
            _stampMount[i].gameObject.SetActive(true);
            art.gameObject.SetActive(true);
            _stampAt[i] = Time.time;
            PoseStamp(i, art, 0f);
            Debug.Log("[VBS][Stations] stamp " + (approved ? "APPROVED" : "GRIEVANCE") + " on " + t.name + ": art prefab " + prefab.name + " (slam-down)");
            return true;
        }

        /// <summary>e = seconds since the poke: drops from 6 cm toward the player and 1.6x big, hits at 1.15x, squashes, settles at 1.</summary>
        void PoseStamp(int i, Transform art, float e)
        {
            var t = _item[i];
            var di = t.GetComponent<DeliveryItem>();
            var mount = _stampMount[i];
            // the mount undoes the item's scale (axis-aligned with it), so the art is world-sized
            Vector3 ls = t.lossyScale;
            mount.localPosition = Vector3.zero;
            mount.localRotation = Quaternion.identity;
            mount.localScale = new Vector3(1f / Mathf.Max(1e-4f, Mathf.Abs(ls.x)), 1f / Mathf.Max(1e-4f, Mathf.Abs(ls.y)), 1f / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));

            Bounds lb = di != null ? di.localBounds : new Bounds(new Vector3(0f, 0.05f, 0f), Vector3.one * 0.1f);
            Vector3 c = t.TransformPoint(lb.center);
            Transform h = Delivery != null ? Delivery.Head : null;
            // v3.3: faces the eye itself (not just its bearing: no more lying back), barely rolled, and stands
            // stampForward in front of the prop so the alarm bees around the hive don't cover it
            Vector3 toHead = h != null ? h.position - c : -t.forward;
            if (toHead.sqrMagnitude < 1e-6f) toHead = -t.forward;
            toHead.Normalize();
            float radius = di != null ? di.Radius : 0.05f;
            Vector3 face = c + toHead * (radius + stampForward);
            Quaternion rest = Quaternion.LookRotation(toHead, Vector3.up) * Quaternion.Euler(0f, 0f, i % 2 == 0 ? -3f : 3f); // art front = +Z

            float slam = Mathf.Max(0.01f, stampSlamSeconds), settle = Mathf.Max(0.01f, stampSettleSeconds);
            float scale, lift, pitch;
            if (e < slam)
            {
                float k = e / slam;
                k *= k; // accelerating into the hit
                scale = Mathf.Lerp(stampStartScale, 1.15f, k);
                lift = Mathf.Lerp(0.06f, 0f, k);
                pitch = Mathf.Lerp(-25f, 0f, k);
            }
            else if (e < slam + settle)
            {
                float k = (e - slam) / settle;
                scale = k < 0.4f ? Mathf.Lerp(1.15f, 0.92f, k / 0.4f) : Mathf.Lerp(0.92f, 1f, (k - 0.4f) / 0.6f);
                lift = 0f;
                pitch = Mathf.Sin(k * Mathf.PI) * 3f;
            }
            else { scale = 1f; lift = 0f; pitch = 0f; }

            art.position = face + toHead * lift + Vector3.up * (lift * 0.5f);
            art.rotation = rest * Quaternion.Euler(pitch, 0f, 0f);
            art.localScale = Vector3.one * (scale * stampScale); // the mount is ~unit scale in world, so this is the world size
        }

        void HideStamp(int i)
        {
            if (_stampAt == null || i < 0 || i >= _n) return;
            _stampAt[i] = -1f;
            if (_stampMount[i] != null && _stampMount[i].gameObject.activeSelf) _stampMount[i].gameObject.SetActive(false);
        }

        void HideStamps()
        {
            if (_stampAt == null) return;
            for (int i = 0; i < _n; i++) HideStamp(i);
        }

        // ------------------------------------------------------------ V19 hint
        void ClearHint(bool picked)
        {
            if (_hintOn)
                Debug.Log("[VBS][Stations] hint cleared (" + (picked ? "pick" : "beat exit/reset") + ") at " + GameIds.StationName(station));
            _hintArmedAt = -1f;
            _hintOn = false;
            _hintStartedAt = -1f;
            _hintEndAt = picked && _hintW > 0f ? Time.time : -1f; // a pick: ease back; exit/reset: at once
            if (!picked) { _hintW = 0f; _hintIndex = -1; }
        }

        void UpdateHint(float now)
        {
            if (_hintArmedAt >= 0f && !_hintOn && !_hintFiredThisOffer && now - _hintArmedAt >= hintAfterSeconds)
            {
                Resolve();
                int right = IndexOf(GameIds.CorrectChoice[(int)station]);
                bool ok = right >= 0 && !_gone[right] && _item[right] != null && _item[right].gameObject.activeInHierarchy &&
                          (!Active || ParkedAt(right));
                _hintFiredThisOffer = true;
                if (ok)
                {
                    _hintOn = true;
                    _hintIndex = right;
                    _hintStartedAt = now;
                    _hintEndAt = -1f;
                    Debug.Log("[VBS][Stations] hint: " + GameIds.StationName(station) + " " + _choice[right] + " (" + _item[right].name +
                              ") leans toward the hive after " + (now - _hintArmedAt).ToString("0.0") + " s without a pick; eager buzz x" + eagerBuzzPitch.ToString("0.00"));
                    RaiseBuzz(_item[right], eagerBuzzPitch, "hint");
                }
                else Debug.Log("[VBS][Stations] hint skipped at " + GameIds.StationName(station) + " (right choice not parked)");
            }
            if (_hintOn) _hintW = Mathf.Clamp01((now - _hintStartedAt) / Mathf.Max(0.01f, hintEaseSeconds));
            else if (_hintEndAt >= 0f)
            {
                _hintW = Mathf.Clamp01(_hintW - Time.deltaTime / 0.25f);
                if (_hintW <= 0f) { _hintEndAt = -1f; _hintIndex = -1; }
            }
        }

        // ------------------------------------------------------------ visual offsets (applied late, removed early)
        void Update() => RestoreAll();

        /// <summary>Take this frame's visual offsets off again, unless something else moved the item since.</summary>
        void RestoreAll()
        {
            if (_offApplied == null) return;
            for (int i = 0; i < _n; i++)
            {
                if (!_offApplied[i]) continue;
                _offApplied[i] = false;
                var t = _item[i];
                if (t == null) continue;
                if (t.position == _offAppliedPos[i] && t.rotation == _offAppliedRot[i])
                    t.SetPositionAndRotation(_offBasePos[i], _offBaseRot[i]);
            }
        }

        void LateUpdate()
        {
            if (!_resolved) return;
            float now = Time.time;
            UpdateHint(now);
            bool active = Active;
            var d = Delivery;
            for (int i = 0; i < _n; i++)
            {
                var t = _item[i];
                if (t == null || !t.gameObject.activeInHierarchy) { _heavyCued[i] = false; continue; }
                bool flying = d != null && d.IsFlying(t);
                Vector3 lastBase = _lastBasePos[i];
                _lastBasePos[i] = t.position;

                // stamp slam
                if (_stampAt[i] >= 0f)
                {
                    var art = _stampArtA[i] != null && _stampArtA[i].gameObject.activeSelf ? _stampArtA[i] : _stampArtG[i];
                    if (art != null)
                    {
                        float e = now - _stampAt[i];
                        PoseStamp(i, art, e);
                        if (e > stampSlamSeconds + stampSettleSeconds + 0.05f) _stampAt[i] = -2f; // settled: pose once more while it rides along
                    }
                }
                else if (_stampAt[i] == -2f)
                {
                    var art = _stampArtA[i] != null && _stampArtA[i].gameObject.activeSelf ? _stampArtA[i] : _stampArtG[i];
                    if (art != null) PoseStamp(i, art, 10f);
                }

                Vector3 dPos = Vector3.zero;
                Quaternion dRot = Quaternion.identity;
                Vector3 pivot = t.position;
                bool any = false;

                // V17: a heavy item shudders while its crew carries it
                var di = t.GetComponent<DeliveryItem>();
                bool heavyFlight = active && flying && di != null && di.Heavy;
                if (heavyFlight && !_heavyCued[i])
                {
                    Debug.Log("[VBS][Delivery] heavy: " + t.name + " crew strains (buzz x" + heavyBuzzPitch.ToString("0.00") + ")");
                    RaiseBuzz(t, heavyBuzzPitch, "heavy");
                }
                _heavyCued[i] = heavyFlight; // once per flight (parked/held in between re-arms it)
                if (heavyFlight && (t.position - lastBase).sqrMagnitude > 1e-8f) // carried, not waiting for its crew
                {
                    float a = strainDegrees * (0.6f * Mathf.Sin(now * strainHz * 6.2832f) + 0.4f * Mathf.Sin(now * strainHz * 1.7f * 6.2832f + 1.3f));
                    Vector3 axis = Vector3.Cross(Vector3.up, t.forward);
                    if (axis.sqrMagnitude < 1e-6f) axis = Vector3.right;
                    dRot = Quaternion.AngleAxis(a, axis.normalized) * Quaternion.AngleAxis(a * 0.5f, Vector3.forward);
                    dPos += Vector3.up * (0.004f * Mathf.Sin(now * strainHz * 2.3f * 6.2832f));
                    pivot = t.position + Vector3.up * (di.Top + 0.04f); // swings from the crew
                    any = true;
                }

                // V19: the right one leans toward the hive, eagerly
                if (i == _hintIndex && _hintW > 0f && !flying)
                {
                    Vector3 to = (hiveRoot != null ? hiveRoot.position : transform.position) - t.position;
                    to.y = 0f;
                    if (to.sqrMagnitude > 1e-6f)
                    {
                        to.Normalize();
                        float w = _hintW * _hintW * (3f - 2f * _hintW);
                        float eager = _hintOn ? 0.8f + 0.2f * Mathf.Sin(now * 9f) : 1f;
                        dPos += to * (hintLean * w * eager) + Vector3.up * (0.006f * w * Mathf.Abs(Mathf.Sin(now * 6f)));
                        dRot = Quaternion.AngleAxis(hintTiltDegrees * w * eager, Vector3.Cross(Vector3.up, to)) * dRot;
                        pivot = t.position;
                        any = true;
                    }
                }

                if (!any) continue;
                Vector3 basePos = t.position;
                Quaternion baseRot = t.rotation;
                Vector3 p = pivot + dRot * (basePos - pivot) + dPos;
                Quaternion r = dRot * baseRot;
                t.SetPositionAndRotation(p, r);
                _offApplied[i] = true;
                _offBasePos[i] = basePos;
                _offBaseRot[i] = baseRot;
                _offAppliedPos[i] = t.position;
                _offAppliedRot[i] = t.rotation;
            }
        }

        static void RaiseBuzz(Transform t, float pitch, string why)
        {
            var cue = BuzzCue;
            if (cue == null) return;
            try { cue(t, pitch, why); }
            catch (Exception e) { Debug.LogException(e); }
        }

        void OnDisable() => RestoreAll();
    }
}
