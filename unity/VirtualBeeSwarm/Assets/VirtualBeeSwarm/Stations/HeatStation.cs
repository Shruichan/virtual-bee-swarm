using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Station 4: heat wave, the hive glows red. Deep bowl (wrong: they can't land, they drown), nothing (wrong:
    /// hotter), shallow dish with pebbles (right: they land, drink, and fan the hive cool). Keys 1-3.
    /// `drinkers` are a few placeholder bees that fly from the door to the water.
    /// </summary>
    public class HeatStation : StationBase
    {
        [SerializeField] GameObject deepBowl;
        [SerializeField] GameObject pebbleDish;
        [Tooltip("Landing spots on the pebbles (one per drinker).")]
        [SerializeField] Transform[] pebbleSpots = new Transform[0];
        [Tooltip("Water surface of the deep bowl (drinkers sink below it).")]
        [SerializeField] Transform bowlWater;
        [SerializeField] Transform[] drinkers = new Transform[0];
        GameObject _dishWater;
        GameObject _handoffTray;

        protected override GameLine Prompt => GameLines.HeatPrompt;
        protected override GameLine Hint => RideLines.HintHeat;

        /// <summary>v2 optional #1 after the pebble line (the score clock has stopped): room_heat_mom.</summary>
        protected override IEnumerator AfterSolved(string choice)
        {
            var mom = RideLines.HeatMom;
            if (!RidePacing.Optional(1, mom)) yield break;
            yield return Wait(0.3f);
            yield return SayLine(mom);
        }
        protected override bool KeepPropsAfter => true; // the pebble dish stays on the table

        protected override void Awake()
        {
            station = GameStation.Heat;
            base.Awake();
            if (pebbleDish != null)
            {
                foreach (var renderer in pebbleDish.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer.name != "Water" || !renderer.enabled) continue;
                    _dishWater = renderer.gameObject;
                    break;
                }
            }
            HideAll();
        }

        /// <summary>Reveal the water surface from the authored dish or its placeholder.</summary>
        public bool ShowDishWater()
        {
            if (_dishWater == null) return false;
            _dishWater.SetActive(true);
            return true;
        }

        protected override void OnStationEnter()
        {
            HideAll();
            StageDishTray();
            if (hive != null) hive.SetHeatGlow(1f);
            if (swarm != null)
            {
                swarm.SetMode(SwarmMode.Idle);
                swarm.Agitation = 0.4f;
            }
            if (net != null) net.RequestScene(GameIds.Scene.HeatStress, sendToHive: false);
        }

        void StageDishTray()
        {
            if (delivery == null || !delivery.Active || !Bloom.MeadowLayout.Active) return;
            Transform anchor = Bloom.MeadowLayout.Anchor;
            Transform spot = delivery.Spot;
            if (anchor == null || spot == null) return;
            float scale = Bloom.MeadowLayout.Scale;
            float height = Mathf.Max(0.58f, Bloom.MeadowTuning.ChoiceHeight - 0.22f);
            float row = Bloom.MeadowLayout.RowOffset(scale);
            spot.SetPositionAndRotation(anchor.TransformPoint(new Vector3(0.48f / scale, height / scale, -row - 0.18f / scale)),
                anchor.rotation);
            if (_handoffTray == null)
            {
                var tray = new GameObject("Dish Handoff Tray");
                tray.transform.SetParent(spot, false);
                tray.transform.localScale = Vector3.one / scale;
                HandsOn.HandsOnKit.Prim(PrimitiveType.Cylinder, "Tray", tray.transform,
                    Vector3.down * 0.023f, new Vector3(0.34f, 0.023f, 0.34f), HandsOn.HandsOnKit.Brass);
                HandsOn.HandsOnKit.Prim(PrimitiveType.Cylinder, "Stand", tray.transform,
                    Vector3.down * (height * 0.5f), new Vector3(0.055f, height * 0.5f, 0.055f), HandsOn.HandsOnKit.Wood);
                _handoffTray = tray;
            }
            _handoffTray.SetActive(true);
        }

        protected override IEnumerator ShowOutcome(string choice, bool correct)
        {
            switch (choice)
            {
                case GameIds.Choice.DeepBowl:
                    SetActive(deepBowl, true);
                    GameSound.Cue("bowl_splash", bowlWater != null ? bowlWater : deepBowl != null ? deepBowl.transform : null, 1.2f); // first drinker lands
                    yield return FlyDrinkers(false);
                    break;
                case GameIds.Choice.Nothing:
                    if (swarm != null) swarm.Spike(0.6f);
                    if (beeswax != null) beeswax.WhipAround();
                    break;
                case GameIds.Choice.PebbleDish:
                    SetActive(pebbleDish, true);
                    ShowDishWater();
                    GameSound.Cue("pebble_sip", pebbleDish != null ? pebbleDish.transform : null, 1.2f); // first drinker lands
                    yield return FlyDrinkers(true);
                    if (hive != null) hive.SetHeatGlow(0f);
                    if (swarm != null)
                    {
                        swarm.Agitation = 0f;
                        swarm.SetMode(SwarmMode.Calm);
                    }
                    break;
            }
        }

        IEnumerator FlyDrinkers(bool pebbles)
        {
            if (hive == null || drinkers.Length == 0) yield break;
            Vector3 from = hive.DoorPoint.position;
            for (int i = 0; i < drinkers.Length; i++)
                if (drinkers[i] != null) { drinkers[i].position = from; drinkers[i].gameObject.SetActive(true); }

            const float fly = 1.2f, sink = 1.5f, stagger = 0.15f;
            float start = Time.time;
            float end = fly + (pebbles ? 0f : sink) + (drinkers.Length - 1) * stagger;
            while (true)
            {
                float age = Mathf.Min(Time.time - start, end);
                for (int i = 0; i < drinkers.Length; i++)
                {
                    var d = drinkers[i];
                    if (d == null) continue;
                    Vector3 land = pebbles
                        ? (i < pebbleSpots.Length && pebbleSpots[i] != null ? pebbleSpots[i].position : from)
                        : (bowlWater != null ? bowlWater.position + new Vector3((i - 1) * 0.025f, 0f, (i % 2) * 0.02f) : from);
                    float k = Mathf.Clamp01((age - i * stagger) / fly);
                    Vector3 p = Vector3.Lerp(from, land, k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * 0.08f);
                    if (!pebbles && k >= 1f) p = land + Vector3.down * Mathf.Clamp01((age - fly - i * stagger) / sink) * 0.04f;
                    d.position = p;
                }
                if (age >= end) yield break;
                yield return null;
            }
        }

        protected override void OnRetry(string wrongChoice)
        {
            if (delivery == null || !delivery.Active) SetActive(deepBowl, false); // delivered: the repo crew takes it
            HideDrinkers();
        }

        protected override void OnStationExit()
        {
            if (hive != null && Solved) hive.SetHeatGlow(0f);
            SetActive(deepBowl, false);
            if (!Solved) { SetActive(pebbleDish, false); SetActive(_handoffTray, false); HideDrinkers(); }
        }

        protected override void OnStationReset()
        {
            if (hive != null) hive.SetHeatGlow(0f);
            HideAll();
        }

        void HideAll()
        {
            SetActive(deepBowl, false);
            SetActive(pebbleDish, false);
            SetActive(_dishWater, false);
            SetActive(_handoffTray, false);
            HideDrinkers();
        }

        void HideDrinkers()
        {
            for (int i = 0; i < drinkers.Length; i++)
                if (drinkers[i] != null) drinkers[i].gameObject.SetActive(false);
        }
    }
}
