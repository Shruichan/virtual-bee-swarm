using System.Collections;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Escape: all four hexagons glow, the hive door opens, the swarm pours out into the room, the window grows into
    /// full bloom, and the score card pops up: "Hive saved in m:ss" (the director's run clock, hook -> escape).
    /// "Motion carried. The union votes to stay."
    /// Ride script v2: room_escape on the roar, room_escape_minutes as the card parks (the hexagons pulse one per S),
    /// room_escape_credit as he lands on the lid (cut-if-behind #1); the beat ends when they're done. The score card also
    /// carries the END CARD (the contract: three things to do this week), shown when the map beat delivers it again and
    /// holds it in front of the player (DeliverEndCard).
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
        [Tooltip("v2: the beat lasts at least this long (it ends when the escape lines are done).")]
        [SerializeField] float scoreHoldSeconds = 6f;
        [Tooltip("v3.1: the score card is flown in by a crown of 6. Empty or delivery.mode off = it scales in.")]
        [SerializeField] BeeDelivery delivery;
        [Header("Score card (v3.3)")]
        [Tooltip("The card (whatever art dresses it) is scaled to this width (m). 0 = as built.")]
        [SerializeField] float cardWidth = 0.44f;
        [Tooltip("Its spot in the card's parent (the TableAnchor): up over the hive's left, clear of Beeswax and his caption.")]
        [SerializeField] Vector3 cardLocalPosition = new Vector3(-0.08f, 0.75f, -0.04f);
        [Tooltip("Tilted back this much so it faces a standing player's eye.")]
        [SerializeField] float cardTiltDegrees = 14f;
        [Header("End card (v2, map beat)")]
        [Tooltip("Held this far in front of the head, its middle this far below the eye.")]
        [SerializeField] float endCardAhead = 0.62f;
        [SerializeField] float endCardBelowEye = 0.3f;
        [SerializeField] float endCardTiltDegrees = 22f;
        [SerializeField] int endCardCrew = 6;

        public string LastScore { get; private set; } = "";

        Vector3 _cardScale = Vector3.one;
        GameObject _endPanel;
        float _endPanelHeight, _cardBottom; // world metres: the panel, and the card's bottom below its pivot
        public bool EndCardShown => _endPanel != null && _endPanel.activeInHierarchy;
        // the held card draws after the room's text (TextMesh ignores depth, so the map's labels showed through it)
        readonly System.Collections.Generic.List<(Renderer r, Material[] mats)> _onTop = new System.Collections.Generic.List<(Renderer, Material[])>();
        readonly System.Collections.Generic.List<Material> _onTopMats = new System.Collections.Generic.List<Material>();

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
            if (scoreCard != null) { FitCard(scoreCard.transform); _cardScale = scoreCard.transform.localScale; BuildEndPanel(scoreCard.transform); }
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

        /// <summary>
        /// v2: the contract under the score (Unity text; the printed card says the same). Built in metres under the card
        /// whatever art dresses it; hidden until the map beat delivers it (DeliverEndCard).
        /// </summary>
        void BuildEndPanel(Transform card)
        {
            float s = Mathf.Abs(card.lossyScale.x);
            if (s < 1e-4f) return;
            // the card's bottom edge in card space (renderers, not text)
            float minY = 0f;
            bool any = false;
            Matrix4x4 toCard = card.worldToLocalMatrix;
            foreach (var r in card.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r.GetComponent<TextMesh>() != null) continue;
                Bounds b = r.localBounds;
                Matrix4x4 m = toCard * r.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    float y = m.MultiplyPoint3x4(c).y;
                    if (!any || y < minY) { minY = y; any = true; }
                }
            }
            _cardBottom = any ? -minY * s : 0.055f;
            const float width = 0.46f, height = 0.33f, gap = 0.012f;
            _endPanelHeight = height;
            var panel = new GameObject("EndCard");
            panel.transform.SetParent(card, false);
            panel.transform.localScale = Vector3.one / s; // children in metres
            panel.transform.localPosition = new Vector3(0f, -(_cardBottom + gap + height * 0.5f) / s, 0f);
            var back = GameObject.CreatePrimitive(PrimitiveType.Cube);
            back.name = "Backing";
            var col = back.GetComponent<Collider>();
            if (col != null) Destroy(col);
            back.transform.SetParent(panel.transform, false);
            back.transform.localPosition = new Vector3(0f, 0f, 0.006f);
            back.transform.localScale = new Vector3(width, height, 0.01f);
            var backR = back.GetComponent<Renderer>();
            backR.sharedMaterial = GameMaterials.Create("VBS_EndCard", new Color(0.11f, 0.095f, 0.075f)); // the score card's dark board
            backR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var rim = GameObject.CreatePrimitive(PrimitiveType.Cube); // gold rim, like the score card's
            rim.name = "Rim";
            var rimCol = rim.GetComponent<Collider>();
            if (rimCol != null) Destroy(rimCol);
            rim.transform.SetParent(panel.transform, false);
            rim.transform.localPosition = new Vector3(0f, 0f, 0.009f);
            rim.transform.localScale = new Vector3(width + 0.016f, height + 0.016f, 0.008f);
            var rimR = rim.GetComponent<Renderer>();
            rimR.sharedMaterial = GameMaterials.Create("VBS_EndCardRim", new Color(0.95f, 0.68f, 0.12f));
            rimR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Font font = scoreText != null ? scoreText.font : null;
            var title = PanelText("Title", panel.transform, new Vector3(0f, height * 0.5f - 0.03f, 0f), "YOUR NEW CONTRACT\n3 THINGS THIS WEEK",
                                  0.0036f, new Color(1f, 0.85f, 0.25f), TextAnchor.UpperCenter, TextAlignment.Center, font);
            var body = PanelText("Body", panel.transform, new Vector3(-width * 0.5f + 0.025f, height * 0.5f - 0.095f, 0f),
                                 RideLines.EndCardBody.Substring(RideLines.EndCardBody.IndexOf('\n') + 1), 0.003f, new Color(0.96f, 0.93f, 0.86f),
                                 TextAnchor.UpperLeft, TextAlignment.Left, font);
            _endPanel = panel;
            panel.SetActive(false);
            Debug.Log("[VBS][EndCard] built under the score card: " + width + " x " + height + " m, title " + (title != null) + ", body " + (body != null));
        }

        static TextMesh PanelText(string name, Transform parent, Vector3 pos, string text, float charSize, Color color, TextAnchor anchor, TextAlignment align, Font font)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var tm = go.AddComponent<TextMesh>();
            if (font != null) tm.font = font;
            tm.fontSize = 64;
            tm.characterSize = charSize;
            tm.anchor = anchor;
            tm.alignment = align;
            tm.color = color;
            tm.text = text;
            var r = go.GetComponent<MeshRenderer>();
            if (font != null) r.sharedMaterial = font.material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return tm;
        }

        /// <summary>
        /// v2, map beat (room_end_contract): a crew delivers the score card again, now with the contract under the score,
        /// and holds it in front of the player (endCardAhead, a little below the eye, tilted to face them).
        /// </summary>
        public void DeliverEndCard()
        {
            if (scoreCard == null) return;
            if (scoreText != null && LastScore.Length > 0) scoreText.text = LastScore;
            SetActive(_endPanel, true);
            var t = scoreCard.transform;
            Transform head = delivery != null ? delivery.Head : (Camera.main != null ? Camera.main.transform : null);
            Vector3 eye = head != null ? head.position : t.position + Vector3.up * 0.4f;
            Vector3 fwd = head != null ? head.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(endCardTiltDegrees, 0f, 0f);
            // the card's pivot is the score line; the whole card (score + contract) is centred on the target
            float below = (_cardBottom + 0.012f + _endPanelHeight) * 0.5f;
            Vector3 centre = eye + fwd * endCardAhead - Vector3.up * endCardBelowEye;
            Vector3 pivot = centre + rot * Vector3.up * below;
            t.localScale = _cardScale;
            if (delivery != null && BeeDelivery.Mode != DeliveryMode.Off)
            {
                delivery.Park(t, new Pose(pivot, rot), endCardCrew);
            }
            else
            {
                t.SetPositionAndRotation(pivot, rot);
                t.localScale = Vector3.zero;
                SetActive(scoreCard, true);
                StartCoroutine(ScaleTo(t, _cardScale, 0.5f));
            }
            DrawOnTop(true);
            Debug.Log("[VBS][EndCard] delivered: " + LastScore + " + the contract, " + endCardAhead.ToString("0.00") + " m ahead, " +
                      Vector3.Distance(centre, eye).ToString("0.00") + " m from the eye");
        }

        /// <summary>
        /// The held card is drawn after the room's other text (queue 3500 for its boards, 3600 for its text, on material
        /// copies), so the map's labels behind it no longer show through; nearer opaque things still hide it (depth test).
        /// </summary>
        void DrawOnTop(bool on)
        {
            if (!on)
            {
                foreach (var (r, mats) in _onTop) if (r != null) r.sharedMaterials = mats;
                _onTop.Clear();
                foreach (var m in _onTopMats) if (m != null) Destroy(m);
                _onTopMats.Clear();
                return;
            }
            if (scoreCard == null || _onTop.Count > 0) return;
            foreach (var r in scoreCard.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                var mats = r.sharedMaterials;
                var copies = new Material[mats.Length];
                bool text = r.GetComponent<TextMesh>() != null;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    copies[i] = new Material(mats[i]) { renderQueue = text ? 3600 : 3500 };
                    _onTopMats.Add(copies[i]);
                }
                _onTop.Add((r, mats));
                r.sharedMaterials = copies;
            }
        }

        /// <summary>The map beat is over (or a reset): the end card goes.</summary>
        public void HideEndCard()
        {
            DrawOnTop(false);
            if (scoreCard == null || !EndCardShown) return;
            if (delivery != null) delivery.Release(scoreCard.transform, hide: true);
            SetActive(scoreCard, false);
            SetActive(_endPanel, false);
            scoreCard.transform.localScale = _cardScale;
        }

        protected override void OnEnter(GameBeat beat) => StartCoroutine(Run());

        IEnumerator Run()
        {
            float start = Time.time;
            if (hexagons != null) hexagons.Celebrate(true);
            if (beeswax != null) beeswax.GoPresent();
            yield return Wait(0.8f);
            if (hive != null) hive.SetDoor(HiveBody.DoorSize.Open);
            GameSound.Cue("door_open", hive != null ? hive.DoorPoint : null);
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
                // back above the hive (the last run's end card left it in front of the player's head)
                scoreCard.transform.localPosition = cardLocalPosition;
                scoreCard.transform.localRotation = Quaternion.Euler(cardTiltDegrees, 0f, 0f);
                scoreCard.transform.localScale = Vector3.zero;
                SetActive(scoreCard, true);
                StartCoroutine(ScaleTo(scoreCard.transform, _cardScale, 0.5f));
            }
            yield return SayAndWait(GameLines.Escape, 2.5f, LineTime.Seconds(net, GameLines.Escape) + 1f);

            // v2: "For the minutes: smoke, syrup, small door, shallow water." One hexagon pulses per S.
            var minutes = RideLines.EscapeMinutes;
            float len = LineTime.Seconds(net, minutes);
            StartCoroutine(PulseForMinutes(minutes, len));
            yield return SayAndWait(minutes, 1f, len + 1f);
            var credit = RideLines.EscapeCredit; // he lands on the lid (Blush); cut-if-behind #1
            if (!RidePacing.Cut(1, credit))
            {
                if (beeswax != null && hive != null) beeswax.FlyTo(hive.transform.position + Vector3.up * 0.3f);
                yield return SayAndWait(credit, 1f, LineTime.Seconds(net, credit) + 1f);
            }
            float left = scoreHoldSeconds - (Time.time - start);
            if (left > 0f) yield return Wait(left);
            Complete(0.4f);
        }

        IEnumerator PulseForMinutes(GameLine minutes, float len)
        {
            if (hexagons == null) yield break;
            string[] words = { "smoke", "syrup", "small door", "shallow water" };
            float start = Time.time;
            yield return null;
            // wait for the line itself to start (it may queue behind room_escape's tail)
            float wait = Time.time;
            while (net != null && !net.IsSpeaking && Time.time - wait < 2f) yield return null;
            start = Time.time;
            for (int i = 0; i < words.Length; i++)
            {
                float at = LineTime.WordAt(len, minutes.caption, words[i], 0.25f + 0.18f * i);
                while (Time.time - start < at) yield return null;
                hexagons.Pulse(i);
            }
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
            SetActive(_endPanel, false);
            DrawOnTop(false);
            if (scoreCard != null) scoreCard.transform.localScale = _cardScale;
        }
    }
}
