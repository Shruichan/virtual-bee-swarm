using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;
using ArtSlots = VirtualBeeSwarm.Editor.ArtSlots;

namespace VirtualBeeSwarm.NewPropsEditor
{
    /// <summary>
    /// Imports dartiros' "Ten New Assets.blend" props (exported by art/blender/scripts/export_dart_props.py: one FBX per
    /// item + manifest.json) into Art/&lt;Slot&gt;/Model/ and makes Art/&lt;Slot&gt;/&lt;Slot&gt;.prefab (native size, front +Z, Legacy
    /// Animation with the Blender clips), then points NewProps/Resources/NewPropsKit.asset at them. The scene builder
    /// is untouched: NewProps installs itself at runtime from the kit.
    ///
    ///   Menu:  Virtual Bee Swarm > Art > Import dartiros new props...   (pick the export folder)
    ///   Batch: VBS_PROPS_DIR=&lt;export dir&gt; Unity -batchmode -nographics -quit -projectPath P
    ///          -executeMethod VirtualBeeSwarm.NewPropsEditor.NewPropsImport.ImportBatch
    ///   Stills (needs graphics, no -nographics): VBS_PROPS_STILLS=&lt;dir&gt; ... NewPropsImport.StillsBatch
    ///
    /// Materials: VirtualBeeSwarm/Unlit with the baked vertex colours, shared with dartiros' other art (Art/_Shared:
    /// VBS_DartVC opaque, VBS_DartWing for the queen's wings), one two-sided textured material per printed texture (badge
    /// emblem, flag prints, cage screen). The flags keep their shape keys (Flutter animates them). The art faces -Z.
    /// </summary>
    public static class NewPropsImport
    {
        // item -> slot (Art/<Slot>/<Slot>.prefab) and the NewPropsKit field it fills
        static readonly (string item, string slot, string field)[] Slots =
        {
            ("union_badge", "UnionBadge", "unionBadge"), ("hive_tool", "HiveTool", "hiveTool"), ("frame_rest", "FrameRest", "frameRest"),
            ("queen_bee", "QueenBee", "queenBee"), ("queen_cage", "QueenCage", "queenCage"), ("nectar_droplet", "NectarDroplet", "nectarDroplet"),
            ("watering_can", "WateringCan", "wateringCan"), ("flower_pots", "FlowerPots", "flowerPots"), ("sample_tray", "SampleTray", "sampleTray"),
            ("moon_flag", "MoonFlag", "moonFlag"), ("earth_flag", "EarthFlag", "earthFlag"),
        };

        const string KitFolder = "/NewProps/Resources";

        [Serializable] class Manifest { public string source, blender; public Item[] items; }
        [Serializable] class Item { public string item, slot, kind, fbx; public int fps, tris; public Size size; public Clip[] clips; public MatEntry[] materials; public string[] blend_shapes; }
        [Serializable] class Size { public float width, height, length, min_z; }
        [Serializable] class Clip { public string name; public int first, last; public bool loop; }
        [Serializable] class MatEntry { public string name, kind, texture; }

        static string Expand(string p) =>
            p.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Substring(2)) : p;

        [MenuItem("Virtual Bee Swarm/Art/Import dartiros new props...")]
        static void ImportMenu()
        {
            string dir = EditorUtility.OpenFolderPanel("export_dart_props.py output (the folder with manifest.json)", "", "");
            if (!string.IsNullOrEmpty(dir)) Import(dir);
        }

        public static void ImportBatch() => Batch(() =>
        {
            string dir = Environment.GetEnvironmentVariable("VBS_PROPS_DIR");
            if (string.IsNullOrEmpty(dir)) throw new Exception("set VBS_PROPS_DIR to the export_dart_props.py output folder");
            Import(Expand(dir));
        });

        static void Batch(Action a)
        {
            try
            {
                a();
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[VBS][NewProps] FAILED: " + e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        public static void Import(string dir)
        {
            var man = JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")));
            Debug.Log("[VBS][NewProps] manifest " + dir + " (" + man.source + ", Blender " + man.blender + "): " + man.items.Length + " items");
            var shared = SharedMaterials();
            var kit = Kit();
            var so = new SerializedObject(kit);
            foreach (var (item, slot, field) in Slots)
            {
                var it = man.items.FirstOrDefault(i => i.item == item);
                if (it == null) { Debug.LogWarning("[VBS][NewProps] " + slot + ": not in the manifest"); continue; }
                var prefab = ImportSlot(dir, it, slot, shared, out string report);
                so.FindProperty(field).objectReferenceValue = prefab;
                Debug.Log("[VBS][NewProps] " + report);
            }
            so.FindProperty("flowers").objectReferenceValue = ArtSlots.Prefab("Flowers");
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(kit);
            AssetDatabase.SaveAssets();
            Debug.Log("[VBS][NewProps] kit " + AssetDatabase.GetAssetPath(kit) + (kit.flowers == null ? " (no Art/Flowers prefab: pots stay empty)" : ""));
        }

        static NewPropsKit Kit()
        {
            string folder = ArtSlots.VbsRoot + KitFolder;
            EnsureFolder(folder);
            string path = folder + "/" + NewPropsKit.ResourceName + ".asset";
            var kit = AssetDatabase.LoadAssetAtPath<NewPropsKit>(path);
            if (kit != null) return kit;
            kit = ScriptableObject.CreateInstance<NewPropsKit>();
            AssetDatabase.CreateAsset(kit, path);
            return kit;
        }

        static GameObject ImportSlot(string dir, Item it, string slot, Dictionary<string, Material> shared, out string report)
        {
            string slotDir = ArtSlots.ArtRoot + "/" + slot;
            string modelDir = slotDir + "/Model";
            EnsureFolder(modelDir);
            string src = Path.Combine(dir, it.fbx);
            string fbx = modelDir + "/" + Path.GetFileName(it.fbx);
            File.Copy(src, fbx, true);
            foreach (var m in it.materials)
                if (!string.IsNullOrEmpty(m.texture))
                    File.Copy(Path.Combine(Path.GetDirectoryName(src), m.texture), modelDir + "/" + m.texture, true);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (var m in it.materials)
                if (!string.IsNullOrEmpty(m.texture)) TextureSettings(modelDir + "/" + m.texture);

            bool shapes = it.blend_shapes != null && it.blend_shapes.Length > 0;
            var mi = (ModelImporter)AssetImporter.GetAtPath(fbx);
            mi.globalScale = 1f;
            mi.useFileScale = true;
            mi.bakeAxisConversion = true;
            mi.preserveHierarchy = true;
            mi.sortHierarchyByName = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importVisibility = false;
            mi.importBlendShapes = shapes;
            if (shapes) mi.importBlendShapeNormals = ModelImporterNormals.None;
            mi.addCollider = false;
            mi.isReadable = false;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.importNormals = ModelImporterNormals.Import;
            mi.importTangents = ModelImporterTangents.None;
            mi.generateSecondaryUV = false;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
            mi.animationType = ModelImporterAnimationType.Legacy;
            mi.generateAnimations = ModelImporterGenerateAnimations.GenerateAnimations;
            mi.importAnimation = it.clips.Length > 0;
            mi.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
            mi.resampleCurves = true;
            mi.skinWeights = ModelImporterSkinWeights.Standard;
            foreach (var m in it.materials)
            {
                var mat = MaterialFor(m, modelDir, shared);
                if (mat != null) mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), m.name), mat);
            }
            mi.SaveAndReimport();

            if (it.clips.Length > 0)
            {
                var takes = mi.importedTakeInfos;
                var list = new List<ModelImporterClipAnimation>();
                foreach (var c in it.clips)
                {
                    bool rig = it.kind == "rig";
                    var take = rig ? takes.FirstOrDefault(t => t.name == c.name || t.name.EndsWith("|" + c.name)) : takes.FirstOrDefault();
                    if (string.IsNullOrEmpty(take.name)) { Debug.LogWarning("[VBS][NewProps] " + slot + ": no take for " + c.name); continue; }
                    float fps = take.sampleRate > 0 ? take.sampleRate : it.fps;
                    list.Add(new ModelImporterClipAnimation
                    {
                        name = c.name,
                        takeName = take.name,
                        firstFrame = rig ? take.startTime * fps : c.first,
                        lastFrame = rig ? take.stopTime * fps : c.last,
                        loopTime = c.loop,
                        wrapMode = c.loop ? WrapMode.Loop : WrapMode.Once,
                    });
                }
                mi.clipAnimations = list.ToArray();
                mi.SaveAndReimport();
            }

            var prefab = MakePrefab(slot, fbx);
            report = Describe(slot, it, prefab);
            return prefab;
        }

        static void TextureSettings(string path)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return;
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.anisoLevel = 2;
            ti.maxTextureSize = 512;
            ti.alphaSource = TextureImporterAlphaSource.None;
            ti.SaveAndReimport();
        }

        // ------------------------------------------------------------------------------------------ materials
        static Dictionary<string, Material> SharedMaterials()
        {
            string dir = ArtSlots.ArtRoot + "/_Shared";
            EnsureFolder(dir);
            return new Dictionary<string, Material>
            {
                ["vc"] = Mat(dir + "/VBS_DartVC.mat", GameMaterials.Blend.Opaque, 1f, false, 1f, null),
                ["wing"] = Mat(dir + "/VBS_DartWing.mat", GameMaterials.Blend.Alpha, 1.1f, true, 0.5f, null),
                ["glow"] = Mat(dir + "/VBS_DartGlow.mat", GameMaterials.Blend.Opaque, 1.6f, false, 1f, null),
            };
        }

        static Material MaterialFor(MatEntry m, string modelDir, Dictionary<string, Material> shared)
        {
            if (m.kind == "tex" && !string.IsNullOrEmpty(m.texture))
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(modelDir + "/" + m.texture);
                // flags and the cage screen are thin cloth / a screen: seen from both sides
                return Mat(modelDir + "/VBS_Dart_" + Path.GetFileNameWithoutExtension(m.texture) + ".mat", GameMaterials.Blend.Opaque, 1f, true, 1f, tex);
            }
            return shared.TryGetValue(m.kind, out var mat) ? mat : shared["vc"];
        }

        /// <summary>Same as DartArtImport's: an existing material is kept (hand tweaks survive a re-import).</summary>
        static Material Mat(string path, GameMaterials.Blend blend, float emission, bool twoSided, float alpha, Texture tex)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            mat = GameMaterials.Create(Path.GetFileNameWithoutExtension(path), new Color(1f, 1f, 1f, alpha), blend, vertexColors: true,
                emission: emission, instancing: true, doubleSided: twoSided);
            if (tex != null && mat.HasProperty(GameMaterials.MainTexId)) mat.SetTexture(GameMaterials.MainTexId, tex);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        // ------------------------------------------------------------------------------------------ prefab
        static GameObject MakePrefab(string slot, string fbx)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            if (model == null) throw new Exception(fbx + " did not import as a model");
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                go.name = slot;
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                    r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                    r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                    r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                    if (r is SkinnedMeshRenderer smr)
                    {
                        smr.quality = SkinQuality.Bone2;
                        smr.updateWhenOffscreen = false;
                        smr.skinnedMotionVectors = false;
                    }
                }
                var anim = go.GetComponent<Animation>();
                if (anim != null)
                {
                    anim.playAutomatically = false;
                    anim.cullingType = AnimationCullingType.BasedOnRenderers;
                    anim.clip = null;
                }
                var prefab = PrefabUtility.SaveAsPrefabAsset(go, ArtSlots.PrefabPath(slot), out bool ok);
                if (!ok) throw new Exception("SaveAsPrefabAsset failed: " + ArtSlots.PrefabPath(slot));
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        static string Describe(string slot, Item it, GameObject prefab)
        {
            int tris = 0, skinned = 0, renderers = 0, shapes = 0;
            var mats = new HashSet<string>();
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                renderers++;
                Mesh m = r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (r is SkinnedMeshRenderer) skinned++;
                if (m != null) { tris += m.triangles.Length / 3; shapes += m.blendShapeCount; }
                foreach (var mm in r.sharedMaterials) if (mm != null) mats.Add(mm.name);
            }
            var anim = prefab.GetComponent<Animation>();
            var clips = anim != null ? AnimationUtility.GetAnimationClips(prefab).Select(c => c.name + " " + c.length.ToString("0.00") + "s" + (c.isLooping ? " loop" : "") +
                (AnimationUtility.GetCurveBindings(c).Any(bd => bd.propertyName.StartsWith("blendShape.")) ? " (shape keys)" : "")).ToArray() : new string[0];
            return slot + " <- " + it.fbx + ": " + renderers + " renderer(s), " + skinned + " skinned, " + shapes + " blend shape(s), " + tris + " tris, materials [" +
                   string.Join(", ", mats) + "], clips [" + string.Join(", ", clips) + "]";
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        // ------------------------------------------------------------------------------------------ stills
        /// <summary>Each new prop at a telling moment of its clip, 900x900 PNGs in VBS_PROPS_STILLS (default Logs/newprops-stills).</summary>
        public static void StillsBatch() => Batch(() =>
        {
            string outDir = Environment.GetEnvironmentVariable("VBS_PROPS_STILLS");
            if (string.IsNullOrEmpty(outDir)) outDir = "Logs/newprops-stills";
            outDir = Expand(outDir);
            Directory.CreateDirectory(outDir);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var shots = new (string slot, string clip, float t, Vector3 dir)[]
            {
                ("UnionBadge", "Settle", 0f, new Vector3(0.2f, 0.15f, -1f)), ("HiveTool", "Lever", 0.22f, new Vector3(0.3f, 0.8f, -1f)),
                ("FrameRest", "InspectComb", 0.5f, new Vector3(0.45f, 0.3f, -1f)), ("QueenBee", "Hover", 0.3f, new Vector3(0.55f, 0.35f, -1f)),
                ("QueenCage", "OpenPlug", 0.5f, new Vector3(0.3f, 0.5f, -1f)), ("NectarDroplet", "Wobble", 0.22f, new Vector3(0.3f, 0.3f, -1f)),
                ("WateringCan", "Pour", 0.5f, new Vector3(0.2f, 0.35f, -1f)), ("FlowerPots", "Fill", 1f, new Vector3(0f, 0.55f, -1f)),
                ("SampleTray", "Inspect", 0.75f, new Vector3(0.2f, 0.8f, -1f)), ("MoonFlag", "Flutter", 0.25f, new Vector3(0.2f, 0.15f, -1f)),
                ("EarthFlag", "Flutter", 0.7f, new Vector3(0.2f, 0.15f, -1f)),
            };
            const int w = 900, h = 900;
            var cam = new GameObject("StillCam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.66f, 0.70f, 1f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.002f;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
            cam.targetTexture = rt;
            var done = new List<string>();
            foreach (var s in shots)
            {
                var prefab = ArtSlots.Prefab(s.slot);
                if (prefab == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.transform.rotation = Quaternion.identity; // front (-Z) to the camera
                var anim = go.GetComponent<Animation>();
                if (anim != null && anim[s.clip] != null)
                {
                    var st = anim[s.clip];
                    anim.Play(s.clip);
                    st.time = s.t * st.length;
                    st.enabled = true;
                    st.weight = 1f;
                    anim.Sample();
                }
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>()) smr.updateWhenOffscreen = true;
                var b = new Bounds(go.transform.position, Vector3.zero);
                bool any = false;
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    if (r.bounds.size.sqrMagnitude < 1e-10f) continue;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
                float radius = b.extents.magnitude * 1.2f;
                cam.transform.position = b.center + s.dir.normalized * (radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
                cam.transform.LookAt(b.center);
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                string file = s.slot + "_" + s.clip + ".png";
                File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                Object.DestroyImmediate(go);
                done.Add(file);
            }
            cam.targetTexture = null;
            Debug.Log("[VBS][NewProps] stills in " + outDir + ": " + string.Join(", ", done));
        });
    }
}
