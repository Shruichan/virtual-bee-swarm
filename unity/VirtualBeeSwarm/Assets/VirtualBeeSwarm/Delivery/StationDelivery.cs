using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// A station's choices, delivered (PLAN v3.1 V15). Sits next to a StationBase (same GameObject); StationBase calls
    /// it at six points (INTEGRATION.md). With delivery.mode off it does nothing and puts every prop, scale and button
    /// back exactly as the scene builder made them, so today's buttons work unchanged.
    ///
    /// On: the choices fly in together when the prompt starts and park, perfectly still, in an arc above the table's
    /// front edge (centred on the hive, 20+ cm apart, within +-25 deg of gaze, 25 cm up). The props are the buttons:
    /// each ChoiceButton becomes a price tag under its prop, and the prop gets a trigger collider (16 cm max) that
    /// forwards pokes and clicks to it. Props are normalized to a 12 cm box. On the poke: APPROVED / GRIEVANCE stamp.
    /// Wrong: the station's own consequence plays, then a red repo crew hauls it out. Right: its crew sets it down on
    /// its spot (TableAnchor/Spots/Smoker|Feeder|Door|Dish) and the leftovers fly back out.
    /// Missing props (Swatter, SprayBottle, Crowbar, Reducer, NothingTag) and spots are built at runtime as placeholders.
    /// </summary>
    public class StationDelivery : MonoBehaviour
    {
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
        [SerializeField] float arcHeight = 0.25f;
        [SerializeField] float arcSpacing = 0.22f;
        [SerializeField] float minSpacing = 0.2f;
        [Tooltip("Front edge of the table, from the hive centre toward the player (HivePlacement puts the hive 22 cm in).")]
        [SerializeField] float frontEdge = 0.22f;
        [SerializeField] float maxGazeDegrees = 25f;
        [SerializeField] float arcBend = 0.04f;

        [Header("Props")]
        [SerializeField] float normalizeTo = 0.12f;
        [SerializeField] float maxCollider = 0.16f;
        [Tooltip("World scale of the price tag (the ChoiceButton) under each prop.")]
        [SerializeField] float tagScale = 0.7f;

        [Header("Order")]
        [Tooltip("Right pick: set down on the spot right away (the outcome plays on the spot). Off: after the outcome visuals (feed: the pour).")]
        [SerializeField] bool rightLandsFirst = true;
        [SerializeField] bool rightLandsFirstFromStation = true;

        public GameStation Station { get { Resolve(); return station; } }
        public bool Active => BeeDelivery.Mode != DeliveryMode.Off && Delivery != null;
        public bool Parked => _parked;
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
            if (!_attached) Attach();
            _parked = true;
            ComputeArc();
            int slot = 0;
            for (int i = 0; i < _n; i++)
            {
                if (_gone[i]) { slot++; continue; }
                Delivery.Park(_item[i], _slots[slot++]);
            }
            Debug.Log("[VBS][Delivery] " + GameIds.StationName(station) + ": " + _n + " choices on their way to the arc");
        }

        /// <summary>OfferChoices: make sure the choices are out (a subclass intro that never called ParkChoices).</summary>
        public void EnsureParked()
        {
            if (!Active) return;
            if (!_parked) ParkChoices();
            int flying = 0;
            for (int i = 0; i < _n; i++) if (!_gone[i] && Delivery.IsFlying(_item[i])) flying++;
            Debug.Log("[VBS][Delivery] " + GameIds.StationName(station) + " choices accept pokes" + (flying > 0 ? " (" + flying + " still flying)" : " (all parked)"));
        }

        /// <summary>Same frame as the poke / choice.request / timeout pick: stamp it; a right pick lands first (except feed).</summary>
        public void OnChoiceMade(string choice, bool correct)
        {
            if (!Active) return;
            int i = IndexOf(choice);
            if (i < 0) return;
            if (!_parked) ParkChoices(); // picked before the arc (key, operator): the others still come and go
            Delivery.Stamp(_item[i], correct);
            if (correct && rightLandsFirst) LandRight(i);
        }

        /// <summary>The station's ShowOutcome coroutine finished: wrong = the repo crew hauls it out; right (feed) = set down now.</summary>
        public void OnOutcomeShown(string choice, bool correct)
        {
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
                else if (Delivery != null) Delivery.Release(_item[i], hide: true);
            }
            _parked = false;
        }

        /// <summary>StationBase.OnResetState (before OnStationReset): everything back to the builder's layout.</summary>
        public void ResetItems()
        {
            Resolve();
            if (Delivery != null)
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
                    _tag[i].localPosition = new Vector3(lb.center.x, lb.min.y + 0.025f / Mathf.Max(1e-4f, Mathf.Abs(ls.y)), lb.min.z - 0.012f / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));
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
                _target[i].Bind(_button[i]);
                _target[i].enabled = true;
                if (Delivery != null) Delivery.Remeasure(t);
            }
        }

        void Detach()
        {
            if (!_attached)
            {
                for (int i = 0; i < _n; i++) if (_placeholder[i] && _item[i].gameObject.activeSelf) _item[i].gameObject.SetActive(false);
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
            int n = _n;
            float s = arcSpacing;
            if (head != null && n > 1)
            {
                Vector3 toC = c - head.position;
                toC.y = 0f;
                float d = Mathf.Max(0.2f, toC.magnitude);
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
    }
}
