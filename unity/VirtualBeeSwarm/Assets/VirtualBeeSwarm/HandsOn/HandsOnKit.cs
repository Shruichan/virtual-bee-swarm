using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.HandsOn
{
    /// <summary>Colours and runtime primitives for the hands-on props (no assets: cubes, cylinders, TextMesh).</summary>
    public static class HandsOnKit
    {
        public static readonly Color Honey = new Color(1f, 0.74f, 0.12f);
        public static readonly Color HoneyDim = new Color(0.5f, 0.36f, 0.12f);
        public static readonly Color Wood = new Color(0.27f, 0.17f, 0.09f);
        public static readonly Color Cream = new Color(0.98f, 0.94f, 0.82f);
        public static readonly Color Brass = new Color(0.82f, 0.64f, 0.26f);
        public static readonly Color Good = new Color(0.3f, 0.82f, 0.3f);
        public static readonly Color Bad = new Color(0.8f, 0.12f, 0.08f);
        public static readonly Color Ink = new Color(0.13f, 0.09f, 0.05f);
        public static readonly Color Water = new Color(0.25f, 0.6f, 1f);

        static Material _glow;
        static Font _font;

        public static Transform Head
        {
            get
            {
                if (BeeDelivery.Instance != null && BeeDelivery.Instance.Head != null) return BeeDelivery.Instance.Head;
                var cam = Camera.main;
                return cam != null ? cam.transform : null;
            }
        }

        public static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 localScale, Color color) =>
            DeliveryPlaceholders.Prim(type, name, parent, localPos, localScale, color);

        /// <summary>See-through primitive: one shared alpha material, tinted per renderer (GameMaterials.Tint).</summary>
        public static Renderer Glow(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 localScale, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            BeeDelivery.DestroyCollider(go);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            if (_glow == null) _glow = GameMaterials.Create("VBS_HandsOnGlow (runtime)", Color.white, GameMaterials.Blend.Alpha, doubleSided: true);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = _glow;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            GameMaterials.Tint(r, color);
            return r;
        }

        public static TextMesh Text(string name, Transform parent, Vector3 localPos, string text, float charSize, Color color)
        {
            if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var tm = go.AddComponent<TextMesh>();
            tm.font = _font;
            tm.fontSize = 64;
            tm.fontStyle = FontStyle.Bold;
            tm.characterSize = charSize;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            tm.text = text;
            var r = go.GetComponent<MeshRenderer>();
            if (_font != null) r.sharedMaterial = _font.material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return tm;
        }

        /// <summary>Rough width/height (m) of TextMesh text at fontSize 64 (bold LegacyRuntime caps).</summary>
        public static Vector2 TextSize(string text, float charSize)
        {
            int lines = 1, longest = 0, run = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') { lines++; run = 0; }
                else if (++run > longest) longest = run;
            }
            float line = charSize * 6.4f;
            return new Vector2(longest * line * 0.6f, lines * line * 1.15f);
        }

        /// <summary>A flat hexagon (three crossed boxes), `across` corner to corner, standing on y = 0.</summary>
        public static Transform Hexagon(string name, Transform parent, Vector3 localPos, float across, float height, Color color)
        {
            var root = new GameObject(name).transform;
            root.SetParent(parent, false);
            root.localPosition = localPos;
            float side = across * 0.5f, flats = across * 0.866f;
            for (int k = 0; k < 3; k++)
            {
                var b = Prim(PrimitiveType.Cube, "Hex" + k, root, new Vector3(0f, height * 0.5f, 0f), new Vector3(side, height, flats), color);
                b.transform.localRotation = Quaternion.Euler(0f, k * 60f, 0f);
            }
            return root;
        }

        public static float Smooth(float k)
        {
            k = Mathf.Clamp01(k);
            return k * k * (3f - 2f * k);
        }
    }
}
