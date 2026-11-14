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
        [Tooltip("v3.1: the score card is flown in by a crown of 6. Empty or delivery.mode off = it scales in.")]
        [SerializeField] BeeDelivery delivery;
        [Header("Score card (v3.3)")]
        [Tooltip("The card (whatever art dresses it) is scaled to this width (m). 0 = as built.")]
        [SerializeField] float cardWidth = 0.44f;
        [Tooltip("Its spot in the card's parent (the TableAnchor): up over the hive's left, clear of Beeswax and his caption.")]
        [SerializeField] Vector3 cardLocalPosition = new Vector3(-0.08f, 0.75f, -0.04f);
        [Tooltip("Tilted back this much so it faces a standing player's eye.")]
        [SerializeField] float cardTiltDegrees = 14f;

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
            if (delivery == null) delivery = FindAnyObjectByType<BeeDelivery>();
            if (scoreCard != null) { FitCard(scoreCard.transform); _cardScale = scoreCard.transform.localScale; }
            SetActive(scoreCard, false);
        }

        /// <summary>v3.3: bigger (cardWidth) and up where a standing player reads it, before anyone records its home.</summary>
        void FitCard(Transform card)
        {
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            Matrix4x4 toCard = card.worldToLocalMatrix;
            foreach (var r in card.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r.GetComponent<TextMesh>() != null) continue;
                Bounds b = r.localBounds;
                Matrix4x4 m = toCard * r.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    float x = m.MultiplyPoint3x4(c).x;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                }
            }
            float before = maxX > minX ? (maxX - minX) * Mathf.Abs(card.lossyScale.x) : 0f;
            if (cardWidth > 0f && before > 0.01f) card.localScale *= cardWidth / before;
            if (card.parent != null) // the builder parents it to the TableAnchor
            {
                card.localPosition = cardLocalPosition;
                card.localRotation = Quaternion.Euler(cardTiltDegrees, 0f, 0f);
            }
            Debug.Log("[VBS] score card: " + before.ToString("0.000") + " m wide -> " + (maxX > minX ? (maxX - minX) * Mathf.Abs(card.lossyScale.x) : 0f).ToString("0.000") +
                      " m at " + card.localPosition.ToString("F2"));
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
            if (scoreCard != null && delivery != null && BeeDelivery.Mode != DeliveryMode.Off)
            {
                scoreCard.transform.localScale = _cardScale;
                delivery.DeliverHome(scoreCard.transform, DeliveryStyle.Crown, 6);
            }
            else if (scoreCard != null)
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
            if (delivery != null && scoreCard != null) delivery.Release(scoreCard.transform, hide: true);
            SetActive(scoreCard, false);
            if (scoreCard != null) scoreCard.transform.localScale = _cardScale;
        }

        protected override void OnResetState()
        {
            if (delivery != null && scoreCard != null) delivery.Release(scoreCard.transform, hide: true);
            SetActive(scoreCard, false);
            if (scoreCard != null) scoreCard.transform.localScale = _cardScale;
        }
    }
}
