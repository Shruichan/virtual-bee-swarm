using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VirtualBeeSwarm.Editor;

namespace VirtualBeeSwarm.Tests
{
    /// <summary>
    /// The smoke test without the Test Runner (for machines where -runTests is blocked, e.g. by licensing):
    ///   Unity -batchmode -nographics -projectPath P -executeMethod VirtualBeeSwarm.Tests.GameSmokeRunner.Run -logFile run.log
    /// Builds OfflineTest.unity, enters play mode (no domain reload), plays a whole run with the Hive
    /// unreachable, resets, plays it again forced offline, and exits 0 on success / 1 on any assert, exception or
    /// error log from game or client code. Never touches the network or the mic; listener volume 0.
    /// Not failures (counted and listed in the result): errors with no VirtualBeeSwarm/Beeswax frame in the stack (the Meta
    /// SDK's native plugins don't exist on a Linux editor, so OVRPlugin/MRUK/Meta XR Audio throw every frame there), and
    /// "VideoPlayer cannot play url" for a window fallback clip that isn't in StreamingAssets yet, or (Linux editor only)
    /// for an .mp4 one that is: Unity on Linux has no H.264 decoder, the Quest has (the window falls back to its tint).
    /// The editor's Enter Play Mode settings are restored afterwards (they're saved in ProjectSettings/EditorSettings.asset).
    /// </summary>
    public static class GameSmokeRunner
    {
        static readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
        static readonly List<string> Errors = new List<string>();
        static readonly SortedSet<string> Foreign = new SortedSet<string>();
        static readonly SortedSet<string> MissingClips = new SortedSet<string>();
        static readonly SortedSet<string> UnplayableClips = new SortedSet<string>();
        static int _foreignCount;
        static bool _running;
        static double _deadline;
        static bool _savedOptions;
        static bool _prevOptionsEnabled;
        static EnterPlayModeOptions _prevOptions;

        public static void Run()
        {
            try
            {
                Application.logMessageReceived += OnLog;
                string path = BuildGameScene.BuildScene("OfflineTest.unity", offline: true);
                EditorSceneManager.OpenScene(path);
                _prevOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
                _prevOptions = EditorSettings.enterPlayModeOptions;
                _savedOptions = true;
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
                EditorApplication.playModeStateChanged += OnPlayMode;
                _deadline = EditorApplication.timeSinceStartup + 600;
                EditorApplication.update += Tick;
                Debug.Log("[VBSTest] entering play mode");
                EditorApplication.EnterPlaymode();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Finish(false);
            }
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                if (condition.StartsWith("[Licensing", StringComparison.Ordinal)) return; // editor licensing noise
                // Editor-only: the Meta SDK writes StreamingAssets/RuntimeActionBindings.json at play start, the asset
                // refresh that follows re-uploads textures and Unity logs this natively (no managed stack). A built player
                // never refreshes assets; game code calling Apply() on an empty texture would have a stack and still count.
                if (string.IsNullOrEmpty(stackTrace) && condition == "No texture data is available to upload.")
                {
                    _foreignCount++;
                    if (Foreign.Count < 8) Foreign.Add(type + ": " + condition + " (editor asset refresh)");
                    return;
                }
                if (!FromOurCode(stackTrace))
                {
                    _foreignCount++;
                    if (Foreign.Count < 8) Foreign.Add(type + ": " + (condition.Length > 120 ? condition.Substring(0, 120) : condition));
                    return;
                }
                const string video = "VideoPlayer cannot play url : ";
                if (condition.StartsWith(video, StringComparison.Ordinal))
                {
                    string file = condition.Substring(video.Length).Trim();
                    if (!File.Exists(file)) { MissingClips.Add(Path.GetFileName(file)); return; }
                    if (Application.platform == RuntimePlatform.LinuxEditor &&
                        file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) { UnplayableClips.Add(Path.GetFileName(file)); return; }
                }
                Errors.Add(type + ": " + condition);
            }
        }

        /// <summary>True if any stack frame is in the VirtualBeeSwarm or Beeswax namespaces (no stack: can't tell, count it).</summary>
        static bool FromOurCode(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return true;
            foreach (var raw in stack.Split('\n'))
            {
                var line = raw.TrimStart();
                if (line.StartsWith("at ")) line = line.Substring(3);
                if (line.StartsWith("VirtualBeeSwarm.") || line.StartsWith("Beeswax.")) return true;
            }
            return false;
        }

        static void OnPlayMode(PlayModeStateChange s)
        {
            if (s != PlayModeStateChange.EnteredPlayMode || _running) return;
            _running = true;
            Stack.Clear();
            Stack.Push(Sequence());
        }

        static IEnumerator Sequence()
        {
            yield return GameSmokeTests.PlayRun(forceOffline: false);
            yield return GameSmokeTests.PlayRun(forceOffline: true);
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup > _deadline)
            {
                Errors.Add("timed out");
                Finish(false);
                return;
            }
            if (!_running) return;
            try
            {
                while (Stack.Count > 0)
                {
                    var top = Stack.Peek();
                    if (!top.MoveNext())
                    {
                        Stack.Pop();
                        continue;
                    }
                    if (top.Current is IEnumerator nested) { Stack.Push(nested); continue; }
                    return; // yield return null (or anything else): next tick
                }
                Finish(Errors.Count == 0);
            }
            catch (Exception e)
            {
                Errors.Add("assert/exception: " + e.Message);
                Debug.LogException(e);
                Finish(false);
            }
        }

        static void Finish(bool ok)
        {
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            Application.logMessageReceived -= OnLog;
            _running = false;
            Time.timeScale = 1f;
            if (_savedOptions)
            {
                EditorSettings.enterPlayModeOptionsEnabled = _prevOptionsEnabled;
                EditorSettings.enterPlayModeOptions = _prevOptions;
                _savedOptions = false;
            }
            foreach (var e in Errors) Debug.Log("[VBSTest] ERROR " + e);
            if (_foreignCount > 0)
                Debug.Log("[VBSTest] ignored " + _foreignCount + " error(s) outside game code: " + string.Join(" | ", Foreign));
            if (MissingClips.Count > 0)
                Debug.Log("[VBSTest] window fallback clips not in StreamingAssets yet: " + string.Join(", ", MissingClips));
            if (UnplayableClips.Count > 0)
                Debug.Log("[VBSTest] window clips found but not playable in a Linux editor (no H.264; fine on Quest): " +
                          string.Join(", ", UnplayableClips));
            Debug.Log("[VBSTest] RESULT " + (ok && Errors.Count == 0 ? "PASS" : "FAIL (" + Errors.Count + " errors)"));
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
            if (Application.isBatchMode) EditorApplication.Exit(ok && Errors.Count == 0 ? 0 : 1);
        }
    }
}
