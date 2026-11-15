using UnityEngine;

namespace VirtualBeeSwarm.Juice
{
    /// <summary>
    /// A dark plate behind Beeswax's caption, so the white text reads over the swarm, the smoke, the confetti and a light
    /// wall. Finds the caption TextMesh the builder makes (Beeswax/Caption/Text), parents one opaque unlit box behind it
    /// and resizes it when the caption changes (GameNet.CurrentCaption, compared by reference: no per-frame allocation).
    /// No caption = no plate. Switch: tuning.json juice.captions.
    /// </summary>
    [DefaultExecutionOrder(70)]
    public class JuiceCaptionPlate : MonoBehaviour
    {
        static readonly Color PlateColor = new Color(0.09f, 0.06f, 0.03f, 1f);
        const float PadX = 0.022f, PadY = 0.012f, Behind = 0.004f;

        GameNet _net;
        TextMesh _text;
        MeshRenderer _textR;
        Transform _plate;
        GameObject _plateGo;
        string _shown;
        float _nextFind;
        int _settle;
        bool _warned;

        public void Bind(GameNet net) => _net = net;

        void Find()
        {
            // the caption TextMesh is BeeswaxController's serialized "caption" (as CaptionFit finds it), else by name
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var field = typeof(BeeswaxController).GetField("caption", F);
            foreach (var bw in FindObjectsByType<BeeswaxController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                _text = field != null ? field.GetValue(bw) as TextMesh : null;
                if (_text == null)
                    foreach (var t in bw.GetComponentsInChildren<TextMesh>(true))
                        if (t.transform.parent != null && t.transform.parent.name.StartsWith("Caption")) { _text = t; break; }
                if (_text != null) break;
            }
            if (_text == null)
            {
                if (!_warned && Time.time > 10f) { _warned = true; Debug.LogWarning("[VBS][Juice] caption plate: no Beeswax caption text found"); }
                return;
            }
            _textR = _text.GetComponent<MeshRenderer>();
            _plateGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _plateGo.name = "[VBS] Caption Plate";
            var col = _plateGo.GetComponent<Collider>();
            if (col != null) Destroy(col);
            _plate = _plateGo.transform;
            _plate.SetParent(_text.transform, false);
            var r = _plateGo.GetComponent<Renderer>();
            r.sharedMaterial = GameMaterials.Create("[VBS] Caption Plate", PlateColor);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            _plateGo.SetActive(false);
            Debug.Log("[VBS][Juice] caption plate ready");
        }

        void LateUpdate()
        {
            if (_text == null)
            {
                if (Time.time < _nextFind) return;
                _nextFind = Time.time + 2f;
                Find();
                if (_text == null) return;
            }
            string c = _net != null ? _net.CurrentCaption : "";
            if (!ReferenceEquals(c, _shown))
            {
                _shown = c;
                _settle = 3; // the TextMesh rebuilds its mesh after BeeswaxController sets the text: size the plate then
                if (string.IsNullOrEmpty(c) && _plateGo.activeSelf) _plateGo.SetActive(false);
            }
            if (_settle <= 0) return;
            _settle--;
            if (string.IsNullOrEmpty(_shown) || _textR == null) return;
            Bounds b = _textR.localBounds;
            if (b.size.x < 1e-4f || b.size.y < 1e-4f) return;
            _plate.localPosition = new Vector3(b.center.x, b.center.y, Behind);
            _plate.localScale = new Vector3(b.size.x + PadX * 2f, b.size.y + PadY * 2f, 0.002f);
            if (!_plateGo.activeSelf) _plateGo.SetActive(true);
        }
    }
}
