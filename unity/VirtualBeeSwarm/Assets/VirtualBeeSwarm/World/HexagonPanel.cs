using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The 4 hexagons on the hive (index = GameStation). Each lights up when its station is solved; all four pulse
    /// together at the escape. Modeler: swap the hex renderers for the real panel; keep the array order.
    /// </summary>
    public class HexagonPanel : MonoBehaviour
    {
        [SerializeField] Renderer[] hexagons = new Renderer[4];
        [SerializeField] Color offColor = new Color(0.25f, 0.2f, 0.12f, 1f);
        [SerializeField] Color onColor = new Color(1f, 0.75f, 0.1f, 1f);
        [SerializeField] Color celebrateColor = new Color(1f, 0.95f, 0.55f, 1f);
        [SerializeField] float popScale = 1.35f;

        [SerializeField] int faceMaterialIndex = -1;

        public int LitCount { get; private set; }

        readonly bool[] _lit = new bool[4];
        readonly float[] _litAt = new float[4];
        Vector3[] _baseScale;
        bool _celebrate;

        void Awake()
        {
            _faceTint = new MaterialPropertyBlock();
            _baseScale = new Vector3[hexagons.Length];
            for (int i = 0; i < hexagons.Length; i++)
                if (hexagons[i] != null) _baseScale[i] = hexagons[i].transform.localScale;
            ResetAll();
        }

        public bool IsLit(int i) => i >= 0 && i < _lit.Length && _lit[i];

        public void Light(int index)
        {
            if (index < 0 || index >= _lit.Length || _lit[index]) return;
            _lit[index] = true;
            _litAt[index] = Time.time;
            LitCount++;
        }

        /// <summary>v2: pop one lit hexagon again (the minutes read back the four S's, one per hexagon).</summary>
        public void Pulse(int index)
        {
            if (index < 0 || index >= _lit.Length || !_lit[index]) return;
            _litAt[index] = Time.time;
        }

        public void LightAll()
        {
            for (int i = 0; i < _lit.Length; i++) Light(i);
        }

        /// <summary>Escape: all four glow and pulse.</summary>
        public void Celebrate(bool on)
        {
            if (on) LightAll();
            _celebrate = on;
        }

        public void ResetAll()
        {
            for (int i = 0; i < _lit.Length; i++) _lit[i] = false;
            LitCount = 0;
            _celebrate = false;
            for (int i = 0; i < hexagons.Length; i++)
            {
                if (hexagons[i] == null) continue;
                TintFace(hexagons[i], offColor);
                if (_baseScale != null) hexagons[i].transform.localScale = _baseScale[i];
            }
        }

        MaterialPropertyBlock _faceTint;

        void TintFace(Renderer renderer, Color color)
        {
            if (faceMaterialIndex < 0)
            {
                GameMaterials.Tint(renderer, color);
                return;
            }

            renderer.GetPropertyBlock(_faceTint, faceMaterialIndex);
            _faceTint.SetColor(GameMaterials.ColorId, color);
            _faceTint.SetColor(GameMaterials.BaseColorId, color);
            renderer.SetPropertyBlock(_faceTint, faceMaterialIndex);
        }

        void Update()
        {
            for (int i = 0; i < hexagons.Length && i < _lit.Length; i++)
            {
                var r = hexagons[i];
                if (r == null || !_lit[i]) continue;
                float age = Time.time - _litAt[i];
                float pop = age < 0.6f ? Mathf.Sin(age / 0.6f * Mathf.PI) * (popScale - 1f) : 0f;
                float pulse = _celebrate ? 0.5f + 0.5f * Mathf.Sin(Time.time * 6f + i * 0.8f) : 0f;
                r.transform.localScale = _baseScale[i] * (1f + pop + pulse * 0.12f);
                var c = _celebrate ? Color.Lerp(onColor, celebrateColor, pulse) : onColor;
                if (age < 0.6f || _celebrate) TintFace(r, c);
                else if (age < 0.7f) TintFace(r, onColor);
            }
        }
    }
}
