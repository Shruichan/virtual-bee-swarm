using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.Rendering;
using UnityEngine.Video;
using VirtualBeeSwarm.Stakes;

namespace VirtualBeeSwarm.Capture
{
    /// <summary>
    /// Footage capture, only in the scene CaptureBuild generates (never in the game or headset scene). Plays the
    /// autopilot show at a fixed frame rate (Time.captureFramerate, so not real time) and writes PNG frames from a POV
    /// camera (standing eye height, the player's head for the game) and a wide 3/4 camera, inside a stand-in room
    /// (floor, table, wall, warm light) that also takes the stakes dim. Voice lines are held for their clip length in
    /// game time (the player has no audio) and logged to lines.tsv; every log line goes to events.tsv with its frame.
    /// Env: VBS_CAPTURE_OUT (output folder), VBS_CAPTURE_VIEWS ("pov,wide"), VBS_CAPTURE_MAX_SECONDS (stop early),
    /// VBS_CAPTURE_EVERY (write every Nth frame only: stills; the clock still runs at fps);
    /// while a file <out>/hold exists the capture waits (an encoder working through the frames).
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public class ShowCapture : MonoBehaviour, IStakesDimOutput
    {
        [SerializeField] Material litTemplate;
        [SerializeField] int fps = 30;
        [SerializeField] Vector2Int size = new Vector2Int(1920, 1080);
        [SerializeField] int supersample = 2;
        [SerializeField] float leadInSeconds = 2f;
        [SerializeField] Vector3 povPos = new Vector3(0f, 1.6f, -0.7f);
        [SerializeField] Vector3 povLook = new Vector3(0f, 1.02f, 0.6f);
        [SerializeField] float povFov = 56f;
        [SerializeField] float lookUpDegrees = 30f; // v3.3: the band and the pour sit ~25-40 deg up
        [SerializeField] Vector3 widePos = new Vector3(1.5f, 1.75f, -1.95f);
        [SerializeField] Vector3 wideLook = new Vector3(-0.15f, 2.0f, 0.8f);
        [SerializeField] float wideFov = 60f;
        [SerializeField] int maxSeconds = 240;
        [SerializeField] int maxEncodesInFlight = 24;

        GameDirector _director;
        GameNet _net;
        GameAutopilot _autopilot;
        IntroSequence _intro;
        Component _voice;
        FieldInfo _fCurrent, _fLineHasAudio, _lStarted, _lStartedAt, _lClip, _lCaptionSecs, _lLineId;
        object _curLine;
        float _curStart, _curLen;
        string _curId = "";

        readonly List<Camera> _cams = new List<Camera>();
        readonly List<string> _views = new List<string>();
        readonly List<RenderTexture> _ss = new List<RenderTexture>(), _out = new List<RenderTexture>();
        Camera _pov;
        string _dir;
        int _frame, _every = 1;
        bool _capturing;
        int _readbacks, _encodes;
        volatile string _writeError;
        StreamWriter _events, _lines;
        readonly ConcurrentBag<byte[]> _pool = new ConcurrentBag<byte[]>();
        readonly List<Vector2> _lookUps = new List<Vector2>(); // (start, end) game seconds
        readonly List<(Material m, Color c)> _roomMats = new List<(Material, Color)>();
        VideoPlayer[] _videos = new VideoPlayer[0];
        float _nextVideoScan;

        void Awake()
        {
            Time.captureFramerate = fps;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;
            AudioListener.volume = 0f;
            // meadow: the floor is culled to Camera.main's view; with several views per frame draw it all
            Bloom.MeadowField.CullToView = (Environment.GetEnvironmentVariable("VBS_CAPTURE_VIEWS") ?? "pov").Trim() == "pov";
            _dir = Environment.GetEnvironmentVariable("VBS_CAPTURE_OUT");
            if (string.IsNullOrEmpty(_dir)) _dir = Path.Combine(Application.persistentDataPath, "capture");
            Directory.CreateDirectory(_dir);
            _events = new StreamWriter(Path.Combine(_dir, "events.tsv")) { AutoFlush = true };
            _lines = new StreamWriter(Path.Combine(_dir, "lines.tsv")) { AutoFlush = true };
            _lines.WriteLine("kind\tframe\tseconds\tid\tclip_seconds\tcaption");
            Application.logMessageReceived += OnLog;
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            SetEndOfFrameHook(false);
            if (_net != null) _net.VoiceLineStarted -= OnVoiceLine;
            if (ReferenceEquals(PassthroughDim.Output, this)) PassthroughDim.Output = null;
        }

        void OnLog(string msg, string stack, LogType type) =>
            _events?.WriteLine(_frame + "\t" + (type == LogType.Log ? "" : type + ": ") + msg.Replace('\n', ' ').Replace('\t', ' '));

        IEnumerator Start()
        {
            _director = FindAnyObjectByType<GameDirector>();
            _net = FindAnyObjectByType<GameNet>();
            _autopilot = FindAnyObjectByType<GameAutopilot>();
            _intro = FindAnyObjectByType<IntroSequence>();
            BindVoice();
            if (_net != null) _net.VoiceLineStarted += OnVoiceLine;
            BuildRoom();
            PassthroughDim.Output = this;
            SetUpCameras();
            SetEndOfFrameHook(true);
            _capturing = true;
            Debug.Log("[VBSCapture] capturing " + string.Join(",", _views) + " at " + size.x + "x" + size.y + " (x" + supersample +
                      " supersampled), " + fps + " fps -> " + _dir);

            float t0 = Time.time;
            while ((_intro == null || !_intro.SignHereReady) && Time.time - t0 < 12f) yield return null;
            float lead = Time.time + leadInSeconds;
            while (Time.time < lead) yield return null;
            Debug.Log("[VBSCapture] START frame " + _frame);
            if (_autopilot != null) _autopilot.Begin();

            bool left = false;
            while (_frame < maxSeconds * fps && _writeError == null)
            {
                if (_director != null && _director.Beat != GameBeat.Pre) left = true;
                if (left && _director != null && _director.Beat == GameBeat.Pre) break; // the autopilot's reset at the end
                if (_autopilot == null || (left && !_autopilot.Running)) break;
                yield return null;
            }
            _capturing = false;
            Debug.Log("[VBSCapture] END frame " + _frame + (_writeError != null ? " write error: " + _writeError : ""));
            while (Volatile.Read(ref _readbacks) > 0 || Volatile.Read(ref _encodes) > 0) yield return null;
            while (_autopilot != null && _autopilot.Running) yield return null;
            bool ok = _writeError == null && _autopilot != null && _autopilot.Passed;
            Debug.Log("[VBSCapture] " + (ok ? "DONE" : "FAILED") + ": " + _frame + " frames");
            _events.Flush();
            _lines.Flush();
            Application.Quit(ok ? 0 : 1);
        }

        // ------------------------------------------------------------ voice: hold each line for its clip, in game time
        void BindVoice()
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var vt = Type.GetType("Beeswax.BeeswaxRoomVoice, Beeswax.Client");
            _voice = vt != null ? FindAnyObjectByType(vt) as Component : null;
            _fLineHasAudio = typeof(GameNet).GetField("_lineHasAudio", F);
            if (_voice == null) { Debug.LogWarning("[VBSCapture] no BeeswaxRoomVoice: captions only"); return; }
            _fCurrent = vt.GetField("_current", F);
            vt.GetField("maxQueueWaitSeconds", F)?.SetValue(_voice, 1e6f); // queue waits are real time
            var lt = vt.GetNestedType("Line", BindingFlags.NonPublic);
            if (lt != null)
            {
                _lStarted = lt.GetField("started", F);
                _lStartedAt = lt.GetField("startedAt", F);
                _lClip = lt.GetField("clip", F);
                _lCaptionSecs = lt.GetField("captionOnlySeconds", F);
                _lLineId = lt.GetField("lineId", F);
            }
            if (_fCurrent == null || _lStartedAt == null || _lClip == null)
            {
                Debug.LogWarning("[VBSCapture] BeeswaxRoomVoice layout changed: lines are not held");
                _fCurrent = null;
            }
        }

        void OnVoiceLine(GameNet.VoiceLineInfo l) =>
            _lines.WriteLine("start\t" + _frame + "\t" + F3(_frame / (float)fps) + "\t" + l.id + "\t" + F3(l.clipSeconds) + "\t" + l.caption);

        void HoldVoice()
        {
            if (_voice == null || _fCurrent == null) return;
            object cur = _fCurrent.GetValue(_voice);
            if (!ReferenceEquals(cur, _curLine))
            {
                if (_curLine != null) _lines.WriteLine("end\t" + _frame + "\t" + F3(_frame / (float)fps) + "\t" + _curId + "\t\t");
                _curLine = cur;
                _curStart = Time.time;
                _curId = "";
                _curLen = 0f;
                if (cur != null)
                {
                    var clip = _lClip.GetValue(cur) as AudioClip;
                    _curLen = clip != null ? clip.length : (_lCaptionSecs != null ? (float)_lCaptionSecs.GetValue(cur) : 2f);
                    _curId = _lLineId != null ? _lLineId.GetValue(cur) as string ?? "" : "";
                    if (_curId == "room_hook_1") LookUp(1.2f, 4.5f); // the pour (the wide shot covers room_strike_lookup)
                }
            }
            if (cur != null && (_lStarted == null || (bool)_lStarted.GetValue(cur)))
            {
                bool hold = Time.time - _curStart < _curLen - 0.5f / fps;
                _lStartedAt.SetValue(cur, Time.realtimeSinceStartup + (hold ? 1000f : -10f));
            }
            if (_net != null) _fLineHasAudio?.SetValue(_net, false); // GameNet's own mouth flap (no audio here)
        }

        void LookUp(float after, float hold) => _lookUps.Add(new Vector2(Time.time + after, Time.time + after + hold));

        // ------------------------------------------------------------ cameras + frames
        void SetUpCameras()
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("VBS_CAPTURE_MAX_SECONDS"), out int maxS) && maxS > 0) maxSeconds = maxS;
            if (int.TryParse(Environment.GetEnvironmentVariable("VBS_CAPTURE_EVERY"), out int every) && every > 1) _every = every;
            var env = Environment.GetEnvironmentVariable("VBS_CAPTURE_VIEWS");
            var want = string.IsNullOrEmpty(env) ? new[] { "pov", "wide" } : env.Split(',');
            var desk = FindAnyObjectByType<DesktopTestCamera>();
            if (desk != null)
            {
                _pov = desk.GetComponent<Camera>();
                desk.enabled = false; // no mouse/keys; this script poses the head
            }
            if (_pov == null) _pov = Camera.main;
            _pov.transform.position = povPos;
            _pov.fieldOfView = povFov;
            _pov.nearClipPlane = 0.02f;
            foreach (var raw in want)
            {
                string v = raw.Trim();
                Camera cam;
                if (v == "pov") cam = _pov;
                else if (v == "wide")
                {
                    cam = new GameObject("WideCamera").AddComponent<Camera>();
                    cam.CopyFrom(_pov);
                    cam.tag = "Untagged";
                    cam.transform.SetPositionAndRotation(widePos, Quaternion.LookRotation(wideLook - widePos, Vector3.up));
                    cam.fieldOfView = wideFov;
                }
                else if (v == "pal") // [Pals] up close on Beeswax and the ghost hand (Pals/PalCam)
                {
                    cam = new GameObject("PalCamera").AddComponent<Camera>();
                    cam.CopyFrom(_pov);
                    cam.tag = "Untagged";
                    cam.fieldOfView = 50f;
                    cam.gameObject.AddComponent<VirtualBeeSwarm.Pals.PalCam>();
                }
                else if (v == "top" || v == "corner") // meadow: straight down over the room / a high 3/4 view of the floor
                {
                    cam = new GameObject(v == "top" ? "TopCamera" : "CornerCamera").AddComponent<Camera>();
                    cam.CopyFrom(_pov);
                    cam.tag = "Untagged";
                    if (v == "top") cam.transform.SetPositionAndRotation(new Vector3(0f, 2.3f, 0.1f), Quaternion.Euler(90f, 0f, 0f));
                    else
                    {
                        var cp = new Vector3(2.6f, 2.25f, -2.4f);
                        cam.transform.SetPositionAndRotation(cp, Quaternion.LookRotation(new Vector3(-0.2f, 0.5f, 0.6f) - cp, Vector3.up));
                    }
                    cam.fieldOfView = v == "top" ? 100f : 62f;
                }
                else continue;
                var ss = NewRt(size.x * supersample, size.y * supersample, 24);
                cam.targetTexture = ss;
                cam.enabled = cam == _pov; // rendered by EndOfFrame; the POV stays enabled: it is Camera.main (the player's head)
                _cams.Add(cam);
                _views.Add(v);
                _ss.Add(ss);
                _out.Add(NewRt(size.x, size.y, 0));
                Directory.CreateDirectory(Path.Combine(_dir, v));
            }
        }

        static RenderTexture NewRt(int w, int h, int depth)
        {
            var rt = new RenderTexture(new RenderTextureDescriptor(w, h, GraphicsFormat.R8G8B8A8_SRGB, depth) { msaaSamples = 1 });
            rt.filterMode = FilterMode.Bilinear;
            rt.Create();
            return rt;
        }

        void LateUpdate()
        {
            HoldVoice();
            PoseHead();
            if (Time.time >= _nextVideoScan)
            {
                _nextVideoScan = Time.time + 1f;
                _videos = FindObjectsByType<VideoPlayer>(FindObjectsSortMode.None);
            }
            foreach (var vp in _videos)
                if (vp != null && vp.timeReference != VideoTimeReference.InternalTime)
                {
                    vp.timeReference = VideoTimeReference.InternalTime; // window clips follow the capture clock
                    vp.skipOnDrop = false;
                }
        }

        void PoseHead()
        {
            if (_pov == null) return;
            float t = Time.time, up = 0f;
            foreach (var w in _lookUps)
            {
                float a = Mathf.SmoothStep(0f, 1f, (t - w.x) / 1.2f) * Mathf.SmoothStep(0f, 1f, (w.y + 1.5f - t) / 1.5f);
                up = Mathf.Max(up, a);
            }
            var look = Quaternion.LookRotation(povLook - povPos, Vector3.up);
            float swayX = Mathf.Sin(t * 0.37f) * 0.5f + Mathf.Sin(t * 0.91f + 1.3f) * 0.25f;
            float swayY = Mathf.Sin(t * 0.29f + 0.7f) * 0.7f + Mathf.Sin(t * 0.73f) * 0.3f;
            _pov.transform.SetPositionAndRotation(
                povPos + new Vector3(Mathf.Sin(t * 0.31f) * 0.01f, Mathf.Sin(t * 0.47f + 2f) * 0.006f, 0f),
                Quaternion.Euler(-lookUpDegrees * up, 0f, 0f) * look * Quaternion.Euler(swayX, swayY, 0f));
        }

        // A batchmode player renders no cameras by itself: render them where the player would (end of PostLateUpdate).
        void SetEndOfFrameHook(bool on)
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != typeof(PostLateUpdate)) continue;
                var subs = new List<PlayerLoopSystem>(loop.subSystemList[i].subSystemList);
                subs.RemoveAll(s => s.type == typeof(ShowCapture));
                // Before FinishFrameRendering, which drops this frame's Graphics.DrawMesh* bees (DarkSwarm, BeeDelivery).
                int at = subs.FindIndex(s => s.type == typeof(PostLateUpdate.FinishFrameRendering));
                if (on) subs.Insert(at >= 0 ? at : subs.Count, new PlayerLoopSystem { type = typeof(ShowCapture), updateDelegate = EndOfFrame });
                loop.subSystemList[i].subSystemList = subs.ToArray();
            }
            PlayerLoop.SetPlayerLoop(loop);
        }

        void EndOfFrame()
        {
            if (!_capturing || _cams.Count == 0 || this == null) return;
            int frame = _frame++;
            if (frame % _every != 0) return;
            for (int i = 0; i < _cams.Count; i++)
            {
                _cams[i].Render();
                Graphics.Blit(_ss[i], _out[i]); // 2x2 box downsample (bilinear at the texel corners)
                string path = Path.Combine(_dir, _views[i], "f_" + frame.ToString("000000") + ".png");
                Interlocked.Increment(ref _readbacks);
                AsyncGPUReadback.Request(_out[i], 0, TextureFormat.RGBA32, r => OnReadback(r, path));
            }
            if (Volatile.Read(ref _encodes) > maxEncodesInFlight)
                while (Volatile.Read(ref _encodes) > maxEncodesInFlight / 2) Thread.Sleep(2); // capture clock: no hurry
            // An outside encoder that turns frames into video as they come can pause us (disk space): <out>/hold.
            if ((frame & 15) == 0) while (File.Exists(Path.Combine(_dir, "hold"))) Thread.Sleep(100);
        }

        void OnReadback(AsyncGPUReadbackRequest r, string path)
        {
            Interlocked.Decrement(ref _readbacks);
            if (r.hasError) { _writeError = "readback failed " + path; return; }
            var data = r.GetData<byte>();
            if (!_pool.TryTake(out var rgba) || rgba.Length != data.Length) rgba = new byte[data.Length];
            data.CopyTo(rgba);
            int w = r.width, h = r.height;
            Interlocked.Increment(ref _encodes);
            ThreadPool.QueueUserWorkItem(_ => Encode(rgba, w, h, path));
        }

        void Encode(byte[] rgba, int w, int h, string path)
        {
            try
            {
                var rgb = new byte[w * h * 3];
                for (int i = 0, j = 0; i < rgb.Length; i += 3, j += 4)
                {
                    rgb[i] = rgba[j];
                    rgb[i + 1] = rgba[j + 1];
                    rgb[i + 2] = rgba[j + 2];
                }
                _pool.Add(rgba);
                var png = ImageConversion.EncodeArrayToPNG(rgb, GraphicsFormat.R8G8B8_SRGB, (uint)w, (uint)h);
                File.WriteAllBytes(path, png);
            }
            catch (Exception e) { _writeError = e.Message; }
            finally { Interlocked.Decrement(ref _encodes); }
        }

        // ------------------------------------------------------------ stand-in room (capture only)
        public void ApplyDim(float amount)
        {
            float k = 1f - Mathf.Clamp01(amount);
            foreach (var (m, c) in _roomMats) m.SetColor("_BaseColor", new Color(c.r * k, c.g * k, c.b * k, 1f));
        }

        void BuildRoom()
        {
            if (litTemplate == null) { Debug.LogWarning("[VBSCapture] no lit material: no stand-in room"); return; }
            var fakeTable = GameObject.Find("FakeTable");
            foreach (var n in new[] { "FakeTable", "FakeFloor" })
            {
                var go = GameObject.Find(n);
                if (go != null) foreach (var r in go.GetComponentsInChildren<Renderer>()) r.enabled = false;
            }
            Vector3 tc = new Vector3(0f, 0.75f, 0.1f);
            Vector2 th = new Vector2(0.7f, 0.4f);
            if (fakeTable != null)
            {
                var s = fakeTable.transform.lossyScale;
                tc = fakeTable.transform.position + new Vector3(0f, s.y * 0.5f, 0f);
                th = new Vector2(s.x * 0.5f, s.z * 0.5f);
            }
            float wallZ = tc.z + th.y + 1.5f;
            var rig = AR.ArRig.Instance;
            if (rig != null && rig.Room.HasWall) wallZ = rig.Room.WindowWall.point.z;

            var root = new GameObject("CaptureRoom").transform;
            var wood = Mat(new Color(0.78f, 0.6f, 0.42f), WoodTexture(false), 0.35f, new Vector2(1f, 1f));
            var floor = Mat(new Color(0.72f, 0.58f, 0.45f), WoodTexture(true), 0.3f, new Vector2(3f, 3f));
            var wall = Mat(new Color(0.96f, 0.93f, 0.87f), NoiseTexture(), 0.05f, new Vector2(3f, 2f));
            var trim = Mat(new Color(0.97f, 0.95f, 0.9f), null, 0.2f, Vector2.one);
            var rug = Mat(new Color(0.42f, 0.5f, 0.52f), NoiseTexture(), 0f, new Vector2(4f, 4f));
            // The ceiling the game believes in (StakesDirector hands it to the dark swarm's band), so nothing real or
            // stand-in hides the band.
            const float W = 6f, back = 6f;
            float H = rig != null ? Mathf.Max(2.3f, rig.Room.CeilingOrDefault) : 2.4f;
            float midZ = wallZ - back * 0.5f;
            Box(root, "Floor", new Vector3(0f, -0.01f, midZ), new Vector3(W, 0.02f, back), floor);
            Box(root, "Ceiling", new Vector3(0f, H + 0.01f, midZ), new Vector3(W, 0.02f, back), trim);
            Box(root, "BackWall", new Vector3(0f, H * 0.5f, wallZ + 0.03f), new Vector3(W, H, 0.06f), wall);
            Box(root, "LeftWall", new Vector3(-W * 0.5f, H * 0.5f, midZ), new Vector3(0.06f, H, back), wall);
            Box(root, "RightWall", new Vector3(W * 0.5f, H * 0.5f, midZ), new Vector3(0.06f, H, back), wall);
            Box(root, "Baseboard", new Vector3(0f, 0.05f, wallZ - 0.01f), new Vector3(W, 0.1f, 0.02f), trim);
            bool meadow = Bloom.MeadowLayout.FreeStanding; // meadow: the hive stands on its stump, no stand-in table or rug
            if (!meadow) Box(root, "Rug", new Vector3(tc.x, 0.003f, tc.z - 0.2f), new Vector3(2.4f, 0.006f, 1.9f), rug);
            if (!meadow) Box(root, "TableTop", new Vector3(tc.x, tc.y - 0.02f, tc.z), new Vector3(th.x * 2f, 0.04f, th.y * 2f), wood);
            if (!meadow) Box(root, "Apron", new Vector3(tc.x, tc.y - 0.08f, tc.z), new Vector3(th.x * 2f - 0.1f, 0.08f, th.y * 2f - 0.1f), wood);
            for (int i = 0; i < (meadow ? 0 : 4); i++)
            {
                float lx = (i % 2 == 0 ? -1f : 1f) * (th.x - 0.07f), lz = (i < 2 ? -1f : 1f) * (th.y - 0.07f);
                Box(root, "Leg" + i, new Vector3(tc.x + lx, (tc.y - 0.04f) * 0.5f, tc.z + lz), new Vector3(0.055f, tc.y - 0.04f, 0.055f), wood);
            }

            var key = new GameObject("CaptureKeyLight").AddComponent<Light>();
            key.transform.SetParent(root, false);
            key.type = LightType.Directional;
            key.transform.rotation = Quaternion.Euler(42f, 28f, 0f); // over the left shoulder, onto the wall
            key.color = new Color(1f, 0.87f, 0.72f);
            key.intensity = 1.05f;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.45f;
            var fill = new GameObject("CaptureLamp").AddComponent<Light>();
            fill.transform.SetParent(root, false);
            fill.transform.position = new Vector3(-1.1f, 1.9f, 0.4f);
            fill.type = LightType.Point;
            fill.range = 6f;
            fill.color = new Color(1f, 0.8f, 0.6f);
            fill.intensity = 0.9f;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.6f, 0.56f, 0.51f);
            var sh = new SphericalHarmonicsL2();
            sh.AddAmbientLight(RenderSettings.ambientLight);
            RenderSettings.ambientProbe = sh;
            Debug.Log("[VBSCapture] stand-in room: table " + tc + " half " + th + ", wall z " + wallZ.ToString("0.00", CultureInfo.InvariantCulture));
        }

        Material Mat(Color c, Texture2D tex, float smooth, Vector2 tiling)
        {
            var m = new Material(litTemplate);
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            if (tex != null)
            {
                m.SetTexture("_BaseMap", tex);
                m.SetTextureScale("_BaseMap", tiling);
            }
            _roomMats.Add((m, c));
            return m;
        }

        static void Box(Transform root, string name, Vector3 pos, Vector3 scale, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>()); // no colliders: the game's raycasts must not see the stand-in room
            go.transform.SetParent(root, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = name.StartsWith("Leg") || name.StartsWith("Table") || name == "Apron" ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        static Texture2D WoodTexture(bool planks)
        {
            const int N = 512;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 8 };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int plank = planks ? y / 64 : 0;
                    float off = plank * 37.3f;
                    float g = Mathf.PerlinNoise(x * 0.004f + off, y * 0.06f + off) * 9f;
                    g = g - Mathf.Floor(g);
                    float fine = Mathf.PerlinNoise(x * 0.05f + off, y * 0.9f);
                    float v = 0.78f + 0.14f * g + 0.08f * fine + (planks ? (Mathf.PerlinNoise(plank * 3.1f, 0.5f) - 0.5f) * 0.2f : 0f);
                    if (planks && (y % 64 < 2 || (x + plank * 173) % 384 < 2)) v *= 0.55f;
                    byte b = (byte)Mathf.Clamp(v * 255f, 0f, 255f);
                    px[y * N + x] = new Color32(b, b, b, 255);
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        static Texture2D NoiseTexture()
        {
            const int N = 256;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float v = 0.93f + 0.07f * Mathf.PerlinNoise(x * 0.08f, y * 0.08f) - 0.03f * Mathf.PerlinNoise(x * 0.5f, y * 0.5f);
                    byte b = (byte)Mathf.Clamp(v * 255f, 0f, 255f);
                    px[y * N + x] = new Color32(b, b, b, 255);
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        static string F3(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);
    }
}
