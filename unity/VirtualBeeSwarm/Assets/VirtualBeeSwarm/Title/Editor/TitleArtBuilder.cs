using System.IO;
using UnityEditor;
using UnityEngine;

namespace VirtualBeeSwarm.Title.Editor
{
    /// <summary>
    /// Keeps Art/Logo ready for the title scene: import settings for the logo PNGs (transparent, mipmapped, clamped) and
    /// Art/Logo/Resources/TitleArt.asset pointing at them and at the flower and grass prefabs. Runs when the editor
    /// loads and changes nothing once the asset is complete.
    ///   Unity -batchmode -quit -projectPath P -executeMethod VirtualBeeSwarm.Title.Editor.TitleArtBuilder.Build
    /// </summary>
    public class TitleArtBuilder : AssetPostprocessor
    {
        public const string LogoFolder = "Assets/VirtualBeeSwarm/Art/Logo";
        public const string AssetPath = LogoFolder + "/Resources/TitleArt.asset";
        public const string AppIcon = LogoFolder + "/app-icon.png";
        static readonly string[] Stickers = { "sticker-bee-1", "sticker-bee-5", "sticker-bee-3", "sticker-bee-4" };
        static readonly int[] Facing = { -1, 1, 1, -1 };

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(LogoFolder + "/")) return;
            var ti = (TextureImporter)assetImporter;
            bool icon = assetPath == AppIcon;
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.alphaIsTransparency = !icon;
            ti.alphaSource = icon ? TextureImporterAlphaSource.None : TextureImporterAlphaSource.FromInput;
            ti.mipmapEnabled = !icon;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 4;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.maxTextureSize = 2048;
            ti.textureCompression = icon ? TextureImporterCompression.Uncompressed : TextureImporterCompression.CompressedHQ;
        }

        [InitializeOnLoadMethod]
        static void OnLoad() => EditorApplication.delayCall += () => Ensure(false);

        public static void Build() => Ensure(true);

        static void Ensure(bool log)
        {
            if (!AssetDatabase.IsValidFolder(LogoFolder)) return;
            var art = AssetDatabase.LoadAssetAtPath<TitleArt>(AssetPath);
            bool created = art == null;
            if (created)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
                AssetDatabase.Refresh();
                art = ScriptableObject.CreateInstance<TitleArt>();
                AssetDatabase.CreateAsset(art, AssetPath);
            }
            bool changed = created;
            changed |= Set(ref art.plate, Tex("logo-plate"));
            changed |= Set(ref art.wordmark, Tex("logo-wordmark"));
            changed |= Set(ref art.flat, Tex("logo-2048"));
            if (art.stickerBees == null || art.stickerBees.Length != Stickers.Length) { art.stickerBees = new Texture2D[Stickers.Length]; changed = true; }
            if (art.stickerFacing == null || art.stickerFacing.Length != Stickers.Length) { art.stickerFacing = new int[Stickers.Length]; changed = true; }
            for (int i = 0; i < Stickers.Length; i++)
            {
                changed |= Set(ref art.stickerBees[i], Tex(Stickers[i]));
                if (art.stickerFacing[i] != Facing[i]) { art.stickerFacing[i] = Facing[i]; changed = true; }
            }
            changed |= Set(ref art.flowers, AssetDatabase.LoadAssetAtPath<GameObject>("Assets/VirtualBeeSwarm/Art/Flowers/Flowers.prefab"));
            changed |= Set(ref art.grass, AssetDatabase.LoadAssetAtPath<GameObject>("Assets/VirtualBeeSwarm/Art/GrassTuft/GrassTuft.prefab"));
            if (changed)
            {
                EditorUtility.SetDirty(art);
                AssetDatabase.SaveAssets();
            }
            if (log || changed)
                Debug.Log("[VBS][Title] " + AssetPath + (created ? " created" : changed ? " updated" : " up to date") + ": plate " + (art.plate != null) +
                          ", wordmark " + (art.wordmark != null) + ", flowers " + (art.flowers != null) + ", grass " + (art.grass != null));
        }

        static Texture2D Tex(string name) => AssetDatabase.LoadAssetAtPath<Texture2D>(LogoFolder + "/" + name + ".png");

        static bool Set<T>(ref T field, T value) where T : Object
        {
            if (value == null || field == value) return false;
            field = value;
            return true;
        }
    }
}
