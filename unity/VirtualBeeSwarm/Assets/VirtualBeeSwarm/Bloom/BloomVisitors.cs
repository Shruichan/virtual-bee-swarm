using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>
    /// A few gold bees (12, comfort 8) leave the hive door once the bloom is out and visit flowers: an arcing flight to a
    /// grown bloom head (it follows the head's sway), 2.5-5 s perched on the Walk clip (_WingFlap 0), then the next
    /// flower. One instanced draw with the BeeVAT bee (BeeVat.Apply, else the placeholder bee + shader flap). Never
    /// within 0.6 m of the player's face: targets closer than that are skipped and a flight is pushed out of the bubble.
    /// </summary>
    public sealed class BloomVisitors
    {
        public const int Max = 12, Comfort = 8;
        const float BeeLength = 0.035f, FaceClear = 0.6f;

        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int WingFlapId = Shader.PropertyToID("_WingFlap");

        Mesh _mesh;
        Material _mat;
        readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        readonly Matrix4x4[] _m = new Matrix4x4[Max];
        readonly Vector4[] _col = new Vector4[Max];
        readonly float[] _flap = new float[Max];
        readonly Vector3[] _pos = new Vector3[Max], _from = new Vector3[Max];
        readonly int[] _inst = new int[Max], _slot = new int[Max];
        readonly float[] _t0 = new float[Max], _dur = new float[Max], _until = new float[Max], _yaw = new float[Max];
        readonly bool[] _perched = new bool[Max], _out = new bool[Max];
        readonly System.Random _rnd = new System.Random(5);
        int _n;
        bool _instancing;
        public int Visits { get; private set; }
        public int TrisPerBee { get; private set; }
        public int Count => _n;
        public float MinFace { get; private set; } = float.PositiveInfinity;

        public BloomVisitors()
        {
            _mesh = GameMeshes.CreateBeeMesh();
            _mat = GameMaterials.Create("VBS_BloomBee (runtime)", Color.white, vertexColors: true, doubleSided: true);
            _mat.enableInstancing = true;
            WingClock.EnableOn(_mat, _mesh);
            BeeVat.Apply(ref _mesh, ref _mat, "bloom visitors");
            _instancing = SystemInfo.supportsInstancing;
            TrisPerBee = _mesh != null ? (int)(_mesh.GetIndexCount(0) / 3) : 0;
            for (int i = 0; i < Max; i++) _col[i] = new Vector4(1f, 0.8f, 0.28f, 1f);
        }

        /// <summary>Starts (or restarts) the visits: n bees leave `door` staggered from `startT` (bloom seconds).</summary>
        public void Begin(int n, Vector3 door, float startT)
        {
            _n = Mathf.Clamp(n, 0, Max);
            Visits = 0;
            MinFace = float.PositiveInfinity;
            for (int i = 0; i < _n; i++)
            {
                _pos[i] = door;
                _from[i] = door;
                _out[i] = false;
                _perched[i] = false;
                _inst[i] = -1;
                _until[i] = startT + 1.2f + i * 0.35f; // leaves the door at this bloom time
                _yaw[i] = (float)_rnd.NextDouble() * 360f;
            }
        }

        public void Clear() => _n = 0;

        public void Tick(FlowerField field, float bloomT, float clock, Vector3 face, int layer)
        {
            if (_n == 0 || field == null || field.Count == 0 || _mesh == null || _mat == null) return;
            int shown = 0;
            for (int i = 0; i < _n; i++)
            {
                if (!_out[i])
                {
                    if (bloomT < _until[i]) continue;
                    _out[i] = true;
                    if (!Pick(i, field, bloomT, clock, face)) { _out[i] = false; _until[i] = bloomT + 0.5f; continue; }
                }
                Vector3 target;
                bool ok = field.Head(_inst[i], _slot[i], bloomT, clock, out target);
                if (!ok) target = _pos[i];
                Quaternion rot;
                if (!_perched[i])
                {
                    float k = Mathf.Clamp01((bloomT - _t0[i]) / Mathf.Max(0.1f, _dur[i]));
                    float e = k * k * (3f - 2f * k);
                    Vector3 mid = (_from[i] + target) * 0.5f + Vector3.up * (0.25f + 0.1f * (i % 3));
                    Vector3 p = Vector3.Lerp(Vector3.Lerp(_from[i], mid, e), Vector3.Lerp(mid, target, e), e);
                    p += new Vector3(Mathf.Sin(clock * 7f + i), Mathf.Sin(clock * 9f + i * 2f), Mathf.Cos(clock * 8f + i)) * (0.012f * (1f - e));
                    Vector3 vel = p - _pos[i];
                    _pos[i] = p;
                    rot = vel.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(new Vector3(vel.x, vel.y * 0.4f, vel.z), Vector3.up) : Quaternion.Euler(0f, _yaw[i], 0f);
                    _flap[i] = 1f;
                    if (k >= 1f)
                    {
                        _perched[i] = true;
                        _until[i] = bloomT + 2.5f + (float)_rnd.NextDouble() * 2.5f;
                        Visits++;
                    }
                }
                else
                {
                    _pos[i] = target;
                    rot = Quaternion.Euler(0f, _yaw[i] + Mathf.Sin(clock * 0.7f + i) * 25f, 0f);
                    _flap[i] = 0f; // Walk clip, wings still
                    if (bloomT >= _until[i])
                    {
                        _perched[i] = false;
                        _from[i] = _pos[i];
                        if (!Pick(i, field, bloomT, clock, face)) { _out[i] = false; _until[i] = bloomT + 1f; }
                    }
                }
                // the face bubble
                Vector3 off = _pos[i] - face;
                float d = off.magnitude;
                if (d < MinFace) MinFace = d; // measured before the push-out, so it's honest
                if (d < FaceClear) _pos[i] = face + (d > 1e-4f ? off / d : Vector3.forward) * FaceClear;
                _m[shown] = Matrix4x4.TRS(_pos[i], rot, new Vector3(BeeLength, BeeLength, BeeLength));
                _col[shown] = new Vector4(1f, 0.8f, 0.28f, 1f);
                _flap[shown] = _flap[i];
                shown++;
            }
            if (shown == 0 || !_instancing) return;
            _mpb.SetVectorArray(ColorId, _col);
            _mpb.SetFloatArray(WingFlapId, _flap);
            Graphics.DrawMeshInstanced(_mesh, 0, _mat, _m, shown, _mpb, ShadowCastingMode.Off, false, layer);
        }

        bool Pick(int i, FlowerField field, float bloomT, float clock, Vector3 face)
        {
            for (int tries = 0; tries < 12; tries++)
            {
                int inst = _rnd.Next(field.Count);
                // prefer the nearer half of the field (around the hive, where the player looks)
                if (tries < 8 && inst > field.Count / 2) inst = _rnd.Next(Mathf.Max(1, field.Count / 2));
                int slot = _rnd.Next(field.SlotCount);
                if (!field.Head(inst, slot, bloomT, clock, out var head)) continue;
                if (Vector3.Distance(head, face) < FaceClear + 0.1f) continue;
                Vector2 flat = new Vector2(head.x - face.x, head.z - face.z);
                if (flat.magnitude < BloomSurfaces.FeetRadius + 0.3f) continue;
                _inst[i] = inst;
                _slot[i] = slot;
                _t0[i] = bloomT;
                _dur[i] = Mathf.Clamp(Vector3.Distance(_pos[i], head) / 0.9f, 0.8f, 4f);
                _from[i] = _pos[i];
                _yaw[i] = (float)_rnd.NextDouble() * 360f;
                return true;
            }
            return false;
        }
    }
}
