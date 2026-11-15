using TMPro;
using UnityEngine;

namespace VirtualBeeSwarm.Theme
{
    /// <summary>
    /// The progress tracker: a union card beside the hive. Four comb cells, one per station;
    /// a fix slaps that station's sticker bee on with a pop, a station Beeswax had to do himself gets the sleeping bee,
    /// every wrong pick adds a red tally stroke, and the corner shows the run clock. Built from UiCard + StickerArt +
    /// themed text only (about 12 draw calls, no lights, 4 alpha quads). StickerDirector feeds it.
    /// </summary>
    public sealed class UnionCard : MonoBehaviour
    {
        public const float Width = 0.29f, Height = 0.15f;
        const float SlotPitch = 0.066f, SlotY = 0.004f, StickerSize = 0.06f, PopSeconds = 0.32f;
        const int MaxTicks = 4;

        public static readonly string[] SlotLabels = { "SMOKER", "FEED", "WASPS", "HEAT" };
        public static readonly int[] SlotBees = { StickerArt.Smiling, StickerArt.Front, StickerArt.Flying, StickerArt.Winking };
        public const string SleptLabel = "BEESWAX DID IT";

        struct Slot
        {
            public Transform cell, sticker;
            public TextMeshPro label;
            public Transform[] ticks;
            public int shownTicks, state; // 0 empty, 1 fixed, 2 Beeswax did it
            public float poppedAt, tickAt, tilt;
        }

        readonly Slot[] _slots = new Slot[4];
        Transform _body;
        TextMeshPro _clock;
        string _clockText;
        float _shownAt = -1f, _bumpAt = -10f, _celebrateAt = -1f;
        bool _visible;
        PanelGrip _grip;

        public bool Visible => _visible;
        public bool PositionOverridden => _grip != null && (_grip.Held || _grip.ManuallyPlaced);
        public int State(int station) => station >= 0 && station < 4 ? _slots[station].state : 0;
        public int Ticks(int station) => station >= 0 && station < 4 ? _slots[station].shownTicks : 0;

        public static UnionCard Create(Transform parent)
        {
            var go = new GameObject("[VBS] UnionCard");
            go.transform.SetParent(parent, false);
            var card = go.AddComponent<UnionCard>();
            card.Build();
            go.SetActive(false);
            return card;
        }

        void Build()
        {
            _body = new GameObject("Body").transform;
            _body.SetParent(transform, false);
            UiCard.Create(_body, "Card", new Vector2(Width, Height), UiCard.Look.Paper);
            _grip = PanelGrip.Add(transform, _body, new Vector3(0f, Height * 0.5f + 0.008f, 0f),
                panelArea: new Vector2(Width, Height));
            // the easel leg: a dark slab behind, so it reads as a thing standing on the table
            var leg = StickerArt.Shape(_body, false, UiTheme.DarkBrown, new Vector2(0.05f, Height * 0.8f), "Leg");
            leg.localPosition = new Vector3(0f, -Height * 0.08f, 0.03f);
            leg.localRotation = Quaternion.Euler(-24f, 0f, 0f);

            const float z = -0.002f; // text and cells sit just proud of the paper
            float top = Height * 0.5f;
            var title = UiTheme.NewText(_body, "Title", "UNION CARD", 15f, UiTheme.Ink, new Vector2(0.15f, 0.022f), display: true,
                align: TextAlignmentOptions.Left, wrap: false);
            title.transform.localPosition = new Vector3(-Width * 0.5f + 0.017f + 0.075f, top - 0.0225f, z);
            var local = UiTheme.NewText(_body, "Local", "LOCAL 20,000", 8f, UiTheme.Brown, new Vector2(0.09f, 0.012f), align: TextAlignmentOptions.Left, wrap: false);
            local.transform.localPosition = new Vector3(-Width * 0.5f + 0.017f + 0.045f + 0.097f, top - 0.0245f, z);

            var pill = UiCard.Create(_body, "ClockPill", new Vector2(0.076f, 0.024f), UiCard.Look.Dark);
            pill.transform.localPosition = new Vector3(Width * 0.5f - 0.052f, top - 0.0225f, z);
            _clock = UiTheme.NewText(pill.transform, "Clock", "T-4:00", 13f, UiTheme.Honey, new Vector2(0.07f, 0.02f), display: true, wrap: false);
            _clock.transform.localPosition = new Vector3(0f, 0.0004f, -0.002f);
            _clockText = "T-4:00";

            for (int i = 0; i < 4; i++)
            {
                float x = (i - 1.5f) * SlotPitch;
                var s = new Slot { ticks = new Transform[MaxTicks], tilt = (i % 2 == 0 ? -1f : 1f) * (5f + 2f * i) };
                s.cell = StickerArt.Shape(_body, true, UiTheme.Faded, new Vector2(0.056f, 0.056f), "Cell" + i);
                s.cell.localPosition = new Vector3(x, SlotY, z);
                s.sticker = StickerArt.Quad(_body, SlotBees[i], StickerSize, "Sticker" + i);
                s.sticker.localPosition = new Vector3(x, SlotY, z * 2.5f);
                s.sticker.gameObject.SetActive(false);
                s.label = UiTheme.NewText(_body, "Label" + i, SlotLabels[i], 7.6f, UiTheme.Brown, new Vector2(SlotPitch + 0.004f, 0.012f), wrap: false);
                s.label.enableAutoSizing = true;
                s.label.fontSizeMax = 9f;
                s.label.fontSizeMin = 7.2f;
                s.label.transform.localPosition = new Vector3(x, SlotY - 0.039f, z);
                for (int k = 0; k < MaxTicks; k++)
                {
                    var t = StickerArt.Shape(_body, false, UiTheme.Grievance, new Vector2(0.0026f, 0.011f), "Tick" + i + "_" + k);
                    t.localPosition = new Vector3(x + 0.016f + k * 0.0048f, SlotY - 0.0215f, z * 3.5f);
                    t.localRotation = Quaternion.Euler(0f, 0f, k == 3 ? 62f : -8f); // the fourth strikes through, like a tally
                    if (k == 3) { t.localPosition = new Vector3(x + 0.016f + 0.0048f, SlotY - 0.0215f, z * 4f); t.localScale = new Vector3(0.0026f, 0.02f, 1f); }
                    t.gameObject.SetActive(false);
                    s.ticks[k] = t;
                }
                _slots[i] = s;
            }

            var foot = UiTheme.NewText(_body, "Foot", "DUES PAID IN HONEY", 8.5f, UiTheme.Brown, new Vector2(Width - 0.03f, 0.012f), wrap: false);
            foot.transform.localPosition = new Vector3(0f, -top + 0.0125f, z);
        }

        // ------------------------------------------------------------ state
        public void Show(bool on)
        {
            if (on == _visible) return;
            _visible = on;
            gameObject.SetActive(on);
            if (on) { _shownAt = Time.time; _body.localScale = Vector3.zero; }
        }

        public void ResetCard()
        {
            _grip?.ResetPlacement();
            for (int i = 0; i < 4; i++)
            {
                _slots[i].state = 0;
                _slots[i].shownTicks = 0;
                _slots[i].poppedAt = -1f;
                _slots[i].sticker.gameObject.SetActive(false);
                _slots[i].cell.gameObject.SetActive(true);
                _slots[i].label.text = SlotLabels[i];
                _slots[i].label.color = UiTheme.Brown;
                for (int k = 0; k < MaxTicks; k++) _slots[i].ticks[k].gameObject.SetActive(false);
            }
            _celebrateAt = -1f;
            SetClock("T-4:00");
        }

        /// <summary>The station is fixed: its sticker goes on (the sleeping bee if Beeswax had to do it).</summary>
        public void Stick(int station, bool beeswaxDidIt)
        {
            if (station < 0 || station >= 4 || _slots[station].state != 0) return;
            ref var s = ref _slots[station];
            s.state = beeswaxDidIt ? 2 : 1;
            StickerArt.SetBee(s.sticker, beeswaxDidIt ? StickerArt.Sleeping : SlotBees[station]);
            s.sticker.gameObject.SetActive(true);
            s.sticker.localScale = Vector3.zero;
            s.poppedAt = Time.time;
            if (beeswaxDidIt) { s.label.text = SleptLabel; s.label.color = UiTheme.Grievance; }
            _bumpAt = Time.time;
        }

        /// <summary>Wrong picks at that station so far (a red tally).</summary>
        public void SetTicks(int station, int count)
        {
            if (station < 0 || station >= 4) return;
            ref var s = ref _slots[station];
            count = Mathf.Clamp(count, 0, MaxTicks);
            if (count == s.shownTicks) return;
            for (int k = 0; k < MaxTicks; k++) s.ticks[k].gameObject.SetActive(k < count);
            if (count > s.shownTicks) { s.tickAt = Time.time; _bumpAt = Time.time; }
            s.shownTicks = count;
        }

        public void SetClock(string text)
        {
            if (_clock == null || text == _clockText) return;
            _clockText = text;
            _clock.text = text;
        }

        public void Celebrate() => _celebrateAt = Time.time;

        void Update()
        {
            float now = Time.time;
            // the card pops up when it appears and takes a small knock from every sticker / tally
            float appear = _shownAt < 0f ? 1f : UiTheme.Pop((now - _shownAt) / 0.45f);
            float bump = now - _bumpAt < 0.22f ? 1f + 0.035f * Mathf.Sin((now - _bumpAt) / 0.22f * Mathf.PI) : 1f;
            _body.localScale = Vector3.one * (appear * bump);

            for (int i = 0; i < 4; i++)
            {
                ref var s = ref _slots[i];
                if (s.state == 0 || s.poppedAt < 0f) continue;
                float t = (now - s.poppedAt) / PopSeconds;
                float k = UiTheme.Pop(t);
                float hop = 0f;
                if (_celebrateAt >= 0f)
                {
                    float c = now - _celebrateAt - i * 0.12f;
                    if (c > 0f && c < 2.4f) hop = Mathf.Abs(Mathf.Sin(c * Mathf.PI / 0.6f)) * 0.008f * (1f - c / 2.4f);
                }
                s.sticker.localScale = new Vector3(StickerSize * k, StickerSize * k, 1f);
                s.sticker.localRotation = Quaternion.Euler(0f, 0f, s.tilt + (1f - Mathf.Clamp01(t)) * 28f);
                var p = s.sticker.localPosition;
                p.y = SlotY + hop;
                s.sticker.localPosition = p;
                if (s.cell.gameObject.activeSelf && t > 0.5f) s.cell.gameObject.SetActive(false);
                if (s.shownTicks > 0 && now - s.tickAt < 0.25f)
                {
                    float q = 1f + 0.6f * Mathf.Sin((now - s.tickAt) / 0.25f * Mathf.PI);
                    var tick = s.ticks[s.shownTicks - 1];
                    bool strike = s.shownTicks - 1 == 3;
                    tick.localScale = new Vector3(0.0026f * q, (strike ? 0.02f : 0.011f) * q, 1f);
                }
            }
            // ticks can also pulse on a slot with no sticker yet
            for (int i = 0; i < 4; i++)
            {
                ref var s = ref _slots[i];
                if (s.state != 0 || s.shownTicks == 0) continue;
                float q = now - s.tickAt < 0.25f ? 1f + 0.6f * Mathf.Sin((now - s.tickAt) / 0.25f * Mathf.PI) : 1f;
                var tick = s.ticks[s.shownTicks - 1];
                bool strike = s.shownTicks - 1 == 3;
                tick.localScale = new Vector3(0.0026f * q, (strike ? 0.02f : 0.011f) * q, 1f);
            }
        }
    }
}
