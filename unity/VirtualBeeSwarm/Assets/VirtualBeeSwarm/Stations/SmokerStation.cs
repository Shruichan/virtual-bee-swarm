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

        protected override IEnumerator ShowOutcome(string choice, bool correct)
        {
            switch (choice)
            {
                case GameIds.Choice.Swat:
                    GameSound.Cue("swat");
                    if (beeswax != null) beeswax.WhipAround();
                    if (swarm != null) swarm.Spike(1f);
                    break;
                case GameIds.Choice.SprayWater:
                    GameSound.Cue("water_spray", waterPuff != null ? waterPuff.transform : null);
                    if (waterPuff != null) waterPuff.Emit(puffParticles);
                    if (swarm != null) swarm.Spike(0.8f);
                    break;
                case GameIds.Choice.Smoker:
                    for (int k = 0; k < 3; k++)
                    {
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
        }
    }
}
