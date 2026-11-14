using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.AR
{
    /// <summary>
    /// Blob contact shadows on the real table (PLAN.md V5): one soft dark disc under each registered thing, opacity 0
    /// at 40 cm above the top rising to 0.45 at contact, spreading a little with height. One instanced draw, no lights,
    /// no allocations per frame, never occluded by real objects (_Occlude = 0). The table plane is the TableAnchor
    /// (it sits on the real top by design). Registers the hive, HONEYMOON-1, the thermometer, Beeswax and every
    /// station prop by itself; BeeDelivery items call Register() / Touchdown.Play().
    /// Switch: ar.shadows.
    /// </summary>
    [DefaultExecutionOrder(200)] // after anything that moves the casters
    public class ContactShadows : MonoBehaviour
    {
        public const int MaxCasters = 64;
        [SerializeField] Transform tableAnchor;
        [SerializeField] float fadeHeight = 0.4f;
        [SerializeField] float contactOpacity = 0.45f;
        [SerializeField] float spreadPerMetre = 0.8f;
        [SerializeField] float lift = 0.002f;

        public static ContactShadows Instance { get; private set; }

        class Caster
        {
            public Transform t;
            public Renderer[] renderers;
            public float radius; // 0 = from the renderers' bounds
            public float pulseUntil;
        }

        readonly List<Caster> _casters = new List<Caster>(MaxCasters);
        readonly Matrix4x4[] _matrices = new Matrix4x4[MaxCasters];
        readonly Vector4[] _colors = new Vector4[MaxCasters];
        Mesh _quad;
        Material _mat;
        MaterialPropertyBlock _mpb;
        static readonly int ColorId = Shader.PropertyToID("_Color");

        public int VisibleCount { get; private set; }

        void Awake()
        {
            Instance = this;
            _quad = BuildDisc();
            _mat = GameMaterials.Create("VBS_ContactShadow (runtime)", Color.black, GameMaterials.Blend.Alpha, instancing: true);
            _mat.mainTexture = GameMeshes.CreateDotTexture(64);
            if (_mat.HasProperty("_MainTex")) _mat.SetTexture("_MainTex", _mat.mainTexture);
            ArMaterials.MarkNeverOccluded(_mat);
            _mat.renderQueue = (int)RenderQueue.Transparent - 10; // under the swarm and the props
            _mpb = new MaterialPropertyBlock();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void Configure(Transform anchor) => tableAnchor = anchor;

        /// <summary>Adds a shadow under `t` (radius 0 = measured from its renderers each frame). Idempotent.</summary>
        public void Register(Transform t, float radius = 0f)
        {
            if (t == null || _casters.Count >= MaxCasters) return;
            for (int i = 0; i < _casters.Count; i++) if (_casters[i].t == t) return;
            var all = t.GetComponentsInChildren<Renderer>(true);
            var solid = new List<Renderer>(all.Length);
            foreach (var r in all)
                if (!(r is ParticleSystemRenderer) && !(r is LineRenderer) && r.GetComponent<TextMesh>() == null) solid.Add(r);
            _casters.Add(new Caster { t = t, renderers = solid.ToArray(), radius = radius });
        }

        public void Unregister(Transform t)
        {
            for (int i = _casters.Count - 1; i >= 0; i--) if (_casters[i].t == t) _casters.RemoveAt(i);
        }

        /// <summary>Darker shadow for a moment (touchdown).</summary>
        public void Pulse(Transform t, float seconds = 0.25f)
        {
            Register(t);
            for (int i = 0; i < _casters.Count; i++) if (_casters[i].t == t) _casters[i].pulseUntil = Time.time + seconds;
        }

        /// <summary>The default casters under the TableAnchor (by the builder's names; missing ones are skipped).</summary>
        public void RegisterDefaults()
        {
            if (tableAnchor == null) return;
            Register(Find(tableAnchor, "Hive/LiftRoot/Body"), 0f);
            Register(Find(tableAnchor, "HONEYMOON-1"), 0.07f);
            Register(Find(tableAnchor, "Thermometer"), 0.03f);
            Register(Find(tableAnchor, "Beeswax"), 0.06f);
            var stations = Find(tableAnchor, "Stations");
            if (stations != null)
            {
                foreach (Transform station in stations)
                {
                    var props = station.Find("Props");
                    if (props == null) continue;
                    foreach (Transform prop in props)
                    {
                        if (prop.name.StartsWith("Wasp") || prop.name.EndsWith("Puff") || prop.name.EndsWith("Point")) continue;
                        if (prop.GetComponentInChildren<Renderer>(true) == null) continue;
                        Register(prop, 0f);
                    }
                }
            }
        }

        static Transform Find(Transform root, string path) => root != null ? root.Find(path) : null;

        void LateUpdate()
        {
            VisibleCount = 0;
            if (!ArTuning.Shadows || tableAnchor == null || _casters.Count == 0) return;
            float tableY = tableAnchor.position.y;
            Quaternion rot = Quaternion.LookRotation(Flat(tableAnchor.forward), Vector3.up);
            float now = Time.time;
            int n = 0;
            for (int i = _casters.Count - 1; i >= 0; i--)
            {
                var c = _casters[i];
                if (c.t == null) { _casters.RemoveAt(i); continue; }
                if (!c.t.gameObject.activeInHierarchy) continue;
                if (!Measure(c, out Bounds b)) continue;
                float h = b.min.y - tableY;
                if (h < -0.05f || h > fadeHeight) continue;
                float k = 1f - Mathf.Clamp01(h / fadeHeight);
                float alpha = contactOpacity * k * k;
                if (now < c.pulseUntil) alpha = Mathf.Min(0.75f, alpha * 1.6f);
                if (alpha < 0.01f) continue;
                float r = (c.radius > 0f ? c.radius : Mathf.Max(b.extents.x, b.extents.z) * 1.1f) * (1f + Mathf.Max(0f, h) * spreadPerMetre);
                Vector3 p = new Vector3(b.center.x, tableY + lift, b.center.z);
                _matrices[n] = Matrix4x4.TRS(p, rot, new Vector3(r * 2f, 1f, r * 2f));
                _colors[n] = new Vector4(0f, 0f, 0f, alpha);
                n++;
                if (n >= MaxCasters) break;
            }
            VisibleCount = n;
            if (n == 0) return;
            _mpb.SetVectorArray(ColorId, _colors);
            if (SystemInfo.supportsInstancing)
                Graphics.DrawMeshInstanced(_quad, 0, _mat, _matrices, n, _mpb, ShadowCastingMode.Off, false, gameObject.layer);
            else
                for (int i = 0; i < n; i++)
                {
                    _mpb.SetColor(ColorId, _colors[i]);
                    Graphics.DrawMesh(_quad, _matrices[i], _mat, gameObject.layer, null, 0, _mpb, ShadowCastingMode.Off, false);
                }
        }

        static bool Measure(Caster c, out Bounds b)
        {
            b = default;
            bool any = false;
            var rs = c.renderers;
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            if (!any && c.radius > 0f) { b = new Bounds(c.t.position, Vector3.one * 0.001f); any = true; }
            return any;
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        /// <summary>A unit disc (diameter 1) in XZ, facing up, uv 0..1 (the dot texture fades it out).</summary>
        static Mesh BuildDisc()
        {
            var m = new Mesh { name = "VBS_ShadowQuad" };
            m.vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }

    /// <summary>
    /// The landing beat for anything set down on the table (PLAN.md V5): a darker shadow, a 70 ms squash
    /// (1.06, 0.92, 1.06), 8 dust particles from one shared system, and an optional thunk (no clip is assigned by
    /// default: the game stays silent until lane D adds one). BeeDelivery calls Touchdown.Play(item, weight).
    /// </summary>
    public static class Touchdown
    {
        static Runner _runner;
        public static int Count { get; private set; }
        /// <summary>Optional thunk (pitch drops with weight). Null = silent.</summary>
        public static AudioClip Thunk;

        public static void Play(Transform item, float weight = 1f)
        {
            if (item == null) return;
            Count++;
            EnsureRunner();
            _runner.Play(item, Mathf.Clamp(weight, 0.2f, 3f));
        }

        static void EnsureRunner()
        {
            if (_runner != null) return;
            var go = new GameObject("Touchdown (runtime)");
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<Runner>();
        }

        class Runner : MonoBehaviour
        {
            ParticleSystem _dust;
            AudioSource _audio;
            static readonly Vector3 Squash = new Vector3(1.06f, 0.92f, 1.06f);

            void Awake()
            {
                var go = new GameObject("Dust");
                go.transform.SetParent(transform, false);
                _dust = go.AddComponent<ParticleSystem>();
                _dust.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = _dust.main;
                main.playOnAwake = false;
                main.loop = false;
                main.startLifetime = 0.6f;
                main.startSpeed = 0.18f;
                main.startSize = 0.025f;
                main.gravityModifier = 0.05f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.startColor = new Color(0.75f, 0.7f, 0.62f, 0.7f);
                main.maxParticles = 64;
                var em = _dust.emission;
                em.enabled = false;
                var shape = _dust.shape;
                shape.shapeType = ParticleSystemShapeType.Circle;
                shape.radius = 0.05f;
                shape.rotation = new Vector3(-90f, 0f, 0f);
                var col = _dust.colorOverLifetime;
                col.enabled = true;
                var g = new Gradient();
                g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
                col.color = g;
                var r = go.GetComponent<ParticleSystemRenderer>();
                var mat = GameMaterials.Create("VBS_Dust (runtime)", Color.white, GameMaterials.Blend.Alpha, vertexColors: true);
                var dot = GameMeshes.CreateDotTexture(32);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", dot);
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", dot);
                ArMaterials.MarkNeverOccluded(mat);
                r.sharedMaterial = mat;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                _audio = gameObject.AddComponent<AudioSource>();
                _audio.playOnAwake = false;
                _audio.spatialBlend = 1f;
            }

            public void Play(Transform item, float weight)
            {
                if (ContactShadows.Instance != null) ContactShadows.Instance.Pulse(item);
                Vector3 baseAt = item.position;
                var rs = item.GetComponentsInChildren<Renderer>();
                if (rs.Length > 0)
                {
                    var b = rs[0].bounds;
                    for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                    baseAt = new Vector3(b.center.x, b.min.y + 0.005f, b.center.z);
                }
                _dust.transform.position = baseAt;
                var sh = _dust.shape;
                sh.radius = 0.03f + 0.02f * weight;
                _dust.Emit(8);
                if (Thunk != null)
                {
                    _audio.transform.position = baseAt;
                    _audio.pitch = Mathf.Lerp(1.15f, 0.7f, Mathf.InverseLerp(0.2f, 3f, weight));
                    _audio.PlayOneShot(Thunk, 0.8f);
                }
                StartCoroutine(SquashRoutine(item));
            }

            IEnumerator SquashRoutine(Transform item)
            {
                Vector3 s0 = item.localScale;
                item.localScale = Vector3.Scale(s0, Squash);
                float until = Time.time + 0.07f;
                while (Time.time < until && item != null) yield return null;
                if (item != null) item.localScale = s0;
            }
        }
    }
}
