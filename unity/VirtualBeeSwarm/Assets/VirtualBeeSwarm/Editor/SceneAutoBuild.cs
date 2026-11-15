using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VirtualBeeSwarm.Editor
{
    /// <summary>
    /// VirtualBeeSwarm.unity is a build output (git-ignored, see BuildGameScene). When the editor opens the project and
    /// the scene file is missing (a fresh clone, or someone deleted it), this builds it once, so the project opens ready
    /// for Play. It never rebuilds a scene that exists: use Virtual Bee Swarm > Build Scene for that.
    ///
    /// Batch mode is left alone by default, because every batch entry point builds what it needs itself
    /// (GameBuild.BuildApk runs the builder first, BuildGameScene.Build is the explicit one). VBS_AUTOBUILD=0 switches
    /// this hook off. VBS_AUTOBUILD=1 turns it on in batch mode too, and a batch run without -executeMethod then exits
    /// once the scene is there (exit code 1 if the build failed), which tests a fresh clone:
    ///   VBS_AUTOBUILD=1 Unity -batchmode -nographics -projectPath P -logFile open.log
    /// </summary>
    [InitializeOnLoad]
    static class SceneAutoBuild
    {
        const string TriedKey = "VBS.SceneAutoBuild.tried";

        static SceneAutoBuild()
        {
            string env = Environment.GetEnvironmentVariable("VBS_AUTOBUILD");
            if (env == "0") return;
            if (Application.isBatchMode && env != "1") return;
            if (SessionState.GetBool(TriedKey, false)) return; // once per editor session, even if it failed
            EditorApplication.delayCall += TryBuild;
        }

        static void TryBuild()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return; // the next domain reload checks again
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryBuild;
                return;
            }

            string path = BuildGameScene.MainScenePath;
            bool ok = true;
            // stale: the game code or data is newer than the scene (someone pulled): rebuild it once, like a missing one
            string stale = File.Exists(path) ? NewerSource(path) : null;
            if (File.Exists(path) && stale == null)
            {
                if (Application.isBatchMode) Debug.Log("[VBS] auto scene build: " + path + " exists, nothing to do");
            }
            else
            {
                if (stale != null) Debug.Log("[VBS] " + path + " is older than " + stale + " (a pull?): rebuilding it");
                SessionState.SetBool(TriedKey, true);
                if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    Debug.LogWarning("[VBS] " + path + " is missing; not built because an open scene has unsaved changes. " +
                                     "Run Virtual Bee Swarm > Build Scene.");
                    return;
                }
                Debug.Log("[VBS] " + path + " is missing (it's a build output, git-ignored): building it now");
                try
                {
                    BuildGameScene.BuildMain();
                    Debug.Log("[VBS] auto scene build done: " + path);
                }
                catch (Exception e)
                {
                    ok = false;
                    Debug.LogError("[VBS] auto scene build FAILED: " + e);
                }
            }

            if (Application.isBatchMode && Array.IndexOf(Environment.GetCommandLineArgs(), "-executeMethod") < 0)
                EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>The first game source (script, tuning json, prefab) under Assets/VirtualBeeSwarm newer than the scene, or null.
        /// Skips Scenes/ and .asset files (the SDKs rewrite settings assets on every open).</summary>
        static string NewerSource(string scenePath)
        {
            try
            {
                var built = File.GetLastWriteTimeUtc(scenePath).AddSeconds(5);
                foreach (var root in new[] { "Assets/VirtualBeeSwarm" })
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (f.EndsWith(".meta") || f.Replace('\\', '/').Contains("/Scenes/")) continue;
                        if (!(f.EndsWith(".cs") || f.EndsWith(".json") || f.EndsWith(".prefab"))) continue;
                        if (File.GetLastWriteTimeUtc(f) > built) return f;
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[VBS] auto scene build: stale check skipped: " + e.Message); }
            return null;
        }
    }
}
