using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
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
    ///
    /// Scenes (v2): every SCENES entry has a recorded clip. orbis{scene} from the Hive, orbis.request{scene}
    /// from the Board, or RequestScene() here switch the clip at once, whether or not live is up. Clip lookup:
    /// sceneClips (inspector) → the Hive's fallback_clip name → StreamingAssets/&lt;sceneClipFolder&gt;/&lt;fallback_clip&gt;
    /// → StreamingAssets/&lt;sceneClipFolder&gt;/&lt;scene&gt;.mp4 → the old per-stage clips. A scene set while offline is
    /// re-sent to the Hive after the next welcome. The Hive drives the scene itself from choice.made, beat.set and
    /// voice intents; RequestScene is for extras.
    /// Only the Hive's bus statuses set the scene: the relay's own status (frames socket) only updates State, because
    /// the relay keeps the last scene it streamed after a stop and lags the Hive during a morph.
    /// The Hive sends orbis{} only on change, so after each (re)connect the window reads the Hive's current scene and
    /// mode from GET /health (syncOnWelcome).
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

        [Serializable]
        public class SceneClip
        {
            [Tooltip("One of SCENES, e.g. feed_sick.")]
            public string scene;
            public VideoClip clip;
        }

        [Header("Fallback clips per scene")]
        [SerializeField] List<SceneClip> sceneClips = new List<SceneClip>();
        [Tooltip("Scenes without a sceneClips entry play StreamingAssets/<this folder>/<scene>.mp4 (empty = the root).")]
        [SerializeField] string sceneClipFolder = "";
        [SerializeField] bool streamingAssetsByScene = true;
        [Tooltip("Scene shown before the Hive names one.")]
        [SerializeField] string initialScene = Beeswax.Scene.Meadow;
        [Tooltip("After RequestScene, ignore Hive statuses naming another scene for this long (they predate the request).")]
        [SerializeField] float pendingSceneHoldSeconds = 4f;
        [Tooltip("After each bus (re)connect, read the Hive's current scene and Board mode from GET /health " +
                 "(the Hive only sends orbis{} when something changes).")]
        [SerializeField] bool syncOnWelcome = true;

        [Header("Fallback clips (index = stage, v1; used when no scene clip is found)")]
        [SerializeField] VideoPlayer videoPlayer;
        [SerializeField] List<VideoClip> stageClips = new List<VideoClip>();
        [Tooltip("Used when stageClips has no entry: file names under StreamingAssets, e.g. orbis/stage0.mp4.")]
        [SerializeField] List<string> stageClipFiles = new List<string>();
        [SerializeField] Vector2Int fallbackSize = new Vector2Int(832, 480);

        /// <summary>(texture now shown or null, isLive). Use it for your own crossfade.</summary>
        public event Action<Texture, bool> OnTextureChanged;
        /// <summary>The window's scene changed (from the Hive, the Board or RequestScene).</summary>
        public event Action<string> OnSceneChanged;

        public bool IsLive { get; private set; }
        public int Stage { get; private set; }
        /// <summary>Current scene (one of SCENES); its clip plays whenever live is down.</summary>
        public string Scene { get; private set; } = "";
        /// <summary>A scene this Quest asked for that the Hive hasn't confirmed yet ("" = none).</summary>
        public string PendingScene => _pendingScene;
        /// <summary>What the fallback layer is playing: "clip:&lt;name&gt;", "file:&lt;path&gt;" or "".</summary>
        public string PlayingClip => _playingClipKey;
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
        bool _wantConnected, _forceFallback, _sourceChosen, _clipFailed, _wantKnown;
        string _playingClipKey = "", _fallbackClipScene = "", _pendingScene = "";
        float _pendingUntil, _resyncAt = -1f;
        readonly HashSet<string> _failedFiles = new HashSet<string>();
        UnityWebRequest _healthReq;
        int _hiveSeq; // bumped by every orbis{} / orbis.request from the bus: a /health reply older than one is ignored

        [Serializable] class HealthOrbis { public string state = "", scene = "", want = ""; }
        [Serializable] class HealthReply { public HealthOrbis orbis; }

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
            if (Scene.Length == 0 && BeeswaxProtocol.IsScene(initialScene)) Scene = initialScene;
            ChooseSource(force: true);
        }

        void OnDisable()
        {
            Unbind();
            CancelHealth();
        }

        void OnDestroy()
        {
            CancelHealth();
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
            b.OnWelcome += HandleWelcome;
        }

        void Unbind()
        {
            if (_bound == null) return;
            _bound.OnOrbis -= HandleStatus;
            _bound.OnOrbisRequest -= HandleRequest;
            _bound.OnWelcome -= HandleWelcome;
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

        /// <summary>
        /// Show `scene` now (its clip, while live is down) and ask the Hive to steer the live window there
        /// (orbis.request{want: current Want, scene}). Offline, the request is sent after the next welcome.
        /// Returns true if the request went out now.
        /// </summary>
        public bool RequestScene(string scene)
        {
            if (!BeeswaxProtocol.IsScene(scene)) { Debug.LogWarning($"[Beeswax] '{scene}' is not one of SCENES."); return false; }
            ApplyScene(scene);
            _pendingScene = scene;
            var b = _bound;
            bool sent = b != null && b.IsConnected && b.SendOrbisRequest(RequestWant, scene);
            // Unsent (offline): Hive statuses naming other scenes are stale until the request is re-sent.
            _pendingUntil = sent ? Time.realtimeSinceStartup + pendingSceneHoldSeconds : float.PositiveInfinity;
            return sent;
        }

        /// <summary>
        /// The `want` a Quest scene request carries. The Hive treats a want that differs from its own as a mode change,
        /// and its default is "clips", so the Quest must not guess "live" (that would start a billed stream):
        /// a running stream (priming/ready) → "live" (never stop it); otherwise the Board's last request; else "clips".
        /// </summary>
        public string RequestWant =>
            State == OrbisState.Priming || State == OrbisState.Ready ? OrbisWant.Live : _wantKnown ? Want : OrbisWant.Clips;

        /// <summary>Switch the local clip only (preview/debug); the Hive is not told.</summary>
        public void ShowSceneLocally(string scene)
        {
            if (BeeswaxProtocol.IsScene(scene)) ApplyScene(scene);
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
            if (_resyncAt >= 0f && now >= _resyncAt) Resync();
            if (_clipFailed)
            {
                _clipFailed = false;
                ChooseSource(force: true);
            }
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
                    try { HandleRelayStatus(BeeswaxProtocol.Parse<OrbisStatus>(json)); }
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

        // The Board's request is authoritative for the scene too.
        void HandleRequest(OrbisRequest r)
        {
            _hiveSeq++;
            if (r.want == "live" || r.want == "clips" || r.want == "off") { Want = r.want; _wantKnown = true; }
            if (r.stage >= 0) Stage = r.stage;
            if (r.HasScene && BeeswaxProtocol.IsScene(r.scene))
            {
                _pendingScene = "";
                ApplyScene(r.scene);
            }
            ChooseSource(force: true);
        }

        // The relay's status on the frames socket: its state only (live/failed/ended switch the layer at once).
        void HandleRelayStatus(OrbisStatus s)
        {
            if (!IsOrbisState(s.state)) return;
            State = s.state;
            ChooseSource(force: true);
        }

        static bool IsOrbisState(string s) =>
            s == OrbisState.Off || s == OrbisState.Priming || s == OrbisState.Ready || s == OrbisState.Failed || s == OrbisState.Ended;

        void HandleStatus(OrbisStatus s)
        {
            _hiveSeq++;
            State = s.state;
            if (s.stage >= 0) Stage = s.stage;
            bool stale = s.scene.Length > 0 && _pendingScene.Length > 0 && s.scene != _pendingScene &&
                         Time.realtimeSinceStartup < _pendingUntil;
            if (!stale)
            {
                FallbackClip = s.fallback_clip;
                _fallbackClipScene = s.scene;
                if (s.scene.Length > 0 && BeeswaxProtocol.IsScene(s.scene))
                {
                    _pendingScene = "";
                    ApplyScene(s.scene);
                }
            }
            ChooseSource(force: true);
        }

        // A new link: if we changed the scene while the Hive couldn't hear it, tell it again (after its own
        // status for the new link has had a moment to arrive).
        void HandleWelcome(Welcome w)
        {
            if (_pendingScene.Length > 0) _resyncAt = Time.realtimeSinceStartup + 0.5f;
            if (syncOnWelcome) FetchHiveWindow();
        }

        // GET /health → orbis {state, scene, want}: the window as the Hive has it now (it may have changed while we
        // were away, and a fresh app start knows nothing). Any orbis{} / orbis.request that arrives first wins.
        void FetchHiveWindow()
        {
            var b = _bound;
            if (b == null || string.IsNullOrEmpty(b.CurrentHost)) return;
            CancelHealth();
            int seq = _hiveSeq;
            UnityWebRequest req;
            try { req = UnityWebRequest.Get(BeeswaxBus.HealthUrl(b.CurrentHost)); }
            catch (Exception e) { Debug.LogWarning("[Beeswax] window sync: " + e.Message); return; }
            req.timeout = 4;
            _healthReq = req;
            req.SendWebRequest().completed += _ =>
            {
                string text = req.result == UnityWebRequest.Result.Success && req.responseCode == 200 ? req.downloadHandler.text : null;
                req.Dispose();
                if (_healthReq != req) return; // cancelled or superseded
                _healthReq = null;
                if (text == null || seq != _hiveSeq) return;
                HealthReply h;
                try { h = JsonUtility.FromJson<HealthReply>(text); }
                catch (Exception) { return; }
                if (h?.orbis != null) ApplyHiveWindow(h.orbis);
            };
        }

        void ApplyHiveWindow(HealthOrbis o)
        {
            if (o.want == OrbisWant.Live || o.want == OrbisWant.Clips || o.want == OrbisWant.Off) { Want = o.want; _wantKnown = true; }
            if (IsOrbisState(o.state)) State = o.state;
            // Our own unconfirmed scene (set offline) is re-sent by Resync and wins.
            if (_pendingScene.Length == 0 && BeeswaxProtocol.IsScene(o.scene) && o.scene != Scene)
            {
                Debug.Log($"[Beeswax] window: the Hive is on '{o.scene}' (was '{Scene}').");
                ApplyScene(o.scene);
            }
            ChooseSource(force: true);
        }

        void CancelHealth()
        {
            var req = _healthReq;
            _healthReq = null;
            if (req == null) return;
            try { req.Abort(); } catch (Exception) { /* already finished */ }
        }

        void Resync()
        {
            _resyncAt = -1f;
            var b = _bound;
            if (_pendingScene.Length == 0 || b == null || !b.IsConnected) return;
            Debug.Log($"[Beeswax] re-sending window scene '{_pendingScene}' after reconnect.");
            if (b.SendOrbisRequest(RequestWant, _pendingScene)) _pendingUntil = Time.realtimeSinceStartup + pendingSceneHoldSeconds;
        }

        void ApplyScene(string scene)
        {
            if (scene == Scene) return;
            Scene = scene;
            ChooseSource(force: true);
            try { OnSceneChanged?.Invoke(scene); } catch (Exception e) { Debug.LogException(e); }
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
            ResolveClip(out VideoClip clip, out string file);
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

        /// <summary>The clip (or StreamingAssets file) the fallback layer would play for the current scene/stage.</summary>
        public void ResolveClip(out VideoClip clip, out string file)
        {
            clip = null;
            file = null;
            if (Scene.Length > 0)
            {
                var entry = sceneClips.Find(c => c != null && c.clip != null && c.scene == Scene);
                if (entry != null) clip = entry.clip;
            }
            // The Hive's fallback_clip names the clip for the scene it reported; use it if that is still our scene.
            bool hiveClipIsOurs = FallbackClip.Length > 0 && (_fallbackClipScene.Length == 0 || _fallbackClipScene == Scene);
            if (clip == null && hiveClipIsOurs)
            {
                string name = Path.GetFileNameWithoutExtension(FallbackClip);
                var entry = sceneClips.Find(c => c != null && c.clip != null && (c.clip.name == name || c.scene == name));
                clip = entry != null ? entry.clip : stageClips.Find(c => c != null && c.name == name);
                if (clip == null)
                    file = stageClipFiles.Find(f => !string.IsNullOrEmpty(f) && !_failedFiles.Contains(f) &&
                                                    Path.GetFileNameWithoutExtension(f) == name);
            }
            if (clip == null && file == null && Scene.Length > 0 && streamingAssetsByScene)
            {
                // orbis_scenes.json may name a clip other than <scene>.mp4 (e.g. a *_pingpong.mp4 loop); a plain
                // file name only, since it comes off the network.
                if (hiveClipIsOurs && FallbackClip.IndexOfAny(new[] { '/', '\\' }) < 0 && !FallbackClip.Contains("..") &&
                    Path.GetFileName(FallbackClip) != Scene + ".mp4")
                {
                    string f = InClipFolder(FallbackClip);
                    if (!_failedFiles.Contains(f)) file = f;
                }
                if (file == null)
                {
                    string f = InClipFolder(Scene + ".mp4");
                    if (!_failedFiles.Contains(f)) file = f;
                }
            }
            if (clip == null && file == null)
            {
                if (stageClips.Count > 0) clip = stageClips[Mathf.Clamp(Stage, 0, stageClips.Count - 1)];
                if (clip == null && stageClipFiles.Count > 0) file = stageClipFiles[Mathf.Clamp(Stage, 0, stageClipFiles.Count - 1)];
                if (file != null && _failedFiles.Contains(file)) file = null;
            }
        }

        string InClipFolder(string fileName) =>
            string.IsNullOrEmpty(sceneClipFolder) ? fileName : sceneClipFolder.TrimEnd('/') + "/" + fileName;

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

        // A missing/broken StreamingAssets file: remember it and fall through to the next candidate (next Update).
        void HandleVideoError(VideoPlayer vp, string message)
        {
            if (_playingClipKey.Length == 0) return; // VideoPlayer repeats the error for a clip we already dropped
            Debug.LogWarning($"[Beeswax] Orbis fallback clip {_playingClipKey}: {message}");
            if (_playingClipKey.StartsWith("file:", StringComparison.Ordinal)) _failedFiles.Add(_playingClipKey.Substring(5));
            _playingClipKey = "";
            _clipFailed = true;
        }

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
