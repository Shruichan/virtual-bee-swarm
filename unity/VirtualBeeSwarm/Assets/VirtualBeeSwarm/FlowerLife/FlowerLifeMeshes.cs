using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm.FlowerLife
{
    /// <summary>
    /// The flower species FlowerLife draws, in the layout FlowerLife.shader reads: metres, pivot at the stem base, +Y up,
    /// vertex colour = baked colour, vertex alpha = sway weight (0 at the base, 1 at the head).
    /// Art first: Daniel's flowers and grass tuft (fl_art_daisy / bluebell / lavender / grass, baked from Art/Flowers and
    /// Art/GrassTuft by FlowerLifeArtBake), then the art lane's bloom kit (Art/Bloom/Resources/bloom_poppy, bloom_clover, bloom_lavender, bloom_salvia,
    /// bloom_phacelia, bloom_sunflower, bloom_grass; same layout) is used for every species it has. Anything missing is
    /// built here: low-poly bee flowers, 20-60 tris each. `height` is the mesh's own height; FlowerLife scales each
    /// instance to the height it wants (table blooms are 6-13 cm, floor blooms 25-50 cm).
    /// </summary>
    public static class FlowerLifeMeshes
    {
        public enum Kind { Poppy, Clover, Lavender, Salvia, Bluebell, Sunflower, Daisy, Grass }
        public const int KindCount = 8;

        public sealed class Species
        {
            public Kind kind;
            public Mesh mesh;
            public float height;     // metres, mesh space
            public Vector3 head;     // where a bee lands, mesh space
            public int tris;
            public bool art;
            public bool bloom;       // has a head a bee wants (not grass)
        }

        // per species: Daniel's models first (baked by FlowerLifeArtBake into FlowerLife/Resources), then the bloom kit
        static readonly string[][] ArtNames =
        {
            new[] { "bloom_poppy" }, new[] { "bloom_clover" }, new[] { "fl_art_lavender", "bloom_lavender" }, new[] { "bloom_salvia" },
            new[] { "fl_art_bluebell", "bloom_phacelia" }, new[] { "bloom_sunflower" }, new[] { "fl_art_daisy" }, new[] { "fl_art_grass", "bloom_grass" },
        };

        static readonly Color Stem = new Color(0.22f, 0.46f, 0.2f), Leaf = new Color(0.32f, 0.62f, 0.27f);
        static readonly Color GrassLo = new Color(0.27f, 0.55f, 0.24f), GrassHi = new Color(0.58f, 0.82f, 0.38f);

        public static Species[] Build(bool allowArt, out int artCount)
        {
            var list = new Species[KindCount];
            artCount = 0;
            for (int k = 0; k < KindCount; k++)
            {
                Species s = null;
                // Keep dense grass tufts procedural and low-poly. The imported tuft is ~296 tris per
                // instance; that is fine for a hero prop, but too costly for a meadow ground layer.
                for (int a = 0; allowArt && (Kind)k != Kind.Grass && s == null && a < ArtNames[k].Length; a++)
                {
                    var m = Resources.Load<Mesh>(ArtNames[k][a]);
                    if (m != null && m.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color))
                    {
                        var b = m.bounds;
                        s = new Species
                        {
                            kind = (Kind)k, mesh = m, height = Mathf.Max(0.01f, b.max.y), art = true,
                            head = new Vector3(b.center.x, b.max.y, b.center.z), tris = (int)(m.GetIndexCount(0) / 3),
                        };
                        artCount++;
                    }
                }
                if (s == null) s = Procedural((Kind)k);
                s.bloom = s.kind != Kind.Grass;
                list[k] = s;
            }
            return list;
        }

        // ------------------------------------------------------------------ procedural kit
        sealed class B
        {
            public readonly List<Vector3> v = new List<Vector3>(256);
            public readonly List<Color> c = new List<Color>(256);
            public readonly List<int> t = new List<int>(512);
            public float height = 0.1f;

            public int Vert(Vector3 p, Color col)
            {
                col.a = Mathf.Clamp01(p.y / height);
                v.Add(p);
                c.Add(col);
                return v.Count - 1;
            }

            public void Tri(Vector3 a, Vector3 b, Vector3 d, Color ca, Color cb, Color cd)
            {
                int i = Vert(a, ca), j = Vert(b, cb), k = Vert(d, cd);
                t.Add(i); t.Add(j); t.Add(k);
            }

            public void Tri(Vector3 a, Vector3 b, Vector3 d, Color col) => Tri(a, b, d, col, col, col);

            public void Quad(Vector3 a, Vector3 b, Vector3 d, Vector3 e, Color lo, Color hi)
            {
                Tri(a, b, d, lo, lo, hi);
                Tri(a, d, e, lo, hi, hi);
            }

            /// <summary>A thin two-plane stem from `from` to `to`.</summary>
            public void StemTo(Vector3 from, Vector3 to, float w, Color col)
            {
                Color hi = col * 1.18f;
                Quad(from + new Vector3(-w, 0f, 0f), from + new Vector3(w, 0f, 0f), to + new Vector3(w * 0.6f, 0f, 0f), to + new Vector3(-w * 0.6f, 0f, 0f), col, hi);
                Quad(from + new Vector3(0f, 0f, -w), from + new Vector3(0f, 0f, w), to + new Vector3(0f, 0f, w * 0.6f), to + new Vector3(0f, 0f, -w * 0.6f), col * 0.88f, hi * 0.9f);
            }

            public void LeafAt(Vector3 at, float yaw, float len, float lift, Color col)
            {
                var q = Quaternion.Euler(0f, yaw, 0f);
                Vector3 tip = at + q * new Vector3(0f, len * lift, len);
                Vector3 l = at + q * new Vector3(-len * 0.22f, len * lift * 0.45f, len * 0.5f);
                Vector3 r = at + q * new Vector3(len * 0.22f, len * lift * 0.45f, len * 0.5f);
                Tri(at, l, tip, col * 0.85f, col, col * 1.15f);
                Tri(at, tip, r, col * 0.85f, col * 1.15f, col * 0.95f);
            }

            /// <summary>A double pyramid (8 tris) around `c`: buds, clover heads, lavender whorls.</summary>
            public void Bud(Vector3 c0, float r, float h, Color col)
            {
                Vector3 top = c0 + Vector3.up * h, bot = c0 - Vector3.up * h * 0.6f;
                for (int i = 0; i < 4; i++)
                {
                    float a0 = i * Mathf.PI * 0.5f + 0.4f, a1 = (i + 1) * Mathf.PI * 0.5f + 0.4f;
                    Vector3 p0 = c0 + new Vector3(Mathf.Cos(a0) * r, 0f, Mathf.Sin(a0) * r);
                    Vector3 p1 = c0 + new Vector3(Mathf.Cos(a1) * r, 0f, Mathf.Sin(a1) * r);
                    float shade = i % 2 == 0 ? 1f : 0.86f;
                    Tri(p0, p1, top, col * shade, col * shade, col * (shade * 1.15f));
                    Tri(p1, p0, bot, col * (shade * 0.8f), col * (shade * 0.8f), col * (shade * 0.65f));
                }
            }

            /// <summary>n petals round `c`, each a kite, opening `cup` (0 flat disc .. 1 upright cup) about `axis`.</summary>
            public void Petals(Vector3 c0, Quaternion face, int n, float len, float width, float cup, Color inner, Color outer)
            {
                for (int i = 0; i < n; i++)
                {
                    var q = face * Quaternion.Euler(0f, i * 360f / n, 0f);
                    Vector3 dir = q * new Vector3(0f, cup, 1f - cup * 0.55f).normalized;
                    Vector3 side = q * Vector3.right;
                    Vector3 mid = c0 + dir * (len * 0.55f);
                    Vector3 tip = c0 + dir * len + (q * Vector3.up) * (len * 0.12f * (1f - cup));
                    float shade = i % 2 == 0 ? 1f : 0.88f;
                    Tri(c0, mid - side * (width * 0.5f), tip, inner * shade, outer * shade, outer * (shade * 1.08f));
                    Tri(c0, tip, mid + side * (width * 0.5f), inner * shade, outer * (shade * 1.08f), outer * (shade * 0.92f));
                }
            }

            public void Disc(Vector3 c0, Quaternion face, float r, Color col)
            {
                for (int i = 0; i < 6; i++)
                {
                    Vector3 p0 = c0 + face * new Vector3(Mathf.Cos(i * Mathf.PI / 3f) * r, 0.0015f, Mathf.Sin(i * Mathf.PI / 3f) * r);
                    Vector3 p1 = c0 + face * new Vector3(Mathf.Cos((i + 1) * Mathf.PI / 3f) * r, 0.0015f, Mathf.Sin((i + 1) * Mathf.PI / 3f) * r);
                    Tri(c0 + face * new Vector3(0f, 0.004f, 0f), p0, p1, col * 1.15f, col * (i % 2 == 0 ? 1f : 0.85f), col * (i % 2 == 0 ? 1f : 0.85f));
                }
            }

            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(v);
                m.SetColors(c);
                m.SetTriangles(t, 0);
                m.RecalculateBounds();
                var b = m.bounds;
                b.Expand(new Vector3(0.12f, 0.04f, 0.12f)); // sway, lean and wilt move vertices in the shader
                m.bounds = b;
                m.UploadMeshData(false);
                return m;
            }
        }

        static Species Procedural(Kind kind)
        {
            var b = new B();
            Vector3 head;
            switch (kind)
            {
                case Kind.Poppy: // California poppy: an orange cup
                {
                    b.height = 0.1f;
                    Vector3 top = new Vector3(0.006f, 0.082f, 0f);
                    b.StemTo(Vector3.zero, top, 0.0022f, Stem);
                    b.LeafAt(new Vector3(0f, 0.012f, 0f), 30f, 0.03f, 0.5f, Leaf * 0.9f);
                    b.LeafAt(new Vector3(0f, 0.02f, 0f), 200f, 0.026f, 0.6f, Leaf * 0.9f);
                    b.Petals(top, Quaternion.Euler(8f, 0f, 0f), 4, 0.024f, 0.026f, 0.72f, new Color(1f, 0.74f, 0.16f), new Color(0.99f, 0.5f, 0.08f));
                    head = top + Vector3.up * 0.014f;
                    break;
                }
                case Kind.Clover: // a pink-white ball on a short stem, three leaves
                {
                    b.height = 0.062f;
                    Vector3 top = new Vector3(0f, 0.046f, 0f);
                    b.StemTo(Vector3.zero, top, 0.002f, Stem);
                    for (int i = 0; i < 3; i++) b.LeafAt(new Vector3(0f, 0.006f, 0f), i * 120f + 20f, 0.022f, 0.25f, Leaf);
                    b.Bud(top + Vector3.up * 0.006f, 0.0105f, 0.012f, new Color(0.95f, 0.66f, 0.8f));
                    head = top + Vector3.up * 0.018f;
                    break;
                }
                case Kind.Lavender: // a lilac spike
                {
                    b.height = 0.125f;
                    Vector3 top = new Vector3(-0.004f, 0.118f, 0.003f);
                    b.StemTo(Vector3.zero, top, 0.0018f, Stem * 1.05f);
                    b.LeafAt(new Vector3(0f, 0.014f, 0f), 110f, 0.03f, 1.1f, Leaf * 0.85f);
                    b.LeafAt(new Vector3(0f, 0.014f, 0f), 290f, 0.03f, 1.1f, Leaf * 0.85f);
                    for (int i = 0; i < 4; i++)
                    {
                        float y = 0.07f + i * 0.0135f;
                        b.Bud(new Vector3(-0.004f * y / 0.118f, y, 0.003f * y / 0.118f), 0.0075f - i * 0.0011f, 0.008f, new Color(0.66f, 0.52f, 0.9f) * (0.92f + 0.05f * i));
                    }
                    head = top + Vector3.up * 0.004f;
                    break;
                }
                case Kind.Salvia: // violet whorls up a stem
                {
                    b.height = 0.11f;
                    Vector3 top = new Vector3(0.003f, 0.104f, -0.002f);
                    b.StemTo(Vector3.zero, top, 0.002f, Stem);
                    b.LeafAt(new Vector3(0f, 0.016f, 0f), 60f, 0.034f, 0.35f, Leaf);
                    b.LeafAt(new Vector3(0f, 0.016f, 0f), 240f, 0.034f, 0.35f, Leaf);
                    for (int i = 0; i < 3; i++)
                        b.Bud(new Vector3(0.001f * i, 0.055f + i * 0.02f, 0f), 0.0095f - i * 0.0012f, 0.006f, new Color(0.47f, 0.36f, 0.9f));
                    head = top + Vector3.up * 0.004f;
                    break;
                }
                case Kind.Bluebell: // phacelia / bluebell: a curled stem with hanging blue bells
                {
                    b.height = 0.095f;
                    Vector3 mid = new Vector3(0.004f, 0.06f, 0f), top = new Vector3(0.022f, 0.088f, 0f);
                    b.StemTo(Vector3.zero, mid, 0.002f, Stem);
                    b.StemTo(mid, top, 0.0014f, Stem * 1.1f);
                    b.LeafAt(new Vector3(0f, 0.014f, 0f), 180f, 0.032f, 0.5f, Leaf);
                    for (int i = 0; i < 3; i++)
                    {
                        Vector3 at = Vector3.Lerp(mid, top, 0.25f + i * 0.375f) + new Vector3(0.004f, -0.004f, (i - 1) * 0.008f);
                        b.Petals(at, Quaternion.Euler(165f, i * 40f, 0f), 4, 0.013f, 0.012f, 0.8f, new Color(0.62f, 0.72f, 1f), new Color(0.42f, 0.56f, 0.96f));
                    }
                    head = top + Vector3.up * 0.003f;
                    break;
                }
                case Kind.Sunflower: // tall, yellow rays, brown disc, facing forward-up
                {
                    b.height = 0.155f;
                    Vector3 top = new Vector3(0f, 0.138f, 0.004f);
                    b.StemTo(Vector3.zero, top, 0.003f, Stem);
                    b.LeafAt(new Vector3(0f, 0.045f, 0f), 80f, 0.042f, 0.3f, Leaf);
                    b.LeafAt(new Vector3(0f, 0.075f, 0f), 260f, 0.038f, 0.3f, Leaf);
                    var face = Quaternion.Euler(52f, 0f, 0f);
                    b.Petals(top, face, 10, 0.027f, 0.013f, 0.06f, new Color(1f, 0.72f, 0.1f), new Color(1f, 0.86f, 0.2f));
                    b.Disc(top, face, 0.0115f, new Color(0.42f, 0.25f, 0.1f));
                    head = top + face * new Vector3(0f, 0.008f, 0f);
                    break;
                }
                case Kind.Daisy: // aster / daisy: white rays, yellow centre
                {
                    b.height = 0.085f;
                    Vector3 top = new Vector3(0.002f, 0.074f, 0f);
                    b.StemTo(Vector3.zero, top, 0.002f, Stem);
                    b.LeafAt(new Vector3(0f, 0.012f, 0f), 140f, 0.028f, 0.4f, Leaf);
                    var face = Quaternion.Euler(24f, 0f, 0f);
                    b.Petals(top, face, 8, 0.019f, 0.011f, 0.12f, new Color(0.93f, 0.9f, 0.98f), new Color(1f, 1f, 1f));
                    b.Disc(top, face, 0.0065f, new Color(1f, 0.8f, 0.16f));
                    head = top + face * new Vector3(0f, 0.007f, 0f);
                    break;
                }
                default: // grass tuft: five blades
                {
                    b.height = 0.075f;
                    for (int i = 0; i < 5; i++)
                    {
                        var q = Quaternion.Euler(0f, i * 72f + 17f, 0f);
                        float len = 0.05f + 0.025f * ((i * 7) % 5) / 4f, lean = 0.012f + 0.01f * (i % 3);
                        Vector3 r = q * Vector3.right * 0.0042f, o = q * Vector3.forward;
                        Vector3 a0 = o * 0.004f - r, a1 = o * 0.004f + r;
                        Vector3 m0 = o * (0.004f + lean * 0.5f) + Vector3.up * (len * 0.55f) - r * 0.7f, m1 = m0 + r * 1.4f;
                        Vector3 tip = o * (0.004f + lean * 1.6f) + Vector3.up * len;
                        Color mc = Color.Lerp(GrassLo, GrassHi, 0.55f);
                        b.Tri(a0, a1, m1, GrassLo, GrassLo, mc);
                        b.Tri(a0, m1, m0, GrassLo, mc, mc);
                        b.Tri(m0, m1, tip, mc, mc, GrassHi);
                    }
                    head = new Vector3(0f, 0.06f, 0f);
                    break;
                }
            }
            var mesh = b.ToMesh("VBS_FlowerLife_" + kind);
            return new Species { kind = kind, mesh = mesh, height = b.height, head = head, tris = b.t.Count / 3 };
        }

        /// <summary>A small two-sided kite (2 tris, ~1 unit long) for petals and pollen: drawn with the game's unlit shader.</summary>
        public static Mesh BuildPetal()
        {
            var m = new Mesh { name = "VBS_FlowerLife_Petal" };
            m.vertices = new[]
            {
                new Vector3(0f, 0f, -0.5f), new Vector3(-0.32f, 0.06f, 0.05f), new Vector3(0f, 0f, 0.5f), new Vector3(0.32f, 0.06f, 0.05f),
            };
            m.colors = new[] { new Color(0.85f, 0.85f, 0.85f, 1f), Color.white, Color.white, new Color(0.92f, 0.92f, 0.92f, 1f) };
            m.uv = new[] { new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            m.UploadMeshData(false);
            return m;
        }

        /// <summary>A unit disc quad in XZ (uv 0..1) for the soft shade under a plant.</summary>
        public static Mesh BuildShadeQuad()
        {
            var m = new Mesh { name = "VBS_FlowerLife_Shade" };
            m.vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            m.UploadMeshData(false);
            return m;
        }
    }
}
