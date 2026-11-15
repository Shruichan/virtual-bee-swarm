using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Dressing
{
    /// <summary>
    /// Grass tufts around the hive's foot, in ONE instanced draw: a base ring that is always there, plus two more
    /// that sprout (scale 0 -> 1.25 -> 1) for every fix, so the run's progress grows on the table. Matrices are
    /// preallocated and rebuilt in place (no per-frame allocation). Spots are in TableAnchor space.
    /// </summary>
    public sealed class DressingTufts
    {
        public const int BaseCount = 8, PerFix = 2, MaxFixes = 4, Max = BaseCount + PerFix * MaxFixes;
        const float SproutSeconds = 0.45f;

        readonly Mesh _mesh;
        readonly Material _mat;
        readonly Transform _anchor;
        readonly Matrix4x4[] _draw = new Matrix4x4[Max];
        readonly Vector3[] _pos = new Vector3[Max];
        readonly float[] _yaw = new float[Max], _size = new float[Max], _born = new float[Max];
        readonly float _meshScale;
        int _shown = BaseCount, _fixes;

        public string Source { get; }
        public int Count => _shown;

        /// <param name="foot">The hive's footprint in TableAnchor space (x/z extents; y ignored).</param>
        public DressingTufts(Mesh mesh, Material mat, Transform anchor, Bounds foot, float tuftHeight, string source)
        {
            _mesh = mesh;
            _mat = mat;
            _anchor = anchor;
            Source = source;
            float h = mesh.bounds.size.y;
            _meshScale = h > 1e-4f ? tuftHeight / h : 1f;
            var rnd = new System.Random(7);
            float x0 = foot.min.x, x1 = foot.max.x, z0 = foot.min.z, z1 = foot.max.z;
            const float o = 0.025f; // just outside the stand
            // base ring: the four corners and the middle of the sides and back (the front middle stays clear: door, arc)
            Vector3[] ring =
            {
                new Vector3(x0 - o, 0f, z0 - o * 0.6f), new Vector3(x1 + o, 0f, z0 - o * 0.6f),
                new Vector3(x0 - o, 0f, z1 + o), new Vector3(x1 + o, 0f, z1 + o),
                new Vector3(x0 - o * 1.2f, 0f, Mathf.Lerp(z0, z1, 0.45f)), new Vector3(x1 + o * 1.2f, 0f, Mathf.Lerp(z0, z1, 0.6f)),
                new Vector3(Mathf.Lerp(x0, x1, 0.3f), 0f, z1 + o * 1.3f), new Vector3(Mathf.Lerp(x0, x1, 0.75f), 0f, z1 + o),
            };
            // sprouts, in the order they appear: front corners first (where the player looks), then along the sides
            Vector3[] sprout =
            {
                new Vector3(x0 + 0.03f, 0f, z0 - o * 1.6f), new Vector3(x1 - 0.03f, 0f, z0 - o * 1.6f),
                new Vector3(x0 - o * 2.2f, 0f, Mathf.Lerp(z0, z1, 0.2f)), new Vector3(x1 + o * 2.2f, 0f, Mathf.Lerp(z0, z1, 0.25f)),
                new Vector3(x0 - o * 2f, 0f, Mathf.Lerp(z0, z1, 0.75f)), new Vector3(x1 + o * 2f, 0f, Mathf.Lerp(z0, z1, 0.85f)),
                new Vector3(Mathf.Lerp(x0, x1, 0.12f), 0f, z0 - o * 2.4f), new Vector3(Mathf.Lerp(x0, x1, 0.88f), 0f, z0 - o * 2.4f),
            };
            for (int i = 0; i < Max; i++)
            {
                Vector3 p = i < BaseCount ? ring[i] : sprout[i - BaseCount];
                p.x += ((float)rnd.NextDouble() - 0.5f) * 0.012f;
                p.z += ((float)rnd.NextDouble() - 0.5f) * 0.012f;
                _pos[i] = p;
                _yaw[i] = (float)rnd.NextDouble() * 360f;
                _size[i] = 0.8f + (float)rnd.NextDouble() * 0.45f;
                _born[i] = -100f;
            }
        }

        /// <summary>Fixes so far this run (0 = only the base ring). New ones sprout now; fewer = snap back.</summary>
        public void SetFixes(int fixes)
        {
            fixes = Mathf.Clamp(fixes, 0, MaxFixes);
            if (fixes == _fixes) return;
            float now = Time.time;
            for (int f = _fixes; f < fixes; f++)
                for (int k = 0; k < PerFix; k++)
                    _born[BaseCount + f * PerFix + k] = now + k * 0.12f;
            _fixes = fixes;
            _shown = BaseCount + fixes * PerFix;
        }

        public void Draw(int layer)
        {
            if (_mesh == null || _mat == null || _anchor == null) return;
            Matrix4x4 a = _anchor.localToWorldMatrix;
            float now = Time.time;
            int n = 0;
            for (int i = 0; i < _shown; i++)
            {
                float k = 1f;
                if (i >= BaseCount)
                {
                    float u = (now - _born[i]) / SproutSeconds;
                    if (u <= 0f) continue;
                    k = u >= 1f ? 1f : Pop(u);
                }
                float s = _size[i] * _meshScale * k;
                _draw[n++] = a * Matrix4x4.TRS(_pos[i], Quaternion.Euler(0f, _yaw[i], 0f), new Vector3(s, s, s));
            }
            if (n == 0) return;
            if (Instancing) Graphics.DrawMeshInstanced(_mesh, 0, _mat, _draw, n, null, ShadowCastingMode.Off, false, layer);
            else for (int i = 0; i < n; i++) Graphics.DrawMesh(_mesh, _draw[i], _mat, layer, null, 0, null, false, false, false); // no GPU (-nographics) or no instancing
        }

        static int _instancing = -1;
        static bool Instancing
        {
            get
            {
                if (_instancing < 0) _instancing = SystemInfo.supportsInstancing && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null ? 1 : 0;
                return _instancing == 1;
            }
        }

        static float Pop(float u)
        {
            // 0 -> 1.25 at 60 %, settles at 1
            return u < 0.6f ? Mathf.SmoothStep(0f, 1.25f, u / 0.6f) : Mathf.Lerp(1.25f, 1f, Mathf.SmoothStep(0f, 1f, (u - 0.6f) / 0.4f));
        }
    }
}
