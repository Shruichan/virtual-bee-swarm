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
        [Tooltip("v2: room_feed_lift_hint when there's no lift this long after room_feed_intro.")]
        [SerializeField] float liftHintSeconds = 8f;
        [Tooltip("v2: no lift this long after room_feed_intro: skip the lift (no room_lift_light), straight to room_feed_tip.")]
        [SerializeField] float liftGiveUpSeconds = 18f;

        protected override GameLine Prompt => GameLines.FeedPrompt;
        protected override GameLine Hint => RideLines.HintFeed;

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

        /// <summary>
        /// v2: room_feed_intro ("Problem two. Speaking of honey...") as Beeswax flies to the hive roof; no lift 8 s ->
        /// room_feed_lift_hint; the lift -> room_lift_light ("Light, right? That's bad.") -> room_feed_tip ("We're
        /// starving."); no lift 18 s -> skip it, room_feed_tip. Then the syrup zips in and the jar's crew strains as
        /// room_feed_prompt plays, and room_crew_heavy over the jar's struggle.
        /// </summary>
        protected override IEnumerator StationIntro()
        {
            if (beeswax != null && hive != null) beeswax.FlyTo(hive.transform.position + Vector3.up * 0.32f); // raps the roof
            if (hive != null) hive.LiftEnabled = true;
            yield return SayAndWait(Prompt, 0.8f, 5.5f);
            float start = Time.time;
            bool hinted = false;
            while (!_lifted && Time.time - start < liftGiveUpSeconds)
            {
                if (!hinted && Time.time - start > liftHintSeconds && (net == null || !net.IsSpeaking))
                {
                    hinted = true;
                    RidePacing.NoteStall("feed: no lift " + liftHintSeconds.ToString("0") + " s after the intro");
                    if (net != null) net.Say(GameLines.FeedLiftHint);
                }
                yield return null;
            }
            if (hive != null) hive.LiftEnabled = false; // set it down before choosing and pouring
            if (beeswax != null) { beeswax.WhipAround(); beeswax.GoPresent(); } // he tumbles off the roof
            if (_lifted) yield return SayLine(RideLines.LiftLight, 1f);
            else Debug.Log("[VBS][Stations] feed: no lift after " + liftGiveUpSeconds.ToString("0") + " s, skipping it");
            yield return SayAndWait(GameLines.FeedTooLight, 1f, 3f, unlessHive: false);
            if (delivery != null) delivery.ParkChoices(); // syrup zips in, the jar labours in
            yield return SayAndWait(GameLines.FeedChoose, 1f, 4f, unlessHive: false);
            if (delivery != null && delivery.HeavyInbound)
            {
                // V17: Beeswax yells at the jar's crew once, over its flight
                Debug.Log("[VBS][Stations] feed: " + GameLines.RoomCrewHeavy.id + " over the jar's flight");
                yield return SayAndWait(GameLines.RoomCrewHeavy, 1f, 3f);
            }
            OfferChoices();
        }

        /// <summary>v2: right first time, the hero fact still gets said (room_feed_why).</summary>
        protected override IEnumerator AfterSolved(string choice)
        {
            if (_triedHoney) yield break;
            yield return Wait(0.3f);
            yield return SayLine(RideLines.FeedWhy);
        }

        protected override bool IsChoiceAllowed(string choice) =>
            !requireMistakeFirst || _triedHoney || choice != GameIds.Choice.SugarSyrup;

        protected override IEnumerator ShowOutcome(string choice, bool correct)
        {
            if (hive != null) hive.LiftEnabled = false;
            bool honey = choice == GameIds.Choice.StoreHoney;
            if (honey) _triedHoney = true;
            Transform prop = honey ? honeyJar : syrupBottle;
            if (prop != null && pourPoint != null && !(correct && SetupByHand)) // HANDSON hook: the player just poured
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
