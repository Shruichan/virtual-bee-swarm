using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Pure motion maths for BeeswaxMotion: easing, springs, paths, and each primitive's shape as a function of time.
    /// No state, no scene: the EditMode tests call these directly.
    /// Offsets are in Beeswax's stage frame: x = his right as the player sees him, y = up, z = toward the player's eye.
    /// Angles are degrees on his body: x = pitch (+ = nose down, toward the player), y = yaw, z = roll.
    /// </summary>
    public static class MotionCurves
    {
        // ------------------------------------------------------------ easing (u in 0..1)
        public static float C01(float u) => u < 0f ? 0f : u > 1f ? 1f : u;
        public static float InOutSine(float u) { u = C01(u); return 0.5f - 0.5f * Mathf.Cos(Mathf.PI * u); }
        public static float OutCubic(float u) { u = 1f - C01(u); return 1f - u * u * u; }
        public static float InQuad(float u) { u = C01(u); return u * u; }
        public static float OutQuad(float u) { u = C01(u); return 1f - (1f - u) * (1f - u); }
        /// <summary>easeOutBack: overshoots by about s * 10% (s 1.70158 = 10%).</summary>
        public static float OutBack(float u, float s = 1.70158f) { u = C01(u) - 1f; return 1f + (s + 1f) * u * u * u + s * u * u; }

        /// <summary>0 -> 1 over [0, inS], hold, 1 -> 0 over [inS + holdS, inS + holdS + outS]. Ease in/out: OutCubic / InOutSine.</summary>
        public static float Pulse(float t, float inS, float holdS, float outS)
        {
            if (t <= 0f) return 0f;
            if (t < inS) return OutCubic(t / inS);
            t -= inS;
            if (t < holdS) return 1f;
            t -= holdS;
            return t < outS ? 1f - InOutSine(t / outS) : 0f;
        }

        /// <summary>A held pose: in over inS (OutCubic) until released at `released` (&lt; 0 = still held), out over outS (InOutSine).</summary>
        public static float Held(float t, float inS, float released, float outS)
        {
            float w = t <= 0f ? 0f : t < inS ? OutCubic(t / inS) : 1f;
            if (released < 0f || t < released) return w;
            float wAtRelease = released <= 0f ? 0f : released < inS ? OutCubic(released / inS) : 1f;
            float k = (t - released) / Mathf.Max(1e-3f, outS);
            return k >= 1f ? 0f : wAtRelease * (1f - InOutSine(k));
        }

        /// <summary>Held, but the way in is InOutSine too (leans toward the eye: its peak speed stays near 0.25 m/s).</summary>
        public static float HeldSoft(float t, float inS, float released, float outS)
        {
            float w = t <= 0f ? 0f : t < inS ? InOutSine(t / inS) : 1f;
            if (released < 0f || t < released) return w;
            float wAtRelease = released <= 0f ? 0f : released < inS ? InOutSine(released / inS) : 1f;
            float k = (t - released) / Mathf.Max(1e-3f, outS);
            return k >= 1f ? 0f : wAtRelease * (1f - InOutSine(k));
        }

        /// <summary>Nearest prop within `radius` of the fingertip, or -1. Geometry only: right and wrong props look the same.</summary>
        public static int HoverTarget(Vector3 tip, System.Collections.Generic.IList<Vector3> props, float radius)
        {
            int best = -1;
            float bestD = radius;
            for (int i = 0; i < props.Count; i++)
            {
                float d = Vector3.Distance(tip, props[i]);
                if (d <= bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>One hop of `height`: up over upS (OutQuad), down over downS (InQuad).</summary>
        public static float Hop(float t, float height, float upS, float downS)
        {
            if (t <= 0f || t >= upS + downS) return 0f;
            return t < upS ? height * OutQuad(t / upS) : height * (1f - InQuad((t - upS) / downS));
        }

        /// <summary>Damped oscillation that starts at 0 (continuous at the hand-off): A e^(-t/tau) sin(2 pi hz t).</summary>
        public static float Ring(float t, float a, float tau, float hz) => t <= 0f ? 0f : a * Mathf.Exp(-t / tau) * Mathf.Sin(2f * Mathf.PI * hz * t);

        /// <summary>Squash pulse on a landing/hit: full `amount` at t = 0..holdS, then back with time constant tau.</summary>
        public static float Squash(float t, float holdS, float tau) => t < 0f ? 0f : t < holdS ? 1f : Mathf.Exp(-(t - holdS) / tau);

        // ------------------------------------------------------------ paths
        public static Vector3 Bezier(Vector3 p0, Vector3 c, Vector3 p1, float u)
        {
            float a = 1f - u;
            return a * a * p0 + 2f * a * u * c + u * u * p1;
        }

        /// <summary>P1 control point: 65% of the way, raised by h. He hops over things and comes down onto the mark.</summary>
        public static Vector3 ArcControl(Vector3 p0, Vector3 p1, float h) => p0 + 0.65f * (p1 - p0) + Vector3.up * h;
        public static float ArcHeight(float d) => Mathf.Clamp(0.25f * d, 0.04f, 0.15f);
        /// <summary>P1 flight time: 0.45 m = 1.0 s (peak about 0.7 m/s).</summary>
        public static float FlightSeconds(float d) => Mathf.Clamp(0.35f + d / 0.7f, 0.5f, 1.6f);

        /// <summary>
        /// Flight time-warp: the curve's clock advances only as far as the speed cap allows (the path keeps its shape, he
        /// just gets there later). stepLen = distance the unwarped step would cover.
        /// </summary>
        public static float WarpedTau(float tau, float dt, float stepLen, float cap) =>
            stepLen > 1e-6f && stepLen > cap * dt ? tau + dt * (cap * dt / stepLen) : tau + dt;

        /// <summary>Centripetal-free uniform Catmull-Rom through pts (ends duplicated), u in 0..1 over the whole chain.</summary>
        public static Vector3 CatmullRom(Vector3[] pts, float u)
        {
            int n = pts.Length;
            if (n == 1) return pts[0];
            float f = C01(u) * (n - 1);
            int i = Mathf.Min(n - 2, (int)f);
            float t = f - i;
            Vector3 p0 = pts[Mathf.Max(0, i - 1)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Mathf.Min(n - 1, i + 2)];
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        /// <summary>Arc-length table (cumulative, normalised u steps) for constant-speed travel along a path.</summary>
        public static float[] ArcTable(System.Func<float, Vector3> path, int steps, out float length)
        {
            var table = new float[steps + 1];
            Vector3 prev = path(0f);
            length = 0f;
            for (int i = 1; i <= steps; i++)
            {
                Vector3 p = path(i / (float)steps);
                length += Vector3.Distance(prev, p);
                table[i] = length;
                prev = p;
            }
            return table;
        }

        /// <summary>u at arc length s (table from ArcTable).</summary>
        public static float UAtLength(float[] table, float s)
        {
            int n = table.Length - 1;
            if (s <= 0f) return 0f;
            if (s >= table[n]) return 1f;
            int lo = 0, hi = n;
            while (hi - lo > 1) { int m = (lo + hi) / 2; if (table[m] < s) lo = m; else hi = m; }
            float seg = table[hi] - table[lo];
            return (lo + (seg > 1e-6f ? (s - table[lo]) / seg : 0f)) / n;
        }

        // ------------------------------------------------------------ P1 arrival settle (0 .. SettleSeconds)
        public const float SettleSeconds = 1.8f;
        static float SettleFade(float t) => t >= SettleSeconds ? 0f : t > SettleSeconds - 0.3f ? (SettleSeconds - t) / 0.3f : 1f;
        /// <summary>Vertical overshoot after the curve ends (dips about 1.1 cm, rings out by ~0.6 s).</summary>
        public static float SettleY(float t) => -Ring(t, 0.02f, 0.15f, 2.5f) * SettleFade(t);
        /// <summary>Hanging sway (our version of the bot's dangle): roll degrees.</summary>
        public static float SwayRoll(float t) => t <= 0f ? 0f : 8f * Mathf.Exp(-t / 0.6f) * Mathf.Sin(2f * Mathf.PI * t / 0.9f) * SettleFade(t);
        public static float SwayX(float t) => t <= 0f ? 0f : 0.010f * Mathf.Exp(-t / 0.6f) * Mathf.Sin(2f * Mathf.PI * t / 0.9f) * SettleFade(t);
        public static float SwayY(float t)
        {
            if (t <= 0f) return 0f;
            float s = Mathf.Sin(2f * Mathf.PI * t / 0.9f);
            return 0.004f * Mathf.Exp(-t / 0.6f) * s * s * SettleFade(t);
        }

        // ------------------------------------------------------------ P4 HOLD THE FLOOR: the metronome
        /// <summary>Tick period and roll amplitude at `t` seconds since the offer (escalates at 8 s and 14 s).</summary>
        public static void TickPhase(float t, out float period, out float amp)
        {
            if (t < 8f) { period = 0.5f; amp = 6f; }
            else if (t < 14f) { period = 0.375f; amp = 7f; }
            else { period = 0.25f; amp = 8f; }
        }

        /// <summary>Index of the tick in effect at t, and when it started.</summary>
        public static int TickIndex(float t, out float tickStart)
        {
            if (t < 0f) { tickStart = 0f; return 0; }
            if (t < 8f) { int k = (int)(t / 0.5f); tickStart = k * 0.5f; return k; }
            if (t < 14f) { int k = (int)((t - 8f) / 0.375f); tickStart = 8f + k * 0.375f; return 16 + k; }
            int j = (int)((t - 14f) / 0.25f);
            tickStart = 14f + j * 0.25f;
            return 32 + j;
        }

        static float TickAmp(int k) => k < 16 ? 6f : k < 32 ? 7f : 8f;

        /// <summary>The steward's foot-tap as roll: alternates +-amp, each switch a crisp 0.08 s OutCubic, then holds.</summary>
        public static float TickRoll(float t)
        {
            if (t <= 0f) return 0f;
            int k = TickIndex(t, out float start);
            float to = ((k & 1) == 0 ? 1f : -1f) * TickAmp(k);
            float from = k == 0 ? 0f : ((k & 1) == 0 ? -1f : 1f) * TickAmp(k - 1);
            return Mathf.Lerp(from, to, OutCubic((t - start) / 0.08f));
        }

        /// <summary>"Well?" lean every 3.5 s: weight 0..1 (0.35 s in, 0.5 s hold, 0.35 s out), first at 3 s.</summary>
        public static float WellLean(float t)
        {
            if (t < 3f) return 0f;
            float local = (t - 3f) % 3.5f;
            return Pulse(local, 0.35f, 0.5f, 0.35f);
        }

        /// <summary>The foot tap you can see: from 2.5 s a 2 cm hop every 1.2 s.</summary>
        public static float TapHop(float t) => t < 2.5f ? 0f : Hop((t - 2.5f) % 1.2f, 0.02f, 0.08f, 0.12f);

        // ------------------------------------------------------------ P7 MOTION CARRIED (t from the verdict)
        public static float CarriedY(float t) => Hop(t, 0.05f, 0.14f, 0.16f) + Hop(t - 0.40f, 0.04f, 0.14f, 0.16f);
        /// <summary>Landing squash weight (1 = (1.06, 0.92, 1.06)).</summary>
        public static float CarriedSquash(float t) => Mathf.Max(Squash(t - 0.30f, 0.06f, 0.1f) * (t >= 0.30f ? 1f : 0f),
                                                                 Squash(t - 0.70f, 0.06f, 0.1f) * (t >= 0.70f ? 1f : 0f));
        /// <summary>A bow to the player: 28 deg pitch, held 0.45 s (no roll, no look elsewhere while it plays).</summary>
        public static float CarriedBow(float t) => 28f * Pulse(t - 0.95f, 0.25f, 0.45f, 0.30f);
        /// <summary>"Look at that": 60% toward the spot from 2.05 s for 1.2 s, back over 0.3 s.</summary>
        public static float CarriedLook(float t) => 0.6f * Pulse(t - 2.05f, 0.3f, 0.9f, 0.3f);
        /// <summary>The hops start this long after the verdict: the stamp lands first.</summary>
        public const float CarriedDelay = 0.20f;
        public const float CarriedSeconds = 3.60f + CarriedDelay;

        // ------------------------------------------------------------ P6 THE TAKE: anticipation (same for right and wrong)
        public const float TakeIn = 0.20f, TakeHold = 0.15f, TakeOut = 0.25f, TakeHitstop = 0.08f;
        /// <summary>Weight of the crouch before the verdict: down 2 cm, 6% squash, 8 deg toward the picked choice.</summary>
        public static float TakeCrouch(float t) => Pulse(t, TakeIn, TakeHold, TakeOut);

        // ------------------------------------------------------------ P8 GRIEVANCE (t from the verdict)
        /// <summary>Buzz shake, 0..0.35 s: sideways (m) and yaw (deg) in phase. 1.0 cm (spec 1.2): keeps the peak under 0.9 m/s.</summary>
        public static float BuzzX(float t) => t <= 0f || t >= 0.35f ? 0f : 0.010f * Mathf.Sin(2f * Mathf.PI * 12f * t) * (1f - t / 0.35f);
        public static float BuzzYaw(float t) => t <= 0f || t >= 0.35f ? 0f : 6f * Mathf.Sin(2f * Mathf.PI * 12f * t) * (1f - t / 0.35f);
        /// <summary>The swat/nothing spin that replaces the shake: one even turn (no strobing), 0 -> 360 over `seconds`.</summary>
        public static float Spin(float t, float seconds = 0.55f) => t <= 0f ? 0f : t >= seconds ? 0f : 360f * InOutSine(t / seconds);
        /// <summary>How much of the slump is left t seconds after the verdict: all of it for 1.85 s, then up to a quarter.</summary>
        public static float SlumpKeep(float t) => t < 1.85f ? 1f : Mathf.Lerp(1f, 0.25f, InOutSine((t - 1.85f) / 0.6f));
        /// <summary>Deflate weight from 0.35 s (0.5 s OutCubic), held until released (t since the verdict).</summary>
        public static float Deflate(float t, float released) => Held(t - 0.35f, 0.5f, released < 0f ? -1f : released - 0.35f, 0.5f);
        /// <summary>Tut-tut at 1.6 s: roll -10, +10, 0 (0.22 s each, InOutSine).</summary>
        public static float TutTut(float t)
        {
            t -= 1.6f;
            if (t <= 0f || t >= 0.66f) return 0f;
            if (t < 0.22f) return -10f * InOutSine(t / 0.22f);
            if (t < 0.44f) return Mathf.Lerp(-10f, 10f, InOutSine((t - 0.22f) / 0.22f));
            return 10f * (1f - InOutSine((t - 0.44f) / 0.22f));
        }

        // ------------------------------------------------------------ GAVEL (room_timeout / the timeout solve)
        /// <summary>Rise 3 cm in 0.15 s, slam 5 cm in 0.12 s (spec 6 cm / 0.08 s: capped under 0.9 m/s), spring back.</summary>
        public static float GavelY(float t)
        {
            if (t <= 0f) return 0f;
            if (t < 0.15f) return 0.03f * OutCubic(t / 0.15f);
            t -= 0.15f;
            if (t < 0.12f) return 0.03f - 0.05f * InQuad(t / 0.12f);
            t -= 0.12f;
            return -0.02f * Mathf.Exp(-t / 0.15f) * Mathf.Cos(2f * Mathf.PI * 3f * t);
        }
        public static float GavelSquash(float t) => t < 0.27f ? 0f : Mathf.Max(0f, 1f - (t - 0.27f) / 0.2f);
        public const float GavelSeconds = 1.2f;

        // ------------------------------------------------------------ word timing
        /// <summary>When `word` is said in a line: clipSeconds x IndexOf / Length (no clip: 1/14.4 s per character). -1 = not in it.</summary>
        public static float WordTime(string caption, string word, float clipSeconds)
        {
            if (string.IsNullOrEmpty(caption) || string.IsNullOrEmpty(word)) return -1f;
            int at = caption.IndexOf(word, System.StringComparison.OrdinalIgnoreCase);
            if (at < 0) return -1f;
            return clipSeconds > 0.05f ? clipSeconds * at / caption.Length : at / 14.4f;
        }

        // ------------------------------------------------------------ angles
        /// <summary>Critically damped (zeta 1) or springy angle follow with a rate cap. Returns the new angle; v is deg/s.</summary>
        public static float SpringAngle(float x, ref float v, float target, float omega, float zeta, float maxRate, float dt)
        {
            dt = Mathf.Min(dt, 1f / 30f); // a hitch never throws the spring
            float err = Mathf.DeltaAngle(x, target);
            float a = omega * omega * err - 2f * zeta * omega * v;
            v += a * dt;
            v = Mathf.Clamp(v, -maxRate, maxRate);
            return x + v * dt;
        }
    }
}
