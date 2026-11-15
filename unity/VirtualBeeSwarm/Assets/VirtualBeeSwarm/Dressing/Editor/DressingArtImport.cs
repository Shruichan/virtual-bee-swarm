using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VirtualBeeSwarm.Dressing;
using Object = UnityEngine.Object;
using ArtSlots = VirtualBeeSwarm.Editor.ArtSlots;

namespace VirtualBeeSwarm.DressingEditor
{
    /// <summary>
    /// dartiros' printed / textured props and the table dressing, from the same export as the creatures
    /// (art/blender/scripts/export_dart.py: one FBX per item + manifest.json), into Art/&lt;Slot&gt;/Model/ with
    /// Art/&lt;Slot&gt;/&lt;Slot&gt;.prefab rebuilt in place (same GUID, part names kept), so ArtSlots and StationDelivery pick
    /// them up with no code change:
    ///   HoneyJar, SyrupBottle (printed labels), ScoreCard (OFFICIAL RESULT header, TextAnchor), SignHere (Board,
    ///   PokePoint), StampApproved / StampGrievance (filled printed cards), SeedPacket (SEEDS MEADOW MIX) - each
    ///   scaled to the size the temp prop had, its temp FBX deleted (as DartArtImport does);
    ///   WildBeeNest, SolitaryBee, Bumblebee, TinyWasp (new) - used at runtime by Dressing/DressingDirector through
    ///   Resources/DressingArt.asset, which this also writes.
    ///
    ///   Menu:  Virtual Bee Swarm > Art > Import dressing art (printed props, nest, small bees)...
    ///   Batch: VBS_DART_DIR=&lt;export dir&gt; Unity -batchmode -nographics -quit -projectPath P
    ///          -executeMethod VirtualBeeSwarm.DressingEditor.DressingArtImport.ImportBatch
    ///   Stills (needs graphics: Xvfb, no -nographics): VBS_DRESS_STILLS=&lt;dir&gt; ... DressingArtImport.StillsBatch
    /// Then Build Scene as usual. Do not run ArtImport.Import afterwards (it copies the temp FBX back).
    /// </summary>
    public static class DressingArtImport
    {
        enum Fit { Native, Width, Height }

        sealed class Def
        {
            public string Item, Slot;
            public Fit Fit;
            public float Target;          // m on that axis; 0 = the size the slot's prefab has now (the temp model)
            public bool BaseOnOrigin;     // lift/drop the art so its lowest point sits on the prefab origin
            public bool DoubleSided;      // printed materials drawn from both sides (a 2-tri card)
            public string[] Anchors = new string[0];
        }

        static readonly Def[] Defs =
        {
            new Def { Item = "honey_jar_printed", Slot = "HoneyJar", Fit = Fit.Height, Anchors = new[] { "Jar", "Lid", "Label" } },
            new Def { Item = "syrup_bottle_printed", Slot = "SyrupBottle", Fit = Fit.Height, Anchors = new[] { "Bottle", "Cap", "Label" } },
            new Def { Item = "score_card_printed", Slot = "ScoreCard", Fit = Fit.Width, Anchors = new[] { "Card", "TextAnchor" } },
            new Def { Item = "sign_here_printed", Slot = "SignHere", Fit = Fit.Height, Anchors = new[] { "Board", "PokePoint" } },
            new Def { Item = "stamp_approved_printed", Slot = "StampApproved", Fit = Fit.Width },
            new Def { Item = "stamp_grievance_printed", Slot = "StampGrievance", Fit = Fit.Width },
            new Def { Item = "seed_packet_textured", Slot = "SeedPacket", Fit = Fit.Height },
            new Def { Item = "wild_bee_nest", Slot = "WildBeeNest", Fit = Fit.Height, Target = 0.13f, BaseOnOrigin = true, Anchors = new[] { "DoorPoint", "SwarmCenter" } },
            new Def { Item = "solitary_bee", Slot = "SolitaryBee", Fit = Fit.Native, Anchors = new[] { "Rig", "Mesh", "Head" } },
            new Def { Item = "bumblebee", Slot = "Bumblebee", Fit = Fit.Native, Anchors = new[] { "Rig", "Mesh", "Head" } },
            new Def { Item = "wasp_tiny", Slot = "TinyWasp", Fit = Fit.Native, Anchors = new[] { "Rig", "Mesh", "Head" } },
        };

        // ------------------------------------------------------------------------------------------ manifest
        [Serializable] class Manifest { public string source, blender; public Item[] items; }
        [Serializable] class Item { public string item, slot, kind, fbx; public int fps, tris; public Size size; public Clip[] clips; public MatEntry[] materials; public string[] bones; }
        [Serializable] class Size { public float width, height, length, min_z; }
        [Serializable] class Clip { public string name; public int first, last; public bool loop; }
        [Serializable] class MatEntry { public string name, kind, texture; }

        const string Log = "[VBS][DressArt] ";

        static string Expand(string p) =>
            p.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Substring(2)) : p;

        static string DressingRoot => ArtSlots.VbsRoot + "/Dressing";
        static string ArtAssetPath => DressingRoot + "/Resources/" + DressingArt.ResourceName + ".asset";

        [MenuItem("Virtual Bee Swarm/Art/Import dressing art (printed props, nest, small bees)...")]
        static void ImportMenu()
        {
            string dir = EditorUtility.OpenFolderPanel("export_dart.py output (the folder with manifest.json)", "", "");
            if (!string.IsNullOrEmpty(dir)) Import(dir);
        }

        [MenuItem("Virtual Bee Swarm/Art/Rewrite Resources/DressingArt")]
        static void WriteArtMenu() => WriteArtAsset();

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
                Debug.LogError(Log + "FAILED: " + e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        public static void Import(string dir)
        {
            var man = JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")));
            Debug.Log(Log + "manifest " + dir + " (" + man.source + ", Blender " + man.blender + "): " + man.items.Length + " items");
            var shared = SharedMaterials();
            var report = new List<string>();
            foreach (var def in Defs)
            {
                var it = man.items.FirstOrDefault(i => i.item == def.Item);
                if (it == null) { report.Add(def.Slot + ": not in the manifest, kept as is"); continue; }
                report.Add(ImportSlot(dir, it, def, shared));
            }
            AssetDatabase.SaveAssets();
            report.Add(WriteArtAsset());
            foreach (var r in report) Debug.Log(Log + r);
        }

        static string ImportSlot(string dir, Item it, Def def, Dictionary<string, Material> shared)
        {
            string slotDir = ArtSlots.ArtRoot + "/" + def.Slot;
            string modelDir = slotDir + "/Model";
            // the size to match: the slot's current prefab (the temp model the slot table was tuned for)
            float target = def.Target;
            if (def.Fit != Fit.Native && target <= 0f)
            {
                var old = ArtSlots.Prefab(def.Slot);
                if (old != null)
                {
                    var ob = RenderBounds(old);
                    target = def.Fit == Fit.Width ? ob.size.x : ob.size.y;
                }
            }
            EnsureFolder(modelDir);
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
            foreach (var m in it.materials)
                if (!string.IsNullOrEmpty(m.texture)) PrintedTexture(modelDir + "/" + m.texture);

            float size = def.Fit == Fit.Width ? it.size.width : def.Fit == Fit.Height ? it.size.height : 0f;
            float k = def.Fit == Fit.Native || size <= 0f || target <= 0f ? 1f : target / size;
            if (def.Target <= 0f && (k < 0.5f || k > 2f))
            {
                Debug.LogWarning(Log + def.Slot + ": the temp prop is " + target.ToString("0.000") + " m vs the art's " + size.ToString("0.000") + " m; kept native size");
                k = 1f;
            }

            var mi = (ModelImporter)AssetImporter.GetAtPath(fbx);
            mi.globalScale = k;
            mi.useFileScale = true;
            mi.bakeAxisConversion = true;
            mi.preserveHierarchy = true;
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
            bool anim = it.clips != null && it.clips.Length > 0;
            mi.animationType = anim ? ModelImporterAnimationType.Legacy : ModelImporterAnimationType.None;
            mi.importAnimation = anim;
            if (anim)
            {
                mi.generateAnimations = ModelImporterGenerateAnimations.GenerateAnimations;
                mi.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
                mi.resampleCurves = true;
            }
            mi.skinWeights = ModelImporterSkinWeights.Standard;
            foreach (var m in it.materials)
            {
                var mat = MaterialFor(m, modelDir, shared, def.DoubleSided);
                if (mat != null) mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), m.name), mat);
            }
            mi.SaveAndReimport();

            if (anim)
            {
                var takes = mi.importedTakeInfos;
                var list = new List<ModelImporterClipAnimation>();
                foreach (var c in it.clips)
                {
                    var take = takes.FirstOrDefault(t => t.name == c.name || t.name.EndsWith("|" + c.name));
                    if (string.IsNullOrEmpty(take.name)) { Debug.LogWarning(Log + def.Slot + ": no take for " + c.name); continue; }
                    float fps = take.sampleRate > 0 ? take.sampleRate : it.fps;
                    list.Add(new ModelImporterClipAnimation
                    {
                        name = c.name,
                        takeName = take.name,
                        firstFrame = take.startTime * fps,
                        lastFrame = take.stopTime * fps,
                        loopTime = c.loop,
                        wrapMode = c.loop ? WrapMode.Loop : WrapMode.Once,
                    });
                }
                mi.clipAnimations = list.ToArray();
                mi.SaveAndReimport();
            }

            var prefab = MakePrefab(def, fbx);
            return Describe(def, it, k, target, prefab);
        }

        static void PrintedTexture(string path)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return;
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 4; // labels are read at a slant
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

        static Material MaterialFor(MatEntry m, string modelDir, Dictionary<string, Material> shared, bool doubleSided)
        {
            if (m.kind == "tex" && !string.IsNullOrEmpty(m.texture))
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(modelDir + "/" + m.texture);
                return Mat(modelDir + "/VBS_Dart_" + Path.GetFileNameWithoutExtension(m.texture) + ".mat", GameMaterials.Blend.Opaque, 1f, doubleSided, 1f, tex);
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
        static GameObject MakePrefab(Def def, string fbx)
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
                // export_dart's FBX (axis conversion baked) lands dartiros' fronts on -Z; the slot convention is +Z
                // (ArtSlots turns the art 180 to show the player its front). Turn the content, never the root:
                // a rig's root carries the Animation, so it goes one level down under a plain root instead.
                var turn = Quaternion.Euler(0f, 180f, 0f);
                if (anim != null)
                {
                    var wrap = new GameObject(def.Slot);
                    go.name = "Art";
                    go.transform.SetParent(wrap.transform, false);
                    go.transform.localRotation = turn;
                    go = wrap;
                }
                else
                {
                    for (int i = 0; i < go.transform.childCount; i++)
                    {
                        var ch = go.transform.GetChild(i);
                        ch.localPosition = turn * ch.localPosition;
                        ch.localRotation = turn * ch.localRotation;
                    }
                }
                if (def.BaseOnOrigin)
                {
                    float minY = RenderBounds(go).min.y;
                    for (int i = 0; i < go.transform.childCount; i++)
                        go.transform.GetChild(i).localPosition -= new Vector3(0f, minY, 0f);
                }
                var prefab = PrefabUtility.SaveAsPrefabAsset(go, ArtSlots.PrefabPath(def.Slot), out bool ok);
                if (!ok) throw new Exception("SaveAsPrefabAsset failed: " + ArtSlots.PrefabPath(def.Slot));
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        static string Describe(Def def, Item it, float k, float target, GameObject prefab)
        {
            int tris = 0, skinned = 0, renderers = 0;
            var mats = new HashSet<string>();
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                renderers++;
                Mesh m = r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (r is SkinnedMeshRenderer) skinned++;
                if (m != null) tris += m.triangles.Length / 3;
                foreach (var mm in r.sharedMaterials) if (mm != null) mats.Add(mm.name);
            }
            var b = RenderBounds(prefab);
            var animGo = prefab.GetComponentInChildren<Animation>(true);
            var clips = animGo != null
                ? AnimationUtility.GetAnimationClips(animGo.gameObject).Select(c => c.name + " " + c.length.ToString("0.00") + "s").ToArray() : new string[0];
            var missing = def.Anchors.Where(n => ArtSlots.FindPath(prefab.transform, n) == null).ToArray();
            string door = "";
            var dp = ArtSlots.FindPath(prefab.transform, "DoorPoint");
            if (dp != null) door = ", DoorPoint at " + prefab.transform.InverseTransformPoint(dp.position).ToString("F3");
            return def.Slot + " <- " + it.fbx + " (x" + k.ToString("0.###") + (target > 0f ? " to " + target.ToString("0.000") + " m" : "") + "): " +
                   renderers + " renderer(s), " + skinned + " skinned, " + tris + " tris, size " + b.size.x.ToString("0.000") + " x " +
                   b.size.y.ToString("0.000") + " x " + b.size.z.ToString("0.000") + " m (min y " + b.min.y.ToString("0.000") + "), materials [" +
                   string.Join(", ", mats) + "]" + (clips.Length > 0 ? ", clips [" + string.Join(", ", clips) + "]" : "") +
                   (missing.Length > 0 ? ", MISSING " + string.Join(", ", missing) : ", parts ok") + door;
        }

        // ------------------------------------------------------------------------------------------ the runtime asset
        /// <summary>Resources/DressingArt.asset: the prefabs + grass the runtime dressing spawns.</summary>
        public static string WriteArtAsset()
        {
            EnsureFolder(DressingRoot + "/Resources");
            var art = AssetDatabase.LoadAssetAtPath<DressingArt>(ArtAssetPath);
            bool created = art == null;
            if (created) art = ScriptableObject.CreateInstance<DressingArt>();
            art.nest = ArtSlots.Prefab("WildBeeNest");
            art.solitaryBee = ArtSlots.Prefab("SolitaryBee");
            art.bumblebee = ArtSlots.Prefab("Bumblebee");
            art.tinyWasp = ArtSlots.Prefab("TinyWasp");
            art.seedPacket = ArtSlots.Prefab("SeedPacket");
            var grass = ArtSlots.Prefab("GrassTuft");
            var mf = grass != null ? grass.GetComponentInChildren<MeshFilter>(true) : null;
            var mr = grass != null ? grass.GetComponentInChildren<MeshRenderer>(true) : null;
            art.grass = mf != null ? mf.sharedMesh : null;
            art.grassMaterial = mr != null ? mr.sharedMaterial : null;
            art.vertexColorMaterial = AssetDatabase.LoadAssetAtPath<Material>(ArtSlots.ArtRoot + "/_Shared/VBS_DartVC.mat");
            if (created) AssetDatabase.CreateAsset(art, ArtAssetPath);
            else EditorUtility.SetDirty(art);
            AssetDatabase.SaveAssets();
            return "Resources/DressingArt: nest " + Has(art.nest) + ", solitary " + Has(art.solitaryBee) + ", bumblebee " + Has(art.bumblebee) +
                   ", wasp " + Has(art.tinyWasp) + ", seed packet " + Has(art.seedPacket) + ", grass " + (art.grass != null ? art.grass.name : "-") +
                   " / " + (art.grassMaterial != null ? art.grassMaterial.name : "-");
        }

        static string Has(Object o) => o != null ? "ok" : "MISSING";

        // ------------------------------------------------------------------------------------------ stills
        /// <summary>
        /// Every imported prefab on a neutral backdrop, front 3/4 from the player's side, one PNG each, to
        /// VBS_DRESS_STILLS (default Logs/dress-stills). Needs a graphics device (Xvfb), not -nographics.
        /// </summary>
        public static void StillsBatch() => Batch(() =>
        {
            string dir = Environment.GetEnvironmentVariable("VBS_DRESS_STILLS");
            if (string.IsNullOrEmpty(dir)) dir = "Logs/dress-stills";
            dir = Expand(dir);
            Directory.CreateDirectory(dir);
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
            var camGo = new GameObject("StillsCam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.2f, 1f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.005f;
            var rt = new RenderTexture(768, 768, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            var tex = new Texture2D(768, 768, TextureFormat.RGB24, false);
            int n = 0;
            foreach (var def in Defs)
            {
                var prefab = ArtSlots.Prefab(def.Slot);
                if (prefab == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                // as the game shows it: front (+Z) turned to the player (yaw 180, ArtSlots' default)
                go.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                var anim = go.GetComponentInChildren<Animation>(true);
                if (anim != null)
                {
                    var hover = anim["Hover"];
                    if (hover != null) { hover.enabled = true; hover.weight = 1f; hover.time = 0.3f; anim.Sample(); }
                }
                var b = RenderBounds(go);
                b.center = go.transform.TransformPoint(b.center);
                float r = Mathf.Max(b.extents.magnitude, 0.01f);
                float dist = r / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.05f;
                Vector3 view = Quaternion.Euler(18f, 28f, 0f) * Vector3.back;
                cam.transform.position = b.center + view * dist;
                cam.transform.LookAt(b.center);
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, 768, 768), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(dir, def.Slot + ".png"), tex.EncodeToPNG());
                Object.DestroyImmediate(go);
                n++;
            }
            Debug.Log(Log + n + " stills -> " + dir);
        });

        // ------------------------------------------------------------------------------------------ util
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
    }
}
