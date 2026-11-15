using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Theme
{
    /// <summary>
    /// The themed card: a rounded cardboard rectangle with a honey border and a stitched seam, an optional speech tail,
    /// and a darker copy 2 mm behind it that reads as thickness. One opaque vertex-coloured mesh (no texture, no alpha),
    /// built in metres in the XY plane, front toward -Z like the builder's text. The rings never overlap, so nothing
    /// z-fights. SetSize rebuilds it (a caption card follows its text).
    /// </summary>
    public sealed class UiCard : MonoBehaviour
    {
        public enum Look { Paper, Dark, Slip, Approved, Grievance }

        Mesh _mesh;
        MeshRenderer _renderer;
        Vector2 _size = new Vector2(-1f, -1f);
        Look _look;
        float _tail, _tailX;

        static readonly List<Vector3> V = new List<Vector3>(4096);
        static readonly List<Color32> C = new List<Color32>(4096);
        static readonly List<int> T = new List<int>(8192);
        static readonly List<Vector2> P = new List<Vector2>(512), Nrm = new List<Vector2>(512);

        public Vector2 Size => _size;
        public Renderer Renderer => _renderer;

        public static UiCard Create(Transform parent, string name, Vector2 size, Look look = Look.Paper, float tail = 0f)
        {
            var go = new GameObject(name);
            go.layer = parent != null ? parent.gameObject.layer : 0;
            go.transform.SetParent(parent, false);
            var card = go.AddComponent<UiCard>();
            card._look = look;
            card._tail = tail;
            card.SetSize(size);
            return card;
        }

        void Ensure()
        {
            if (_mesh != null) return;
            _mesh = new Mesh { name = "VBS_UiCard" };
            _mesh.MarkDynamic();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = UiTheme.CardMaterial;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }

        public void SetLook(Look look)
        {
            if (look == _look) return;
            _look = look;
            Build();
        }

        /// <summary>The speech tail's tip, measured from the card's centre along X (it hangs under the bottom edge).</summary>
        public void SetTail(float length, float x)
        {
            if (Mathf.Approximately(length, _tail) && Mathf.Abs(x - _tailX) < 0.002f) return;
            _tail = length;
            _tailX = x;
            Build();
        }

        public void SetSize(Vector2 size)
        {
            size = new Vector2(Mathf.Max(0.012f, size.x), Mathf.Max(0.012f, size.y));
            if (Mathf.Abs(size.x - _size.x) < 0.0005f && Mathf.Abs(size.y - _size.y) < 0.0005f) return;
            _size = size;
            Build();
        }

        void Build()
        {
            Ensure();
            float w = _size.x, h = _size.y;
            float k = Mathf.Clamp(Mathf.Min(w, h) / 0.05f, 0.3f, 1f); // small tags get a finer border
            float border = 0.0032f * k, gap = 0.0024f * k, stitch = 0.0011f * k, pitch = 0.0046f * k;
            float inner = border + gap + stitch;
            float radius = Mathf.Min(0.011f * k, Mathf.Min(w, h) * 0.5f - 0.0005f);
            radius = Mathf.Max(radius, Mathf.Min(inner + 0.0008f, Mathf.Min(w, h) * 0.5f - 0.0005f));
            if (inner > radius - 0.0003f) // a sliver of a card: plain border only
            {
                float s = Mathf.Max(0.05f, (radius - 0.0003f) / inner);
                border *= s; gap *= s; stitch *= s; inner *= s;
            }

            Color32 cBorder, cPaper, cStitch, cBack;
            switch (_look)
            {
                case Look.Dark: cBorder = UiTheme.Vertex(UiTheme.Honey); cPaper = UiTheme.Vertex(UiTheme.Ink); cStitch = UiTheme.Vertex(UiTheme.Amber); cBack = UiTheme.Vertex(UiTheme.Dark); break;
                case Look.Slip: cBorder = UiTheme.Vertex(UiTheme.Amber); cPaper = UiTheme.Vertex(UiTheme.Cream); cStitch = UiTheme.Vertex(UiTheme.Honey); cBack = UiTheme.Vertex(UiTheme.Brown); break;
                case Look.Approved: cBorder = UiTheme.Vertex(UiTheme.Approved); cPaper = UiTheme.Vertex(UiTheme.Cream); cStitch = UiTheme.Vertex(UiTheme.Approved); cBack = UiTheme.Vertex(UiTheme.DarkBrown); break;
                case Look.Grievance: cBorder = UiTheme.Vertex(UiTheme.Grievance); cPaper = UiTheme.Vertex(UiTheme.Cream); cStitch = UiTheme.Vertex(UiTheme.Grievance); cBack = UiTheme.Vertex(UiTheme.DarkBrown); break;
                default: cBorder = UiTheme.Vertex(UiTheme.Honey); cPaper = UiTheme.Vertex(UiTheme.Cream); cStitch = UiTheme.Vertex(UiTheme.Brown); cBack = UiTheme.Vertex(UiTheme.DarkBrown); break;
            }

            Outline(w, h, radius, pitch);
            V.Clear(); C.Clear(); T.Clear();
            int n = P.Count;

            // the "thickness": the same outline, 2 mm behind and a little down-right
            Vector3 backOffset = new Vector3(0.0016f * k, -0.0022f * k, 0.002f);
            int centre = V.Count;
            V.Add(backOffset); C.Add(cBack);
            for (int i = 0; i < n; i++) { V.Add((Vector3)P[i] + backOffset); C.Add(cBack); }
            for (int i = 0; i < n; i++) { T.Add(centre); T.Add(centre + 1 + i); T.Add(centre + 1 + (i + 1) % n); }

            // border, paper gap, stitched seam, then the paper
            Ring(0f, border, cBorder, cBorder);
            Ring(border, border + gap, cPaper, cPaper);
            Ring(border + gap, inner, cStitch, cPaper);
            centre = V.Count;
            V.Add(Vector3.zero); C.Add(cPaper);
            for (int i = 0; i < n; i++) { V.Add(P[i] + Nrm[i] * inner); C.Add(cPaper); }
            for (int i = 0; i < n; i++) { T.Add(centre); T.Add(centre + 1 + i); T.Add(centre + 1 + (i + 1) % n); }

            if (_tail > 0.001f)
            {
                float half = Mathf.Min(_tail * 0.55f, w * 0.5f - radius - 0.001f);
                if (half > 0.002f)
                {
                    float bx = Mathf.Clamp(_tailX * 0.5f, -(w * 0.5f - radius - half), w * 0.5f - radius - half);
                    int v0 = V.Count;
                    V.Add(new Vector3(bx - half, -h * 0.5f, 0f)); V.Add(new Vector3(bx + half, -h * 0.5f, 0f)); V.Add(new Vector3(_tailX, -h * 0.5f - _tail, 0f));
                    C.Add(cBorder); C.Add(cBorder); C.Add(cBorder);
                    T.Add(v0); T.Add(v0 + 1); T.Add(v0 + 2);
                }
            }

            _mesh.Clear();
            _mesh.SetVertices(V);
            _mesh.SetColors(C);
            _mesh.SetTriangles(T, 0, true);
        }

        /// <summary>Quads between the outline inset by d0 and by d1; every other segment takes colour b (the stitches).</summary>
        static void Ring(float d0, float d1, Color32 a, Color32 b)
        {
            int n = P.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                var c = (i & 1) == 0 ? a : b;
                int v0 = V.Count;
                V.Add(P[i] + Nrm[i] * d0); V.Add(P[j] + Nrm[j] * d0); V.Add(P[j] + Nrm[j] * d1); V.Add(P[i] + Nrm[i] * d1);
                C.Add(c); C.Add(c); C.Add(c); C.Add(c);
                T.Add(v0); T.Add(v0 + 1); T.Add(v0 + 2); T.Add(v0); T.Add(v0 + 2); T.Add(v0 + 3);
            }
        }

        /// <summary>The rounded rectangle's outline, counter-clockwise from the bottom edge, with each point's inward normal.</summary>
        static void Outline(float w, float h, float r, float pitch)
        {
            P.Clear(); Nrm.Clear();
            float hx = w * 0.5f, hy = h * 0.5f;
            const int ArcSegments = 5;
            // edge i runs from corner i's arc end to corner i+1's arc start; corners: BR, TR, TL, BL (arc centres)
            for (int e = 0; e < 4; e++)
            {
                Vector2 dir, inward, from;
                float len;
                switch (e)
                {
                    case 0: dir = Vector2.right; inward = Vector2.up; from = new Vector2(-hx + r, -hy); len = w - 2f * r; break;
                    case 1: dir = Vector2.up; inward = Vector2.left; from = new Vector2(hx, -hy + r); len = h - 2f * r; break;
                    case 2: dir = Vector2.left; inward = Vector2.down; from = new Vector2(hx - r, hy); len = w - 2f * r; break;
                    default: dir = Vector2.down; inward = Vector2.right; from = new Vector2(-hx, hy - r); len = h - 2f * r; break;
                }
                int steps = Mathf.Max(1, Mathf.RoundToInt(len / pitch));
                if ((steps & 1) == 1) steps++; // even, so the dash pattern closes
                for (int s = 0; s < steps; s++) { P.Add(from + dir * (len * s / steps)); Nrm.Add(inward); }
                // the corner after this edge
                Vector2 centre = from + dir * len + inward * r;
                float a0 = Mathf.Atan2(-inward.y, -inward.x);
                for (int s = 0; s < ArcSegments + 1; s++)
                {
                    float a = a0 + (Mathf.PI * 0.5f) * s / (ArcSegments + 1);
                    var radial = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    P.Add(centre + radial * r);
                    Nrm.Add(-radial);
                }
            }
        }
    }
}
