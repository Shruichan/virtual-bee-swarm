using TMPro;
using UnityEngine;

namespace VirtualBeeSwarm.Theme
{
    /// <summary>How one TextMesh is restyled.</summary>
    public sealed class UiTextStyle
    {
        public string what = "text";
        public bool display;            // Lilita One (titles, stamps) instead of Baloo 2
        public bool reflow;             // sentences: drop the 34-char line breaks and let TextMeshPro wrap in the same box
        public bool card;               // a themed card behind it
        public UiCard.Look look = UiCard.Look.Paper;
        public bool cardOnTop;          // the card is drawn always on top like the text (the caption: the open hive lid covers it)
        public bool recolor;            // ink colour instead of the TextMesh's own (on a paper card)
        public Color ink = UiTheme.Ink;
        public Vector2 pad = new Vector2(0.014f, 0.009f); // card padding around the text box (m)
        public float tail;              // speech tail length (m), 0 = none
        public float scale = 1f;        // TMP em relative to the TextMesh's em
        public int sticker;             // 1-5: a Daniel sticker bee on the card's top-left corner
        public float stickerSize = 0.05f;
        public string hideSibling;      // a backing of the owner's to hide (name of a child or sibling renderer)
        public float behind = 0.004f;   // the card sits this far behind the text (m); stacked cards need different depths
        public int queue;               // fixed render queue for the text (its card one below); 0 = follow the TextMesh's queue
    }

    /// <summary>
    /// Sits on another system's TextMesh and shows it in the themed font: the TextMesh stays the source of truth (its
    /// owner keeps writing .text, .color, toggling its renderer or GameObject, CaptionFit keeps reading its bounds), its
    /// renderer is only kept from drawing (forceRenderingOff), and a TextMeshPro child mirrors it inside the same box,
    /// with an optional card behind. Children are named "[VBS] ..." so DeliveryItem.Measure skips them.
    /// The text is read when the TextMesh's bounds change and, for same-size changes (a clock), four times a second.
    /// </summary>
    [DefaultExecutionOrder(15000)]
    public sealed class ThemedText : MonoBehaviour
    {
        const float PollSeconds = 0.25f;

        public UiTextStyle Style { get; private set; }
        public UiCard Card => _card;
        public TextMeshPro Text => _tmp;

        TextMesh _src;
        MeshRenderer _srcRenderer;
        TextMeshPro _tmp;
        UiCard _card;
        Transform _sticker;
        Renderer _hidden;
        Material _srcMat, _queueMat, _cardMat;
        bool _srcMatRead;
        string _text;
        Vector3 _boundsSize, _boundsCenter, _lossy;
        float _boxW, _boxH; // the TMP text box (source local units): the card must cover it, not just the TextMesh bounds
        Color _color = new Color(-1f, 0f, 0f, 0f);
        float _nextPoll;
        bool _shown = true, _logged;

        public static ThemedText Attach(TextMesh src, UiTextStyle style)
        {
            if (src == null || style == null || !UiTheme.Enabled) return null;
            var existing = src.GetComponent<ThemedText>();
            if (existing != null) return existing;
            var r = src.GetComponent<MeshRenderer>();
            if (r == null) return null;
            var tt = src.gameObject.AddComponent<ThemedText>();
            tt._src = src;
            tt._srcRenderer = r;
            tt.Style = style;
            tt.Build();
            return tt;
        }

        void Build()
        {
            _tmp = UiTheme.NewText(transform, "[VBS] UiText", "", 36f, _src.color, new Vector2(1f, 1f), Style.display, overlay: true,
                align: Align(_src.alignment), wrap: Style.reflow);
            if (_tmp == null) { Destroy(this); return; }
            _tmp.enableAutoSizing = true;
            _tmp.fontSizeMax = 36f;
            _tmp.fontSizeMin = 9f;
            if (Style.card)
            {
                _card = UiCard.Create(transform, "[VBS] UiCard", new Vector2(0.1f, 0.04f), Style.look, Style.tail);
                if (Style.cardOnTop && _card.Renderer != null)
                {
                    var cm = UiTheme.CardOverlayMaterial;
                    if (Style.queue > 0 && cm != null) cm = _cardMat = new Material(cm) { name = cm.name + " (queue)", renderQueue = Style.queue - 1 };
                    _card.Renderer.sharedMaterial = cm;
                }
                if (Style.sticker > 0) _sticker = StickerArt.Quad(_card.transform, Style.sticker, Style.stickerSize, "[VBS] UiCardSticker");
            }
            FindHidden();
            _srcRenderer.forceRenderingOff = true;
            _nextPoll = Time.unscaledTime + Random.value * PollSeconds;
            Show(false);
            LateUpdate();
        }

        /// <summary>The owner's backing to hide; looked up again on each poll until found (Juice makes its caption plate later).</summary>
        void FindHidden()
        {
            if (_hidden != null || string.IsNullOrEmpty(Style.hideSibling)) return;
            var t = transform.Find(Style.hideSibling);
            if (t == null && transform.parent != null) t = transform.parent.Find(Style.hideSibling);
            _hidden = t != null ? t.GetComponent<Renderer>() : null;
            if (_hidden != null) _hidden.forceRenderingOff = true;
        }

        /// <summary>
        /// The text draws in the source TextMesh's queue (3000 like GUI/Text, ZTest Always), and follows it when the owner
        /// swaps its material: EscapeSequence.DrawOnTop lifts the held end card's text to 3600 over its boards at 3500.
        /// </summary>
        void SyncQueue()
        {
            var sm = _srcRenderer.sharedMaterial;
            if (_srcMatRead && ReferenceEquals(sm, _srcMat)) return;
            _srcMatRead = true;
            _srcMat = sm;
            var baseMat = UiTheme.OverlayMaterial(Style.display);
            if (baseMat == null) return;
            int q = Style.queue > 0 ? Style.queue : sm != null ? sm.renderQueue : baseMat.renderQueue;
            if (q == baseMat.renderQueue) { _tmp.fontSharedMaterial = baseMat; return; }
            if (_queueMat == null) _queueMat = new Material(baseMat) { name = baseMat.name + " (queue)" };
            _queueMat.renderQueue = q;
            _tmp.fontSharedMaterial = _queueMat;
        }

        static TextAlignmentOptions Align(TextAlignment a) =>
            a == TextAlignment.Left ? TextAlignmentOptions.Left : a == TextAlignment.Right ? TextAlignmentOptions.Right : TextAlignmentOptions.Center;

        void OnDestroy()
        {
            if (_srcRenderer != null) _srcRenderer.forceRenderingOff = false;
            if (_hidden != null) _hidden.forceRenderingOff = false;
            if (_tmp != null) Destroy(_tmp.gameObject);
            if (_card != null) Destroy(_card.gameObject);
            if (_queueMat != null) Destroy(_queueMat);
            if (_cardMat != null) Destroy(_cardMat);
        }

        void Show(bool on)
        {
            if (_shown == on) return;
            _shown = on;
            if (_tmp != null) _tmp.gameObject.SetActive(on);
            if (_card != null) _card.gameObject.SetActive(on);
        }

        void LateUpdate()
        {
            if (_src == null || _srcRenderer == null || _tmp == null) return;
            Bounds b = _srcRenderer.localBounds;
            bool show = _srcRenderer.enabled && b.size.x > 1e-6f;
            if (!show)
            {
                if (_shown) { Show(false); _boundsSize = Vector3.zero; }
                return;
            }
            Vector3 lossy = transform.lossyScale;
            bool changed = b.size != _boundsSize || b.center != _boundsCenter || _text == null;
            string s = null;
            if (!changed && Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                FindHidden();
                s = _src.text;
                changed = !string.Equals(s, _text, System.StringComparison.Ordinal);
            }
            SyncQueue();
            if (changed) Refresh(s ?? _src.text, b, lossy);
            else if (_card != null && (lossy - _lossy).sqrMagnitude > 1e-10f) PlaceCard(b, lossy);
            Show(true);

            Color c = _src.color;
            if (c != _color)
            {
                _color = c;
                if (Style.recolor) { var ink = Style.ink; ink.a = c.a; _tmp.color = ink; }
                else _tmp.color = c;
            }
        }

        void Refresh(string text, Bounds b, Vector3 lossy)
        {
            _text = text ?? "";
            _boundsSize = b.size;
            _boundsCenter = b.center;
            int lines = 1;
            for (int i = 0; i < _text.Length; i++) if (_text[i] == '\n') lines++;

            // the TextMesh's em, in its own units: characterSize x fontSize / 10
            float fontSize = _src.fontSize > 0 ? _src.fontSize : (_src.font != null ? _src.font.fontSize : 13);
            float em = _src.characterSize * fontSize * 0.1f;
            if (em < 1e-7f) em = b.size.y / (lines * 1.15f);
            float k = em * Style.scale / 3.6f; // at this scale, TMP fontSize 36 = the TextMesh's em
            var t = _tmp.transform;
            t.localScale = new Vector3(k, k, k);
            t.localPosition = new Vector3(b.center.x, b.center.y, 0f);
            float boxW = b.size.x * 1.04f, boxH = Mathf.Max(b.size.y, lines * em * 1.2f) * 1.06f;
            _boxW = boxW;
            _boxH = boxH;
            _tmp.rectTransform.sizeDelta = new Vector2(boxW / k, boxH / k);
            _tmp.text = Style.reflow ? _text.Replace('\n', ' ') : _text;
            if (_card != null) PlaceCard(b, lossy);
            if (!_logged)
            {
                _logged = true;
                Debug.Log("[VBS][UiTheme] themed " + Style.what + " (" + name + "): em " + (em * Mathf.Abs(lossy.y) * 1000f).ToString("0.0") + " mm, box " +
                          (b.size.x * Mathf.Abs(lossy.x) * 100f).ToString("0.0") + " x " + (b.size.y * Mathf.Abs(lossy.y) * 100f).ToString("0.0") + " cm" +
                          (Style.card ? ", card " + Style.look : "") + (Style.display ? ", display font" : ""));
            }
        }

        void PlaceCard(Bounds b, Vector3 lossy)
        {
            _lossy = lossy;
            float sx = Mathf.Abs(lossy.x), sy = Mathf.Abs(lossy.y), sz = Mathf.Abs(lossy.z);
            if (sx < 1e-5f || sy < 1e-5f || sz < 1e-5f) return; // scaled to nothing (a pop-in): next frame
            var ct = _card.transform;
            ct.localScale = new Vector3(1f / sx, 1f / sy, 1f / sz);
            ct.localPosition = new Vector3(b.center.x, b.center.y, Style.behind / sz);
            // cover the box the reflowed text actually fills (1.04x wider, maybe more lines than the TextMesh): sized from
            // the TextMesh bounds alone, words at the line ends ran past the paper edge (seen in the 20-room playtests)
            float w = Mathf.Max(b.size.x, _boxW), h = Mathf.Max(b.size.y, _boxH);
            var size = new Vector2(w * sx + 2f * Style.pad.x, h * sy + 2f * Style.pad.y);
            _card.SetSize(size);
            if (_sticker != null)
                _sticker.localPosition = new Vector3(-size.x * 0.5f + Style.stickerSize * 0.12f, size.y * 0.5f - Style.stickerSize * 0.05f, -0.003f);
        }
    }
}
