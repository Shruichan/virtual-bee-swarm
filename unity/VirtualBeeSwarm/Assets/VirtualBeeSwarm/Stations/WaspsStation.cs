using System.Collections;
using UnityEngine;
using VirtualBeeSwarm.HandsOn;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Station 3: wasps buzz at the entrance. Bigger door = they raid the hive (wrong); smaller door = guard bees hold
    /// it and the wasps give up (right: fewer guards needed to hold a small door). Keys 1-2.
    /// Wasps are a few placeholder transforms animated here (modeler: swap the meshes under them).
    /// </summary>
    public class WaspsStation : StationBase
    {
        [SerializeField] Transform[] wasps = new Transform[0];
        [SerializeField] int guardsAtSmallDoor = 2;
        [SerializeField] int guardsAtNormalDoor = 4;

        HandsOnPrompt _arrivalWarning;
        public bool WarningVisible => _arrivalWarning != null && _arrivalWarning.Visible;
        public bool WaspsArrived => IsActive && _state != WaspState.Hidden;

        protected override GameLine Prompt => GameLines.WaspsPrompt;
        protected override GameLine Hint => RideLines.HintWasps;

        /// <summary>WASPBATOFF hook (WaspBatOff/): the bat-off burst, between the wasps arriving and the door choice. Null = none.</summary>
        public static System.Func<WaspsStation, IEnumerator> BatOff; // WASPBATOFF hook

        /// <summary>
        /// v2: room_wasps_arrive as the wasps let themselves in through the window, then room_wasps_intro as Beeswax ducks
        /// behind the hive and BIGGER DOOR / ENTRANCE REDUCER fly in.
        /// </summary>
        protected override IEnumerator StationIntro()
        {
            if (hive != null)
            {
                if (_arrivalWarning == null) _arrivalWarning = HandsOnPrompt.Build(transform, "Wasp warning", 0.0032f);
                _arrivalWarning.Show("! WASPS INCOMING", hive.SwarmCenter, Vector3.up * 0.33f, 1.2f, HandsOnKit.Bad);
                GameSound.Cue("countdown_beep", hive.transform);
                yield return Wait(1.2f);
                _arrivalWarning.Hide();
            }
            SetState(_window != null && hive != null ? WaspState.Arrive : WaspState.Buzz);
            yield return SayAndWait(RideLines.WaspsArrive, 0.8f, LineTime.Seconds(net, RideLines.WaspsArrive) + 1f);
            if (BatOff != null) yield return BatOff(this); // WASPBATOFF hook
            if (beeswax != null && hive != null)
            {
                beeswax.WhipAround();
                Vector3 away = hive.transform.position - hive.DoorPoint.position;
                away.y = 0f;
                beeswax.FlyTo(hive.transform.position + (away.sqrMagnitude > 1e-4f ? away.normalized * 0.22f : Vector3.zero) + Vector3.up * 0.15f);
            }
            if (delivery != null) delivery.ParkChoices();
            yield return SayAndWait(Prompt, 0.8f, 6f);
            OfferChoices();
        }

        /// <summary>v2 optional #3: he creeps back out from behind the hive (room_wasps_stinger).</summary>
        protected override IEnumerator AfterSolved(string choice)
        {
            if (beeswax != null) beeswax.GoPresent();
            var stinger = RideLines.WaspsStinger;
            if (!RidePacing.Optional(3, stinger)) yield break;
            yield return SayLine(stinger);
        }

        enum WaspState { Hidden, Arrive, Buzz, Raid, Leave }
        WaspState _state = WaspState.Hidden;
        float _stateSince;
        Vector3[] _leaveFrom;
        FutureWindow _window;

        protected override void Awake()
        {
            station = GameStation.Wasps;
            base.Awake();
            _leaveFrom = new Vector3[wasps.Length];
            _window = FindAnyObjectByType<FutureWindow>(FindObjectsInactive.Include);
            SetWaspsVisible(false);
        }

        protected override void OnStationEnter()
        {
            if (hive != null) hive.SetDoor(HiveBody.DoorSize.Normal);
            if (swarm != null)
            {
                swarm.SetGuardCount(guardsAtNormalDoor);
                swarm.SetMode(SwarmMode.Guard);
            }
            SetState(WaspState.Hidden);
        }

        protected override IEnumerator ShowOutcome(string choice, bool correct)
        {
            if (choice == GameIds.Choice.BiggerDoor)
            {
                if (hive != null) hive.SetDoor(HiveBody.DoorSize.Big);
                GameSound.Cue("door_widen", hive != null ? hive.DoorPoint : null);
                yield return Wait(0.6f);
                SetState(WaspState.Raid);
                if (swarm != null) swarm.Spike(0.7f);
            }
            else
            {
                if (hive != null) hive.SetDoor(HiveBody.DoorSize.Small);
                GameSound.Cue("door_narrow", hive != null ? hive.DoorPoint : null);
                if (swarm != null) swarm.SetGuardCount(Mathf.Min(guardsAtSmallDoor, Mathf.Max(0, guardsAtNormalDoor - 1)));
                yield return Wait(1.2f);
                SetState(WaspState.Leave);
            }
        }

        protected override void OnRetry(string wrongChoice)
        {
            if (hive != null) hive.SetDoor(HiveBody.DoorSize.Normal);
            SetState(WaspState.Buzz);
        }

        protected override void OnStationExit()
        {
            _arrivalWarning?.Hide();
            SetState(WaspState.Hidden);
            if (swarm != null) swarm.SetMode(SwarmMode.Calm);
        }

        protected override void OnStationReset()
        {
            _arrivalWarning?.Hide();
            SetState(WaspState.Hidden);
            if (hive != null) hive.SetDoor(HiveBody.DoorSize.Normal);
        }

        void SetState(WaspState s)
        {
            _state = s;
            _stateSince = Time.time;
            SetWaspsVisible(s != WaspState.Hidden && s != WaspState.Arrive);
            if (s == WaspState.Leave)
                for (int i = 0; i < wasps.Length; i++)
                    if (wasps[i] != null) _leaveFrom[i] = wasps[i].position;
        }

        void SetWaspsVisible(bool on)
        {
            for (int i = 0; i < wasps.Length; i++)
                if (wasps[i] != null && wasps[i].gameObject.activeSelf != on) wasps[i].gameObject.SetActive(on);
        }

        Vector3 WindowPoint(int index)
        {
            Transform pane = _window.PaneTransform;
            return pane.position + pane.right * ((index - (wasps.Length - 1) * 0.5f) * 0.055f);
        }

        static Vector3 Arc(Vector3 from, Vector3 to, float k) =>
            Vector3.Lerp(from, to, k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * 0.28f);

        protected override void Update()
        {
            base.Update();
            if (_state == WaspState.Hidden || hive == null) return;
            Transform door = hive.DoorPoint;
            Vector3 doorPos = door.position;
            Vector3 outward = -door.forward;
            float t = Time.time, age = t - _stateSince;
            for (int i = 0; i < wasps.Length; i++)
            {
                var w = wasps[i];
                if (w == null) continue;
                float ph = i * 1.7f;
                Vector3 hover = doorPos + outward * (0.09f + 0.03f * Mathf.Sin(t * 2.3f + ph))
                                + door.right * (0.07f * Mathf.Sin(t * 1.6f + ph * 2f))
                                + Vector3.up * (0.04f + 0.03f * Mathf.Sin(t * 3.1f + ph));
                Vector3 p;
                switch (_state)
                {
                    case WaspState.Arrive:
                        float flyAge = age - i * 0.12f;
                        if (flyAge < 0f) continue;
                        float flight = Mathf.Clamp01(flyAge / 1.4f);
                        p = Arc(WindowPoint(i), hover, Mathf.SmoothStep(0f, 1f, flight));
                        break;
                    case WaspState.Raid:
                        float k = Mathf.Clamp01((age - i * 0.25f) / 0.8f);
                        p = Vector3.Lerp(hover, doorPos - outward * 0.05f, k);
                        if (k >= 1f && w.gameObject.activeSelf) w.gameObject.SetActive(false);
                        break;
                    case WaspState.Leave:
                        float kl = Mathf.Clamp01((age - i * 0.2f) / 2f);
                        Vector3 away = _window != null ? WindowPoint(i)
                            : doorPos + outward * 0.4f + Vector3.up * 0.6f + door.right * ((i - wasps.Length * 0.5f) * 0.25f);
                        p = Arc(_leaveFrom[i], away, kl * kl);
                        if (kl >= 1f && w.gameObject.activeSelf) w.gameObject.SetActive(false);
                        break;
                    default:
                        p = hover + Random.insideUnitSphere * 0.004f;
                        break;
                }
                bool arriving = _state == WaspState.Arrive && !w.gameObject.activeSelf;
                Vector3 dir = arriving ? hover - p : p - w.position;
                w.position = p;
                if (dir.sqrMagnitude > 1e-7f) w.rotation = Quaternion.LookRotation(dir, Vector3.up);
                if (arriving) w.gameObject.SetActive(true);
            }
            if (_state == WaspState.Arrive && age >= 1.4f + (wasps.Length - 1) * 0.12f) SetState(WaspState.Buzz);
        }
    }
}
