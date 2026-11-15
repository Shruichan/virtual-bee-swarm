using UnityEngine;

namespace VirtualBeeSwarm.HandsOn
{
    /// <summary>
    /// "Put it here": a glowing honeycomb ring where a set-up step ends, with a fill disc (the pour meter) and three
    /// beads that sweep from the prop to the ring as a hint. Flat on a surface, or upright (the hive door).
    /// </summary>
    public class SetupSocket : MonoBehaviour
    {
        Transform _ring, _fillDisc;
        Renderer[] _segments;
        Renderer _fill;
        Renderer[] _beads;
        float _radius = 0.07f, _tight, _burstAt = -1f;
        bool _sweep;
        Vector3 _sweepFrom;

        public bool Bright;
        public Color Tint = HandsOnKit.Honey;
        public float Fill;
        public bool Visible => gameObject.activeSelf;

        public static SetupSocket Build(Transform parent)
        {
            var go = new GameObject("[VBS] SetupSocket");
            go.transform.SetParent(parent, false);
            var s = go.AddComponent<SetupSocket>();
            s._ring = new GameObject("Ring").transform;
            s._ring.SetParent(go.transform, false);
            s._segments = new Renderer[6];
            for (int k = 0; k < 6; k++)
            {
                Quaternion q = Quaternion.Euler(0f, k * 60f, 0f);
                var seg = HandsOnKit.Glow(PrimitiveType.Cube, "Seg" + k, s._ring, q * new Vector3(0f, 0f, 0.866f), new Vector3(1.02f, 0.1f, 0.13f), HandsOnKit.Honey);
                seg.transform.localRotation = q;
                s._segments[k] = seg;
            }
            s._fill = HandsOnKit.Glow(PrimitiveType.Cylinder, "Fill", s._ring, Vector3.zero, new Vector3(0f, 0.03f, 0f), HandsOnKit.Honey);
            s._fillDisc = s._fill.transform;
            s._beads = new Renderer[3];
            for (int k = 0; k < 3; k++)
                s._beads[k] = HandsOnKit.Glow(PrimitiveType.Sphere, "Bead" + k, go.transform, Vector3.zero, Vector3.one * 0.014f, HandsOnKit.Honey);
            go.SetActive(false);
            return s;
        }

        public void Show(Vector3 worldPos, Quaternion worldRot, float radius, bool upright)
        {
            transform.SetPositionAndRotation(worldPos, upright ? worldRot * Quaternion.Euler(90f, 0f, 0f) : worldRot);
            _radius = radius;
            _tight = 0f;
            _burstAt = -1f;
            Bright = false;
            Fill = 0f;
            Tint = HandsOnKit.Honey;
            _sweep = false;
            gameObject.SetActive(true);
            Paint();
        }

        public void Hide()
        {
            if (gameObject.activeSelf) gameObject.SetActive(false);
        }

        /// <summary>0..1: the ring closes in as the prop gets near.</summary>
        public void Tighten(float k) => _tight = Mathf.Clamp01(k);

        /// <summary>Hint: beads run from here to the ring this frame on (call every frame; StopSweep ends it).</summary>
        public void SweepFrom(Vector3 worldFrom) { _sweep = true; _sweepFrom = worldFrom; }
        public void StopSweep() => _sweep = false;

        /// <summary>Success: the ring pops outward and fades, then hides.</summary>
        public void Burst(Color c)
        {
            Tint = c;
            _burstAt = Time.time;
            _sweep = false;
        }

        void LateUpdate() => Paint();

        void Paint()
        {
            float t = Time.time;
            float pulse = 0.5f + 0.5f * Mathf.Sin(t * (Bright ? 9f : 4f));
            float alpha = Bright ? 0.75f + 0.25f * pulse : 0.45f + 0.3f * pulse;
            float scale = _radius * Mathf.Lerp(1f, 0.72f, _tight) * (1f + (Bright ? 0.08f : 0.03f) * pulse);
            if (_burstAt >= 0f)
            {
                float k = (t - _burstAt) / 0.4f;
                if (k >= 1f) { _burstAt = -1f; Hide(); return; }
                scale = _radius * Mathf.Lerp(1f, 1.9f, k);
                alpha = 1f - k;
            }
            _ring.localScale = Vector3.one * scale;
            Color c = Tint;
            c.a = alpha;
            for (int i = 0; i < _segments.Length; i++) GameMaterials.Tint(_segments[i], c);

            float f = Mathf.Clamp01(Fill);
            bool fill = f > 0.001f && _burstAt < 0f;
            if (_fill.gameObject.activeSelf != fill) _fill.gameObject.SetActive(fill);
            if (fill)
            {
                _fillDisc.localScale = new Vector3(1.6f * f, 0.03f, 1.6f * f);
                GameMaterials.Tint(_fill, new Color(1f, 0.8f, 0.25f, 0.85f));
            }

            for (int k = 0; k < _beads.Length; k++)
            {
                var b = _beads[k];
                if (b.gameObject.activeSelf != _sweep) b.gameObject.SetActive(_sweep);
                if (!_sweep) continue;
                float u = Mathf.Repeat(t * 0.8f - k * 0.13f, 1f);
                b.transform.position = Vector3.Lerp(_sweepFrom, transform.position, u) + Vector3.up * (Mathf.Sin(u * Mathf.PI) * 0.05f);
                GameMaterials.Tint(b, new Color(1f, 0.85f, 0.3f, Mathf.Sin(u * Mathf.PI)));
            }
        }
    }
}
