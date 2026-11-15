using UnityEngine;

namespace VirtualBeeSwarm.Stakes
{
    /// <summary>
    /// HONEYMOON-1's display, v3.1 states:
    ///   pre / hook / meet       EARTH → MOON · BOARDING 4:00
    ///   smoker … what-if        EARTH → MOON · BOARDING m:ss   (counts down from the start of `smoker`)
    ///   past 4:00               BOARDING DELAYED (UNION RULES)   (no fail ending)
    ///   escape                  EVACUATION CANCELLED
    ///   t0                      PASSENGER: BEESWAX · 3 · 2 · 1, then LIFTOFF
    /// It draws its own TextMesh (same font and colour as the rocket's `CountdownDisplay`; v3.3: a small gate sign on a
    /// dark backing, on the table just in front of the rocket's base and turned to the player, instead of over the
    /// fins) and hides that one's renderer while it runs, so GameDirector's "T-m:ss" and T0Sequence's writes go to an
    /// invisible mesh and nothing fights. T0Sequence's countdown digits are mirrored from that hidden mesh until T0 calls
    /// <see cref="ShowPassengerCount"/> itself (INTEGRATION.md). Text changes at most once a second (no per-frame garbage).
    /// </summary>
    public class StakesRocketDisplay : MonoBehaviour
    {
        [Tooltip("The rocket's CountdownDisplay (GameDirector.countdownText / T0Sequence.rocketDisplay). Found by name if empty.")]
        [SerializeField] TextMesh source;
        [SerializeField] float charSizeScale = 0.65f;
        [Tooltip("v3.3: the sign's centre, in the rocket's space (no scale): in front of the base, toward the player (-Z).")]
        [SerializeField] Vector3 signPlace = new Vector3(-0.07f, 0.03f, -0.10f);
        [SerializeField] Color backingColor = new Color(0.08f, 0.06f, 0.05f, 1f);
        [SerializeField] Vector2 backingPadding = new Vector2(0.014f, 0.01f);
        [SerializeField] Color color = new Color(1f, 0.85f, 0.3f, 1f);
        [SerializeField] Color delayedColor = new Color(1f, 0.45f, 0.2f, 1f);
        [SerializeField] Color cancelledColor = new Color(0.55f, 1f, 0.55f, 1f);

        public string Text => _text != null ? _text.text : "";
        /// <summary>The countdown clock: seconds since `smoker` started (frozen at the escape). -1 = not started.</summary>
        public float ClockSeconds => _clockStart < 0f ? -1f : (_clockEnd >= 0f ? _clockEnd : Time.time) - _clockStart;

        TextMesh _text;
        MeshRenderer _sourceRenderer, _textRenderer;
        Transform _rocket, _backing;
        GameDirector _director;
        float _clockStart = -1f, _clockEnd = -1f;
        int _lastSecond = int.MinValue;
        string _arrow = "→", _dot = "·";
        string _lastSourceText;
        string _passenger;
        bool _explicitT0, _loggedClockRunning;

        public void Bind(GameDirector director) => _director = director;

        void Awake()
        {
            if (source == null) source = FindSource();
            if (source == null) { Debug.Log("[VBS][Stakes] rocket display: no CountdownDisplay found, display off"); enabled = false; return; }
            var go = new GameObject("StakesDisplay");
            _rocket = source.transform.parent;
            for (var p = source.transform.parent; p != null; p = p.parent)
                if (p.name == "HONEYMOON-1") { _rocket = p; break; }
            go.transform.SetParent(_rocket, false);
            Vector3 ls = _rocket.lossyScale;
            go.transform.localScale = new Vector3(1f / Mathf.Max(1e-4f, Mathf.Abs(ls.x)), 1f / Mathf.Max(1e-4f, Mathf.Abs(ls.y)), 1f / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));
            PlaceSign(go.transform);
            go.layer = source.gameObject.layer;
            _text = go.AddComponent<TextMesh>();
            _text.font = source.font;
            _text.fontSize = source.fontSize;
            _text.characterSize = source.characterSize * charSizeScale;
            _text.anchor = TextAnchor.MiddleCenter;
            _text.alignment = TextAlignment.Center;
            _text.lineSpacing = 0.9f;
            _text.color = color;
            _sourceRenderer = source.GetComponent<MeshRenderer>();
            var r = go.GetComponent<MeshRenderer>();
            _textRenderer = r;
            if (_sourceRenderer != null) r.sharedMaterial = _sourceRenderer.sharedMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            // the backing: a dark plate behind the text (TextMesh reads from -Z, so behind = +Z)
            var back = GameObject.CreatePrimitive(PrimitiveType.Quad);
            back.name = "Backing";
            var bc = back.GetComponent<Collider>();
            if (bc != null) Destroy(bc);
            back.layer = go.layer;
            _backing = back.transform;
            _backing.SetParent(go.transform, false);
            _backing.localPosition = new Vector3(0f, 0f, 0.002f);
            _backing.localScale = new Vector3(0.17f, 0.06f, 1f);
            var br = back.GetComponent<MeshRenderer>();
            br.sharedMaterial = GameMaterials.Create("VBS_RocketDisplayBacking (runtime)", backingColor);
            br.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            br.receiveShadows = false;
            var f = source.font;
            if (f != null)
            {
                // a dynamic font may not have the arrow / middle dot on this device: fall back to ASCII
                if (!f.HasCharacter('→')) _arrow = "->";
                if (!f.HasCharacter('·')) _dot = "-";
            }
        }

        /// <summary>In front of the rocket's base, turned to the player's eye, its lines level in the player's view.</summary>
        void PlaceSign(Transform t)
        {
            if (_rocket == null) return;
            Quaternion yaw = Quaternion.Euler(0f, _rocket.eulerAngles.y, 0f);
            Vector3 p = _rocket.position + yaw * signPlace;
            t.position = p;
            var cam = Camera.main;
            Vector3 view = cam != null ? p - cam.transform.position : yaw * new Vector3(0f, -0.7f, 1f);
            if (view.sqrMagnitude < 1e-6f) view = yaw * Vector3.forward;
            t.rotation = Quaternion.LookRotation(view, cam != null ? cam.transform.up : Vector3.up); // level on screen
        }

        /// <summary>The backing follows the text's size (one or two lines).</summary>
        void FitBacking()
        {
            if (_backing == null || _textRenderer == null) return;
            Bounds b = _textRenderer.localBounds;
            if (b.size.x < 1e-5f) return;
            Vector3 ls = _text.transform.lossyScale;
            float sx = Mathf.Max(1e-4f, Mathf.Abs(ls.x)), sy = Mathf.Max(1e-4f, Mathf.Abs(ls.y));
            _backing.localPosition = new Vector3(b.center.x, b.center.y, 0.002f);
            _backing.localScale = new Vector3(b.size.x + 2f * backingPadding.x / sx, b.size.y + 2f * backingPadding.y / sy, 1f);
        }

        static TextMesh FindSource()
        {
            var all = FindObjectsByType<TextMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
                if (all[i].name == "CountdownDisplay") return all[i];
            return null;
        }

        void OnEnable() => ShowOwn(StakesFlags.Display);

        void OnDisable() => ShowOwn(false);

        void ShowOwn(bool on)
        {
            if (_text != null) _text.gameObject.SetActive(on);
            if (_sourceRenderer != null) _sourceRenderer.enabled = !on;
        }

        // ------------------------------------------------------------------ driven by StakesDirector
        public void ResetClock()
        {
            _clockStart = _clockEnd = -1f;
            _lastSecond = int.MinValue;
        }

        /// <summary>The countdown starts (first entry into `smoker` or any later station since pre).</summary>
        public void StartClock()
        {
            if (_clockStart >= 0f) { _clockEnd = -1f; return; }
            _clockStart = Time.time;
            _clockEnd = -1f;
            Debug.Log("[VBS][Stakes] boarding clock started");
        }

        public void StopClock()
        {
            if (_clockStart >= 0f && _clockEnd < 0f) _clockEnd = Time.time;
        }

        /// <summary>T-0 hook: the countdown digit (3, 2, 1), or 0 for just the passenger line.</summary>
        public void ShowPassengerCount(int n)
        {
            _explicitT0 = true;
            _passenger = n <= 0 ? "PASSENGER: BEESWAX" : (n >= 3 || _passenger == null ? "PASSENGER: BEESWAX\n" + n : _passenger + " " + _dot + " " + n);
            Set(_passenger, color);
        }

        void LateUpdate()
        {
            if (_text == null) return;
            bool on = StakesFlags.Display;
            if (_text.gameObject.activeSelf != on) ShowOwn(on);
            if (!on || _director == null) return;
            if (_director.Beat < GameBeat.T0) PlaceSign(_text.transform); // T-0 on: it rides the rocket as it is
            FitBacking();

            var beat = _director.Beat;
            if (beat == GameBeat.T0) { MirrorT0(); return; }
            _passenger = null;
            _lastSourceText = null;

            float limit = _director.TimeLimitSeconds > 0f ? _director.TimeLimitSeconds : 240f;
            if (beat == GameBeat.Escape) { SetOnce(-2, "EVACUATION\nCANCELLED", cancelledColor); return; }
            if (beat >= GameBeat.Map) return; // the crashed rocket keeps whatever T-0 left on it
            // The director's run clock, so BOARDING freezes at the 4th repair with the score (no ticking through the fire).
            int remaining = _clockStart < 0f ? Mathf.CeilToInt(limit) : Mathf.CeilToInt(limit - _director.RunSeconds);
            if (remaining <= 0) { SetOnce(-1, "BOARDING DELAYED\n(UNION RULES)", delayedColor); return; }
            if (remaining == _lastSecond) return;
            _lastSecond = remaining;
            Set("EARTH " + _arrow + " MOON\nBOARDING " + GameMaterials.Clock(remaining), color);
        }

        void SetOnce(int key, string text, Color c)
        {
            if (_lastSecond == key) return;
            _lastSecond = key;
            Set(text, c);
        }

        /// <summary>Until T0Sequence calls ShowPassengerCount: read what it writes to the hidden CountdownDisplay.</summary>
        void MirrorT0()
        {
            _lastSecond = int.MinValue;
            if (_explicitT0) return;
            string s = source != null ? source.text : null;
            if (_passenger == null) { _passenger = "PASSENGER: BEESWAX"; _lastSourceText = s; Set(_passenger, color); return; }
            if (ReferenceEquals(s, _lastSourceText) || s == _lastSourceText) return;
            _lastSourceText = s;
            if (s != null && s.Length == 1 && s[0] >= '1' && s[0] <= '9')
            {
                _passenger = s[0] == '3' || _passenger == "PASSENGER: BEESWAX" ? "PASSENGER: BEESWAX\n" + s : _passenger + " " + _dot + " " + s;
                Set(_passenger, color);
            }
            else if (s == "LIFTOFF") Set("LIFTOFF", color);
            else if (s == "HONEYMOON-1") Set("HONEYMOON-1", color);
        }

        void Set(string text, Color c)
        {
            if (_text.text == text && _text.color == c) return;
            // log state changes, not every second of the countdown
            bool tick = text.StartsWith("EARTH", System.StringComparison.Ordinal) && _text.text != null &&
                        _text.text.StartsWith("EARTH", System.StringComparison.Ordinal) && _loggedClockRunning == (_clockStart >= 0f);
            _text.text = text;
            _text.color = c;
            if (tick) return;
            _loggedClockRunning = _clockStart >= 0f;
            Debug.Log("[VBS][Stakes] rocket: " + text.Replace('\n', ' '));
        }

        /// <summary>Back to the start (reset to pre).</summary>
        public void ResetDisplay()
        {
            ResetClock();
            _passenger = null;
            _lastSourceText = null;
            _explicitT0 = false;
        }

        void OnDestroy()
        {
            if (_sourceRenderer != null) _sourceRenderer.enabled = true;
        }
    }
}
