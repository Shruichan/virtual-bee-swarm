using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Video;

namespace VirtualBeeSwarm.Title
{
    /// <summary>
    /// The documentary wall inside the title globe (tuning title.wall, default on; never in comfort mode): an open
    /// cylinder round the player with rows of small bee monitors, each row sliding sideways at its own speed, like the
    /// end of a nature doc showreel. Two mosaic videos feed every screen (StreamingAssets/wall_strip_0/1.mp4: 16 x 4
    /// Orbis wall clips of 240x135 each, a 6 s loop); the shader (Resources/VBS_TitleWall) scrolls, frames and collapses
    /// them, so the wall costs two decoders. Prepared while the title shows, posters (Resources/wall_poster_N) until the
    /// first frame; on Linux players VBS_GLOBE_CLIPS/wall_strip_N.webm (VP8) stands in. Owned by <see cref="TitleGlobe"/>.
    /// </summary>
    public class TitleWall
    {
        public const int Strips = 2;
        const int Around = 32;       // tiles round the cylinder: twice a 16-tile strip row
        const float Bezel = 0.045f;  // the shader's bezel, in slot widths

        readonly Transform _root;
        readonly Material _mat;
        readonly Mesh _mesh;
        readonly VideoPlayer[] _vp = new VideoPlayer[Strips];
        readonly RenderTexture[] _rt = new RenderTexture[Strips];
        readonly Texture[] _poster = new Texture[Strips];
        readonly bool[] _frame = new bool[Strips], _bad = new bool[Strips], _ready = new bool[Strips];
        readonly string[] _url = new string[Strips];
        readonly Action<string> _log;
        float _clock;
        bool _shown;

        static readonly int[] StripId = { Shader.PropertyToID("_StripA"), Shader.PropertyToID("_StripB") };
        static readonly int AlphaId = Shader.PropertyToID("_Alpha");
        static readonly int RevealId = Shader.PropertyToID("_Reveal");
        static readonly int CollapseId = Shader.PropertyToID("_Collapse");
        static readonly int ClockId = Shader.PropertyToID("_WallTime");

        public bool Usable => _mat != null;

        /// <summary>Decoders it holds right now (prepared or playing).</summary>
        public int Decoders
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Strips; i++) if (_vp[i] != null && !_bad[i] && (_vp[i].isPrepared || _vp[i].isPlaying)) n++;
                return n;
            }
        }

        public TitleWall(Transform parent, float radius, int rows, Action<string> log)
        {
            _log = log;
            var shader = Resources.Load<Shader>("VBS_TitleWall");
            if (shader == null) shader = Shader.Find("VirtualBeeSwarm/TitleWall");
            if (shader == null || !shader.isSupported)
            {
                Debug.LogWarning("[VBS][Globe] no TitleWall shader: the dive keeps the plain globe");
                return;
            }
            float slotW = 2f * Mathf.PI * radius / Around;
            float pitch = slotW * (2f * Bezel + (1f - 2f * Bezel) * 9f / 16f); // a 16:9 screen inside a bezel of even width
            _mat = new Material(shader) { name = "VBS_TitleWall", renderQueue = 2989 }; // before the inside-out globe (2990)
            _mat.SetFloat("_Rows", rows);
            _mat.SetFloat("_Around", Around);
            _mat.SetFloat("_SlotAspect", slotW / pitch);
            _mesh = Cylinder(radius, rows, pitch, 128);

            var go = new GameObject("TitleWall");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _root = go.transform;
            go.SetActive(false);

            FindStrips();
            for (int i = 0; i < Strips; i++)
            {
                _poster[i] = Resources.Load<Texture2D>("wall_poster_" + i);
                _mat.SetTexture(StripId[i], _poster[i] != null ? _poster[i] : Texture2D.blackTexture);
                if (_url[i] == null) continue;
                int k = i;
                var vgo = new GameObject("WallStrip" + i);
                vgo.transform.SetParent(parent, false);
                var vp = vgo.AddComponent<VideoPlayer>();
                vp.playOnAwake = false;
                vp.waitForFirstFrame = true;
                vp.skipOnDrop = true;
                vp.isLooping = true;
                vp.audioOutputMode = VideoAudioOutputMode.None;
                vp.renderMode = VideoRenderMode.RenderTexture;
                vp.source = VideoSource.Url;
                vp.prepareCompleted += p => _ready[k] = true;
                vp.errorReceived += (p, message) =>
                {
                    if (_bad[k]) return;
                    _bad[k] = true;
                    Debug.LogWarning("[VBS][Globe] " + Path.GetFileName(_url[k]) + ": video failed to play, the wall shows its poster");
                };
                _vp[i] = vp;
            }
        }

        void FindStrips()
        {
            string dir = null;
            try { dir = Environment.GetEnvironmentVariable("VBS_GLOBE_CLIPS"); } catch (Exception) { }
            string sa = Application.streamingAssetsPath;
            for (int i = 0; i < Strips; i++)
            {
                string n = "wall_strip_" + i;
                if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, n + ".webm"))) { _url[i] = Path.Combine(dir, n + ".webm"); continue; }
#if UNITY_ANDROID && !UNITY_EDITOR
                _url[i] = Path.Combine(sa, n + ".mp4"); // can't list StreamingAssets here: a missing one fails and keeps its poster
#else
                string p = Path.Combine(sa, n + ".mp4");
                if (File.Exists(p)) _url[i] = p;
#endif
            }
            Log("wall strips: " + string.Join(", ", Array.ConvertAll(_url, u => u == null ? "(poster only)" : Path.GetFileName(u))));
        }

        /// <summary>While the title shows: get both strips decoding-ready, so the wall opens on a live frame.</summary>
        public void Prepare()
        {
            for (int i = 0; i < Strips; i++)
            {
                var vp = _vp[i];
                if (vp == null || _bad[i]) continue;
                if (_rt[i] == null)
                {
                    _rt[i] = new RenderTexture(3840, 540, 0) { name = "VBS_WallStrip" + i, wrapMode = TextureWrapMode.Clamp };
                    _rt[i].Create();
                    vp.targetTexture = _rt[i];
                }
                vp.Stop();
                _ready[i] = _frame[i] = false;
                vp.url = _url[i];
                vp.Prepare();
                if (_mat != null) _mat.SetTexture(StripId[i], _poster[i] != null ? _poster[i] : Texture2D.blackTexture);
            }
        }

        /// <summary>START: the strips roll (the wall itself shows once the player is inside the globe).</summary>
        public void Play()
        {
            for (int i = 0; i < Strips; i++)
                if (_vp[i] != null && !_bad[i]) _vp[i].Play(); // plays once prepared if it isn't yet
            _clock = 0f;
        }

        /// <summary>Round the player: centred on the eyes, its front (and the rows' mid point) facing `rotation`.</summary>
        public void Show(Vector3 eye, Quaternion rotation)
        {
            if (_root == null) return;
            _root.SetPositionAndRotation(eye + Vector3.down * 0.05f, rotation);
            _root.gameObject.SetActive(true);
            if (!_shown) Log("wall up: " + Decoders + " strip decoder(s), " + Mathf.RoundToInt(_mat.GetFloat("_Rows")) * Around + " screens");
            _shown = true;
        }

        /// <summary>reveal 0..1 lights the rows from eye level out; collapse 0..1 folds them in and burns them away.</summary>
        public void Tick(float dt, float alpha, float reveal, float collapse, float speed)
        {
            if (_mat == null) return;
            for (int i = 0; i < Strips; i++)
            {
                var vp = _vp[i];
                if (vp == null || _frame[i] || !vp.isPlaying || vp.frame <= 0) continue;
                _frame[i] = true;
                _mat.SetTexture(StripId[i], _rt[i]);
            }
            _clock += dt * speed;
            _mat.SetFloat(ClockId, _clock);
            _mat.SetFloat(AlphaId, alpha);
            _mat.SetFloat(RevealId, reveal);
            _mat.SetFloat(CollapseId, collapse);
        }

        public void Stop()
        {
            if (_root != null) _root.gameObject.SetActive(false);
            _shown = false;
            for (int i = 0; i < Strips; i++)
            {
                if (_vp[i] != null) _vp[i].Stop();
                _ready[i] = _frame[i] = false;
            }
        }

        public void Destroy()
        {
            Stop();
            for (int i = 0; i < Strips; i++)
                if (_rt[i] != null) { _rt[i].Release(); UnityEngine.Object.Destroy(_rt[i]); }
            if (_mat != null) UnityEngine.Object.Destroy(_mat);
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
        }

        /// <summary>An open cylinder round the y axis: uv.x 0..1 round it (0.5 straight ahead, +z), uv.y 0..rows.</summary>
        static Mesh Cylinder(float radius, int rows, float pitch, int seg)
        {
            var verts = new Vector3[(seg + 1) * 2];
            var uvs = new Vector2[verts.Length];
            var tris = new int[seg * 6];
            float half = rows * pitch * 0.5f;
            for (int i = 0; i <= seg; i++)
            {
                float u = i / (float)seg;
                float a = (u - 0.5f) * Mathf.PI * 2f;
                var p = new Vector3(Mathf.Sin(a) * radius, 0f, Mathf.Cos(a) * radius);
                verts[i * 2] = p + Vector3.down * half;
                verts[i * 2 + 1] = p + Vector3.up * half;
                uvs[i * 2] = new Vector2(u, 0f);
                uvs[i * 2 + 1] = new Vector2(u, rows);
            }
            for (int i = 0, t = 0; i < seg; i++)
            {
                int a0 = i * 2, a1 = a0 + 2;
                tris[t++] = a0; tris[t++] = a0 + 1; tris[t++] = a1;
                tris[t++] = a1; tris[t++] = a0 + 1; tris[t++] = a1 + 1;
            }
            var m = new Mesh { name = "VBS_TitleWall", vertices = verts, uv = uvs, triangles = tris };
            m.RecalculateNormals();
            m.bounds = new Bounds(Vector3.zero, new Vector3(radius * 4f, half * 2f + 1f, radius * 4f)); // the collapse widens it
            return m;
        }

        void Log(string msg) => _log?.Invoke(msg);
    }
}
