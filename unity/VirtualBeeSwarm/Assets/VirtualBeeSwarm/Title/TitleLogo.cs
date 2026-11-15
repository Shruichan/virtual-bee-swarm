using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Title
{
    /// <summary>
    /// The game logo as a small 3D object: the honeycomb plate, the wordmark and Daniel's sticker bees on separate
    /// layers a few cm apart (so they shift against each other when the head moves), four sticker bees flying loops
    /// around it with dotted trails, a gentle bob. Local +Z points away from the player; the canvas is 2:1.
    /// Alpha-blended quads: the layers are ordered by sortingOrder (all below 0, so a caption or a stamp in front of the
    /// logo is always drawn over it), front to back from whichever side the camera is on.
    /// </summary>
    public sealed class TitleLogo
    {
        const float PlateZ = 0.03f, WordZ = -0.025f;
        const int Dots = 7;

        struct Loop
        {
            public Vector2 centre, radius;   // canvas widths
            public float tilt, period, phase, depth, size, bob;
            public int dir;
        }

        // loops in canvas widths (the canvas is 1 x 0.5); depth in metres toward the player on the lower arc
        static readonly Loop[] Loops =
        {
            new Loop { centre = new Vector2(0.02f, 0.0f), radius = new Vector2(0.5f, 0.235f), tilt = -8f, period = 9f, phase = 0.6f, depth = 0.11f, size = 0.115f, dir = -1 },
            new Loop { centre = new Vector2(0f, -0.02f), radius = new Vector2(0.44f, 0.2f), tilt = 14f, period = 11.5f, phase = 3.6f, depth = 0.09f, size = 0.105f, dir = 1 },
            new Loop { centre = new Vector2(0.3f, 0.17f), radius = new Vector2(0.13f, 0.07f), tilt = 0f, period = 6.5f, phase = 1.2f, depth = 0.07f, size = 0.085f, dir = 1 },
            new Loop { centre = new Vector2(-0.37f, -0.2f), radius = new Vector2(0.1f, 0.035f), tilt = 0f, period = 15f, phase = 2.2f, depth = 0.06f, size = 0.09f, dir = -1, bob = 0.012f },
        };

        public Transform Root { get; }
        public float Width { get; }
        /// <summary>0 = gone, 1 = fully shown.</summary>
        public float Shown { get; private set; }

        readonly Transform _plate, _word;
        readonly Renderer _plateR, _wordR;
        readonly Transform[] _bees;
        readonly Renderer[] _beeR;
        readonly int[] _facing;
        readonly Transform[] _dots;
        readonly Renderer[] _dotR;
        readonly Material[] _materials;
        readonly Texture2D _dotTex;
        float _scatter;   // 0..1: the sticker bees fly off
        static Mesh _quad;

        public TitleLogo(Transform parent, TitleArt art, float width)
        {
            Width = width;
            Root = new GameObject("TitleLogo").transform;
            Root.SetParent(parent, false);
            var mats = new System.Collections.Generic.List<Material>();
            bool layered = art.plate != null && art.wordmark != null;
            _plate = Layer("Plate", layered ? art.plate : art.flat, new Vector3(width, width * 0.5f, 1f), mats, out _plateR);
            if (layered) _word = Layer("Wordmark", art.wordmark, new Vector3(width, width * 0.5f, 1f), mats, out _wordR);

            int n = Mathf.Min(Loops.Length, art.stickerBees != null ? art.stickerBees.Length : 0);
            _bees = new Transform[n];
            _beeR = new Renderer[n];
            _facing = new int[n];
            for (int i = 0; i < n; i++)
            {
                _bees[i] = Layer("StickerBee" + (i + 1), art.stickerBees[i], Vector3.one * (Loops[i].size * width), mats, out _beeR[i]);
                _facing[i] = art.stickerFacing != null && i < art.stickerFacing.Length && art.stickerFacing[i] < 0 ? -1 : 1;
            }

            var dotMat = GameMaterials.Create("VBS_TitleTrail", Color.white, GameMaterials.Blend.Alpha, doubleSided: true);
            dotMat.mainTexture = _dotTex = DotTexture(64);
            mats.Add(dotMat);
            _dots = new Transform[n * Dots];
            _dotR = new Renderer[n * Dots];
            for (int i = 0; i < _dots.Length; i++)
            {
                var go = new GameObject("Trail");
                go.transform.SetParent(Root, false);
                go.AddComponent<MeshFilter>().sharedMesh = Quad();
                var r = go.AddComponent<MeshRenderer>();
                Plain(r, dotMat);
                _dots[i] = go.transform;
                _dotR[i] = r;
            }
            _materials = mats.ToArray();
            Root.gameObject.SetActive(false);
        }

        Transform Layer(string name, Texture2D tex, Vector3 size, System.Collections.Generic.List<Material> mats, out Renderer renderer)
        {
            var go = new GameObject(name);
            go.transform.SetParent(Root, false);
            go.transform.localScale = size;
            go.AddComponent<MeshFilter>().sharedMesh = Quad();
            var mat = GameMaterials.Create("VBS_Title" + name, Color.white, GameMaterials.Blend.Alpha, doubleSided: true);
            mat.mainTexture = tex;
            mats.Add(mat);
            var r = go.AddComponent<MeshRenderer>();
            Plain(r, mat);
            renderer = r;
            return go.transform;
        }

        static void Plain(MeshRenderer r, Material mat)
        {
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        /// <summary>1 x 1 in XY, seen from -Z with +X to the right.</summary>
        static Mesh Quad()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "VBS_TitleQuad" };
            _quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
            _quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            _quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _quad.RecalculateBounds();
            return _quad;
        }

        /// <summary>The logo's trail dot: honey with the ink outline.</summary>
        static Texture2D DotTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "VBS_TitleDot", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            Color honey = new Color(0.97f, 0.73f, 0.2f), ink = new Color(0.165f, 0.09f, 0.04f);
            float h = (size - 1) * 0.5f, aa = 2f / size;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - h) * (x - h) + (y - h) * (y - h)) / h;
                Color c = Color.Lerp(honey, ink, Mathf.Clamp01((d - 0.66f) / aa));
                c.a = Mathf.Clamp01((0.96f - d) / aa);
                px[y * size + x] = c;
            }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        public void Show(bool on)
        {
            if (Root.gameObject.activeSelf != on) Root.gameObject.SetActive(on);
            if (!on) { Shown = 0f; _scatter = 0f; }
        }

        public void SetScatter(float k) => _scatter = Mathf.Clamp01(k);

        /// <summary>
        /// Pose and animate. shown 0..1 pops the logo in (the layers start flat and spread apart); scale is the fly-off
        /// shrink; time drives the loops.
        /// </summary>
        public void Tick(Vector3 position, Quaternion rotation, float shown, float scale, float time, Vector3 cameraPosition)
        {
            Shown = shown;
            // pop with a small overshoot
            float s = shown <= 0f ? 0f : shown >= 1f ? 1f : OutBack(shown);
            s = Mathf.Max(0f, s) * scale;
            Root.SetPositionAndRotation(position, rotation);
            Root.localScale = Vector3.one * s;
            float spread = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 1f, shown));
            // which side is the camera on? (local -Z = the front)
            bool front = Vector3.Dot(cameraPosition - position, rotation * Vector3.forward) <= 0f;

            _plate.localPosition = new Vector3(0f, 0f, PlateZ * spread);
            _plateR.sortingOrder = Order(PlateZ * spread, front);
            if (_word != null)
            {
                _word.localPosition = new Vector3(0f, 0f, WordZ * spread);
                _wordR.sortingOrder = Order(WordZ * spread, front) + (front ? 1 : -1);
            }

            float w = Width;
            for (int i = 0; i < _bees.Length; i++)
            {
                var l = Loops[i];
                float a = l.phase + l.dir * time * (Mathf.PI * 2f / l.period);
                float reach = 1f + _scatter * 2.4f;
                Vector3 p = LoopPoint(l, a, w, time, spread) * reach;
                p.z -= _scatter * 0.15f;
                // heading: the tangent, in the logo plane
                Vector3 ahead = LoopPoint(l, a + l.dir * 0.05f, w, time, spread) * reach;
                float vx = ahead.x - p.x, vy = ahead.y - p.y;
                float bank = Mathf.Clamp(Mathf.Atan2(vy, Mathf.Abs(vx) + 1e-5f) * Mathf.Rad2Deg * 0.45f, -22f, 22f);
                float look = vx >= 0f ? 1f : -1f;
                float size = l.size * w * (1f - _scatter);
                _bees[i].localPosition = p;
                _bees[i].localRotation = Quaternion.Euler(0f, 0f, bank * look);
                _bees[i].localScale = new Vector3(size * look * _facing[i], size, 1f);
                _beeR[i].sortingOrder = Order(p.z, front);

                for (int d = 0; d < Dots; d++)
                {
                    int k = i * Dots + d;
                    float back = 0.2f + d * 0.11f; // radians behind the bee
                    Vector3 q = LoopPoint(l, a - l.dir * back * (l.radius.x > 0.2f ? 1f : 2.2f), w, time, spread) * reach;
                    q.z += 0.004f; // just behind its bee's plane
                    float ds = w * 0.02f * (1f - d / (float)(Dots + 1)) * (1f - _scatter) * spread;
                    _dots[k].localPosition = q;
                    _dots[k].localScale = new Vector3(ds, ds, 1f);
                    _dotR[k].sortingOrder = Order(q.z, front) - (front ? 1 : -1);
                }
            }
        }

        static float OutBack(float x)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            return 1f + c3 * Mathf.Pow(x - 1f, 3f) + c1 * Mathf.Pow(x - 1f, 2f);
        }

        static Vector3 LoopPoint(Loop l, float a, float w, float time, float spread)
        {
            float x = Mathf.Cos(a) * l.radius.x, y = Mathf.Sin(a) * l.radius.y;
            float tr = l.tilt * Mathf.Deg2Rad, ct = Mathf.Cos(tr), st = Mathf.Sin(tr);
            float px = (l.centre.x + x * ct - y * st) * w;
            float py = (l.centre.y + x * st + y * ct) * w + Mathf.Sin(time * 1.7f + l.phase) * l.bob;
            // in front of the logo on the lower arc, behind it on the upper one
            return new Vector3(px, py, Mathf.Sin(a) * l.depth * spread);
        }

        // nearer the camera = drawn later; everything stays below 0
        static int Order(float z, bool front) => -30 + Mathf.RoundToInt((front ? -z : z) * 100f);

        public void Destroy()
        {
            foreach (var m in _materials) if (m != null) Object.Destroy(m);
            if (_dotTex != null) Object.Destroy(_dotTex);
            if (Root != null) Object.Destroy(Root.gameObject);
        }
    }
}
