using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm.Pebbles
{
    /// <summary>
    /// Runtime meshes for the pebble toss: lumpy pebbles (~64 tris), a clay bowl (lathe, ~190 tris), and the invisible
    /// basins (a convex floor disc + a ring of rim boxes) that catch pebbles in the dish and the bowl. Lighting is baked
    /// into vertex colours so one unlit vertex-colour material draws everything.
    /// </summary>
    static class PebbleArt
    {
        static readonly Vector3 Light = new Vector3(0.35f, 0.85f, -0.4f).normalized;

        static Color Lit(Color c, Vector3 n)
        {
            float k = 0.55f + 0.45f * Mathf.Clamp01(Vector3.Dot(n, Light) * 0.5f + 0.5f);
            return new Color(c.r * k, c.g * k, c.b * k, 1f);
        }

        /// <summary>A pebble of radius 1 (x/z) and 0.6 (y), lumpy, coloured from a seed.</summary>
        public static Mesh Pebble(int seed)
        {
            var rnd = new System.Random(seed * 7919 + 13);
            const int lon = 8, lat = 5;
            var v = new List<Vector3>();
            var t = new List<int>();
            v.Add(new Vector3(0f, -0.6f, 0f));
            for (int i = 1; i < lat; i++)
            {
                float phi = Mathf.PI * i / lat - Mathf.PI * 0.5f;
                for (int j = 0; j < lon; j++)
                {
                    float th = Mathf.PI * 2f * j / lon;
                    float r = 0.88f + 0.2f * (float)rnd.NextDouble();
                    v.Add(new Vector3(Mathf.Cos(phi) * Mathf.Cos(th) * r, Mathf.Sin(phi) * 0.6f * r, Mathf.Cos(phi) * Mathf.Sin(th) * r));
                }
            }
            v.Add(new Vector3(0f, 0.6f, 0f));
            int top = v.Count - 1;
            for (int j = 0; j < lon; j++) { t.Add(0); t.Add(1 + j); t.Add(1 + (j + 1) % lon); }
            for (int i = 0; i < lat - 2; i++)
                for (int j = 0; j < lon; j++)
                {
                    int a = 1 + i * lon + j, b = 1 + i * lon + (j + 1) % lon, a2 = a + lon, b2 = b + lon;
                    t.Add(a); t.Add(a2); t.Add(b);
                    t.Add(b); t.Add(a2); t.Add(b2);
                }
            int last = 1 + (lat - 2) * lon;
            for (int j = 0; j < lon; j++) { t.Add(top); t.Add(last + (j + 1) % lon); t.Add(last + j); }
            var m = new Mesh { name = "VBS_TossPebble" + seed };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            // warm river stones: they read apart from the dish's own grey pebbles
            float g = 0.62f + 0.16f * (float)rnd.NextDouble();
            var baseC = new Color(g + 0.2f, g * 0.86f, g * 0.6f);
            var ns = m.normals;
            var c = new Color[ns.Length];
            for (int i = 0; i < ns.Length; i++) c[i] = Lit(baseC, ns[i]);
            m.colors = c;
            m.RecalculateBounds();
            return m;
        }

        /// <summary>A shallow clay bowl, inner radius 1, rim at y 0.5 (lathe, 16 segments).</summary>
        public static Mesh Bowl()
        {
            // profile from the outer foot over the lip to the inner centre
            Vector2[] p =
            {
                new Vector2(0.6f, 0f), new Vector2(1.05f, 0.12f), new Vector2(1.2f, 0.42f), new Vector2(1.15f, 0.52f),
                new Vector2(1.0f, 0.46f), new Vector2(0.82f, 0.2f), new Vector2(0.4f, 0.1f), new Vector2(0f, 0.09f),
            };
            const int seg = 16;
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int i = 0; i < p.Length; i++)
                for (int s = 0; s < seg; s++)
                {
                    float a = Mathf.PI * 2f * s / seg;
                    v.Add(new Vector3(Mathf.Cos(a) * p[i].x, p[i].y, Mathf.Sin(a) * p[i].x));
                }
            for (int i = 0; i < p.Length - 1; i++)
                for (int s = 0; s < seg; s++)
                {
                    int a = i * seg + s, b = i * seg + (s + 1) % seg, a2 = a + seg, b2 = b + seg;
                    t.Add(a); t.Add(a2); t.Add(b);
                    t.Add(b); t.Add(a2); t.Add(b2);
                }
            // the foot's underside
            int c0 = v.Count;
            v.Add(Vector3.zero);
            for (int s = 0; s < seg; s++) { t.Add(c0); t.Add(s); t.Add((s + 1) % seg); }
            var m = new Mesh { name = "VBS_TossBowl" };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            var clay = new Color(0.72f, 0.4f, 0.24f);
            var inside = new Color(0.58f, 0.3f, 0.17f);
            var ns = m.normals;
            var col = new Color[ns.Length];
            for (int i = 0; i < ns.Length; i++) col[i] = Lit(i >= 4 * seg ? inside : clay, ns[i]);
            m.colors = col;
            m.RecalculateBounds();
            return m;
        }

        /// <summary>A convex disc (n-gon prism) of radius 1 and height 1, top at y 0.</summary>
        public static Mesh Disc(int n = 16)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int s = 0; s < n; s++)
            {
                float a = Mathf.PI * 2f * s / n;
                v.Add(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)));
                v.Add(new Vector3(Mathf.Cos(a), -1f, Mathf.Sin(a)));
            }
            for (int s = 1; s < n - 1; s++) { t.Add(0); t.Add(2 * (s + 1)); t.Add(2 * s); t.Add(1); t.Add(2 * s + 1); t.Add(2 * (s + 1) + 1); }
            for (int s = 0; s < n; s++)
            {
                int a = 2 * s, b = 2 * ((s + 1) % n);
                t.Add(a); t.Add(b); t.Add(a + 1);
                t.Add(b); t.Add(b + 1); t.Add(a + 1);
            }
            var m = new Mesh { name = "VBS_TossDisc" };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// An invisible catch basin under `root` (world-scale 1, y up): a floor disc whose top is at floorTop (local y) and
        /// `segments` rim boxes standing on it at `radius`, `rimHeight` tall. Layer 2 (Ignore Raycast): no ray ever hits it.
        /// </summary>
        public static void Basin(Transform root, Mesh disc, PhysicsMaterial mat, float radius, float floorTop, float rimHeight,
                                 float rimThick, int segments = 12)
        {
            var floor = new GameObject("Floor") { layer = 2 };
            floor.transform.SetParent(root, false);
            floor.transform.localPosition = new Vector3(0f, floorTop, 0f);
            floor.transform.localScale = new Vector3(radius, 0.04f, radius);
            var mc = floor.AddComponent<MeshCollider>();
            mc.sharedMesh = disc;
            mc.convex = true;
            mc.sharedMaterial = mat;
            float w = 2f * Mathf.PI * radius / segments * 1.15f;
            for (int s = 0; s < segments; s++)
            {
                float a = 360f * s / segments;
                var rim = new GameObject("Rim" + s) { layer = 2 };
                rim.transform.SetParent(root, false);
                rim.transform.localRotation = Quaternion.Euler(0f, a, 0f);
                rim.transform.localPosition = rim.transform.localRotation * new Vector3(0f, 0f, radius + rimThick * 0.5f) +
                                              Vector3.up * (floorTop + rimHeight * 0.5f - 0.01f);
                var bc = rim.AddComponent<BoxCollider>();
                bc.size = new Vector3(w, rimHeight + 0.02f, rimThick);
                bc.sharedMaterial = mat;
            }
        }
    }
}
