using UnityEditor;
using UnityEngine;

namespace VirtualBeeSwarm.Bloom.Editor
{
    /// <summary>
    /// Batch check for the floor meadow: compiles VirtualBeeSwarm/Meadow for the Quest (Vulkan/Android, every occlusion x
    /// instancing x multiview variant, via ArShaderCheck) and checks Resources/VBS_Meadow. Logs "[VBS][Meadow] check: PASS/FAIL".
    ///   Unity -batchmode -nographics -quit -projectPath P -executeMethod VirtualBeeSwarm.Bloom.Editor.MeadowCheck.Run
    /// </summary>
    public static class MeadowCheck
    {
        public static void Run()
        {
            AssetDatabase.ImportAsset("Assets/VirtualBeeSwarm/Bloom/Meadow.shader", ImportAssetOptions.ForceUpdate);
            bool ok = AR.Editor.ArShaderCheck.Check("VirtualBeeSwarm/Meadow", out _);
            var mat = Resources.Load<Material>(MeadowField.MaterialResource);
            if (mat == null) { ok = false; Debug.LogError("[VBS][Meadow] check: Resources/" + MeadowField.MaterialResource + " missing"); }
            else if (!mat.enableInstancing || mat.shader == null || mat.shader.name != "VirtualBeeSwarm/Meadow") { ok = false; Debug.LogError("[VBS][Meadow] check: material not instanced or wrong shader"); }
            Debug.Log("[VBS][Meadow] check: " + (ok ? "PASS" : "FAIL"));
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
