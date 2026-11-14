using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>Tiny procedural placeholder meshes (the modeler replaces them). Built once; the scene builder saves them as assets.</summary>
    public static class GameMeshes
    {
        /// <summary>
        /// A low-poly bee pointing +Z, 1 unit long (scale it to 3-4 cm): striped spindle body with vertex colors + two
        /// wing quads. ~50 vertices. Needs a material with vertex colors on (VirtualBeeSwarm/Unlit _UseVertexColor = 1).
        /// </summary>
        public static Mesh CreateBeeMesh()
        {
            float[] z = { -0.5f, -0.38f, -0.24f, -0.08f, 0.08f, 0.22f, 0.36f, 0.46f };
            float[] r = { 0.05f, 0.17f, 0.22f, 0.22f, 0.17f, 0.14f, 0.15f, 0.08f };
            var yellow = new Color(1f, 0.78f, 0.12f, 1f);
            var black = new Color(0.08f, 0.06f, 0.04f, 1f);
            var head = new Color(0.15f, 0.12f, 0.08f, 1f);
            Color[] ringColor = { black, yellow, black, yellow, black, yellow, head, head };
            const int seg = 6;

            var v = new List<Vector3>();
            var c = new List<Color>();
            var t = new List<int>();
            for (int i = 0; i < z.Length; i++)
            {
                for (int s = 0; s < seg; s++)
                {
                    float a = s * Mathf.PI * 2f / seg;
                    v.Add(new Vector3(Mathf.Cos(a) * r[i], Mathf.Sin(a) * r[i] * 0.9f, z[i]));
                    c.Add(ringColor[i]);
                }
            }
            for (int i = 0; i < z.Length - 1; i++)
            {
                for (int s = 0; s < seg; s++)
                {
                    int a = i * seg + s, b = i * seg + (s + 1) % seg, a2 = a + seg, b2 = b + seg;
                    t.Add(a); t.Add(b); t.Add(a2);
                    t.Add(b); t.Add(b2); t.Add(a2);
                }
            }
            int tail = v.Count;
            v.Add(new Vector3(0f, 0f, -0.56f)); c.Add(black);
            int nose = v.Count;
            v.Add(new Vector3(0f, 0f, 0.52f)); c.Add(head);
            int last = (z.Length - 1) * seg;
            for (int s = 0; s < seg; s++)
            {
                t.Add(tail); t.Add((s + 1) % seg); t.Add(s);
                t.Add(nose); t.Add(last + s); t.Add(last + (s + 1) % seg);
            }

            // wings (double-sided quads, pale)
            var wing = new Color(0.85f, 0.93f, 1f, 1f);
            AddQuad(v, c, t, wing, new Vector3(-0.03f, 0.17f, 0.16f), new Vector3(-0.48f, 0.34f, 0.12f),
                new Vector3(-0.46f, 0.31f, -0.16f), new Vector3(-0.03f, 0.17f, -0.04f));
            AddQuad(v, c, t, wing, new Vector3(0.03f, 0.17f, -0.04f), new Vector3(0.46f, 0.31f, -0.16f),
                new Vector3(0.48f, 0.34f, 0.12f), new Vector3(0.03f, 0.17f, 0.16f));

            var mesh = new Mesh { name = "VBS_Bee" };
            mesh.SetVertices(v);
            mesh.SetColors(c);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static void AddQuad(List<Vector3> v, List<Color> c, List<int> t, Color col, Vector3 a, Vector3 b, Vector3 d, Vector3 e)
        {
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(d); v.Add(e);
            c.Add(col); c.Add(col); c.Add(col); c.Add(col);
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
            t.Add(i); t.Add(i + 2); t.Add(i + 3);
            t.Add(i); t.Add(i + 2); t.Add(i + 1); // back side
            t.Add(i); t.Add(i + 3); t.Add(i + 2);
        }

        /// <summary>
        /// Hexagonal prism, pointy-top, axis along Z: the front face (z = -depth/2) faces a viewer at -Z, so it sits
        /// flat on a vertical panel with no rotation. radius = center to corner.
        /// </summary>
        public static Mesh CreateHexPrism(float radius, float depth, string name = "VBS_Hex")
        {
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var t = new List<int>();
            float zf = -depth * 0.5f, zb = depth * 0.5f;

            // front face: center + 6 corners
            v.Add(new Vector3(0f, 0f, zf)); uv.Add(new Vector2(0.5f, 0.5f));
            for (int k = 0; k < 6; k++)
            {
                float a = Mathf.PI * 0.5f + k * Mathf.PI / 3f;
                v.Add(new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, zf));
                uv.Add(new Vector2(Mathf.Cos(a) * 0.5f + 0.5f, Mathf.Sin(a) * 0.5f + 0.5f));
            }
            for (int k = 0; k < 6; k++)
            {
                t.Add(0); t.Add(1 + (k + 1) % 6); t.Add(1 + k);
            }
            // back face
            int bc = v.Count;
            v.Add(new Vector3(0f, 0f, zb)); uv.Add(new Vector2(0.5f, 0.5f));
            for (int k = 0; k < 6; k++)
            {
                float a = Mathf.PI * 0.5f + k * Mathf.PI / 3f;
                v.Add(new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, zb));
                uv.Add(new Vector2(Mathf.Cos(a) * 0.5f + 0.5f, Mathf.Sin(a) * 0.5f + 0.5f));
            }
            for (int k = 0; k < 6; k++)
            {
                t.Add(bc); t.Add(bc + 1 + k); t.Add(bc + 1 + (k + 1) % 6);
            }
            // sides
            for (int k = 0; k < 6; k++)
            {
                int f0 = 1 + k, f1 = 1 + (k + 1) % 6, b0 = bc + 1 + k, b1 = bc + 1 + (k + 1) % 6;
                t.Add(f0); t.Add(f1); t.Add(b1);
                t.Add(f0); t.Add(b1); t.Add(b0);
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(v);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Soft round dot (white, alpha falloff) for embers / smoke particles.</summary>
        public static Texture2D CreateDotTexture(int size = 32)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "VBS_Dot", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            float h = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - h) * (x - h) + (y - h) * (y - h)) / h;
                float a = Mathf.Clamp01(1f - d);
                a = a * a * (3f - 2f * a);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Soft value-noise texture (grey) that the window's local fallback scrolls and tints per scene.</summary>
        public static Texture2D CreateNoiseTexture(int size = 64, int seed = 7)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "VBS_Noise", wrapMode = TextureWrapMode.Repeat };
            var rnd = new System.Random(seed);
            const int cells = 8;
            var grid = new float[cells, cells];
            for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
                grid[x, y] = (float)rnd.NextDouble();
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fx = (float)x / size * cells, fy = (float)y / size * cells;
                int x0 = (int)fx, y0 = (int)fy;
                float tx = fx - x0, ty = fy - y0;
                tx = tx * tx * (3f - 2f * tx);
                ty = ty * ty * (3f - 2f * ty);
                float a = Mathf.Lerp(grid[x0 % cells, y0 % cells], grid[(x0 + 1) % cells, y0 % cells], tx);
                float b = Mathf.Lerp(grid[x0 % cells, (y0 + 1) % cells], grid[(x0 + 1) % cells, (y0 + 1) % cells], tx);
                byte g = (byte)(Mathf.Lerp(0.55f, 1f, Mathf.Lerp(a, b, ty)) * 255f);
                px[y * size + x] = new Color32(g, g, g, 255);
            }
            tex.SetPixels32(px);
            tex.Apply(true, false);
            return tex;
        }
    }
}
