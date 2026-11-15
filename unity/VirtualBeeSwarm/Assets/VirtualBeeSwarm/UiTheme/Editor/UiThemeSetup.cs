using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace VirtualBeeSwarm.Theme.Editor
{
    /// <summary>
    /// Builds what the theme loads at runtime: the two static TextMeshPro SDF font assets in UiTheme/Resources/UiTheme
    /// (Latin-1 + punctuation, 1024 atlas, each with an always-on-top "Overlay" material) from the TTFs in
    /// UiTheme/Fonts, and the import settings of the sticker bees. To swap a font: replace the TTF (or point the
    /// constants at the logo's) and run the menu item, or in batch mode
    /// -executeMethod VirtualBeeSwarm.Theme.Editor.UiThemeSetup.Build. Needs Assets/TextMesh Pro (TMP Essentials).
    /// </summary>
    public static class UiThemeSetup
    {
        const string Dir = "Assets/VirtualBeeSwarm/UiTheme";
        const string BodyTtf = Dir + "/Fonts/Baloo2-Bold.ttf", DisplayTtf = Dir + "/Fonts/LilitaOne-Regular.ttf";
        const string StickerDir = "Assets/VirtualBeeSwarm/Stickers/Resources/Stickers";

        [MenuItem("Virtual Bee Swarm/UI Theme/Rebuild fonts and stickers")]
        public static void Build()
        {
            AssetDatabase.Refresh();
            if (TMP_Settings.instance == null)
                throw new System.InvalidOperationException("[VBS][UiTheme] TMP Essential Resources are missing (Assets/TextMesh Pro)");
            var fallback = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset");
            var body = BuildFont(BodyTtf, "VBS Body SDF", fallback);
            BuildFont(DisplayTtf, "VBS Display SDF", body);
            ConfigureStickers();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[VBS][UiTheme] setup done");
        }

        static TMP_FontAsset BuildFont(string ttf, string name, TMP_FontAsset fallback)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(ttf);
            if (font == null) throw new System.IO.FileNotFoundException("[VBS][UiTheme] no font at " + ttf);
            if (!AssetDatabase.IsValidFolder(Dir + "/Resources")) AssetDatabase.CreateFolder(Dir, "Resources");
            if (!AssetDatabase.IsValidFolder(Dir + "/Resources/UiTheme")) AssetDatabase.CreateFolder(Dir + "/Resources", "UiTheme");
            string path = Dir + "/Resources/UiTheme/" + name + ".asset";
            string overlayPath = Dir + "/Resources/UiTheme/" + name + " Overlay.mat";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.DeleteAsset(overlayPath);

            var fa = TMP_FontAsset.CreateFontAsset(font, 56, 6, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, false);
            if (fa == null) throw new System.InvalidOperationException("[VBS][UiTheme] could not create a font asset from " + ttf);
            fa.name = name;
            AssetDatabase.CreateAsset(fa, path);
            fa.material.name = name + " Material";
            fa.atlasTexture.name = name + " Atlas";
            AssetDatabase.AddObjectToAsset(fa.material, fa);
            AssetDatabase.AddObjectToAsset(fa.atlasTexture, fa);

            var sb = new StringBuilder();
            for (int c = 32; c <= 126; c++) sb.Append((char)c);
            for (int c = 160; c <= 255; c++) sb.Append((char)c);
            sb.Append("–—‘’“”•…←↑→↓−€™Œœ");
            fa.TryAddCharacters(sb.ToString(), out string missing);
            fa.atlasPopulationMode = AtlasPopulationMode.Static; // nothing is rasterised on the headset; the TTF stays out of the build
            fa.fallbackFontAssetTable = fallback != null ? new List<TMP_FontAsset> { fallback } : new List<TMP_FontAsset>();
            var so = new SerializedObject(fa);
            var clear = so.FindProperty("m_ClearDynamicDataOnBuild");
            if (clear != null) { clear.boolValue = false; so.ApplyModifiedPropertiesWithoutUndo(); }
            EditorUtility.SetDirty(fa);
            EditorUtility.SetDirty(fa.material);
            EditorUtility.SetDirty(fa.atlasTexture);

            var overlayShader = Shader.Find("TextMeshPro/Mobile/Distance Field Overlay");
            if (overlayShader == null) throw new System.InvalidOperationException("[VBS][UiTheme] the TMP mobile overlay shader is missing");
            var overlay = new Material(fa.material) { name = name + " Overlay", shader = overlayShader };
            AssetDatabase.CreateAsset(overlay, overlayPath);
            AssetDatabase.SaveAssets();
            Debug.Log("[VBS][UiTheme] font " + name + ": " + fa.characterTable.Count + " characters from " + ttf + ", atlas " + fa.atlasWidth + "x" + fa.atlasHeight +
                      (string.IsNullOrEmpty(missing) ? "" : ", not in the font: " + missing) + ", shader " + fa.material.shader.name);
            return fa;
        }

        static void ConfigureStickers()
        {
            int n = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { StickerDir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) continue;
                imp.textureType = TextureImporterType.Default;
                imp.sRGBTexture = true;
                imp.alphaSource = TextureImporterAlphaSource.FromInput;
                imp.alphaIsTransparency = true;
                imp.mipmapEnabled = true;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.filterMode = FilterMode.Trilinear;
                imp.maxTextureSize = 512;
                imp.npotScale = TextureImporterNPOTScale.None;
                imp.textureCompression = TextureImporterCompression.Compressed;
                var android = imp.GetPlatformTextureSettings("Android");
                android.overridden = true;
                android.maxTextureSize = 512;
                android.format = TextureImporterFormat.ASTC_6x6;
                imp.SetPlatformTextureSettings(android);
                imp.SaveAndReimport();
                n++;
            }
            Debug.Log("[VBS][UiTheme] stickers: " + n + " texture(s) set to alpha + mips + ASTC 6x6");
        }
    }
}
