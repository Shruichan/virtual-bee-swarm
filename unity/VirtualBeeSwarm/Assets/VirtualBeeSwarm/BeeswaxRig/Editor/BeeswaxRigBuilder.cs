using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VirtualBeeSwarm.Editor
{
    /// <summary>
    /// Builds Daniel's rigged Beeswax into the Beeswax art slot, everything from code (no hand-made assets):
    ///   1. Art/Beeswax/Rig/Beeswax.fbx (copied from art/blender/beeswax/Beeswax.fbx when that one differs): Generic rig,
    ///      21 clips named after his actions, Loop Time on BeeswaxRig.LoopClips (a baked duplicate end frame is trimmed),
    ///      his materials remapped to BeeswaxRig/Generated/Materials (VirtualBeeSwarm/BeeswaxToon, his colours).
    ///   2. BeeswaxRig/Generated/Masks/*.mask (one Generic AvatarMask per override layer, BeeswaxRig.Masks) and
    ///      BeeswaxRig/Generated/Beeswax.controller (base Hover + the masked override layers, BeeswaxRig.States).
    ///   3. Art/Beeswax/Beeswax.prefab: Beeswax / Body (BeeswaxController bobs it) / Model (his FBX, unpacked, Animator +
    ///      BeeswaxRig, turned to face +Z and scaled to the temp Beeswax's size) + empty Body/Mouth, Body/WingL,
    ///      Body/WingR (the slot's procedural targets) + CaptionAnchor. ArtSlots dresses the scene with it as before.
    ///   4. The temp Art/Beeswax/beeswax.fbx is deleted, so ArtImport's "Rebuild art prefabs" can't put it back.
    ///   5. The music bed clip (Audio/Music/Resources/VBS_MusicLoop02.ogg): Vorbis, streaming.
    /// Menu: Virtual Bee Swarm > Art > Build Beeswax rig. Batch: -executeMethod VirtualBeeSwarm.Editor.BeeswaxRigBuilder.BuildBatch
    /// (VBS_BEESWAX_FBX = another source FBX). Then rebuild the scene. Logs "[VBS][BeeswaxRig] ...".
    /// </summary>
    public static class BeeswaxRigBuilder
    {
        public static string RigDir => ArtSlots.VbsRoot + "/BeeswaxRig";
        public static string GenDir => RigDir + "/Generated";
        public static string ControllerPath => GenDir + "/Beeswax.controller";
        public static string FbxPath => ArtSlots.ArtRoot + "/Beeswax/Rig/Beeswax.fbx";
        public static string MusicPath => ArtSlots.VbsRoot + "/Audio/Music/Resources/" + MusicBed.ClipResource + ".ogg";
        static string TempFbx => ArtSlots.ArtRoot + "/Beeswax/beeswax.fbx";

        // The temp Beeswax prefab (Art/Beeswax/Beeswax.prefab before this builder, at prefab scale; ArtSlots scales the
        // slot by 0.55 in the scene). Measured from it on the first run (2026-11-14); used when it's gone.
        static readonly Vector3 TempSize = new Vector3(0.3023f, 0.3845f, 0.2423f);
        static readonly Vector3 TempCenter = new Vector3(0f, 0.0507f, -0.0128f);
        static readonly Vector3 TempCaption = new Vector3(0f, 0.27f, 0f);

        /// <summary>His Blender colours (linear, Principled base colour; Peach Blush from its viewport colour, see below).</summary>
        static readonly (string name, Color linear, bool twoSided, float shade, float rim)[] Materials =
        {
            ("Marigold", new Color(1f, 0.624f, 0.041f), false, 0.38f, 0.16f),
            ("Cocoa", new Color(0.125f, 0.04f, 0.01f), false, 0.38f, 0.16f),
            ("White Wings", new Color(1f, 1f, 1f), true, 0.22f, 0.25f),
            // the node's Base Color is the default 0.8 grey (only the viewport colour is peach), so the FBX says grey
            ("Peach Blush", new Color(0.82f, 0.21f, 0.15f), true, 0.1f, 0f),
            ("Temporary Eye Cocoa", new Color(0.032f, 0.012f, 0.006f), true, 0.2f, 0.12f),
        };

        [MenuItem("Virtual Bee Swarm/Art/Build Beeswax rig (Daniel's FBX)")]
        static void Menu() => Build();

        public static void BuildBatch()
        {
            try
            {
                Build();
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[VBS][BeeswaxRig] build FAILED: " + e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        public static string Build()
        {
            EnsureFolder(GenDir + "/Materials");
            EnsureFolder(GenDir + "/Masks");
            EnsureFolder(Path.GetDirectoryName(FbxPath).Replace('\\', '/'));

            // what the temp art looked like (size, centre, caption height), before the prefab is replaced
            MeasureTemp(out var tempSize, out var tempCenter, out var tempCaption);

            CopySource();
            var mats = EnsureMaterials();
            ConfigureModel(mats);
            var clips = LoadClips();
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath) ?? throw new Exception(FbxPath + " did not import as a model");
            var masks = BuildMasks(model);
            var ctrl = BuildController(clips, masks);
            var prefab = BuildPrefab(model, ctrl, tempSize, tempCenter, tempCaption, out string fit);
            if (File.Exists(TempFbx))
            {
                AssetDatabase.DeleteAsset(TempFbx);
                Debug.Log("[VBS][BeeswaxRig] deleted the temp " + TempFbx + " (ArtImport would rebuild the slot from it)");
            }
            ConfigureMusic();
            AssetDatabase.SaveAssets();

            string summary = Describe(prefab, clips, masks) + "; " + fit;
            Debug.Log("[VBS][BeeswaxRig] built " + ArtSlots.PrefabPath("Beeswax") + ": " + summary);
            return summary;
        }

        // ------------------------------------------------------------------------------------------ source
        static void CopySource()
        {
            string src = Environment.GetEnvironmentVariable("VBS_BEESWAX_FBX");
            if (string.IsNullOrEmpty(src))
                src = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../art/blender/beeswax/Beeswax.fbx"));
            if (!File.Exists(src))
            {
                if (!File.Exists(FbxPath)) throw new FileNotFoundException("no Beeswax FBX (set VBS_BEESWAX_FBX)", src);
                Debug.Log("[VBS][BeeswaxRig] source " + src + " not here, using " + FbxPath + " as is");
                return;
            }
            if (File.Exists(FbxPath) && File.ReadAllBytes(src).SequenceEqual(File.ReadAllBytes(FbxPath))) return;
            File.Copy(src, FbxPath, true);
            AssetDatabase.ImportAsset(FbxPath, ImportAssetOptions.ForceSynchronousImport);
            Debug.Log("[VBS][BeeswaxRig] copied " + src + " -> " + FbxPath);
        }

        static void MeasureTemp(out Vector3 size, out Vector3 center, out Vector3 caption)
        {
            size = TempSize;
            center = TempCenter;
            caption = TempCaption;
            var old = ArtSlots.Prefab("Beeswax");
            if (old == null || old.GetComponentInChildren<BeeswaxRig>(true) != null) return;
            var b = Bounds(old.transform, old.GetComponentsInChildren<Renderer>(true));
            if (b.size == Vector3.zero) return;
            size = b.size;
            center = b.center;
            var cap = ArtSlots.FindPath(old.transform, "CaptionAnchor");
            if (cap != null) caption = old.transform.InverseTransformPoint(cap.position);
            Debug.Log("[VBS][BeeswaxRig] temp Beeswax prefab: size " + V(size) + ", center " + V(center) + ", caption anchor " + V(caption));
        }

        // ------------------------------------------------------------------------------------------ import
        static Dictionary<string, Material> EnsureMaterials()
        {
            var shader = Shader.Find("VirtualBeeSwarm/BeeswaxToon");
            if (shader == null) throw new Exception("shader VirtualBeeSwarm/BeeswaxToon is missing (BeeswaxRig/BeeswaxToon.shader)");
            var result = new Dictionary<string, Material>();
            foreach (var (name, linear, twoSided, shade, rim) in Materials)
            {
                string path = GenDir + "/Materials/Beeswax_" + name.Replace(" ", "") + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                bool created = mat == null;
                if (created) mat = new Material(shader);
                mat.shader = shader;
                mat.name = Path.GetFileNameWithoutExtension(path);
                mat.SetColor("_Albedo", linear.gamma); // Color properties are sRGB; his values are Blender's linear ones
                mat.SetColor("_Color", Color.white);
                mat.SetFloat("_Shade", shade);
                mat.SetFloat("_Rim", rim);
                mat.SetFloat("_Cull", twoSided ? (float)UnityEngine.Rendering.CullMode.Off : (float)UnityEngine.Rendering.CullMode.Back);
                mat.enableInstancing = false; // skinned: never instanced
                if (created) AssetDatabase.CreateAsset(mat, path);
                else EditorUtility.SetDirty(mat);
                result[name] = mat;
            }
            return result;
        }

        static void ConfigureModel(Dictionary<string, Material> mats)
        {
            var mi = (ModelImporter)AssetImporter.GetAtPath(FbxPath) ?? throw new Exception(FbxPath + " has no ModelImporter");
            mi.globalScale = 1f;
            mi.useFileScale = true;
            mi.bakeAxisConversion = false;
            mi.preserveHierarchy = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importVisibility = false;
            mi.importBlendShapes = false;
            mi.importConstraints = false;
            mi.addCollider = false;
            mi.isReadable = false;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.importNormals = ModelImporterNormals.Import;
            mi.importTangents = ModelImporterTangents.None;
            mi.generateSecondaryUV = false;
            mi.animationType = ModelImporterAnimationType.Generic;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.optimizeGameObjects = false;          // ArtSlots + the masks need the bone transforms
            mi.motionNodeName = "";
            mi.importAnimation = true;
            mi.importAnimatedCustomProperties = false;
            mi.resampleCurves = true;
            mi.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
            mi.animationRotationError = 0.2f;
            mi.animationPositionError = 0.2f;
            mi.animationScaleError = 0.2f;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
            foreach (var kv in mats) mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
            mi.SaveAndReimport();

            // clips: his action names, Loop Time on the sustained ones
            var defs = mi.defaultClipAnimations;
            if (defs == null || defs.Length == 0) throw new Exception(FbxPath + " has no animation takes");
            var list = new List<ModelImporterClipAnimation>();
            foreach (var d in defs)
            {
                string shortName = d.takeName.Substring(d.takeName.LastIndexOf('|') + 1).Trim();
                d.name = shortName;
                d.loopTime = BeeswaxRig.LoopClips.Contains(shortName);
                d.loopPose = false;
                d.lockRootRotation = d.lockRootHeightY = d.lockRootPositionXZ = false;
                list.Add(d);
            }
            mi.clipAnimations = list.ToArray();
            mi.SaveAndReimport();

            // a baked loop often repeats its first frame at the end: drop it, or the loop holds one frame
            bool trimmed = false;
            var byName = AssetDatabase.LoadAllAssetsAtPath(FbxPath).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__"))
                                      .ToDictionary(c => c.name);
            var clipsNow = mi.clipAnimations;
            var trimmedNames = new List<string>();
            foreach (var c in clipsNow)
            {
                if (!c.loopTime || !byName.TryGetValue(c.name, out var clip)) continue;
                if (c.lastFrame - c.firstFrame < 4) continue;
                if (MaxEndDifference(clip) < 1e-3f)
                {
                    c.lastFrame -= 1f;
                    trimmed = true;
                    trimmedNames.Add(c.name);
                }
            }
            if (trimmed)
            {
                mi.clipAnimations = clipsNow;
                mi.SaveAndReimport();
                Debug.Log("[VBS][BeeswaxRig] loop clips with a repeated end frame, trimmed by one: " + string.Join(", ", trimmedNames));
            }
        }

        static float MaxEndDifference(AnimationClip clip)
        {
            float max = 0f;
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                if (curve == null || curve.length == 0) continue;
                max = Mathf.Max(max, Mathf.Abs(curve.Evaluate(0f) - curve.Evaluate(clip.length)));
            }
            return max;
        }

        static Dictionary<string, AnimationClip> LoadClips()
        {
            var clips = AssetDatabase.LoadAllAssetsAtPath(FbxPath).OfType<AnimationClip>()
                                     .Where(c => !c.name.StartsWith("__preview__")).ToDictionary(c => c.name);
            var missing = BeeswaxRig.States.Select(s => s.clip).Where(n => !clips.ContainsKey(n)).ToList();
            if (missing.Count > 0) throw new Exception(FbxPath + " is missing clip(s): " + string.Join(", ", missing) + " (has: " + string.Join(", ", clips.Keys) + ")");
            return clips;
        }

        // ------------------------------------------------------------------------------------------ masks + controller
        static Dictionary<BeeswaxRig.Layer, AvatarMask> BuildMasks(GameObject model)
        {
            var root = model.transform;
            var armature = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Root")?.parent
                           ?? throw new Exception("no Root bone in " + FbxPath);
            var all = root.GetComponentsInChildren<Transform>(true).Where(t => t != root).ToList();
            var result = new Dictionary<BeeswaxRig.Layer, AvatarMask>();
            foreach (var kv in BeeswaxRig.Masks)
            {
                var want = new HashSet<string>(kv.Value);
                string path = GenDir + "/Masks/Beeswax_" + kv.Key + ".mask";
                var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
                bool created = mask == null;
                if (created) mask = new AvatarMask();
                for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++) mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i, false);
                mask.transformCount = all.Count;
                var found = new HashSet<string>();
                for (int i = 0; i < all.Count; i++)
                {
                    var t = all[i];
                    bool isBone = t.IsChildOf(armature) && t != armature;
                    bool on = isBone && want.Contains(t.name);
                    if (on) found.Add(t.name);
                    mask.SetTransformPath(i, AnimationUtility.CalculateTransformPath(t, root));
                    mask.SetTransformActive(i, on);
                }
                var missing = want.Where(n => !found.Contains(n)).ToList();
                if (missing.Count > 0) throw new Exception("mask " + kv.Key + ": no bone(s) " + string.Join(", ", missing));
                if (created) AssetDatabase.CreateAsset(mask, path);
                else EditorUtility.SetDirty(mask);
                result[kv.Key] = mask;
            }
            return result;
        }

        static AnimatorController BuildController(Dictionary<string, AnimationClip> clips, Dictionary<BeeswaxRig.Layer, AvatarMask> masks)
        {
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (ctrl == null) ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            // rebuilt in place (same asset, same GUID): drop every layer but the base, every state and parameter
            while (ctrl.layers.Length > 1) ctrl.RemoveLayer(ctrl.layers.Length - 1);
            foreach (var p in ctrl.parameters) ctrl.RemoveParameter(p);
            var baseSm = ctrl.layers[0].stateMachine;
            foreach (var s in baseSm.states) baseSm.RemoveState(s.state);
            ctrl.AddParameter(new AnimatorControllerParameter
            {
                name = BeeswaxRig.FaceSpeedParam, type = AnimatorControllerParameterType.Float, defaultFloat = 1f,
            });

            var layerNames = Enum.GetNames(typeof(BeeswaxRig.Layer));
            var layers = ctrl.layers;
            layers[0].name = layerNames[0];
            ctrl.layers = layers;
            for (int i = 1; i < layerNames.Length; i++) ctrl.AddLayer(layerNames[i]);
            layers = ctrl.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                var layer = (BeeswaxRig.Layer)i;
                var sm = layers[i].stateMachine;
                if (i > 0)
                {
                    layers[i].avatarMask = masks[layer];
                    layers[i].blendingMode = AnimatorLayerBlendingMode.Override;
                    layers[i].defaultWeight = 0f;            // BeeswaxRig fades each layer in when it plays something
                    var idle = sm.AddState(BeeswaxRig.IdleState, new Vector3(250f, 0f, 0f));
                    idle.writeDefaultValues = false;
                    sm.defaultState = idle;
                }
                int n = 0;
                foreach (var (l, clip) in BeeswaxRig.States)
                {
                    if (l != layer) continue;
                    var st = sm.AddState(clip, new Vector3(250f, 70f * ++n, 0f));
                    st.motion = clips[clip];
                    st.writeDefaultValues = false;
                    if (layer == BeeswaxRig.Layer.Face)
                    {
                        st.speedParameterActive = true;
                        st.speedParameter = BeeswaxRig.FaceSpeedParam;
                    }
                    if (layer == BeeswaxRig.Layer.Base && clip == "Hover") sm.defaultState = st;
                }
            }
            ctrl.layers = layers;
            EditorUtility.SetDirty(ctrl);
            return ctrl;
        }

        // ------------------------------------------------------------------------------------------ prefab
        static GameObject BuildPrefab(GameObject modelAsset, AnimatorController ctrl, Vector3 tempSize, Vector3 tempCenter, Vector3 tempCaption, out string fit)
        {
            var root = new GameObject("Beeswax");
            try
            {
                var body = new GameObject("Body").transform;
                body.SetParent(root.transform, false);
                // BeeswaxController's procedural targets (ArtSlots maps Body/Mouth, Body/WingL, Body/WingR here). Empty: the
                // Animator drives his real wings and mouth; a controller without RigDriven moves only these.
                foreach (var n in new[] { "Mouth", "WingL", "WingR" }) new GameObject(n).transform.SetParent(body, false);

                var model = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
                PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                model.name = "Model";
                model.transform.SetParent(body, false);

                // face +Z: head ahead of the thorn
                var head = ArtSlots.FindPath(model.transform, "Head") ?? throw new Exception("no Head bone");
                var thorn = ArtSlots.FindPath(model.transform, "Thorn") ?? throw new Exception("no Thorn bone");
                Vector3 fwd = head.position - thorn.position;
                fwd.y = 0f;
                float yaw = Vector3.SignedAngle(fwd, Vector3.forward, Vector3.up);
                model.transform.localRotation = Quaternion.Euler(0f, yaw, 0f) * model.transform.localRotation;
                var tip = ArtSlots.FindPath(model.transform, "Antenna_Tip.L");
                var bodyBone = ArtSlots.FindPath(model.transform, "Body");
                bool upright = tip != null && bodyBone != null && tip.position.y > bodyBone.position.y;

                // size: as big as the temp Beeswax (largest side), centred where it was
                var renderers = model.GetComponentsInChildren<Renderer>(true);
                var b = Bounds(root.transform, renderers);
                float s = Mathf.Max(tempSize.x, tempSize.y, tempSize.z) / Mathf.Max(1e-5f, Mathf.Max(b.size.x, b.size.y, b.size.z));
                model.transform.localScale *= s;
                b = Bounds(root.transform, renderers);
                model.transform.localPosition += tempCenter - b.center;
                b = Bounds(root.transform, renderers);

                var cap = new GameObject("CaptionAnchor").transform;
                cap.SetParent(root.transform, false);
                cap.localPosition = new Vector3(0f, Mathf.Max(tempCaption.y, b.max.y + 0.02f), 0f);

                foreach (var r in renderers)
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                    r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                    r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                    r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                    if (r is SkinnedMeshRenderer smr)
                    {
                        smr.updateWhenOffscreen = false;
                        smr.skinnedMotionVectors = false;
                        smr.quality = SkinQuality.Bone2;
                    }
                }

                var anim = model.GetComponent<Animator>() ?? model.AddComponent<Animator>();
                anim.runtimeAnimatorController = ctrl;
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                anim.updateMode = AnimatorUpdateMode.Normal;
                var rig = model.GetComponent<BeeswaxRig>() ?? model.AddComponent<BeeswaxRig>();
                var so = new SerializedObject(rig);
                so.FindProperty("animator").objectReferenceValue = anim;
                so.ApplyModifiedPropertiesWithoutUndo();

                fit = "model yaw " + yaw.ToString("0", CultureInfo.InvariantCulture) + ", scale x" + s.ToString("0.###", CultureInfo.InvariantCulture) +
                      " (size " + V(b.size) + " vs temp " + V(tempSize) + "), " + (upright ? "upright" : "NOT UPRIGHT (antenna below body)") +
                      ", caption at " + cap.localPosition.y.ToString("0.000", CultureInfo.InvariantCulture);
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, ArtSlots.PrefabPath("Beeswax"), out bool ok);
                if (!ok) throw new Exception("SaveAsPrefabAsset failed");
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ------------------------------------------------------------------------------------------ music
        static void ConfigureMusic()
        {
            var imp = AssetImporter.GetAtPath(MusicPath) as AudioImporter;
            if (imp == null) { Debug.LogWarning("[VBS][BeeswaxRig] no music clip at " + MusicPath); return; }
            if (MusicImportSettings.Apply(imp)) imp.SaveAndReimport();
        }

        // ------------------------------------------------------------------------------------------ report
        static string Describe(GameObject prefab, Dictionary<string, AnimationClip> clips, Dictionary<BeeswaxRig.Layer, AvatarMask> masks)
        {
            int tris = 0, skinned = 0, bones = 0, subs = 0;
            foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                skinned++;
                subs += smr.sharedMesh.subMeshCount;
                tris += smr.sharedMesh.triangles.Length / 3;
                bones = Mathf.Max(bones, smr.bones.Length);
            }
            var loops = clips.Values.Where(c => c.isLooping).Select(c => c.name).OrderBy(n => n);
            return clips.Count + " clips (" + string.Join(", ", clips.Values.OrderBy(c => c.name).Select(c => c.name + " " + c.length.ToString("0.00", CultureInfo.InvariantCulture) + "s")) +
                   "), looping: " + string.Join(", ", loops) + "; " + skinned + " skinned renderers (" + subs + " submeshes = draw calls), " +
                   tris + " tris, " + bones + " bones; masks " + string.Join(", ", masks.Select(kv => kv.Key + "=" + BeeswaxRig.Masks[kv.Key].Length));
        }

        // ------------------------------------------------------------------------------------------ helpers
        static Bounds Bounds(Transform space, Renderer[] renderers)
        {
            var b = new Bounds();
            bool any = false;
            foreach (var r in renderers)
            {
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                var mb = mesh.bounds;
                if (mb.size.sqrMagnitude < 1e-12f) continue; // a face decal parked at scale 0 (Angry brows/frown)
                var m = space.worldToLocalMatrix * r.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(c);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            return b;
        }

        static string V(Vector3 v) => "(" + v.x.ToString("0.0000", CultureInfo.InvariantCulture) + ", " + v.y.ToString("0.0000", CultureInfo.InvariantCulture) + ", " +
                                      v.z.ToString("0.0000", CultureInfo.InvariantCulture) + ")";

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }

    /// <summary>The music bed clips (Audio/Music/...): Vorbis, streamed (no 60 s of PCM in memory), stereo kept.</summary>
    class MusicImportSettings : AssetPostprocessor
    {
        static bool IsMusic(string path) => path.Replace('\\', '/').Contains("/VirtualBeeSwarm/Audio/Music/");

        void OnPreprocessAudio()
        {
            if (IsMusic(assetPath) && assetImporter is AudioImporter imp) Apply(imp);
        }

        /// <summary>Returns true when something changed.</summary>
        public static bool Apply(AudioImporter imp)
        {
            bool changed = false;
            if (imp.forceToMono) { imp.forceToMono = false; changed = true; }
            if (!imp.loadInBackground) { imp.loadInBackground = true; changed = true; }
            var want = imp.defaultSampleSettings;
            want.loadType = AudioClipLoadType.Streaming;
            want.compressionFormat = AudioCompressionFormat.Vorbis;
            want.quality = 0.6f;
            want.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            want.preloadAudioData = false;
            var cur = imp.defaultSampleSettings;
            if (cur.loadType != want.loadType || cur.compressionFormat != want.compressionFormat || Mathf.Abs(cur.quality - want.quality) > 1e-3f ||
                cur.sampleRateSetting != want.sampleRateSetting || cur.preloadAudioData != want.preloadAudioData)
            {
                imp.defaultSampleSettings = want;
                changed = true;
            }
            if (imp.ContainsSampleSettingsOverride("Android"))
            {
                imp.ClearSampleSettingOverride("Android");
                changed = true;
            }
            return changed;
        }
    }
}
