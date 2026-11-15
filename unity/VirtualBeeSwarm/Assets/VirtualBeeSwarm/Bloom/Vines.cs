using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>
    /// The vines: every VinePath becomes a 3-sided tube (spline spine, 6 cm steps, ~4 cm thick at the root) that climbs
    /// from its floor contact at 0.45 m/s once the bloom wave reaches its root, with a leaf every step on alternating
    /// sides (0.15 s behind the tip), clusters of 2-3 saturated blossoms (0.5 s behind) and 2-4 side shoots, so a wall
    /// reads as covered from 2 m through passthrough. Walls: a wandering climb up the plane, stopped short of the window
    /// (the window's flanking vines lean away from it). Pillars: 3 spirals each, floor to top; table legs: one.
    /// All of it is ONE mesh drawn once (Bloom.shader with no instancing), capped at MaxTris in VinePath priority order:
    /// each ring slides up from the previous ring and opens out, so the tip grows like a real shoot.
    /// </summary>
    public static class Vines
    {
        public const float Climb = 0.45f, Step = 0.06f;
        /// <summary>The vine mesh's triangle cap (flowers 105k + vines + bees 7k stay under 150k).</summary>
        public const int MaxTris = 32000;
        const float WindowPad = 0.2f;
        public static string ArtUsed = "";

        // saturated, so they still read on beige walls through grainy passthrough
        static readonly Color[] BlossomColors =
        {
            new Color(0.36f, 0.2f, 0.92f),  // morning glory blue-violet
            new Color(1f, 0.8f, 0.06f),     // yellow
            new Color(0.58f, 0.12f, 0.86f), // clematis violet
            new Color(0.95f, 0.3f, 0.62f),  // wild rose magenta
        };

        struct Spine
        {
            public List<Vector3> p, n;
            public List<float> t; // arrival (s after the wave start)
        }

        public static Mesh Build(BloomSurfaces s, float wave, int seed, out int tris, out float done)
        {
            var b = new BloomMeshBuilder();
            var rnd = new System.Random(seed);
            done = 0f;
            var leafArt = BloomTuning.Art != BloomTuning.ArtMode.Off ? BloomMeshes.Art("Bloom_VineLeaf", "bloom_vine_leaf") : null;
            var blossomArt = BloomTuning.Art != BloomTuning.ArtMode.Off ? BloomMeshes.Art("Bloom_VineBlossom", "bloom_vine_blossom") : null;
            ArtUsed = (leafArt != null ? "art leaves" : "procedural leaves") + ", " + (blossomArt != null ? "art blossoms" : "procedural blossoms");
            int skipped = 0;
            foreach (var v in s.Vines)
            {
                if (b.Tris >= MaxTris) { skipped++; continue; }
                float birth = 0.15f + s.WaveArrival(v.root, wave) + (float)rnd.NextDouble() * 0.2f;
                var main = v.kind == VineKind.Wall ? WallSpine(v, s, birth, rnd) : SpiralSpine(v, birth, rnd);
                if (main.p.Count < 2) continue;
                bool leg = v.kind == VineKind.Leg;
                Color blossom = BlossomColors[rnd.Next(BlossomColors.Length)];
                Emit(b, main, leg ? 0.011f : 0.02f, leg ? 2 : 1, blossom, rnd, leafArt, blossomArt, leg ? 0.5f : 0.3f);
                done = Mathf.Max(done, main.t[main.t.Count - 1] + 0.5f + FlowerField.GrowSeconds);
                if (v.kind == VineKind.Wall && main.p.Count >= 4)
                {
                    int shoots = 2 + rnd.Next(3);
                    for (int k = 0; k < shoots; k++)
                    {
                        float at01 = 0.15f + 0.7f * (k + (float)rnd.NextDouble()) / shoots; // spread up the stem
                        int at = Mathf.Clamp((int)(main.p.Count * at01), 1, main.p.Count - 2);
                        var br = Branch(main, at, v, s, rnd, k);
                        if (br.p.Count >= 2) Emit(b, br, 0.012f, 1, blossom, rnd, leafArt, blossomArt, 0f);
                    }
                }
            }
            if (skipped > 0) Debug.Log("[VBS][Bloom] vines: triangle cap " + MaxTris + " reached, " + skipped + " of " + s.Vines.Count + " vines left out");
            tris = b.Tris;
            return b.V.Count > 0 ? b.ToMesh("VBS_BloomVines") : null;
        }

        static bool InWindowOpening(BloomSurfaces s, Vector3 point) =>
            (s.HasWindow && s.Window.Contains(point, WindowPad, 0.5f)) ||
            (s.WallSightlineKeepOut != null && s.WallSightlineKeepOut(point));

        static Spine NewSpine() => new Spine { p = new List<Vector3>(48), n = new List<Vector3>(48), t = new List<float>(48) };

        static Spine WallSpine(VinePath v, BloomSurfaces s, float birth, System.Random rnd)
        {
            var sp = NewSpine();
            float amp = 0.05f + 0.05f * (float)rnd.NextDouble(), freq = 4f + 2f * (float)rnd.NextDouble();
            float ph = (float)rnd.NextDouble() * 6.28f, drift = ((float)rnd.NextDouble() - 0.5f) * 0.2f;
            if (v.away != 0f) { drift = Mathf.Abs(drift) * 0.6f; ph = 0f; amp *= 0.6f; } // leans away, never back over the window
            float arc = 0f;
            Vector3 prev = v.root;
            for (float y = 0f; y <= v.height + 1e-4f; y += Step)
            {
                Vector3 p = v.root + Vector3.up * y + v.tangent * (amp * Mathf.Sin(y * freq + ph) - amp * Mathf.Sin(ph) + drift * y);
                if (InWindowOpening(s, p)) break; // never over the window
                if (sp.p.Count > 0) arc += Vector3.Distance(prev, p);
                sp.p.Add(p);
                sp.n.Add(v.normal);
                sp.t.Add(birth + arc / Climb);
                prev = p;
            }
            return sp;
        }

        static Spine SpiralSpine(VinePath v, float birth, System.Random rnd)
        {
            var sp = NewSpine();
            bool leg = v.kind == VineKind.Leg;
            float pitch = leg ? 0.32f : 1.0f;
            float off = leg ? 0.012f : 0.02f;
            float r = (v.half.x + v.half.y) * 0.5f + off;
            float dy = Step / Mathf.Sqrt(1f + Mathf.Pow(2f * Mathf.PI * r / pitch, 2f));
            float th0 = leg ? (float)rnd.NextDouble() * 6.28f : v.phase;
            float arc = 0f;
            Vector3 prev = Vector3.zero;
            int guard = 0;
            for (float y = 0.01f; y <= v.height && guard < 400; y += dy, guard++)
            {
                float th = th0 + 2f * Mathf.PI * y / pitch;
                Vector3 d = new Vector3(Mathf.Cos(th), 0f, Mathf.Sin(th));
                float tx = Mathf.Abs(d.x) > 1e-4f ? v.half.x / Mathf.Abs(d.x) : 1e4f;
                float tz = Mathf.Abs(d.z) > 1e-4f ? v.half.y / Mathf.Abs(d.z) : 1e4f;
                Vector3 face = tx < tz ? new Vector3(Mathf.Sign(d.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(d.z));
                Vector3 local = d * Mathf.Min(tx, tz) + face * off;
                Vector3 p = v.center + v.rotation * local + Vector3.up * y;
                if (sp.p.Count > 0) arc += Vector3.Distance(prev, p);
                sp.p.Add(p);
                sp.n.Add(v.rotation * face);
                sp.t.Add(birth + y / Climb); // a spiral climbs at the same vertical speed as a wall vine
                prev = p;
            }
            return sp;
        }

        static Spine Branch(Spine main, int at, VinePath v, BloomSurfaces s, System.Random rnd, int nth)
        {
            var sp = NewSpine();
            float side = v.away != 0f ? 1f : nth % 2 == 0 ? -1f : 1f; // alternate sides; the window's vines branch away
            Vector3 n = v.normal;
            Vector3 dir = (v.tangent * side * 0.8f + Vector3.up * 0.6f).normalized;
            float len = 0.3f + (float)rnd.NextDouble() * 0.3f, arc = 0f;
            Vector3 p = main.p[at];
            for (int k = 0; arc <= len; k++)
            {
                if (InWindowOpening(s, p)) break;
                sp.p.Add(p);
                sp.n.Add(n);
                sp.t.Add(main.t[at] + 0.2f + arc / Climb);
                dir = (Quaternion.AngleAxis(side * 12f, n) * dir).normalized; // curls back up
                p += dir * Step;
                arc += Step;
            }
            return sp;
        }

        static void Emit(BloomMeshBuilder b, Spine sp, float radius, int leafEvery, Color blossom, System.Random rnd, Mesh leafArt,
            Mesh blossomArt, float blossomFrom)
        {
            int n = sp.p.Count;
            var stem = new Color(0.22f, 0.36f, 0.12f);
            int prevRing = -1;
            b.Slot = -1f;
            b.SwayBase = 1e4f; // no sway on the walls
            for (int k = 0; k < n; k++)
            {
                Vector3 p = sp.p[k], N = sp.n[k];
                Vector3 T = (sp.p[Mathf.Min(n - 1, k + 1)] - sp.p[Mathf.Max(0, k - 1)]).normalized;
                if (T.sqrMagnitude < 0.5f) T = Vector3.up;
                Vector3 B = Vector3.Cross(T, N).normalized;
                float rk = Mathf.Lerp(radius, radius * 0.55f, k / (float)Mathf.Max(1, n - 1));
                Vector3 c = p + N * rk;
                b.Birth = sp.t[k];
                b.Pivot = c;
                b.From = k > 0 ? sp.p[k - 1] + sp.n[k - 1] * rk : c;
                int ring = b.V.Count;
                for (int j = 0; j < 3; j++)
                {
                    float a = j * 2.0944f;
                    b.Vert(c + (N * Mathf.Cos(a) + B * Mathf.Sin(a)) * rk, BloomMeshes.Shade(stem, j == 0 ? 1.1f : j == 1 ? 0.85f : 0.7f), 0f);
                }
                if (prevRing >= 0)
                    for (int j = 0; j < 3; j++)
                    {
                        int j1 = (j + 1) % 3;
                        b.Tri(prevRing + j, ring + j, ring + j1);
                        b.Tri(prevRing + j, ring + j1, prevRing + j1);
                    }
                prevRing = ring;

                // leaves, alternating sides
                if (k > 0 && k % leafEvery == 0 && k < n - 1)
                {
                    float side = (k / leafEvery) % 2 == 0 ? 1f : -1f;
                    Vector3 dir = (B * side * 0.75f + T * 0.65f).normalized;
                    float len = 0.11f + (float)rnd.NextDouble() * 0.06f, wid = len * 0.4f;
                    Vector3 at = p + N * (rk * 1.2f);
                    b.Birth = sp.t[k] + 0.15f;
                    b.Pivot = b.From = at;
                    Color leaf = Color.Lerp(new Color(0.18f, 0.44f, 0.14f), new Color(0.34f, 0.6f, 0.2f), (float)rnd.NextDouble());
                    b.Alt = leaf;
                    if (leafArt != null) b.Append(leafArt, Matrix4x4.TRS(at, Quaternion.LookRotation(N, dir), Vector3.one * (len / 0.065f)), leaf, 0f);
                    else
                    {
                        Vector3 W = Vector3.Cross(N, dir).normalized * wid;
                        Vector3 tip = at + dir * len + N * 0.015f, m = at + dir * (len * 0.45f) + N * 0.006f;
                        int i0 = b.Vert(at, BloomMeshes.Shade(leaf, 0.75f), 0f), i1 = b.Vert(m + W, leaf, 0f);
                        int i2 = b.Vert(tip, BloomMeshes.Shade(leaf, 1.15f), 0f), i3 = b.Vert(m - W, BloomMeshes.Shade(leaf, 0.9f), 0f);
                        b.Tri(i0, i1, i2);
                        b.Tri(i0, i2, i3);
                    }
                }

                // a cluster of 2-3 blossoms every ~0.36 m past blossomFrom, and one at a shoot's end
                float y01 = k / (float)Mathf.Max(1, n - 1);
                bool shootEnd = blossomFrom <= 0f && k == n - 1;
                if ((k % 6 == 3 && y01 >= blossomFrom && blossomFrom > 0f && rnd.NextDouble() < 0.8) || shootEnd)
                {
                    int cluster = shootEnd ? 2 : 2 + rnd.Next(2);
                    for (int c2 = 0; c2 < cluster; c2++)
                    {
                        float a = c2 * 2.4f + (float)rnd.NextDouble();
                        Vector3 fc = p + N * (rk + 0.02f) + (B * Mathf.Cos(a) + T * Mathf.Sin(a)) * (c2 == 0 ? 0.02f : 0.05f);
                        b.Birth = sp.t[k] + 0.5f + c2 * 0.12f;
                        b.Pivot = b.From = fc;
                        b.Alt = Color.Lerp(blossom, Color.white, 0.2f);
                        if (blossomArt != null)
                            b.Append(blossomArt, Matrix4x4.TRS(fc, Quaternion.LookRotation(N, T) * Quaternion.Euler(0f, 0f, a * Mathf.Rad2Deg), Vector3.one * 1.9f), blossom, 0f, blossom);
                        else Blossom(b, fc, N, T, B, blossom, (float)rnd.NextDouble() * 1.2f, 1.9f);
                    }
                }
            }
        }

        static void Blossom(BloomMeshBuilder b, Vector3 c, Vector3 N, Vector3 T, Vector3 B, Color col, float rot, float size)
        {
            for (int j = 0; j < 5; j++)
            {
                float a = rot + j * 1.2566f;
                Vector3 d = (T * Mathf.Cos(a) + B * Mathf.Sin(a)) * size;
                Vector3 s = (T * -Mathf.Sin(a) + B * Mathf.Cos(a)) * (0.011f * size);
                b.Tri(c + s * 0.4f, c - s * 0.4f, c + d * 0.03f + N * 0.006f, BloomMeshes.Shade(col, 0.8f), BloomMeshes.Shade(col, 0.8f), BloomMeshes.Shade(col, 1.1f), 0f);
                b.Tri(c + d * 0.03f + N * 0.006f, c + d * 0.016f + s, c + d * 0.016f - s, BloomMeshes.Shade(col, 1.1f), col, col, 0f);
            }
            var yellow = col.b > col.r ? new Color(1f, 0.85f, 0.2f) : new Color(0.4f, 0.22f, 0.08f);
            Vector3 cc = c + N * 0.005f;
            float e = 0.007f * size;
            b.Tri(cc + T * e, cc + B * e - T * (e * 0.6f), cc - B * e - T * (e * 0.6f), yellow, yellow, yellow, 0f);
        }

        public static void Draw(Mesh mesh, Material mat, MaterialPropertyBlock mpb, int layer)
        {
            if (mesh == null) return;
            Graphics.DrawMesh(mesh, Matrix4x4.identity, mat, layer, null, 0, mpb, ShadowCastingMode.Off, false);
        }
    }
}
