using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Juice
{
    /// <summary>
    /// The window frame answers (feel audit gaps 3 and 8): 24 marquee bulbs over the hex frame's dots, one instanced draw.
    /// Idle: a slow warm twinkle. Window scene change: a 0.45 s chase. Right pick: a gold chase twice round. Wrong pick: a
    /// red double blink. Escape: alternating gold strobe. Mic open: the bulbs breathe with the hum / voice level.
    /// Only while the window is open.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public class JuiceMarquee : MonoBehaviour
    {
        const int PerEdge = 4, N = PerEdge * 6;
        // where the frame's dots sit, as a fraction of the pane's half width (flat-top hex; tuned on the v33 capture)
        const float KX = 1.085f, KY = 0.84f, SizeOfHalf = 0.08f, Ahead = 0.008f, MaxFront = 0.03f;
        static readonly int ColorArrayId = Shader.PropertyToID("_Color");
        static readonly Color Warm = new Color(1f, 0.96f, 0.84f, 1f);  // idle: the art's own pale dots
        static readonly Color Dark = new Color(0.3f, 0.17f, 0.07f, 1f); // a bulb that is off
        static readonly Color Gold = new Color(1f, 0.95f, 0.66f, 1f); // hot white-gold: reads on the yellow frame next to dark bulbs
        static readonly Color Red = new Color(1f, 0.12f, 0.08f, 1f);

        readonly Matrix4x4[] _m = new Matrix4x4[N];
        readonly Vector4[] _c = new Vector4[N];
        readonly Vector2[] _unit = new Vector2[N];
        MaterialPropertyBlock _mpb;
        Mesh _mesh;
        Material _mat;
        bool _instancing;
        FutureWindow _win;
        GameNet _net;
        float _nextFind, _chaseAt = -9f, _chaseDur = 0.45f, _chaseLaps = 1f, _blinkAt = -9f, _strobeUntil = -9f, _punchAt = -9f;
        Color _chaseColor = Warm, _blinkColor = Red;
        int _state = -1;
        Renderer[] _frame = new Renderer[0];
        float _front = 0.012f, _nextFront;
        static readonly string[] StateNames = { "showing", "off: no FutureWindow", "off: window inactive", "off: no pane renderer", "off: portal closed", "off: pane too small" };

        public bool Visible { get; private set; }

        void Awake()
        {
            _mesh = GameMeshes.CreateHexPrism(0.5f, 0.3f, "VBS_JuiceBulb");
            _mat = GameMaterials.Create("VBS_JuiceBulb (runtime)", Color.white);
            _mpb = new MaterialPropertyBlock();
            _instancing = SystemInfo.supportsInstancing;
            // flat-top hexagon: corners at 0, 60, ... degrees (pointy left and right), 4 bulbs per edge
            int k = 0;
            for (int e = 0; e < 6; e++)
            {
                float a0 = e * Mathf.PI / 3f, a1 = (e + 1) * Mathf.PI / 3f;
                Vector2 v0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)), v1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                for (int j = 0; j < PerEdge; j++) _unit[k++] = Vector2.Lerp(v0, v1, (j + 0.5f) / PerEdge);
            }
        }

        public void Bind(GameNet net) => _net = net;

        public void Chase(Color c, float seconds = 0.45f, float laps = 1f) { _chaseAt = Time.time; _chaseColor = c; _chaseDur = seconds; _chaseLaps = laps; _punchAt = Time.time; }
        public void Sting(bool right)
        {
            if (right) Chase(Gold, 0.8f, 2f);
            else { _blinkAt = Time.time; _blinkColor = Red; _punchAt = Time.time; }
        }
        public void Strobe(float seconds) => _strobeUntil = Time.time + seconds;
        public void ResetAll() { _chaseAt = _blinkAt = _strobeUntil = _punchAt = -9f; }

        void LateUpdate()
        {
            float now = Time.time;
            if (_win == null && now >= _nextFind) { _nextFind = now + 2f; _win = FindAnyObjectByType<FutureWindow>(); }
            Renderer pane = null;
            int state = 1;
            if (_win != null)
            {
                state = 2;
                if (_win.isActiveAndEnabled)
                {
                    state = 3;
                    var pt = _win.PaneTransform;
                    pane = pt != null ? pt.GetComponent<Renderer>() : null;
                    // the portal is active only while the window is (partly) open; the live pane may hide the fallback renderer
                    if (pane != null && !pane.gameObject.activeInHierarchy) { pane = null; state = 4; }
                }
            }
            float half = 0f;
            Vector3 center = Vector3.zero;
            Transform t = pane != null ? pane.transform : null;
            if (pane != null)
            {
                Bounds lb = pane.localBounds;
                half = Mathf.Abs(lb.extents.x * t.lossyScale.x);
                center = t.TransformPoint(lb.center);
                if (half < 0.02f) // no usable local bounds: the world box of a pane standing upright
                {
                    Bounds wb = pane.bounds;
                    Vector3 r = t.right;
                    float k = Mathf.Abs(r.x) + Mathf.Abs(r.z);
                    half = k > 0.1f ? (wb.extents.x + wb.extents.z) / k : wb.extents.x;
                    center = wb.center;
                }
                state = half < 0.02f ? 5 : 0;
            }
            if (state == 0 && (state != _state || now >= _nextFront)) { _nextFront = now + 2f; MeasureFront(pane, center, state != _state); }
            if (state != _state)
            {
                _state = state;
                Debug.Log("[VBS][Juice] marquee " + StateNames[state] + (state == 0 ? " on " + pane.name + ", half width " + half.ToString("0.000") + " m, frame front " + _front.ToString("0.000") + " m" : ""));
            }
            Visible = state == 0;
            if (!Visible) return;

            var cam = Camera.main;
            Vector3 toHead = cam != null ? (cam.transform.position - center).normalized : -t.forward;
            Vector3 right = t.right, up = t.up;
            float p = now - _punchAt;
            float punch = p < 0.15f ? 1f + 0.015f * Mathf.Sin(p / 0.15f * Mathf.PI) : 1f;
            float rx = half * KX * punch, ry = half * KY * punch;
            Quaternion rot = Quaternion.LookRotation(-toHead, up);

            float mic = 0f;
            if (_net != null && _net.MicListening) mic = Mathf.Clamp01(Mathf.Max(_net.HumLevel, _net.VoiceAmplitude) * 3f);
            float chaseT = (now - _chaseAt) / _chaseDur;
            float blink = now - _blinkAt;
            bool blinkOn = (blink >= 0f && blink < 0.13f) || (blink >= 0.24f && blink < 0.37f);
            bool strobe = now < _strobeUntil;
            int strobePhase = (int)(now / 0.12f) & 1;

            float size = half * SizeOfHalf; // just over the frame's dots, whatever the window's size
            bool chasing = chaseT >= 0f && chaseT < 1f, blinking = blink >= 0f && blink < 0.5f;
            for (int i = 0; i < N; i++)
            {
                Vector3 pos = center + right * (_unit[i].x * rx) + up * (_unit[i].y * ry) + _normal * (_front + Ahead);
                float tw = 0.85f + 0.15f * Mathf.Sin(now * 1.7f + i * 2.399f);
                if (mic > 0f) tw = 0.55f + 0.6f * mic * (0.5f + 0.5f * Mathf.Sin(now * 9f + i * 0.5f));
                Color c = Warm * tw;
                float s = size;
                if (chasing)
                {
                    float d = Mathf.Repeat(chaseT * N * _chaseLaps - i, N);
                    if (d < 5f) { float w = 1f - d / 5f; c = Color.Lerp(Dark, _chaseColor, w); s *= 1f + 0.3f * w; }
                    else c = Dark;
                }
                if (blinking) { c = blinkOn ? _blinkColor : Dark; if (blinkOn) s *= 1.2f; }
                if (strobe) { bool on = (i & 1) == strobePhase; c = on ? Gold : Dark; s *= on ? 1.2f : 0.95f; }
                _m[i] = Matrix4x4.TRS(pos, rot, new Vector3(s, s, s));
                _c[i] = new Vector4(c.r, c.g, c.b, 1f);
            }

            if (_instancing)
            {
                _mpb.SetVectorArray(ColorArrayId, _c);
                Graphics.DrawMeshInstanced(_mesh, 0, _mat, _m, N, _mpb, ShadowCastingMode.Off, false, gameObject.layer);
            }
            else
                for (int i = 0; i < N; i++)
                {
                    _mpb.SetColor(ColorArrayId, _c[i]);
                    Graphics.DrawMesh(_mesh, _m[i], _mat, gameObject.layer, null, 0, _mpb, ShadowCastingMode.Off, false);
                }
        }

        Vector3 _normal = Vector3.back;

        /// <summary>How far the frame art stands out of the pane toward the player (bulbs sit just in front of it):
        /// the oriented boxes of the renderers under the pane's parent (the portal), projected on the pane normal.</summary>
        void MeasureFront(Renderer pane, Vector3 center, bool rescan)
        {
            var t = pane.transform;
            var cam = Camera.main;
            _normal = -t.forward; // a quad faces -Z
            if (cam != null && Vector3.Dot(cam.transform.position - center, _normal) < 0f) _normal = -_normal;
            var root = t.parent != null ? t.parent : t;
            if (rescan) _frame = root.GetComponentsInChildren<Renderer>(false); // only when the window (re)opens
            float front = 0.005f;
            foreach (var r in _frame)
            {
                if (r == null || r == pane || r is ParticleSystemRenderer || !r.gameObject.activeInHierarchy) continue;
                Bounds lb = r.localBounds;
                var rt = r.transform;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 c = lb.center + Vector3.Scale(lb.extents, new Vector3((k & 1) == 0 ? -1f : 1f, (k & 2) == 0 ? -1f : 1f, (k & 4) == 0 ? -1f : 1f));
                    float d = Vector3.Dot(rt.TransformPoint(c) - center, _normal);
                    if (d > front && d < 0.25f) front = d;
                }
            }
            _front = Mathf.Min(front, MaxFront); // a sill or planter may stand further out than the dots
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_mat != null) Destroy(_mat);
        }
    }
}
