#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VirtualBeeSwarm.FlowerLife
{
    /// <summary>
    /// Editor only: bakes Daniel's flower and grass models (Art/Flowers, Art/GrassTuft; palette-textured) into the
    /// layout FlowerLife.shader draws (vertex colour = the palette colour, vertex alpha = sway weight, pivot at the stem
    /// base) as FlowerLife/Resources/fl_art_*.asset. Run again when the models change:
    ///   Unity -batchmode -quit -executeMethod VirtualBeeSwarm.FlowerLife.FlowerLifeArtBake.Bake
    /// The art files themselves are not touched.
    /// </summary>
    public static class FlowerLifeArtBake
    {
        const string Art = "Assets/VirtualBeeSwarm/Art/";
        const string Out = "Assets/VirtualBeeSwarm/FlowerLife/Resources/";

        [MenuItem("VBS/FlowerLife/Bake Daniel's flowers")]
        public static void Bake()
        {
            var palette = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            string png = Art + "_Shared/VBS_Palette.png";
            if (!File.Exists(png) || !palette.LoadImage(File.ReadAllBytes(png)))
            {
                Debug.LogError("[VBS][FlowerLife] bake: no palette at " + png);
                return;
            }
            int n = 0;
            n += One(Art + "Flowers/Flowers.prefab", "Flower_Purple", "fl_art_daisy", palette);
            n += One(Art + "Flowers/Flowers.prefab", "Flower_Blue", "fl_art_bluebell", palette);
            n += One(Art + "Flowers/Flowers.prefab", "Flower_Lavender", "fl_art_lavender", palette);
            n += One(Art + "GrassTuft/GrassTuft.prefab", "GrassTuft", "fl_art_grass", palette);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[VBS][FlowerLife] bake: " + n + " of 4 art meshes baked into " + Out);
        }

        static int One(string prefabPath, string node, string outName, Texture2D palette)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) { Debug.LogWarning("[VBS][FlowerLife] bake: no " + prefabPath); return 0; }
            MeshFilter mf = null;
            foreach (var f in prefab.GetComponentsInChildren<MeshFilter>(true))
                if (f.name == node && f.sharedMesh != null) { mf = f; break; }
            if (mf == null) { Debug.LogWarning("[VBS][FlowerLife] bake: no mesh " + node + " in " + prefabPath); return 0; }
            var src = mf.sharedMesh;
            var rot = Matrix4x4.TRS(Vector3.zero, mf.transform.localRotation, mf.transform.localScale);
            var sv = src.vertices;
            var sn = src.normals;
            var su = src.uv;
            var v = new Vector3[sv.Length];
            var b = new Bounds();
            for (int i = 0; i < sv.Length; i++)
            {
                v[i] = rot.MultiplyPoint3x4(sv[i]);
                if (i == 0) b = new Bounds(v[i], Vector3.zero); else b.Encapsulate(v[i]);
            }
            // pivot: the stem base (the lowest 6% of the mesh), on the ground
            Vector3 foot = Vector3.zero;
            int feet = 0;
            float low = b.min.y + b.size.y * 0.06f;
            for (int i = 0; i < v.Length; i++)
                if (v[i].y <= low) { foot += v[i]; feet++; }
            foot = feet > 0 ? foot / feet : b.center;
            foot.y = b.min.y;
            float height = Mathf.Max(1e-4f, b.size.y);
            bool linear = PlayerSettings.colorSpace == ColorSpace.Linear;
            var c = new Color[v.Length];
            var nrm = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                v[i] -= foot;
                Vector2 uv = i < su.Length ? su[i] : Vector2.zero;
                Color px = palette.GetPixel(Mathf.Clamp((int)(Mathf.Repeat(uv.x, 1f) * palette.width), 0, palette.width - 1),
                                            Mathf.Clamp((int)(Mathf.Repeat(uv.y, 1f) * palette.height), 0, palette.height - 1));
                if (linear) px = px.linear;
                float t = Mathf.Clamp01(v[i].y / height);
                px.a = Mathf.Pow(t, 1.5f);
                c[i] = px;
                nrm[i] = i < sn.Length ? rot.MultiplyVector(sn[i]).normalized : Vector3.up;
            }
            var tris = new List<int>(src.triangles.Length);
            for (int s = 0; s < src.subMeshCount; s++) tris.AddRange(src.GetTriangles(s));
            var m = new Mesh { name = outName };
            m.vertices = v;
            m.normals = nrm;
            m.colors = c;
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            Directory.CreateDirectory(Out);
            string path = Out + outName + ".asset";
            var old = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (old != null)
            {
                old.Clear();
                EditorUtility.CopySerialized(m, old); // keeps the guid
                EditorUtility.SetDirty(old);
            }
            else AssetDatabase.CreateAsset(m, path);
            Debug.Log("[VBS][FlowerLife] bake: " + node + " -> " + path + " (" + v.Length + " verts, " + tris.Count / 3 + " tris, " +
                      height.ToString("0.000") + " m tall)");
            return 1;
        }
    }
}
#endif
