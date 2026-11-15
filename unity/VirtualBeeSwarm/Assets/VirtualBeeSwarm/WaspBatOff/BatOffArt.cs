using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualBeeSwarm.Theme;

namespace VirtualBeeSwarm.BatOff
{
    /// <summary>
    /// The bat-off's art, all built in code: a low-poly cartoon wasp (body ~130 tris, wings 4; nose +Z, 1 unit long,
    /// vertex colours, one shared unlit material for every wasp) and the GUARD DUTY sticker card (UiCard + themed text).
    /// </summary>
    public static class BatOffArt
    {
        static readonly Color Scarlet = new Color(1f, 0.14f, 0.10f), Black = new Color(0.22f, 0.035f, 0.045f);
        static readonly Color Eye = new Color(0.12f, 0.08f, 0.05f), Wing = new Color(0.86f, 0.93f, 1f);

        /// <summary>The body: abdomen with stripes and a stinger point, a pinched wasp waist, thorax, a head with dark eyes.</summary>
        public static Mesh CreateWaspBody()
        {
            // rings along +Z (tail at -0.5, nose at +0.5): z, radius, colour
            float[] z = { -0.46f, -0.38f, -0.28f, -0.18f, -0.08f, 0.0f, 0.04f, 0.09f, 0.18f, 0.25f, 0.30f, 0.38f, 0.46f };
            float[] r = { 0.03f, 0.11f, 0.15f, 0.16f, 0.13f, 0.05f, 0.04f, 0.10f, 0.11f, 0.06f, 0.10f, 0.11f, 0.06f };
            Color[] col = { Black, Scarlet, Black, Scarlet, Black, Scarlet, Black, Black, Black, Black, Scarlet, Eye, Scarlet };
            const int seg = 6;
            var v = new List<Vector3>(z.Length * seg + 2);
            var c = new List<Color>(v.Capacity);
            var t = new List<int>(z.Length * seg * 6 + seg * 6);
            for (int i = 0; i < z.Length; i++)
                for (int s = 0; s < seg; s++)
                {
                    float a = s * Mathf.PI * 2f / seg;
                    v.Add(new Vector3(Mathf.Cos(a) * r[i], Mathf.Sin(a) * r[i] * 0.92f, z[i]));
                    c.Add(UiTheme.Vertex(col[i]));
                }
            for (int i = 0; i < z.Length - 1; i++)
                for (int s = 0; s < seg; s++)
                {
                    int a = i * seg + s, b = i * seg + (s + 1) % seg, a2 = a + seg, b2 = b + seg;
                    t.Add(a); t.Add(b); t.Add(a2);
                    t.Add(b); t.Add(b2); t.Add(a2);
                }
            int tail = v.Count;
            v.Add(new Vector3(0f, 0f, -0.55f)); c.Add(UiTheme.Vertex(Black)); // the stinger
            int nose = v.Count;
            v.Add(new Vector3(0f, -0.01f, 0.5f)); c.Add(UiTheme.Vertex(Scarlet));
            int last = (z.Length - 1) * seg;
            for (int s = 0; s < seg; s++)
            {
                t.Add(tail); t.Add((s + 1) % seg); t.Add(s);
                t.Add(nose); t.Add(last + s); t.Add(last + (s + 1) % seg);
            }
            var m = new Mesh { name = "VBS_BatOffWasp" };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Two flat wings on the thorax (origin = wing root line), swept back; flapped by scaling X.</summary>
        public static Mesh CreateWaspWings()
        {
            Color w = UiTheme.Vertex(Wing);
            var v = new[]
            {
                new Vector3(0.02f, 0f, 0.14f), new Vector3(0.42f, 0.06f, -0.02f), new Vector3(0.36f, 0.05f, -0.2f), new Vector3(0.02f, 0f, 0.04f),
                new Vector3(-0.02f, 0f, 0.14f), new Vector3(-0.42f, 0.06f, -0.02f), new Vector3(-0.36f, 0.05f, -0.2f), new Vector3(-0.02f, 0f, 0.04f),
            };
            var m = new Mesh { name = "VBS_BatOffWings" };
            m.vertices = v;
            m.colors = new[] { w, w, w, w, w, w, w, w };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6 };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        public static MeshRenderer Renderer(GameObject go, Mesh mesh, Material mat)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return r;
        }
    }

    /// <summary>The GUARD DUTY sticker card: "GUARD DUTY" over the bounced count; pops on every bounce.</summary>
    public sealed class GuardDutyCard : MonoBehaviour
    {
        public const float Width = 0.17f, Height = 0.085f;
        Transform _body;
        TextMeshPro _count;
        Transform _sticker;
        float _popAt = -10f, _shownAt = -10f;
        int _shown = -1, _goal;
        bool _visible, _won;

        public static GuardDutyCard Create(Transform parent)
        {
            var go = new GameObject("[VBS] GuardDutyCard");
            go.transform.SetParent(parent, false);
            var card = go.AddComponent<GuardDutyCard>();
            card.Build();
            go.SetActive(false);
            return card;
        }

        void Build()
        {
            _body = new GameObject("Body").transform;
            _body.SetParent(transform, false);
            UiCard.Create(_body, "Card", new Vector2(Width, Height), UiCard.Look.Paper);
            const float z = -0.002f;
            var title = UiTheme.NewText(_body, "Title", "GUARD DUTY", 13f, UiTheme.Ink, new Vector2(Width - 0.05f, 0.02f), display: true, wrap: false);
            if (title != null) title.transform.localPosition = new Vector3(0.018f, Height * 0.5f - 0.02f, z);
            _count = UiTheme.NewText(_body, "Count", "0 bounced", 16f, UiTheme.Approved, new Vector2(Width - 0.05f, 0.03f), display: true, wrap: false);
            if (_count != null) _count.transform.localPosition = new Vector3(0.018f, -0.012f, z);
            _sticker = StickerArt.Quad(_body, StickerArt.Flying, 0.05f, "Sticker");
            _sticker.localPosition = new Vector3(-Width * 0.5f + 0.03f, 0f, z * 2.5f);
            _sticker.localRotation = Quaternion.Euler(0f, 0f, 12f);
        }

        public void Show(bool on)
        {
            if (on == _visible) return;
            _visible = on;
            gameObject.SetActive(on);
            if (on) { _shownAt = Time.time; _shown = -1; _won = false; SetCount(0); }
        }

        /// <summary>Bounce this many to win: the count reads "n / goal" (0 = just "n bounced").</summary>
        public void SetGoal(int goal) { _goal = goal; _shown = -1; }

        public void SetCount(int n)
        {
            if (n == _shown || _won) return;
            if (_shown >= 0) _popAt = Time.time;
            _shown = n;
            // only on a change: no per-frame strings
            if (_count != null) _count.text = _goal > 0 ? n + "/" + _goal + " bounced" : n + " bounced";
        }

        /// <summary>The player bounced enough: the card says so and pops.</summary>
        public void SetWon(int n)
        {
            _won = true;
            _popAt = Time.time;
            if (_count != null) _count.text = "YOU WIN! " + n;
        }

        void LateUpdate()
        {
            float s = UiTheme.Pop((Time.time - _shownAt) / 0.35f);
            float pop = Time.time - _popAt;
            float bump = pop < 0.25f ? 1f + 0.18f * Mathf.Sin(pop / 0.25f * Mathf.PI) : 1f;
            _body.localScale = Vector3.one * Mathf.Max(0.01f, s * bump);
            if (_sticker != null) _sticker.localRotation = Quaternion.Euler(0f, 0f, 12f + (pop < 0.3f ? Mathf.Sin(pop * 40f) * 14f * (1f - pop / 0.3f) : 0f));
        }
    }
}
