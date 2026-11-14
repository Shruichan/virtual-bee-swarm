using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Stakes
{
    public enum DarkSwarmMode
    {
        Hidden, // nothing drawn
        Deck,   // a band above the meadow window; SetCount grows / shrinks it through the window
        Home,   // every bee turns gold and streams back into the window, then Hidden
    }

    /// <summary>
    /// The swarm's ultimatum (V13 / V19): up to 150 dark red-brown bees, 5 cm, in one instanced draw. A second swarm,
    /// separate from the hive's SwarmSystem so neither can break the other.
    ///   PourOut(150)  bees leave the meadow window a few per frame, arc up over the player (peak at least 0.6 m above
    ///                 the head and at least 0.4 m in front of the face, never behind) and settle in the band.
    ///   SetCount(n)   eases the size: new bees fly out of the window into the band, extra bees fly back in.
    ///   Tendril()     8 bees dip down to the table in front of the hive and back.
    ///   Dive(n)       n bees swoop at the face and stop 40 cm short (off in comfort mode).
    ///   Home()        the bees turn gold and stream home through the window, then it hides.
    ///   Snap(n)       the end state at once (beat jumps, Board jumps): n bees in the band, nothing in flight.
    /// The band: 1.2 m wide, 0.4 m deep, centred on the head→window direction, 30° above the eye (25–35°; keep it at
    /// 30 or less for a 3S), at least 1 m out, and below CeilingY − 0.2 when the ceiling is known.
    /// Safety, applied to every bee every frame: never closer than 0.4 m to the face, never less than 0.4 m in front of
    /// the head along the head→window direction (so never beside or behind the player).
    /// Comfort: every size × 60/150 (150 → 60), straight up from the window to the band (no overhead pass), no dives.
    /// No allocations per frame, no colliders, no shadows.
    /// </summary>
    public class DarkSwarm : MonoBehaviour
    {
        const int HardMax = 200;
        const byte Off = 0, Fly = 1, Band = 2;
        const byte AfterBand = 0, AfterOff = 1, AfterReturn = 2;

        [Range(20, HardMax)] [SerializeField] int maxBees = 150;
        [SerializeField] Mesh beeMesh;
        [Tooltip("Instanced, unlit, vertex colours (VirtualBeeSwarm/Unlit). Per-instance _Color tints it.")]
        [SerializeField] Material beeMaterial;
        [SerializeField] float beeLength = 0.05f;

        [Header("Anchors (found at runtime if empty)")]
        [SerializeField] FutureWindow window;
        [Tooltip("Use this instead of the FutureWindow pane as the source / home point.")]
        [SerializeField] Transform windowOverride;
        [SerializeField] HiveBody hive;
        [Tooltip("The player's head (empty = main camera).")]
        [SerializeField] Transform head;

        [Header("Band")]
        [SerializeField] float bandWidth = 1.2f;
        [SerializeField] float bandDepth = 0.4f;
        [SerializeField] float bandHeight = 0.22f;
        [Range(25f, 35f)] [SerializeField] float bandElevationDeg = 30f;
        [SerializeField] float minElevationDeg = 25f;
        [SerializeField] float minBandDistance = 1.0f;
        [SerializeField] float ceilingMargin = 0.2f;

        [Header("Safety")]
        [Tooltip("The overhead pass peaks at least this far above the head.")]
        [SerializeField] float overheadClearance = 0.6f;
        [Tooltip("The overhead pass peaks this far in front of the face (at least 0.4).")]
        [SerializeField] float peakInFront = 0.5f;
        [Tooltip("No bee is ever less than this far in front of the head (along head → window).")]
        [SerializeField] float minFront = 0.4f;
        [Tooltip("Dives stop this far from the face; no bee ever comes closer.")]
        [SerializeField] float faceStop = 0.4f;
        [Tooltip("Comfort: the largest swarm; every size is scaled by comfortMax / maxBees.")]
        [SerializeField] int comfortMax = 60;

        [Header("Timing")]
        [SerializeField] float pourRate = 55f;
        [SerializeField] float changeRate = 40f;
        [SerializeField] float pourFlightSeconds = 2.6f;
        [SerializeField] float flightSeconds = 1.4f;
        [SerializeField] float homeFlightSeconds = 1.5f;

        [Header("Colours")]
        [SerializeField] Color darkColor = new Color(0.28f, 0.1f, 0.06f, 1f);
        [SerializeField] Color goldColor = new Color(1f, 0.8f, 0.25f, 1f);
        [SerializeField] float brightness = 1f;

        /// <summary>World height of the ceiling; NaN = unknown (an open hall). Set by the room model when it has one.</summary>
        public float CeilingY { get; set; } = float.NaN;
        public bool Comfort { get; set; }
        public DarkSwarmMode Mode { get; private set; } = DarkSwarmMode.Hidden;
        /// <summary>The size it is easing toward (after the comfort cap).</summary>
        public int Target => EffectiveTarget();
        /// <summary>Bees out of the window and not on their way back in.</summary>
        public int Active { get; private set; }
        /// <summary>Bees drawn this frame.</summary>
        public int Drawn { get; private set; }
        public Vector3 BandCenter => _band;
        /// <summary>Band centre angle above the eye (degrees).</summary>
        public float BandElevationDeg { get; private set; }
        /// <summary>Band centre direction vs the window direction, horizontal (degrees).</summary>
        public float BandYawFromWindowDeg { get; private set; }
        /// <summary>Closest any bee came to the face since the last Clear/Snap/PourOut (m).</summary>
        public float MinFaceDistance { get; private set; } = float.PositiveInfinity;
        /// <summary>Smallest distance in front of the head (along head → window) since the last Clear/Snap/PourOut (m).</summary>
        public float MinFront { get; private set; } = float.PositiveInfinity;
        /// <summary>Lowest height above the head of a bee in the overhead pass while within 0.75 m of the head (m).</summary>
        public float MinOverheadClearance { get; private set; } = float.PositiveInfinity;

        int _n, _target;
        bool _pouring;
        float _budget;
        byte[] _state, _after;
        bool[] _overhead;
        Vector3[] _p0, _c1, _c2, _p3, _pos, _prev, _vel, _slot;
        float[] _t0, _dur, _phase, _goldStart, _homeAt;
        Matrix4x4[] _matrices;
        Vector4[] _colors;
        MaterialPropertyBlock _mpb;
        static readonly int ColorArrayId = Shader.PropertyToID("_Color");
        bool _instancing, _bandValid, _loggedSettle;
        int _cursor;

        // frame geometry
        Vector3 _band, _headPos, _fwd, _right, _paneCenter, _nWin;

        void Awake()
        {
            if (window == null) window = FindAnyObjectByType<FutureWindow>();
            if (hive == null) hive = FindAnyObjectByType<HiveBody>();
            if (beeMesh == null) beeMesh = GameMeshes.CreateBeeMesh();
            if (beeMaterial == null)
                beeMaterial = GameMaterials.Create("VBS_DarkBee (runtime)", Color.white, vertexColors: true, doubleSided: true);
            beeMaterial.enableInstancing = true;
            _instancing = SystemInfo.supportsInstancing;
            Allocate();
        }

        void Allocate()
        {
            _n = Mathf.Clamp(maxBees, 1, HardMax);
            _state = new byte[_n];
            _after = new byte[_n];
            _overhead = new bool[_n];
            _p0 = new Vector3[_n]; _c1 = new Vector3[_n]; _c2 = new Vector3[_n]; _p3 = new Vector3[_n];
            _pos = new Vector3[_n]; _prev = new Vector3[_n]; _vel = new Vector3[_n]; _slot = new Vector3[_n];
            _t0 = new float[_n]; _dur = new float[_n]; _phase = new float[_n]; _goldStart = new float[_n]; _homeAt = new float[_n];
            _matrices = new Matrix4x4[_n];
            _colors = new Vector4[_n];
            _mpb = new MaterialPropertyBlock();
            var rnd = new System.Random(4321);
            for (int i = 0; i < _n; i++)
            {
                _phase[i] = (float)rnd.NextDouble() * Mathf.PI * 2f;
                // slot in the band: x -1..1 (width), y -1..1 (height), z -1..1 (depth); denser in the middle
                float x = (float)rnd.NextDouble() * 2f - 1f;
                x = Mathf.Sign(x) * Mathf.Pow(Mathf.Abs(x), 0.8f);
                _slot[i] = new Vector3(x, (float)rnd.NextDouble() * 2f - 1f, (float)rnd.NextDouble() * 2f - 1f);
                _goldStart[i] = -1f;
                _homeAt[i] = -1f;
            }
        }

        // ------------------------------------------------------------------ API
        public int Capacity => _n;

        /// <summary>The stakes line: `count` bees pour out of the window over the player into the band.</summary>
        public void PourOut(int count)
        {
            if (Mode != DarkSwarmMode.Deck) ClearAll();
            Mode = DarkSwarmMode.Deck;
            _target = Mathf.Clamp(count, 0, _n);
            _pouring = true;
            _budget = 0f;
            _loggedSettle = false;
            ResetSafetyStats();
            UpdateGeometry(0f, snapBand: true);
            Log("pour", _target);
        }

        /// <summary>Ease the size to `count` (through the window). Shows the band if it was hidden.</summary>
        public void SetCount(int count)
        {
            count = Mathf.Clamp(count, 0, _n);
            if (Mode == DarkSwarmMode.Hidden || Mode == DarkSwarmMode.Home)
            {
                if (Mode == DarkSwarmMode.Home) ClearAll();
                Mode = DarkSwarmMode.Deck;
                _pouring = false;
            }
            if (count == _target) return;
            _target = count;
            Log("count", _target);
        }

        /// <summary>End state at once: `count` bees in the band, nothing in flight.</summary>
        public void Snap(int count)
        {
            ClearAll();
            Mode = DarkSwarmMode.Deck;
            _target = Mathf.Clamp(count, 0, _n);
            _pouring = false;
            _loggedSettle = true;
            ResetSafetyStats();
            if (!UpdateGeometry(0f, snapBand: true)) { Log("snap (no head yet)", _target); return; }
            int want = EffectiveTarget();
            for (int i = 0; i < _n && Active < want; i++)
            {
                _state[i] = Band;
                _pos[i] = _prev[i] = SlotWorld(i, Time.time);
                _vel[i] = Vector3.zero;
                Active++;
            }
            Log("snap", _target);
        }

        /// <summary>The escape: gold, then home through the window, then hidden.</summary>
        public void Home()
        {
            if (Mode == DarkSwarmMode.Hidden) return;
            Mode = DarkSwarmMode.Home;
            _target = 0;
            _pouring = false;
            float now = Time.time;
            int rank = 0;
            UpdateGeometry(0f, snapBand: !_bandValid);
            for (int i = 0; i < _n; i++)
            {
                if (_state[i] == Off) continue;
                _goldStart[i] = now + rank * 0.006f;
                if (_state[i] == Band) _homeAt[i] = now + 0.6f + rank * 0.018f;
                else if (_after[i] != AfterOff) LaunchToWindow(i, now, homeFlightSeconds);
                rank++;
            }
            Log("home", 0);
        }

        /// <summary>8 bees dip to the table in front of the hive and back.</summary>
        public void Tendril(int count = 8)
        {
            if (Mode != DarkSwarmMode.Deck) return;
            Vector3 table = TablePoint();
            float now = Time.time;
            int sent = 0;
            for (int k = 0; k < _n && sent < count; k++)
            {
                int i = (_cursor + k) % _n;
                if (_state[i] != Band) continue;
                Vector3 to = table + _right * ((sent - count * 0.5f) * 0.025f) + Vector3.up * (0.01f * (sent & 1));
                Launch(i, now, _pos[i], _pos[i] + _nWin * 0.25f + Vector3.down * 0.1f, to + Vector3.up * 0.3f, to,
                       1.3f + 0.04f * sent, AfterReturn, false);
                sent++;
            }
            _cursor = (_cursor + 17) % _n;
            if (sent > 0) Debug.Log("[VBS][Stakes] swarm tendril " + sent + " bees to the table");
        }

        /// <summary>`count` bees swoop at the face and stop `faceStop` short. Off in comfort mode.</summary>
        public void Dive(int count = 6)
        {
            if (Mode != DarkSwarmMode.Deck || Comfort || count <= 0) return;
            float now = Time.time;
            Vector3 toBand = (_band - _headPos).normalized;
            int sent = 0;
            for (int k = 0; k < _n && sent < count; k++)
            {
                int i = (_cursor + 7 + k * 3) % _n;
                if (_state[i] != Band) continue;
                Vector3 d = _headPos + (toBand + _right * ((sent - count * 0.5f) * 0.12f)).normalized * (faceStop + 0.02f);
                Launch(i, now, _pos[i], _pos[i] + Vector3.up * 0.15f, d + toBand * 0.25f, d, 0.75f + 0.05f * sent, AfterReturn, false);
                sent++;
            }
            _cursor = (_cursor + 29) % _n;
            if (sent > 0) Debug.Log("[VBS][Stakes] swarm dive " + sent + " bees (stop " + faceStop.ToString("0.00") + " m from the face)");
        }

        /// <summary>Everything off at once (reset).</summary>
        public void Clear()
        {
            bool was = Mode != DarkSwarmMode.Hidden;
            ClearAll();
            Mode = DarkSwarmMode.Hidden;
            _target = 0;
            _pouring = false;
            if (was) Log("clear", 0);
        }

        // ------------------------------------------------------------------ internals
        void ClearAll()
        {
            for (int i = 0; i < _n; i++)
            {
                _state[i] = Off;
                _overhead[i] = false;
                _goldStart[i] = -1f;
                _homeAt[i] = -1f;
            }
            Active = 0;
            Drawn = 0;
            _budget = 0f;
        }

        void ResetSafetyStats()
        {
            MinFaceDistance = MinFront = MinOverheadClearance = float.PositiveInfinity;
        }

        int EffectiveTarget()
        {
            int t = Mathf.Clamp(_target, 0, _n);
            // comfort scales every size (150 -> 60, 80 -> 32, +30 -> +12) so the meter still reads
            return Comfort ? Mathf.Min(comfortMax, Mathf.RoundToInt(t * (float)comfortMax / _n)) : t;
        }

        Transform Head()
        {
            if (head != null) return head;
            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }

        /// <summary>Head, window and band for this frame. False when there's no head to place it against.</summary>
        bool UpdateGeometry(float dt, bool snapBand)
        {
            Transform h = Head();
            if (h == null) return false;
            _headPos = h.position;

            Transform pane = windowOverride != null ? windowOverride : window != null ? window.PaneTransform : null;
            if (pane != null) _paneCenter = pane.position;
            else
            {
                Vector3 f = h.forward; f.y = 0f;
                if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
                _paneCenter = _headPos + f.normalized * 1.5f + Vector3.down * 0.3f;
            }

            Vector3 toWin = _paneCenter - _headPos;
            toWin.y = 0f;
            float dist = toWin.magnitude;
            _fwd = dist > 1e-3f ? toWin / dist : Vector3.forward;
            _nWin = -_fwd;
            _right = Vector3.Cross(Vector3.up, _fwd);

            float d = Mathf.Max(minBandDistance, dist);
            float y = _headPos.y + d * Mathf.Tan(bandElevationDeg * Mathf.Deg2Rad);
            if (!float.IsNaN(CeilingY))
            {
                float maxY = CeilingY - ceilingMargin - bandHeight * 0.5f;
                if (y > maxY)
                {
                    y = maxY;
                    // keep it at least minElevationDeg up by bringing it closer (not nearer than 0.8 m)
                    float rise = Mathf.Max(0.05f, y - _headPos.y);
                    float dMax = rise / Mathf.Tan(minElevationDeg * Mathf.Deg2Rad);
                    if (dMax < d) d = Mathf.Max(0.8f, dMax);
                }
            }
            Vector3 desired = new Vector3(_headPos.x, y, _headPos.z) + _fwd * d;
            if (snapBand || !_bandValid) _band = desired;
            else _band = Vector3.Lerp(_band, desired, 1f - Mathf.Exp(-dt * 1.2f));
            _bandValid = true;

            Vector3 rel = _band - _headPos;
            float horiz = new Vector2(rel.x, rel.z).magnitude;
            BandElevationDeg = Mathf.Atan2(rel.y, Mathf.Max(1e-3f, horiz)) * Mathf.Rad2Deg;
            Vector3 relH = new Vector3(rel.x, 0f, rel.z);
            BandYawFromWindowDeg = relH.sqrMagnitude > 1e-6f ? Vector3.Angle(relH, _fwd) : 0f;
            return true;
        }

        Vector3 SlotWorld(int i, float t)
        {
            Vector3 s = _slot[i];
            float ph = _phase[i];
            return _band
                   + _right * (s.x * bandWidth * 0.5f + Mathf.Sin(t * 0.7f + ph) * 0.05f)
                   + Vector3.up * (s.y * bandHeight * 0.5f + Mathf.Sin(t * 1.3f + ph * 2f) * 0.03f)
                   + _fwd * (s.z * bandDepth * 0.5f + Mathf.Cos(t * 0.9f + ph) * 0.04f);
        }

        Vector3 WindowPoint(int i)
        {
            Vector3 s = _slot[i];
            return _paneCenter + _right * (s.x * 0.18f) + Vector3.up * (s.y * 0.12f) + _nWin * 0.02f;
        }

        Vector3 TablePoint()
        {
            if (hive != null)
            {
                Vector3 door = hive.DoorPoint.position;
                return new Vector3(door.x, hive.transform.position.y + 0.04f, door.z) + _nWin * 0.15f;
            }
            return _paneCenter + _nWin * 0.4f + Vector3.down * 0.2f;
        }

        void Launch(int i, float now, Vector3 p0, Vector3 c1, Vector3 c2, Vector3 p3, float seconds, byte after, bool overhead)
        {
            _state[i] = Fly;
            _after[i] = after;
            _overhead[i] = overhead;
            _p0[i] = p0; _c1[i] = c1; _c2[i] = c2; _p3[i] = p3;
            _t0[i] = now;
            _dur[i] = Mathf.Max(0.1f, seconds);
            _homeAt[i] = -1f;
        }

        /// <summary>Out of the window into its band slot: over the player (pour), straight up (comfort) or a short arc.</summary>
        void LaunchFromWindow(int i, float now, bool pour)
        {
            Vector3 p0 = WindowPoint(i);
            Vector3 p3 = SlotWorld(i, now + flightSeconds);
            _pos[i] = _prev[i] = p0;
            _vel[i] = Vector3.zero;
            _goldStart[i] = -1f;
            float r = 0.5f + 0.5f * Mathf.Sin(_phase[i] * 3.7f);
            if (Comfort)
            {
                // straight up out of the window, then into the band: never toward the player
                Launch(i, now, p0, p0 + Vector3.up * Mathf.Max(0.2f, (p3.y - p0.y) * 0.8f), p3 + Vector3.down * 0.05f, p3,
                       flightSeconds * (1.1f + 0.3f * r), AfterBand, false);
            }
            else if (pour)
            {
                // a cubic through the peak K at t = 0.5: K is in front of the face and high over the head
                Vector3 k = new Vector3(_headPos.x, 0f, _headPos.z) + _fwd * Mathf.Max(0.4f, peakInFront)
                            + _right * (_slot[i].x * 0.55f)
                            + Vector3.up * (_headPos.y + overheadClearance + 0.12f + 0.2f * r);
                Vector3 c = (8f * k - p0 - p3) / 6f;
                Launch(i, now, p0, c, c, p3, pourFlightSeconds * (0.85f + 0.3f * r), AfterBand, true);
            }
            else
            {
                Launch(i, now, p0, p0 + _nWin * 0.25f + Vector3.up * 0.2f, p3 + _nWin * 0.1f + Vector3.down * 0.05f, p3,
                       flightSeconds * (0.9f + 0.25f * r), AfterBand, false);
            }
            Active++;
        }

        void LaunchToWindow(int i, float now, float seconds)
        {
            Vector3 p = _pos[i];
            Vector3 w = WindowPoint(i) - _nWin * 0.06f; // just through the pane
            Launch(i, now, p, p + Vector3.up * 0.12f + _nWin * 0.05f, w + _nWin * 0.3f + Vector3.up * 0.1f, w, seconds, AfterOff, false);
            Active--;
        }

        void Update()
        {
            if (Mode == DarkSwarmMode.Hidden || _n == 0) { Drawn = 0; return; }
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            float now = Time.time;
            if (!UpdateGeometry(dt, snapBand: false)) { Drawn = 0; return; }

            // size controller (Deck only)
            if (Mode == DarkSwarmMode.Deck)
            {
                int want = EffectiveTarget();
                if (Active != want)
                {
                    _budget = Mathf.Min(_budget + (_pouring ? pourRate : changeRate) * dt, 12f);
                    while (_budget >= 1f && Active < want)
                    {
                        int i = FindOff();
                        if (i < 0) break;
                        LaunchFromWindow(i, now, _pouring);
                        _budget -= 1f;
                    }
                    while (_budget >= 1f && Active > want)
                    {
                        int i = FindToRecall();
                        if (i < 0) break;
                        LaunchToWindow(i, now, flightSeconds);
                        _budget -= 1f;
                    }
                }
                else _budget = 0f;
                if (_pouring && Active >= want) _pouring = false;
            }

            bool anyIn = false, anyAlive = false;
            Color dark = darkColor * brightness, gold = goldColor * brightness;
            int drawn = 0;
            for (int i = 0; i < _n; i++)
            {
                byte st = _state[i];
                if (st == Off) continue;
                anyAlive = true;
                Vector3 p;
                float scale = 1f;

                if (st == Band && _homeAt[i] >= 0f && now >= _homeAt[i])
                {
                    LaunchToWindow(i, now, homeFlightSeconds);
                    st = Fly;
                }

                if (st == Fly)
                {
                    float k = Mathf.Clamp01((now - _t0[i]) / _dur[i]);
                    float e = k * k * (3f - 2f * k);
                    float u = 1f - e;
                    p = u * u * u * _p0[i] + 3f * u * u * e * _c1[i] + 3f * u * e * e * _c2[i] + e * e * e * _p3[i];
                    // a little flutter across the path, zero at both ends
                    float f = Mathf.Sin(k * Mathf.PI) * 0.025f;
                    p += _right * (Mathf.Sin(now * 9f + _phase[i]) * f) + Vector3.up * (Mathf.Cos(now * 7f + _phase[i] * 2f) * f);
                    if (_after[i] == AfterOff) scale = Mathf.Lerp(1f, 0.5f, Mathf.InverseLerp(0.75f, 1f, k));
                    else if (_after[i] == AfterBand) { scale = Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(k * 6f)); anyIn = true; }
                    if (k >= 1f)
                    {
                        if (_after[i] == AfterOff)
                        {
                            _state[i] = Off;
                            _goldStart[i] = -1f;
                            continue;
                        }
                        if (_after[i] == AfterReturn)
                        {
                            Vector3 slot = SlotWorld(i, now + flightSeconds);
                            Launch(i, now, p, p + Vector3.up * 0.25f, slot + Vector3.down * 0.1f, slot, flightSeconds, AfterBand, false);
                        }
                        else
                        {
                            _state[i] = Band;
                            _overhead[i] = false;
                            _vel[i] = (p - _prev[i]) / Mathf.Max(dt, 1e-4f);
                        }
                    }
                }
                else
                {
                    // milling in the band: spring toward the slot
                    Vector3 target = SlotWorld(i, now);
                    Vector3 v = _vel[i];
                    v += ((target - _pos[i]) * 5f - v * 2.2f) * dt;
                    float vm = v.magnitude;
                    if (vm > 0.9f) v *= 0.9f / vm;
                    _vel[i] = v;
                    p = _pos[i] + v * dt;
                }

                p = Constrain(p, _overhead[i] && _state[i] == Fly);

                Vector3 step = p - _prev[i];
                _prev[i] = p;
                _pos[i] = p;
                Vector3 fwd = step.sqrMagnitude > 1e-8f ? step : (_band - p);
                Quaternion rot = fwd.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(fwd, Vector3.up) : Quaternion.identity;
                float flick = 1f + 0.06f * Mathf.Sin(now * 60f + _phase[i] * 10f);
                float len = beeLength * scale;
                _matrices[drawn] = Matrix4x4.TRS(p, rot, new Vector3(len * flick, len, len));

                float g = _goldStart[i] < 0f ? 0f : Mathf.Clamp01((now - _goldStart[i]) / 0.6f);
                float shade = 0.8f + 0.35f * (0.5f + 0.5f * Mathf.Sin(_phase[i] * 5.3f));
                Color c = Color.Lerp(dark, gold, g) * shade;
                _colors[drawn] = new Vector4(c.r, c.g, c.b, 1f);
                drawn++;
            }
            Drawn = drawn;

            if (Mode == DarkSwarmMode.Deck && !_loggedSettle && !_pouring && !anyIn && Active > 0)
            {
                _loggedSettle = true;
                Debug.Log("[VBS][Stakes] swarm settled " + Active + " in the band: elev " + BandElevationDeg.ToString("0.0") +
                          "° yaw " + BandYawFromWindowDeg.ToString("0.0") + "° | closest to face " + Fmt(MinFaceDistance) +
                          " m, min in front " + Fmt(MinFront) + " m, overhead clearance " + Fmt(MinOverheadClearance) + " m");
            }
            if (Mode == DarkSwarmMode.Home && !anyAlive)
            {
                Mode = DarkSwarmMode.Hidden;
                Active = 0;
                Debug.Log("[VBS][Stakes] swarm home: 0 left");
            }
            if (drawn == 0) return;

            if (_instancing)
            {
                _mpb.SetVectorArray(ColorArrayId, _colors);
                Graphics.DrawMeshInstanced(beeMesh, 0, beeMaterial, _matrices, drawn, _mpb, ShadowCastingMode.Off, false, gameObject.layer);
            }
            else
            {
                for (int i = 0; i < drawn; i++)
                {
                    _mpb.SetColor(ColorArrayId, _colors[i]);
                    Graphics.DrawMesh(beeMesh, _matrices[i], beeMaterial, gameObject.layer, null, 0, _mpb, ShadowCastingMode.Off, false);
                }
            }
        }

        /// <summary>The safety rules: never behind or beside the player, never within faceStop of the face, and the
        /// overhead pass stays overheadClearance above the head while it's within 0.75 m.</summary>
        Vector3 Constrain(Vector3 p, bool overheadPass)
        {
            Vector3 rel = p - _headPos;
            float along = Vector3.Dot(rel, _fwd);
            if (along < minFront) { p += _fwd * (minFront - along); rel = p - _headPos; along = minFront; }

            float horiz = new Vector2(rel.x, rel.z).magnitude;
            if (overheadPass && horiz < 0.75f)
            {
                if (rel.y < overheadClearance) { p.y = _headPos.y + overheadClearance; rel.y = overheadClearance; }
                if (rel.y < MinOverheadClearance) MinOverheadClearance = rel.y;
            }

            float d2 = rel.sqrMagnitude;
            if (d2 < faceStop * faceStop)
            {
                float d = Mathf.Sqrt(d2);
                p = d > 1e-4f ? _headPos + rel * (faceStop / d) : _headPos + _fwd * faceStop;
                rel = p - _headPos;
                d2 = faceStop * faceStop;
            }
            float dist = Mathf.Sqrt(d2);
            if (dist < MinFaceDistance) MinFaceDistance = dist;
            float a = Vector3.Dot(rel, _fwd);
            if (a < MinFront) MinFront = a;
            return p;
        }

        int FindOff()
        {
            for (int i = 0; i < _n; i++)
                if (_state[i] == Off) return i;
            return -1;
        }

        /// <summary>A bee to send home when shrinking: one in the band (highest index), else one still flying in.</summary>
        int FindToRecall()
        {
            for (int i = _n - 1; i >= 0; i--)
                if (_state[i] == Band && _homeAt[i] < 0f) return i;
            for (int i = _n - 1; i >= 0; i--)
                if (_state[i] == Fly && _after[i] != AfterOff) return i;
            return -1;
        }

        void Log(string what, int target)
        {
            Debug.Log("[VBS][Stakes] swarm " + what + " target=" + target + (Comfort ? " (comfort: " + EffectiveTarget() + ")" : "") +
                      " active=" + Active + " band elev " + BandElevationDeg.ToString("0.0") + "° yaw " +
                      BandYawFromWindowDeg.ToString("0.0") + "°");
        }

        public static string Fmt(float v) => float.IsPositiveInfinity(v) ? "-" : v.ToString("0.00");
    }
}
