using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.FlowerLife
{
    /// <summary>
    /// A few bees (flowerlife.bees, 6; comfort mode half) that work the garden: out of the hive door, an arcing hop to a
    /// healthy bloom (it follows the head's sway and the bloom dips under the bee), 1.6-3.6 s perched with still wings,
    /// two to four blooms, then home. Only healthy blooms attract them, so the visits start with the first solved
    /// station and get busier as the garden heals. A fingertip within 14 cm sends a perched bee off. Never within
    /// 0.5 m of the player's face. One instanced draw with the animated or static worker bee.
    /// </summary>
    public sealed class FlowerBees
    {
        public const int Max = 10;
        const float BeeLength = 0.035f, FaceClear = 0.5f, Speed = 0.55f;

        enum State : byte { Home, Flying, Perched, Returning }

        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int WingFlapId = Shader.PropertyToID("_WingFlap");

        Mesh _mesh;
        Material _mat;
        readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        readonly Matrix4x4[] _m = new Matrix4x4[Max];
        readonly Vector4[] _col = new Vector4[Max];
        readonly float[] _flap = new float[Max], _flapDraw = new float[Max];
        readonly Vector3[] _pos = new Vector3[Max], _from = new Vector3[Max];
        readonly State[] _state = new State[Max];
        readonly int[] _inst = new int[Max], _left = new int[Max];
        readonly float[] _t0 = new float[Max], _dur = new float[Max], _until = new float[Max], _yaw = new float[Max];
        readonly System.Random _rnd = new System.Random(23);
        readonly bool _instancing;
        int _n;

        public int Visits { get; private set; }
        public int Count => _n;

        public FlowerBees()
        {
            _mesh = BeeVat.AuthoredWorkerMesh() ?? GameMeshes.CreateBeeMesh();
            _mat = GameMaterials.Create("VBS_FlowerBee (runtime)", Color.white, vertexColors: true, doubleSided: true);
            _mat.enableInstancing = true;
            WingClock.EnableOn(_mat, _mesh);
            BeeVat.Apply(ref _mesh, ref _mat, "flower visitors");
            _instancing = SystemInfo.supportsInstancing;
            for (int i = 0; i < Max; i++) _col[i] = new Vector4(1f, 0.8f, 0.28f, 1f);
        }

        public void Begin(int n)
        {
            _n = Mathf.Clamp(n, 0, Max);
            Visits = 0;
            for (int i = 0; i < _n; i++)
            {
                _state[i] = State.Home;
                _inst[i] = -1;
                _until[i] = Time.time + 1.5f + i * 0.9f;
            }
        }

        public void Clear()
        {
            _n = 0;
        }

        public void Tick(FlowerLifeDirector life, Vector3 door, Vector3 face, float now, float dt, int layer)
        {
            if (_n == 0 || life.Count == 0 || _mesh == null || _mat == null) return;
            bool h0 = GameHands.TryGetIndexTip(GameHand.Left, out var hand0), h1 = GameHands.TryGetIndexTip(GameHand.Right, out var hand1);
            int shown = 0;
            for (int i = 0; i < _n; i++)
            {
                Quaternion rot = Quaternion.Euler(0f, _yaw[i], 0f);
                switch (_state[i])
                {
                    case State.Home:
                        if (now < _until[i]) continue;
                        _pos[i] = door;
                        _left[i] = 2 + _rnd.Next(3);
                        if (!Pick(i, life, face, now)) { _until[i] = now + 1.5f + (float)_rnd.NextDouble(); continue; }
                        break;
                    case State.Flying:
                    case State.Returning:
                    {
                        bool home = _state[i] == State.Returning;
                        if (!home && !life.Landable(_inst[i]))
                        {
                            // the bloom wilted (bad news, the Moon) while the bee was on its way
                            if (!Pick(i, life, face, now)) GoHome(i, door, now);
                            home = _state[i] == State.Returning;
                        }
                        Vector3 target = home ? door : life.Head(_inst[i]) + Vector3.up * 0.006f;
                        float k = Mathf.Clamp01((now - _t0[i]) / Mathf.Max(0.1f, _dur[i]));
                        float e = k * k * (3f - 2f * k);
                        float lift = Mathf.Clamp(Vector3.Distance(_from[i], target) * 0.45f, 0.04f, 0.3f) + 0.02f * (i % 3);
                        Vector3 mid = (_from[i] + target) * 0.5f + Vector3.up * lift;
                        Vector3 p = Vector3.Lerp(Vector3.Lerp(_from[i], mid, e), Vector3.Lerp(mid, target, e), e);
                        p += new Vector3(Mathf.Sin(now * 7f + i), Mathf.Sin(now * 9f + i * 2f), Mathf.Cos(now * 8f + i)) * (0.008f * (1f - e));
                        Vector3 vel = p - _pos[i];
                        _pos[i] = p;
                        if (vel.sqrMagnitude > 1e-9f)
                        {
                            rot = Quaternion.LookRotation(new Vector3(vel.x, vel.y * 0.4f, vel.z), Vector3.up);
                            _yaw[i] = rot.eulerAngles.y;
                        }
                        _flap[i] = 1f;
                        if (k >= 1f)
                        {
                            if (home)
                            {
                                _state[i] = State.Home;
                                _until[i] = now + (life.Escaped ? 0.6f : 2f) + (float)_rnd.NextDouble() * (life.Escaped ? 1.5f : 4f);
                                continue;
                            }
                            _state[i] = State.Perched;
                            _until[i] = now + 1.6f + (float)_rnd.NextDouble() * 2f;
                            life.SetLoad(_inst[i], 1f);
                            Visits++;
                        }
                        break;
                    }
                    case State.Perched:
                    {
                        Vector3 head = life.Head(_inst[i]) + Vector3.up * 0.006f;
                        _pos[i] = head;
                        rot = Quaternion.Euler(0f, _yaw[i] + Mathf.Sin(now * 0.9f + i) * 30f, 0f);
                        _flap[i] = 0f;
                        bool shooed = (h0 && (hand0 - head).sqrMagnitude < 0.14f * 0.14f) || (h1 && (hand1 - head).sqrMagnitude < 0.14f * 0.14f);
                        if (shooed || now >= _until[i] || !life.Landable(_inst[i]))
                        {
                            life.SetLoad(_inst[i], 0f);
                            _left[i]--;
                            if (_left[i] <= 0 || !Pick(i, life, face, now)) GoHome(i, door, now);
                        }
                        break;
                    }
                }
                // the face bubble
                Vector3 off = _pos[i] - face;
                float d = off.magnitude;
                if (d < FaceClear) _pos[i] = face + (d > 1e-4f ? off / d : Vector3.forward) * FaceClear;
                _m[shown] = Matrix4x4.TRS(_pos[i], rot, new Vector3(BeeLength, BeeLength, BeeLength));
                _flapDraw[shown] = _flap[i];
                shown++;
            }
            if (shown == 0 || !_instancing) return;
            _mpb.SetVectorArray(ColorId, _col);
            _mpb.SetFloatArray(WingFlapId, _flapDraw);
            Graphics.DrawMeshInstanced(_mesh, 0, _mat, _m, shown, _mpb, ShadowCastingMode.Off, false, layer);
        }

        void GoHome(int i, Vector3 door, float now)
        {
            if (_inst[i] >= 0) _inst[i] = -1;
            _state[i] = State.Returning;
            _from[i] = _pos[i];
            _t0[i] = now;
            _dur[i] = Mathf.Clamp(Vector3.Distance(_pos[i], door) / Speed, 0.5f, 4f);
        }

        bool Pick(int i, FlowerLifeDirector life, Vector3 face, float now)
        {
            for (int tries = 0; tries < 16; tries++)
            {
                int inst = _rnd.Next(life.Count);
                if (!life.Landable(inst)) continue;
                // the table, its rim and the window sill first (where the player looks); the floor now and then
                if (tries < 11 && life.ZoneOf(inst) == FlowerLifeDirector.Zone.Floor) continue;
                bool taken = false;
                for (int j = 0; j < _n && !taken; j++) taken = j != i && _inst[j] == inst && _state[j] != State.Home;
                if (taken) continue;
                Vector3 head = life.Head(inst);
                if (Vector3.Distance(head, face) < FaceClear + 0.08f) continue;
                _inst[i] = inst;
                _state[i] = State.Flying;
                _from[i] = _pos[i];
                _t0[i] = now;
                _dur[i] = Mathf.Clamp(Vector3.Distance(_pos[i], head) / Speed, 0.45f, 4f);
                return true;
            }
            return false;
        }
    }
}
