using System.Collections;
using UnityEngine;

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
        [SerializeField] int guardsAtSmallDoor = 10;
        [SerializeField] int guardsAtNormalDoor = 4;

        protected override GameLine Prompt => GameLines.WaspsPrompt;

        enum WaspState { Hidden, Buzz, Raid, Leave }
        WaspState _state = WaspState.Hidden;
        float _stateSince;
        Vector3[] _leaveFrom;

        protected override void Awake()
        {
            station = GameStation.Wasps;
            base.Awake();
            _leaveFrom = new Vector3[wasps.Length];
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
            SetState(WaspState.Buzz);
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
                if (swarm != null) swarm.SetGuardCount(guardsAtSmallDoor);
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
            SetState(WaspState.Hidden);
            if (swarm != null) swarm.SetMode(SwarmMode.Calm);
        }

        protected override void OnStationReset()
        {
            SetState(WaspState.Hidden);
            if (hive != null) hive.SetDoor(HiveBody.DoorSize.Normal);
        }

        void SetState(WaspState s)
        {
            _state = s;
            _stateSince = Time.time;
            SetWaspsVisible(s != WaspState.Hidden);
            if (s == WaspState.Leave)
                for (int i = 0; i < wasps.Length; i++)
                    if (wasps[i] != null) _leaveFrom[i] = wasps[i].position;
        }

        void SetWaspsVisible(bool on)
        {
            for (int i = 0; i < wasps.Length; i++)
                if (wasps[i] != null && wasps[i].gameObject.activeSelf != on) wasps[i].gameObject.SetActive(on);
        }

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
                    case WaspState.Raid:
                        float k = Mathf.Clamp01((age - i * 0.25f) / 0.8f);
                        p = Vector3.Lerp(hover, doorPos - outward * 0.05f, k);
                        if (k >= 1f && w.gameObject.activeSelf) w.gameObject.SetActive(false);
                        break;
                    case WaspState.Leave:
                        float kl = Mathf.Clamp01((age - i * 0.2f) / 2f);
                        Vector3 away = doorPos + outward * 0.4f + Vector3.up * 0.6f + door.right * ((i - wasps.Length * 0.5f) * 0.25f);
                        p = Vector3.Lerp(_leaveFrom[i], away, kl * kl);
                        if (kl >= 1f && w.gameObject.activeSelf) w.gameObject.SetActive(false);
                        break;
                    default:
                        p = hover + Random.insideUnitSphere * 0.004f;
                        break;
                }
                Vector3 dir = p - w.position;
                w.position = p;
                if (dir.sqrMagnitude > 1e-7f) w.rotation = Quaternion.LookRotation(dir, Vector3.up);
            }
        }
    }
}
