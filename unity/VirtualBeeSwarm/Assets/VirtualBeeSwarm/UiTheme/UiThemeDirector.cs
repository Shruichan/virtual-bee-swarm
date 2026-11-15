using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm.Theme
{
    /// <summary>
    /// Themes every text surface without editing the systems that own them. Installs itself into any scene with a
    /// GameDirector, finds the TextMeshes (also the ones made later at runtime: stamps, the rocket's gate sign, the
    /// placement label) by name and parent, and puts a ThemedText on each: the themed font everywhere, and a card where
    /// the text used to float bare in the air (Beeswax's caption becomes a paper tag with a tail down to him, the pre
    /// instructions and the what-if prompt become cards, the rocket sign and the level map get the dark card).
    /// Left alone: the "Sound by Satie" credit, and anything under an object named "[NoTheme]...".
    /// VBS_UITHEME=0 or UiThemeDirector.AutoInstall = false turns it off.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class UiThemeDirector : MonoBehaviour
    {
        public static bool AutoInstall = true;
        public static UiThemeDirector Instance { get; private set; }

        const float ScanSeconds = 1f, BurstScanSeconds = 0.1f, BurstSeconds = 3f; // a stamp's TextMesh is made the moment it is stamped: catch it during its pop-in
        float _burstUntil;
        GameDirector _director;
        GameNet _net;
        Transform _titlePlate;
        bool _titlePlateDone;

        readonly HashSet<int> _seen = new HashSet<int>();
        float _nextScan;

        public int Themed { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            Instance = null;
            if (!AutoInstall) return;
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<UiThemeDirector>() != null) return;
            if (!UiTheme.Enabled) { Debug.Log("[VBS][UiTheme] off (VBS_UITHEME=0 or no font asset)"); return; }
            var go = new GameObject("[VBS] UiTheme");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<UiThemeDirector>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_director != null) _director.BeatChanged -= OnBeat;
            if (_net != null) _net.ChoiceMade -= OnChoice;
        }

        void Burst() => _burstUntil = Time.unscaledTime + BurstSeconds;
        void OnBeat(GameBeat prev, GameBeat beat) { Burst(); _nextScan = 0f; }
        void OnChoice(GameStation s, string choice, bool correct) { Burst(); _nextScan = 0f; }

        void Subscribe()
        {
            if (_director == null && GameDirector.Instance != null) { _director = GameDirector.Instance; _director.BeatChanged += OnBeat; }
            if (_net == null && GameNet.Instance != null) { _net = GameNet.Instance; _net.ChoiceMade += OnChoice; }
        }

        /// <summary>
        /// The first pass waits a few frames: on the first frame the hook's VideoPlayers report their load errors
        /// (always, in a -nographics smoke player), and Unity hands the message string to errorReceived unrooted, so a
        /// collection between the failed load and the callback (font assets + 40 TextMeshPros allocate enough to cause
        /// one) turns it into garbage and throws inside the owner's handler.
        /// </summary>
        public const int StartFrames = 5;
        bool _ready;

        System.Collections.IEnumerator Start()
        {
            for (int i = 0; i < StartFrames; i++) yield return null;
            _ready = true;
            Subscribe();
            Burst();
            Scan();
            ThemeBacking("LevelMap", "Backing", UiCard.Look.Dark);
            ThemeButtons();
            Debug.Log("[VBS][UiTheme] themed " + Themed + " text surface(s): fonts " + UiTheme.Body.name + " / " + UiTheme.Display.name);
            UiThemeShots.InstallIfAsked();
        }

        void Update()
        {
            if (!_ready || Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + (Time.unscaledTime < _burstUntil ? BurstScanSeconds : ScanSeconds);
            Subscribe();
            Scan();
            HideTitlePlate();
        }

        /// <summary>The title's dark plate sits behind the pre instructions, which now have their own paper card.</summary>
        void HideTitlePlate()
        {
            if (_titlePlateDone) return;
            if (_titlePlate == null)
            {
                var td = FindAnyObjectByType<VirtualBeeSwarm.Title.TitleDirector>(FindObjectsInactive.Include);
                if (td == null) { if (Time.unscaledTime > 20f) _titlePlateDone = true; return; }
                _titlePlate = td.transform.Find("TitleTextPlate");
                if (_titlePlate == null) return;
            }
            var r = _titlePlate.GetComponent<Renderer>();
            if (r == null) return;
            r.forceRenderingOff = true;
            _titlePlateDone = true;
            Debug.Log("[VBS][UiTheme] title text plate hidden (the instructions have their own card)");
        }

        void Scan()
        {
            var all = FindObjectsByType<TextMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                var tm = all[i];
                if (tm == null || !_seen.Add(tm.GetInstanceID())) continue;
                var style = StyleFor(tm);
                if (style != null && ThemedText.Attach(tm, style) != null) Themed++;
            }
        }

        // ------------------------------------------------------------ the rules
        static readonly UiTextStyle Body = new UiTextStyle { what = "label" };
        static readonly UiTextStyle Title = new UiTextStyle { what = "title", display = true };
        static readonly UiTextStyle Caption = new UiTextStyle
        {
            what = "Beeswax caption", reflow = true, card = true, cardOnTop = true, recolor = true, tail = 0.024f, pad = new Vector2(0.016f, 0.01f),
            hideSibling = "[VBS] Caption Plate",
            queue = 3900, // Beeswax's words stay on top, also over the held end card and its sticker strip // Juice's dark plate (made later; ThemedText looks again on each poll)
        };
        static readonly UiTextStyle Instructions = new UiTextStyle
        {
            what = "pre instructions", card = true, recolor = true, pad = new Vector2(0.022f, 0.014f), sticker = StickerArt.Front, stickerSize = 0.07f,
        };
        static readonly UiTextStyle Prompt = new UiTextStyle
        {
            what = "what-if prompt", card = true, recolor = true, pad = new Vector2(0.018f, 0.006f), sticker = StickerArt.Flying, stickerSize = 0.06f,
        };
        static readonly UiTextStyle Transcript = new UiTextStyle
        {
            what = "what-if transcript", card = true, look = UiCard.Look.Slip, recolor = true, pad = new Vector2(0.012f, 0.007f),
        };
        static readonly UiTextStyle PromptHint = new UiTextStyle
        {
            what = "what-if hint", card = true, look = UiCard.Look.Slip, recolor = true, ink = UiTheme.Brown, pad = new Vector2(0.009f, 0.004f), behind = 0.002f,
        };
        static readonly UiTextStyle RocketSign = new UiTextStyle
        {
            what = "rocket gate sign", card = true, look = UiCard.Look.Dark, pad = new Vector2(0.014f, 0.009f), hideSibling = "Backing",
        };
        static readonly UiTextStyle Stamp = new UiTextStyle { what = "stamp", display = true };
        static readonly UiTextStyle Score = new UiTextStyle { what = "score card", display = true };
        static readonly UiTextStyle Sign = new UiTextStyle { what = "SIGN HERE", display = true };
        static readonly UiTextStyle PriceTag = new UiTextStyle { what = "price tag" };
        static readonly UiTextStyle Placement = new UiTextStyle { what = "placement label", display = true };

        static UiTextStyle StyleFor(TextMesh tm)
        {
            string n = tm.name;
            Transform p = tm.transform.parent;
            string pn = p != null ? p.name : "";
            if (n == "[VBS] SoundCredit") return null; // the licence credit stays exactly as its owner made it
            for (var t = tm.transform; t != null; t = t.parent)
                if (t.name.StartsWith("[NoTheme]", System.StringComparison.Ordinal)) return null;

            if (pn.StartsWith("Caption", System.StringComparison.Ordinal) && tm.GetComponentInParent<BeeswaxController>(true) != null) return Caption;
            if (n == "Instructions") return Instructions;
            if (pn == "WhatIfUI") return n == "Prompt" ? Prompt : n == "Transcript" ? Transcript : n == "Hint" ? PromptHint : Body;
            if (n == "StakesDisplay") return RocketSign;
            if (n == "[VBS] StampText") return Stamp;
            if (n == "Score") return Score;
            if (pn == "SignHere" || (p != null && p.GetComponentInParent<SignHereClipboard>(true) != null)) return Sign;
            if (n == "Title") return Title;
            if (tm.GetComponentInParent<ChoiceButton>(true) != null) return PriceTag;
            if (tm.GetComponentInParent<VirtualBeeSwarm.AR.InstantPlacement>(true) != null) return Placement;
            if (pn == "Thermometer") return Title;
            return Body;
        }

        /// <summary>
        /// The choice buttons / price tags keep their honeycomb face, in the theme's colours: honey while it can be
        /// picked, faded paper once it is locked (it was flat grey), grievance red when it was the wrong pick.
        /// </summary>
        static void ThemeButtons()
        {
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var t = typeof(ChoiceButton);
            var idle = t.GetField("idleColor", F); var hover = t.GetField("hoverColor", F);
            var locked = t.GetField("lockedColor", F); var wrong = t.GetField("wrongColor", F);
            int n = 0;
            foreach (var b in FindObjectsByType<ChoiceButton>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (idle != null) idle.SetValue(b, UiTheme.Honey);
                if (hover != null) hover.SetValue(b, Color.Lerp(UiTheme.Honey, UiTheme.Cream, 0.55f));
                if (locked != null) locked.SetValue(b, UiTheme.Faded);
                if (wrong != null) wrong.SetValue(b, UiTheme.Grievance);
                n++;
            }
            Debug.Log("[VBS][UiTheme] " + n + " price tag(s) in the theme's colours" + (locked == null ? " (ChoiceButton colour fields not found)" : ""));
        }

        /// <summary>A dark placeholder slab (a child Cube) becomes a themed card of the same size; the slab only stops drawing.</summary>
        static void ThemeBacking(string rootName, string backingName, UiCard.Look look)
        {
            foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t.name != rootName) continue;
                var backing = t.Find(backingName);
                var r = backing != null ? backing.GetComponent<MeshRenderer>() : null;
                var mf = backing != null ? backing.GetComponent<MeshFilter>() : null;
                if (r == null || mf == null || mf.sharedMesh == null || mf.sharedMesh.name != "Cube") continue; // dressed with art: leave it
                Vector3 s = backing.localScale;
                var card = UiCard.Create(t, "[VBS] UiCard", new Vector2(Mathf.Abs(s.x), Mathf.Abs(s.y)), look);
                card.transform.localPosition = backing.localPosition - new Vector3(0f, 0f, Mathf.Abs(s.z) * 0.5f);
                card.transform.localRotation = backing.localRotation;
                r.forceRenderingOff = true;
                Debug.Log("[VBS][UiTheme] " + rootName + "/" + backingName + " -> " + look + " card " + s.x.ToString("0.00") + " x " + s.y.ToString("0.00") + " m");
            }
        }
    }
}
