using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Signed distances for Beeswax's body (a capsule) against oriented boxes and spheres. Pure functions, no allocation.
    /// Negative = overlap (how deep). The box and sphere SDFs are convex, so the capsule's closest point along its segment
    /// is found by a golden-section search.
    /// </summary>
    public static class BodyMath
    {
        /// <summary>An oriented box: centre, unit axes, half extents along them (world metres).</summary>
        public struct Obb
        {
            public Vector3 c, ax, ay, az, h;

            public Vector3 AabbExtents => new Vector3(
                Mathf.Abs(ax.x) * h.x + Mathf.Abs(ay.x) * h.y + Mathf.Abs(az.x) * h.z,
                Mathf.Abs(ax.y) * h.x + Mathf.Abs(ay.y) * h.y + Mathf.Abs(az.y) * h.z,
                Mathf.Abs(ax.z) * h.x + Mathf.Abs(ay.z) * h.y + Mathf.Abs(az.z) * h.z);

            /// <summary>From a transform matrix and bounds in its local space. False if the matrix squashes an axis flat.</summary>
            public static bool From(in Matrix4x4 m, Vector3 localCenter, Vector3 localExtents, out Obb b)
            {
                b = default;
                b.c = m.MultiplyPoint3x4(localCenter);
                Vector3 x = new Vector3(m.m00, m.m10, m.m20), y = new Vector3(m.m01, m.m11, m.m21), z = new Vector3(m.m02, m.m12, m.m22);
                float lx = x.magnitude, ly = y.magnitude, lz = z.magnitude;
                if (lx < 1e-7f || ly < 1e-7f || lz < 1e-7f) return false;
                b.ax = x / lx; b.ay = y / ly; b.az = z / lz;
                b.h = new Vector3(Mathf.Abs(localExtents.x) * lx, Mathf.Abs(localExtents.y) * ly, Mathf.Abs(localExtents.z) * lz);
                return true;
            }
        }

        /// <summary>Signed distance from p to the box; n = the way out (unit).</summary>
        public static float BoxSdf(in Obb b, Vector3 p, out Vector3 n)
        {
            Vector3 d = p - b.c;
            float x = Vector3.Dot(d, b.ax), y = Vector3.Dot(d, b.ay), z = Vector3.Dot(d, b.az);
            float qx = Mathf.Abs(x) - b.h.x, qy = Mathf.Abs(y) - b.h.y, qz = Mathf.Abs(z) - b.h.z;
            if (qx <= 0f && qy <= 0f && qz <= 0f)
            {
                // inside: out through the nearest face
                if (qx >= qy && qx >= qz) { n = b.ax * (x < 0f ? -1f : 1f); return qx; }
                if (qy >= qz) { n = b.ay * (y < 0f ? -1f : 1f); return qy; }
                n = b.az * (z < 0f ? -1f : 1f); return qz;
            }
            float ox = qx > 0f ? (x < 0f ? -qx : qx) : 0f, oy = qy > 0f ? (y < 0f ? -qy : qy) : 0f, oz = qz > 0f ? (z < 0f ? -qz : qz) : 0f;
            Vector3 w = b.ax * ox + b.ay * oy + b.az * oz;
            float len = w.magnitude;
            n = len > 1e-7f ? w / len : b.ay;
            return len;
        }

        public static float SphereSdf(Vector3 c, float r, Vector3 p, out Vector3 n)
        {
            Vector3 d = p - c;
            float len = d.magnitude;
            n = len > 1e-6f ? d / len : Vector3.up;
            return len - r;
        }

        const float Phi = 0.618034f;

        /// <summary>Capsule (segment a-b, radius r) to box: signed gap between the capsule's surface and the box.</summary>
        public static float CapsuleBox(Vector3 a, Vector3 b, float r, in Obb box, out Vector3 n)
        {
            Vector3 ab = b - a;
            if (ab.sqrMagnitude < 1e-10f) return BoxSdf(box, a, out n) - r;
            float lo = 0f, hi = 1f;
            float t1 = hi - Phi * (hi - lo), t2 = lo + Phi * (hi - lo);
            float f1 = BoxSdf(box, a + ab * t1, out _), f2 = BoxSdf(box, a + ab * t2, out _);
            for (int i = 0; i < 14; i++)
            {
                if (f1 < f2) { hi = t2; t2 = t1; f2 = f1; t1 = hi - Phi * (hi - lo); f1 = BoxSdf(box, a + ab * t1, out _); }
                else { lo = t1; t1 = t2; f1 = f2; t2 = lo + Phi * (hi - lo); f2 = BoxSdf(box, a + ab * t2, out _); }
            }
            float t = 0.5f * (lo + hi);
            float best = BoxSdf(box, a + ab * t, out n);
            // the ends (a flat minimum along a face can sit there)
            float fa = BoxSdf(box, a, out Vector3 na), fb = BoxSdf(box, b, out Vector3 nb);
            if (fa < best - 1e-6f) { best = fa; n = na; }
            if (fb < best - 1e-6f) { best = fb; n = nb; }
            return best - r;
        }

        /// <summary>Capsule to sphere (centre c, radius rs).</summary>
        public static float CapsuleSphere(Vector3 a, Vector3 b, float r, Vector3 c, float rs, out Vector3 n)
        {
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude > 1e-10f ? Mathf.Clamp01(Vector3.Dot(c - a, ab) / ab.sqrMagnitude) : 0f;
            Vector3 p = a + ab * t;
            Vector3 d = p - c;
            float len = d.magnitude;
            n = len > 1e-6f ? d / len : Vector3.up;
            return len - rs - r;
        }

        /// <summary>Critically damped spring step (exact for any dt): x follows target, v is its velocity.</summary>
        public static void Crit(ref Vector3 x, ref Vector3 v, Vector3 target, float omega, float dt)
        {
            float e = Mathf.Exp(-omega * dt);
            Vector3 change = x - target;
            Vector3 temp = (v + omega * change) * dt;
            v = (v - omega * temp) * e;
            x = target + (change + temp) * e;
        }
    }
}
