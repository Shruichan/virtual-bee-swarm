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
            if (File.Exists(path))
            {
                if (Application.isBatchMode) Debug.Log("[VBS] auto scene build: " + path + " exists, nothing to do");
            }
            else
            {
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
    }
}
