using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.Theme
{
    /// <summary>
    /// Daniel's sticker bees (Resources/Stickers/bee-1..5.png, the white outside the sticker border already transparent):
    /// 1 smiling, 2 front, 3 flying, 4 sleeping, 5 winking. One alpha-blended VirtualBeeSwarm/Unlit material per bee
    /// (so the Depth API hides them behind real things like everything else) and a shared quad facing -Z.
    /// </summary>
    public static class StickerArt
    {
        public const int Smiling = 1, Front = 2, Flying = 3, Sleeping = 4, Winking = 5, Count = 5;

        static readonly Material[] Mats = new Material[Count + 1];
        static Mesh _quad, _hex, _bar;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            for (int i = 0; i < Mats.Length; i++) Mats[i] = null;
            _quad = _hex = _bar = null;
        }

        public static Material Material(int bee)
        {
            bee = Mathf.Clamp(bee, 1, Count);
            if (Mats[bee] != null) return Mats[bee];
            var tex = Resources.Load<Texture2D>("Stickers/bee-" + bee);
            var m = GameMaterials.Create("VBS_Sticker" + bee + " (runtime)", Color.white, GameMaterials.Blend.Alpha, doubleSided: true);
            if (tex != null)
            {
                if (m.HasProperty(GameMaterials.MainTexId)) m.SetTexture(GameMaterials.MainTexId, tex);
                if (m.HasProperty(GameMaterials.BaseMapId)) m.SetTexture(GameMaterials.BaseMapId, tex);
            }
            else Debug.LogWarning("[VBS][Stickers] Resources/Stickers/bee-" + bee + " is missing");
            Mats[bee] = m;
            return m;
        }

        /// <summary>A sticker of `size` metres (the texture's square) under `parent`, facing -Z.</summary>
        public static Transform Quad(Transform parent, int bee, float size, string name = "[VBS] Sticker")
        {
            var go = new GameObject(name);
            go.layer = parent != null ? parent.gameObject.layer : 0;
            go.transform.SetParent(parent, false);
            go.transform.localScale = new Vector3(size, size, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = QuadMesh;
            Plain(go.AddComponent<MeshRenderer>()).sharedMaterial = Material(bee);
            return go.transform;
        }

        public static void SetBee(Transform sticker, int bee)
        {
            var r = sticker != null ? sticker.GetComponent<MeshRenderer>() : null;
            if (r != null) r.sharedMaterial = Material(bee);
        }

        /// <summary>A vertex-coloured shape on the card material: an empty comb cell (hexagon ring) or a tally stroke.</summary>
        public static Transform Shape(Transform parent, bool hexRing, Color color, Vector2 size, string name)
        {
            var go = new GameObject(name);
            go.layer = parent != null ? parent.gameObject.layer : 0;
            go.transform.SetParent(parent, false);
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = hexRing ? HexMesh : BarMesh;
            var r = Plain(go.AddComponent<MeshRenderer>());
            r.sharedMaterial = UiTheme.CardMaterial;
            GameMaterials.Tint(r, color); // the mesh is white: the tint is the colour
            return go.transform;
        }

        static MeshRenderer Plain(MeshRenderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return r;
        }

        static Mesh QuadMesh
        {
            get
            {
                if (_quad != null) return _quad;
                _quad = new Mesh { name = "VBS_StickerQuad" };
                _quad.SetVertices(new[] { new Vector3(-0.5f, -0.5f), new Vector3(0.5f, -0.5f), new Vector3(0.5f, 0.5f), new Vector3(-0.5f, 0.5f) });
                _quad.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) });
                _quad.SetColors(new[] { Color.white, Color.white, Color.white, Color.white });
                _quad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
                return _quad;
            }
        }

        static Mesh BarMesh
        {
            get
            {
                if (_bar != null) return _bar;
                _bar = Object.Instantiate(QuadMesh);
                _bar.name = "VBS_StickerBar";
                return _bar;
            }
        }

        /// <summary>A pointy-top hexagon ring, outer radius 0.5, 14% thick.</summary>
        static Mesh HexMesh
        {
            get
            {
                if (_hex != null) return _hex;
                var v = new Vector3[12];
                var c = new Color[12];
                var t = new int[36];
                for (int i = 0; i < 6; i++)
                {
                    float a = (90f + i * 60f) * Mathf.Deg2Rad;
                    var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    v[i * 2] = d * 0.5f;
                    v[i * 2 + 1] = d * 0.43f;
                    c[i * 2] = c[i * 2 + 1] = Color.white;
                    int j = (i + 1) % 6;
                    t[i * 6] = i * 2; t[i * 6 + 1] = j * 2; t[i * 6 + 2] = j * 2 + 1;
                    t[i * 6 + 3] = i * 2; t[i * 6 + 4] = j * 2 + 1; t[i * 6 + 5] = i * 2 + 1;
                }
                _hex = new Mesh { name = "VBS_StickerHex" };
                _hex.SetVertices(v);
                _hex.SetColors(c);
                _hex.SetTriangles(t, 0);
                return _hex;
            }
        }
    }
}
