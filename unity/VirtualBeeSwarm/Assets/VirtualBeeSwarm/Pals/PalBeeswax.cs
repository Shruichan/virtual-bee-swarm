using UnityEngine;
using VirtualBeeSwarm.Juice;

namespace VirtualBeeSwarm.Pals
{
    /// <summary>
    /// Beeswax plays with the player's hands (tuning pals.on, default on):
    ///   boop       a fingertip (or controller tip) pokes him: he's knocked back on a spring and reacts, a new variant each
    ///              time (giggle wiggle, "that's harassment, I'm filing", a spin, a blush), a line at most every 6 s.
    ///   wave       a raised hand waved side to side (3 turns): he waves back with his front leg.
    ///   high-five  a raised open palm held still, facing away from you: he flies over, slaps it (sparkle burst, squash),
    ///              then goes back to where the script had him.
    /// Never blocks the ride: nothing here waits on a beat; the high-five only starts when he's free (out, not talking, not
    /// riding anything) and drops out the moment the script orders him somewhere; lines only when he isn't talking, never
    /// in the hook/meet/escape/T-0. In-place moves are added on top of BeeswaxMotion's body offsets. Logs "[VBS][Pals] ...".
    /// </summary>
    [DefaultExecutionOrder(-4)] // after BeeswaxMotion (-5) wrote the body offsets, before BeeswaxController (0) applies them
    public class PalBeeswax : MonoBehaviour
    {
        public static PalBeeswax Instance { get; private set; }

        BeeswaxController _bw;
        BeeswaxRig _rig;
        GameNet _net;
        GameDirector _director;
        JuiceParticles _fx;
        IntroSequence _intro;
        Transform _legR, _legL;

        // additive body layer
        Vector3 _offset, _offVel, _wroteOffset, _baseOffset;
        Quaternion _wroteTilt = Quaternion.identity, _baseTilt = Quaternion.identity;
        Vector3 _wroteSquash = Vector3.one, _baseSquash = Vector3.one;
        bool _wrote;
        float _spinFrom = -1f, _wiggleFrom = -1f, _squashFrom = -1f;

        // boop
        readonly bool[] _touching = new bool[2];
        float _nextBoop;
        int _boops, _lastVariant = -1;

        // wave
        readonly float[] _waveX = new float[2], _waveExt = new float[2], _waveStart = new float[2];
        readonly int[] _waveDir = new int[2], _waveTurns = new int[2];
        float _nextWave, _waveBackFrom = -1f, _waveBackUntil = -1f;

        // high-five
        enum Five { Idle, Flying, Slap, Back }
        Five _five;
        int _fiveHand = -1;
        float _fiveHeld, _fiveAt, _nextFive, _slapAt, _retargetAt;
        Vector3 _fivePalm, _fiveNormal, _prevGoal;
        Transform _prevGoalT;
        bool _mine, _interrupted;

        float _nextLine;

        public static bool On { get; set; } = true;
        public int Boops => _boops;
        public int HighFives { get; private set; }
        public int WavesBack { get; private set; }

        void Awake() => Instance = this;

        void Start()
        {
            _bw = FindAnyObjectByType<BeeswaxController>();
            _rig = _bw != null ? _bw.GetComponentInChildren<BeeswaxRig>(true) : null;
            _net = FindAnyObjectByType<GameNet>();
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            _fx = FindAnyObjectByType<JuiceParticles>();
            if (_bw != null) _bw.Ordered += OnOrdered;
            Debug.Log("[VBS][Pals] on: boop, wave, high-five" + (_bw == null ? " (no Beeswax: off)" : "") + ", radius " + Radius().ToString("0.00") + " m");
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_bw != null) _bw.Ordered -= OnOrdered;
        }

        void OnOrdered(string what)
        {
            // the script sent him somewhere: a high-five in flight gives way at once
            if (_mine || _five == Five.Idle) return;
            if (what == "fly" || what == "present" || what == "home" || what == "attach" || what == "hide" || what == "snap" || what == "reset" || what == "climb")
            {
                _interrupted = true;
                _five = Five.Idle;
                Log("high-five dropped: the script ordered " + what);
            }
        }

        // ------------------------------------------------------------------ frame
        void Update()
        {
            if (_bw == null) return;
            var hands = PalHands.Instance;
            bool free = Free();
            if (On && hands != null && _bw.Visible && Allowed(false))
            {
                for (int h = 0; h < 2; h++)
                {
                    var hand = hands.Hands[h];
                    if (!hand.tracked) { _touching[h] = false; _waveTurns[h] = 0; continue; }
                    Boop(h, hand);
                    Wave(h, hand);
                    if (free) FiveWatch(h, hand);
                }
            }
            StepFive(hands);
            Body();
        }

        void LateUpdate()
        {
            // leg moves after the Animator: the wave back, and the slap
            float t = Time.time;
            if (_waveBackUntil > t)
            {
                if (_legR == null) _legR = FindBone(_bw.transform, "Leg_Front_Upper.R");
                float u = (t - _waveBackFrom) / (_waveBackUntil - _waveBackFrom);
                float env = Mathf.Clamp01(u / 0.15f) * Mathf.Clamp01((1f - u) / 0.15f);
                float a = env * (110f + 28f * Mathf.Sin((t - _waveBackFrom) * Mathf.PI * 2f * 2.2f));
                if (_legR != null) _legR.rotation = Quaternion.AngleAxis(a, _bw.transform.forward) * _legR.rotation;
            }
            if (_five == Five.Slap || (_five == Five.Back && t - _slapAt < 0.35f))
            {
                if (_legL == null) _legL = FindBone(_bw.transform, "Leg_Front_Upper.L");
                float k = Mathf.Clamp01((t - _slapAt + 0.25f) / 0.25f);
                float a = Mathf.Sin(k * Mathf.PI) * 120f;
                if (_legL != null) _legL.rotation = Quaternion.AngleAxis(-a, _bw.transform.forward) * _legL.rotation;
                if (_legR == null) _legR = FindBone(_bw.transform, "Leg_Front_Upper.R");
                if (_legR != null) _legR.rotation = Quaternion.AngleAxis(a, _bw.transform.forward) * _legR.rotation;
            }
        }

        bool Free() => _bw.Visible && !_bw.Inside && !_bw.Climbing && !_bw.IsAttached && (_net == null || !_net.IsSpeaking) &&
                       !BeeswaxMotion.ShowMoment && Allowed(true) && _five == Five.Idle && Time.time >= _nextFive;

        /// <summary>moves: he may fly (high-five) or talk; false = in-place reactions only.</summary>
        bool Allowed(bool moves)
        {
            if (_director == null) return true;
            var b = _director.Beat;
            if (b == GameBeat.T0 || b == GameBeat.Field) return false;
            if (!moves) return true;
            if (b == GameBeat.Hook || b == GameBeat.Meet || b == GameBeat.Escape) return false;
            if (b == GameBeat.Pre)
            {
                if (_intro == null) _intro = FindAnyObjectByType<IntroSequence>();
                return _intro == null || _intro.SignHereReady; // the clipboard is handed over: he's just hovering
            }
            return true;
        }

        // ------------------------------------------------------------------ boop
        void Boop(int h, PalHand hand)
        {
            Vector3 c = Centre();
            float r = Radius() * 0.75f + 0.015f;
            float d = Vector3.Distance(hand.tip, c);
            if (d > r + 0.03f) { _touching[h] = false; return; }
            if (_touching[h] || d > r) return;
            _touching[h] = true;
            if (Time.time < _nextBoop) return;
            _nextBoop = Time.time + 0.8f;
            _boops++;
            Vector3 push = c - hand.tip;
            if (push.sqrMagnitude < 1e-6f) push = _bw.transform.position - (Head() != null ? Head().position : c - Vector3.forward);
            push.Normalize();
            _offVel += push * (0.55f + Radius() * 1.5f); // knocked back, springs home
            _squashFrom = Time.time;
            int v = Random.Range(0, 4);
            if (v == _lastVariant) v = (v + 1 + Random.Range(0, 3)) % 4;
            _lastVariant = v;
            switch (v)
            {
                case 0: _wiggleFrom = Time.time; Rig("Wag", "boop: giggle"); Say(PalLines.BoopGiggle); break;
                case 1: Rig("No Shake", "boop: grievance"); Rig("Angry", "boop: grievance"); Say(PalLines.BoopFile); break;
                case 2: _spinFrom = Time.time; Rig("Startle", "boop: spin"); break; // silence is a line
                default: Rig("Blush", "boop: blush"); _wiggleFrom = Time.time; Say(PalLines.BoopBlush); break;
            }
            if (_fx != null) _fx.Burst(hand.tip, 6, new Color(1f, 0.55f, 0.75f), 0.35f, 0.45f, 0.012f, 0.4f);
            GameSound.Cue("beeswax_boop", _bw.transform);
            Log("boop #" + _boops + " (" + (hand.ghost ? "ghost" : ((GameHand)h).ToString().ToLowerInvariant()) + "): " +
                (v == 0 ? "giggle" : v == 1 ? "grievance" : v == 2 ? "spin" : "blush"));
        }

        // ------------------------------------------------------------------ wave
        void Wave(int h, PalHand hand)
        {
            Transform head = Head();
            if (head == null) return;
            Vector3 eye = head.position, fwd = Flat(head.forward), right = Vector3.Cross(Vector3.up, fwd);
            Vector3 rel = hand.centre - eye;
            bool raised = rel.y > -0.28f && Vector3.Dot(rel, fwd) > 0.12f && (!hand.palm || hand.open);
            if (!raised) { _waveTurns[h] = 0; return; }
            float x = Vector3.Dot(rel, right);
            if (_waveTurns[h] == 0 && _waveDir[h] == 0) { _waveExt[h] = x; _waveDir[h] = 0; _waveStart[h] = Time.time; }
            int dir = x > _waveExt[h] + 0.005f ? 1 : x < _waveExt[h] - 0.005f ? -1 : 0;
            if (_waveDir[h] == 0 && Mathf.Abs(x - _waveExt[h]) > 0.03f) { _waveDir[h] = x > _waveExt[h] ? 1 : -1; _waveExt[h] = x; _waveStart[h] = Time.time; }
            else if (_waveDir[h] != 0)
            {
                if (dir == _waveDir[h]) _waveExt[h] = x; // still going: track the extreme
                else if (Mathf.Abs(x - _waveExt[h]) > 0.035f)
                {
                    _waveTurns[h]++;
                    _waveDir[h] = -_waveDir[h];
                    _waveExt[h] = x;
                }
            }
            if (Time.time - _waveStart[h] > 2.2f) { _waveTurns[h] = 0; _waveDir[h] = 0; }
            _waveX[h] = x;
            if (_waveTurns[h] >= 3 && Time.time >= _nextWave)
            {
                _waveTurns[h] = 0;
                _waveDir[h] = 0;
                _nextWave = Time.time + 3f;
                _fiveHeld = 0f; // a wave is not a high-five
                if (_bw.Inside || _bw.Climbing) return;
                WaveBack(1.6f);
                WavesBack++;
                if (Allowed(true)) Say(PalLines.Wave, 25f);
                Log("wave seen (" + (hand.ghost ? "ghost" : ((GameHand)h).ToString().ToLowerInvariant()) + "): Beeswax waves back");
            }
        }

        /// <summary>His right front leg waves for `seconds` (also the opening's hello).</summary>
        public void WaveBack(float seconds)
        {
            _waveBackFrom = Time.time;
            _waveBackUntil = Time.time + seconds;
            Rig("Wag", "waves back");
        }

        // ------------------------------------------------------------------ high-five
        void FiveWatch(int h, PalHand hand)
        {
            Transform head = Head();
            if (head == null || !hand.palm || !hand.open) { if (h == _fiveHand) _fiveHeld = 0f; return; }
            Vector3 eye = head.position;
            Vector3 rel = hand.centre - eye;
            float dist = rel.magnitude;
            bool up = rel.y > -0.22f && dist > 0.28f && dist < 0.85f && Vector3.Dot(Flat(rel), Flat(head.forward)) > 0.1f;
            bool facingOut = Vector3.Dot(hand.normal, rel / Mathf.Max(dist, 1e-4f)) > 0.45f && hand.normal.y > -0.5f;
            bool still = hand.vel.magnitude < 0.3f;
            if (!(up && facingOut && still) || (_fiveHand >= 0 && _fiveHand != h)) { if (h == _fiveHand || _fiveHand < 0) { _fiveHeld = 0f; _fiveHand = -1; } return; }
            _fiveHand = h;
            _fiveHeld += Time.deltaTime;
            if (_fiveHeld < 0.45f) return;
            // go
            _fiveHeld = 0f;
            _fivePalm = hand.centre;
            _fiveNormal = hand.normal;
            _prevGoalT = _bw.GoalTransform;
            _prevGoal = _bw.Goal;
            _interrupted = false;
            _five = Five.Flying;
            _fiveAt = Time.time;
            _retargetAt = Time.time + 0.3f;
            Order(() => _bw.FlyTo(FiveSpot(hand.centre, hand.normal)));
            Rig("Celebrate", "high-five: up top");
            Log("high-five offered (" + (hand.ghost ? "ghost" : ((GameHand)h).ToString().ToLowerInvariant()) + "): he flies over");
        }

        Vector3 FiveSpot(Vector3 palm, Vector3 normal) => palm + normal * (Radius() * 0.85f + 0.02f);

        void StepFive(PalHands hands)
        {
            if (_five == Five.Idle) return;
            float t = Time.time;
            bool handOk = hands != null && _fiveHand >= 0 && hands.Hands[_fiveHand].tracked && hands.Hands[_fiveHand].palm;
            if (_five == Five.Flying)
            {
                if (handOk)
                {
                    var hand = hands.Hands[_fiveHand];
                    if (t >= _retargetAt && (hand.centre - _fivePalm).sqrMagnitude > 0.05f * 0.05f)
                    {
                        _fivePalm = hand.centre;
                        _fiveNormal = hand.normal;
                        _retargetAt = t + 0.3f;
                        Order(() => _bw.FlyTo(FiveSpot(_fivePalm, _fiveNormal)));
                    }
                }
                float d = Vector3.Distance(Centre(), _fivePalm);
                if (d < Radius() + 0.08f || t - _fiveAt > 1.8f)
                {
                    _five = Five.Slap;
                    _slapAt = t;
                    _squashFrom = t;
                    _offVel += (Centre() - _fivePalm).normalized * 0.5f;
                    HighFives++;
                    if (_fx != null)
                    {
                        _fx.Burst(_fivePalm, 16, new Color(1f, 0.86f, 0.3f), 0.9f, 0.55f, 0.016f, 0.3f);
                        _fx.Ring(_fivePalm, _fiveNormal, 10, Color.white, 0.6f, 0.4f, 0.012f);
                    }
                    GameSound.Cue("high_five", _fivePalm);
                    Say(PalLines.HighFive, 20f);
                    Log("high-five! " + (d < Radius() + 0.08f ? "contact" : "air five (couldn't reach: " + d.ToString("0.00") + " m)"));
                }
            }
            else if (_five == Five.Slap && t - _slapAt > 0.5f)
            {
                _five = Five.Back;
                Order(() => { if (_prevGoalT != null) _bw.FlyTo(_prevGoalT); else _bw.FlyTo(_prevGoal); });
            }
            else if (_five == Five.Back && (_bw.Arrived || t - _slapAt > 2.5f))
            {
                _five = Five.Idle;
                _fiveHand = -1;
                _nextFive = t + 3f;
            }
        }

        void Order(System.Action a)
        {
            _mine = true;
            try { a(); }
            finally { _mine = false; }
        }

        // ------------------------------------------------------------------ the additive body layer
        void Body()
        {
            float dt = Time.deltaTime, t = Time.time;
            // spring the knock-back home
            _offVel += (-_offset * 160f - _offVel * 9f) * dt;
            _offset += _offVel * dt;
            Quaternion tilt = Quaternion.identity;
            Vector3 squash = Vector3.one;
            if (_spinFrom >= 0f)
            {
                float k = (t - _spinFrom) / 0.6f;
                if (k >= 1f) _spinFrom = -1f;
                else tilt *= Quaternion.Euler(0f, 360f * Mathf.SmoothStep(0f, 1f, k), 0f);
            }
            if (_wiggleFrom >= 0f)
            {
                float s = t - _wiggleFrom;
                if (s > 0.9f) _wiggleFrom = -1f;
                else tilt *= Quaternion.Euler(0f, 0f, 14f * Mathf.Sin(s * Mathf.PI * 2f * 7f) * (1f - s / 0.9f));
            }
            if (_squashFrom >= 0f)
            {
                float s = t - _squashFrom;
                if (s > 0.35f) _squashFrom = -1f;
                else { float a = Mathf.Sin(s / 0.35f * Mathf.PI) * 0.16f * (1f - s / 0.35f); squash = new Vector3(1f + a, 1f - a, 1f + a); }
            }
            bool idle = _offset.sqrMagnitude < 1e-8f && _offVel.sqrMagnitude < 1e-6f && tilt == Quaternion.identity && squash == Vector3.one;
            // what the motion module wrote this frame (or, if it didn't, what was there before my last write)
            bool fresh = !_wrote || _bw.BodyOffset != _wroteOffset || _bw.BodyTilt != _wroteTilt || _bw.BodySquash != _wroteSquash;
            if (fresh) { _baseOffset = _bw.BodyOffset; _baseTilt = _bw.BodyTilt; _baseSquash = _bw.BodySquash; }
            if (idle)
            {
                if (_wrote && !fresh) { _bw.BodyOffset = _baseOffset; _bw.BodyTilt = _baseTilt; _bw.BodySquash = _baseSquash; }
                _offset = _offVel = Vector3.zero;
                _wrote = false;
                return;
            }
            _bw.BodyOffset = _baseOffset + _bw.transform.InverseTransformVector(_offset);
            _bw.BodyTilt = _baseTilt * tilt;
            _bw.BodySquash = Vector3.Scale(_baseSquash, squash);
            _wroteOffset = _bw.BodyOffset;
            _wroteTilt = _bw.BodyTilt;
            _wroteSquash = _bw.BodySquash;
            _wrote = true;
        }

        // ------------------------------------------------------------------ helpers
        void Say(GameLine line, float gap = 6f)
        {
            if (_net == null || _net.IsSpeaking || Time.time < _nextLine || !Allowed(true)) return;
            _nextLine = Time.time + gap;
            _net.Say(line);
        }

        void Rig(string clip, string why)
        {
            if (_rig != null && _rig.isActiveAndEnabled) _rig.Play(clip, "pals: " + why);
        }

        Vector3 Centre() => _bw.BodyTransform != null ? _bw.BodyTransform.position : _bw.transform.position;

        Transform Head()
        {
            var del = BeeDelivery.Instance;
            if (del != null && del.Head != null) return del.Head;
            return Camera.main != null ? Camera.main.transform : null;
        }

        public static float Radius()
        {
            var body = BeeswaxBody.Instance;
            float r = body != null && body.Active ? body.Reach : 0f;
            return r > 0.02f ? Mathf.Min(r, 0.4f) : 0.08f;
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        static Transform FindBone(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var f = FindBone(root.GetChild(i), name);
                if (f != null) return f;
            }
            return null;
        }

        static void Log(string msg) => Debug.Log("[VBS][Pals] " + msg);
    }
}
