using System;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Theme
{
    /// <summary>
    /// The one look for every text surface: the logo's palette, two TextMeshPro SDF fonts (Resources/UiTheme: "VBS Body
    /// SDF" = Baloo 2 Bold for sentences, "VBS Display SDF" = Lilita One for titles and stamps; rebuild or swap them with
    /// UiThemeSetup in the editor), and the factories for themed text and card materials. Nothing here touches other
    /// systems: UiThemeDirector restyles their TextMeshes at runtime. VBS_UITHEME=0 turns the lane off.
    /// </summary>
    public static class UiTheme
    {
        // the logo's palette (art-preview/logo)
        public static readonly Color Ink = Rgb(42, 23, 10), Brown = Rgb(96, 54, 22), DarkBrown = Rgb(62, 34, 14);
        public static readonly Color Honey = Rgb(247, 187, 52), Amber = Rgb(226, 140, 28), Cream = Rgb(255, 243, 214);
        public static readonly Color Leaf = Rgb(125, 168, 74), Dark = Rgb(20, 17, 12);
        public static readonly Color Approved = Rgb(40, 132, 62), Grievance = Rgb(196, 52, 38), Faded = Rgb(214, 190, 142);

        public const string BodyFontPath = "UiTheme/VBS Body SDF", DisplayFontPath = "UiTheme/VBS Display SDF";
        /// <summary>TextMeshPro objects made here are scaled so that fontSize = the em height in millimetres.</summary>
        public const float TextScale = 0.01f;

        static bool _loaded, _enabledRead, _enabled = true;
        static TMP_FontAsset _body, _display;
        static Material _bodyOverlay, _displayOverlay, _cardMaterial, _cardOverlay;

        static Color Rgb(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f, 1f);

        /// <summary>Off with VBS_UITHEME=0 (desktop / capture A-B) or when the font assets are missing.</summary>
        public static bool Enabled
        {
            get
            {
                if (!_enabledRead)
                {
                    _enabledRead = true;
                    try { _enabled = Environment.GetEnvironmentVariable("VBS_UITHEME") != "0"; } catch (Exception) { }
                }
                return _enabled && Body != null;
            }
        }

        public static TMP_FontAsset Body { get { Load(); return _body; } }
        public static TMP_FontAsset Display { get { Load(); return _display != null ? _display : _body; } }

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _body = Resources.Load<TMP_FontAsset>(BodyFontPath);
            _display = Resources.Load<TMP_FontAsset>(DisplayFontPath);
            _bodyOverlay = Resources.Load<Material>(BodyFontPath + " Overlay");
            _displayOverlay = Resources.Load<Material>(DisplayFontPath + " Overlay");
            if (_body == null) Debug.LogWarning("[VBS][UiTheme] no font asset at Resources/" + BodyFontPath + ": the theme is off (run UiThemeSetup.Build)");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _loaded = false;
            _enabledRead = false;
            _enabled = true;
            _body = _display = null;
            _bodyOverlay = _displayOverlay = _cardMaterial = _cardOverlay = null;
        }

        /// <summary>The always-on-top material of a font (what the old TextMesh font material did): for restyled game text.</summary>
        public static Material OverlayMaterial(bool display)
        {
            Load();
            var m = display ? _displayOverlay : _bodyOverlay;
            if (m != null) return m;
            var f = display ? Display : Body;
            return f != null ? f.material : null;
        }

        /// <summary>One opaque, vertex-coloured, double-sided material for every card (depth-tested, so hands and bees pass in front).</summary>
        public static Material CardMaterial
        {
            get
            {
                if (_cardMaterial == null)
                    _cardMaterial = GameMaterials.Create("VBS_UiCard (runtime)", Color.white, GameMaterials.Blend.Opaque, vertexColors: true, doubleSided: true);
                return _cardMaterial;
            }
        }

        /// <summary>The card material drawn always on top (the caption card, like its text); the depth-tested one if the shader is missing.</summary>
        public static Material CardOverlayMaterial
        {
            get
            {
                if (_cardOverlay == null)
                {
                    var sh = Resources.Load<Shader>("UiTheme/VBS_UiCardOverlay");
                    if (sh == null || !sh.isSupported) return CardMaterial;
                    _cardOverlay = new Material(sh) { name = "VBS_UiCardOverlay (runtime)" };
                }
                return _cardOverlay;
            }
        }

        /// <summary>Vertex colours skip Unity's colour management: convert by hand in a linear project.</summary>
        public static Color32 Vertex(Color c) => QualitySettings.activeColorSpace == ColorSpace.Linear ? (Color32)c.linear : (Color32)c;

        /// <summary>
        /// A TextMeshPro child of `parent` facing -Z like the builder's TextMeshes. emMm = em height in millimetres at the
        /// parent's scale, box = its width and height in the parent's units. overlay = always on top (restyled game text).
        /// </summary>
        public static TextMeshPro NewText(Transform parent, string name, string text, float emMm, Color color, Vector2 box,
            bool display = false, bool overlay = false, TextAlignmentOptions align = TextAlignmentOptions.Center, bool wrap = true)
        {
            var font = display ? Display : Body;
            if (font == null) return null;
            var go = new GameObject(name, typeof(RectTransform));
            go.SetActive(false); // the font is in place before TextMeshPro wakes up (no default-font load)
            go.layer = parent != null ? parent.gameObject.layer : 0;
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshPro>();
            t.font = font;
            if (overlay) t.fontSharedMaterial = OverlayMaterial(display);
            t.richText = false;
            t.fontSize = emMm;
            t.color = color;
            t.alignment = align;
            t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.rectTransform.sizeDelta = box / TextScale;
            go.transform.localScale = Vector3.one * TextScale;
            t.text = text ?? "";
            go.SetActive(true);
            var r = t.renderer;
            if (r != null)
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            return t;
        }

        /// <summary>0 → 1 with an overshoot (a sticker slapped on, a card popping up).</summary>
        public static float Pop(float t)
        {
            t = Mathf.Clamp01(t);
            const float c1 = 2.2f, c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }
    }
}
