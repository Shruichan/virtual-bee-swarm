using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// BeeswaxBody's half of BeeswaxMotion: every flight is swept with his capsule when it starts; if it passes through
    /// something (the hive, the rocket, a card...), it goes over or around it instead (the cheapest clear detour, on the
    /// player's side by preference), at the same speed. Called from StartFlight (the [BeeswaxBody hook] there).
    /// </summary>
    public partial class BeeswaxMotion
    {
        static readonly Vector3[] AvoidPts = new Vector3[160];
        static readonly Vector3[] AvoidW1 = new Vector3[10], AvoidW2 = new Vector3[10];
        Vector3[] _bestChain;

        int SamplePath(Flight fl, float reach)
        {
            // dense enough that his capsule overlaps from one sample to the next
            float len = 0f;
            Vector3 prev = fl.Path(0f);
            for (int i = 1; i <= 16; i++) { Vector3 p = fl.Path(i / 16f); len += Vector3.Distance(prev, p); prev = p; }
            int n = Mathf.Clamp(Mathf.CeilToInt(len / Mathf.Max(0.35f * reach, 1e-3f)) + 1, 8, AvoidPts.Length);
            for (int i = 0; i < n; i++) AvoidPts[i] = fl.Path(i / (float)(n - 1));
            return n;
        }

        static float PathLength(Vector3[] pts, int n)
        {
            float l = 0f;
            for (int i = 1; i < n; i++) l += Vector3.Distance(pts[i - 1], pts[i]);
            return l;
        }

        partial void AvoidObstacles(Flight fl)
        {
            var body = BeeswaxBody.Instance;
            if (body == null || !body.Active || fl == null) return;
            float thr = body.PathThreshold;
            int n = SamplePath(fl, body.Reach);
            float gap0 = body.SweepGap(AvoidPts, n, thr, out int i0, out int i1, out int g0);
            if (i0 < 0) return;
            float len0 = PathLength(AvoidPts, n);
            Vector3 a = AvoidPts[Mathf.Max(0, i0 - 1)], b = AvoidPts[Mathf.Min(n - 1, i1 + 1)];
            Vector3 s0 = AvoidPts[i0], s1 = AvoidPts[i1];
            if (!body.Obstruction(s0, s1, out Bounds hb)) return;

            var oldChain = fl.chain;
            float bestCost = float.PositiveInfinity, bestGap = float.NegativeInfinity, bestLen = len0;
            int tried = 0;
            Vector3 bw1 = default, bw2 = default, aw1 = default, aw2 = default;
            bool found = false, any = false;
            // around the whole cluster (the hive with what touches it) first; then around only what is in the way (the
            // cluster can be too tall to go over: the window above the hive roof)
            for (int pass = 0; pass < 2 && !found; pass++)
            {
                if (pass == 1 && !body.ObstructionOf(g0, s0, s1, out hb)) break;
                int c = body.Detours(fl.p0, fl.end, a, b, hb, AvoidW1, AvoidW2);
                tried += c;
                for (int k = 0; k < c; k++)
                {
                    bool two = AvoidW1[k] != AvoidW2[k];
                    fl.chain = two ? new[] { fl.p0, AvoidW1[k], AvoidW2[k], fl.end } : new[] { fl.p0, AvoidW1[k], fl.end };
                    int m = SamplePath(fl, body.Reach);
                    float gap = body.SweepGap(AvoidPts, m, thr, out int j0, out _, out _);
                    float len = PathLength(AvoidPts, m);
                    if (gap > bestGap) { bestGap = gap; aw1 = AvoidW1[k]; aw2 = AvoidW2[k]; any = true; }
                    if (j0 >= 0) continue;
                    float cost = len * (1f + body.BehindPenalty(AvoidW1[k], hb) + (two ? 0.05f : 0f));
                    if (cost < bestCost) { bestCost = cost; bw1 = AvoidW1[k]; bw2 = AvoidW2[k]; bestLen = len; found = true; }
                }
            }
            if (!found && (!any || bestGap <= gap0))
            {
                fl.chain = oldChain;
                body.NoteRoute(fl.kind, tried, false, gap0, gap0, BeeswaxBody.GroupName(g0));
                return;
            }
            if (!found) { bw1 = aw1; bw2 = aw2; }
            bool twoPts = bw1 != bw2;
            _bestChain = twoPts ? new[] { fl.p0, bw1, bw2, fl.end } : new[] { fl.p0, bw1, fl.end };
            fl.chain = _bestChain;
            if (!found) bestLen = PathLength(AvoidPts, SamplePath(fl, body.Reach));
            // same speed over the longer way
            if (fl.speed <= 0f && len0 > 1e-4f) fl.T *= Mathf.Clamp(bestLen / len0, 1f, 2.5f);
            if (fl.speed > 0f || fl.table != null) BuildTable(fl);
            body.NoteRoute(fl.kind, tried, found, gap0, found ? thr : bestGap,
                           (found ? "" : "least bad ") + "(" + (twoPts ? "2 points" : "1 point") + ", around the " + BeeswaxBody.GroupName(g0) +
                           ", +" + (bestLen - len0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " m)");
        }
    }
}
