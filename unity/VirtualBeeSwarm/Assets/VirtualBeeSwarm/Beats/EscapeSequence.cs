using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Escape: all four hexagons glow, the hive door opens, the swarm pours out into the room, the window grows into
    /// full bloom, and the score card pops up: "Hive saved in m:ss" (the director's run clock, hook -> escape).
    /// "Motion carried. The union votes to stay."
    /// </summary>
    public class EscapeSequence : BeatBehaviour
    {
        [SerializeField] HexagonPanel hexagons;
        [SerializeField] HiveBody hive;
        [SerializeField] SwarmSystem swarm;
        [SerializeField] FutureWindow window;
        [SerializeField] BeeswaxController beeswax;
        [SerializeField] GameObject scoreCard;
        [SerializeField] TextMesh scoreText;
        [SerializeField] float holdSeconds = 15f;

        public string LastScore { get; private set; } = "";

        Vector3 _cardScale = Vector3.one;

        public override bool Handles(GameBeat beat) => beat == GameBeat.Escape;

        protected override void Awake()
        {
            base.Awake();
            if (hexagons == null) hexagons = FindAnyObjectByType<HexagonPanel>();
            if (hive == null) hive = FindAnyObjectByType<HiveBody>();
            if (swarm == null) swarm = FindAnyObjectByType<SwarmSystem>();
            if (window == null) window = FindAnyObjectByType<FutureWindow>();
            if (beeswax == null) beeswax = FindAnyObjectByType<BeeswaxController>();
            if (scoreCard != null) _cardScale = scoreCard.transform.localScale;
            SetActive(scoreCard, false);
        }

        protected override void OnEnter(GameBeat beat) => StartCoroutine(Run());

        IEnumerator Run()
        {
            float start = Time.time;
            if (hexagons != null) hexagons.Celebrate(true);
            if (beeswax != null) beeswax.GoPresent();
            yield return Wait(0.8f);
            if (hive != null) hive.SetDoor(HiveBody.DoorSize.Open);
            yield return Wait(0.5f);
            if (swarm != null) swarm.SetMode(SwarmMode.PourOut);
            if (window != null) { window.Open(); window.Grow(); }
            if (net != null) net.RequestScene(GameIds.Scene.FullBloom, sendToHive: true);

            float run = director != null ? director.RunSeconds : 0f;
            LastScore = "Hive saved in " + GameMaterials.Clock(run);
            if (scoreText != null) scoreText.text = LastScore;
            if (net != null) net.SendLog("score " + GameMaterials.Clock(run));
            if (scoreCard != null)
            {
                scoreCard.transform.localScale = Vector3.zero;
                SetActive(scoreCard, true);
                StartCoroutine(ScaleTo(scoreCard.transform, _cardScale, 0.5f));
            }
            yield return SayAndWait(GameLines.Escape, 2.5f, 8f);
            float left = holdSeconds - (Time.time - start);
            if (left > 0f) yield return Wait(left);
            Complete();
        }

        protected override void OnExit(GameBeat beat)
        {
            SetActive(scoreCard, false);
            if (scoreCard != null) scoreCard.transform.localScale = _cardScale;
        }

        protected override void OnResetState()
        {
            SetActive(scoreCard, false);
            if (scoreCard != null) scoreCard.transform.localScale = _cardScale;
        }
    }
}
