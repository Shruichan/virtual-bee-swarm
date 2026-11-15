using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Pals
{
    /// <summary>
    /// Bees land on an open palm (part of Pals): hold a hand palm-up and still for a moment and four foragers fly in, land
    /// and walk about on it, wings flicking; close the hand (or turn it over, or lose tracking) and they buzz off. Beeswax
    /// says the forager fact once (room_pal_palm). Four instanced bees in one draw (the VAT bee when it loads), no lights,
    /// no physics. Hand tracking only (needs the palm); off-headset the ghost hand (F8) does it.
    /// </summary>
    public class PalBees : MonoBehaviour
    {
        const int N = 4;
        static readonly int ColorArrayId = Shader.PropertyToID("_Color");

        enum State { Off, Coming, Landed, Leaving }
        struct Bee
        {
            public Vector3 from, local, goal, pos;   // local: on the palm (x across, z along the fingers), metres
            public float delay, heading, nextGoal, flick, phase;
        }

        readonly Bee[] _b = new Bee[N];
        readonly Matrix4x4[] _m = new Matrix4x4[N];
        readonly Vector4[] _c = new Vector4[N];
        State _state;
        int _hand = -1;
        float _held, _at, _nextTry;
        bool _said;
        Vector3 _centre, _normal = Vector3.up, _fingers = Vector3.forward;
        Mesh _mesh, _fallback;
        Material _mat;
        MaterialPropertyBlock _mpb;
        bool _instancing, _built;
        GameNet _net;
        GameDirector _director;

        public bool Landed => _state == State.Landed;
        public int Landings { get; private set; }

        void Start()
        {
            _net = FindAnyObjectByType<GameNet>();
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
        }

        void Build()
        {
            _built = true;
            if (!BeeVat.Apply(ref _mesh, ref _mat, "palm bees"))
            {
                _mesh = BeeVat.AuthoredWorkerMesh();
                if (_mesh == null) _mesh = _fallback = GameMeshes.CreateBeeMesh();
                _mat = GameMaterials.Create("VBS_PalmBee (runtime)", Color.white, vertexColors: true, doubleSided: true);
                _mat.enableInstancing = true;
            }
            _mpb = new MaterialPropertyBlock();
            _instancing = SystemInfo.supportsInstancing;
        }

        void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
            if (_fallback != null) Destroy(_fallback);
        }

        void Update()
        {
            var hands = PalHands.Instance;
            if (hands == null) return;
            float t = Time.time, dt = Time.deltaTime;
            bool allowed = PalBeeswax.On && (_director == null || (_director.Beat != GameBeat.T0 && _director.Beat != GameBeat.Field));

            if (_state == State.Off)
            {
                if (!allowed || t < _nextTry) return;
                int best = -1;
                for (int h = 0; h < 2; h++)
                {
                    var p = hands.Hands[h];
                    if (PalmUp(p)) { best = h; break; }
                }
                if (best < 0 || (_hand >= 0 && best != _hand)) { _held = 0f; _hand = -1; return; }
                _hand = best;
                _held += dt;
                if (_held < 0.6f) return;
                Come(hands.Hands[best]);
                return;
            }

            var hand = _hand >= 0 ? hands.Hands[_hand] : default;
            bool keep = allowed && hand.tracked && hand.palm && !hand.fist && hand.normal.y > 0.2f;
            if (keep)
            {
                _centre = hand.centre;
                _normal = hand.normal;
                _fingers = hand.fingers;
            }
            if (_state != State.Leaving && !keep) Leave(hand.fist ? "fist" : !hand.tracked ? "hand lost" : "hand turned over");

            Quaternion frame = Quaternion.LookRotation(_fingers, _normal);
            float s = PalmScale();
            int n = 0;
            for (int i = 0; i < N; i++)
            {
                ref var b = ref _b[i];
                float age = t - _at - b.delay;
                Vector3 onPalm = _centre + frame * (b.local * s) + _normal * 0.004f;
                Quaternion face = frame * Quaternion.Euler(0f, b.heading, 0f);
                float sc = 0.02f;
                if (_state == State.Coming || (_state == State.Landed && age < 1.1f))
                {
                    if (age < 0f) continue;
                    float k = Mathf.Clamp01(age / 1.1f), e = k * k * (3f - 2f * k);
                    Vector3 ctrl = (b.from + onPalm) * 0.5f + Vector3.up * 0.18f;
                    Vector3 p = Bez(b.from, ctrl, onPalm, e) + Vector3.up * (Mathf.Sin(age * 20f + b.phase) * 0.006f * (1f - k));
                    Vector3 ahead = Bez(b.from, ctrl, onPalm, Mathf.Min(1f, e + 0.04f)) - p;
                    b.pos = p;
                    face = k < 0.92f && ahead.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(ahead, Vector3.up) : face;
                    sc *= Mathf.Clamp01(age / 0.15f);
                }
                else if (_state == State.Landed)
                {
                    // walk: a slow random walk inside the palm, turning toward the next spot
                    if (t >= b.nextGoal)
                    {
                        b.goal = new Vector3(Random.Range(-0.03f, 0.03f), 0f, Random.Range(-0.035f, 0.04f));
                        b.nextGoal = t + Random.Range(1.2f, 2.6f);
                    }
                    Vector3 d = b.goal - b.local;
                    if (d.sqrMagnitude > 0.003f * 0.003f)
                    {
                        float want = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                        b.heading = Mathf.MoveTowardsAngle(b.heading, want, 240f * dt);
                        if (Mathf.Abs(Mathf.DeltaAngle(b.heading, want)) < 35f) b.local = Vector3.MoveTowards(b.local, b.goal, 0.018f * dt);
                    }
                    if (t > b.flick) b.flick = t + Random.Range(1.5f, 4f);
                    b.pos = onPalm + _normal * (b.flick - t < 0.12f ? 0.003f : 0f);
                    face = frame * Quaternion.Euler(0f, b.heading, 0f);
                }
                else // leaving: straight up and out, shrinking
                {
                    float k = Mathf.Clamp01((t - _at - b.delay * 0.3f) / 0.9f);
                    if (k >= 1f) continue;
                    Vector3 to = b.from + Vector3.up * 0.4f;
                    b.pos = Vector3.Lerp(b.pos, to, 1f - Mathf.Exp(-dt * 3.5f)) + Random.insideUnitSphere * 0.002f;
                    face = Quaternion.LookRotation((to - b.pos).sqrMagnitude > 1e-6f ? to - b.pos : Vector3.up, Vector3.up);
                    sc *= 1f - k * k;
                }
                _m[n] = Matrix4x4.TRS(b.pos, face, new Vector3(sc, sc, sc));
                _c[n] = new Vector4(1f, 0.82f, 0.35f, 1f);
                n++;
            }
            if (_state == State.Coming && t - _at > 1.1f + 0.45f) _state = State.Landed;
            if (_state == State.Leaving && t - _at > 1.2f) { _state = State.Off; _nextTry = t + 1.5f; _held = 0f; _hand = -1; }
            if (n > 0) Draw(n);
        }

        bool PalmUp(PalHand p)
        {
            if (!p.tracked || !p.palm || !p.open || p.normal.y < 0.7f || p.vel.magnitude > 0.25f) return false;
            var cam = Camera.main;
            if (cam == null) return true;
            Vector3 rel = p.centre - cam.transform.position;
            return rel.y < -0.05f && rel.magnitude > 0.2f && rel.magnitude < 0.8f;
        }

        void Come(PalHand hand)
        {
            _state = State.Coming;
            _at = Time.time;
            _centre = hand.centre;
            _normal = hand.normal;
            _fingers = hand.fingers;
            Landings++;
            // they come from the hive's side (else from ahead), spread out
            var hive = FindAnyObjectByType<HiveBody>();
            Vector3 src = hive != null && hive.isActiveAndEnabled ? hive.transform.position + Vector3.up * 0.5f : _centre + _fingers * 0.9f + Vector3.up * 0.3f;
            for (int i = 0; i < N; i++)
            {
                ref var b = ref _b[i];
                b.from = src + Random.insideUnitSphere * 0.25f;
                b.local = new Vector3((i % 2 == 0 ? -1f : 1f) * Random.Range(0.008f, 0.025f), 0f, Random.Range(-0.03f, 0.035f));
                b.goal = b.local;
                b.heading = Random.Range(0f, 360f);
                b.delay = i * 0.28f;
                b.nextGoal = Time.time + 1.5f + b.delay + Random.Range(0f, 1f);
                b.flick = Time.time + Random.Range(2f, 3f);
                b.phase = Random.Range(0f, 6.28f);
                b.pos = b.from;
            }
            if (!_said && _net != null && !_net.IsSpeaking)
            {
                _said = true;
                _net.Say(PalLines.Palm);
            }
            GameSound.Cue("palm_bees_land", _centre);
            Debug.Log("[VBS][Pals] palm up (" + (hand.ghost ? "ghost" : ((GameHand)_hand).ToString().ToLowerInvariant()) + "): " + N + " bees fly in to land");
        }

        void Leave(string why)
        {
            if (_state == State.Leaving || _state == State.Off) return;
            _state = State.Leaving;
            _at = Time.time;
            for (int i = 0; i < N; i++) _b[i].from = _b[i].pos + Random.insideUnitSphere * 0.15f + Vector3.up * 0.1f;
            GameSound.Cue("palm_bees_leave", _centre);
            Debug.Log("[VBS][Pals] palm bees buzz off (" + why + ")");
        }

        /// <summary>Bigger hands (a scaled rig never scales the player's hand): 1.</summary>
        static float PalmScale() => 1f;

        void Draw(int n)
        {
            if (!_built) Build();
            if (_mesh == null || _mat == null) return;
            if (_instancing)
            {
                _mpb.SetVectorArray(ColorArrayId, _c);
                Graphics.DrawMeshInstanced(_mesh, 0, _mat, _m, n, _mpb, ShadowCastingMode.Off, false, gameObject.layer);
            }
            else
                for (int i = 0; i < n; i++)
                {
                    _mpb.SetColor(ColorArrayId, _c[i]);
                    Graphics.DrawMesh(_mesh, _m[i], _mat, gameObject.layer, null, 0, _mpb, ShadowCastingMode.Off, false);
                }
        }

        static Vector3 Bez(Vector3 a, Vector3 c, Vector3 b, float t)
        {
            float u = 1f - t;
            return u * u * a + 2f * u * t * c + t * t * b;
        }
    }
}
