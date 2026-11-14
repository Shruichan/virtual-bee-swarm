using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Video;
#if BEESWAX_UGUI
using UnityEngine.UI;
#endif
using Debug = UnityEngine.Debug;

namespace Beeswax
{
    /// <summary>
    /// The window's live layer: JPEG frames from wss://&lt;bus host&gt;/orbis/frames?token=… decoded newest-first into
    /// one reused Texture2D (at most one decode per frame; frames that arrive while we're behind are dropped).
    /// Falls back to a looping VideoPlayer clip for the current stage when no frame came for staleSeconds, or when
    /// the relay/Hive says orbis{state: failed|ended|off}. The Unity diorama floor stays the real floor underneath.
    /// Frames: binary JPEG, raw (what hive/orbis-relay sends) or behind one kind byte. With relayStatus the socket
    /// also asks for the relay's JSON orbis{} status (?status=1).
    /// The Board's orbis.request (via the bus) picks the layer: live (live, clips as fallback) | clips | off
    /// (hidden, and the frames socket is closed so the relay's idle stop can end a billed session).
    /// </summary>
    public class OrbisWindow : MonoBehaviour
    {
        [SerializeField] BeeswaxBus bus;
        [SerializeField] string framesPath = "/orbis/frames";
        [SerializeField] bool connectOnStart = true;
        [Tooltip("Show the fallback clip when no live frame arrived for this long.")]
        [SerializeField] float staleSeconds = 8f;
        [SerializeField] float maxDecodesPerSecond = 24f;
        [Tooltip("Ask the relay for its status JSON on the frames socket (?status=1), so failed/ended switch to clips at once.")]
        [SerializeField] bool relayStatus = true;

        [Header("Display")]
        [SerializeField] Renderer targetRenderer;
        [Tooltip("URP: _BaseMap. Built-in: _MainTex (used automatically if the first is missing).")]
        [SerializeField] string textureProperty = "_BaseMap";
#if BEESWAX_UGUI
        [SerializeField] RawImage targetRawImage;
#endif
        [Tooltip("Hide the renderer/RawImage when there is neither a live frame nor a clip.")]
        [SerializeField] bool hideWhenEmpty = true;

        [Header("Fallback clips (index = stage)")]
        [SerializeField] VideoPlayer videoPlayer;
        [SerializeField] List<VideoClip> stageClips = new List<VideoClip>();
        [Tooltip("Used when stageClips has no entry: file names under StreamingAssets, e.g. orbis/stage0.mp4.")]
        [SerializeField] List<string> stageClipFiles = new List<string>();
        [SerializeField] Vector2Int fallbackSize = new Vector2Int(832, 480);

        /// <summary>(texture now shown or null, isLive). Use it for your own crossfade.</summary>
        public event Action<Texture, bool> OnTextureChanged;

        public bool IsLive { get; private set; }
        public int Stage { get; private set; }
        public string State { get; private set; } = "";
        public string FallbackClip { get; private set; } = "";
        /// <summary>The Board's choice from orbis.request: "live" (default), "clips" or "off".</summary>
        public string Want { get; private set; } = "live";
        public Texture CurrentTexture { get; private set; }
        public float SecondsSinceFrame => _lastFrameAt < 0f ? float.PositiveInfinity : Time.realtimeSinceStartup - _lastFrameAt;
        public int FramesDecoded { get; private set; }
        public int FramesDropped { get; private set; }
        public float LastDecodeMs { get; private set; }
        public bool Connected => _socket.IsOpen;

        readonly BeeswaxSocket _socket = new BeeswaxSocket();
        Texture2D _live;
        byte[] _lastGood;
        RenderTexture _rt;
        MaterialPropertyBlock _mpb;
        BeeswaxBus _bound;
        byte[] _pending;
        int _pendingOffset;
        float _lastFrameAt = -1f, _nextDecodeAt, _nextAttemptAt;
        int _failures;
        string _socketHost = "";
        bool _wantConnected, _forceFallback, _sourceChosen;
        string _playingClipKey = "";

        void Awake()
        {
            _live = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "OrbisLive", wrapMode = TextureWrapMode.Clamp };
            _socket.Opened += () => { _failures = 0; Debug.Log("[Beeswax] Orbis frames connected: " + _socketHost); };
            _socket.Closed += (wasOpen, reason) =>
            {
                _failures = wasOpen ? 0 : _failures + 1;
                _nextAttemptAt = Time.realtimeSinceStartup + Mathf.Min(8f, 0.5f * Mathf.Pow(2f, _failures));
            };
            _socket.Message += HandleFrame;
        }

        void OnEnable() => Bind();

        void Start()
        {
            Bind();
            if (connectOnStart) _wantConnected = true;
            ChooseSource(force: true);
        }

        void OnDisable() => Unbind();

        void OnDestroy()
        {
            _socket.Close();
            if (_live != null) Destroy(_live);
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
        }

        void Bind()
        {
            var b = bus != null ? bus : BeeswaxBus.Instance;
            if (b == null || b == _bound) return;
            Unbind();
            _bound = b;
            b.OnOrbis += HandleStatus;
            b.OnOrbisRequest += HandleRequest;
        }

        void Unbind()
        {
            if (_bound == null) return;
            _bound.OnOrbis -= HandleStatus;
            _bound.OnOrbisRequest -= HandleRequest;
            _bound = null;
        }

        // ---------------------------------------------------------------- public API
        public void Connect() => _wantConnected = true;

        public void Disconnect()
        {
            _wantConnected = false;
            _socket.Close();
        }

        /// <summary>Which stage clip to fall back to (the window greens a stage per good scan).</summary>
        public void SetStage(int stage)
        {
            if (stage < 0 || stage == Stage) return;
            Stage = stage;
            ChooseSource(force: true);
        }

        /// <summary>Operator/debug: show clips even when live frames arrive.</summary>
        public void ForceFallback(bool on)
        {
            _forceFallback = on;
            ChooseSource(force: true);
        }

        // ---------------------------------------------------------------- frames
        void Update()
        {
            Bind();
            MaintainSocket();
            _socket.Tick();

            float now = Time.realtimeSinceStartup;
            if (_pending != null && now >= _nextDecodeAt)
            {
                Decode(_pending, _pendingOffset);
                _pending = null;
                _nextDecodeAt = now + 1f / Mathf.Max(1f, maxDecodesPerSecond);
            }
            ChooseSource(force: false);
        }

        void MaintainSocket()
        {
            var b = _bound;
            if (Want == "off")
            {
                if (_socket.State != BeeswaxSocket.Status.Idle) _socket.Close();
                return;
            }
            if (!_wantConnected || b == null || string.IsNullOrEmpty(b.CurrentHost)) return;
            // Follow the bus to the host it actually reached, right away.
            if (b.IsConnected && _socketHost != b.CurrentHost)
            {
                _socket.Abort();
                _socketHost = b.CurrentHost;
                _failures = 0;
                _nextAttemptAt = 0f;
            }
            if (_socket.State == BeeswaxSocket.Status.Idle && Time.realtimeSinceStartup >= _nextAttemptAt)
            {
                _socketHost = b.CurrentHost;
                _socket.Open(b.UrlFor(framesPath, relayStatus ? "status=1" : null));
            }
        }

        void HandleFrame(byte[] data)
        {
            if (data == null || data.Length < 3) return;
            if (BeeswaxProtocol.LooksLikeJson(data))
            {
                string json = BeeswaxProtocol.Utf8(data);
                if (BeeswaxProtocol.PeekType(json) == MsgType.Orbis)
                {
                    try { HandleStatus(BeeswaxProtocol.Parse<OrbisStatus>(json)); }
                    catch (Exception e) { Debug.LogWarning("[Beeswax] bad orbis status: " + e.Message); }
                }
                return;
            }
            int offset;
            if (data[0] == 0xFF && data[1] == 0xD8) offset = 0;          // raw JPEG
            else if (data[1] == 0xFF && data[2] == 0xD8) offset = 1;     // [kind][JPEG]
            else return;
            if (_pending != null) FramesDropped++;
            _pending = data;
            _pendingOffset = offset;
        }

        void Decode(byte[] data, int offset)
        {
            if (offset > 0)
            {
                var jpeg = new byte[data.Length - offset];
                Buffer.BlockCopy(data, offset, jpeg, 0, jpeg.Length);
                data = jpeg;
            }
            var sw = Stopwatch.StartNew();
            bool ok;
            try { ok = _live.LoadImage(data, false); }
            catch (Exception) { ok = false; }
            LastDecodeMs = (float)sw.Elapsed.TotalMilliseconds;
            if (!ok)
            {
                // A failed LoadImage leaves Unity's 8x8 "?" image in the texture: show the last good frame again.
                if (_lastGood != null) _live.LoadImage(_lastGood, false);
                return;
            }
            _lastGood = data;
            FramesDecoded++;
            _lastFrameAt = Time.realtimeSinceStartup;
        }

        void HandleRequest(OrbisRequest r)
        {
            if (r.want == "live" || r.want == "clips" || r.want == "off") Want = r.want;
            if (r.stage >= 0) Stage = r.stage;
            ChooseSource(force: true);
        }

        void HandleStatus(OrbisStatus s)
        {
            State = s.state;
            FallbackClip = s.fallback_clip;
            if (s.stage >= 0) Stage = s.stage;
            ChooseSource(force: true);
        }

        // ---------------------------------------------------------------- live vs clip
        void ChooseSource(bool force)
        {
            bool off = Want == "off";
            bool stateAllowsLive = State != OrbisState.Failed && State != OrbisState.Ended && State != OrbisState.Off;
            bool live = !off && !_forceFallback && Want != "clips" && stateAllowsLive && SecondsSinceFrame < staleSeconds;
            if (!force && _sourceChosen && live == IsLive) return;
            _sourceChosen = true;
            IsLive = live;
            if (off)
            {
                StopClip();
                Show(null, false, hidden: true);
            }
            else if (live)
            {
                StopClip();
                Show(_live, true);
            }
            else Show(PlayClip(), false);
        }

        Texture PlayClip()
        {
            VideoClip clip = null;
            string file = null;
            if (FallbackClip.Length > 0)
            {
                clip = stageClips.Find(c => c != null && c.name == Path.GetFileNameWithoutExtension(FallbackClip));
                if (clip == null)
                    file = stageClipFiles.Find(f => !string.IsNullOrEmpty(f) &&
                        Path.GetFileNameWithoutExtension(f) == Path.GetFileNameWithoutExtension(FallbackClip));
            }
            if (clip == null && file == null)
            {
                if (stageClips.Count > 0) clip = stageClips[Mathf.Clamp(Stage, 0, stageClips.Count - 1)];
                if (clip == null && stageClipFiles.Count > 0) file = stageClipFiles[Mathf.Clamp(Stage, 0, stageClipFiles.Count - 1)];
            }
            if (clip == null && string.IsNullOrEmpty(file)) { StopClip(); return null; }

            EnsurePlayer();
            string key = clip != null ? "clip:" + clip.name : "file:" + file;
            if (key != _playingClipKey)
            {
                _playingClipKey = key;
                if (clip != null) { videoPlayer.source = VideoSource.VideoClip; videoPlayer.clip = clip; }
                else { videoPlayer.source = VideoSource.Url; videoPlayer.url = Path.Combine(Application.streamingAssetsPath, file); }
                videoPlayer.Play();
            }
            else if (!videoPlayer.isPlaying) videoPlayer.Play();
            return _rt;
        }

        void StopClip()
        {
            if (videoPlayer != null && videoPlayer.isPlaying) videoPlayer.Stop();
            _playingClipKey = "";
        }

        void EnsurePlayer()
        {
            if (_rt == null)
            {
                _rt = new RenderTexture(fallbackSize.x, fallbackSize.y, 0) { name = "OrbisFallback" };
                _rt.Create();
            }
            if (videoPlayer == null) videoPlayer = gameObject.AddComponent<VideoPlayer>();
            videoPlayer.playOnAwake = false;
            videoPlayer.isLooping = true;
            videoPlayer.skipOnDrop = true;
            videoPlayer.audioOutputMode = VideoAudioOutputMode.None; // window clips are silent
            videoPlayer.renderMode = VideoRenderMode.RenderTexture;
            videoPlayer.targetTexture = _rt;
            videoPlayer.errorReceived -= HandleVideoError;
            videoPlayer.errorReceived += HandleVideoError;
        }

        static void HandleVideoError(VideoPlayer vp, string message) => Debug.LogWarning("[Beeswax] Orbis fallback clip: " + message);

        void Show(Texture tex, bool live, bool hidden = false)
        {
            bool changed = tex != CurrentTexture;
            CurrentTexture = tex;
            bool visible = !hidden && (tex != null || !hideWhenEmpty);
            if (targetRenderer != null)
            {
                targetRenderer.enabled = visible;
                if (tex != null)
                {
                    _mpb = _mpb ?? new MaterialPropertyBlock();
                    var mat = targetRenderer.sharedMaterial;
                    string prop = mat != null && !mat.HasProperty(textureProperty) && mat.HasProperty("_MainTex") ? "_MainTex" : textureProperty;
                    targetRenderer.GetPropertyBlock(_mpb);
                    _mpb.SetTexture(prop, tex);
                    targetRenderer.SetPropertyBlock(_mpb);
                }
            }
#if BEESWAX_UGUI
            if (targetRawImage != null)
            {
                targetRawImage.enabled = visible;
                targetRawImage.texture = tex;
            }
#endif
            if (changed)
            {
                try { OnTextureChanged?.Invoke(tex, live); } catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
