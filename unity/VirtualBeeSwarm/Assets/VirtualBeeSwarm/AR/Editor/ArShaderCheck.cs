using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm.AR.Editor
{
    /// <summary>
    /// Compiles VirtualBeeSwarm/Unlit for the Quest (Vulkan, Android) in every occlusion variant without building an
    /// APK: keywords {none, HARD_OCCLUSION, SOFT_OCCLUSION} x {plain, INSTANCING_ON} x {mono, STEREO_MULTIVIEW_ON},
    /// vertex + fragment. Logs "[VBS][AR] shader check: PASS/FAIL". Works on any active build target.
    ///   Unity -batchmode -nographics -quit -projectPath P -executeMethod VirtualBeeSwarm.AR.Editor.ArShaderCheck.Run
    /// Exit code 0 = every variant compiled.
    /// </summary>
    public static class ArShaderCheck
    {
        [MenuItem("Virtual Bee Swarm/AR/Check occlusion shader variants (Vulkan)")]
        static void Menu() => Check(GameMaterials.ShaderName, out _);

        public static void Run()
        {
            bool ok = Check(GameMaterials.ShaderName, out _);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// Compiles the PLAYER scripts (not the editor's) for the active build target into Temp/, e.g. Android IL with
        /// UNITY_ANDROID and without UNITY_EDITOR, so #if UNITY_ANDROID &amp;&amp; !UNITY_EDITOR code is checked without an APK.
        ///   Unity -batchmode -nographics -quit -projectPath P -executeMethod VirtualBeeSwarm.AR.Editor.ArShaderCheck.CompilePlayer
        /// </summary>
        public static void CompilePlayer()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var group = BuildPipeline.GetBuildTargetGroup(target);
            var settings = new UnityEditor.Build.Player.ScriptCompilationSettings { target = target, group = group };
            string outDir = "Temp/ArPlayerScripts-" + target;
            var result = UnityEditor.Build.Player.PlayerBuildInterface.CompilePlayerScripts(settings, outDir);
            IEnumerable<string> asms = result.assemblies != null ? (IEnumerable<string>)result.assemblies : Array.Empty<string>();
            bool ok = asms.Contains("VirtualBeeSwarm.dll") && asms.Contains("VirtualBeeSwarm.AR.dll");
            Debug.Log("[VBS][AR] player script compile for " + target + ": " + (ok ? "PASS" : "FAIL") + ", " + asms.Count() +
                      " assemblies: " + string.Join(", ", asms.Where(a => a.StartsWith("VirtualBeeSwarm") || a.StartsWith("Beeswax"))));
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>Compiles the variants; `hashes` maps "keywords|stage" to an FNV hash of the compiled bytes.</summary>
        public static bool Check(string shaderName, out Dictionary<string, string> hashes)
        {
            hashes = new Dictionary<string, string>();
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError("[VBS][AR] shader check: FAIL, shader " + shaderName + " not found");
                return false;
            }
            bool hasOcclusion = shader.FindPropertyIndex("_Occlude") >= 0;
            var data = ShaderUtil.GetShaderData(shader);
            var pass = data.GetSubshader(0).GetPass(0);
            string[] occ = hasOcclusion ? new[] { "", "HARD_OCCLUSION", "SOFT_OCCLUSION" } : new[] { "" };
            string[] inst = { "", "INSTANCING_ON" };
            string[] stereo = { "", "STEREO_MULTIVIEW_ON" };
            int compiled = 0, failed = 0;
            foreach (var o in occ)
            foreach (var i in inst)
            foreach (var s in stereo)
            foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
            {
                var kw = new[] { o, i, s }.Where(k => k.Length > 0).ToArray();
                var r = pass.CompileVariant(stage, kw, ShaderCompilerPlatform.Vulkan, BuildTarget.Android);
                string key = (kw.Length == 0 ? "-" : string.Join("+", kw)) + "|" + stage;
                var errors = r.Messages == null ? new ShaderMessage[0] : r.Messages.Where(m => m.severity == ShaderCompilerMessageSeverity.Error).ToArray();
                if (!r.Success || errors.Length > 0)
                {
                    failed++;
                    foreach (var m in errors) Debug.LogError("[VBS][AR] shader check: " + key + ": " + m.message + " (" + m.file + ":" + m.line + ")");
                    if (errors.Length == 0) Debug.LogError("[VBS][AR] shader check: " + key + " failed with no message");
                    continue;
                }
                compiled++;
                hashes[key] = Fnv(r.ShaderData) + " (" + (r.ShaderData?.Length ?? 0) + " B)";
            }
            bool ok = failed == 0;
            Debug.Log("[VBS][AR] shader check: " + (ok ? "PASS" : "FAIL") + ": " + shaderName + " " + compiled + " variant stage(s) compiled for Vulkan/Android, " +
                      failed + " failed; occlusion edit " + (hasOcclusion ? "present" : "NOT present (only the keyword-off variants were compiled)"));
            foreach (var kv in hashes.OrderBy(k => k.Key)) Debug.Log("[VBS][AR] shader check: " + kv.Key + " " + kv.Value);
            return ok;
        }

        static string Fnv(byte[] b)
        {
            if (b == null) return "null";
            uint h = 2166136261;
            for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 16777619; }
            return h.ToString("x8");
        }
    }
}
