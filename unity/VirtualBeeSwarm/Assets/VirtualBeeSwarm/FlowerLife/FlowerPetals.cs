using UnityEngine;
using UnityEngine.Rendering;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.FlowerLife
{
    /// <summary>
    /// Petals and pollen in the air (one instanced draw of a 2-tri kite, opaque, the game's unlit shader so the Depth
    /// API occludes them): petals blow out of the meadow window into the room (a few at first, more with every solved
    /// station, a stream at the escape), tumble, drift on the room's gust, and come to rest on the real table or floor
    /// for a few seconds; blooms that perk up pop three petals; healthy blooms give off gold pollen that floats up.
    /// </summary>
    public sealed class FlowerPetals
    {
        public const int Max = 80;

        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly Color[] Palette =
        {
            new Color(1f, 0.6f, 0.2f), new Color(0.98f, 0.72f, 0.84f), new Color(0.74f, 0.62f, 0.95f), new Color(0.6f, 0.5f, 0.96f),
            new Color(0.62f, 0.74f, 1f), new Color(1f, 0.86f, 0.25f), new Color(1f, 1f, 0.97f), new Color(0.7f, 0.9f, 0.5f),
        };
        static readonly Color Pollen = new Color(1f, 0.86f, 0.3f);

        readonly Mesh _mesh;
        readonly Material _mat;
        readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        readonly Matrix4x4[] _m = new Matrix4x4[Max];
        readonly Vector4[] _col = new Vector4[Max], _colDraw = new Vector4[Max];
        readonly Vector3[] _pos = new Vector3[Max], _vel = new Vector3[Max], _axis = new Vector3[Max];
        readonly float[] _born = new float[Max], _life = new float[Max], _size = new float[Max], _spin = new float[Max], _restAt = new float[Max];
        readonly bool[] _alive = new bool[Max], _pollen = new bool[Max];
        readonly System.Random _rnd = new System.Random(31);
        readonly bool _instancing;
        float _windowDebt, _pollenDebt;
        int _next;

        public int Emitted { get; private set; }

        public FlowerPetals()
        {
            _mesh = FlowerLifeMeshes.BuildPetal();
            _mat = GameMaterials.Create("VBS_FlowerPetal (runtime)", Color.white, vertexColors: true, doubleSided: true);
            _mat.enableInstancing = true;
            _instancing = SystemInfo.supportsInstancing;
        }

        public void Clear()
        {
            for (int i = 0; i < Max; i++) _alive[i] = false;
            _windowDebt = _pollenDebt = 0f;
        }

        float R() => (float)_rnd.NextDouble();

        void Spawn(Vector3 at, Vector3 vel, Color c, float size, float life, bool pollen)
        {
            int i = _next;
            _next = (_next + 1) % Max;
            _alive[i] = true;
            _pollen[i] = pollen;
            _pos[i] = at;
            _vel[i] = vel;
            _axis[i] = new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f).normalized;
            _spin[i] = (pollen ? 40f : 140f) + R() * 220f;
            _born[i] = Time.time;
            _life[i] = life;
            _size[i] = size;
            _restAt[i] = -1f;
            _col[i] = new Vector4(c.r, c.g, c.b, 1f);
            Emitted++;
        }

        /// <summary>A bloom perked up: a few petals pop off it.</summary>
        public void Burst(Vector3 at, int n, int species)
        {
            Color c = Palette[Mathf.Clamp(species, 0, Palette.Length - 1)];
            for (int k = 0; k < n; k++)
                Spawn(at, new Vector3(R() - 0.5f, 0.5f + R() * 0.5f, R() - 0.5f) * 0.35f, c, 0.011f + R() * 0.006f, 2.2f + R() * 1.5f, false);
        }

        public void Tick(FlowerLifeDirector life, Renderer pane, bool breeze, int lit, bool escaped, float now, float dt, int layer)
        {
            // ---- the window's breeze
            if (breeze && pane != null)
            {
                _windowDebt += dt * (escaped ? 5f : 0.45f + 0.55f * lit);
                var b = pane.bounds;
                Transform pt = pane.transform;
                Vector3 outward = -pt.forward, right = pt.right;
                while (_windowDebt >= 1f)
                {
                    _windowDebt -= 1f;
                    Vector3 at = b.center + right * ((R() * 2f - 1f) * b.extents.magnitude * 0.45f) + Vector3.up * ((R() * 2f - 1f) * b.extents.y * 0.7f) + outward * 0.05f;
                    Vector3 vel = outward * (0.3f + R() * 0.35f) + Vector3.up * (R() * 0.22f) + right * ((R() - 0.5f) * 0.3f);
                    bool pollen = R() < 0.25f;
                    Spawn(at, pollen ? vel * 0.5f : vel, pollen ? Pollen : Palette[_rnd.Next(Palette.Length - 1)],
                          pollen ? 0.006f + R() * 0.003f : 0.016f + R() * 0.012f, pollen ? 5f + R() * 2f : 6f + R() * 3f, pollen);
                }
            }
            else _windowDebt = 0f;

            // ---- pollen off the healthy blooms
            if (lit > 0 || escaped)
            {
                _pollenDebt += dt * (escaped ? 3f : 0.45f * lit);
                while (_pollenDebt >= 1f)
                {
                    _pollenDebt -= 1f;
                    int inst = _rnd.Next(Mathf.Max(1, life.Count));
                    if (!life.Landable(inst)) continue;
                    Spawn(life.Head(inst), new Vector3((R() - 0.5f) * 0.05f, 0.05f + R() * 0.05f, (R() - 0.5f) * 0.05f), Pollen, 0.005f + R() * 0.003f, 3f + R() * 2f, true);
                }
            }

            // ---- move, rest, draw
            var rig = ArRig.Instance;
            var room = rig != null ? rig.Room : null;
            float floorY = room != null ? room.FloorY : 0f;
            Vector4 wind = life.Wind;
            int shown = 0;
            for (int i = 0; i < Max; i++)
            {
                if (!_alive[i]) continue;
                float age = now - _born[i];
                if (age >= _life[i]) { _alive[i] = false; continue; }
                Quaternion rot;
                if (_restAt[i] < 0f)
                {
                    float flutter = Mathf.Sin(now * 3.1f + i * 1.7f);
                    Vector3 acc = _pollen[i]
                        ? new Vector3(wind.x, 0.25f, wind.y) * 0.03f - _vel[i] * 0.8f
                        : new Vector3(wind.x * 0.07f + flutter * 0.12f, -0.22f, wind.y * 0.07f) - _vel[i] * 0.9f;
                    _vel[i] += acc * dt;
                    _pos[i] += _vel[i] * dt;
                    rot = Quaternion.AngleAxis(_spin[i] * age + i * 40f, _axis[i]);
                    if (!_pollen[i])
                    {
                        float ground = room != null && room.HasTable && room.InsideTable(_pos[i]) ? room.Table.Y : floorY;
                        if (_pos[i].y <= ground + 0.004f && _vel[i].y < 0f)
                        {
                            _pos[i].y = ground + 0.003f;
                            _restAt[i] = now;
                            _life[i] = Mathf.Min(_life[i], age + 3.5f);
                        }
                    }
                }
                else rot = Quaternion.Euler(0f, i * 47f, 0f);
                float left = _life[i] - age;
                float s = _size[i] * Mathf.Clamp01(age / 0.25f) * Mathf.Clamp01(left / 0.8f);
                _m[shown] = Matrix4x4.TRS(_pos[i], rot, new Vector3(s, s, s));
                _colDraw[shown] = _col[i];
                shown++;
            }
            if (shown == 0 || !_instancing) return;
            _mpb.SetVectorArray(ColorId, _colDraw);
            Graphics.DrawMeshInstanced(_mesh, 0, _mat, _m, shown, _mpb, ShadowCastingMode.Off, false, layer);
        }
    }
}
