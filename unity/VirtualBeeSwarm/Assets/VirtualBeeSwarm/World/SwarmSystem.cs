using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm
{
    public enum SwarmMode
    {
        Idle,    // orbit around the hive
        Angry,   // fast, wide, red, dive-bombs near the player
        Calm,    // slow and close; some forage out toward the window
        Guard,   // a few guards hold an arc in front of the door, the rest calm
        PourOut, // escape: stream out of the door and fill the room
        Ball,    // hornet: everyone balls up around the target
        Hidden,  // not drawn
    }

    /// <summary>
    /// 120-180 GPU-instanced bees (one Graphics.DrawMeshInstanced call, per-instance color), boids-lite steering:
    /// each bee chases its own moving anchor for the current mode, plus cheap separation from two fixed neighbours.
    /// No per-frame allocations, no colliders, no shadows. ~0.1 ms CPU for 150 bees.
    /// Reacts to fx swarm_calm / swarm_angry by itself.
    /// </summary>
    public class SwarmSystem : MonoBehaviour
    {
        [SerializeField] GameNet net;
        [Range(20, 300)] [SerializeField] int beeCount = 150;
        [SerializeField] Mesh beeMesh;
        [Tooltip("Instanced, unlit, vertex colors on (VirtualBeeSwarm/Unlit). Per-instance _Color tints it.")]
        [SerializeField] Material beeMaterial;
        [SerializeField] float beeLength = 0.035f;

        [Header("Anchors")]
        [SerializeField] HiveBody hive;
        [Tooltip("Foraging bees fly toward this (the future window). Optional.")]
        [SerializeField] Transform forageTarget;
        [Tooltip("The player's head (empty = main camera).")]
        [SerializeField] Transform head;

        [Header("Colors")]
        [SerializeField] Color idleColor = new Color(1f, 0.85f, 0.3f, 1f);
        [SerializeField] Color angryColor = new Color(1f, 0.35f, 0.15f, 1f);
        [SerializeField] Color calmColor = new Color(1f, 0.93f, 0.6f, 1f);
        [SerializeField] Color guardColor = new Color(1f, 0.65f, 0.1f, 1f);
        [SerializeField] Color ballHotColor = new Color(1f, 0.2f, 0.05f, 1f);
        [SerializeField] float brightness = 1.3f;

        public SwarmMode Mode { get; private set; } = SwarmMode.Idle;
        /// <summary>0..1 extra agitation on top of the mode (bad choices spike it).</summary>
        public float Agitation { get; set; }
        /// <summary>0..1 heat of the hornet ball (thermometer), tints the ball.</summary>
        public float BallHeat { get; set; }
        public int GuardCount { get; private set; } = 12;
        public int Count => _n;

        const int MaxBees = 640; // meadow: swarm.count (tuning.json) up to 640, one instanced draw
        int _n;
        Vector3[] _pos, _vel, _dir;
        float[] _phase, _radius, _height, _speed;
        Matrix4x4[] _matrices;
        Vector4[] _colors;
        Bloom.BeeLod _lod; // meadow
        MaterialPropertyBlock _mpb;
        static readonly int ColorArrayId = Shader.PropertyToID("_Color");
        float _modeSince, _pourStart;
        bool _instancing;
        float _pull = 1f; // v3.1: the orbit pulls in 30% while BeeDelivery has a flight in the air (not when Angry)
        Transform _ballTarget;
        Vector3 _ballPoint;

        void Awake()
        {
            if (net == null) net = FindAnyObjectByType<GameNet>();
            if (hive == null) hive = FindAnyObjectByType<HiveBody>();
            if (beeMesh == null) beeMesh = GameMeshes.CreateBeeMesh();
            if (beeMaterial == null)
                beeMaterial = GameMaterials.Create("VBS_Bee (runtime)", Color.white, vertexColors: true, doubleSided: true);
            beeMaterial.enableInstancing = true;
            WingClock.EnableOn(beeMaterial, beeMesh); // wings flap in the shader (Shaders/GameUnlit.shader)
            _lod = Bloom.BeeLod.Make(beeMesh, beeMaterial); // meadow: far bees low-poly (Quest budget)
            BeeVat.Apply(ref beeMesh, ref beeMaterial, "swarm"); // swarm.vat: Daniel's animated bee (BeeVAT/), replaces the flap above
            _instancing = SystemInfo.supportsInstancing;
            if (!_instancing) Debug.LogWarning("[VBS] GPU instancing not supported here: bees drawn one by one.");
            Allocate();
            Scatter();
        }

        void OnEnable() { if (net != null) net.FxReceived += HandleFx; }
        void OnDisable() { if (net != null) net.FxReceived -= HandleFx; }

        void HandleFx(string name, int delayMs, int durationMs)
        {
            if (name == GameIds.Fx.SwarmCalm) SetMode(SwarmMode.Calm);
            else if (name == GameIds.Fx.SwarmAngry) SetMode(SwarmMode.Angry);
        }

        void Allocate()
        {
            Bloom.MeadowTuning.EnsureLoaded(); // meadow: swarm.count fills the room
            _n = Mathf.Clamp(Mathf.Max(beeCount, Bloom.MeadowTuning.SwarmCount), 1, MaxBees);
            _pos = new Vector3[_n];
            _vel = new Vector3[_n];
            _dir = new Vector3[_n];
            _phase = new float[_n];
            _radius = new float[_n];
            _height = new float[_n];
            _speed = new float[_n];
            _matrices = new Matrix4x4[_n];
            _colors = new Vector4[_n];
            _mpb = new MaterialPropertyBlock();
            var rnd = new System.Random(1234);
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < _n; i++)
            {
                _phase[i] = (float)rnd.NextDouble() * Mathf.PI * 2f;
                _radius[i] = (float)rnd.NextDouble();
                _height[i] = (float)rnd.NextDouble();
                _speed[i] = 0.75f + 0.5f * (float)rnd.NextDouble();
                // Fibonacci sphere: an even spread of directions for the ball / the pour-out
                float y = 1f - (i + 0.5f) / _n * 2f;
                float r = Mathf.Sqrt(1f - y * y);
                float th = golden * i;
                _dir[i] = new Vector3(Mathf.Cos(th) * r, y, Mathf.Sin(th) * r);
            }
        }

        /// <summary>Put every bee near the hive (reset / after placement).</summary>
        public void Scatter()
        {
            Vector3 c = Center();
            for (int i = 0; i < _n; i++)
            {
                _pos[i] = c + _dir[i] * (0.15f + 0.2f * _radius[i]);
                _vel[i] = Vector3.zero;
            }
        }

        public void SetMode(SwarmMode mode)
        {
            if (mode == Mode) return;
            Mode = mode;
            _modeSince = Time.time;
            if (mode == SwarmMode.PourOut) _pourStart = Time.time;
            if (mode == SwarmMode.Hidden) Scatter();
        }

        public void SetGuardCount(int n) => GuardCount = Mathf.Clamp(n, 0, _n);

        public void SetBallTarget(Transform t) => _ballTarget = t;
        public void SetBallPoint(Vector3 p) { _ballTarget = null; _ballPoint = p; }

        /// <summary>A bad choice: they get angrier for a while.</summary>
        public void Spike(float amount = 1f) => Agitation = Mathf.Clamp01(Agitation + amount);

        public void ResetSwarm()
        {
            Agitation = 0f;
            BallHeat = 0f;
            GuardCount = 12;
            _ballTarget = null;
            Mode = SwarmMode.Idle;
            Scatter();
        }

        Vector3 Center() => hive != null ? hive.SwarmCenter.position : transform.position;

        Transform Head()
        {
            if (head != null) return head;
            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }

        /// <summary>Meadow: which bees roam the room (a third; comfort mode: one in ten).</summary>
        static bool Roams(int i) => Stakes.StakesFlags.Comfort ? i % 10 == 1 : i % 3 == 1;

        /// <summary>Meadow: a wide lazy loop through the room round the hive (1-2.8 m out, 0.25-2.1 m above the floor),
        /// clear of the player's head. One in three bees in Idle / Calm.</summary>
        Vector3 Roam(int i, float t, Vector3 center, Vector3 headPos)
        {
            float ph = _phase[i], sp = _speed[i];
            float a = ph + t * 0.22f * sp * ((i & 2) == 0 ? 1f : -1f);
            float r = 1.0f + 1.8f * _radius[i] + 0.3f * Mathf.Sin(t * 0.37f + ph * 3f);
            float floor = Bloom.MeadowLayout.FloorY;
            float y = floor + 0.25f + 1.85f * _height[i] + 0.25f * Mathf.Sin(t * 0.5f + ph * 2f);
            Vector3 p = new Vector3(center.x + Mathf.Cos(a) * r, y, center.z + Mathf.Sin(a) * r);
            var field = Bloom.MeadowField.Instance; // inside the room: never through a wall
            if (field != null && field.RoomRect(out var lo, out var hi))
            {
                p.x = Mathf.Clamp(p.x, lo.x + 0.3f, hi.x - 0.3f);
                p.z = Mathf.Clamp(p.z, lo.y + 0.3f, hi.y - 0.3f);
            }
            Vector3 off = p - headPos;
            if (off.sqrMagnitude < 0.8f * 0.8f) p.y = headPos.y + 0.75f; // over the head, not through it
            return p;
        }

        void Update()
        {
            if (Mode == SwarmMode.Hidden || _n == 0) return;
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            float t = Time.time;
            Agitation = Mathf.MoveTowards(Agitation, 0f, dt * 0.12f);
            var delivery = BeeDelivery.Instance;
            float pullTarget = delivery != null && delivery.InFlight > 0 && Mode != SwarmMode.Angry ? 0.7f : 1f;
            _pull = Mathf.MoveTowards(_pull, pullTarget, dt * 1.5f);

            Vector3 center = Center();
            Transform door = hive != null ? hive.DoorPoint : transform;
            Vector3 doorPos = door.position;
            Vector3 doorFwd = -door.forward; // the door faces the player (-Z of the hive)
            Vector3 doorRight = door.right;
            Transform headT = Head();
            Vector3 headPos = headT != null ? headT.position : center + new Vector3(0f, 0.5f, -0.7f);
            Vector3 ball = _ballTarget != null ? _ballTarget.position : _ballPoint;
            Vector3 forage = forageTarget != null ? forageTarget.position : center + new Vector3(-0.4f, 0.2f, 0f);
            float agit = Agitation;

            Color baseColor;
            float maxSpeed, accel;
            switch (Mode)
            {
                case SwarmMode.Angry: baseColor = angryColor; maxSpeed = 1.6f; accel = 9f; break;
                case SwarmMode.Calm: baseColor = calmColor; maxSpeed = 0.45f; accel = 3f; break;
                case SwarmMode.Guard: baseColor = calmColor; maxSpeed = 0.6f; accel = 5f; break;
                case SwarmMode.PourOut: baseColor = calmColor; maxSpeed = 1.1f; accel = 4f; break;
                case SwarmMode.Ball: baseColor = Color.Lerp(idleColor, ballHotColor, BallHeat); maxSpeed = 1.4f; accel = 14f; break;
                default: baseColor = idleColor; maxSpeed = 0.7f; accel = 4f; break;
            }
            baseColor = Color.Lerp(baseColor, angryColor, agit);
            maxSpeed *= 1f + agit;

            float _spread = Bloom.MeadowLayout.SwarmSpread; // meadow: orbit the scaled-up hive, not inside it
            float bl = beeLength * (_spread > 1.01f ? 1.4f : 1f); // meadow: bees that read across the room
            for (int i = 0; i < _n; i++)
            {
                float ph = _phase[i];
                float sp = _speed[i];
                Vector3 target;
                Color col = baseColor;

                switch (Mode)
                {
                    case SwarmMode.Angry:
                    {
                        float a = ph + t * 2.4f * sp;
                        float r = (0.35f + 0.5f * _radius[i]) * _spread;
                        target = center + new Vector3(Mathf.Cos(a) * r, -0.05f + 0.6f * _height[i] + Mathf.Sin(t * 3f + ph) * 0.1f, Mathf.Sin(a) * r);
                        // every 8th bee takes turns buzzing toward the player (stops halfway between hive and face)
                        if ((i & 7) == 0 && Mathf.Sin(t * 0.9f + ph) > 0.55f)
                        {
                            Vector3 toHead = headPos - center;
                            target = center + toHead * 0.5f + _dir[i] * 0.1f;
                        }
                        target += _dir[i] * (Mathf.Sin(t * 7f + ph * 3f) * 0.06f);
                        break;
                    }
                    case SwarmMode.Calm:
                    case SwarmMode.Guard:
                    {
                        if (Mode == SwarmMode.Guard && i < GuardCount)
                        {
                            float k = GuardCount <= 1 ? 0f : (float)i / (GuardCount - 1) - 0.5f;
                            target = doorPos + doorRight * (k * 0.1f) + doorFwd * (0.025f + 0.01f * Mathf.Sin(t * 5f + ph))
                                     + Vector3.up * (0.012f * Mathf.Sin(t * 9f + ph));
                            col = guardColor;
                            break;
                        }
                        if (Mode == SwarmMode.Calm && Bloom.MeadowLayout.Active && Roams(i)) { target = Roam(i, t, center, headPos); break; } // meadow
                        if ((i % 5) == 0)
                        {
                            // forager: hive -> window -> hive
                            float f = 0.5f - 0.5f * Mathf.Cos((t * 0.25f * sp + ph) % (Mathf.PI * 2f));
                            target = Vector3.Lerp(doorPos + doorFwd * 0.05f, forage, f) + _dir[i] * 0.04f;
                            break;
                        }
                        float a = ph + t * 0.5f * sp;
                        float r = (0.14f + 0.12f * _radius[i]) * _pull * _spread;
                        target = center + new Vector3(Mathf.Cos(a) * r, -0.02f + 0.2f * _height[i] + Mathf.Sin(t + ph) * 0.02f, Mathf.Sin(a) * r);
                        break;
                    }
                    case SwarmMode.PourOut:
                    {
                        float leaveAt = _pourStart + i * 0.025f;
                        if (t < leaveAt)
                        {
                            float a = ph + t * 0.6f * sp;
                            target = center + new Vector3(Mathf.Cos(a) * 0.12f * _spread, 0.05f, Mathf.Sin(a) * 0.12f * _spread);
                        }
                        else if (t < leaveAt + 0.5f)
                        {
                            target = doorPos + doorFwd * 0.1f;
                        }
                        else
                        {
                            float a = ph + t * 0.35f * sp;
                            float r = (0.6f + 1.0f * _radius[i]) * _spread;
                            target = center + new Vector3(Mathf.Cos(a) * r, 0.1f + 0.9f * _height[i] + Mathf.Sin(t * 0.7f + ph) * 0.1f, Mathf.Sin(a) * r);
                        }
                        break;
                    }
                    case SwarmMode.Ball:
                    {
                        float r = 0.06f + 0.035f * _radius[i];
                        target = ball + _dir[i] * r + _dir[(i + 31) % _n] * (0.008f * Mathf.Sin(t * 30f + ph));
                        break;
                    }
                    default:
                    {
                        if (Bloom.MeadowLayout.Active && Roams(i)) // meadow: a third of the swarm roams the whole room (comfort: 1 in 10)
                        {
                            target = Roam(i, t, center, headPos);
                            break;
                        }
                        float a = ph + t * 0.9f * sp;
                        float r = (0.18f + 0.17f * _radius[i]) * _pull * _spread;
                        target = center + new Vector3(Mathf.Cos(a) * r, -0.04f + 0.3f * _height[i] + Mathf.Sin(t * 1.3f + ph) * 0.04f, Mathf.Sin(a) * r);
                        break;
                    }
                }

                // steer: spring toward the target, damped; plus separation from two fixed neighbours
                Vector3 p = _pos[i];
                Vector3 v = _vel[i];
                Vector3 steer = (target - p) * accel - v * (accel * 0.35f);
                int j1 = (i + 1) % _n, j2 = (i + 13) % _n;
                Vector3 d1 = p - _pos[j1], d2 = p - _pos[j2];
                float s1 = d1.sqrMagnitude, s2 = d2.sqrMagnitude;
                if (s1 < 0.0009f && s1 > 1e-8f) steer += d1 * (0.6f / s1) * 0.01f;
                if (s2 < 0.0009f && s2 > 1e-8f) steer += d2 * (0.6f / s2) * 0.01f;
                v += steer * dt;
                float vm = v.magnitude;
                if (vm > maxSpeed) v *= maxSpeed / vm;
                p += v * dt;
                if (_spread > 1.01f) // meadow: no bee closer than 0.45 m to the face (comfort: 0.8 m)
                {
                    float clear = Stakes.StakesFlags.Comfort ? 0.8f : 0.45f;
                    Vector3 fo = p - headPos;
                    float fd = fo.magnitude;
                    if (fd < clear) p = headPos + (fd > 1e-4f ? fo / fd : Vector3.up) * clear;
                }
                _pos[i] = p;
                _vel[i] = v;

                Vector3 fwd = vm > 0.02f ? v / vm : (target - p);
                Quaternion rot = fwd.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fwd, Vector3.up) : Quaternion.identity;
                _matrices[i] = Matrix4x4.TRS(p, rot * InsectBodySway.Offset(Time.time, i * 2.4f, 0.55f), new Vector3(bl, bl, bl)); // wings flap in the shader

                float shade = 0.85f + 0.3f * _radius[i];
                _colors[i] = new Vector4(col.r * shade * brightness, col.g * shade * brightness, col.b * shade * brightness, 1f);
            }

            if (_instancing)
            {
                if (_lod == null || !_lod.Draw(beeMesh, beeMaterial, _matrices, _colors, _n, gameObject.layer)) // meadow: near VAT / far low-poly
                {
                _mpb.SetVectorArray(ColorArrayId, _colors);
                Graphics.DrawMeshInstanced(beeMesh, 0, beeMaterial, _matrices, _n, _mpb, ShadowCastingMode.Off, false, gameObject.layer);
                }
            }
            else
            {
                // No GPU instancing (null device, very old GPU): one draw per bee, same look. MPB is copied per call.
                for (int i = 0; i < _n; i++)
                {
                    _mpb.SetColor(ColorArrayId, _colors[i]);
                    Graphics.DrawMesh(beeMesh, _matrices[i], beeMaterial, gameObject.layer, null, 0, _mpb, ShadowCastingMode.Off, false);
                }
            }
        }
    }
}
