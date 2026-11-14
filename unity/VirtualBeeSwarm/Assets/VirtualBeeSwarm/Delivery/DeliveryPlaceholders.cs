using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Primitive placeholders for the new v3.1 station props (Swatter, SprayBottle, Crowbar, Reducer, NothingTag) and
    /// the rain cloud, built at runtime when the scene doesn't have them. Pivot at the base, front = -Z (the player).
    /// The modeler replaces them by putting a prop with the same name under the station's Props.
    /// </summary>
    public static class DeliveryPlaceholders
    {
        static readonly Dictionary<int, Material> Mats = new Dictionary<int, Material>();
        static Font _font;

        /// <summary>Placeholder weight (kg) per choice: crew = 3 + round(w x 10). The jar is the heavy one.</summary>
        public static float Weight(string choice)
        {
            switch (choice)
            {
                case GameIds.Choice.StoreHoney: return 0.5f;
                case GameIds.Choice.Nothing: return 0f;
                case GameIds.Choice.Smoker: return 0.12f;
                default: return 0.1f;
            }
        }

        public static Transform Build(string choice, Transform parent)
        {
            var root = new GameObject(StationDelivery.PropName(choice)).transform;
            root.SetParent(parent, false);
            switch (choice)
            {
                case GameIds.Choice.Swat:
                    Prim(PrimitiveType.Cylinder, "Handle", root, new Vector3(0f, 0.045f, 0f), new Vector3(0.012f, 0.045f, 0.012f), new Color(0.45f, 0.32f, 0.2f));
                    Prim(PrimitiveType.Cube, "Paddle", root, new Vector3(0f, 0.125f, 0f), new Vector3(0.065f, 0.07f, 0.006f), new Color(0.85f, 0.15f, 0.12f));
                    break;
                case GameIds.Choice.SprayWater:
                    Prim(PrimitiveType.Cylinder, "Body", root, new Vector3(0f, 0.05f, 0f), new Vector3(0.045f, 0.05f, 0.045f), new Color(0.3f, 0.6f, 0.95f));
                    Prim(PrimitiveType.Cylinder, "Neck", root, new Vector3(0f, 0.108f, 0f), new Vector3(0.02f, 0.01f, 0.02f), new Color(0.95f, 0.95f, 0.95f));
                    Prim(PrimitiveType.Cube, "Head", root, new Vector3(0f, 0.128f, -0.008f), new Vector3(0.024f, 0.022f, 0.04f), new Color(0.95f, 0.95f, 0.95f));
                    Label(root, new Vector3(0f, 0.05f, -0.024f), "WATER", 0.0018f);
                    break;
                case GameIds.Choice.BiggerDoor:
                {
                    var shaft = Prim(PrimitiveType.Cube, "Shaft", root, new Vector3(0f, 0.065f, 0f), new Vector3(0.014f, 0.13f, 0.014f), new Color(0.75f, 0.12f, 0.1f));
                    shaft.transform.localRotation = Quaternion.Euler(0f, 0f, 8f);
                    Prim(PrimitiveType.Cube, "Hook", root, new Vector3(-0.012f, 0.128f, 0f), new Vector3(0.04f, 0.014f, 0.014f), new Color(0.75f, 0.12f, 0.1f));
                    Prim(PrimitiveType.Cube, "Claw", root, new Vector3(0.012f, 0.004f, 0f), new Vector3(0.03f, 0.008f, 0.014f), new Color(0.55f, 0.57f, 0.6f));
                    break;
                }
                case GameIds.Choice.SmallerDoor:
                    Prim(PrimitiveType.Cube, "Block", root, new Vector3(0f, 0.015f, 0f), new Vector3(0.12f, 0.03f, 0.025f), new Color(0.7f, 0.5f, 0.28f));
                    Prim(PrimitiveType.Cube, "Slot", root, new Vector3(0f, 0.008f, -0.001f), new Vector3(0.035f, 0.012f, 0.026f), new Color(0.08f, 0.06f, 0.05f));
                    break;
                case GameIds.Choice.Nothing:
                    Prim(PrimitiveType.Cube, "Card", root, new Vector3(0f, 0.03f, 0f), new Vector3(0.08f, 0.05f, 0.004f), new Color(0.97f, 0.95f, 0.88f));
                    Label(root, new Vector3(0f, 0.03f, -0.003f), "NOTHING", 0.0024f);
                    break;
                default:
                    Prim(PrimitiveType.Cube, "Box", root, new Vector3(0f, 0.04f, 0f), Vector3.one * 0.08f, new Color(0.9f, 0.8f, 0.5f));
                    break;
            }
            return root;
        }

        /// <summary>A small grey cloud of spheres (pivot at its base) for the fire brigade.</summary>
        public static Transform BuildCloud(Transform parent)
        {
            var root = new GameObject("RainCloud").transform;
            root.SetParent(parent, false);
            var c = new Color(0.78f, 0.8f, 0.86f);
            Prim(PrimitiveType.Sphere, "Puff0", root, new Vector3(0f, 0.06f, 0f), new Vector3(0.12f, 0.08f, 0.1f), c);
            Prim(PrimitiveType.Sphere, "Puff1", root, new Vector3(-0.06f, 0.05f, 0.01f), new Vector3(0.09f, 0.065f, 0.08f), c);
            Prim(PrimitiveType.Sphere, "Puff2", root, new Vector3(0.06f, 0.05f, -0.01f), new Vector3(0.09f, 0.06f, 0.08f), c);
            Prim(PrimitiveType.Sphere, "Puff3", root, new Vector3(0.015f, 0.09f, 0.0f), new Vector3(0.08f, 0.06f, 0.07f), c);
            return root;
        }

        public static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 localScale, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            BeeDelivery.DestroyCollider(go);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = Mat(color);
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return go;
        }

        static Material Mat(Color c)
        {
            int key = ((int)(c.r * 255f) << 16) | ((int)(c.g * 255f) << 8) | (int)(c.b * 255f);
            if (Mats.TryGetValue(key, out var m) && m != null) return m;
            m = GameMaterials.Create("VBS_DeliveryProp (runtime)", c);
            Mats[key] = m;
            return m;
        }

        static void Label(Transform parent, Vector3 localPos, string text, float charSize)
        {
            if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var tm = go.AddComponent<TextMesh>();
            tm.font = _font;
            tm.fontSize = 64;
            tm.characterSize = charSize;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = Color.black;
            tm.text = text;
            var r = go.GetComponent<MeshRenderer>();
            if (_font != null) r.sharedMaterial = _font.material;
            r.shadowCastingMode = ShadowCastingMode.Off;
        }
    }
}
