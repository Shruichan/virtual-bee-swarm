using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The window as a free-floating magic portal (FutureWindow builds it in Awake, under portalRoot so it opens, grows and
    /// swells with the window): a glowing honeycomb rim that follows the pane's outline (arch or rectangle), a soft halo
    /// behind it, a soft inner vignette on the picture, a honey-coloured back so it reads from behind, a slow hover,
    /// a swirl + ripple on every scene change and a flare (halo burst + hex sparks) at the escape.
    /// Everything is generated at runtime: 5 draws (rim, halo, vignette, swirl, back) + one small particle system.
    /// </summary>
    public sealed class PortalFx : MonoBehaviour
    {
        Transform _root, _pane, _swirlT;
        Renderer _rim, _halo, _vignette, _swirl, _back;
        ParticleSystem _sparks;
        Vector3 _rootPos;
        float _bob = 0.01f;               // portal-local units for ~1.5 cm
        float _swirlStart = -100f, _flareStart = -100f, _flareSeconds = 2.5f;
        Color _swirlColor = Color.white;
        float _swirlRadius = 0.2f;
        public string Summary { get; private set; } = "";
        /// <summary>The whole portal (rim to rim) in portalRoot-local units, and its centre there: ArRig sizes and
        /// places the window by this, not by the picture alone.</summary>
        public Vector2 OuterSize { get; private set; }
        public Vector3 OuterCenter { get; private set; }
        /// <summary>The soft halo behind the portal (off by default).</summary>
        public static bool BuildHalo = false;

        static Texture2D _haloTex, _swirlTex, _dotTex;
        static Material _rimMat, _haloMat, _vigMat, _swirlMat, _backMat, _sparkMat;

        /// <summary>Builds the portal dressing. root = portalRoot (at its base scale), pane = the fallback pane (its mesh
        /// gives the outline), towardPlayer = world direction the picture faces.</summary>
        public static PortalFx Build(Transform root, Renderer pane, Vector3 towardPlayer)
        {
            if (root == null || pane == null) return null;
            var mf = pane.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return null;
            var fx = root.gameObject.AddComponent<PortalFx>();
            fx.Init(root, pane.transform, mf.sharedMesh, towardPlayer);
            return fx;
        }

        void Init(Transform root, Transform pane, Mesh paneMesh, Vector3 towardPlayer)
        {
            _root = root;
            _pane = pane;
            _rootPos = root.localPosition;
            EnsureMaterials();

            // outline of the pane in portalRoot-local space (metric proportions: the pane's own scale applied)
            var loop = Outline(paneMesh, out bool fromMesh);
            var pts = new List<Vector3>(loop.Count);
            foreach (var p in loop) pts.Add(root.InverseTransformPoint(pane.TransformPoint(p)));
            Vector3 fwdL = root.InverseTransformDirection(-towardPlayer).normalized; // into the portal (away from the player)
            if (fwdL.sqrMagnitude < 0.5f) fwdL = Vector3.forward;
            Vector3 upL = Vector3.ProjectOnPlane(root.InverseTransformDirection(Vector3.up), fwdL).normalized;
            if (upL.sqrMagnitude < 0.5f) upL = Vector3.up;
            Vector3 rightL = Vector3.Cross(upL, fwdL).normalized;

            // 2D coordinates on the pane plane
            var p2 = new List<Vector2>(pts.Count);
            Vector3 c3 = Vector3.zero;
            foreach (var p in pts) c3 += p;
            c3 /= Mathf.Max(1, pts.Count);
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in pts)
            {
                var q = new Vector2(Vector3.Dot(p - c3, rightL), Vector3.Dot(p - c3, upL));
                p2.Add(q);
                minX = Mathf.Min(minX, q.x); maxX = Mathf.Max(maxX, q.x); minY = Mathf.Min(minY, q.y); maxY = Mathf.Max(maxY, q.y);
            }
            float w = maxX - minX, h = maxY - minY;
            float frameMargin = FrameMargin(root, pane, rightL, c3, w);
            float r = 0.034f * w; // hex cell radius: ~6.5 cm on a 1.9 m portal
            Vector3 front = -fwdL * (0.02f * w);

            // ---- honeycomb rim: two staggered rows of hexes along the outline, just outside the frame
            var verts = new List<Vector3>();
            var cols = new List<Color>();
            var tris = new List<int>();
            var rnd = new System.Random(7);
            float area = 0f;
            for (int i = 0; i < p2.Count; i++) { var a = p2[i]; var b = p2[(i + 1) % p2.Count]; area += a.x * b.y - b.x * a.y; }
            float ccw = area >= 0f ? 1f : -1f;
            float perim = 0f;
            for (int i = 0; i < p2.Count; i++) perim += (p2[(i + 1) % p2.Count] - p2[i]).magnitude;
            int cells = 0;
            for (int row = 0; row < 2; row++)
            {
                float spacing = r * (row == 0 ? 1.82f : 2.1f);
                float off = frameMargin + r * (row == 0 ? 0.75f : 2.3f);
                float rr = r * (row == 0 ? 1f : 0.78f);
                int n = Mathf.Max(8, Mathf.RoundToInt(perim / spacing));
                for (int k = 0; k < n; k++)
                {
                    if (row == 1 && rnd.NextDouble() < 0.35) continue;
                    float s = (k + (row == 1 ? 0.5f : 0f)) / n * perim;
                    Sample(p2, s, out var at, out var tan);
                    var nrm = new Vector2(tan.y, -tan.x) * ccw; // outward
                    var q = at + nrm * off;
                    float jitter = row == 1 ? ((float)rnd.NextDouble() - 0.5f) * r * 0.6f : 0f;
                    q += nrm * jitter;
                    Vector3 cL = c3 + rightL * q.x + upL * q.y + front;
                    float shade = 0.85f + 0.3f * (float)rnd.NextDouble();
                    var face = new Color(1f * shade, 0.64f * shade, 0.12f * shade, 1f);
                    var side = new Color(0.62f, 0.36f, 0.08f, 1f);
                    Hex(verts, cols, tris, cL, rightL, upL, fwdL, rr * (0.92f + 0.1f * (float)rnd.NextDouble()), rr * 0.45f, face, side,
                        (float)rnd.NextDouble() * 0.2f);
                    cells++;
                }
            }
            var rimMesh = new Mesh { name = "PortalHoneycombRim" };
            rimMesh.SetVertices(verts);
            rimMesh.SetColors(cols);
            rimMesh.SetTriangles(tris, 0);
            rimMesh.RecalculateBounds();
            _rim = MakeRenderer("HoneycombRim", root, rimMesh, _rimMat);

            // ---- halo behind: a soft gold glow the size of the portal + rim
            // (off: additive over a bright wall it read as a smudge, not a glow; the rim and the flare carry it)
            if (BuildHalo) {
            var haloMesh = Quad("PortalHalo");
            _halo = MakeRenderer("Halo", root, haloMesh, _haloMat);
            var ht = _halo.transform;
            ht.localPosition = c3 + fwdL * (0.03f * w);
            ht.localRotation = Quaternion.LookRotation(fwdL, upL);
            float hs = 1.55f;
            ht.localScale = new Vector3((w + 2f * (frameMargin + 3f * r)) * hs, (h + 2f * (frameMargin + 3f * r)) * hs, 1f);
            _haloScale = ht.localScale; // set here: FutureWindow may Tick before this component's own Start
            }

            // ---- back: the pane mesh again, flipped, honey-dark, so the portal is a solid object from behind
            var backMesh = Object.Instantiate(paneMesh);
            backMesh.name = "PortalBack";
            var bt = backMesh.triangles;
            for (int i = 0; i < bt.Length; i += 3) (bt[i + 1], bt[i + 2]) = (bt[i + 2], bt[i + 1]);
            backMesh.triangles = bt;
            var backGo = new GameObject("PortalBack");
            backGo.transform.SetParent(pane, false);
            backGo.transform.localPosition = pane.InverseTransformDirection(root.TransformDirection(fwdL)).normalized * 0.01f;
            backGo.AddComponent<MeshFilter>().sharedMesh = backMesh;
            _back = backGo.AddComponent<MeshRenderer>();
            Quiet(_back);
            _back.sharedMaterial = _backMat;

            // ---- inner vignette: the pane mesh in front of the picture, alpha from a texture of the distance to the edge
            var vigGo = new GameObject("Vignette");
            vigGo.transform.SetParent(pane, false);
            vigGo.transform.localPosition = pane.InverseTransformDirection(root.TransformDirection(-fwdL)).normalized * 0.004f;
            vigGo.AddComponent<MeshFilter>().sharedMesh = paneMesh;
            _vignette = vigGo.AddComponent<MeshRenderer>();
            Quiet(_vignette);
            var vigMat = new Material(_vigMat) { name = "VBS_PortalVignette (runtime)" };
            vigMat.mainTexture = VignetteTexture(paneMesh, pane.localScale);
            _vignette.sharedMaterial = vigMat;

            // ---- swirl: a spiral disc that spins up and ripples out on scene changes
            _swirlRadius = 0.47f * Mathf.Min(w, h);
            var swirlMesh = Quad("PortalSwirl");
            _swirl = MakeRenderer("Swirl", root, swirlMesh, _swirlMat);
            _swirlT = _swirl.transform;
            _swirlT.localPosition = c3 - fwdL * (0.008f * w);
            _swirlT.localRotation = Quaternion.LookRotation(fwdL, upL);
            _swirl.enabled = false;
            _swirlBase = _swirlT.localRotation;

            // ---- sparks: hex-gold sparks burst off the rim at the escape
            var sg = new GameObject("RimSparks");
            sg.transform.SetParent(root, false);
            sg.transform.localPosition = c3 - fwdL * (0.03f * w);
            sg.transform.localRotation = Quaternion.LookRotation(-fwdL, upL); // +Z toward the player
            _sparks = sg.AddComponent<ParticleSystem>();
            _sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = _sparks.main;
            main.loop = false;
            main.playOnAwake = false;
            main.duration = 1f;
            main.maxParticles = QuestPerf.Particles(90);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.15f * w, 0.45f * w);
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f * w, 0.03f * w);
            main.gravityModifier = 0.02f;
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.35f), new Color(1f, 0.6f, 0.15f));
            var em = _sparks.emission;
            em.rateOverTime = 0f;
            var sh = _sparks.shape;
            sh.shapeType = ParticleSystemShapeType.Circle;
            sh.radius = 0.5f * Mathf.Max(w, h) + frameMargin;
            sh.radiusThickness = 0.15f;
            sh.arc = 360f;
            var col = _sparks.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var pr = sg.GetComponent<ParticleSystemRenderer>();
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.sharedMaterial = _sparkMat;
            Quiet(pr);

            // hover: portal-local, so it scales with the window (~1.5 cm on a 1.9 m portal)
            _bob = 0.008f * w;

            var rb = rimMesh.bounds;
            OuterSize = new Vector2(Mathf.Abs(Vector3.Dot(rb.size, rightL)), Mathf.Abs(Vector3.Dot(rb.size, upL)));
            OuterCenter = rb.center;
            Summary = "honeycomb rim " + cells + " cells (" + (fromMesh ? "pane outline, " + loop.Count + " pts" : "chamfered pane bounds (mesh not readable)") + ", " + w.ToString("0.00") + " x " + h.ToString("0.00") +
                      "), frame margin " + (frameMargin / Mathf.Max(1e-4f, w)).ToString("0.00") + " w, vignette, swirl, back, sparks";
        }

        // ------------------------------------------------------------------ per frame (FutureWindow drives it)
        /// <summary>open 0..1, glow = the window's glow colour this frame (alpha = strength).</summary>
        public void Tick(float open, Color glow)
        {
            float t = Time.time;
            if (_root != null)
                _root.localPosition = _rootPos + Vector3.up * (_bob * Mathf.Sin(t * 1.1f));

            float flare = 1f - Mathf.Clamp01((t - _flareStart) / _flareSeconds);
            flare = flare * flare;
            float a = Mathf.Clamp01(glow.a);
            if (_halo != null)
            {
                var hc = new Color(glow.r, glow.g, glow.b, Mathf.Clamp01((0.55f + 0.45f * a) * open + 0.6f * flare));
                GameMaterials.Tint(_halo, hc);
                float s = 1f + 0.25f * flare;
                _halo.transform.localScale = new Vector3(Mathf.Abs(_haloScale.x) * s, Mathf.Abs(_haloScale.y) * s, 1f);
            }
            if (_rim != null)
            {
                float k = 0.9f + 0.25f * a + 0.6f * flare;
                GameMaterials.Tint(_rim, new Color(Mathf.Lerp(1f, glow.r, 0.25f) * k, Mathf.Lerp(1f, glow.g, 0.25f) * k, Mathf.Lerp(1f, glow.b, 0.15f) * k, 1f));
            }
            if (_vignette != null)
                GameMaterials.Tint(_vignette, new Color(0.22f + 0.5f * flare, 0.11f + 0.35f * flare, 0.02f, 0.75f));

            if (_swirl != null)
            {
                float st = (t - _swirlStart) / 1.7f;
                bool on = st >= 0f && st < 1f;
                if (_swirl.enabled != on) _swirl.enabled = on;
                if (on)
                {
                    float e = 1f - (1f - st) * (1f - st);
                    float sc = _swirlRadius * 2f * Mathf.Lerp(0.25f, 1.05f, e);
                    _swirlT.localScale = new Vector3(sc, sc, 1f);
                    _swirlT.localRotation = _swirlBase * Quaternion.Euler(0f, 0f, -540f * e);
                    float fade = Mathf.SmoothStep(0f, 1f, st / 0.15f) * (1f - Mathf.SmoothStep(0.55f, 1f, st));
                    var c = _swirlColor;
                    c.a = 0.85f * fade * open;
                    GameMaterials.Tint(_swirl, c);
                }
            }
        }

        Vector3 _haloScale;
        Quaternion _swirlBase;


        /// <summary>A scene change: the picture swirls and ripples out (tinted toward the new scene).</summary>
        public void Swirl(Color tint)
        {
            _swirlStart = Time.time;
            _swirlColor = Color.Lerp(new Color(1f, 0.85f, 0.45f), tint, 0.4f);
        }

        /// <summary>A big moment (escape): halo burst, rim flares, hex sparks fly off the rim.</summary>
        public void Flare(float seconds = 2.5f, int sparks = 70)
        {
            _flareStart = Time.time;
            _flareSeconds = Mathf.Max(0.3f, seconds);
            if (_sparks != null && _sparks.gameObject.activeInHierarchy) _sparks.Emit(QuestPerf.Particles(sparks));
        }

        // ------------------------------------------------------------------ geometry
        static string V(Vector3 v) => "(" + v.x.ToString("0.000") + "," + v.y.ToString("0.000") + "," + v.z.ToString("0.000") + ")";

        static void Sample(List<Vector2> loop, float s, out Vector2 at, out Vector2 tan)
        {
            int n = loop.Count;
            for (int i = 0; i < n; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % n];
                float len = (b - a).magnitude;
                if (s <= len || i == n - 1)
                {
                    tan = len > 1e-6f ? (b - a) / len : Vector2.right;
                    at = Vector2.Lerp(a, b, len > 1e-6f ? Mathf.Clamp01(s / len) : 0f);
                    // smooth the tangent over corners: average with the neighbours' directions
                    var pa = loop[(i - 1 + n) % n];
                    var nb = loop[(i + 2) % n];
                    float u = len > 1e-6f ? Mathf.Clamp01(s / len) : 0f;
                    if (u < 0.5f && (a - pa).sqrMagnitude > 1e-10f) tan = Vector2.Lerp(((a - pa).normalized + tan).normalized, tan, u * 2f).normalized;
                    else if (u >= 0.5f && (nb - b).sqrMagnitude > 1e-10f) tan = Vector2.Lerp(tan, ((nb - b).normalized + tan).normalized, (u - 0.5f) * 2f).normalized;
                    return;
                }
                s -= len;
            }
            at = loop[0];
            tan = Vector2.right;
        }

        /// <summary>The pane mesh's outer boundary (mesh space), welded by position; the bounds rectangle if that fails.</summary>
        static List<Vector3> Outline(Mesh mesh, out bool fromMesh)
        {
            fromMesh = false;
            var v = mesh.vertices;
            var tri = mesh.triangles;
            var b = mesh.bounds;
            var rect = new List<Vector3>
            {
                new Vector3(b.min.x, b.min.y, b.center.z), new Vector3(b.max.x, b.min.y, b.center.z),
                new Vector3(b.max.x, b.max.y, b.center.z), new Vector3(b.min.x, b.max.y, b.center.z),
            };
            if (v.Length < 3 || tri.Length < 3)
            {
                // not readable in a player build (the art's pane mesh): Daniel's frame is a rectangle with its top
                // corners cut off, so follow that
                float c = 0.2f * Mathf.Min(b.size.x, b.size.y);
                return new List<Vector3>
                {
                    new Vector3(b.min.x, b.min.y, b.center.z), new Vector3(b.max.x, b.min.y, b.center.z),
                    new Vector3(b.max.x, b.max.y - c, b.center.z), new Vector3(b.max.x - c, b.max.y, b.center.z),
                    new Vector3(b.min.x + c, b.max.y, b.center.z), new Vector3(b.min.x, b.max.y - c, b.center.z),
                };
            }
            // the portal shapes are convex (arch, octagon, rectangle): the convex hull of the pane seen face on is its outline
            var hull = Hull(new List<Vector3>(v), b, rect, out fromMesh);
            if (fromMesh) return hull;
            float q = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) * 1e-4f + 1e-7f;
            var weld = new Dictionary<Vector3Int, int>();
            var map = new int[v.Length];
            var pos = new List<Vector3>();
            for (int i = 0; i < v.Length; i++)
            {
                var key = new Vector3Int(Mathf.RoundToInt(v[i].x / q), Mathf.RoundToInt(v[i].y / q), Mathf.RoundToInt(v[i].z / q));
                if (!weld.TryGetValue(key, out int id)) { id = pos.Count; weld[key] = id; pos.Add(v[i]); }
                map[i] = id;
            }
            var edges = new Dictionary<long, int>();
            for (int i = 0; i + 2 < tri.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = map[tri[i + e]], c = map[tri[i + (e + 1) % 3]];
                    if (a == c) continue;
                    long key = a < c ? ((long)a << 32) | (uint)c : ((long)c << 32) | (uint)a;
                    edges.TryGetValue(key, out int cnt);
                    edges[key] = cnt + 1;
                }
            var next = new Dictionary<int, List<int>>();
            foreach (var kv in edges)
            {
                if (kv.Value != 1) continue;
                int a = (int)(kv.Key >> 32), c = (int)(kv.Key & 0xffffffff);
                if (!next.TryGetValue(a, out var la)) next[a] = la = new List<int>();
                if (!next.TryGetValue(c, out var lc)) next[c] = lc = new List<int>();
                la.Add(c); lc.Add(a);
            }
            if (next.Count < 3) return Hull(pos, b, rect, out fromMesh);
            // walk the loops; keep the longest
            var seen = new HashSet<int>();
            List<int> best = null;
            foreach (var start in next.Keys)
            {
                if (seen.Contains(start)) continue;
                var loop = new List<int> { start };
                seen.Add(start);
                int prev = -1, cur = start;
                for (int guard = 0; guard < next.Count + 2; guard++)
                {
                    int nx = -1;
                    foreach (var cand in next[cur]) if (cand != prev && !seen.Contains(cand)) { nx = cand; break; }
                    if (nx < 0) break;
                    loop.Add(nx);
                    seen.Add(nx);
                    prev = cur;
                    cur = nx;
                }
                if (best == null || loop.Count > best.Count) best = loop;
            }
            if (best == null || best.Count < 4) return Hull(pos, b, rect, out fromMesh);
            var outl = new List<Vector3>(best.Count);
            foreach (int id in best) outl.Add(pos[id]);
            // drop near-duplicates so the spacing walk stays even
            var clean = new List<Vector3>();
            foreach (var p in outl) if (clean.Count == 0 || (p - clean[clean.Count - 1]).sqrMagnitude > q * q * 4f) clean.Add(p);
            if (clean.Count < 4) return rect;
            fromMesh = true;
            return clean;
        }

        /// <summary>A closed (two-sided or slab) pane has no open edge: its outline is the convex hull of its vertices
        /// seen face on (the portal shapes are convex: arch, octagon, rectangle).</summary>
        static List<Vector3> Hull(List<Vector3> pts, Bounds b, List<Vector3> rect, out bool ok)
        {
            ok = false;
            var p = new List<Vector2>(pts.Count);
            foreach (var q in pts) p.Add(new Vector2(q.x, q.y));
            p.Sort((a, c) => a.x != c.x ? a.x.CompareTo(c.x) : a.y.CompareTo(c.y));
            if (p.Count < 3) return rect;
            var h = new List<Vector2>(p.Count * 2);
            for (int pass = 0; pass < 2; pass++)
            {
                int start = h.Count;
                for (int i = 0; i < p.Count; i++)
                {
                    var c = pass == 0 ? p[i] : p[p.Count - 1 - i];
                    while (h.Count >= start + 2 && Cross(h[h.Count - 2], h[h.Count - 1], c) <= 0f) h.RemoveAt(h.Count - 1);
                    h.Add(c);
                }
                h.RemoveAt(h.Count - 1);
            }
            if (h.Count < 3) return rect;
            ok = true;
            var outl = new List<Vector3>(h.Count);
            foreach (var q in h) outl.Add(new Vector3(q.x, q.y, b.center.z));
            return outl;
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        /// <summary>How far the frame art reaches past the pane (portal-local units), measured from its meshes.</summary>
        static float FrameMargin(Transform root, Transform pane, Vector3 rightL, Vector3 c3, float paneW)
        {
            float half = paneW * 0.5f, reach = half;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.transform == pane || mf.name.Contains("Pane") || mf.name == "Glow") continue;
                var r = mf.GetComponent<Renderer>();
                if (r == null || !r.enabled) continue;
                var b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                    var p = root.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    reach = Mathf.Max(reach, Mathf.Abs(Vector3.Dot(p - c3, rightL)));
                }
            }
            return Mathf.Clamp(reach - half, 0f, paneW * 0.12f);
        }

        static void Hex(List<Vector3> v, List<Color> c, List<int> t, Vector3 center, Vector3 right, Vector3 up, Vector3 fwd,
            float r, float depth, Color face, Color side, float spin)
        {
            int baseI = v.Count;
            v.Add(center); c.Add(face * 1.08f);
            for (int i = 0; i < 6; i++)
            {
                float a = (i * 60f + 30f) * Mathf.Deg2Rad + spin;
                v.Add(center + (right * Mathf.Cos(a) + up * Mathf.Sin(a)) * r);
                c.Add(face);
            }
            // front face (toward the player = -fwd): wind so the normal points -fwd
            for (int i = 0; i < 6; i++)
            {
                int a = baseI + 1 + i, b = baseI + 1 + (i + 1) % 6;
                if (Vector3.Dot(Vector3.Cross(v[a] - v[baseI], v[b] - v[baseI]), -fwd) > 0f) { t.Add(baseI); t.Add(a); t.Add(b); }
                else { t.Add(baseI); t.Add(b); t.Add(a); }
            }
            // sides: a short prism back into the portal
            int ring = v.Count;
            for (int i = 0; i < 6; i++)
            {
                v.Add(v[baseI + 1 + i]); c.Add(side);
                v.Add(v[baseI + 1 + i] + fwd * depth); c.Add(side * 0.8f);
            }
            for (int i = 0; i < 6; i++)
            {
                int a0 = ring + i * 2, a1 = a0 + 1, b0 = ring + ((i + 1) % 6) * 2, b1 = b0 + 1;
                Vector3 mid = (v[a0] + v[b0]) * 0.5f - center;
                if (Vector3.Dot(Vector3.Cross(v[b0] - v[a0], v[a1] - v[a0]), mid) > 0f) { t.Add(a0); t.Add(b0); t.Add(a1); t.Add(b0); t.Add(b1); t.Add(a1); }
                else { t.Add(a0); t.Add(a1); t.Add(b0); t.Add(b0); t.Add(a1); t.Add(b1); }
            }
        }

        static Mesh Quad(string name)
        {
            var m = new Mesh { name = name };
            m.SetVertices(new List<Vector3> { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) });
            m.SetUVs(0, new List<Vector2> { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) });
            m.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0); // faces -Z
            m.RecalculateBounds();
            return m;
        }

        static Renderer MakeRenderer(string name, Transform parent, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            Quiet(r);
            r.sharedMaterial = mat;
            return r;
        }

        static void Quiet(Renderer r)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        // ------------------------------------------------------------------ materials + textures
        static void EnsureMaterials()
        {
            if (_rimMat != null) return;
            _rimMat = GameMaterials.Create("VBS_PortalRim (runtime)", Color.white, GameMaterials.Blend.Opaque, vertexColors: true, emission: 1.15f);
            _haloTex = HaloTexture(64);
            _haloMat = GameMaterials.Create("VBS_PortalHalo (runtime)", new Color(1f, 0.8f, 0.35f, 0.5f), GameMaterials.Blend.Additive, doubleSided: true);
            _haloMat.mainTexture = _haloTex;
            _vigMat = GameMaterials.Create("VBS_PortalVignette", new Color(0.22f, 0.11f, 0.02f, 0.75f), GameMaterials.Blend.Alpha);
            _vigMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 20; // after the picture (its live pane may be transparent)
            _swirlTex = SwirlTexture(128);
            _swirlMat = GameMaterials.Create("VBS_PortalSwirl (runtime)", new Color(1f, 0.85f, 0.45f, 0f), GameMaterials.Blend.Additive, doubleSided: true);
            _swirlMat.mainTexture = _swirlTex;
            _swirlMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 25;
            _backMat = GameMaterials.Create("VBS_PortalBack (runtime)", new Color(0.55f, 0.33f, 0.08f), GameMaterials.Blend.Opaque);
            _dotTex = HaloTexture(32);
            _sparkMat = GameMaterials.Create("VBS_PortalSpark (runtime)", Color.white, GameMaterials.Blend.Additive, vertexColors: true, emission: 1.6f, instancing: false);
            _sparkMat.mainTexture = _dotTex;
        }

        static Texture2D HaloTexture(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "VBS_PortalHalo", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    float d = Mathf.Pow(Mathf.Pow(Mathf.Abs(u), 3f) + Mathf.Pow(Mathf.Abs(v), 3f), 1f / 3f); // soft rounded square
                    float a = 1f - Mathf.SmoothStep(0.55f, 1f, d);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        static Texture2D SwirlTexture(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "VBS_PortalSwirl", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    if (r >= 1f) { px[y * n + x] = new Color32(255, 255, 255, 0); continue; }
                    float ang = Mathf.Atan2(v, u);
                    float arms = 0.5f + 0.5f * Mathf.Cos(3f * ang + 7f * r); // three spiral arms
                    arms = Mathf.Pow(arms, 3f);
                    float ring = Mathf.Exp(-Mathf.Pow((r - 0.9f) / 0.06f, 2f)); // the ripple's leading ring
                    float fall = Mathf.SmoothStep(0f, 0.25f, r) * (1f - Mathf.SmoothStep(0.85f, 1f, r));
                    float a = Mathf.Clamp01(arms * fall * 0.85f + ring * 0.9f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        /// <summary>Alpha = closeness to the pane's edge, in the pane mesh's UV space (UV = bounds 0..1, as ArtSlots and
        /// the primitive quad both map it). scale = the pane transform's local scale (aspect).</summary>
        static Texture2D VignetteTexture(Mesh paneMesh, Vector3 scale)
        {
            const int n = 96;
            var loop = Outline(paneMesh, out _);
            var b = paneMesh.bounds;
            float sx = Mathf.Abs(scale.x), sy = Mathf.Abs(scale.y);
            // the pane is a plane in mesh XY (ArtSlots flattens to XY; the primitive quad is XY)
            var pts = new List<Vector2>(loop.Count);
            foreach (var p in loop) pts.Add(new Vector2((p.x - b.min.x) * sx, (p.y - b.min.y) * sy));
            float W = Mathf.Max(1e-5f, b.size.x * sx), H = Mathf.Max(1e-5f, b.size.y * sy);
            float edge = 0.16f * Mathf.Min(W, H);
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "VBS_PortalVignette", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var q = new Vector2((x + 0.5f) / n * W, (y + 0.5f) / n * H);
                    float d = float.MaxValue;
                    for (int i = 0; i < pts.Count; i++) d = Mathf.Min(d, SegDist(q, pts[i], pts[(i + 1) % pts.Count]));
                    float a = 1f - Mathf.Clamp01(d / edge);
                    a = a * a;
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float l = ab.sqrMagnitude;
            float k = l > 1e-12f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / l) : 0f;
            return (p - (a + ab * k)).magnitude;
        }
    }
}
