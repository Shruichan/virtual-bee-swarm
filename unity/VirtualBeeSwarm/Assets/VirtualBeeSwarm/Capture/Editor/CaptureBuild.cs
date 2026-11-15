using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.Video;
using UnityEngine.XR.Management;
using VirtualBeeSwarm.Editor;
using Object = UnityEngine.Object;

namespace VirtualBeeSwarm.Capture.Editor
{
    /// <summary>
    /// Builds a silent Windows or Linux player of a generated capture scene: the offline desktop scene + ShowCapture (stand-in room,
    /// POV + wide cameras, PNG frames), the autopilot started by ShowCapture with delivery on.
    ///   Unity -batchmode -nographics -quit -projectPath P -executeMethod VirtualBeeSwarm.Capture.Editor.CaptureBuild.Build
    ///   P/Builds/Capture/vbs-capture.x86_64 -batchmode -force-vulkan  (env VBS_CAPTURE_OUT=dir; needs a GPU + an X display)
    /// Env: CAPTURE_OUT (player folder), CAPTURE_CLIPS (an Assets folder of VP8 .webm window clips named by scene: Linux
    /// players can't decode the H.264 StreamingAssets clips). The scene and the temp material are deleted afterwards.
    /// </summary>
    public static class CaptureBuild
    {
        const string SceneFile = "CaptureShow.unity";
        const string TempMat = "Assets/VBS_CaptureLit.mat";

        public static void Build()
        {
            int code = 1;
            const string audioPath = "ProjectSettings/AudioManager.asset";
            Object audio = null;
            bool audioWasDisabled = false;
            XRGeneralSettings standaloneXr = null;
            bool xrWasEnabled = false;
            string scenePath = null;
            try
            {
                BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
                if (target != BuildTarget.StandaloneLinux64 && target != BuildTarget.StandaloneWindows64)
                    throw new Exception("active build target is " + target + ", expected Linux64 or Windows64");
                scenePath = BuildGameScene.BuildScene(SceneFile, offline: true, withRig: false);
                var scene = EditorSceneManager.OpenScene(scenePath);

                var roomType = Type.GetType("MetaXRAudioRoomAcousticProperties, Meta.XR.Audio");
                if (roomType != null)
                {
                    var room = Object.FindAnyObjectByType(roomType) as Behaviour;
                    if (room == null) room = (Behaviour)new GameObject("Disabled Meta Audio Room").AddComponent(roomType);
                    room.enabled = false;
                }

                var ap = Object.FindAnyObjectByType<GameAutopilot>(FindObjectsInactive.Include);
                if (ap == null) throw new Exception("no GameAutopilot in the generated scene");
                var so = new SerializedObject(ap);
                so.FindProperty("runOnStart").boolValue = false; // ShowCapture starts it once the clipboard is parked
                so.FindProperty("quitWhenDone").boolValue = false;
                so.FindProperty("runs").intValue = 1;
                so.FindProperty("alternateOfflineModes").boolValue = false;
                so.FindProperty("timeScale").floatValue = 1f;
                so.FindProperty("ignoreForeignExceptions").boolValue = true;
                so.FindProperty("requireClips").boolValue = true;
                so.FindProperty("stepTimeoutSeconds").floatValue = 1200f; // real seconds; the capture runs slower than real time
                so.ApplyModifiedPropertiesWithoutUndo();

                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) throw new Exception("URP Lit shader not found");
                AssetDatabase.DeleteAsset(TempMat);
                var mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, TempMat);
                var cap = new GameObject("ShowCapture").AddComponent<ShowCapture>();
                cap.gameObject.AddComponent<RoomPlaytest>();
                var cso = new SerializedObject(cap);
                cso.FindProperty("litTemplate").objectReferenceValue = mat;
                cso.ApplyModifiedPropertiesWithoutUndo();

                string clipsDir = Environment.GetEnvironmentVariable("CAPTURE_CLIPS");
                int clips = 0;
                if (!string.IsNullOrEmpty(clipsDir))
                {
                    var orbisType = Type.GetType("Beeswax.OrbisWindow, Beeswax.Client");
                    var orbis = orbisType != null ? Object.FindAnyObjectByType(orbisType, FindObjectsInactive.Include) : null;
                    if (orbis == null) throw new Exception("no OrbisWindow in the scene");
                    var oso = new SerializedObject(orbis);
                    var list = oso.FindProperty("sceneClips");
                    var found = AssetDatabase.FindAssets("t:VideoClip", new[] { clipsDir })
                        .Select(g => AssetDatabase.LoadAssetAtPath<VideoClip>(AssetDatabase.GUIDToAssetPath(g))).Where(c => c != null).ToArray();
                    list.arraySize = found.Length;
                    for (int i = 0; i < found.Length; i++)
                    {
                        var e = list.GetArrayElementAtIndex(i);
                        e.FindPropertyRelative("scene").stringValue = found[i].name;
                        e.FindPropertyRelative("clip").objectReferenceValue = found[i];
                    }
                    oso.ApplyModifiedPropertiesWithoutUndo();
                    clips = found.Length;
                }
                if (!EditorSceneManager.SaveScene(scene)) throw new Exception("could not save " + scenePath);
                // The first batch run after scripts change can save the scene before Unity has mapped the recompiled
                // ShowCapture class to its script asset; the player then has a missing script and a corrupted level0.
                // Check the scene as saved on disk and stop instead of building a broken player.
                var script = MonoScript.FromMonoBehaviour(cap);
                if (script == null || script.GetClass() != typeof(ShowCapture) || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(script)))
                    throw new Exception("ShowCapture's script asset isn't mapped yet (scripts were still importing). Run the build again.");
                int missing = MissingScripts(scenePath);
                if (missing > 0)
                    throw new Exception(missing + " missing script(s) in the saved capture scene. Run the build again.");
                Debug.Log("[VBSCapture] scene " + scenePath + ", window clips " + clips + ", 0 missing scripts");

                audio = AssetDatabase.LoadAllAssetsAtPath(audioPath).FirstOrDefault();
                var audioSo = audio != null ? new SerializedObject(audio) : null;
                var disable = audioSo?.FindProperty("m_DisableAudio");
                if (disable == null) throw new Exception("cannot switch audio off");
                audioWasDisabled = disable.boolValue;
                disable.boolValue = true;
                audioSo.ApplyModifiedPropertiesWithoutUndo();

                // Capture uses desktop rooms. Native XR startup can crash this player before the scene loads.
                standaloneXr = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
                if (standaloneXr != null)
                {
                    xrWasEnabled = standaloneXr.InitManagerOnStart;
                    SetXrStartup(standaloneXr, false);
                }

                string outDir = Environment.GetEnvironmentVariable("CAPTURE_OUT");
                if (string.IsNullOrEmpty(outDir)) outDir = Path.GetFullPath("Builds/Capture");
                Directory.CreateDirectory(outDir);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scenePath },
                    locationPathName = Path.Combine(outDir, target == BuildTarget.StandaloneWindows64 ? "vbs-capture.exe" : "vbs-capture.x86_64"),
                    target = target,
                    options = Environment.GetEnvironmentVariable("CAPTURE_DEVELOPMENT") == "1"
                        ? BuildOptions.Development : BuildOptions.None,
                });
                Debug.Log("[VBSCapture] build " + report.summary.result + " errors=" + report.summary.totalErrors + " -> " + outDir);
                code = report.summary.result == BuildResult.Succeeded ? 0 : 1;
            }
            catch (Exception e)
            {
                Debug.LogError("[VBSCapture] FAILED: " + e);
                code = 1;
            }
            finally
            {
                if (standaloneXr != null) SetXrStartup(standaloneXr, xrWasEnabled);
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
                AssetDatabase.DeleteAsset(TempMat);
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static void SetXrStartup(XRGeneralSettings settings, bool enabled)
        {
            settings.InitManagerOnStart = enabled;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);
        }

        static int MissingScripts(string path)
        {
            var saved = EditorSceneManager.OpenScene(path);
            int missing = 0;
            foreach (var root in saved.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    missing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
            return missing;
        }
    }
}
