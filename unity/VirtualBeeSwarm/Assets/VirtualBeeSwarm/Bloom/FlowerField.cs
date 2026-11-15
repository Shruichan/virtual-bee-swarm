using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>
    /// The flowers on the floor: up to 500 clumps of one mesh (BloomMeshes.BuildClump, ~200 tris) in ONE instanced draw
    /// (Bloom.shader, assumeuniformscaling: ~1000 fit a batch on Vulkan). Per instance: yaw, scale 0.8-1.25, a palette
    /// value, a sway phase and a mask of which of the 12 bloom slots show. Birth = 0.15 s + distance from the hive's
    /// floor point past the nearest flower (BloomSurfaces.WaveStart) / bloom.wave + up to 0.25 s, so the bloom runs out
    /// from the hive as a wave that starts at once at the table's edge; every part pops in with
    /// an ease-out-back over 0.6 s in the vertex shader. No GameObject per flower, no colliders, no lights.
    /// </summary>
    public sealed class FlowerField
    {
        public const float GrowSeconds = 0.6f;
        const int SunflowerSlot = 10;

        static readonly int BloomDataId = Shader.PropertyToID("_BloomData");
        static readonly int BloomTId = Shader.PropertyToID("_BloomT");
        static readonly int ClockId = Shader.PropertyToID("_BloomClock");
        static readonly int ScaleId = Shader.PropertyToID("_BloomScale");
        static readonly int FeetId = Shader.PropertyToID("_FeetFold");

        readonly Mesh _mesh;
        readonly Material _mat;
        readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        readonly Matrix4x4[] _m = new Matrix4x4[BloomTuning.MaxClumps];
        readonly Vector4[] _data = new Vector4[BloomTuning.MaxClumps];
        readonly float[] _scale = new float[BloomTuning.MaxClumps];
        int _n;
        bool _dataSet;

        public readonly BloomMeshes.SlotInfo[] Slots;
        public int Count => _n;
        public Mesh Mesh => _mesh;
        public int TrisPerClump { get; }
        public int Tris => _n * TrisPerClump;
        /// <summary>Seconds after the wave start when the last flower is fully out.</summary>
        public float Done { get; private set; }

        public readonly bool Art;
        public int SlotCount => Slots.Length;
        /// <summary>Clumps that fit 105k tris with this clump mesh (vines, capped at Vines.MaxTris, + bees stay under 150k).</summary>
        public int MaxClumps => Mathf.Clamp(TriBudget / Mathf.Max(1, TrisPerClump), 1, BloomTuning.MaxClumps);
        public const int TriBudget = 105000;

        public FlowerField(Material template, bool art)
        {
            Art = art;
            _mesh = art ? BloomMeshes.BuildArtClump(out Slots) : BloomMeshes.BuildClump(out Slots);
            TrisPerClump = (int)(_mesh.GetIndexCount(0) / 3);
            _mat = new Material(template) { name = "VBS_BloomFlowers (runtime)", enableInstancing = true };
            _mat.SetFloat(FeetId, 1f);
        }

        public void Build(BloomSurfaces s, float wave, int seed)
        {
            var rnd = new System.Random(seed);
            _n = Mathf.Min(s.Floor.Count, MaxClumps);
            Done = 0f;
            for (int i = 0; i < _n; i++)
            {
                Vector3 p = s.Floor[i];
                float scale = (Art ? 0.95f : 0.8f) + (float)rnd.NextDouble() * 0.45f;
                // under the window (within 1 m of its wall, inside its span): no sunflowers, short clumps, so no head
                // reaches into the (moon-sized) pane from the player's view
                bool underWindow = false;
                if (s.HasWindow)
                {
                    Vector3 d = p - s.Window.center;
                    underWindow = Vector3.Dot(d, s.Window.normal) < 1f && Mathf.Abs(Vector3.Dot(d, s.Window.right)) < s.Window.half.x + 0.3f;
                    if (underWindow) scale = Mathf.Min(scale, 0.95f);
                }
                _scale[i] = scale;
                _m[i] = Matrix4x4.TRS(p, Quaternion.Euler(0f, (float)rnd.NextDouble() * 360f, 0f), new Vector3(scale, scale, scale));
                int mask = 0, on = 0;
                int slots = SlotCount, sun = Art ? (int)BloomMeshes.Species.Sunflower : SunflowerSlot, min = Art ? 3 : 4;
                for (int b = 0; b < slots; b++)
                {
                    float chance = b == sun ? (underWindow ? 0f : Art ? 0.35f : 0.3f) : Art ? 0.8f : 0.62f; // art: most of the (costly) slots show
                    if (rnd.NextDouble() < chance) { mask |= 1 << b; on++; }
                }
                for (int b = rnd.Next(slots); on < min; b = (b + 1) % slots)
                    if (b != sun && (mask & (1 << b)) == 0) { mask |= 1 << b; on++; }
                float birth = 0.15f + s.WaveArrival(p, wave) + (float)rnd.NextDouble() * 0.25f;
                _data[i] = new Vector4(birth, (float)rnd.NextDouble(), (float)rnd.NextDouble() * 6.2832f, mask);
                Done = Mathf.Max(Done, birth + 0.35f + GrowSeconds);
            }
            for (int i = _n; i < _data.Length; i++) _data[i] = Vector4.zero;
            _dataSet = false;
        }

        /// <summary>Draws one collapsed, unborn instance so the GPU pipeline for this variant exists before the escape.</summary>
        public void Warm(Vector3 at, int layer)
        {
            if (!SystemInfo.supportsInstancing) return;
            if (_n == 0) _m[0] = Matrix4x4.Translate(at); // in view, so it isn't culled; collapsed, so nothing shows
            _mpb.SetVectorArray(BloomDataId, _data);
            _dataSet = false;
            _mpb.SetFloat(BloomTId, -1000f);
            _mpb.SetFloat(ClockId, 0f);
            _mpb.SetFloat(ScaleId, 0f);
            Graphics.DrawMeshInstanced(_mesh, 0, _mat, _m, 1, _mpb, ShadowCastingMode.Off, false, layer);
        }

        public void Draw(float bloomT, float clock, float scale, int layer)
        {
            if (_n == 0 || !SystemInfo.supportsInstancing) return; // null device (smoke player): nothing to draw
            if (!_dataSet)
            {
                _mpb.SetVectorArray(BloomDataId, _data);
                _dataSet = true;
            }
            _mpb.SetFloat(BloomTId, bloomT);
            _mpb.SetFloat(ClockId, clock);
            _mpb.SetFloat(ScaleId, scale);
            Graphics.DrawMeshInstanced(_mesh, 0, _mat, _m, _n, _mpb, ShadowCastingMode.Off, false, layer);
        }

        /// <summary>A grown bloom head a bee can land on: instance i, slot b. False if that slot is off or not out yet.</summary>
        public bool Head(int i, int b, float bloomT, float clock, out Vector3 world)
        {
            world = default;
            if (i < 0 || i >= _n) return false;
            var d = _data[i];
            if (b < 0 || b >= Slots.Length || (((int)d.w) & (1 << b)) == 0) return false;
            if (bloomT < d.x + Slots[b].birth + GrowSeconds) return false;
            Vector3 local = BloomMeshes.Sway(Slots[b].head, clock, d.z, 1f) + Vector3.up * 0.012f;
            world = _m[i].MultiplyPoint3x4(local);
            return true;
        }

        public Vector3 Position(int i) => i >= 0 && i < _n ? (Vector3)_m[i].GetColumn(3) : Vector3.zero;
    }
}
