using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VirtualBeeSwarm.Editor
{
    /// <summary>
    /// Quest APK build (PLAN.md S1.14). Two types: D = development build (Debug.isDebugBuild is true, so the device
    /// self-test can switch itself on; never quote fps from it) and R = release. Each build:
    ///   1. checks the Android target is active and the S1.10 Quest settings hold (ProjectBootstrap's check);
    ///   2. runs the scene builder (VirtualBeeSwarm.unity is a build output) and checks the build list is exactly that
    ///      scene, with no MRUK / scene-model component in it, and its OVRManager requesting the scene permission
    ///      (the only requester: PLAN.md V1);
    ///   3. writes the Hive token from the HIVE_TOKEN env var into Assets/Resources/beeswax_token.txt (git-ignored)
    ///      and deletes it again after the build, whatever happens. No token = no build;
    ///   4. signs with the shared debug keystore (kept outside the repo; the passwords come from env vars and are
    ///      never saved into ProjectSettings);
    ///   5. writes &lt;VBS_BUILD_DIR&gt;/VirtualBeeSwarm-&lt;D|R&gt;-&lt;n&gt;-&lt;commit or date&gt;.apk;
    ///   6. checks the APK's manifest with aapt2: RECORD_AUDIO, INTERNET, cleartext, hand tracking, PASSTHROUGH,
    ///      USE_SCENE and USE_ANCHOR_API present. A failed check renames the APK to *.apk.REJECTED and the build fails.
    ///
    /// Headless (exit code 0 = a checked APK was written):
    ///   HIVE_TOKEN=... VBS_KEYSTORE_PASS=... Unity -batchmode -nographics -quit -projectPath P -buildTarget Android \
    ///     -executeMethod VirtualBeeSwarm.Editor.GameBuild.BuildApk -logFile apk.log
    /// Env vars:
    ///   HIVE_TOKEN          required. The Hive token (never printed). In the editor menu an existing
    ///                       Assets/Resources/beeswax_token.txt is used (and kept) when HIVE_TOKEN isn't set.
    ///   VBS_BUILD_TYPE      D (default) or R. BuildApkD / BuildApkR force it.
    ///   VBS_KEYSTORE        keystore path (default ~/immerse-the-bay/keys/vbs-debug.keystore)
    ///   VBS_KEYSTORE_PASS   required. VBS_KEY_ALIAS (default vbs-debug), VBS_KEY_PASS (default = VBS_KEYSTORE_PASS)
    ///   VBS_BUILD_DIR       output folder (default Builds/ in the project, git-ignored)
    ///   VBS_BUILD_NUMBER    n (default: 1 + the highest n already in VBS_BUILD_DIR)
    ///   VBS_BUILD_COMMIT    the name's last part (default: git short hash of the project, +"-dirty"; no git = date-time)
    ///   VBS_APK             full output path (overrides the three above)
    ///   VBS_AAPT2           aapt2 path (default: newest build-tools in Unity's Android SDK)
    /// Never switches the build target itself: that reimports everything (and copies B/C must never switch).
    /// </summary>
    public static class GameBuild
    {
        const string TokenAssetPath = "Assets/Resources/beeswax_token.txt";
        const string SetupType = "VirtualBeeSwarm.EditorTools.ProjectBootstrap, VirtualBeeSwarm.Setup.Editor";
        const string Tag = "[VBSBuild]";

        // aapt2 checks (S1.14). Each is (what, regex over `aapt2 dump badging` + `aapt2 dump xmltree` output).
        static readonly (string what, string pattern)[] Required =
        {
            ("RECORD_AUDIO", @"uses-permission: name='android\.permission\.RECORD_AUDIO'"),
            ("INTERNET", @"uses-permission: name='android\.permission\.INTERNET'"),
            ("cleartext (usesCleartextTraffic=true)", @"usesCleartextTraffic\(0x[0-9a-f]+\)=(true|\(type 0x12\)0xffffffff)"),
            ("hand tracking permission", @"uses-permission: name='com\.oculus\.permission\.HAND_TRACKING'"),
            ("hand tracking feature", @"uses-feature(-not-required)?: name='oculus\.software\.handtracking'"),
            ("PASSTHROUGH feature", @"uses-feature(-not-required)?: name='com\.oculus\.feature\.PASSTHROUGH'"),
            // V1: the Depth API and EnvironmentRaycastManager wait for USE_SCENE; anchors keep world-locking.
            ("USE_SCENE", @"uses-permission: name='com\.oculus\.permission\.USE_SCENE'"),
            ("USE_ANCHOR_API", @"uses-permission: name='com\.oculus\.permission\.USE_ANCHOR_API'"),
        };
        static readonly (string what, string pattern)[] Forbidden =
        {
        };

        [MenuItem("Virtual Bee Swarm/Build Quest APK (D, development)")]
        public static void BuildApkMenuD() => BuildFromMenu('D');

        [MenuItem("Virtual Bee Swarm/Build Quest APK (R, release)")]
        public static void BuildApkMenuR() => BuildFromMenu('R');

        static void BuildFromMenu(char type)
        {
            // The scene builder replaces the open scenes, so offer to save unsaved changes first.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Run(type);
        }

        /// <summary>-executeMethod entry point: type from VBS_BUILD_TYPE (default D). Exit code 0 = checked APK written.</summary>
        public static void BuildApk()
        {
            var t = (Environment.GetEnvironmentVariable("VBS_BUILD_TYPE") ?? "D").Trim().ToUpperInvariant();
            if (t != "D" && t != "R")
            {
                Debug.LogError($"{Tag} FAILED: VBS_BUILD_TYPE='{t}': use D or R");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }
            Run(t[0]);
        }

        public static void BuildApkD() => Run('D');
        public static void BuildApkR() => Run('R');

        static void Run(char type)
        {
            int code = 1;
            var t0 = DateTime.Now;
            var token = new TokenInjection();
            var signing = new SigningOverride();
            string outPath = null;
            try
            {
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                    throw new Exception("active build target is " + EditorUserBuildSettings.activeBuildTarget +
                                        ": switch to Android first (File > Build Profiles > Android > Switch Platform, or -buildTarget Android)");

                CheckQuestSettings();

                // The scene is generated, so build it from the current code and assets first (no prompt).
                string mainScene = BuildGameScene.BuildMain();
                var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
                if (scenes.Length != 1 || scenes[0] != mainScene)
                    throw new Exception("Build Settings scenes should be exactly [" + mainScene + "], got [" + string.Join(", ", scenes) + "]");
                CheckScene(mainScene);

                token.Inject();
                signing.Apply();
                outPath = OutputPath(type);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                EditorUserBuildSettings.buildAppBundle = false;
                var options = type == 'D' ? BuildOptions.Development : BuildOptions.None;

                Debug.Log($"{Tag} building {type} -> {outPath} from [{string.Join(", ", scenes)}] options={options} " +
                          $"keystore={Path.GetFileName(PlayerSettings.Android.keystoreName)} alias={PlayerSettings.Android.keyaliasName} token=injected");
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outPath,
                    target = BuildTarget.Android,
                    options = options,
                });
                var sum = report.summary;
                Debug.Log($"{Tag} {sum.result} errors={sum.totalErrors} warnings={sum.totalWarnings} " +
                          $"size={sum.totalSize / (1024 * 1024)} MB build time={sum.totalTime.TotalSeconds:0} s");
                if (sum.result != BuildResult.Succeeded) throw new Exception("BuildPipeline result " + sum.result);

                VerifyManifest(outPath);
                var apk = new FileInfo(outPath);
                Debug.Log($"{Tag} OK {type} {apk.FullName} ({apk.Length / (1024.0 * 1024.0):0.0} MB, " +
                          $"{(DateTime.Now - t0).TotalSeconds:0} s wall)");
                code = 0;
            }
            catch (Exception e)
            {
                Debug.LogError($"{Tag} FAILED: {e.Message}");
                code = 1;
            }
            finally
            {
                // Always: the token never stays in Assets, the keystore passwords never reach ProjectSettings.
                try { signing.Restore(); } catch (Exception e) { Debug.LogError($"{Tag} signing restore failed: {e.Message}"); code = 1; }
                try { token.Remove(); } catch (Exception e) { Debug.LogError($"{Tag} TOKEN FILE NOT REMOVED ({TokenAssetPath}): {e.Message}"); code = 1; }
                try { ScrubDevAgentSettings(); } catch (Exception e) { Debug.LogError($"{Tag} {DevAgentSettingsPath}: {e.Message}"); code = 1; }
                // The build may have saved ProjectSettings.asset with the keystore path in it: save the restored values.
                AssetDatabase.SaveAssets();
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        // ---------------------------------------------------------------- settings + scene checks

        static void CheckQuestSettings()
        {
            var setup = Type.GetType(SetupType);
            var check = setup?.GetMethod("QuestRequirementProblems");
            if (check == null) throw new Exception("ProjectBootstrap.QuestRequirementProblems not found (Setup.Editor assembly missing?)");
            var problems = (List<string>)check.Invoke(null, new object[] { null });
            if (problems.Count > 0)
                throw new Exception("Quest settings (S1.10) not met: " + string.Join("; ", problems) +
                                    ". Run Virtual Bee Swarm > Bootstrap Project Settings.");
        }

        static void CheckScene(string scenePath)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid() || scene.path != scenePath) scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            var find = Type.GetType(SetupType)?.GetMethod("FindSceneModelComponents");
            if (find == null) throw new Exception("ProjectBootstrap.FindSceneModelComponents not found");
            var sceneModel = (List<string>)find.Invoke(null, new object[] { scene });
            if (sceneModel.Count > 0)
                throw new Exception($"{scenePath} has MRUK / scene-model components ({string.Join(", ", sceneModel)}): no MRUK scene loading (S1.10)");

            // OVRManager lives in the Meta assembly, which this one doesn't reference: read the fields by reflection.
            int managers = 0;
            foreach (var root in scene.GetRootGameObjects())
            foreach (var c in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null || c.GetType().Name != "OVRManager") continue;
                managers++;
                var so = new SerializedObject(c);
                var scenePerm = so.FindProperty("requestScenePermissionOnStartup");
                if (scenePerm == null || !scenePerm.boolValue)
                    throw new Exception($"{scenePath}: OVRManager doesn't request the scene permission at startup (V1: it is the only " +
                                        "USE_SCENE requester). Re-run the bootstrap (it fixes the rig template), then build again.");
            }
            if (managers != 1) throw new Exception($"{scenePath} should have exactly 1 OVRManager, has {managers}");
        }

        // ---------------------------------------------------------------- Meta DevAgent settings

        const string DevAgentSettingsPath = "Assets/Resources/DevAgentSettings.asset";

        /// <summary>
        /// Meta XR Core's DevAgentBuildProcessor writes the build machine's LAN IP and its agent-bridge access token into
        /// Assets/Resources/DevAgentSettings.asset on every build, and "restores" whatever was there before, so once
        /// they're in, they stay in a tracked file (and this repo is public). The DevAgent is off (enabled: 0), so
        /// clear both after every build. The APK itself still gets them, which is fine: APKs stay private anyway.
        /// </summary>
        static void ScrubDevAgentSettings()
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(DevAgentSettingsPath);
            if (asset == null) return;
            var so = new SerializedObject(asset);
            bool changed = false;
            foreach (var name in new[] { "accessToken", "serverAddress" })
            {
                var p = so.FindProperty(name);
                if (p == null || p.propertyType != SerializedPropertyType.String || p.stringValue.Length == 0) continue;
                p.stringValue = "";
                changed = true;
            }
            if (!changed) return;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(asset);
            Debug.Log($"{Tag} cleared the Meta DevAgent access token and LAN address from {DevAgentSettingsPath} (tracked file)");
        }

        // ---------------------------------------------------------------- output name

        static string OutputPath(char type)
        {
            string explicitPath = Environment.GetEnvironmentVariable("VBS_APK");
            if (!string.IsNullOrEmpty(explicitPath)) return Path.GetFullPath(explicitPath);

            string dir = Environment.GetEnvironmentVariable("VBS_BUILD_DIR");
            dir = string.IsNullOrEmpty(dir) ? Path.GetFullPath("Builds") : Path.GetFullPath(ExpandHome(dir));

            int n;
            var nEnv = Environment.GetEnvironmentVariable("VBS_BUILD_NUMBER");
            if (!string.IsNullOrEmpty(nEnv))
            {
                if (!int.TryParse(nEnv, out n) || n < 1) throw new Exception($"VBS_BUILD_NUMBER='{nEnv}' isn't a positive number");
            }
            else
            {
                n = 1;
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "VirtualBeeSwarm-*"))
                    {
                        var m = Regex.Match(Path.GetFileName(f), @"^VirtualBeeSwarm-[DR]-(\d+)-");
                        if (m.Success) n = Math.Max(n, int.Parse(m.Groups[1].Value) + 1);
                    }
            }

            string tag = Environment.GetEnvironmentVariable("VBS_BUILD_COMMIT");
            if (string.IsNullOrEmpty(tag)) tag = GitTag() ?? DateTime.Now.ToString("yyyyMMdd-HHmm");
            tag = Regex.Replace(tag, @"[^A-Za-z0-9._-]", "_");
            return Path.Combine(dir, $"VirtualBeeSwarm-{type}-{n}-{tag}.apk");
        }

        /// <summary>Short hash of HEAD (+"-dirty" if the project folder has uncommitted changes), or null without git.</summary>
        static string GitTag()
        {
            var hash = RunTool("git", "rev-parse --short HEAD", Directory.GetCurrentDirectory(), out int rc);
            if (rc != 0 || string.IsNullOrWhiteSpace(hash)) return null;
            var status = RunTool("git", "status --porcelain -- .", Directory.GetCurrentDirectory(), out rc);
            return hash.Trim() + (rc == 0 && !string.IsNullOrWhiteSpace(status) ? "-dirty" : "");
        }

        // ---------------------------------------------------------------- token

        /// <summary>Puts the token in Assets/Resources for the build and takes it out again. Never logs it.</summary>
        sealed class TokenInjection
        {
            bool _injected;
            string _backup;

            public void Inject()
            {
                string token = (Environment.GetEnvironmentVariable("HIVE_TOKEN") ?? "").Trim();
                if (token.Length == 0)
                {
                    // The editor menu may use a token file the developer put there by hand (README "Token"); it stays.
                    bool fileOk = File.Exists(TokenAssetPath) && File.ReadAllText(TokenAssetPath).Trim().Length > 0;
                    if (!Application.isBatchMode && fileOk)
                    {
                        Debug.Log($"{Tag} token: HIVE_TOKEN not set, using the existing {TokenAssetPath} (kept)");
                        return;
                    }
                    throw new Exception("HIVE_TOKEN is not set: no token, no APK (it would run offline only). " +
                                        "Set HIVE_TOKEN (ask Rome) and build again.");
                }
                if (token.Contains("\n") || token.Contains("\r")) throw new Exception("HIVE_TOKEN has a line break in it");

                Directory.CreateDirectory(Path.GetDirectoryName(TokenAssetPath));
                if (File.Exists(TokenAssetPath))
                {
                    // Someone's own token file: keep it out of Assets for the build, put it back afterwards.
                    _backup = Path.Combine(Path.GetFullPath("Temp"), "beeswax_token.txt.bak-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.GetDirectoryName(_backup));
                    File.Move(TokenAssetPath, _backup);
                }
                _injected = true;
                File.WriteAllText(TokenAssetPath, token + "\n", new UTF8Encoding(false));
                AssetDatabase.ImportAsset(TokenAssetPath, ImportAssetOptions.ForceSynchronousImport);
                if (Resources.Load<TextAsset>("beeswax_token") == null)
                    throw new Exception($"{TokenAssetPath} written but Resources.Load(\"beeswax_token\") can't see it");
                Debug.Log($"{Tag} token: injected from HIVE_TOKEN ({token.Length} chars), removed after the build");
            }

            public void Remove()
            {
                if (!_injected) return;
                AssetDatabase.DeleteAsset(TokenAssetPath); // removes the .meta too
                if (File.Exists(TokenAssetPath)) File.Delete(TokenAssetPath);
                if (File.Exists(TokenAssetPath + ".meta")) File.Delete(TokenAssetPath + ".meta");
                if (_backup != null && File.Exists(_backup))
                {
                    File.Move(_backup, TokenAssetPath);
                    AssetDatabase.ImportAsset(TokenAssetPath);
                }
                AssetDatabase.Refresh();
                _injected = false;
                if (_backup == null && File.Exists(TokenAssetPath)) throw new Exception("still there after the delete");
                Debug.Log($"{Tag} token: removed {TokenAssetPath}" + (_backup != null ? " (the original file is back)" : ""));
            }
        }

        // ---------------------------------------------------------------- signing

        /// <summary>
        /// Points the build at the shared debug keystore and restores the previous Player settings afterwards, so the
        /// machine-specific path never lands in ProjectSettings.asset. Unity keeps the passwords in memory only. The
        /// restore writes the serialized values back as they were (the keystoreName setter would store an empty
        /// name as "{inproject}: " and show up as a ProjectSettings diff).
        /// </summary>
        sealed class SigningOverride
        {
            static readonly string[] Props = { "AndroidKeystoreName", "AndroidKeyaliasName", "androidUseCustomKeystore" };
            bool _applied;
            readonly Dictionary<string, object> _raw = new Dictionary<string, object>();

            static SerializedObject PlayerSettingsAsset()
            {
                var obj = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
                if (obj == null) throw new Exception("ProjectSettings/ProjectSettings.asset not loadable");
                return new SerializedObject(obj);
            }

            public void Apply()
            {
                string path = Environment.GetEnvironmentVariable("VBS_KEYSTORE");
                if (string.IsNullOrEmpty(path))
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "immerse-the-bay", "keys", "vbs-debug.keystore");
                path = Path.GetFullPath(ExpandHome(path));
                if (!File.Exists(path))
                    throw new Exception($"keystore {path} not found: get the shared debug keystore (ask Rome) or set VBS_KEYSTORE");
                string storePass = Environment.GetEnvironmentVariable("VBS_KEYSTORE_PASS");
                if (string.IsNullOrEmpty(storePass)) throw new Exception("VBS_KEYSTORE_PASS is not set (the keystore's password)");
                string alias = Environment.GetEnvironmentVariable("VBS_KEY_ALIAS");
                if (string.IsNullOrEmpty(alias)) alias = "vbs-debug";
                string keyPass = Environment.GetEnvironmentVariable("VBS_KEY_PASS");
                if (string.IsNullOrEmpty(keyPass)) keyPass = storePass;

                var so = PlayerSettingsAsset();
                foreach (var name in Props)
                {
                    var p = so.FindProperty(name) ?? throw new Exception("PlayerSettings has no serialized " + name);
                    _raw[name] = p.propertyType == SerializedPropertyType.Boolean ? (object)p.boolValue
                        : p.propertyType == SerializedPropertyType.Integer ? p.intValue
                        : (object)p.stringValue;
                }
                _applied = true;
                PlayerSettings.Android.useCustomKeystore = true;
                PlayerSettings.Android.keystoreName = path;
                PlayerSettings.Android.keystorePass = storePass;
                PlayerSettings.Android.keyaliasName = alias;
                PlayerSettings.Android.keyaliasPass = keyPass;
            }

            public void Restore()
            {
                if (!_applied) return;
                PlayerSettings.Android.keystorePass = "";
                PlayerSettings.Android.keyaliasPass = "";
                var so = PlayerSettingsAsset();
                foreach (var kv in _raw)
                {
                    var p = so.FindProperty(kv.Key);
                    if (kv.Value is bool b) p.boolValue = b;
                    else if (kv.Value is int i) p.intValue = i;
                    else p.stringValue = (string)kv.Value;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                _applied = false;
                if (PlayerSettings.Android.useCustomKeystore && string.IsNullOrEmpty(PlayerSettings.Android.keystoreName) == false &&
                    PlayerSettings.Android.keystoreName.Contains("vbs-debug"))
                    throw new Exception("the keystore path is still in the Player settings after the restore");
            }
        }

        // ---------------------------------------------------------------- aapt2 check

        static void VerifyManifest(string apk)
        {
            string aapt2 = FindAapt2();
            string badging = RunTool(aapt2, $"dump badging \"{apk}\"", null, out int rc1);
            string xml = RunTool(aapt2, $"dump xmltree --file AndroidManifest.xml \"{apk}\"", null, out int rc2);
            if (rc1 != 0 || rc2 != 0) throw new Exception($"aapt2 failed (rc {rc1}/{rc2}) on {apk}");
            string all = badging + "\n" + xml;

            var lines = badging.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("package:") || l.StartsWith("uses-permission") || l.StartsWith("uses-feature") ||
                            l.StartsWith("sdkVersion") || l.StartsWith("targetSdkVersion") || l.StartsWith("launchable-activity"))
                .ToList();
            var problems = new List<string>();
            foreach (var (what, pattern) in Required)
                if (!Regex.IsMatch(all, pattern)) problems.Add("missing " + what);
            foreach (var (what, pattern) in Forbidden)
                if (Regex.IsMatch(all, pattern)) problems.Add("has " + what);
            if (Regex.IsMatch(xml, @"networkSecurityConfig"))
                lines.Add("NOTE: a networkSecurityConfig is set; it may override usesCleartextTraffic");

            File.WriteAllText(apk + ".manifest.txt",
                "aapt2 dump badging (selected lines) for " + Path.GetFileName(apk) + "\n" + string.Join("\n", lines) + "\n\n" +
                (problems.Count == 0 ? "S1.14 manifest check: PASS" : "S1.14 manifest check: FAILED: " + string.Join("; ", problems)) + "\n");
            Debug.Log($"{Tag} aapt2 ({aapt2}):\n  " + string.Join("\n  ", lines));

            if (problems.Count > 0)
            {
                string rejected = apk + ".REJECTED";
                if (File.Exists(rejected)) File.Delete(rejected);
                File.Move(apk, rejected);
                throw new Exception("manifest check FAILED: " + string.Join("; ", problems) + $" (APK renamed to {rejected})");
            }
            Debug.Log($"{Tag} manifest check: PASS (RECORD_AUDIO, INTERNET, cleartext, hand tracking, PASSTHROUGH, USE_SCENE, USE_ANCHOR_API)");
        }

        static string FindAapt2()
        {
            string env = Environment.GetEnvironmentVariable("VBS_AAPT2");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            var roots = new List<string>();
            var tools = Type.GetType("UnityEditor.Android.AndroidExternalToolsSettings, UnityEditor.Android.Extensions");
            if (tools?.GetProperty("sdkRootPath")?.GetValue(null) is string unitySdk && unitySdk.Length > 0) roots.Add(unitySdk);
            foreach (var v in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
            {
                var r = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(r)) roots.Add(r);
            }
            string exe = Application.platform == RuntimePlatform.WindowsEditor ? "aapt2.exe" : "aapt2";
            foreach (var root in roots)
            {
                var bt = Path.Combine(root, "build-tools");
                if (!Directory.Exists(bt)) continue;
                var best = Directory.GetDirectories(bt)
                    .Select(d => Path.Combine(d, exe)).Where(File.Exists)
                    .OrderByDescending(p => ParseVersion(Path.GetFileName(Path.GetDirectoryName(p))))
                    .FirstOrDefault();
                if (best != null) return best;
            }
            throw new Exception("aapt2 not found (no build-tools in the Android SDK); set VBS_AAPT2");
        }

        static Version ParseVersion(string s) => Version.TryParse(Regex.Replace(s, @"[^0-9.].*$", ""), out var v) ? v : new Version(0, 0);

        // ---------------------------------------------------------------- helpers

        static string ExpandHome(string path) =>
            path.StartsWith("~/") || path == "~"
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path.Substring(2) : "")
                : path;

        static string RunTool(string file, string args, string workDir, out int exitCode)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                if (workDir != null) psi.WorkingDirectory = workDir;
                using (var p = Process.Start(psi))
                {
                    var stderr = p.StandardError.ReadToEndAsync();
                    string stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    exitCode = p.ExitCode;
                    if (exitCode != 0 && stderr.Result.Length > 0) stdout += "\n" + stderr.Result;
                    return stdout;
                }
            }
            catch (Exception)
            {
                exitCode = -1;
                return null;
            }
        }
    }
}
