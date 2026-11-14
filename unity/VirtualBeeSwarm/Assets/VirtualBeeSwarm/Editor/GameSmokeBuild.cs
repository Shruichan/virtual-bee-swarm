using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VirtualBeeSwarm.Editor
{
    /// <summary>
    /// Headless smoke test: builds a desktop player of a generated test scene (no XR rig, no mic) in which
    /// GameAutopilot plays the whole show and quits with exit code 0 = PASS. Run it, then run the player:
    ///
    ///   Unity -batchmode -nographics -quit -projectPath P -executeMethod VirtualBeeSwarm.Editor.GameSmokeBuild.Build -logFile smoke-build.log
    ///   xvfb-run -a P/Builds/Smoke/vbs-smoke.x86_64 -batchmode -nographics -logFile smoke-run.log; echo "exit $?"
    ///
    /// Environment (all optional):
    ///   SMOKE_ONLINE=1          online run against the Hive (HumDetector + BeeswaxVoiceInput removed, so no mic);
    ///                           the token comes from Resources/beeswax_token.txt or the HIVE_TOKEN env var at run time
    ///   SMOKE_HOSTS=a,b         bus hosts for the online run (default wss://beeswax.drori.dev)
    ///   SMOKE_RUNS / SMOKE_SCALE   runs (default 2 offline / 1 online) and time scale (default 4 offline / 1 online)
    ///   SMOKE_IDLE_STATION=0..3 leave that station to the max-duration wrap-up; SMOKE_WHATIF_HANDS_OFF=1 lets the offline
    ///                           what-if timers start/put out the fire
    ///   SMOKE_OUT=dir           player folder (default Builds/Smoke in the project, git-ignored)
    ///   SMOKE_ALLOW_SWITCH=1    allow building when the active target isn't a desktop one (switching = full reimport)
    ///   SMOKE_REQUIRE_CLIPS=1   fail if Beeswax starts a line without a pre-rendered clip, or one the intro didn't preload
    ///                           (S8.3). Without it the autopilot only logs "[VBSAutopilot] voice: ..." counts.
    /// Audio is switched off in Project Settings for the duration of the build (restored afterwards), so the player
    /// is silent. The generated scene is deleted after the build.
    /// </summary>
    public static class GameSmokeBuild
    {
        const string SmokeScene = "SmokeTest.unity";

        public static void Build()
        {
            int code = 1;
            string audioPath = "ProjectSettings/AudioManager.asset";
            Object audio = null;
            bool audioWasDisabled = false;
            string scenePath = null;
            try
            {
                var target = DesktopTarget();
                var active = EditorUserBuildSettings.activeBuildTarget;
                if (active != target && Environment.GetEnvironmentVariable("SMOKE_ALLOW_SWITCH") != "1")
                    throw new Exception("active build target is " + active + "; a " + target + " smoke build would switch it " +
                                        "(full reimport). Set SMOKE_ALLOW_SWITCH=1 to do it anyway, or use the editor (F2).");

                bool online = Environment.GetEnvironmentVariable("SMOKE_ONLINE") == "1";
                scenePath = BuildGameScene.BuildScene(SmokeScene, offline: !online, withRig: false);
                var scene = EditorSceneManager.OpenScene(scenePath);

                if (online)
                {
                    RemoveComponent("Beeswax.HumDetector, Beeswax.Client");
                    RemoveComponent("Beeswax.BeeswaxVoiceInput, Beeswax.Client");
                    string hosts = Environment.GetEnvironmentVariable("SMOKE_HOSTS");
                    if (string.IsNullOrEmpty(hosts)) hosts = "wss://beeswax.drori.dev";
                    SetBusHosts(hosts.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(h => h.Trim()).ToArray());
                }

                var ap = Object.FindAnyObjectByType<GameAutopilot>(FindObjectsInactive.Include);
                if (ap == null) throw new Exception("no GameAutopilot in the generated scene");
                var so = new SerializedObject(ap);
                int runs = int.TryParse(Environment.GetEnvironmentVariable("SMOKE_RUNS"), out var r) ? r : (online ? 1 : 2);
                float scale = float.TryParse(Environment.GetEnvironmentVariable("SMOKE_SCALE"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var sc) ? sc : (online ? 1f : 4f);
                int idle = int.TryParse(Environment.GetEnvironmentVariable("SMOKE_IDLE_STATION"), out var ist) ? ist : -1;
                bool handsOff = Environment.GetEnvironmentVariable("SMOKE_WHATIF_HANDS_OFF") == "1";
                bool requireClips = Environment.GetEnvironmentVariable("SMOKE_REQUIRE_CLIPS") == "1";
                so.FindProperty("runOnStart").boolValue = true;
                so.FindProperty("quitWhenDone").boolValue = true;
                so.FindProperty("runs").intValue = runs;
                so.FindProperty("alternateOfflineModes").boolValue = !online; // offline: run 2 = forced offline
                so.FindProperty("timeScale").floatValue = scale;
                so.FindProperty("idleStation").intValue = idle;
                so.FindProperty("whatIfHandsOff").boolValue = handsOff;
                so.FindProperty("waitForOnlineSeconds").floatValue = online ? 25f : 0f;
                so.FindProperty("ignoreForeignExceptions").boolValue = true; // Meta natives don't exist in a desktop player
                so.FindProperty("requireClips").boolValue = requireClips;
                so.ApplyModifiedPropertiesWithoutUndo();
                if (!EditorSceneManager.SaveScene(scene)) throw new Exception("could not save " + scenePath);
                Debug.Log("[VBSSmoke] online=" + online + " runs=" + runs + " scale=" + scale + " idleStation=" + idle +
                          " whatIfHandsOff=" + handsOff + " requireClips=" + requireClips + " target=" + target);

                // Silent player: disable audio for this build only.
                audio = AssetDatabase.LoadAllAssetsAtPath(audioPath).FirstOrDefault();
                var audioSo = audio != null ? new SerializedObject(audio) : null;
                var disable = audioSo?.FindProperty("m_DisableAudio");
                if (disable == null) throw new Exception("cannot switch audio off (AudioManager.m_DisableAudio not found)");
                audioWasDisabled = disable.boolValue;
                disable.boolValue = true;
                audioSo.ApplyModifiedPropertiesWithoutUndo();

                string outDir = Environment.GetEnvironmentVariable("SMOKE_OUT");
                if (string.IsNullOrEmpty(outDir)) outDir = Path.GetFullPath("Builds/Smoke");
                Directory.CreateDirectory(outDir);
                string exe = target == BuildTarget.StandaloneWindows64 ? "vbs-smoke.exe"
                    : target == BuildTarget.StandaloneOSX ? "vbs-smoke.app" : "vbs-smoke.x86_64";
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scenePath },
                    locationPathName = Path.Combine(outDir, exe),
                    target = target,
                    options = BuildOptions.Development,
                });
                Debug.Log("[VBSSmoke] build " + report.summary.result + " errors=" + report.summary.totalErrors +
                          " -> " + Path.Combine(outDir, exe));
                code = report.summary.result == BuildResult.Succeeded ? 0 : 1;
            }
            catch (Exception e)
            {
                Debug.LogError("[VBSSmoke] FAILED: " + e);
                code = 1;
            }
            finally
            {
                if (audio != null)
                {
                    var audioSo = new SerializedObject(audio);
                    audioSo.FindProperty("m_DisableAudio").boolValue = audioWasDisabled;
                    audioSo.ApplyModifiedPropertiesWithoutUndo();
                    AssetDatabase.SaveAssets();
                }
                if (scenePath != null)
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    AssetDatabase.DeleteAsset(scenePath);
                }
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static BuildTarget DesktopTarget()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor: return BuildTarget.StandaloneWindows64;
                case RuntimePlatform.OSXEditor: return BuildTarget.StandaloneOSX;
                default: return BuildTarget.StandaloneLinux64;
            }
        }

        static void RemoveComponent(string typeName)
        {
            var t = Type.GetType(typeName);
            if (t == null) return;
            foreach (var c in Object.FindObjectsByType(t, FindObjectsInactive.Include, FindObjectsSortMode.None))
                Object.DestroyImmediate(c);
        }

        static void SetBusHosts(string[] hosts)
        {
            var t = Type.GetType("Beeswax.BeeswaxBus, Beeswax.Client");
            var bus = t != null ? Object.FindAnyObjectByType(t, FindObjectsInactive.Include) : null;
            if (bus == null) throw new Exception("no BeeswaxBus in the online smoke scene");
            var so = new SerializedObject(bus);
            var p = so.FindProperty("hosts");
            if (p == null || !p.isArray) throw new Exception("BeeswaxBus.hosts not found (client API changed?)");
            p.arraySize = hosts.Length;
            for (int i = 0; i < hosts.Length; i++) p.GetArrayElementAtIndex(i).stringValue = hosts[i];
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
