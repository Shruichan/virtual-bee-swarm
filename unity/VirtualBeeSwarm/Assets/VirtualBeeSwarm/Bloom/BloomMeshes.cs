using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>
    /// Builds the bloom meshes with the vertex layout Bloom.shader reads (see its header). Low-poly, baked shading.
    /// The flower clump: 12 bloom slots of real bee flowers on a 0.17 m disk + grass, ~200 tris; each instance's mask
    /// shows some of the slots and its palette moves petals toward an alt colour, so neighbours differ.
    ///   salvia (violet whorled spike), lavender (lilac spike), phacelia (blue curled head), clover (white/pink ball),
    ///   California poppy (orange cup), sunflower (tall, yellow rays, brown disk)
    /// Art (the art lane's bloom kit, art/blender/scripts/bloom_flowers.py): Art/Bloom/Resources/bloom_salvia,
    /// bloom_lavender, bloom_phacelia, bloom_clover, bloom_poppy, bloom_sunflower, bloom_grass (.fbx; metres, pivot at
    /// the stem base, +Y up, vertex colour = baked colour, vertex alpha = sway weight; BloomArtImport makes them
    /// readable). With all seven present (bloom.art auto/on) the clump is the art clump instead: one of each species
    /// (~100 tris each) + a grass tuft, 2-4 shown per instance, and fewer clumps so the field stays under 150k tris.
    /// bloom_vine_leaf / bloom_vine_blossom (front +Z out of the wall, tip +Y) replace the vines' leaves and blossoms.
    /// </summary>
    public sealed class BloomMeshBuilder
    {
        public readonly List<Vector3> V = new List<Vector3>(4096);
        public readonly List<Color> C = new List<Color>(4096);
        public readonly List<Vector4> G = new List<Vector4>(4096); // birth offset, sway weight, slot, 0
        public readonly List<Vector3> P = new List<Vector3>(4096); // pivot
        public readonly List<Vector3> F = new List<Vector3>(4096); // from
        public readonly List<Vector3> A = new List<Vector3>(4096); // alt colour
        public readonly List<int> T = new List<int>(8192);

        // the current part's attributes
        public float Birth, Slot = -1f;
        public Vector3 Pivot, From;
        public Color Alt = Color.white;
        public float SwayBase; // sway weight = (y - SwayBase)^2 / 0.25, per vertex

        public int Vert(Vector3 p, Color c, float petal)
        {
            V.Add(p);
            c.a = petal;
            C.Add(c);
            float h = Mathf.Max(0f, p.y - SwayBase);
            G.Add(new Vector4(Birth, h * h / 0.25f, Slot, 0f));
            P.Add(Pivot);
            F.Add(From);
            A.Add(new Vector3(Alt.r, Alt.g, Alt.b));
            return V.Count - 1;
        }

        public void Tri(int a, int b, int c) { T.Add(a); T.Add(b); T.Add(c); }

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color ca, Color cb, Color cc, float petal)
        {
            int i = Vert(a, ca, petal), j = Vert(b, cb, petal), k = Vert(c, cc, petal);
            Tri(i, j, k);
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color ca, Color cb, float petal)
        {
            int i = Vert(a, ca, petal), j = Vert(b, ca, petal), k = Vert(c, cb, petal), l = Vert(d, cb, petal);
            Tri(i, j, k);
            Tri(i, k, l);
        }

        struct ArtArrays
        {
            public Vector3[] v;
            public Color[] c;
            public int[] t;
        }

        // the art meshes' arrays, read once (the vines append ~1000 leaves and blossoms per build)
        static readonly Dictionary<Mesh, ArtArrays> _artCache = new Dictionary<Mesh, ArtArrays>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetCache() => _artCache.Clear();

        /// <summary>Appends a mesh (transformed) with the current part attributes. Art vertex alpha is the sway weight
        /// (times swayScale); art keeps its own colours (no palette shift), except that with a tint the petal colours
        /// (not the greens, not a yellow centre) take the tint's hue at the art's shading.</summary>
        public bool Append(Mesh m, Matrix4x4 xf, Color fallback, float swayScale, Color? tint = null)
        {
            if (m == null || !m.isReadable) return false;
            if (!_artCache.TryGetValue(m, out var arr))
                _artCache[m] = arr = new ArtArrays { v = m.vertices, c = m.colors, t = m.triangles };
            var mv = arr.v;
            var mc = arr.c;
            var mt = arr.t;
            bool hasC = mc != null && mc.Length == mv.Length;
            int b = V.Count;
            for (int i = 0; i < mv.Length; i++)
            {
                Color c = hasC ? mc[i] : fallback;
                if (tint.HasValue && hasC && !(c.g > c.r && c.g > c.b) && !(c.r > 0.7f && c.g > 0.55f && c.b < 0.4f))
                {
                    float lum = 0.3f * c.r + 0.59f * c.g + 0.11f * c.b, kk = Mathf.Lerp(0.75f, 1.15f, lum);
                    Color t = tint.Value;
                    c = new Color(Mathf.Clamp01(t.r * kk), Mathf.Clamp01(t.g * kk), Mathf.Clamp01(t.b * kk), c.a);
                }
                int k = Vert(xf.MultiplyPoint3x4(mv[i]), c, 0f);
                var g = G[k];
                g.y = (hasC ? mc[i].a : 0f) * swayScale;
                G[k] = g;
            }
            for (int i = 0; i < mt.Length; i++) T.Add(b + mt[i]);
            return true;
        }

        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name };
            m.indexFormat = V.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m.SetVertices(V);
            m.SetColors(C);
            m.SetUVs(0, G);
            m.SetUVs(1, P);
            m.SetUVs(2, F);
            m.SetUVs(3, A);
            m.SetTriangles(T, 0, true);
            m.bounds = new Bounds(m.bounds.center, m.bounds.size + Vector3.one * 0.2f); // sway + overshoot
            m.UploadMeshData(false);
            return m;
        }

        public int Tris => T.Count / 3;
    }

    public static class BloomMeshes
    {
        public const int Slots = 12;
        public enum Species { Salvia, Lavender, Phacelia, Clover, Poppy, Sunflower }

        static readonly Species[] SlotSpecies =
        {
            Species.Salvia, Species.Lavender, Species.Phacelia, Species.Clover, Species.Poppy, Species.Salvia,
            Species.Lavender, Species.Phacelia, Species.Clover, Species.Poppy, Species.Sunflower, Species.Clover,
        };

        public struct SlotInfo
        {
            public Vector3 head; // clump-local, where a bee lands
            public float birth;
            public Species species;
        }

        static readonly Color Stem = new Color(0.24f, 0.5f, 0.2f), StemDark = new Color(0.13f, 0.3f, 0.12f);

        public static string ArtUsed = "";

        /// <summary>The clump mesh and where each slot's flower head is (for the visiting bees).</summary>
        public static Mesh BuildClump(out SlotInfo[] slots, int seed = 3)
        {
            var b = new BloomMeshBuilder();
            var rnd = new System.Random(seed);
            slots = new SlotInfo[Slots];
            var art = new List<string>();
            // grass first (always on, born first)
            b.Slot = -1f;
            b.Birth = 0f;
            for (int tuft = 0; tuft < 2; tuft++)
            {
                float a = tuft * Mathf.PI + 0.6f;
                Vector3 c = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.08f;
                Grass(b, c, rnd, art);
            }
            for (int i = 0; i < Slots; i++)
            {
                float ang = i * 2.39996f; // golden angle
                float rad = 0.035f + 0.135f * Mathf.Sqrt((i + 0.5f) / Slots);
                Vector3 basePos = new Vector3(Mathf.Cos(ang) * rad, 0f, Mathf.Sin(ang) * rad);
                b.Slot = i;
                b.Birth = 0.05f + (float)rnd.NextDouble() * 0.3f;
                b.Pivot = b.From = basePos;
                b.SwayBase = 0f;
                var sp = SlotSpecies[i];
                float yaw = (float)rnd.NextDouble() * 360f;
                Vector3 head = Bloom(b, sp, basePos, yaw, rnd, art);
                slots[i] = new SlotInfo { head = head, birth = b.Birth, species = sp };
            }
            ArtUsed = "procedural";
            return b.ToMesh("VBS_BloomClump");
        }

        /// <summary>An art mesh from any Resources folder by file or mesh name; null if missing or not readable.</summary>
        public static Mesh Art(string pascal, string file, List<string> problems = null)
        {
            var m = Resources.Load<Mesh>(file);
            if (m == null) m = Resources.Load<Mesh>(pascal);
            if (m == null) return null;
            if (!m.isReadable) { problems?.Add(file + " not readable"); return null; }
            return m;
        }

        static Mesh ArtFor(Species sp, List<string> problems = null) => Art("Bloom_" + sp, "bloom_" + sp.ToString().ToLowerInvariant(), problems);
        static Mesh ArtGrass(List<string> problems = null) => Art("Bloom_Grass", "bloom_grass", problems);

        /// <summary>True when all six flowers and the grass tuft of the art kit load (readable).</summary>
        public static bool ArtKitPresent(out string why)
        {
            var problems = new List<string>();
            int n = 0;
            foreach (Species sp in System.Enum.GetValues(typeof(Species)))
                if (ArtFor(sp, problems) != null) n++;
            if (ArtGrass(problems) != null) n++;
            why = n == 7 ? "art kit (7 meshes)" : n == 0 && problems.Count == 0 ? "no art kit in Resources" : n + "/7 art meshes" +
                  (problems.Count > 0 ? " (" + string.Join(", ", problems) + ")" : "");
            return n == 7;
        }

        /// <summary>The art clump: one of each species round a grass tuft (slot = species index).</summary>
        public static Mesh BuildArtClump(out SlotInfo[] slots, int seed = 3)
        {
            var b = new BloomMeshBuilder();
            var rnd = new System.Random(seed);
            var species = (Species[])System.Enum.GetValues(typeof(Species));
            slots = new SlotInfo[species.Length];
            b.Slot = -1f;
            b.Birth = 0f;
            b.Pivot = b.From = Vector3.zero;
            b.Append(ArtGrass(), Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, (float)rnd.NextDouble() * 360f, 0f), Vector3.one * 0.8f),
                Stem, 0.8f);
            for (int i = 0; i < species.Length; i++)
            {
                float ang = i * 2.39996f + 0.4f;
                float rad = 0.05f + 0.11f * Mathf.Sqrt((i + 0.5f) / species.Length);
                Vector3 basePos = new Vector3(Mathf.Cos(ang) * rad, 0f, Mathf.Sin(ang) * rad);
                b.Slot = i;
                b.Birth = 0.05f + (float)rnd.NextDouble() * 0.3f;
                b.Pivot = b.From = basePos;
                var m = ArtFor(species[i]);
                var xf = Matrix4x4.TRS(basePos, Quaternion.Euler(0f, (float)rnd.NextDouble() * 360f, 0f), Vector3.one);
                b.Append(m, xf, Color.white, 1f);
                slots[i] = new SlotInfo { head = xf.MultiplyPoint3x4(TopVertex(m)) - Vector3.up * 0.01f, birth = b.Birth, species = species[i] };
            }
            ArtUsed = "art kit (" + species.Length + " species + grass)";
            return b.ToMesh("VBS_BloomClump (art)");
        }

        static Vector3 TopVertex(Mesh m)
        {
            var v = m.vertices;
            Vector3 best = Vector3.zero;
            for (int i = 0; i < v.Length; i++) if (v[i].y > best.y) best = v[i];
            return best;
        }

        static void Grass(BloomMeshBuilder b, Vector3 c, System.Random rnd, List<string> art)
        {
            b.Pivot = b.From = c;
            b.SwayBase = 0f;
            for (int k = 0; k < 5; k++)
            {
                float a = (float)rnd.NextDouble() * Mathf.PI * 2f;
                Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Vector3 side = new Vector3(-dir.z, 0f, dir.x);
                float h = 0.1f + (float)rnd.NextDouble() * 0.14f;
                Vector3 p0 = c + side * 0.008f, p1 = c - side * 0.008f;
                Vector3 tip = c + dir * (h * 0.35f) + Vector3.up * h;
                Color top = Color.Lerp(new Color(0.42f, 0.68f, 0.26f), new Color(0.55f, 0.75f, 0.3f), (float)rnd.NextDouble());
                b.Tri(p0, p1, tip, StemDark, StemDark, top, 0f);
            }
        }

        /// <summary>One flower; returns its head (clump-local).</summary>
        static Vector3 Bloom(BloomMeshBuilder b, Species sp, Vector3 basePos, float yaw, System.Random rnd, List<string> art)
        {
            float r01() => (float)rnd.NextDouble();
            var q = Quaternion.Euler(0f, yaw, 0f);
            Vector3 lean = q * new Vector3(0.04f * (r01() - 0.5f), 0f, 0.04f * (r01() - 0.5f));
            switch (sp)
            {
                case Species.Salvia:
                {
                    float h = 0.34f + r01() * 0.12f;
                    Vector3 top = basePos + lean + Vector3.up * h;
                    StemQuads(b, basePos, top, 0.006f, q);
                    var petal = new Color(0.45f, 0.24f, 0.8f);
                    b.Alt = new Color(0.28f, 0.32f, 0.88f);
                    for (int w = 0; w < 4; w++)
                    {
                        float k = 0.62f + w * 0.12f;
                        Vector3 at = Vector3.Lerp(basePos, top, k);
                        float len = 0.032f - w * 0.005f;
                        for (int j = 0; j < 3; j++)
                        {
                            float a = (yaw + w * 40f + j * 120f) * Mathf.Deg2Rad;
                            Vector3 d = new Vector3(Mathf.Cos(a), 0.55f, Mathf.Sin(a)).normalized;
                            Vector3 s = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a)) * 0.007f;
                            b.Tri(at + s, at - s, at + d * len, Shade(petal, 0.75f), Shade(petal, 0.75f), Shade(petal, 1.15f), 1f);
                        }
                    }
                    return top;
                }
                case Species.Lavender:
                {
                    float h = 0.3f + r01() * 0.1f;
                    Vector3 top = basePos + lean + Vector3.up * h;
                    StemQuads(b, basePos, top, 0.005f, q);
                    var petal = new Color(0.64f, 0.52f, 0.86f);
                    b.Alt = new Color(0.5f, 0.34f, 0.8f);
                    Bipyramid(b, Vector3.Lerp(basePos, top, 0.72f), 0.07f, 0.014f, yaw, petal);
                    Bipyramid(b, Vector3.Lerp(basePos, top, 0.9f), 0.06f, 0.012f, yaw + 45f, petal);
                    return top;
                }
                case Species.Phacelia:
                {
                    float h = 0.28f + r01() * 0.1f;
                    Vector3 top = basePos + lean + Vector3.up * h;
                    StemQuads(b, basePos, top, 0.005f, q);
                    var petal = new Color(0.46f, 0.52f, 0.95f);
                    b.Alt = new Color(0.62f, 0.46f, 0.92f);
                    // the curled cyme: 6 florets along a hook
                    Vector3 fwd = q * Vector3.forward;
                    for (int j = 0; j < 6; j++)
                    {
                        float a = j * 0.5f;
                        Vector3 at = top + fwd * (Mathf.Sin(a) * 0.035f) + Vector3.up * ((1f - Mathf.Cos(a)) * 0.02f - j * 0.002f);
                        float sz = 0.016f - j * 0.0015f;
                        Vector3 s = q * Vector3.right * sz;
                        b.Tri(at - s, at + s, at + Vector3.up * sz * 1.3f, Shade(petal, 0.85f), Shade(petal, 0.85f), Shade(petal, 1.2f), 1f);
                    }
                    return top + Vector3.up * 0.02f;
                }
                case Species.Clover:
                {
                    float h = 0.11f + r01() * 0.07f;
                    Vector3 top = basePos + lean * 0.5f + Vector3.up * h;
                    StemQuads(b, basePos, top, 0.004f, q);
                    var leaf = new Color(0.3f, 0.6f, 0.25f);
                    b.Alt = leaf;
                    for (int j = 0; j < 3; j++)
                    {
                        float a = (yaw + j * 120f) * Mathf.Deg2Rad;
                        Vector3 d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                        Vector3 s = new Vector3(-d.z, 0f, d.x) * 0.012f;
                        Vector3 at = basePos + Vector3.up * (h * 0.45f);
                        b.Tri(at, at + d * 0.03f + s, at + d * 0.03f - s, Shade(leaf, 0.8f), leaf, leaf, 0f);
                    }
                    var petal = new Color(0.96f, 0.93f, 0.96f);
                    b.Alt = new Color(0.96f, 0.6f, 0.78f);
                    Octahedron(b, top + Vector3.up * 0.016f, 0.02f, petal);
                    return top + Vector3.up * 0.036f;
                }
                case Species.Poppy:
                {
                    float h = 0.2f + r01() * 0.1f;
                    Vector3 top = basePos + lean + Vector3.up * h;
                    StemQuads(b, basePos, top, 0.0045f, q);
                    var petal = new Color(1f, 0.55f, 0.1f);
                    b.Alt = new Color(1f, 0.78f, 0.16f);
                    for (int j = 0; j < 4; j++)
                    {
                        float a = (yaw + j * 90f) * Mathf.Deg2Rad;
                        Vector3 d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                        Vector3 s = new Vector3(-d.z, 0f, d.x);
                        Vector3 o = top + d * 0.006f;
                        Vector3 tip = top + d * 0.032f + Vector3.up * 0.03f;
                        b.Quad(o - s * 0.006f, o + s * 0.006f, tip + s * 0.017f, tip - s * 0.017f, Shade(petal, 0.8f), Shade(petal, 1.15f), 1f);
                    }
                    return top + Vector3.up * 0.015f;
                }
                default: // sunflower
                {
                    float h = 0.62f + r01() * 0.16f;
                    Vector3 top = basePos + lean * 1.5f + Vector3.up * h;
                    StemQuads(b, basePos, top, 0.009f, q);
                    var leaf = new Color(0.28f, 0.55f, 0.22f);
                    b.Alt = leaf;
                    for (int j = 0; j < 2; j++)
                    {
                        float a = (yaw + 90f + j * 180f) * Mathf.Deg2Rad;
                        Vector3 d = new Vector3(Mathf.Cos(a), 0.25f, Mathf.Sin(a)).normalized;
                        Vector3 s = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a)) * 0.03f;
                        Vector3 at = Vector3.Lerp(basePos, top, 0.4f + j * 0.15f);
                        b.Quad(at, at + d * 0.05f + s, at + d * 0.1f, at + d * 0.05f - s, Shade(leaf, 0.8f), leaf, 0f);
                    }
                    // the face: tilted toward +forward(yaw), brown disk + 10 yellow rays
                    Quaternion face = q * Quaternion.Euler(-60f, 0f, 0f); // its +Z points mostly up, a little forward
                    Vector3 fz = face * Vector3.forward, fx = face * Vector3.right, fy = face * Vector3.up;
                    Vector3 disk = top + fz * 0.01f;
                    var brown = new Color(0.36f, 0.2f, 0.08f);
                    var ray = new Color(1f, 0.82f, 0.1f);
                    b.Alt = new Color(1f, 0.64f, 0.05f);
                    int center = b.Vert(disk + fz * 0.008f, brown, 0f);
                    int first = -1, prev = -1;
                    for (int j = 0; j < 6; j++)
                    {
                        float a = j * Mathf.PI / 3f;
                        int k = b.Vert(disk + (fx * Mathf.Cos(a) + fy * Mathf.Sin(a)) * 0.04f, Shade(brown, 0.8f), 0f);
                        if (prev >= 0) b.Tri(center, prev, k);
                        else first = k;
                        prev = k;
                    }
                    b.Tri(center, prev, first);
                    for (int j = 0; j < 10; j++)
                    {
                        float a = (j + 0.5f) * Mathf.PI / 5f;
                        Vector3 d = fx * Mathf.Cos(a) + fy * Mathf.Sin(a);
                        Vector3 s = (fx * -Mathf.Sin(a) + fy * Mathf.Cos(a)) * 0.012f;
                        b.Tri(disk + d * 0.036f + s, disk + d * 0.036f - s, disk + d * 0.085f - fz * 0.008f, ray, ray, Shade(ray, 1.1f), 1f);
                    }
                    return disk + fz * 0.015f;
                }
            }
        }

        static void StemQuads(BloomMeshBuilder b, Vector3 a, Vector3 c, float w, Quaternion q)
        {
            Vector3 x = q * Vector3.right * w, z = q * Vector3.forward * w;
            b.Quad(a - x, a + x, c + x * 0.6f, c - x * 0.6f, StemDark, Stem, 0f);
            b.Quad(a - z, a + z, c + z * 0.6f, c - z * 0.6f, StemDark, Stem, 0f);
        }

        static void Bipyramid(BloomMeshBuilder b, Vector3 c, float h, float r, float yaw, Color col)
        {
            Vector3 top = c + Vector3.up * (h * 0.5f), bot = c - Vector3.up * (h * 0.5f);
            int t = b.Vert(top, Shade(col, 1.2f), 1f), u = b.Vert(bot, Shade(col, 0.7f), 1f);
            var ring = new int[4];
            for (int i = 0; i < 4; i++)
            {
                float a = (yaw + i * 90f) * Mathf.Deg2Rad;
                ring[i] = b.Vert(c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r, Shade(col, i % 2 == 0 ? 1f : 0.85f), 1f);
            }
            for (int i = 0; i < 4; i++)
            {
                b.Tri(t, ring[i], ring[(i + 1) % 4]);
                b.Tri(u, ring[(i + 1) % 4], ring[i]);
            }
        }

        static void Octahedron(BloomMeshBuilder b, Vector3 c, float r, Color col)
        {
            int t = b.Vert(c + Vector3.up * r, Shade(col, 1.1f), 1f), u = b.Vert(c - Vector3.up * r, Shade(col, 0.75f), 1f);
            var ring = new int[4];
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f + 0.4f;
                ring[i] = b.Vert(c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r, Shade(col, i % 2 == 0 ? 0.95f : 0.85f), 1f);
            }
            for (int i = 0; i < 4; i++)
            {
                b.Tri(t, ring[i], ring[(i + 1) % 4]);
                b.Tri(u, ring[(i + 1) % 4], ring[i]);
            }
        }

        public static Color Shade(Color c, float k) => new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), c.a);

        /// <summary>The clump's sway in instance space, exactly as Bloom.shader does it (for the bees' landing spot).</summary>
        public static Vector3 Sway(Vector3 p, float clock, float phase, float scale, float wind = 0.02f, float hz = 1.6f)
        {
            float h = Mathf.Max(0f, p.y);
            float w = h * h / 0.25f * scale;
            float t = clock * hz + phase;
            p.x += wind * w * Mathf.Sin(t + p.y * 3f);
            p.z += wind * w * Mathf.Cos(t * 0.83f + p.y * 2.3f);
            return p;
        }
    }
}
