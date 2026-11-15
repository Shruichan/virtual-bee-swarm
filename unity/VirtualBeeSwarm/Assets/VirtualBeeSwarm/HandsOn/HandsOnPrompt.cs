using UnityEngine;

namespace VirtualBeeSwarm.HandsOn
{
    /// <summary>
    /// One floating instruction label (label text, never Beeswax speech): dark pill, honey letters, faces the
    /// player, follows a transform, and is pushed back so it is never nearer than 0.45 m to the eyes.
    /// </summary>
    public class HandsOnPrompt : MonoBehaviour
    {
        const float MinEyeDistance = 0.45f;

        TextMesh _text;
        Renderer _back;
        Transform _follow;
        Vector3 _offset;
        float _until = -1f, _shownAt;
        float _charSize = 0.0026f;

        public string Current { get; private set; } = "";
        public bool Visible => gameObject.activeSelf;

        public static HandsOnPrompt Build(Transform parent, string name, float charSize = 0.0026f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var p = go.AddComponent<HandsOnPrompt>();
            p._charSize = charSize;
            p._back = HandsOnKit.Glow(PrimitiveType.Quad, "Back", go.transform, new Vector3(0f, 0f, 0.003f), Vector3.one, new Color(0.1f, 0.07f, 0.04f, 0.86f));
            p._text = HandsOnKit.Text("Text", go.transform, Vector3.zero, "", charSize, HandsOnKit.Honey);
            p._text.GetComponent<Renderer>().sortingOrder = 2;
            go.SetActive(false);
            return p;
        }

        /// <summary>Show until Hide (seconds &lt;= 0) or for `seconds`.</summary>
        public void Show(string text, Transform follow, Vector3 offset, float seconds = 0f, Color? color = null)
        {
            _follow = follow;
            _offset = offset;
            _until = seconds > 0f ? Time.time + seconds : -1f;
            _text.color = color ?? HandsOnKit.Honey;
            if (text != Current || !gameObject.activeSelf)
            {
                Current = text;
                _text.text = text;
                Vector2 s = HandsOnKit.TextSize(text, _charSize);
                _back.transform.localScale = new Vector3(s.x + 0.03f, s.y + 0.016f, 1f);
                _shownAt = Time.time;
            }
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            Place();
        }

        public void Hide()
        {
            Current = "";
            _until = -1f;
            if (gameObject.activeSelf) gameObject.SetActive(false);
        }

        void LateUpdate()
        {
            if (_until > 0f && Time.time > _until) { Hide(); return; }
            Place();
        }

        void Place()
        {
            Vector3 p = _follow != null ? _follow.position + _offset : transform.position;
            Transform head = HandsOnKit.Head;
            if (head != null)
            {
                Vector3 d = p - head.position;
                float m = d.magnitude;
                if (m < 1e-4f) { d = head.forward; m = 1f; }
                if (m < MinEyeDistance) p = head.position + d / m * MinEyeDistance;
                // parallel to the view and upright (a label off to the side must not look rolled)
                Vector3 f = Vector3.Slerp(d / m, head.forward, 0.7f);
                if (Mathf.Abs(f.y) > 0.98f) f = d / m;
                transform.rotation = Quaternion.LookRotation(f, Vector3.up);
            }
            transform.position = p;
            float k = Mathf.Clamp01((Time.time - _shownAt) / 0.14f);
            transform.localScale = Vector3.one * Mathf.Lerp(0.6f, 1f, 1f - (1f - k) * (1f - k));
        }
    }
}
