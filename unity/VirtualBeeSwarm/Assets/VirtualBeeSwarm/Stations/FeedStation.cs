using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Station 2: "Is there enough food in there?" The player lifts/tips the hive (pinch-grab it, ray-grab it, or hold
    /// G in the editor); it comes up way too easily -> "We're starving." Then store honey (wrong: the colony gets
    /// sick, the honey mistake) or sugar syrup (right). Keys 1-2.
    /// requireMistakeFirst = syrup is locked until honey was tried (for filming the honey mistake).
    /// </summary>
    public class FeedStation : StationBase
    {
        [SerializeField] Transform honeyJar;
        [SerializeField] Transform syrupBottle;
        [Tooltip("Where a jar goes to pour (above the hive).")]
        [SerializeField] Transform pourPoint;
        [SerializeField] bool requireMistakeFirst;
        [SerializeField] float liftHintAfterSeconds = 10f;
        [SerializeField] float giveUpLiftAfterSeconds = 20f;

        protected override GameLine Prompt => GameLines.FeedPrompt;

        bool _lifted, _triedHoney;
        Vector3 _honeyHome, _syrupHome;
        Quaternion _honeyRot, _syrupRot;

        protected override void Awake()
        {
            station = GameStation.Feed;
            base.Awake();
            if (honeyJar != null) { _honeyHome = honeyJar.localPosition; _honeyRot = honeyJar.localRotation; }
            if (syrupBottle != null) { _syrupHome = syrupBottle.localPosition; _syrupRot = syrupBottle.localRotation; }
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (hive != null) hive.Lifted += HandleLifted;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (hive != null) hive.Lifted -= HandleLifted;
        }

        void HandleLifted(float height)
        {
            if (IsActive) _lifted = true;
        }

        protected override void OnStationEnter()
        {
            _lifted = false;
            if (swarm != null) swarm.SetMode(SwarmMode.Calm);
        }

        protected override IEnumerator StationIntro()
        {
            yield return SayAndWait(Prompt, 0.8f, 5f);
            if (hive != null) hive.LiftEnabled = true;
            float start = Time.time;
            bool hinted = false;
            while (!_lifted && Time.time - start < giveUpLiftAfterSeconds)
            {
                if (!hinted && Time.time - start > liftHintAfterSeconds)
                {
                    hinted = true;
                    if (net != null) net.Say(GameLines.FeedLiftHint);
                }
                yield return null;
            }
            if (beeswax != null) beeswax.WhipAround();
            yield return SayAndWait(GameLines.FeedTooLight, 1f, 3f, unlessHive: false);
            if (delivery != null) delivery.ParkChoices(); // syrup zips in, the jar labours in
            if (delivery != null && delivery.HeavyInbound)
            {
                // V17: Beeswax yells at the jar's crew once, over its flight
                Debug.Log("[VBS][Stations] feed: " + GameLines.RoomCrewHeavy.id + " over the jar's flight");
                yield return SayAndWait(GameLines.RoomCrewHeavy, 1f, 3f);
            }
            yield return SayAndWait(GameLines.FeedChoose, 1f, 4f, unlessHive: false);
            OfferChoices();
        }

        protected override bool IsChoiceAllowed(string choice) =>
            !requireMistakeFirst || _triedHoney || choice != GameIds.Choice.SugarSyrup;

        protected override IEnumerator ShowOutcome(string choice, bool correct)
        {
            if (hive != null) hive.LiftEnabled = false;
            bool honey = choice == GameIds.Choice.StoreHoney;
            if (honey) _triedHoney = true;
            Transform prop = honey ? honeyJar : syrupBottle;
            if (prop != null && pourPoint != null)
            {
                Vector3 home = prop.position;
                Quaternion rot = prop.rotation;
                yield return MoveTo(prop, pourPoint.position, 0.7f);
                GameSound.Cue("jar_tip", prop);
                float t0 = Time.time;
                while (Time.time - t0 < 0.5f)
                {
                    prop.rotation = rot * Quaternion.Euler(0f, 0f, Mathf.Lerp(0f, 110f, (Time.time - t0) / 0.5f));
                    yield return null;
                }
                yield return Wait(0.8f);
                if (hive != null) hive.SetSick(honey);
                if (swarm != null) swarm.SetMode(SwarmMode.Calm);
                prop.rotation = rot;
                yield return MoveTo(prop, home, 0.6f);
            }
            else if (hive != null) hive.SetSick(honey);
        }

        protected override void OnStationExit()
        {
            if (hive != null)
            {
                hive.LiftEnabled = false;
                if (Solved) hive.SetSick(false);
            }
            RestoreProps();
        }

        protected override void OnStationReset()
        {
            _lifted = false;
            _triedHoney = false;
            if (hive != null)
            {
                hive.LiftEnabled = false;
                hive.SetSick(false);
            }
            RestoreProps();
        }

        void RestoreProps()
        {
            if (honeyJar != null) { honeyJar.localPosition = _honeyHome; honeyJar.localRotation = _honeyRot; }
            if (syrupBottle != null) { syrupBottle.localPosition = _syrupHome; syrupBottle.localRotation = _syrupRot; }
        }
    }
}
