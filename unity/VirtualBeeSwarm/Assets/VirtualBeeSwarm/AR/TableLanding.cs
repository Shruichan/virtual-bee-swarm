using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.AR
{
    /// <summary>
    /// Bees land on the real table between beats (PLAN.md V10, the part that doesn't touch SwarmSystem): while the hive
    /// swarm is Idle or Calm, a small crew (16) takes turns leaving the hive, flying to a landing spot from the
    /// RoomModel (outside the hive/station/arc/rocket footprints), sitting still for 2-5 s and flying home. At the escape
    /// they all land (table and window-wall spots). When the swarm turns angry, hides or the beat ends, they fly home
    /// in 0.6 s and vanish. Landed bees get the contact depth bias per instance. One instanced draw, no allocations.
    /// Switch: ar.landing.
    /// </summary>
    public class TableLanding : MonoBehaviour
    {
        [SerializeField] int crew = 16;
        [SerializeField] float beeLength = 0.035f;
        [SerializeField] float flySpeed = 0.45f;
        [SerializeField] Vector2 sitSeconds = new Vector2(2f, 5f);
        [SerializeField] Vector2 waitSeconds = new Vector2(0.5f, 3.5f);
        [SerializeField] Color color = new Color(1f, 0.85f, 0.3f, 1f);
        [SerializeField] float brightness = 1.3f;

        const byte Home = 0, Out = 1, Landed = 2, Back = 3;
        const int MaxCrew = 32;

        ArRig _rig;
        SwarmSystem _swarm;
        HiveBody _hive;
        Mesh _mesh;
        Material _mat;
        MaterialPropertyBlock _mpb;
        int _n;
        byte[] _state;
        float[] _t0, _dur, _until, _yaw;
        short[] _spot; // >= 0 table spot, < 0 wall spot (-1 - i)
        Vector3[] _from, _to, _toNormal;
        Matrix4x4[] _matrices;
        Vector4[] _colors;
        float[] _bias;
        float[] _flap; // per-instance _WingFlap: 0 = landed (wings still), 1 = flying
        static readonly int WingFlapId = Shader.PropertyToID("_WingFlap");
        System.Random _rnd = new System.Random(4242);
        int _nextSpot;
        bool _escapeLanding;
        static readonly int ColorId = Shader.PropertyToID("_Color");

        public int LandedCount { get; private set; }
        public int FlyingCount { get; private set; }
        public int TotalLandings { get; private set; }
        public bool Gate { get; private set; }

        public void Init(ArRig rig, SwarmSystem swarm, HiveBody hive)
        {
            _rig = rig;
            _swarm = swarm;
            _hive = hive;
            _n = Mathf.Clamp(crew, 1, MaxCrew);
            _state = new byte[_n];
            _t0 = new float[_n];
            _dur = new float[_n];
            _until = new float[_n];
            _yaw = new float[_n];
            _spot = new short[_n];
            _from = new Vector3[_n];
            _to = new Vector3[_n];
            _toNormal = new Vector3[_n];
            _matrices = new Matrix4x4[_n];
            _colors = new Vector4[_n];
            _bias = new float[_n];
            _flap = new float[_n];
            for (int i = 0; i < _n; i++)
            {
                _until[i] = Time.time + Rand(waitSeconds) + i * 0.4f;
                _yaw[i] = (float)_rnd.NextDouble() * 360f;
            }
            _mesh = GameMeshes.CreateBeeMesh();
            _mat = GameMaterials.Create("VBS_LandingBee (runtime)", Color.white, vertexColors: true, doubleSided: true);
            _mat.enableInstancing = true;
            WingClock.EnableOn(_mat, _mesh); // wings flap in the shader; _WingFlap 0 stills the landed ones
            BeeVat.Apply(ref _mesh, ref _mat, "table landing"); // swarm.vat: Daniel's animated bee, Walk while landed (_WingFlap 0)
            _mpb = new MaterialPropertyBlock();
        }

        float Rand(Vector2 r) => r.x + (r.y - r.x) * (float)_rnd.NextDouble();

        /// <summary>Everyone home and hidden now (reset, Board jump).</summary>
        public void ResetAll()
        {
            if (_state == null) return;
            for (int i = 0; i < _n; i++) { _state[i] = Home; _until[i] = Time.time + Rand(waitSeconds); }
            _escapeLanding = false;
        }

        bool ComputeGate(out bool escape)
        {
            escape = false;
            if (!ArTuning.Landing || _rig == null || _rig.Room == null || _rig.Room.TableSpotCount == 0) return false;
            if (_hive == null || !_hive.isActiveAndEnabled) return false;
            if (_swarm == null || !_swarm.isActiveAndEnabled) return false;
            var d = _rig.Director;
            if (d == null) return false;
            var beat = d.Beat;
            if (beat == GameBeat.Pre || beat == GameBeat.T0 || beat == GameBeat.Map || beat == GameBeat.Field) return false;
            var mode = _swarm.Mode;
            if (beat == GameBeat.Escape && (mode == SwarmMode.PourOut || mode == SwarmMode.Calm || mode == SwarmMode.Idle))
            {
                escape = true;
                return true;
            }
            if (_hive.Held || _hive.LiftHeight > 0.02f) return false; // the feed station lifts the hive: everyone stays on
            return mode == SwarmMode.Idle || mode == SwarmMode.Calm;
        }

        void Update()
        {
            if (_state == null) return;
            float now = Time.time;
            Gate = ComputeGate(out bool escape);
            if (escape && !_escapeLanding) { _escapeLanding = true; for (int i = 0; i < _n; i++) if (_state[i] == Home) _until[i] = now + i * 0.08f; }
            if (!escape) _escapeLanding = false;
            Vector3 home = _hive != null ? _hive.SwarmCenter.position : transform.position;
            var room = _rig != null ? _rig.Room : null;

            int landed = 0, flying = 0, drawn = 0;
            for (int i = 0; i < _n; i++)
            {
                byte s = _state[i];
                if (!Gate && (s == Out || s == Landed))
                {
                    StartFlight(i, CurrentPos(i, now), 0.6f, Back);
                    s = Back;
                }
                switch (s)
                {
                    case Home:
                        if (Gate && now >= _until[i] && (escape || (i & 3) == ((int)(now * 0.5f) & 3) || _rnd.NextDouble() < 0.02))
                        {
                            if (PickSpot(room, escape, i, out var p, out var nrm))
                            {
                                _to[i] = p;
                                _toNormal[i] = nrm;
                                StartFlight(i, home, Mathf.Clamp(Vector3.Distance(home, p) / flySpeed, 0.8f, 2.2f), Out);
                            }
                            else _until[i] = now + 1f;
                        }
                        break;
                    case Out:
                        if (now >= _t0[i] + _dur[i])
                        {
                            _state[i] = Landed;
                            _until[i] = escape ? float.MaxValue : now + Rand(sitSeconds);
                            TotalLandings++;
                        }
                        break;
                    case Landed:
                        if (now >= _until[i]) StartFlight(i, _to[i], Mathf.Clamp(Vector3.Distance(home, _to[i]) / flySpeed, 0.8f, 2.2f), Back);
                        break;
                    case Back:
                        if (now >= _t0[i] + _dur[i])
                        {
                            _state[i] = Home;
                            _until[i] = now + Rand(waitSeconds);
                        }
                        break;
                }
                s = _state[i];
                if (s == Home) continue;
                if (s == Landed) landed++; else flying++;
                Pose(i, now, s, out var pos, out var rot);
                _matrices[drawn] = Matrix4x4.TRS(pos, rot, new Vector3(beeLength, beeLength, beeLength));
                _colors[drawn] = color * brightness;
                _colors[drawn].w = 1f;
                _bias[drawn] = s == Landed ? ArTuning.DepthBiasContact : 0f;
                _flap[drawn] = s == Landed ? 0f : 1f;
                drawn++;
            }
            LandedCount = landed;
            FlyingCount = flying;
            if (drawn == 0) return;
            _mpb.SetVectorArray(ColorId, _colors);
            _mpb.SetFloatArray(ArMaterials.BiasId, _bias);
            _mpb.SetFloatArray(WingFlapId, _flap);
            if (SystemInfo.supportsInstancing)
                Graphics.DrawMeshInstanced(_mesh, 0, _mat, _matrices, drawn, _mpb, ShadowCastingMode.Off, false, gameObject.layer);
            else
                for (int i = 0; i < drawn; i++)
                {
                    _mpb.SetColor(ColorId, _colors[i]);
                    _mpb.SetFloat(WingFlapId, _flap[i]);
                    Graphics.DrawMesh(_mesh, _matrices[i], _mat, gameObject.layer, null, 0, _mpb, ShadowCastingMode.Off, false);
                }
        }

        bool PickSpot(RoomModel room, bool escape, int bee, out Vector3 p, out Vector3 n)
        {
            p = default;
            n = Vector3.up;
            if (room == null) return false;
            int total = room.TableSpotCount + (escape ? room.WallSpotCount : 0);
            if (total == 0) return false;
            for (int tries = 0; tries < total; tries++)
            {
                int k = (_nextSpot++) % total;
                if (SpotTaken(k, bee)) continue;
                _spot[bee] = (short)(k < room.TableSpotCount ? k : -1 - (k - room.TableSpotCount));
                if (k < room.TableSpotCount) { p = room.TableSpots[k] + Vector3.up * 0.006f; n = Vector3.up; }
                else { int w = k - room.TableSpotCount; p = room.WallSpots[w]; n = room.WallSpotNormals[w]; }
                return true;
            }
            return false;
        }

        bool SpotTaken(int k, int except)
        {
            var room = _rig.Room;
            short code = (short)(k < room.TableSpotCount ? k : -1 - (k - room.TableSpotCount));
            for (int i = 0; i < _n; i++)
                if (i != except && _state[i] != Home && _spot[i] == code) return true;
            return false;
        }

        void StartFlight(int i, Vector3 from, float seconds, byte state)
        {
            _from[i] = from;
            _t0[i] = Time.time;
            _dur[i] = seconds;
            _state[i] = state;
        }

        Vector3 CurrentPos(int i, float now)
        {
            Pose(i, now, _state[i], out var p, out _);
            return p;
        }

        void Pose(int i, float now, byte s, out Vector3 pos, out Quaternion rot)
        {
            if (s == Landed)
            {
                pos = _to[i];
                Vector3 n = _toNormal[i];
                Vector3 tangent = Quaternion.AngleAxis(_yaw[i], n) * (Mathf.Abs(n.y) > 0.9f ? Vector3.forward : Vector3.up);
                tangent = Vector3.ProjectOnPlane(tangent, n);
                // wings still (_WingFlap 0); a tiny walk-turn now and then
                float turn = Mathf.Sin(now * 0.7f + i) * 8f;
                rot = Quaternion.LookRotation(Quaternion.AngleAxis(turn, n) * tangent.normalized, n);
                pos += n * (beeLength * 0.18f);
                return;
            }
            Vector3 a = _from[i];
            Vector3 b = s == Back ? (_hive != null ? _hive.SwarmCenter.position : transform.position) : _to[i];
            float u = Mathf.Clamp01((now - _t0[i]) / Mathf.Max(0.05f, _dur[i]));
            float e = u * u * (3f - 2f * u);
            Vector3 mid = (a + b) * 0.5f + Vector3.up * (0.08f + 0.15f * Vector3.Distance(a, b));
            Vector3 p0 = Vector3.Lerp(a, mid, e), p1 = Vector3.Lerp(mid, b, e);
            pos = Vector3.Lerp(p0, p1, e);
            pos += new Vector3(Mathf.Sin(now * 13f + i), Mathf.Sin(now * 11f + i * 2f), Mathf.Cos(now * 12f + i)) * 0.006f;
            Vector3 vel = p1 - p0;
            rot = vel.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(vel.normalized, Vector3.up) : Quaternion.identity;
        }
    }
}
