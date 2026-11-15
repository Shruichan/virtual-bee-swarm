using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Station 1: the swarm is angry. Swat / spray water make it angrier; the smoker calms it (smoke masks the
    /// banana-smelling alarm pheromone and makes them gorge on honey). Keys 1-3.
    /// Physical option: give the smoker prop a ChoiceButton with choice "smoker" (poke/click it).
    /// </summary>
    public class SmokerStation : StationBase
    {
        [SerializeField] ParticleSystem smokePuff;
        [SerializeField] ParticleSystem waterPuff;
        [SerializeField] int puffParticles = 25;

        protected override GameLine Prompt => GameLines.SmokerPrompt;
        protected override GameLine Hint => RideLines.HintSmoker;

        /// <summary>v2: "Don't eat one near my house." 0.3 s after the smoker line (cut-if-behind #2).</summary>
        protected override IEnumerator AfterSolved(string choice)
        {
            var banana = RideLines.SmokerBanana;
            if (RidePacing.Cut(2, banana)) yield break;
            yield return Wait(0.3f);
            yield return SayLine(banana, 0.5f);
        }

        protected override void Awake()
        {
            station = GameStation.Smoker;
            base.Awake();
        }

        protected override void OnStationEnter()
        {
            if (swarm != null) swarm.SetMode(SwarmMode.Angry);
            if (net != null) net.RequestScene(GameIds.Scene.SmokerAngry, sendToHive: false);
        }

        ArtAnimProp FindPropAnimation(ParticleSystem effect)
        {
            if (props == null || effect == null) return null;
            foreach (var animation in props.GetComponentsInChildren<ArtAnimProp>(true))
                if (animation.particleName == effect.name) return animation;
            return null;
        }

        protected override IEnumerator ShowOutcome(string choice, bool correct)
        {
            var animation = FindPropAnimation(choice == GameIds.Choice.Smoker ? smokePuff : waterPuff);
            switch (choice)
            {
                case GameIds.Choice.Swat:
                    GameSound.Cue("swat");
                    if (beeswax != null) beeswax.WhipAround();
                    if (swarm != null) swarm.Spike(1f);
                    break;
                case GameIds.Choice.SprayWater:
                    // spray from the delivered bottle's nozzle (the builder parks the puff at a fixed spot)
                    var nozzle = SprayNozzle();
                    if (waterPuff != null && nozzle != null)
                    {
                        if (!_puffHomeSaved) { _puffHome = waterPuff.transform.localPosition; _puffHomeSaved = true; }
                        waterPuff.transform.position = nozzle.position;
                    }
                    animation?.PlayManualAction();
                    GameSound.Cue("water_spray", waterPuff != null ? waterPuff.transform : null);
                    if (waterPuff != null) waterPuff.Emit(puffParticles);
                    if (swarm != null) swarm.Spike(0.8f);
                    break;
                case GameIds.Choice.Smoker:
                    for (int k = 0; k < 3 && !SetupByHand; k++) // HANDSON hook: the player just pumped them
                    {
                        animation?.PlayManualAction();
                        if (smokePuff != null) smokePuff.Emit(puffParticles);
                        GameSound.Cue("smoker_puff", smokePuff != null ? smokePuff.transform : null);
                        yield return Wait(0.4f);
                    }
                    if (swarm != null)
                    {
                        swarm.Agitation = 0f;
                        swarm.SetMode(SwarmMode.Calm);
                    }
                    break;
            }
        }

        protected override void OnStationExit()
        {
            if (swarm != null && !Solved) swarm.SetMode(SwarmMode.Idle);
        }

        protected override void OnStationReset()
        {
            if (smokePuff != null) smokePuff.Clear();
            if (waterPuff != null) waterPuff.Clear();
            if (waterPuff != null && _puffHomeSaved) waterPuff.transform.localPosition = _puffHome;
        }

        Vector3 _puffHome;
        bool _puffHomeSaved;
        Transform _sprayPoint;

        /// <summary>The authored SprayBottle's SprayPoint (found once), or null if the art has none.</summary>
        Transform SprayNozzle()
        {
            if (_sprayPoint != null) return _sprayPoint;
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t.name == "SprayPoint") { _sprayPoint = t; break; }
            return _sprayPoint;
        }
    }
}
