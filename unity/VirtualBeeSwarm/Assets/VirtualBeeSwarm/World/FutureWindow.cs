using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The portal next to the hive. Shows the Orbis picture (live frames or the recorded clip, both via
    /// GameNet -> the client's OrbisWindow) on livePane. When there's neither, fallbackPane shows a local
    /// stand-in: scrolling noise tinted per scene, morphing over ~2.5 s like Orbis does. Scenes come from
    /// GameNet.SceneChanged (the Hive's orbis{scene}, or local RequestScene calls). SetOverride puts another picture
    /// (ForecastMoment's clips at the hook) on livePane in front of the Hive's until it is cleared.
    /// Modeler: replace frame/glow; keep the two panes (same size, livePane in front), and portalRoot (scaled).
    /// </summary>
    public class FutureWindow : MonoBehaviour
    {
        [SerializeField] GameNet net;
        [Tooltip("Scaled for open/close/grow/moon.")]
        [SerializeField] Transform portalRoot;
        [SerializeField] Renderer livePane;
        [SerializeField] Renderer fallbackPane;
        [SerializeField] Renderer glow;
        [SerializeField] Texture2D fallbackNoise;
        [SerializeField] float morphSeconds = 2.5f;
        [SerializeField] float openSeconds = 1.2f;
        [SerializeField] Color glowColor = new Color(1f, 0.8f, 0.35f, 0.5f);
        [SerializeField] float growScale = 1.5f;
        [SerializeField] float moonScale = 2.4f;
        [Tooltip("How long the frame glow flares when the scene changes.")]
        [SerializeField] float flareSeconds = 1.5f;

        public bool IsOpen => _openTarget > 0.5f;
        /// <summary>Seconds from Open() to fully open.</summary>
        public float OpenSeconds => openSeconds;
        public bool ShowingOrbis { get; private set; }
        public Transform PaneTransform => fallbackPane != null ? fallbackPane.transform : transform;
        public string Scene => _scene;
        /// <summary>The honeycomb rim, halo, vignette, swirl and flare (null if the pane has no mesh).</summary>
        public PortalFx Fx => _fx;
        /// <summary>Largest size factor Grow / SwellToMoon may reach (ArRig sets it so the free-floating portal stays
        /// under the ceiling and over the hive roof). Default: no limit.</summary>
        public float MaxSize { get; set; } = float.MaxValue;
        PortalFx _fx;

        Vector3 _baseScale = Vector3.one;
        float _open, _openTarget, _size = 1f, _sizeTarget = 1f;
        Color _fromA, _fromB, _toA, _toB;
        bool _fromFlicker, _toFlicker;
        float _morphStart = -100f;
        string _scene = "";
        Texture _liveTex, _override;
        MaterialPropertyBlock _mpb;

        void Awake()
        {
            if (net == null) net = FindAnyObjectByType<GameNet>();
            if (portalRoot == null) portalRoot = transform;
            _baseScale = portalRoot.localScale;
            _mpb = new MaterialPropertyBlock();
            if (fallbackNoise == null) fallbackNoise = GameMeshes.CreateNoiseTexture();
            SceneColors("", out _toA, out _toB, out _toFlicker);
            _fromA = _toA; _fromB = _toB;
            // built at portalRoot's base scale, before ApplyOpen squashes it
            Vector3 toPlayer = -transform.forward;
            _fx = PortalFx.Build(portalRoot, fallbackPane, toPlayer);
            if (_fx != null) Debug.Log("[VBS][Portal] " + _fx.Summary);
            ApplyOpen(0f);
        }

        void OnEnable() { if (net != null) net.SceneChanged += SetScene; }
        void OnDisable() { if (net != null) net.SceneChanged -= SetScene; }

        GameDirector _director;
        AR.ArRig _rig;
        bool _faceViewer;

        void Start()
        {
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (_director != null) _director.BeatChanged += HandleBeat;
            _rig = AR.ArRig.Instance != null ? AR.ArRig.Instance : FindAnyObjectByType<AR.ArRig>();
            if (_rig != null) _rig.RoomChanged += HandleRoomChanged;
        }

        void OnDestroy()
        {
            if (_director != null) _director.BeatChanged -= HandleBeat;
            if (_rig != null) _rig.RoomChanged -= HandleRoomChanged;
        }

        void HandleRoomChanged(AR.RoomModel _) => _faceViewer = true;

        // A Board jump (beat.request) or a skip past `meet` must not leave the window shut for the rest of the show.
        void HandleBeat(GameBeat prev, GameBeat beat)
        {
            if (beat > GameBeat.Meet && !IsOpen) Open();
        }

        // ------------------------------------------------------------ API
        public void Open()
        {
            if (!IsOpen) _faceViewer = true;
            _openTarget = 1f;
        }
        public void Close() => _openTarget = 0f;
        public void Grow()
        {
            _sizeTarget = Mathf.Min(growScale, Mathf.Max(1f, MaxSize));
            if (_fx != null) _fx.Flare(2.8f, 80); // the escape: the portal flares
        }

        public void SwellToMoon()
        {
            _sizeTarget = Mathf.Min(moonScale, Mathf.Max(1f, MaxSize));
            if (_fx != null) _fx.Flare(1.5f, 30);
        }

        public void NormalSize() => _sizeTarget = 1f;

        /// <summary>Show this texture instead of the Hive's live/clip picture (null: back to the Hive's).</summary>
        public void SetOverride(Texture tex) => _override = tex;

        public void ResetWindow()
        {
            _override = null;
            _openTarget = _open = 0f;
            _sizeTarget = _size = 1f;
            _scene = "";
            SceneColors("", out _toA, out _toB, out _toFlicker);
            _fromA = _toA; _fromB = _toB; _fromFlicker = _toFlicker;
            ApplyOpen(0f);
        }

        /// <summary>Local fallback look for a scene (the live/clip layer is driven by the Hive).</summary>
        public void SetScene(string scene)
        {
            if (scene == _scene) return;
            float k = Mathf.Clamp01((Time.time - _morphStart) / morphSeconds);
            _fromA = Color.Lerp(_fromA, _toA, k);
            _fromB = Color.Lerp(_fromB, _toB, k);
            _fromFlicker = _toFlicker;
            _scene = scene;
            SceneColors(scene, out _toA, out _toB, out _toFlicker);
            _morphStart = Time.time;
            if (_fx != null && _open > 0.05f) _fx.Swirl(Color.Lerp(_toA, _toB, 0.5f)); // ripple/swirl into the new scene
        }

        // ------------------------------------------------------------ frame
        void Update()
        {
            float dt = Time.deltaTime;
            float prevOpen = _open, prevSize = _size;
            _open = Mathf.MoveTowards(_open, _openTarget, dt / Mathf.Max(0.05f, openSeconds));
            _size = Mathf.MoveTowards(_size, _sizeTarget, dt * 0.8f);
            if (_open != prevOpen || _size != prevSize) ApplyOpen(_open);
            if (_open <= 0.001f) return;
            Color glowNow = glowColor;

            // live / clip picture from the Hive (or the override in front of it)
            Texture tex = _override != null ? _override : net != null ? net.OrbisTexture : null;
            bool showLive = livePane != null && tex != null;
            if (livePane != null)
            {
                if (livePane.enabled != showLive) livePane.enabled = showLive;
                if (showLive && !ReferenceEquals(tex, _liveTex))
                {
                    _liveTex = tex;
                    GameMaterials.SetTexture(livePane, tex, new Vector4(1f, 1f, 0f, 0f));
                }
            }
            ShowingOrbis = showLive;

            // Leave the frame attached to the fallback pane in place and draw only the visible surface.
            if (fallbackPane != null && fallbackPane.enabled == showLive) fallbackPane.enabled = !showLive;
            if (fallbackPane != null && !showLive)
            {
                float t = Time.time;
                float k = Mathf.SmoothStep(0f, 1f, (t - _morphStart) / morphSeconds);
                Color a = Color.Lerp(_fromA, _toA, k), b = Color.Lerp(_fromB, _toB, k);
                bool flicker = k > 0.5f ? _toFlicker : _fromFlicker;
                float wave = 0.5f + 0.5f * Mathf.Sin(t * (flicker ? 9f : 1.2f));
                if (flicker) wave *= 0.75f + 0.25f * Mathf.Sin(t * 23f);
                Color c = Color.Lerp(a, b, wave);
                fallbackPane.GetPropertyBlock(_mpb);
                _mpb.SetColor(GameMaterials.ColorId, c);
                _mpb.SetColor(GameMaterials.BaseColorId, c);
                if (fallbackNoise != null)
                {
                    _mpb.SetTexture(GameMaterials.MainTexId, fallbackNoise);
                    _mpb.SetTexture(GameMaterials.BaseMapId, fallbackNoise);
                }
                var st = new Vector4(1.5f, 1f, t * (flicker ? 0.12f : 0.02f), t * (flicker ? 0.25f : 0.01f));
                _mpb.SetVector(GameMaterials.MainTexStId, st);
                _mpb.SetVector(GameMaterials.BaseMapStId, st);
                fallbackPane.SetPropertyBlock(_mpb);
            }

            if (glow != null)
            {
                var g = glowColor;
                g.a *= _open * (0.7f + 0.3f * Mathf.Sin(Time.time * 2f));
                // A new scene (a station outcome, a what-if, a question to Beeswax): the frame flares so eyes go to it.
                float flare = 1f - Mathf.Clamp01((Time.time - _morphStart) / flareSeconds);
                g.a = Mathf.Min(1f, g.a * (1f + 1.5f * flare * flare));
                if (_toFlicker) g = Color.Lerp(g, new Color(1f, 0.3f, 0.05f, g.a), 0.7f);
                GameMaterials.Tint(glow, g);
                glowNow = g;
            }
            else
            {
                float flare = 1f - Mathf.Clamp01((Time.time - _morphStart) / flareSeconds);
                glowNow.a *= _open * (0.7f + 0.3f * Mathf.Sin(Time.time * 2f)) * (1f + 1.5f * flare * flare);
                if (_toFlicker) glowNow = Color.Lerp(glowNow, new Color(1f, 0.3f, 0.05f, glowNow.a), 0.7f);
            }
            if (_fx != null) _fx.Tick(_open, glowNow);
        }

        void LateUpdate()
        {
            if (!_faceViewer || !IsOpen || !Bloom.MeadowLayout.Active) return;
            var camera = Camera.main;
            if (camera == null) return;
            _faceViewer = false;
            Vector3 away = Vector3.ProjectOnPlane(transform.position - camera.transform.position, Vector3.up);
            if (away.sqrMagnitude < 0.04f) return;
            // The picture faces local -Z. Sample the standing spot only when opening or placing the window.
            transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }

        void ApplyOpen(float open)
        {
            float e = open * open * (3f - 2f * open);
            Vector3 s = _baseScale * _size;
            portalRoot.localScale = new Vector3(s.x * e, s.y * Mathf.Max(0.02f, e), s.z);
            bool visible = open > 0.001f;
            if (portalRoot != transform && portalRoot.gameObject.activeSelf != visible) portalRoot.gameObject.SetActive(visible);
        }

        /// <summary>Stand-in palette per scene: (base, accent, flicker).</summary>
        static void SceneColors(string scene, out Color a, out Color b, out bool flicker)
        {
            flicker = false;
            switch (scene)
            {
                case GameIds.Scene.Meadow: a = new Color(0.35f, 0.75f, 0.3f); b = new Color(0.9f, 0.85f, 0.3f); break;
                case GameIds.Scene.SmokerSettle: a = new Color(0.45f, 0.8f, 0.4f); b = new Color(1f, 0.9f, 0.5f); break;
                case GameIds.Scene.SmokerAngry: a = new Color(0.85f, 0.45f, 0.15f); b = new Color(1f, 0.2f, 0.1f); break;
                case GameIds.Scene.FeedSick: a = new Color(0.45f, 0.48f, 0.38f); b = new Color(0.3f, 0.32f, 0.28f); break;
                case GameIds.Scene.FeedRecover: a = new Color(0.4f, 0.8f, 0.35f); b = new Color(0.95f, 0.9f, 0.4f); break;
                case GameIds.Scene.WaspsRaid: a = new Color(0.6f, 0.5f, 0.15f); b = new Color(0.15f, 0.12f, 0.05f); break;
                case GameIds.Scene.WaspsLeave: a = new Color(0.4f, 0.78f, 0.35f); b = new Color(0.95f, 0.85f, 0.35f); break;
                case GameIds.Scene.HeatStress: a = new Color(0.95f, 0.45f, 0.15f); b = new Color(1f, 0.15f, 0.05f); break;
                case GameIds.Scene.HeatDrink: a = new Color(0.3f, 0.7f, 0.8f); b = new Color(0.5f, 0.9f, 0.6f); break;
                case GameIds.Scene.WhatIfFire: a = new Color(1f, 0.4f, 0.05f); b = new Color(0.8f, 0.08f, 0.02f); flicker = true; break;
                case GameIds.Scene.FireOut: a = new Color(0.3f, 0.32f, 0.36f); b = new Color(0.45f, 0.55f, 0.4f); break;
                case GameIds.Scene.WhatIfRain: a = new Color(0.35f, 0.45f, 0.6f); b = new Color(0.6f, 0.7f, 0.85f); break;
                case GameIds.Scene.WhatIfFlowers: a = new Color(0.55f, 0.85f, 0.4f); b = new Color(0.85f, 0.5f, 0.9f); break;
                case GameIds.Scene.WhatIfSpray: a = new Color(0.6f, 0.62f, 0.45f); b = new Color(0.4f, 0.45f, 0.35f); break;
                case GameIds.Scene.WhatIfWinter: a = new Color(0.85f, 0.9f, 0.95f); b = new Color(0.6f, 0.7f, 0.85f); break;
                case GameIds.Scene.FullBloom: a = new Color(0.5f, 0.95f, 0.45f); b = new Color(1f, 0.6f, 0.85f); break;
                case GameIds.Scene.Moon: a = new Color(0.55f, 0.55f, 0.58f); b = new Color(0.25f, 0.25f, 0.3f); break;
                case GameIds.Scene.ForecastPeaceful: a = new Color(0.45f, 0.7f, 0.95f); b = new Color(0.45f, 0.82f, 0.35f); break;
                case GameIds.Scene.ForecastRavaged: a = new Color(0.95f, 0.3f, 0.08f); b = new Color(0.35f, 0.05f, 0.05f); flicker = true; break;
                default: a = new Color(0.15f, 0.2f, 0.15f); b = new Color(0.25f, 0.3f, 0.2f); break;
            }
        }
    }
}
