using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;
using ArtSlots = VirtualBeeSwarm.Editor.ArtSlots;

namespace VirtualBeeSwarm.ArtAnimEditor
{
    /// <summary>
    /// Imports dartiros' animated creatures and props (exported by art/blender/scripts/export_dart.py: one FBX per item
    /// + manifest.json) into Art/&lt;Slot&gt;/Model/ and rebuilds Art/&lt;Slot&gt;/&lt;Slot&gt;.prefab, which ArtSlots then puts into
    /// the scene as before. The temp FBX in Art/&lt;Slot&gt;/ is deleted; the model lives one folder down so
    /// ArtImport.RebuildPrefabs (which turns animation off) leaves these slots alone.
    ///
    ///   Menu:  Virtual Bee Swarm > Art > Import dartiros animated art...   (pick the export folder)
    ///   Batch: VBS_DART_DIR=&lt;export dir&gt; Unity -batchmode -nographics -quit -projectPath P
    ///          -executeMethod VirtualBeeSwarm.ArtAnimEditor.DartArtImport.ImportBatch
    ///   Stills (needs graphics: xvfb-run, no -nographics): VBS_DART_STILLS=&lt;dir&gt; ... DartArtImport.StillsBatch
    ///
    /// Import: Legacy animation (one Animation component on the prefab root, clips in place, no root curves), axis
    /// conversion baked (+Z front, Y up, nodes unrotated), scale matched to the temp model the slot table was tuned for,
    /// materials = VirtualBeeSwarm/Unlit with the baked vertex colours (opaque / wings alpha / flame glow / printed
    /// texture; the hive's HeatGlow keeps Materials/VBS_Glow). Each prefab gets ArtAnimCreature or ArtAnimProp.
    /// </summary>
    public static class DartArtImport
    {
        enum Fit { Native, Length, Height }

        sealed class SlotDef
        {
            public string Item, Slot;
            public Fit Fit;
            public float Target;          // size of the temp model on that axis (m), what ArtSlots' Scale assumes
            public Action<GameObject> Setup;
        }

        static readonly SlotDef[] Defs =
        {
            new SlotDef { Item = "hornet", Slot = "Hornet", Fit = Fit.Length, Target = 0.33f, Setup = go =>
            {
                var c = go.AddComponent<ArtAnimCreature>();
                c.loopClip = "Hover";
                c.accents = new[]
                {
                    new ArtAnimCreature.Accent { clip = "Wag", maskBone = "Abdomen", minGap = 2.5f, maxGap = 4.5f },
                    new ArtAnimCreature.Accent { clip = "Thorn Flick", maskBone = "Thorn", minGap = 2.5f, maxGap = 5f },
                    new ArtAnimCreature.Accent { clip = "Antenna Twitch", maskBone = "Head", minGap = 3f, maxGap = 6f },
                };
                c.hornetReactions = true;
            }},
            new SlotDef { Item = "wasp", Slot = "Wasp", Fit = Fit.Length, Target = 0.06f, Setup = go =>
            {
                var c = go.AddComponent<ArtAnimCreature>();
                c.loopClip = "Hover";
                c.accents = new[]
                {
                    new ArtAnimCreature.Accent { clip = "Antenna Twitch", maskBone = "Head", minGap = 3f, maxGap = 7f },
                    new ArtAnimCreature.Accent { clip = "Wag", maskBone = "Abdomen", minGap = 4f, maxGap = 8f },
                };
            }},
            new SlotDef { Item = "worker_bee", Slot = "WorkerBee", Fit = Fit.Native, Setup = go =>
            {
                var c = go.AddComponent<ArtAnimCreature>();
                c.loopClip = "Hover";
                c.idleClip = "Antenna Twitch"; // landed on the pebbles: wings still, drinking
                c.faceVelocity = true;
            }},
            new SlotDef { Item = "hive", Slot = "Hive", Fit = Fit.Height, Target = 0.45f, Setup = go =>
            {
                var p = go.AddComponent<ArtAnimProp>();
                p.trigger = ArtAnimProp.Trigger.Beat;
                p.clip = "LidOpen";
                p.followClip = "LidClose";
                p.beat = GameBeat.Escape;
                p.delay = 0.3f;
            }},
            new SlotDef { Item = "smoker", Slot = "Smoker", Fit = Fit.Height, Target = 0.27f, Setup = go =>
            {
                var p = go.AddComponent<ArtAnimProp>();
                p.trigger = ArtAnimProp.Trigger.ParticleBurst;
                p.particleName = "SmokePuff";
                p.clip = "Squeeze";
                p.speed = 2f;
                p.repeat = 3; // SmokerStation: 3 puffs, 0.4 s apart
                p.repeatGap = 0.4f;
            }},
            new SlotDef { Item = "spray_bottle", Slot = "SprayBottle", Fit = Fit.Height, Target = 0.18f, Setup = go =>
            {
                var p = go.AddComponent<ArtAnimProp>();
                p.trigger = ArtAnimProp.Trigger.ParticleBurst;
                p.particleName = "WaterPuff";
                p.clip = "Pull";
                p.speed = 1.5f;
            }},
            new SlotDef { Item = "rocket", Slot = "Rocket", Fit = Fit.Height, Target = 0.60f, Setup = go =>
            {
                var p = go.AddComponent<ArtAnimProp>();
                p.trigger = ArtAnimProp.Trigger.ObjectActive;
                p.watchName = "Flame";
                p.clip = "Ignition";
                p.followClip = "Flicker";
            }},
        };

        // ------------------------------------------------------------------------------------------ manifest
        [Serializable] class Manifest { public string source, blender; public Item[] items; }
        [Serializable] class Item { public string item, slot, kind, fbx; public int fps, tris; public Size size; public Clip[] clips; public MatEntry[] materials; public string[] bones; }
        [Serializable] class Size { public float width, height, length, min_z; }
        [Serializable] class Clip { public string name; public int first, last; public bool loop; }
        [Serializable] class MatEntry { public string name, kind, texture; }

        static string Expand(string p) =>
            p.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Substring(2)) : p;

        [MenuItem("Virtual Bee Swarm/Art/Import dartiros animated art...")]
        static void ImportMenu()
        {
            string dir = EditorUtility.OpenFolderPanel("export_dart.py output (the folder with manifest.json)", "", "");
            if (!string.IsNullOrEmpty(dir)) Import(dir);
        }

        public static void ImportBatch() => Batch(() =>
        {
            string dir = Environment.GetEnvironmentVariable("VBS_DART_DIR");
            if (string.IsNullOrEmpty(dir)) throw new Exception("set VBS_DART_DIR to the export_dart.py output folder");
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
                Debug.LogError("[VBS][ArtAnim] FAILED: " + e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        public static void Import(string dir)
        {
            var man = JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")));
            Debug.Log("[VBS][ArtAnim] manifest " + dir + " (" + man.source + ", Blender " + man.blender + "): " + man.items.Length + " items");
            var shared = SharedMaterials();
            var report = new List<string>();
            foreach (var def in Defs)
            {
                var it = man.items.FirstOrDefault(i => i.item == def.Item);
                if (it == null) { report.Add(def.Slot + ": not in the manifest, temp model kept"); continue; }
                report.Add(ImportSlot(dir, it, def, shared));
            }
            AssetDatabase.SaveAssets();
            var unused = man.items.Where(i => Defs.All(d => d.Item != i.item)).Select(i => i.item).ToArray();
            foreach (var r in report) Debug.Log("[VBS][ArtAnim] " + r);
            if (unused.Length > 0) Debug.Log("[VBS][ArtAnim] exported but not used in the game (no slot): " + string.Join(", ", unused));
        }

        static string ImportSlot(string dir, Item it, SlotDef def, Dictionary<string, Material> shared)
        {
            string slotDir = ArtSlots.ArtRoot + "/" + def.Slot;
            string modelDir = slotDir + "/Model";
            EnsureFolder(modelDir);
            // the temp model goes (top-level FBX only); the prefab is rewritten in place, so its GUID stays
            if (Directory.Exists(slotDir))
                foreach (var f in Directory.GetFiles(slotDir, "*.fbx"))
                    AssetDatabase.DeleteAsset(f.Replace('\\', '/'));
            string src = Path.Combine(dir, it.fbx);
            string fbx = modelDir + "/" + Path.GetFileName(it.fbx);
            File.Copy(src, fbx, true);
            foreach (var m in it.materials)
                if (!string.IsNullOrEmpty(m.texture))
                    File.Copy(Path.Combine(Path.GetDirectoryName(src), m.texture), modelDir + "/" + m.texture, true);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            float size = def.Fit == Fit.Length ? it.size.length : def.Fit == Fit.Height ? it.size.height : 0f;
            float k = def.Fit == Fit.Native || size <= 0f ? 1f : def.Target / size;

            var mi = (ModelImporter)AssetImporter.GetAtPath(fbx);
            mi.globalScale = k;
            mi.useFileScale = true;
            mi.bakeAxisConversion = true;
            mi.preserveHierarchy = true; // the prefab root never carries animation curves (ArtSlots places it)
            mi.sortHierarchyByName = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importVisibility = false;
            mi.importBlendShapes = false;
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

            // clips: a rig's takes keep their names; a prop's single take is split by frame range
            if (it.clips.Length > 0)
            {
                var takes = mi.importedTakeInfos;
                var list = new List<ModelImporterClipAnimation>();
                foreach (var c in it.clips)
                {
                    bool rig = it.kind == "rig";
                    var take = rig ? takes.FirstOrDefault(t => t.name == c.name || t.name.EndsWith("|" + c.name)) : takes.FirstOrDefault();
                    if (string.IsNullOrEmpty(take.name)) { Debug.LogWarning("[VBS][ArtAnim] " + def.Slot + ": no take for " + c.name); continue; }
                    float fps = take.sampleRate > 0 ? take.sampleRate : it.fps;
                    var ca = new ModelImporterClipAnimation
                    {
                        name = c.name,
                        takeName = take.name,
                        firstFrame = rig ? take.startTime * fps : c.first,
                        lastFrame = rig ? take.stopTime * fps : c.last,
                        loopTime = c.loop,
                        wrapMode = c.loop ? WrapMode.Loop : WrapMode.Once,
                    };
                    list.Add(ca);
                }
                mi.clipAnimations = list.ToArray();
                mi.SaveAndReimport();
            }

            var prefab = MakePrefab(def, fbx);
            return Describe(def, it, k, prefab);
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
                ["heatglow"] = AssetDatabase.LoadAssetAtPath<Material>(ArtSlots.VbsRoot + "/Materials/VBS_Glow.mat"),
            };
        }

        static Material MaterialFor(MatEntry m, string modelDir, Dictionary<string, Material> shared)
        {
            if (m.kind == "tex" && !string.IsNullOrEmpty(m.texture))
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(modelDir + "/" + m.texture);
                return Mat(modelDir + "/VBS_Dart_" + Path.GetFileNameWithoutExtension(m.texture) + ".mat", GameMaterials.Blend.Opaque, 1f, false, 1f, tex);
            }
            return shared.TryGetValue(m.kind, out var mat) ? mat : shared["vc"];
        }

        static Material Mat(string path, GameMaterials.Blend blend, float emission, bool twoSided, float alpha, Texture tex)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat; // hand tweaks survive a re-import
            mat = GameMaterials.Create(Path.GetFileNameWithoutExtension(path), new Color(1f, 1f, 1f, alpha), blend, vertexColors: true,
                emission: emission, instancing: true, doubleSided: twoSided);
            if (tex != null && mat.HasProperty(GameMaterials.MainTexId)) mat.SetTexture(GameMaterials.MainTexId, tex);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        // ------------------------------------------------------------------------------------------ prefab
        static GameObject MakePrefab(SlotDef def, string fbx)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            if (model == null) throw new Exception(fbx + " did not import as a model");
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                go.name = def.Slot;
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
                def.Setup?.Invoke(go);
                var prefab = PrefabUtility.SaveAsPrefabAsset(go, ArtSlots.PrefabPath(def.Slot), out bool ok);
                if (!ok) throw new Exception("SaveAsPrefabAsset failed: " + ArtSlots.PrefabPath(def.Slot));
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        static readonly Dictionary<string, string[]> Anchors = new Dictionary<string, string[]>
        {
            { "Hive", new[] { "LiftRoot", "Stand", "BoxLower", "BoxUpper", "Lid", "Door", "DoorSlot", "DoorPoint", "SwarmCenter", "HeatGlow", "Hexagons", "HexSlot1", "HexSlot2", "HexSlot3", "HexSlot4", "EntranceReducer" } },
            { "Rocket", new[] { "Flame", "Seat", "Perch", "CountdownDisplay", "Hull" } },
            { "Smoker", new[] { "SmokePoint", "BellowsLever" } },
            { "SprayBottle", new[] { "SprayPoint", "Trigger" } },
            { "Hornet", new[] { "Rig", "Mesh", "Abdomen", "Thorn", "Head", "Wing.L", "Wing.R" } },
            { "Wasp", new[] { "Rig", "Mesh", "Abdomen", "Head", "Wing.L", "Wing.R" } },
            { "WorkerBee", new[] { "Rig", "Mesh", "Head", "Wing.L", "Wing.R" } },
        };

        static string Describe(SlotDef def, Item it, float k, GameObject prefab)
        {
            int tris = 0, skinned = 0, renderers = 0, subs = 0;
            var mats = new HashSet<string>();
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                renderers++;
                Mesh m = r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (r is SkinnedMeshRenderer) skinned++;
                if (m != null) { tris += m.triangles.Length / 3; subs += m.subMeshCount; }
                foreach (var mm in r.sharedMaterials) if (mm != null) mats.Add(mm.name);
            }
            var b = RenderBounds(prefab);
            var anim = prefab.GetComponent<Animation>();
            var clips = anim != null ? AnimationUtility.GetAnimationClips(prefab).Select(c => c.name + " " + c.length.ToString("0.00") + "s" + (c.isLooping ? " loop" : "")).ToArray() : new string[0];
            var missing = new List<string>();
            var rotated = new List<string>();
            if (Anchors.TryGetValue(def.Slot, out var names))
                foreach (var n in names)
                {
                    var t = ArtSlots.FindPath(prefab.transform, n);
                    if (t == null) missing.Add(n);
                    else if ((it.kind != "rig" || n == "Rig" || n == "Mesh") && Quaternion.Angle(t.localRotation, Quaternion.identity) > 0.5f)
                        rotated.Add(n); // bones keep their bone rolls; props and rig objects should be unrotated
                }
            return def.Slot + " <- " + it.fbx + " (scale x" + k.ToString("0.###") + "): " + renderers + " renderer(s), " + skinned + " skinned, " + subs + " submesh(es), " +
                   tris + " tris, size " + b.size.x.ToString("0.000") + " x " + b.size.y.ToString("0.000") + " x " + b.size.z.ToString("0.000") +
                   " m (w x h x l), materials [" + string.Join(", ", mats) + "], clips [" + string.Join(", ", clips) + "]" +
                   (missing.Count > 0 ? ", MISSING " + string.Join(", ", missing) : ", anchors ok") +
                   (rotated.Count > 0 ? ", ROTATED " + string.Join(", ", rotated) : "");
        }

        static Bounds RenderBounds(GameObject go)
        {
            var b = new Bounds();
            bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                Mesh m = r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (m == null) continue;
                var mtx = go.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                var mb = m.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = mtx.MultiplyPoint3x4(c);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return b;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        // ------------------------------------------------------------------------------------------ stills
        /// <summary>
        /// Renders each animated prefab at a telling moment of its clip to VBS_DART_STILLS (default Logs/dart-stills):
        /// hornet + wasp + worker bee mid-Hover, the hive with the lid open, the smoker mid-squeeze, the spray mid-pull,
        /// the rocket mid-flicker after ignition. Needs graphics (xvfb-run, without -nographics).
        /// </summary>
        public static void StillsBatch() => Batch(() =>
        {
            string outDir = Environment.GetEnvironmentVariable("VBS_DART_STILLS");
            if (string.IsNullOrEmpty(outDir)) outDir = "Logs/dart-stills";
            outDir = Expand(outDir);
            Directory.CreateDirectory(outDir);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var shots = new (string slot, string file, string clip, float t, Vector3 dir, float pad)[]
            {
                ("Hornet", "hornet_hover.png", "Hover", 0.3f, new Vector3(0.55f, 0.35f, 1f), 1.25f),
                ("Hornet", "hornet_wag.png", "Wag", 0.4f, new Vector3(-0.9f, 0.3f, 0.6f), 1.25f),
                ("Wasp", "wasp_hover.png", "Hover", 0.3f, new Vector3(0.55f, 0.35f, 1f), 1.25f),
                ("WorkerBee", "worker_bee_hover.png", "Hover", 0.3f, new Vector3(0.55f, 0.35f, 1f), 1.25f),
                ("Hive", "hive_closed.png", "LidOpen", 0f, new Vector3(0.5f, 0.45f, 1f), 1.15f),
                ("Hive", "hive_lid_open.png", "LidOpen", 1f, new Vector3(0.5f, 0.6f, 1f), 1.15f),
                ("Smoker", "smoker_rest.png", "Squeeze", 0f, new Vector3(-1f, 0.25f, 0.45f), 1.2f),
                ("Smoker", "smoker_squeeze.png", "Squeeze", 0.55f, new Vector3(-1f, 0.25f, 0.45f), 1.2f),
                ("SprayBottle", "spray_pull.png", "Pull", 0.5f, new Vector3(-1f, 0.2f, 0.6f), 1.2f),
                ("Rocket", "rocket_ignition.png", "Ignition", 0.75f, new Vector3(0.6f, 0.2f, 1f), 1.15f),
                ("Rocket", "rocket_flicker.png", "Flicker", 0.4f, new Vector3(0.6f, 0.2f, 1f), 1.15f),
            };
            const int w = 900, h = 900;
            var camGo = new GameObject("StillCam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.66f, 0.70f, 1f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.005f;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
            cam.targetTexture = rt;
            var done = new List<string>();
            foreach (var s in shots)
            {
                var prefab = ArtSlots.Prefab(s.slot);
                if (prefab == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.transform.rotation = Quaternion.Euler(0f, 180f, 0f); // front to the camera, as ArtSlots does
                var anim = go.GetComponent<Animation>();
                if (anim != null && anim[s.clip] != null)
                {
                    if (s.slot == "Rocket") ArtSlots.FindPath(go.transform, "Flame")?.gameObject.SetActive(true);
                    var st = anim[s.clip];
                    anim.Play(s.clip);
                    st.time = s.t * st.length;
                    st.enabled = true;
                    st.weight = 1f;
                    anim.Sample();
                }
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>()) smr.updateWhenOffscreen = true;
                var b = WorldBounds(go);
                float radius = b.extents.magnitude * s.pad;
                var dir = s.dir.normalized;
                cam.transform.position = b.center + dir * (radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
                cam.transform.LookAt(b.center);
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(Path.Combine(outDir, s.file), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                Object.DestroyImmediate(go);
                done.Add(s.file);
            }
            cam.targetTexture = null;
            Debug.Log("[VBS][ArtAnim] stills in " + outDir + ": " + string.Join(", ", done));
        });

        static Bounds WorldBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            var b = new Bounds(go.transform.position, Vector3.zero);
            bool any = false;
            foreach (var r in rs)
            {
                if (r.bounds.size.sqrMagnitude < 1e-10f) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return b;
        }
    }
}
