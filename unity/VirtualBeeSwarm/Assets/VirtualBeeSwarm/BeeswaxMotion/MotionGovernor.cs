using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The comfort rules (G1-G6) as pure functions. BeeswaxMotion runs them last, every frame; tests call them directly.
    /// G1 distance (0.60 soft, 0.45 hard at his surface), G2 never behind or above the player, G3 speed (0.9 cap, 0.35 in the periphery,
    /// approach 0.25), G4 clear view of what the player must see, G5 never chase the held hive, G6 steady caption.
    /// Only G1 may snap (a head that lunges at him); behind/above corrections are rate-limited by the caller.
    /// G7 (no camera) holds by construction: nothing here writes the head.
    /// </summary>
    public static class MotionGovernor
    {
        public const float SoftDistance = 0.60f, HardDistance = 0.45f;
        /// <summary>The hard distance for his body CENTRE: his surface (7.5 cm closer) never inside 0.45 m.</summary>
        public const float HardCentre = HardDistance + BodyRadius + 0.005f;
        /// <summary>Eye this close (horizontally) to the hive: the table's forward stops following the head (leaning over it).</summary>
        public const float ForwardFreeze = 0.35f;
        public const float MinForward = 0.20f, MaxAboveEye = 0.10f;
        public const float MaxSpeed = 0.9f, FastSpeed = 1.2f, FastSeconds = 0.4f, PeripherySpeed = 0.35f, PeripheryDegrees = 35f;
        public const float FastGazeDegrees = 30f, MaxApproach = 0.25f, BackOffSpeed = 0.6f;
        public const float BodyRadius = 0.075f, CaptionUp = 0.13f, CaptionRadius = 0.06f, ClearMargin = 0.03f;
        public const float HeldHiveRadius = 0.25f;
        public const float HideFactor = 0.55f;

        /// <summary>The player: eye position, where the head looks, and the table's forward (eye to hive, horizontal).</summary>
        public struct Ctx
        {
            public Vector3 eye, gaze, forward;
            public bool attached;
            /// <summary>keepForward: last frame's forward, kept while the eye is over the hive (the direction is unstable there).</summary>
            public static Ctx Make(Vector3 eye, Vector3 gaze, Vector3 hive, Vector3 keepForward = default)
            {
                Vector3 f = hive - eye;
                f.y = 0f;
                Vector3 fwd = f.sqrMagnitude < ForwardFreeze * ForwardFreeze && keepForward.sqrMagnitude > 0.5f ? keepForward
                            : f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
                return new Ctx { eye = eye, gaze = gaze.sqrMagnitude > 1e-6f ? gaze.normalized : Vector3.forward, forward = fwd };
            }
        }

        /// <summary>A protected thing the player must see: centre and radius (bounds radius + margin).</summary>
        public struct Disc
        {
            public Vector3 center;
            public float radius;
            public string name;
            /// <summary>It must not hide him either (a parked choice): he keeps clear of it in front AND behind.</summary>
            public bool both;
            public Disc(Vector3 c, float r, string n, bool both = false) { center = c; radius = r; name = n; this.both = both; }
        }

        // ------------------------------------------------------------ G1 + G2 on a point
        /// <summary>Pushes a goal or a lean out to `minDist` from the eye along eye->p, in front of the forward plane, below the brow.</summary>
        /// <summary>floorY: the brow rule never pushes him below this (his mark's own height: a crouching player does not drag him into the table).</summary>
        public static Vector3 Keep(Vector3 p, in Ctx c, float minDist, out string rule, float floorY = float.NegativeInfinity)
        {
            rule = null;
            if (c.attached) return p;
            Vector3 d = p - c.eye;
            float fwd = Vector3.Dot(d, c.forward);
            if (fwd < MinForward) { p += c.forward * (MinForward - fwd); rule = "behind"; }
            float brow = Mathf.Max(c.eye.y + MaxAboveEye, floorY);
            if (p.y > brow) { p.y = brow; rule = rule ?? "above"; }
            d = p - c.eye;
            float dist = d.magnitude;
            if (dist < minDist)
            {
                Vector3 dir = dist > 1e-4f ? d / dist : c.forward;
                p = c.eye + dir * minDist;
                rule = rule ?? (minDist <= HardDistance + 1e-4f ? "distance-hard" : "distance");
                // pushing out along the ray may lift it over the brow line: keep G2
                if (p.y > brow) p.y = brow;
            }
            return p;
        }

        /// <summary>G1 alone: straight out along the eye ray to minDist. The only correction that may snap.</summary>
        public static Vector3 KeepDistance(Vector3 p, in Ctx c, float minDist)
        {
            if (c.attached) return p;
            Vector3 d = p - c.eye;
            float dist = d.magnitude;
            if (dist >= minDist) return p;
            return c.eye + (dist > 1e-4f ? d / dist : c.forward) * minDist;
        }

        // ------------------------------------------------------------ G3 speed
        /// <summary>Degrees between where the head looks and the direction to p.</summary>
        public static float GazeAngle(Vector3 p, in Ctx c) => Vector3.Angle(c.gaze, p - c.eye);

        /// <summary>
        /// Fastest he may move from p along dir (unit): 0.9 m/s, 0.35 in the periphery, and the part toward the eye at most
        /// 0.25. fastOk lets a short move reach 1.2 m/s while within 30 deg of gaze and not approaching.
        /// </summary>
        public static float MaxSpeedAlong(Vector3 p, Vector3 dir, in Ctx c, bool fastOk)
        {
            float cap = GazeAngle(p, c) > PeripheryDegrees ? PeripherySpeed : MaxSpeed;
            Vector3 toEye = c.eye - p;
            float dist = toEye.magnitude;
            float inward = dist > 1e-4f ? Vector3.Dot(dir, toEye / dist) : 0f;
            if (fastOk && inward <= 0.05f && GazeAngle(p, c) <= FastGazeDegrees) cap = FastSpeed;
            if (inward > 1e-3f) cap = Mathf.Min(cap, MaxApproach / inward);
            return cap;
        }

        /// <summary>Limits a body step: at most `maxSpeed` overall and the eye-ward part at most 0.25 m/s.</summary>
        public static Vector3 LimitStep(Vector3 from, Vector3 to, float dt, in Ctx c, float maxSpeed)
        {
            if (dt <= 0f) return from;
            Vector3 step = to - from;
            Vector3 toEye = c.eye - from;
            float dist = toEye.magnitude;
            if (dist > 1e-4f)
            {
                Vector3 n = toEye / dist;
                float inward = Vector3.Dot(step, n);
                float lim = MaxApproach * dt;
                if (inward > lim) step -= n * (inward - lim);
            }
            float m = step.magnitude, maxM = maxSpeed * dt;
            if (m > maxM) step *= maxM / m;
            return from + step;
        }

        // ------------------------------------------------------------ G4 clear view
        /// <summary>Angular radius (deg) of a disc of radius r at distance d.</summary>
        static float AngR(float r, float d) => d <= r ? 90f : Mathf.Asin(r / d) * Mathf.Rad2Deg;

        /// <summary>Does Beeswax at p (his body disc, and his caption disc above him) cover any protected disc? Being behind it is fine.</summary>
        public static bool Covers(Vector3 p, in Ctx c, List<Disc> discs, out string what)
        {
            what = null;
            if (discs == null) return false;
            for (int i = 0; i < discs.Count; i++)
            {
                var k = discs[i];
                Vector3 toK = k.center - c.eye;
                float dk = toK.magnitude;
                if (dk < 1e-3f) continue;
                if (CoversOne(p, BodyRadius, c, toK, dk, k.radius, k.both) || CoversOne(p + Vector3.up * CaptionUp, CaptionRadius, c, toK, dk, k.radius, false))
                {
                    what = k.name;
                    return true;
                }
            }
            return false;
        }

        static bool CoversOne(Vector3 q, float r, in Ctx c, Vector3 toK, float dk, float rk, bool both)
        {
            Vector3 toQ = q - c.eye;
            float dq = toQ.magnitude;
            if (dq < 1e-3f) return false;
            if (dq > dk + rk)
            {
                if (!both) return false; // behind it (or level with its back): it covers him, not the other way
                rk *= HideFactor;        // it hides him: its solid middle counts, not the loose bounds (label, margin)
            }
            return Vector3.Angle(toQ, toK) < AngR(r, dq) + AngR(rk, dk);
        }

        /// <summary>
        /// A mark that covers something: slide it around the eye (same distance, same height) outward to the nearest free
        /// direction, at most 70 deg. Returns the mark unchanged when it is clear (or nothing nearby is free).
        /// </summary>
        public static Vector3 ClearView(Vector3 goal, in Ctx c, List<Disc> discs, out string what)
        {
            if (!Covers(goal, c, discs, out what)) return goal;
            // outward = away from the protected things' mean bearing
            Vector3 mean = Vector3.zero;
            for (int i = 0; i < discs.Count; i++) mean += (discs[i].center - c.eye).normalized;
            Vector3 g = goal - c.eye;
            float side = Vector3.Dot(Vector3.Cross(mean, g), Vector3.up) >= 0f ? 1f : -1f;
            for (int step = 1; step <= 35; step++)
            {
                for (int s = 0; s < 2; s++)
                {
                    float sign = s == 0 ? side : -side;
                    Vector3 cand = c.eye + Quaternion.AngleAxis(sign * step * 2f, Vector3.up) * g;
                    if (!Covers(cand, c, discs, out _)) return cand;
                }
            }
            return goal;
        }

        // ------------------------------------------------------------ G5 held hive
        /// <summary>While the hive is held, a goal near it becomes the podium: he never chases the player's hands.</summary>
        public static Vector3 HeldHive(Vector3 goal, Vector3 hive, Vector3 podium, bool held) =>
            held && Vector3.Distance(goal, hive) < HeldHiveRadius ? podium : goal;

        // ------------------------------------------------------------ G6 caption steadiness
        /// <summary>While he talks, an offset no beat ordered stays within 6 cm.</summary>
        public static Vector3 SteadyCaption(Vector3 ambientOffset, bool speaking) =>
            speaking ? Vector3.ClampMagnitude(ambientOffset, 0.06f) : ambientOffset;
    }
}
