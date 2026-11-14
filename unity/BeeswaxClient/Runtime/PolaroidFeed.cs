using System;
using System.Diagnostics;
using UnityEngine;
#if BEESWAX_UGUI
using UnityEngine.UI;
#endif
using Debug = UnityEngine.Debug;

namespace Beeswax
{
    /// <summary>
    /// Turns scan.photo JPEGs from the bus into one reused Texture2D and shows it on a Renderer and/or RawImage.
    /// Your polaroid script listens to OnScanStarted (blank polaroid + flash) and OnPhoto (start developing).
    /// </summary>
    public class PolaroidFeed : MonoBehaviour
    {
        [SerializeField] BeeswaxBus bus;
        [Tooltip("Optional: gets the photo via a MaterialPropertyBlock.")]
        [SerializeField] Renderer targetRenderer;
        [Tooltip("URP Lit/Unlit: _BaseMap. Built-in: _MainTex (used automatically if the first is missing).")]
        [SerializeField] string textureProperty = "_BaseMap";
#if BEESWAX_UGUI
        [SerializeField] RawImage targetRawImage;
#endif
        [Tooltip("Clear back to blank when a new scan starts.")]
        [SerializeField] bool blankOnScanStarted = true;
        [SerializeField] Color blankColor = new Color(0.08f, 0.08f, 0.08f, 1f);

        public event Action<ScanStarted> OnScanStarted;
        /// <summary>(texture, meta). The texture object is reused for every photo.</summary>
        public event Action<Texture2D, ScanPhoto> OnPhoto;

        public Texture2D Texture => _tex;
        public string LastScanId { get; private set; } = "";
        public float LastDecodeMs { get; private set; }
        public float Aspect => _tex != null && _tex.height > 0 ? (float)_tex.width / _tex.height : 1f;

        Texture2D _tex;
        byte[] _lastGood;
        MaterialPropertyBlock _mpb;
        BeeswaxBus _bound;

        void Awake()
        {
            _tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "BeeswaxPolaroid", wrapMode = TextureWrapMode.Clamp };
            FillBlank();
        }

        void OnEnable() => Bind();
        void Start() => Bind();
        void OnDisable() => Unbind();
        void OnDestroy() { if (_tex != null) Destroy(_tex); }

        void Bind()
        {
            var b = bus != null ? bus : BeeswaxBus.Instance;
            if (b == null || b == _bound) return;
            Unbind();
            _bound = b;
            b.OnScanStarted += HandleScanStarted;
            b.OnScanPhoto += HandlePhoto;
        }

        void Unbind()
        {
            if (_bound == null) return;
            _bound.OnScanStarted -= HandleScanStarted;
            _bound.OnScanPhoto -= HandlePhoto;
            _bound = null;
        }

        void HandleScanStarted(ScanStarted s)
        {
            LastScanId = s.scan_id;
            if (blankOnScanStarted) Clear();
            Raise(() => OnScanStarted?.Invoke(s));
        }

        void HandlePhoto(ScanPhoto meta, byte[] jpeg) => Show(jpeg, meta);

        /// <summary>Decode a JPEG/PNG into the shared texture and show it. Returns false (keeping the old image) on bad data.</summary>
        public bool Show(byte[] jpeg, ScanPhoto meta = null)
        {
            if (jpeg == null || jpeg.Length < 4) return false;
            var sw = Stopwatch.StartNew();
            bool ok;
            try { ok = _tex.LoadImage(jpeg, false); }
            catch (Exception e) { Debug.LogWarning("[Beeswax] photo decode threw: " + e.Message); ok = false; }
            LastDecodeMs = (float)sw.Elapsed.TotalMilliseconds;
            if (!ok)
            {
                // A failed LoadImage still replaces the texture with Unity's 8x8 "?" image: put the last photo back.
                Debug.LogWarning($"[Beeswax] could not decode a {jpeg.Length}-byte photo.");
                if (_lastGood != null) _tex.LoadImage(_lastGood, false);
                else FillBlank();
                return false;
            }
            _lastGood = jpeg;
            meta = meta ?? new ScanPhoto { scan_id = LastScanId };
            if (meta.scan_id.Length > 0) LastScanId = meta.scan_id;
            Assign();
            Raise(() => OnPhoto?.Invoke(_tex, meta));
            return true;
        }

        /// <summary>Back to a blank (undeveloped) polaroid.</summary>
        public void Clear()
        {
            _lastGood = null;
            FillBlank();
            Assign();
        }

        void FillBlank()
        {
            _tex.Reinitialize(2, 2);
            _tex.SetPixels(new[] { blankColor, blankColor, blankColor, blankColor });
            _tex.Apply(false);
        }

        void Assign()
        {
            if (targetRenderer != null)
            {
                _mpb = _mpb ?? new MaterialPropertyBlock();
                var mat = targetRenderer.sharedMaterial;
                string prop = mat != null && !mat.HasProperty(textureProperty) && mat.HasProperty("_MainTex") ? "_MainTex" : textureProperty;
                targetRenderer.GetPropertyBlock(_mpb);
                _mpb.SetTexture(prop, _tex);
                targetRenderer.SetPropertyBlock(_mpb);
            }
#if BEESWAX_UGUI
            if (targetRawImage != null) targetRawImage.texture = _tex;
#endif
        }

        static void Raise(Action a)
        {
            try { a(); } catch (Exception e) { Debug.LogException(e); }
        }
    }
}
