// Virtual Bee Swarm Quest MR project bootstrap. Idempotent: run it as often as you like.
//
//   Unity -batchmode -nographics -projectPath <VirtualBeeSwarm> -buildTarget Android \
//         -executeMethod VirtualBeeSwarm.EditorTools.ProjectBootstrap.Run -quit -logFile -
//
// (drop -buildTarget Android while the Android module is not installed; the Android settings are
// written anyway and the build target is switched once the module exists). In the editor:
// menu Virtual Bee Swarm > Bootstrap Project Settings.
//
// Hard requirements (package id, IL2CPP/ARM64, API levels, ASTC, Linear, Internet, XR loaders, OpenXR
// features, Meta project config, the rig template's permission/boundary flags, the custom Android
// manifest) are re-applied on every run. Tunables (the URP asset's quality settings, the rest of the
// base scene) are only written when they don't exist yet, so hand edits survive.
// Every line it logs starts with [VBSBootstrap]; -executeMethod exits non-zero on a failure, and the
// final "S1.10 check" step fails the run if any Quest launch requirement (PLAN.md S1.10) isn't met.
//
// Boundary (PLAN.md S1.10 "boundaryless sits behind a toggle"): one switch, the Android scripting
// define. Default (neither define) = OFF: the normal Meta boundary, no Boundary API permission, no
// suppression request. VBS_BOUNDARYLESS = contextual boundaryless (Boundary API: the boundary hides
// while passthrough is visible). VBS_BOUNDARYLESS_APP = fully boundaryless app (manifest feature
// com.oculus.feature.BOUNDARYLESS_APP). Switch it with the menu Virtual Bee Swarm > Boundary, or
// headless with the env var VBS_BOUNDARY_MODE=off|contextual|app on a bootstrap run.

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;
#if UNITY_ANDROID
using UnityEditor.Android;
#endif

namespace VirtualBeeSwarm.EditorTools
{
    public static class ProjectBootstrap
    {
        public const string PackageId = "dev.drori.virtualbeeswarm";
        public const string ProductName = "Virtual Bee Swarm";
        public const string CompanyName = "Virtual Bee Swarm";
        public const string BaseScenePath = "Assets/VirtualBeeSwarm/Scenes/RigTemplate.unity";

        // Meta XR Core SDK 209 Project Setup Tool: min API 32 is Required, target API 34 is
        // Recommended ("to upload to the Meta Quest Store"). See Editor/Rules/AndroidCompatibility.cs.
        const AndroidSdkVersions MinSdk = AndroidSdkVersions.AndroidApiLevel32;
        const int TargetSdk = 34;

        const string SettingsDir = "Assets/VirtualBeeSwarm/Settings";
        const string UrpAssetPath = SettingsDir + "/VirtualBeeSwarmURP.asset";
        const string UrpRendererPath = SettingsDir + "/VirtualBeeSwarmURP_Renderer.asset";
        const string UrpPostProcessDataPath = "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset";
        const string OvrCameraRigPrefab = "Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab";
        const string XRGeneralSettingsAssetPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";

        const string OpenXRLoaderTypeName = "UnityEngine.XR.OpenXR.OpenXRLoader";
        const string MetaXRFeatureSetId = "com.meta.openxr.featureset.metaxr"; // Meta.XR.MetaXRFeatureSet (internal)
        const string MetaXRFeatureId = "com.meta.openxr.feature.metaxr"; //       Meta.XR.MetaXRFeature
        const string OculusTouchProfileId = "com.unity.openxr.feature.input.oculustouch";
        // Unity OpenXR: Meta (com.unity.xr.meta-openxr). Meta's Depth API (occlusion) on the OpenXR
        // path requires these two (Meta XR Core 209, Editor/EnvironmentDepth/ProjectSetupDepthAPI.cs), on Android AND
        // Standalone (Link play mode). PLAN.md v3.1 V1: the AR layer uses the Depth API, so they are ON.
        static readonly string[] UnityMetaFeaturesOn =
        {
            "com.unity.openxr.feature.arfoundation-meta-session",
            "com.unity.openxr.feature.arfoundation-meta-occlusion",
        };
        // Unity OpenXR: Meta (com.unity.xr.meta-openxr, AR Foundation) features that must stay OFF (PLAN.md S1.10, V1).
        // Passthrough has exactly ONE provider: Meta XR Core's OVRPassthroughLayer through the Meta XR feature + Meta
        // Quest Support. The AR Foundation camera would be a second one. Planes, bounding boxes, meshes and raycasts
        // belong to AR Foundation (we use Meta's EnvironmentRaycastManager, which talks to OVRPlugin/MRUK directly),
        // anchors add IoT map data. The boundary goes through OVRManager (the toggle above), not Unity's boundary feature.
        static readonly string[] UnityMetaFeaturesOff =
        {
            "com.unity.openxr.feature.arfoundation-meta-camera",
            "com.unity.openxr.feature.arfoundation-meta-plane",
            "com.unity.openxr.feature.arfoundation-meta-anchor",
            "com.unity.openxr.feature.arfoundation-meta-bounding-boxes",
            "com.unity.openxr.feature.arfoundation-meta-mesh",
            "com.unity.openxr.feature.arfoundation-meta-raycast",
            "com.unity.openxr.feature.meta-boundary-visibility",
        };

        // Meta Project Setup Tool tasks that contradict S1.10 and must never be auto-fixed (here or by "Fix All" in
        // the editor). They're marked Ignored, which Meta stores in Assets/Oculus/OculusProjectSetupSettings.asset
        // (in git), so the setting travels with the repo. Matched by the start of the task's message.
        static readonly string[] IgnoredSetupTasks =
        {
            // MRUK's rule fires for any scene with an OVRCameraRig; its fix sets Scene Support = Required (USE_SCENE).
            "MR Utility Kit recommends Scene Support",
        };

        // Setup-tool tasks the bootstrap never auto-fixes (left for a person in the Project Setup Tool). The XR
        // Simulator "fix" starts an async download/installer that batch mode then waits on until it times out (300 s,
        // exit code 1), and the simulator isn't part of our workflow.
        static readonly string[] NeverAutoFixSetupTasks =
        {
            "Newest Meta XR Simulator",
        };

        // USE_SCENE must be in the manifest (V1: depth + environment raycast wait for it); the other two must not
        // unless the boundary toggle asks for them.
        public const string UseScenePermission = "com.oculus.permission.USE_SCENE";
        public const string BoundaryVisibilityPermission = "com.oculus.permission.BOUNDARY_VISIBILITY";
        public const string BoundarylessAppFeature = "com.oculus.feature.BOUNDARYLESS_APP";
        const string CustomManifestPath = "Assets/Plugins/Android/AndroidManifest.xml";

        // ------------------------------------------------------------------ boundary toggle

        public enum BoundaryMode { Off, Contextual, App }
        public const string BoundaryContextualDefine = "VBS_BOUNDARYLESS";
        public const string BoundaryAppDefine = "VBS_BOUNDARYLESS_APP";

        /// <summary>The boundary mode, read from the Android scripting defines (the toggle).</summary>
        public static BoundaryMode CurrentBoundaryMode
        {
            get
            {
                PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Android, out string[] defines);
                if (defines.Contains(BoundaryAppDefine)) return BoundaryMode.App;
                if (defines.Contains(BoundaryContextualDefine)) return BoundaryMode.Contextual;
                return BoundaryMode.Off;
            }
        }

        static bool SetBoundaryDefine(BoundaryMode mode)
        {
            PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Android, out string[] defines);
            var next = defines.Where(d => d != BoundaryAppDefine && d != BoundaryContextualDefine).ToList();
            if (mode == BoundaryMode.App) next.Add(BoundaryAppDefine);
            else if (mode == BoundaryMode.Contextual) next.Add(BoundaryContextualDefine);
            if (next.SequenceEqual(defines)) return false;
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Android, next.ToArray());
            Log($"boundary mode -> {mode} (Android defines: {string.Join(";", next)})");
            return true;
        }

        const string BoundaryMenu = "Virtual Bee Swarm/Boundary/";
        [MenuItem(BoundaryMenu + "Off (normal boundary)")] static void BoundaryOff() => SwitchBoundary(BoundaryMode.Off);
        [MenuItem(BoundaryMenu + "Contextual boundaryless (hidden while passthrough shows)")] static void BoundaryContextual() => SwitchBoundary(BoundaryMode.Contextual);
        [MenuItem(BoundaryMenu + "Boundaryless app (BOUNDARYLESS_APP)")] static void BoundaryApp() => SwitchBoundary(BoundaryMode.App);
        [MenuItem(BoundaryMenu + "Off (normal boundary)", true)] static bool BoundaryOffCheck() => CheckBoundary(BoundaryMode.Off, "Off (normal boundary)");
        [MenuItem(BoundaryMenu + "Contextual boundaryless (hidden while passthrough shows)", true)] static bool BoundaryContextualCheck() => CheckBoundary(BoundaryMode.Contextual, "Contextual boundaryless (hidden while passthrough shows)");
        [MenuItem(BoundaryMenu + "Boundaryless app (BOUNDARYLESS_APP)", true)] static bool BoundaryAppCheck() => CheckBoundary(BoundaryMode.App, "Boundaryless app (BOUNDARYLESS_APP)");

        static bool CheckBoundary(BoundaryMode mode, string item)
        {
            Menu.SetChecked(BoundaryMenu + item, CurrentBoundaryMode == mode);
            return true;
        }

        static void SwitchBoundary(BoundaryMode mode)
        {
            SetBoundaryDefine(mode);
            RunInternal(throwOnFailure: false); // applies the Meta config, rig template and manifest for the new mode
        }

        static void ApplyBoundaryModeFromEnv()
        {
            var env = (Environment.GetEnvironmentVariable("VBS_BOUNDARY_MODE") ?? "").Trim().ToLowerInvariant();
            if (env.Length == 0) return;
            BoundaryMode mode;
            switch (env)
            {
                case "off": mode = BoundaryMode.Off; break;
                case "contextual": mode = BoundaryMode.Contextual; break;
                case "app": mode = BoundaryMode.App; break;
                default: throw new Exception($"VBS_BOUNDARY_MODE='{env}': use off, contextual or app");
            }
            SetBoundaryDefine(mode);
        }

        const string SpatializerName = "Meta XR Audio";
        const string Tag = "[VBSBootstrap]";

        static readonly List<string> s_Failures = new List<string>();
        static readonly List<string> s_Notes = new List<string>();

        static bool AndroidSupported =>
            BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android);

        [MenuItem("Virtual Bee Swarm/Bootstrap Project Settings")]
        static void RunFromMenu() => RunInternal(throwOnFailure: false);

        /// <summary>Entry point for -executeMethod. Throws (non-zero exit) if any step failed.</summary>
        public static void Run() => RunInternal(throwOnFailure: true);

        static void RunInternal(bool throwOnFailure)
        {
            s_Failures.Clear();
            s_Notes.Clear();
            Log($"start. Unity {Application.unityVersion}, active target {EditorUserBuildSettings.activeBuildTarget}, " +
                $"Android module installed: {AndroidSupported}");

            Step("boundary toggle (VBS_BOUNDARY_MODE)", ApplyBoundaryModeFromEnv);
            Log($"boundary mode: {CurrentBoundaryMode}");
            Step("player settings", ConfigurePlayerSettings);
            Step("audio spatializer", ConfigureAudio);
            Step("URP", ConfigureUrp);
            Step("XR loaders (Standalone)", () => ConfigureXRLoader(BuildTargetGroup.Standalone));
            Step("XR loaders (Android)", () => ConfigureXRLoader(BuildTargetGroup.Android));
            Step("OpenXR features (Standalone)", () => ConfigureOpenXR(BuildTargetGroup.Standalone));
            Step("OpenXR features (Android)", () => ConfigureOpenXR(BuildTargetGroup.Android));
            Step("Meta project config", ConfigureOvrProjectConfig);
            Step("base scene", EnsureBaseScene);
            Step("rig template flags", ConfigureRigTemplate);
            Step("Meta Project Setup Tool: ignore tasks that contradict S1.10", IgnoreConflictingSetupTasks);
            Step("Meta Project Setup Tool fixes (Android)", () => RunMetaProjectSetupFixes(BuildTargetGroup.Android));
            Step("OpenXR validation (Android)", () => RunOpenXRValidation(BuildTargetGroup.Android));
            // A setup-tool or validator fix may have flipped one of these back; the hard requirements win.
            Step("re-assert hard requirements", () =>
            {
                ConfigurePlayerSettings();
                ConfigureOvrProjectConfig();
                DisableUnityMetaFeatures(BuildTargetGroup.Standalone);
                DisableUnityMetaFeatures(BuildTargetGroup.Android);
                EnableDepthFeatures(BuildTargetGroup.Standalone);
                EnableDepthFeatures(BuildTargetGroup.Android);
                ConfigureRigTemplate();
            });
            Step("custom Android manifest", UpdateCustomAndroidManifest);
            Step("save", SaveAll);
            Step("summary", LogSummary);
            Step("build target", SwitchToAndroidIfPossible);
            Step("S1.10 check", CheckQuestRequirements);

            foreach (var n in s_Notes) Log("NOTE: " + n);
            if (s_Failures.Count == 0)
            {
                Log("done: OK");
                return;
            }

            var msg = $"done with {s_Failures.Count} failed step(s): {string.Join("; ", s_Failures)}";
            Debug.LogError($"{Tag} {msg}");
            if (throwOnFailure) throw new Exception(msg);
        }

        // ------------------------------------------------------------------ player settings

        static void ConfigurePlayerSettings()
        {
            PlayerSettings.companyName = CompanyName;
            PlayerSettings.productName = ProductName;

            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, PackageId);
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, PackageId);

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = MinSdk;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)TargetSdk;
            PlayerSettings.Android.forceInternetPermission = true; // Internet Access = Require
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity; // Meta: single GameActivity on 2023.2+
            PlayerSettings.Android.preferredInstallLocation = AndroidPreferredInstallLocation.Auto;
            PlayerSettings.Android.androidTVCompatibility = false;
            // The venue fallback is ws://<laptop-LAN-IP> (cleartext) and the bus probes http(s)://host/health.
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;

            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.stereoRenderingPath = StereoRenderingPath.Instancing;

            // Active Input Handling = Both (OpenXR/Interaction SDK use the Input System, OVRInput and
            // some samples still read UnityEngine.Input). Takes effect the next time the editor starts.
            SetSerialized("ProjectSettings/ProjectSettings.asset", "activeInputHandler", p => p.intValue = 2);

            // ASTC: the tracked Player setting (ProjectSettings.asset) plus the per-machine build override.
            if (!(PlayerSettings.Android.textureCompressionFormats ?? Array.Empty<TextureCompressionFormat>())
                    .SequenceEqual(new[] { TextureCompressionFormat.ASTC }))
                PlayerSettings.Android.textureCompressionFormats = new[] { TextureCompressionFormat.ASTC };
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
        }

        // ------------------------------------------------------------------ audio

        static void ConfigureAudio()
        {
            // The Linux editor has no Meta XR Audio editor plugin (Windows/macOS/Android only), so the
            // package's own auto-setter never runs here. The name is what the Android player loads.
            SetSerialized("ProjectSettings/AudioManager.asset", "m_SpatializerPlugin", p => p.stringValue = SpatializerName);
            SetSerialized("ProjectSettings/AudioManager.asset", "m_AmbisonicDecoderPlugin", p => p.stringValue = SpatializerName);
            if (!AudioSettings.GetSpatializerPluginNames().Contains(SpatializerName))
                s_Notes.Add($"'{SpatializerName}' is not loadable in this editor ({Application.platform}); " +
                            "it is set in AudioManager.asset and loads in the Android player. Spatial audio can't be previewed in this editor.");
        }

        // ------------------------------------------------------------------ URP

        static void ConfigureUrp()
        {
            EnsureFolder(SettingsDir);
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (asset == null)
            {
                var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(UrpRendererPath);
                if (rendererData == null)
                {
                    rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                    rendererData.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(UrpPostProcessDataPath);
                    rendererData.intermediateTextureMode = IntermediateTextureMode.Auto; // Meta: "Auto", not "Always"
                    AssetDatabase.CreateAsset(rendererData, UrpRendererPath);
                }

                asset = UniversalRenderPipelineAsset.Create(rendererData);
                // Quest-friendly starting point (only on creation; tune freely afterwards).
                asset.msaaSampleCount = 4;
                asset.supportsHDR = false;
                asset.supportsCameraDepthTexture = false;
                asset.supportsCameraOpaqueTexture = false;
                asset.renderScale = 1f;
                asset.shadowDistance = 20f;
                AssetDatabase.CreateAsset(asset, UrpAssetPath);
                Log($"created {UrpAssetPath} (+ renderer)");
            }

            if (GraphicsSettings.defaultRenderPipeline != asset)
            {
                if (GraphicsSettings.defaultRenderPipeline != null)
                    s_Notes.Add($"default render pipeline was {AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline)}; replaced with {UrpAssetPath}");
                GraphicsSettings.defaultRenderPipeline = asset;
            }

            // Quality levels with their own pipeline asset override the default one.
            var names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
            {
                var rp = QualitySettings.GetRenderPipelineAssetAt(i);
                if (rp != null && rp != asset)
                    s_Notes.Add($"quality level '{names[i]}' overrides the render pipeline with {AssetDatabase.GetAssetPath(rp)}");
            }
        }

        // ------------------------------------------------------------------ XR Plug-in Management

        static void ConfigureXRLoader(BuildTargetGroup group)
        {
            var perTarget = GetOrCreateXRGeneralSettingsPerBuildTarget();
            if (!perTarget.HasSettingsForBuildTarget(group))
                perTarget.CreateDefaultSettingsForBuildTarget(group);
            if (!perTarget.HasManagerSettingsForBuildTarget(group))
                perTarget.CreateDefaultManagerSettingsForBuildTarget(group);

            var general = perTarget.SettingsForBuildTarget(group);
            general.InitManagerOnStart = true;
            var manager = general.Manager;

            // Only OpenXR. (com.unity.xr.oculus is not installed; Meta recommends OpenXR since v74.)
            foreach (var other in manager.activeLoaders.Where(l => l != null && !(l is OpenXRLoader)).ToList())
            {
                XRPackageMetadataStore.RemoveLoader(manager, other.GetType().FullName, group);
                Log($"{group}: removed XR loader {other.GetType().Name}");
            }

            if (!manager.activeLoaders.Any(l => l is OpenXRLoader))
            {
                if (!XRPackageMetadataStore.AssignLoader(manager, OpenXRLoaderTypeName, group))
                    throw new Exception($"AssignLoader(OpenXRLoader, {group}) returned false");
                Log($"{group}: assigned the OpenXR loader");
            }

            EditorUtility.SetDirty(general);
            EditorUtility.SetDirty(manager);
            EditorUtility.SetDirty(perTarget);
        }

        static XRGeneralSettingsPerBuildTarget GetOrCreateXRGeneralSettingsPerBuildTarget()
        {
            EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget perTarget);
            if (perTarget != null) return perTarget;

            var guid = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget").FirstOrDefault();
            if (guid != null)
                perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(AssetDatabase.GUIDToAssetPath(guid));

            if (perTarget == null)
            {
                EnsureFolder(Path.GetDirectoryName(XRGeneralSettingsAssetPath));
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, XRGeneralSettingsAssetPath);
                AssetDatabase.SaveAssets();
                Log($"created {XRGeneralSettingsAssetPath}");
            }

            EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, perTarget, true);
            return perTarget;
        }

        // ------------------------------------------------------------------ OpenXR

        static void ConfigureOpenXR(BuildTargetGroup group)
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (settings == null)
            {
                if (group == BuildTargetGroup.Android && !AndroidSupported)
                {
                    s_Notes.Add("Android OpenXR features NOT configured yet: the Android module is not installed " +
                                "(OpenXR only creates per-platform settings for installed modules). Re-run the bootstrap after installing it.");
                    return;
                }
                throw new Exception($"OpenXR settings for {group} are missing");
            }

            // Meta XR feature group (the "Meta XR" checkbox under OpenXR Feature Groups).
            var featureSet = OpenXRFeatureSetManager.GetFeatureSetWithId(group, MetaXRFeatureSetId);
            if (featureSet == null)
                throw new Exception($"OpenXR feature group {MetaXRFeatureSetId} not found for {group}");
            featureSet.isEnabled = true;
            OpenXRFeatureSetManager.SetFeaturesFromEnabledFeatureSets(group);

            EnableFeature(group, MetaXRFeatureId, required: true);
            EnableFeature(group, OculusTouchProfileId, required: true); // Meta: needed for full OVRInput support

            if (group == BuildTargetGroup.Android)
            {
                var quest = settings.GetFeature<MetaQuestFeature>();
                if (quest == null) throw new Exception("MetaQuestFeature (Meta Quest Support) not found");
                quest.enabled = true;
                quest.ForceRemoveInternetPermission = false; // we talk to the Hive over the network
                EditorUtility.SetDirty(quest);
            }

            if (settings.renderMode != OpenXRSettings.RenderMode.SinglePassInstanced)
                settings.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced; // multiview
            FeatureHelpers.RefreshFeatures(group);
            // The passthrough is OVRPassthroughLayer (Meta XR Core), NOT AR Foundation's camera feature (S1.10).
            DisableUnityMetaFeatures(group);
            EnableDepthFeatures(group);
            EditorUtility.SetDirty(settings);
        }

        /// <summary>Meta Quest: Session + Meta Quest: Occlusion (the Depth API on OpenXR), for this target group (V1).</summary>
        static void EnableDepthFeatures(BuildTargetGroup group)
        {
            if (OpenXRSettings.GetSettingsForBuildTargetGroup(group) == null) return; // module missing, noted elsewhere
            foreach (var id in UnityMetaFeaturesOn) EnableFeature(group, id, required: true);
        }

        static void DisableUnityMetaFeatures(BuildTargetGroup group)
        {
            if (OpenXRSettings.GetSettingsForBuildTargetGroup(group) == null) return; // module missing, noted elsewhere
            foreach (var feature in FeatureHelpers.GetFeaturesWithIdsForBuildTarget(group, UnityMetaFeaturesOff))
            {
                if (feature == null || !feature.enabled) continue;
                feature.enabled = false;
                EditorUtility.SetDirty(feature);
                Log($"{group}: disabled OpenXR feature {feature.GetType().Name} (S1.10)");
            }
        }

        static void EnableFeature(BuildTargetGroup group, string featureId, bool required)
        {
            var feature = FeatureHelpers.GetFeaturesWithIdsForBuildTarget(group, new[] { featureId }).FirstOrDefault();
            if (feature == null)
            {
                if (required) throw new Exception($"OpenXR feature {featureId} not found for {group}");
                s_Notes.Add($"OpenXR feature {featureId} not found for {group}");
                return;
            }
            if (!feature.enabled)
            {
                feature.enabled = true;
                Log($"{group}: enabled OpenXR feature {featureId}");
            }
            EditorUtility.SetDirty(feature);
        }

        // ------------------------------------------------------------------ Meta XR project config

        static void ConfigureOvrProjectConfig()
        {
            var cfg = OVRProjectConfig.CachedProjectConfig;
            if (cfg == null) throw new Exception("OVRProjectConfig.CachedProjectConfig is null");

            cfg.targetDeviceTypes = new List<OVRProjectConfig.DeviceType>
            {
                OVRProjectConfig.DeviceType.Quest2,
                OVRProjectConfig.DeviceType.QuestPro,
                OVRProjectConfig.DeviceType.Quest3,
                OVRProjectConfig.DeviceType.Quest3S,
            };
            // Hands are the game; controllers stay usable for the operator/debugging.
            cfg.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
            cfg.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required; // MR only
            // Scene Support = Required (V1): puts com.oculus.permission.USE_SCENE in the manifest. The Depth API
            // (occlusion) and EnvironmentRaycastManager (aim-and-pinch, height snap, window on the wall) wait for it;
            // Meta's Project Setup Tool asks for Required. MRUK scene loading stays off (nothing calls LoadScene*).
            cfg.sceneSupport = OVRProjectConfig.FeatureSupport.Required;
            cfg.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled; //                 world-locking
            // The boundary toggle: only contextual boundaryless uses the Boundary API
            // (com.oculus.permission.BOUNDARY_VISIBILITY); the rig template's OVRManager asks for the suppression.
            // BOUNDARYLESS_APP is mutually exclusive with the Boundary API, and Off uses neither.
            cfg.boundaryVisibilitySupport = CurrentBoundaryMode == BoundaryMode.Contextual
                ? OVRProjectConfig.FeatureSupport.Supported
                : OVRProjectConfig.FeatureSupport.None;
            cfg.systemLoadingScreenBackground = OVRProjectConfig.SystemLoadingScreenBackground.ContextualPassthrough;
            // Meta's network_sec_config.xml sets cleartextTrafficPermitted=false; the LAN fallback is ws://.
            cfg.enableNSCConfig = false;
            OVRProjectConfig.CommitProjectConfig(cfg);
        }

        // ------------------------------------------------------------------ base scene

        static void EnsureBaseScene()
        {
            if (File.Exists(BaseScenePath))
            {
                // Open it so the scene-dependent Meta setup checks (OVRManager, camera) see it.
                EditorSceneManager.OpenScene(BaseScenePath, OpenSceneMode.Single);
                Log($"{BaseScenePath} exists; left as is");
            }
            else
            {
                EnsureFolder(Path.GetDirectoryName(BaseScenePath).Replace('\\', '/'));
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(OvrCameraRigPrefab);
                if (rigPrefab == null) throw new Exception($"{OvrCameraRigPrefab} not found");
                var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab, scene);

                var manager = rig.GetComponent<OVRManager>();
                if (manager == null) throw new Exception("OVRCameraRig has no OVRManager");
                manager.isInsightPassthroughEnabled = true;
                // The boundary flag and the scene permission are set by ConfigureRigTemplate (the toggle, S1.10).
                var so = new SerializedObject(manager);
                SetIfExists(so, "requestRecordAudioPermissionOnStartup", p => p.boolValue = true); // hum + voice
                so.ApplyModifiedPropertiesWithoutUndo();

                if (rig.GetComponent<OVRPassthroughLayer>() == null)
                    rig.AddComponent<OVRPassthroughLayer>(); // underlay, full-screen reconstructed passthrough

                foreach (var cam in rig.GetComponentsInChildren<Camera>(true))
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0f, 0f, 0f, 0f); // transparent: reveals passthrough
                }

                var light = new GameObject("Directional Light");
                var l = light.AddComponent<Light>();
                l.type = LightType.Directional;
                l.intensity = 1f;
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

                if (!EditorSceneManager.SaveScene(scene, BaseScenePath))
                    throw new Exception($"could not save {BaseScenePath}");
                Log($"created {BaseScenePath} (OVRCameraRig + passthrough underlay)");
            }

            // Only claims the build list when nobody else has put scenes in it yet.
            if (EditorBuildSettings.scenes.Length == 0)
            {
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(BaseScenePath, true) };
                Log($"build scenes = [{BaseScenePath}]");
            }
        }

        // ------------------------------------------------------------------ rig template flags (S1.10)

        /// <summary>
        /// The rig template's OVRManager: no scene-permission request at startup (S1.10: no USE_SCENE), boundary
        /// suppression only in contextual mode (the toggle), passthrough and the mic request on. Marks the template
        /// dirty only when something changed (saved by the save step). Refuses MRUK / OVRSceneManager in the
        /// template: they'd load the scene model (S1.10: no MRUK scene loading).
        /// </summary>
        static void ConfigureRigTemplate()
        {
            if (!File.Exists(BaseScenePath)) throw new Exception($"{BaseScenePath} missing");
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid() || scene.path != BaseScenePath)
                scene = EditorSceneManager.OpenScene(BaseScenePath, OpenSceneMode.Single);

            var managers = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<OVRManager>(true)).ToList();
            if (managers.Count != 1) throw new Exception($"{BaseScenePath} should have exactly 1 OVRManager, has {managers.Count}");
            var so = new SerializedObject(managers[0]);
            var changed = new List<string>();
            SetBool(so, "requestScenePermissionOnStartup", true, changed); // OVRManager is the ONLY requester (V1)
            SetBool(so, "shouldBoundaryVisibilityBeSuppressed", CurrentBoundaryMode == BoundaryMode.Contextual, changed);
            SetBool(so, "isInsightPassthroughEnabled", true, changed);
            SetBool(so, "requestRecordAudioPermissionOnStartup", true, changed);
            if (changed.Count > 0)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.MarkSceneDirty(scene);
                Log($"{BaseScenePath}: OVRManager {string.Join(", ", changed)}");
            }

            var sceneModel = FindSceneModelComponents(scene);
            if (sceneModel.Count > 0)
                throw new Exception($"{BaseScenePath} has scene-model components ({string.Join(", ", sceneModel)}): " +
                                    "remove them (S1.10: no MRUK scene loading, no USE_SCENE)");
        }

        static void SetBool(SerializedObject so, string property, bool value, List<string> changed)
        {
            var p = so.FindProperty(property);
            if (p == null) throw new Exception($"{so.targetObject.GetType().Name} has no serialized field '{property}' (SDK changed?)");
            if (p.boolValue == value) return;
            p.boolValue = value;
            changed.Add($"{property}={value}");
        }

        /// <summary>MRUK components (Meta.XR.MRUtilityKit.*) and the deprecated OVRSceneManager load the scene model
        /// and need USE_SCENE. Returns "object/Type" for each one in the scene. GameBuild runs this on the game scene.</summary>
        public static List<string> FindSceneModelComponents(UnityEngine.SceneManagement.Scene scene)
        {
            var found = new List<string>();
            foreach (var root in scene.GetRootGameObjects())
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var t = c.GetType();
                if ((t.Namespace ?? "").StartsWith("Meta.XR.MRUtilityKit") || t.Name == "OVRSceneManager")
                    found.Add($"{c.gameObject.name}/{t.Name}");
            }
            return found;
        }

        // ------------------------------------------------------------------ Meta Project Setup Tool: ignored tasks

        static void IgnoreConflictingSetupTasks()
        {
            var getTasks = GetSetupTasksMethod();
            foreach (var group in new[] { BuildTargetGroup.Android, BuildTargetGroup.Standalone })
            {
                int matched = 0;
                foreach (var task in ((IEnumerable)getTasks.Invoke(null, new object[] { group })).Cast<object>().ToList())
                {
                    var message = (LambdaValue(TaskProp(task, "Message"), group) as string ?? "").Replace('\n', ' ');
                    if (!IgnoredSetupTasks.Any(m => message.StartsWith(m, StringComparison.Ordinal))) continue;
                    matched++;
                    var isIgnored = (bool)task.GetType().GetMethod("IsIgnored", new[] { typeof(BuildTargetGroup) })
                        .Invoke(task, new object[] { group });
                    if (isIgnored) continue;
                    task.GetType().GetMethod("SetIgnored", new[] { typeof(BuildTargetGroup), typeof(bool) })
                        .Invoke(task, new object[] { group, true });
                    Log($"Meta setup task marked Ignored ({group}): {message}");
                }
                if (matched < IgnoredSetupTasks.Length)
                    s_Notes.Add($"Meta setup ({group}): {IgnoredSetupTasks.Length - matched} task(s) to ignore not found (SDK changed?)");
            }
        }

        static MethodInfo GetSetupTasksMethod()
        {
            var setupType = FindType("OVRProjectSetup");
            if (setupType == null) throw new Exception("OVRProjectSetup not found (Meta XR Core SDK missing?)");
            var getTasks = setupType.GetMethod("GetTasks", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(BuildTargetGroup) }, null);
            if (getTasks == null) throw new Exception("OVRProjectSetup.GetTasks(BuildTargetGroup) not found (SDK API changed?)");
            return getTasks;
        }

        // ------------------------------------------------------------------ custom Android manifest

        /// <summary>
        /// Re-applies Meta's tags to Assets/Plugins/Android/AndroidManifest.xml from the project config, removing the
        /// ones that are switched off (USE_SCENE, BOUNDARY_VISIBILITY). Meta's build-time patch only ADDS to a custom
        /// manifest, so a stale entry here would ship. Same call as Meta XR Tools > Update AndroidManifest.xml.
        /// </summary>
        static void UpdateCustomAndroidManifest()
        {
            string before = File.Exists(CustomManifestPath) ? File.ReadAllText(CustomManifestPath) : null;
            OVRManifestPreprocessor.GenerateOrUpdateAndroidManifest(silentMode: true);
            string after = File.Exists(CustomManifestPath) ? File.ReadAllText(CustomManifestPath) : null;
            if (after == null) throw new Exception($"{CustomManifestPath} was not generated");
            if (before != after) Log($"{CustomManifestPath} updated from the Meta project config");
        }

        // ------------------------------------------------------------------ S1.10 check

        /// <summary>
        /// Everything PLAN.md S1.10 asks of the Quest build, as a list of problems (empty = OK). The bootstrap's last
        /// step fails on any problem; GameBuild.BuildApk calls this (by reflection) before every APK build.
        /// </summary>
        public static List<string> QuestRequirementProblems(List<string> passed = null)
        {
            var problems = new List<string>();
            void Req(bool ok, string what) => (ok ? passed : problems)?.Add(what);

            Req(PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) == ScriptingImplementation.IL2CPP, "IL2CPP");
            Req(PlayerSettings.Android.targetArchitectures == AndroidArchitecture.ARM64, "ARM64 only");
            Req(PlayerSettings.Android.minSdkVersion == MinSdk, "min API 32");
            Req((PlayerSettings.Android.textureCompressionFormats ?? Array.Empty<TextureCompressionFormat>())
                .SequenceEqual(new[] { TextureCompressionFormat.ASTC }), "ASTC");
            Req(PlayerSettings.GetGraphicsAPIs(BuildTarget.Android).SequenceEqual(new[] { GraphicsDeviceType.Vulkan }), "Vulkan only");
            Req(PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android) == PackageId, "package " + PackageId);

            var general = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
            var loaders = general?.Manager?.activeLoaders?.Where(l => l != null).ToList() ?? new List<XRLoader>();
            Req(loaders.Count == 1 && loaders[0] is OpenXRLoader, "XR loader: OpenXR only");

            var android = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (android == null) problems.Add("no Android OpenXR settings (Android module missing?)");
            else
            {
                Req(android.renderMode == OpenXRSettings.RenderMode.SinglePassInstanced, "multiview (single-pass instanced)");
                var metaXR = FeatureHelpers.GetFeaturesWithIdsForBuildTarget(BuildTargetGroup.Android, new[] { MetaXRFeatureId }).FirstOrDefault();
                Req(metaXR != null && metaXR.enabled, "Meta XR feature on");
                var quest = android.GetFeature<MetaQuestFeature>();
                Req(quest != null && quest.enabled, "Meta Quest Support on");
                var stillOn = FeatureHelpers.GetFeaturesWithIdsForBuildTarget(BuildTargetGroup.Android, UnityMetaFeaturesOff)
                    .Where(f => f != null && f.enabled).Select(f => f.GetType().Name).ToList();
                Req(stillOn.Count == 0, "Unity OpenXR: Meta camera/planes/anchors/meshing/bounding boxes/raycast off" +
                                        (stillOn.Count > 0 ? " (still on: " + string.Join(", ", stillOn) + ")" : ""));
            }
            foreach (var group in new[] { BuildTargetGroup.Android, BuildTargetGroup.Standalone })
            {
                if (OpenXRSettings.GetSettingsForBuildTargetGroup(group) == null) continue; // reported above for Android
                var depthOff = UnityMetaFeaturesOn
                    .Where(id => !FeatureHelpers.GetFeaturesWithIdsForBuildTarget(group, new[] { id }).Any(f => f != null && f.enabled))
                    .ToList();
                Req(depthOff.Count == 0, $"Meta Quest: Session + Occlusion on ({group})" +
                                         (depthOff.Count > 0 ? " (off: " + string.Join(", ", depthOff) + ")" : ""));
            }

            var cfg = OVRProjectConfig.CachedProjectConfig;
            var mode = CurrentBoundaryMode;
            Req(cfg.sceneSupport == OVRProjectConfig.FeatureSupport.Required, "Meta Scene Support = Required (USE_SCENE, V1)");
            Req(cfg.anchorSupport == OVRProjectConfig.AnchorSupport.Enabled, "Meta Anchor Support = Enabled (USE_ANCHOR_API)");
            Req(cfg.targetDeviceTypes.Contains(OVRProjectConfig.DeviceType.Quest3) && cfg.targetDeviceTypes.Contains(OVRProjectConfig.DeviceType.Quest3S),
                "target devices include Quest 3 and Quest 3S (Depth API)");
            Req(cfg.insightPassthroughSupport != OVRProjectConfig.FeatureSupport.None, "Meta passthrough support");
            Req(cfg.handTrackingSupport != OVRProjectConfig.HandTrackingSupport.ControllersOnly, "Meta hand tracking support");
            Req((cfg.boundaryVisibilitySupport != OVRProjectConfig.FeatureSupport.None) == (mode == BoundaryMode.Contextual),
                $"boundary visibility support matches the toggle ({mode})");

            if (!File.Exists(CustomManifestPath)) problems.Add(CustomManifestPath + " missing");
            else
            {
                var manifest = File.ReadAllText(CustomManifestPath);
                Req(manifest.Contains(UseScenePermission), CustomManifestPath + " has USE_SCENE");
                Req(manifest.Contains(BoundaryVisibilityPermission) == (mode == BoundaryMode.Contextual),
                    CustomManifestPath + $" boundary permission matches the toggle ({mode})");
            }

            if (File.Exists(BaseScenePath))
            {
                var template = File.ReadAllText(BaseScenePath);
                // A prefab-instance override in the template (the rig is an OVRCameraRig prefab instance).
                Req(System.Text.RegularExpressions.Regex.IsMatch(template,
                        @"propertyPath: requestScenePermissionOnStartup\s+value: 1"),
                    "rig template's OVRManager requests the scene permission (the only requester)");
            }

            var nws = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.endel.nativewebsocket/package.json");
            Req(nws != null && nws.source == UnityEditor.PackageManager.PackageSource.Embedded,
                "NativeWebSocket vendored under Packages/ (embedded)" + (nws != null ? $" [source={nws.source}]" : " [not found]"));
            return problems;
        }

        static void CheckQuestRequirements()
        {
            var passed = new List<string>();
            var problems = QuestRequirementProblems(passed);
            Log($"S1.10 check: {passed.Count} OK: {string.Join("; ", passed)}");
            if (problems.Count > 0) throw new Exception("S1.10 check FAILED: " + string.Join("; ", problems));
            Log("S1.10 check: PASS");
        }

        // ------------------------------------------------------------------ Meta Project Setup Tool

        // OVRProjectSetup's fixer API is internal (only FixAllAsync is public, and it needs editor
        // updates to make progress), so this walks the same task registry by reflection and calls each
        // task's own Fix(). Required + Recommended only: Optional tasks are left alone.
        static void RunMetaProjectSetupFixes(BuildTargetGroup group)
        {
            var getTasks = GetSetupTasksMethod();

            int totalFixed = 0;
            for (int pass = 1; pass <= 3; pass++)
            {
                int fixedThisPass = 0;
                foreach (var task in ((IEnumerable)getTasks.Invoke(null, new object[] { group })).Cast<object>().ToList())
                {
                    if (!TaskWantsFix(task, group, out var level, out var message)) continue;
                    if (TaskProp(task, "FixAction") == null) continue; // manual or async-only: reported below
                    if (NeverAutoFixSetupTasks.Any(m => message.StartsWith(m, StringComparison.Ordinal))) continue;
                    try
                    {
                        var ok = (bool)task.GetType().GetMethod("Fix", new[] { typeof(BuildTargetGroup) }).Invoke(task, new object[] { group });
                        if (ok)
                        {
                            fixedThisPass++;
                            Log($"Meta setup fixed [{level}] {message}");
                        }
                    }
                    catch (Exception e)
                    {
                        s_Notes.Add($"Meta setup fix threw for [{level}] {message}: {e.InnerException?.Message ?? e.Message}");
                    }
                }
                totalFixed += fixedThisPass;
                if (fixedThisPass == 0) break;
            }
            Log($"Meta Project Setup Tool ({group}): {totalFixed} fix(es) applied");

            var remaining = new StringBuilder();
            foreach (var task in ((IEnumerable)getTasks.Invoke(null, new object[] { group })).Cast<object>())
            {
                if (!TaskWantsFix(task, group, out var level, out var message)) continue;
                var how = NeverAutoFixSetupTasks.Any(m => message.StartsWith(m, StringComparison.Ordinal)) ? "not auto-fixed by the bootstrap"
                    : TaskProp(task, "FixAction") != null ? "auto-fix did not stick"
                    : TaskProp(task, "AsyncFixAction") != null ? "async fix: use the Project Setup Tool"
                    : "manual";
                remaining.AppendLine($"  [{level}] ({how}) {message}");
            }
            if (remaining.Length > 0)
                s_Notes.Add($"Meta Project Setup Tool ({group}) items still open:\n{remaining.ToString().TrimEnd()}");
        }

        /// <summary>Valid, not done, not ignored, Required or Recommended.</summary>
        static bool TaskWantsFix(object task, BuildTargetGroup group, out string level, out string message)
        {
            level = "?";
            message = "?";
            try
            {
                var levelValue = Convert.ToInt32(LambdaValue(TaskProp(task, "Level"), group)); // Hidden -1, Optional 0, Recommended 1, Required 2
                level = levelValue == 2 ? "Required" : levelValue == 1 ? "Recommended" : levelValue.ToString();
                message = (LambdaValue(TaskProp(task, "Message"), group) as string ?? "?").Replace('\n', ' ');
                if (levelValue < 1) return false;
                var isDone = (Func<BuildTargetGroup, bool>)TaskProp(task, "IsDone");
                if (isDone(group)) return false;
                var ignored = (bool)task.GetType().GetMethod("IsIgnored", new[] { typeof(BuildTargetGroup) }).Invoke(task, new object[] { group });
                return !ignored;
            }
            catch (Exception e)
            {
                s_Notes.Add($"could not evaluate Meta setup task '{message}': {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        static object TaskProp(object task, string name) =>
            task.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(task);

        static object LambdaValue(object optionalLambda, BuildTargetGroup group) =>
            optionalLambda?.GetType().GetMethod("GetValue", new[] { typeof(BuildTargetGroup) })?.Invoke(optionalLambda, new object[] { group });

        // ------------------------------------------------------------------ OpenXR validation

        static void RunOpenXRValidation(BuildTargetGroup group)
        {
            if (OpenXRSettings.GetSettingsForBuildTargetGroup(group) == null) return; // module missing, noted above

            var issues = new List<OpenXRFeature.ValidationRule>();
            OpenXRProjectValidation.GetCurrentValidationIssues(issues, group);
            foreach (var issue in issues.Where(i => i.fixIt != null && i.fixItAutomatic).ToList())
            {
                try
                {
                    issue.fixIt();
                    Log($"OpenXR validation fixed: {issue.message}");
                }
                catch (Exception e)
                {
                    s_Notes.Add($"OpenXR validation fix threw for '{issue.message}': {e.Message}");
                }
            }

            // Some fixes (e.g. MetaQuestFeature's latency optimization) change OpenXRSettings without SetDirty, so the
            // value would be lost and "fixed" again on every run. Mark everything the validator may touch.
            var settingsAfter = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            EditorUtility.SetDirty(settingsAfter);
            foreach (var f in settingsAfter.GetFeatures()) if (f != null) EditorUtility.SetDirty(f);

            OpenXRProjectValidation.GetCurrentValidationIssues(issues, group);
            foreach (var issue in issues)
                s_Notes.Add($"OpenXR validation ({group}) {(issue.error ? "ERROR" : "warning")}: {issue.message}");
        }

        // ------------------------------------------------------------------ save / summary / target

        static void SaveAll()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid() && scene.isDirty && scene.path == BaseScenePath)
                EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
        }

        static void LogSummary()
        {
            var s = new StringBuilder();
            s.AppendLine($"product='{PlayerSettings.productName}' company='{PlayerSettings.companyName}' version={PlayerSettings.bundleVersion}");
            s.AppendLine($"android id={PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android)} " +
                         $"backend={PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android)} arch={PlayerSettings.Android.targetArchitectures} " +
                         $"minSdk={(int)PlayerSettings.Android.minSdkVersion} targetSdk={(int)PlayerSettings.Android.targetSdkVersion} " +
                         $"internet={(PlayerSettings.Android.forceInternetPermission ? "Require" : "Auto")} entry={PlayerSettings.Android.applicationEntry}");
            s.AppendLine($"colorSpace={PlayerSettings.colorSpace} androidGfx=[{string.Join(",", PlayerSettings.GetGraphicsAPIs(BuildTarget.Android))}] " +
                         $"stereo={PlayerSettings.stereoRenderingPath} insecureHttp={PlayerSettings.insecureHttpOption}");
            s.AppendLine($"renderPipeline={AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline)} " +
                         $"spatializer='{GetSerialized("ProjectSettings/AudioManager.asset", "m_SpatializerPlugin")}' " +
                         $"activeInputHandler={GetSerialized("ProjectSettings/ProjectSettings.asset", "activeInputHandler")}");
            foreach (var group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
            {
                var general = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
                var loaders = general?.Manager?.activeLoaders?.Where(l => l != null).Select(l => l.GetType().Name) ?? Enumerable.Empty<string>();
                var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
                var features = settings == null ? "(no OpenXR settings)"
                    : string.Join(", ", settings.GetFeatures().Where(f => f.enabled).Select(f => f.GetType().Name));
                s.AppendLine($"{group}: loaders=[{string.Join(",", loaders)}] openxrFeatures=[{features}]" +
                             (settings != null ? $" renderMode={settings.renderMode}" : ""));
            }
            var cfg = OVRProjectConfig.CachedProjectConfig;
            s.AppendLine($"meta: devices=[{string.Join(",", cfg.targetDeviceTypes)}] hands={cfg.handTrackingSupport} " +
                         $"passthrough={cfg.insightPassthroughSupport} scene={cfg.sceneSupport} anchors={cfg.anchorSupport} " +
                         $"boundaryVisibility={cfg.boundaryVisibilitySupport} loadingScreen={cfg.systemLoadingScreenBackground} nsc={cfg.enableNSCConfig}");
            PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Android, out string[] androidDefines);
            s.AppendLine($"boundary toggle={CurrentBoundaryMode} androidDefines=[{string.Join(";", androidDefines)}] " +
                         $"textures=[{string.Join(",", PlayerSettings.Android.textureCompressionFormats ?? Array.Empty<TextureCompressionFormat>())}]");
            s.Append($"build scenes=[{string.Join(",", EditorBuildSettings.scenes.Select(x => x.path))}]");
            Log("summary:\n" + s);
        }

        static void SwitchToAndroidIfPossible()
        {
            if (!AndroidSupported)
            {
                s_Notes.Add($"build target stays {EditorUserBuildSettings.activeBuildTarget}: Android module not installed yet");
                return;
            }
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android) return;
            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
                throw new Exception("SwitchActiveBuildTarget(Android) returned false");
            Log("switched the active build target to Android. Run the bootstrap once more so the Android-only steps run under UNITY_ANDROID.");
        }

        // ------------------------------------------------------------------ helpers

        static void Step(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                s_Failures.Add(name);
                Debug.LogError($"{Tag} step '{name}' failed: {e}");
            }
        }

        static void Log(string msg) => Debug.Log($"{Tag} {msg}");

        static void SetSerialized(string assetPath, string property, Action<SerializedProperty> set)
        {
            var obj = AssetDatabase.LoadAllAssetsAtPath(assetPath).FirstOrDefault();
            if (obj == null) throw new Exception($"{assetPath} not loadable");
            var so = new SerializedObject(obj);
            var p = so.FindProperty(property);
            if (p == null) throw new Exception($"{assetPath} has no property {property}");
            set(p);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static string GetSerialized(string assetPath, string property)
        {
            var obj = AssetDatabase.LoadAllAssetsAtPath(assetPath).FirstOrDefault();
            var p = obj == null ? null : new SerializedObject(obj).FindProperty(property);
            if (p == null) return "?";
            return p.propertyType == SerializedPropertyType.String ? p.stringValue : p.intValue.ToString();
        }

        static void SetIfExists(SerializedObject so, string property, Action<SerializedProperty> set)
        {
            var p = so.FindProperty(property);
            if (p != null) set(p);
            else s_Notes.Add($"{so.targetObject.GetType().Name} has no serialized field '{property}' (SDK changed?)");
        }

        static void EnsureFolder(string path)
        {
            path = path.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static Type FindType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t != null);
    }

    /// <summary>
    /// Meta XR Core's DevAgentBuildProcessor writes the build machine's LAN IP and its agent-bridge access token into
    /// Assets/Resources/DevAgentSettings.asset (a tracked file, in a public repo) on every build of any platform, and its
    /// own post-build restore doesn't stick. The DevAgent is off (enabled: 0), so this clears both after every build,
    /// after Meta's hooks. (GameBuild.BuildApk also clears them when an APK build fails.)
    /// </summary>
    class DevAgentSettingsScrub : IPostprocessBuildWithReport
    {
        public const string AssetPath = "Assets/Resources/DevAgentSettings.asset";
        public int callbackOrder => int.MaxValue;

        public void OnPostprocessBuild(BuildReport report) => Scrub();

        public static bool Scrub()
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(AssetPath);
            if (asset == null) return false;
            var so = new SerializedObject(asset);
            bool changed = false;
            foreach (var name in new[] { "accessToken", "serverAddress" })
            {
                var p = so.FindProperty(name);
                if (p == null || p.propertyType != SerializedPropertyType.String || p.stringValue.Length == 0) continue;
                p.stringValue = "";
                changed = true;
            }
            if (!changed) return false;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(asset);
            Debug.Log($"[VBSBootstrap] cleared the Meta DevAgent access token and LAN address from {AssetPath} (tracked file)");
            return true;
        }
    }

#if UNITY_ANDROID
    /// <summary>
    /// Runs after Meta's OVRGradleGeneration (callbackOrder 99999) on the generated unityLibrary
    /// manifest: makes sure RECORD_AUDIO (hum + voice) and INTERNET (the Hive) are there, and allows cleartext
    /// (android:usesCleartextTraffic="true": the ws://LAN-IP fallback and the http /health probe; Unity 6 doesn't add
    /// it for insecureHttpOption = AlwaysAllowed, and targetSdk 34 blocks cleartext by default). With the
    /// boundary toggle on "app" (define VBS_BOUNDARYLESS_APP, menu Virtual Bee Swarm > Boundary) it also
    /// declares com.oculus.feature.BOUNDARYLESS_APP (fully boundaryless; mutually exclusive with the
    /// contextual Boundary API route). GameBuild verifies the result with aapt2.
    /// </summary>
    class GameManifestPatch : IPostGenerateGradleAndroidProject
    {
        const string AndroidNs = "http://schemas.android.com/apk/res/android";
        const string ToolsNs = "http://schemas.android.com/tools";

        public int callbackOrder => 100000;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var file = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(file))
            {
                Debug.LogWarning($"[VBSBootstrap] no manifest at {file}; RECORD_AUDIO/INTERNET not verified");
                return;
            }

            var doc = new XmlDocument();
            doc.Load(file);
            var root = doc.DocumentElement;
            bool changed = Ensure(doc, root, "uses-permission", "android.permission.RECORD_AUDIO", null);
            changed |= Ensure(doc, root, "uses-permission", "android.permission.INTERNET", null);
            changed |= EnsureApplicationAttribute(root, "usesCleartextTraffic", "true");
#if VBS_BOUNDARYLESS_APP
            changed |= Ensure(doc, root, "uses-feature", "com.oculus.feature.BOUNDARYLESS_APP", "true");
#endif
            if (changed)
            {
                doc.Save(file);
                Debug.Log($"[VBSBootstrap] patched {file}");
            }
        }

        static bool EnsureApplicationAttribute(XmlElement root, string attribute, string value)
        {
            var app = root.GetElementsByTagName("application").OfType<XmlElement>().FirstOrDefault();
            if (app == null)
            {
                Debug.LogWarning($"[VBSBootstrap] no <application> in the Gradle manifest; {attribute} not set");
                return false;
            }
            if (app.GetAttribute(attribute, AndroidNs) == value) return false;
            app.SetAttribute(attribute, AndroidNs, value);
            return true;
        }

        static bool Ensure(XmlDocument doc, XmlElement root, string tag, string name, string required)
        {
            foreach (XmlElement e in root.GetElementsByTagName(tag))
            {
                if (e.GetAttribute("name", AndroidNs) != name) continue;
                if (e.GetAttribute("node", ToolsNs) == "remove")
                {
                    e.RemoveAttribute("node", ToolsNs);
                    return true;
                }
                return false;
            }
            var el = doc.CreateElement(tag);
            el.SetAttribute("name", AndroidNs, name);
            if (required != null) el.SetAttribute("required", AndroidNs, required);
            root.AppendChild(el);
            return true;
        }
    }
#endif
}
#endif
