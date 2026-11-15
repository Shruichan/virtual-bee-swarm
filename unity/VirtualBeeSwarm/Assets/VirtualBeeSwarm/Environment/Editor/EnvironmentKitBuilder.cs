using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VirtualBeeSwarm.Editor;

namespace VirtualBeeSwarm.EnvironmentArt.Editor
{
    /// <summary>
    /// Art/Environment/environment.fbx + environment_layout.json (art/blender/scripts/environment.py --out DIR, copied
    /// over Art/Environment/) -> the four materials next to them and Environment/Resources/VBS_EnvironmentKit.asset,
    /// which EnvironmentDirector loads at runtime. Run it after a new export:
    ///   Menu: Virtual Bee Swarm > Art > Rebuild environment kit
    ///   Batch: Unity -batchmode -nographics -quit -projectPath P
    ///          -executeMethod VirtualBeeSwarm.EnvironmentArt.Editor.EnvironmentKitBuilder.BuildBatch
    /// </summary>
    public static class EnvironmentKitBuilder
    {
        public static string ArtDir => ArtSlots.ArtRoot + "/Environment";
        static string KitDir => ArtSlots.VbsRoot + "/Environment/Resources";

        [MenuItem("Virtual Bee Swarm/Art/Rebuild environment kit")]
        static void Menu() => Debug.Log("[VBS][Env] " + Build());

        public static void BuildBatch()
        {
            int code = 0;
            try { Debug.Log("[VBS][Env] " + Build()); }
            catch (Exception e) { Debug.LogError("[VBS][Env] kit build FAILED: " + e); code = 1; }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        public static string Build()
        {
            string fbx = ArtDir + "/environment.fbx";
            if (!(AssetImporter.GetAtPath(fbx) is ModelImporter mi)) throw new Exception("no model at " + fbx);
            ArtImport.ApplySettings(mi);
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.isReadable = true; // the petal is a particle mesh
            mi.SaveAndReimport();
            var meshes = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Mesh>().ToDictionary(m => m.name);
            Mesh Get(string n) => meshes.TryGetValue(n, out var m) ? m : throw new Exception(fbx + " has no mesh " + n);

            var ground = Mat("VBS_EnvGround", GameMaterials.Blend.Opaque, occlude: true, bias: 0.06f, cullOff: false);
            var vc = Mat("VBS_EnvVC", GameMaterials.Blend.Opaque, occlude: true, bias: 0f, cullOff: false);
            var glow = Mat("VBS_EnvGlow", GameMaterials.Blend.Additive, occlude: true, bias: 0f, cullOff: true);
            var stars = Mat("VBS_EnvStars", GameMaterials.Blend.Additive, occlude: false, bias: 0f, cullOff: true);
            var layout = AssetDatabase.LoadAssetAtPath<TextAsset>(ArtDir + "/environment_layout.json");
            if (layout == null) throw new Exception("no " + ArtDir + "/environment_layout.json");

            EnsureFolder(KitDir);
            string kitPath = KitDir + "/" + EnvironmentKit.ResourceName + ".asset";
            var kit = AssetDatabase.LoadAssetAtPath<EnvironmentKit>(kitPath);
            bool isNew = kit == null;
            if (isNew) kit = ScriptableObject.CreateInstance<EnvironmentKit>();
            kit.island = Get("Island");
            kit.moonGround = Get("MoonGround");
            kit.portalVines = Get("PortalVines");
            kit.shafts = Get("Shafts");
            kit.starsA = Get("StarsA");
            kit.starsB = Get("StarsB");
            kit.earthGlow = Get("EarthGlow");
            kit.petal = Get("Petal");
            kit.blooms = meshes.Values.Where(m => m.name.StartsWith("Bloom_")).OrderBy(m => m.name).ToArray();
            kit.ground = ground;
            kit.vc = vc;
            kit.glow = glow;
            kit.stars = stars;
            kit.layout = layout;
            if (isNew) AssetDatabase.CreateAsset(kit, kitPath);
            else EditorUtility.SetDirty(kit);
            AssetDatabase.SaveAssets();
            int tris = meshes.Values.Sum(m => (int)(m.GetIndexCount(0) / 3));
            return "environment kit " + kitPath + ": " + meshes.Count + " meshes (" + tris + " tris), " + kit.blooms.Length +
                   " flower kinds, " + kit.BloomSpots().Length + " flower spots";
        }

        static Material Mat(string name, GameMaterials.Blend blend, bool occlude, float bias, bool cullOff)
        {
            string path = ArtDir + "/" + name + ".mat";
            var fresh = GameMaterials.Create(name, Color.white, blend, vertexColors: true, instancing: true, doubleSided: cullOff);
            if (fresh.HasProperty("_Occlude")) fresh.SetFloat("_Occlude", occlude ? 1f : 0f);
            if (fresh.HasProperty("_EnvironmentDepthBias")) fresh.SetFloat("_EnvironmentDepthBias", bias);
            if (blend == GameMaterials.Blend.Additive) fresh.renderQueue = (int)RenderQueue.Transparent + 20;
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                AssetDatabase.CreateAsset(fresh, path);
                return fresh;
            }
            mat.shader = fresh.shader;
            mat.CopyPropertiesFromMaterial(fresh);
            mat.renderQueue = fresh.renderQueue;
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
