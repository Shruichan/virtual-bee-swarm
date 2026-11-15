using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Juice
{
    /// <summary>
    /// Pooled, code-animated particles in two instanced draws: flat diamonds (pollen, sparks, puff, ink ring) and little
    /// bees (gold crews flying a curve). Opaque unlit, no lights or shadows, no per-frame allocations; a particle fades by
    /// shrinking. Delayed spawns wait (hidden) until their start time.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public class JuiceParticles : MonoBehaviour
    {
        const int MaxSparks = 240, MaxBees = 48;
        static readonly int ColorArrayId = Shader.PropertyToID("_Color");

        struct Spark { public Vector3 pos, vel; public float born, life, size, spin, drag, gravity; public Color color; }
        struct Bee { public Vector3 from, ctrl, to; public float born, life, size, wobble; public Color color; }

        readonly Spark[] _s = new Spark[MaxSparks];
        readonly Bee[] _b = new Bee[MaxBees];
        int _ns, _nb;
        readonly Matrix4x4[] _ms = new Matrix4x4[MaxSparks], _mb = new Matrix4x4[MaxBees];
        readonly Vector4[] _cs = new Vector4[MaxSparks], _cb = new Vector4[MaxBees];
        MaterialPropertyBlock _mpbS, _mpbB;
        Mesh _diamond, _bee, _fallbackBee;
        Material _sparkMat, _beeMat;
        bool _instancing;

        public int Live => _ns + _nb;

        void Awake()
        {
            _diamond = CreateDiamond();
            _sparkMat = GameMaterials.Create("VBS_JuiceSpark (runtime)", Color.white, doubleSided: true);
            if (!BeeVat.Apply(ref _bee, ref _beeMat, "flying bee crews"))
            {
                _bee = _fallbackBee = GameMeshes.CreateBeeMesh();
                _beeMat = GameMaterials.Create("VBS_JuiceBee (runtime)", Color.white, vertexColors: true, doubleSided: true);
            }
            _mpbS = new MaterialPropertyBlock();
            _mpbB = new MaterialPropertyBlock();
            _instancing = SystemInfo.supportsInstancing;
        }

        static Mesh CreateDiamond()
        {
            var m = new Mesh { name = "VBS_JuiceDiamond" };
            m.vertices = new[] { new Vector3(0f, 0.5f, 0f), new Vector3(0.38f, 0f, 0f), new Vector3(0f, -0.5f, 0f), new Vector3(-0.38f, 0f, 0f) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------ spawning
        /// <summary>n diamonds out of a point: speed m/s (randomised 50-100 %), biased up by upBias, falling with gravity.</summary>
        public void Burst(Vector3 at, int n, Color c, float speed, float life, float size, float gravity = 1.6f,
                          float upBias = 0.6f, float drag = 2.5f, float delay = 0f)
        {
            float now = Time.time + delay;
            for (int k = 0; k < n && _ns < MaxSparks; k++)
            {
                Vector3 d = Random.onUnitSphere;
                d.y = Mathf.Abs(d.y) * upBias + d.y * (1f - upBias);
                ref var s = ref _s[_ns++];
                s.pos = at;
                s.vel = d.normalized * speed * Random.Range(0.5f, 1f);
                s.born = now + Random.Range(0f, 0.04f);
                s.life = life * Random.Range(0.7f, 1.15f);
                s.size = size * Random.Range(0.7f, 1.2f);
                s.spin = Random.Range(-12f, 12f);
                s.drag = drag;
                s.gravity = gravity;
                s.color = c * Random.Range(0.85f, 1.1f);
                s.color.a = 1f;
            }
        }

        /// <summary>An expanding ring of n diamonds in the plane facing normal (an ink ring, a shock ring).</summary>
        public void Ring(Vector3 at, Vector3 normal, int n, Color c, float speed, float life, float size, float delay = 0f)
        {
            if (normal.sqrMagnitude < 1e-6f) normal = Vector3.up;
            normal.Normalize();
            Vector3 u = Vector3.Cross(normal, Mathf.Abs(normal.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 v = Vector3.Cross(normal, u);
            float now = Time.time + delay, a0 = Random.Range(0f, 6.283f);
            for (int k = 0; k < n && _ns < MaxSparks; k++)
            {
                float a = a0 + k * 6.2831853f / n;
                Vector3 d = u * Mathf.Cos(a) + v * Mathf.Sin(a);
                ref var s = ref _s[_ns++];
                s.pos = at + d * 0.01f;
                s.vel = d * speed;
                s.born = now;
                s.life = life;
                s.size = size;
                s.spin = 0f;
                s.drag = 6f;
                s.gravity = 0f;
                s.color = c;
            }
        }

        /// <summary>One bee flying a curve from -> to (arcUp metres above the midpoint) over life seconds, starting after delay.</summary>
        public void Fly(Vector3 from, Vector3 to, float arcUp, float life, float delay, Color c, float size, Vector3 jitter = default)
        {
            if (_nb >= MaxBees) return;
            ref var b = ref _b[_nb++];
            b.from = from;
            b.to = to;
            b.ctrl = (from + to) * 0.5f + Vector3.up * arcUp + jitter;
            b.born = Time.time + delay;
            b.life = Mathf.Max(0.05f, life);
            b.size = size;
            b.wobble = Random.Range(0f, 6.283f);
            b.color = c;
        }

        public void Clear() { _ns = 0; _nb = 0; }

        // ------------------------------------------------------------------ frame
        void LateUpdate()
        {
            float now = Time.time, dt = Time.deltaTime;
            var cam = Camera.main;
            Quaternion face = cam != null ? cam.transform.rotation : Quaternion.identity;
            int layer = gameObject.layer;

            // sparks: integrate, compact, build matrices
            int drawn = 0;
            for (int i = 0; i < _ns;)
            {
                ref var s = ref _s[i];
                float age = now - s.born;
                if (age >= s.life) { _s[i] = _s[--_ns]; continue; }
                i++;
                if (age < 0f) continue;
                s.vel *= 1f / (1f + s.drag * dt);
                s.vel.y -= s.gravity * dt;
                s.pos += s.vel * dt;
                float t = age / s.life;
                float sc = s.size * (t < 0.12f ? t / 0.12f : 1f - (t - 0.12f) / 0.88f * ((t - 0.12f) / 0.88f));
                if (sc <= 0.0005f) continue;
                _ms[drawn] = Matrix4x4.TRS(s.pos, face * Quaternion.Euler(0f, 0f, s.spin * age * 57.3f), new Vector3(sc, sc, sc));
                _cs[drawn] = new Vector4(s.color.r, s.color.g, s.color.b, 1f);
                drawn++;
            }
            if (drawn > 0) Draw(_diamond, _sparkMat, _ms, _cs, drawn, _mpbS, layer);

            drawn = 0;
            for (int i = 0; i < _nb;)
            {
                ref var b = ref _b[i];
                float age = now - b.born;
                if (age >= b.life) { _b[i] = _b[--_nb]; continue; }
                i++;
                if (age < 0f) continue;
                float t = age / b.life;
                float e = t * t * (3f - 2f * t);
                Vector3 p = Bez(b.from, b.ctrl, b.to, e);
                Vector3 ahead = Bez(b.from, b.ctrl, b.to, Mathf.Min(1f, e + 0.03f)) - p;
                p += Vector3.up * (Mathf.Sin(age * 22f + b.wobble) * 0.006f);
                float sc = b.size * (t < 0.1f ? t / 0.1f : t > 0.85f ? (1f - t) / 0.15f : 1f);
                if (sc <= 0.0005f) continue;
                Quaternion r = ahead.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(ahead, Vector3.up) : face;
                _mb[drawn] = Matrix4x4.TRS(p, r, new Vector3(sc, sc, sc));
                _cb[drawn] = new Vector4(b.color.r, b.color.g, b.color.b, 1f);
                drawn++;
            }
            if (drawn > 0) Draw(_bee, _beeMat, _mb, _cb, drawn, _mpbB, layer);
        }

        static Vector3 Bez(Vector3 a, Vector3 c, Vector3 b, float t)
        {
            float u = 1f - t;
            return u * u * a + 2f * u * t * c + t * t * b;
        }

        void Draw(Mesh mesh, Material mat, Matrix4x4[] m, Vector4[] c, int n, MaterialPropertyBlock mpb, int layer)
        {
            if (mesh == null || mat == null) return;
            if (_instancing)
            {
                mpb.SetVectorArray(ColorArrayId, c);
                Graphics.DrawMeshInstanced(mesh, 0, mat, m, n, mpb, ShadowCastingMode.Off, false, layer);
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    mpb.SetColor(ColorArrayId, c[i]);
                    Graphics.DrawMesh(mesh, m[i], mat, layer, null, 0, mpb, ShadowCastingMode.Off, false);
                }
            }
        }

        void OnDestroy()
        {
            if (_diamond != null) Destroy(_diamond);
            if (_fallbackBee != null) Destroy(_fallbackBee);
            if (_sparkMat != null) Destroy(_sparkMat);
            if (_beeMat != null) Destroy(_beeMat);
        }
    }
}
