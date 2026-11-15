using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.Title
{
    /// <summary>
    /// The living part of the title scene: a bed of flowers and grass on the surface under the logo (the real table or
    /// floor in AR), swaying, and a small pool of swarm bees (the instanced VAT bee) that drift around the logo and visit
    /// the flowers. One instanced draw per kind of plant and one for the bees; no GameObjects, no allocations per frame.
    /// </summary>
    public sealed class TitleMeadow
    {
        const int MaxPlants = 96, BeeCount = 36;

        struct Kind
        {
            public Mesh mesh;
            public Material material;
            public Matrix4x4 fit;     // mesh -> 1 unit tall, standing on the origin
            public bool grass;
        }

        struct Plant
        {
            public Vector3 pos;
            public float yaw, height, phase, delay;
            public int kind;
        }

        readonly List<Kind> _kinds = new List<Kind>();
        readonly List<Plant> _plants = new List<Plant>(MaxPlants);
        readonly List<int> _flowers = new List<int>(48);
        Matrix4x4[][] _matrices;
        int[] _counts;
        readonly List<Material> _owned = new List<Material>();

        // bees
        Mesh _beeMesh, _ownMesh;
        Material _beeMaterial;
        readonly Vector3[] _pos = new Vector3[BeeCount], _vel = new Vector3[BeeCount], _dir = new Vector3[BeeCount];
        readonly float[] _phase = new float[BeeCount], _rad = new float[BeeCount], _hgt = new float[BeeCount], _speed = new float[BeeCount];
        readonly Matrix4x4[] _beeMatrices = new Matrix4x4[BeeCount];
        readonly Vector4[] _beeColors = new Vector4[BeeCount];
        readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        static readonly int ColorArrayId = Shader.PropertyToID("_Color");
        static readonly int DepthBiasId = Shader.PropertyToID("_EnvironmentDepthBias");
        readonly bool _instancing;
        readonly Transform _shadow;
        readonly Texture2D _shadowTex;
        float _laidOutAt;
        bool _onFloor;

        public int PlantCount => _plants.Count;
        public int FlowerCount => _flowers.Count;

        public TitleMeadow(Transform parent, TitleArt art)
        {
            _instancing = SystemInfo.supportsInstancing;
            AddKinds(art.flowers, false);
            AddKinds(art.grass, true);
            _matrices = new Matrix4x4[_kinds.Count][];
            _counts = new int[_kinds.Count];
            for (int i = 0; i < _kinds.Count; i++) _matrices[i] = new Matrix4x4[MaxPlants];

            _beeMesh = _ownMesh = GameMeshes.CreateBeeMesh();
            _beeMaterial = GameMaterials.Create("VBS_TitleBee (runtime)", Color.white, vertexColors: true, doubleSided: true);
            WingClock.EnableOn(_beeMaterial, _beeMesh);
            _owned.Add(_beeMaterial);
            if (BeeVat.Apply(ref _beeMesh, ref _beeMaterial, "title")) _owned.Add(_beeMaterial);
            var rnd = new System.Random(4242);
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < BeeCount; i++)
            {
                _phase[i] = (float)rnd.NextDouble() * Mathf.PI * 2f;
                _rad[i] = (float)rnd.NextDouble();
                _hgt[i] = (float)rnd.NextDouble();
                _speed[i] = 0.75f + 0.5f * (float)rnd.NextDouble();
                float y = 1f - (i + 0.5f) / BeeCount * 2f, r = Mathf.Sqrt(1f - y * y), th = golden * i;
                _dir[i] = new Vector3(Mathf.Cos(th) * r, y, Mathf.Sin(th) * r);
            }

            // a soft blob on the surface under the logo, so it reads as floating over it
            var go = new GameObject("TitleShadow");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            var mat = GameMaterials.Create("VBS_TitleShadow", new Color(0f, 0f, 0f, 0.3f), GameMaterials.Blend.Alpha);
            mat.mainTexture = _shadowTex = GameMeshes.CreateDotTexture(32);
            if (mat.HasProperty("_Occlude")) mat.SetFloat("_Occlude", 0f);
            _owned.Add(mat);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.sortingOrder = -60;
            _shadow = go.transform;
            go.SetActive(false);
        }

        void AddKinds(GameObject prefab, bool grass)
        {
            if (prefab == null) return;
            Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null || mr.sharedMaterial == null) continue;
                Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
                m.SetColumn(3, new Vector4(0f, 0f, 0f, 1f)); // where it sits in the prefab doesn't matter
                Bounds b = mf.sharedMesh.bounds;
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 p = m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f)));
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                float h = Mathf.Max(1e-4f, max.y - min.y);
                var mat = new Material(mr.sharedMaterial) { name = mr.sharedMaterial.name + " (title)", enableInstancing = true };
                if (mat.HasProperty(DepthBiasId)) mat.SetFloat(DepthBiasId, 0.06f); // they stand on the real table: no flicker
                _owned.Add(mat);
                _kinds.Add(new Kind
                {
                    mesh = mf.sharedMesh,
                    material = mat,
                    grass = grass,
                    fit = Matrix4x4.Scale(Vector3.one / h) * Matrix4x4.Translate(new Vector3(-(min.x + max.x) * 0.5f, -min.y, -(min.z + max.z) * 0.5f)) * m,
                });
            }
        }

        /// <summary>
        /// Plant the bed around `centre` (a point on the surface): `right` across the logo, `away` from the player. On
        /// the table it stays on the top (and off the hive's footprint when that is already there); on the floor it is
        /// a bigger ring that leaves the player's feet clear.
        /// </summary>
        public void LayOut(Vector3 centre, Vector3 right, Vector3 away, bool onTable, RoomModel room, bool avoidFootprints, Vector3 head, float time)
        {
            _plants.Clear();
            _flowers.Clear();
            _laidOutAt = time;
            _onFloor = !onTable;
            var rnd = new System.Random(77);
            float rx = onTable ? 0.52f : 0.62f, rz = onTable ? 0.26f : 0.5f, grow = onTable ? 1f : 1.6f;
            int wantFlowers = onTable ? 34 : 40, wantGrass = onTable ? 44 : 50;
            int flowerKinds = 0, grassKinds = 0;
            foreach (var k in _kinds) { if (k.grass) grassKinds++; else flowerKinds++; }
            int flowers = 0, grass = 0;
            bool table = onTable && room != null && room.HasTable;
            for (int attempt = 0; attempt < 900 && (flowers < wantFlowers || grass < wantGrass) && _plants.Count < MaxPlants; attempt++)
            {
                float u = (float)rnd.NextDouble() * 2f - 1f, v = (float)rnd.NextDouble() * 2f - 1f;
                float jitter = (float)rnd.NextDouble(), pick = (float)rnd.NextDouble();
                float rr = Mathf.Sqrt(u * u + v * v);
                if (rr > 1f) continue;
                bool wantFlower = flowers < wantFlowers && flowerKinds > 0 && rr > 0.3f && (attempt & 1) == 0;
                if (!wantFlower && (grass >= wantGrass || grassKinds == 0 || rr < 0.2f)) continue;
                Vector3 p = centre + right * (u * rx) + away * (v * rz);
                if (table && !room.InsideTable(p, 0.04f)) continue;
                if (avoidFootprints && room != null && room.InsideFootprint(p, 0.03f)) continue;
                Vector3 toHead = p - head;
                toHead.y = 0f;
                if (!onTable && toHead.sqrMagnitude < 0.5f * 0.5f) continue;
                float gap = wantFlower ? 0.055f * grow : 0.03f * grow;
                bool crowded = false;
                for (int i = 0; i < _plants.Count && !crowded; i++)
                    crowded = (_plants[i].pos - p).sqrMagnitude < gap * gap;
                if (crowded) continue;
                int nth = Mathf.Min((int)(pick * (wantFlower ? flowerKinds : grassKinds)), (wantFlower ? flowerKinds : grassKinds) - 1);
                int kind = -1;
                for (int i = 0; i < _kinds.Count; i++)
                    if (_kinds[i].grass != wantFlower && nth-- == 0) { kind = i; break; }
                if (kind < 0) continue;
                _plants.Add(new Plant
                {
                    pos = p,
                    yaw = jitter * 360f,
                    height = (wantFlower ? Mathf.Lerp(0.1f, 0.17f, jitter) : Mathf.Lerp(0.045f, 0.075f, jitter)) * grow,
                    phase = pick * Mathf.PI * 2f,
                    delay = 0.1f + rr * 0.45f + jitter * 0.2f,
                    kind = kind,
                });
                if (wantFlower) { flowers++; _flowers.Add(_plants.Count - 1); } else grass++;
            }
            if (_shadow != null)
            {
                _shadow.gameObject.SetActive(onTable);
                _shadow.SetPositionAndRotation(centre + Vector3.up * 0.004f, Quaternion.LookRotation(Vector3.down, away));
                _shadow.localScale = new Vector3(0.78f, 0.3f, 1f);
            }
        }

        public void Hide()
        {
            _plants.Clear();
            _flowers.Clear();
            if (_shadow != null) _shadow.gameObject.SetActive(false);
        }

        /// <summary>Put the bees around the logo (start of the title).</summary>
        public void ScatterBees(Vector3 logo)
        {
            for (int i = 0; i < BeeCount; i++)
            {
                _pos[i] = logo + _dir[i] * (0.2f + 0.3f * _rad[i]);
                _vel[i] = Vector3.zero;
            }
        }

        /// <summary>
        /// One frame. leave 0..1: the bees scatter away from the logo and thin out, the plants tuck back into the
        /// ground. Nothing here comes nearer than `keepClear` to the head.
        /// </summary>
        public void Tick(float time, float dt, Vector3 logo, Vector3 right, Vector3 away, Vector3 head, float leave, float keepClear, int layer)
        {
            dt = Mathf.Min(dt, 0.05f);
            // ---- plants
            for (int k = 0; k < _counts.Length; k++) _counts[k] = 0;
            float since = time - _laidOutAt;
            for (int i = 0; i < _plants.Count; i++)
            {
                var pl = _plants[i];
                float g = Mathf.Clamp01((since - pl.delay) / 0.5f);
                g = g <= 0f ? 0f : g >= 1f ? 1f : 1f + 2.70158f * Mathf.Pow(g - 1f, 3f) + 1.70158f * Mathf.Pow(g - 1f, 2f);
                g *= 1f - Mathf.Clamp01(leave * 1.6f - pl.delay * 0.6f);
                if (g <= 0.001f) continue;
                float sway = _kinds[pl.kind].grass ? 7f : 4.5f;
                Quaternion rot = Quaternion.Euler(Mathf.Sin(time * 1.3f + pl.phase) * sway, 0f, Mathf.Cos(time * 1.05f + pl.phase * 1.7f) * sway) *
                                 Quaternion.Euler(0f, pl.yaw, 0f);
                int n = _counts[pl.kind]++;
                _matrices[pl.kind][n] = Matrix4x4.TRS(pl.pos, rot, Vector3.one * (pl.height * g)) * _kinds[pl.kind].fit;
            }
            if (_instancing)
                for (int k = 0; k < _kinds.Count; k++)
                    if (_counts[k] > 0)
                        Graphics.DrawMeshInstanced(_kinds[k].mesh, 0, _kinds[k].material, _matrices[k], _counts[k], null, ShadowCastingMode.Off, false, layer);

            // ---- bees
            if (_beeMesh == null || _beeMaterial == null) return;
            float thin = 1f - Mathf.Clamp01((leave - 0.45f) / 0.55f);
            Color col = new Color(1f, 0.85f, 0.3f, 1f);
            int nf = _flowers.Count;
            for (int i = 0; i < BeeCount; i++)
            {
                float ph = _phase[i], sp = _speed[i];
                Vector3 p = _pos[i], v = _vel[i], target;
                float accel = 4f, maxSpeed = 0.7f;
                if (leave > 0f)
                {
                    Vector3 outward = p - logo;
                    if (outward.sqrMagnitude < 1e-4f) outward = _dir[i];
                    target = p + outward.normalized * 1.2f + Vector3.up * (0.3f + 0.4f * _hgt[i]) + _dir[i] * 0.3f;
                    accel = 9f;
                    maxSpeed = 2.4f;
                }
                else if (nf > 0 && i % 3 == 0)
                {
                    // a visitor: hover over one flower, then on to the next
                    float period = 3.2f + 2.2f * _rad[i];
                    float cyc = (time + ph * 3f) / period;
                    int k = Mathf.FloorToInt(cyc);
                    float f = cyc - k;
                    Vector3 a = Top(_plants[_flowers[Pick(i, k, nf)]]), b = Top(_plants[_flowers[Pick(i, k + 1, nf)]]);
                    if (f < 0.68f)
                        target = a + new Vector3(Mathf.Cos(time * 5f + ph), 0.4f + 0.4f * Mathf.Sin(time * 3.1f + ph), Mathf.Sin(time * 5f + ph)) * 0.014f;
                    else
                    {
                        float x = Mathf.SmoothStep(0f, 1f, (f - 0.68f) / 0.32f);
                        target = Vector3.Lerp(a, b, x) + Vector3.up * (Mathf.Sin(x * Mathf.PI) * 0.09f);
                    }
                    accel = 9f;
                    maxSpeed = 0.55f;
                }
                else
                {
                    // a drifter: wide slow loops around the logo, in front of it and behind
                    float a = ph + time * 0.55f * sp;
                    target = logo + right * (Mathf.Cos(a) * (0.26f + 0.3f * _rad[i]))
                                  + Vector3.up * (-0.2f + 0.46f * _hgt[i] + Mathf.Sin(time * 1.1f + ph) * 0.04f)
                                  + away * (Mathf.Sin(a) * (0.1f + 0.2f * _rad[i]));
                }
                Vector3 fromHead = target - head;
                float hd = fromHead.magnitude;
                if (hd < keepClear && hd > 1e-4f) target = head + fromHead * (keepClear / hd);

                Vector3 steer = (target - p) * accel - v * (accel * 0.35f);
                int j1 = (i + 1) % BeeCount, j2 = (i + 13) % BeeCount;
                Vector3 d1 = p - _pos[j1], d2 = p - _pos[j2];
                float s1 = d1.sqrMagnitude, s2 = d2.sqrMagnitude;
                if (s1 < 0.0009f && s1 > 1e-8f) steer += d1 * (0.6f / s1) * 0.01f;
                if (s2 < 0.0009f && s2 > 1e-8f) steer += d2 * (0.6f / s2) * 0.01f;
                v += steer * dt;
                float vm = v.magnitude;
                if (vm > maxSpeed) { v *= maxSpeed / vm; vm = maxSpeed; }
                p += v * dt;
                // comfort: a bee itself never ends up inside the clear zone either
                Vector3 ph2 = p - head;
                float pd = ph2.magnitude;
                if (pd < keepClear && pd > 1e-4f) p = head + ph2 * (keepClear / pd);
                _pos[i] = p;
                _vel[i] = v;

                Vector3 fwd = vm > 0.02f ? v / vm : target - p;
                Quaternion rot = fwd.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fwd, Vector3.up) : Quaternion.identity;
                float size = 0.035f * Mathf.Clamp01((since - 0.2f - 0.6f * _rad[i]) / 0.4f) * thin;
                _beeMatrices[i] = Matrix4x4.TRS(p, rot * InsectBodySway.Offset(Time.time, i * 2.4f, 0.5f), new Vector3(size, size, size));
                float shade = (0.85f + 0.3f * _rad[i]) * 1.3f;
                _beeColors[i] = new Vector4(col.r * shade, col.g * shade, col.b * shade, 1f);
            }
            if (_instancing && thin > 0.001f)
            {
                _mpb.SetVectorArray(ColorArrayId, _beeColors);
                Graphics.DrawMeshInstanced(_beeMesh, 0, _beeMaterial, _beeMatrices, BeeCount, _mpb, ShadowCastingMode.Off, false, layer);
            }
        }

        static Vector3 Top(Plant p) => p.pos + Vector3.up * (p.height + 0.02f);

        static int Pick(int bee, int step, int count)
        {
            unchecked
            {
                uint h = (uint)(bee * 73856093) ^ (uint)(step * 19349663);
                h ^= h >> 13;
                h *= 0x5bd1e995;
                h ^= h >> 15;
                return (int)(h % (uint)count);
            }
        }

        public void Destroy()
        {
            foreach (var m in _owned) if (m != null) Object.Destroy(m);
            if (_shadowTex != null) Object.Destroy(_shadowTex);
            if (_ownMesh != null) Object.Destroy(_ownMesh);
            if (_shadow != null) Object.Destroy(_shadow.gameObject);
        }
    }
}
