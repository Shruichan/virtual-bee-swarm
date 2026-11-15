using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>
    /// Many bees on the Quest budget: in the meadow layout a bee pool draws only its nearest swarm.vatNear bees (within
    /// VatRange of the eyes) with Daniel's VAT bee (~560 tris); every other bee is the low-poly placeholder bee
    /// (GameMeshes, ~100 tris), which reads the same at a few metres. Same per-instance _Color, two instanced draws per
    /// pool at most, no garbage per frame. Make it from the pool's own mesh and material BEFORE BeeVat.Apply swaps them.
    /// </summary>
    public sealed class BeeLod
    {
        public const float VatRange = 2.5f;
        static readonly int ColorId = Shader.PropertyToID("_Color");

        readonly Mesh _cheapMesh;
        readonly Material _cheapMat;
        readonly MaterialPropertyBlock _nearMpb = new MaterialPropertyBlock(), _farMpb = new MaterialPropertyBlock();
        Matrix4x4[] _nm, _fm;
        Vector4[] _nc, _fc;
        float[] _d, _sel;
        public int LastNear { get; private set; }
        public int LastFar { get; private set; }

        BeeLod(Mesh mesh, Material mat)
        {
            _cheapMesh = mesh;
            _cheapMat = mat;
        }

        /// <summary>Null when there is nothing to fall back to.</summary>
        public static BeeLod Make(Mesh cheapMesh, Material cheapMat) => cheapMesh != null && cheapMat != null ? new BeeLod(cheapMesh, cheapMat) : null;

        /// <summary>Draws the pool split near / far. False: not in the meadow layout (or no VAT bee): draw as before.</summary>
        public bool Draw(Mesh mesh, Material mat, Matrix4x4[] m, Vector4[] col, int n, int layer)
        {
            if (!MeadowLayout.Active || mesh == null || mat == null || mesh == _cheapMesh || n <= 0 || m == null || col == null) return false;
            var cam = Camera.main;
            if (cam == null) return false;
            Ensure(m.Length);
            n = Mathf.Min(n, _nm.Length);
            Vector3 eye = cam.transform.position;
            int nearMax = Mathf.Max(0, MeadowTuning.VatNear);
            float r2 = VatRange * VatRange;
            int inRange = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = m[i].GetColumn(3);
                float d = (p - eye).sqrMagnitude;
                _d[i] = d;
                if (d < r2) _sel[inRange++] = d;
            }
            float cut = r2;
            if (inRange > nearMax) cut = nearMax == 0 ? -1f : Select(_sel, inRange, nearMax - 1);
            int nn = 0, nf = 0;
            for (int i = 0; i < n; i++)
            {
                if (_d[i] <= cut && _d[i] < r2 && nn < nearMax) { _nm[nn] = m[i]; _nc[nn] = col[i]; nn++; }
                else { _fm[nf] = m[i]; _fc[nf] = col[i]; nf++; }
            }
            LastNear = nn;
            LastFar = nf;
            if (nn > 0)
            {
                _nearMpb.SetVectorArray(ColorId, _nc);
                Graphics.DrawMeshInstanced(mesh, 0, mat, _nm, nn, _nearMpb, ShadowCastingMode.Off, false, layer);
            }
            if (nf > 0)
            {
                _farMpb.SetVectorArray(ColorId, _fc);
                Graphics.DrawMeshInstanced(_cheapMesh, 0, _cheapMat, _fm, nf, _farMpb, ShadowCastingMode.Off, false, layer);
            }
            return true;
        }

        void Ensure(int cap)
        {
            cap = Mathf.Clamp(cap, 1, 1023);
            if (_nm != null && _nm.Length >= cap) return;
            _nm = new Matrix4x4[cap]; _fm = new Matrix4x4[cap];
            _nc = new Vector4[cap]; _fc = new Vector4[cap];
            _d = new float[Mathf.Max(cap, 1024)]; _sel = new float[Mathf.Max(cap, 1024)];
        }

        /// <summary>The k-th smallest of a[0..n) (quickselect, reorders a).</summary>
        static float Select(float[] a, int n, int k)
        {
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                float pivot = a[(lo + hi) >> 1];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (a[i] < pivot) i++;
                    while (a[j] > pivot) j--;
                    if (i <= j) { float t = a[i]; a[i] = a[j]; a[j] = t; i++; j--; }
                }
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else return a[k];
            }
            return a[k];
        }
    }
}
