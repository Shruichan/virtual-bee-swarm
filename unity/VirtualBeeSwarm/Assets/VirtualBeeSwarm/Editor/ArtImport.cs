using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VirtualBeeSwarm.Editor
{
    /// <summary>
    /// Imports the temp art set (PLAN.md S11) into Assets/VirtualBeeSwarm/Art/&lt;Slot&gt;/ and makes one prefab per slot,
    /// Art/&lt;Slot&gt;/&lt;Slot&gt;.prefab, which the scene builder then uses (ArtSlots). FBX only: GLB would need glTFast,
    /// which isn't installed.
    ///
    ///   Menu: Virtual Bee Swarm > Art > Import staged art (FBX)...  (pick the staging "final" folder)
    ///         Virtual Bee Swarm > Art > Rebuild art prefabs         (after replacing an FBX in Art/&lt;Slot&gt;/ by hand)
    ///   Batch: VBS_ART_DIR=~/immerse-the-bay/art-staging/final Unity -batchmode -nographics -quit -projectPath P
    ///          -executeMethod VirtualBeeSwarm.Editor.ArtImport.ImportBatch -logFile art-import.log
    ///
    /// What a prefab is: the FBX as exported (meters, Y-up, front = +Z, every node unrotated and unscaled), unpacked,
    /// with project materials. The slot table in ArtSlots turns it to face the player and sizes it in the scene.
    /// Materials (all VirtualBeeSwarm/Unlit, so instancing and single-pass stereo work as for the placeholders):
    ///   VBS_Palette      -> Art/_Shared/VBS_ArtPalette     (palette texture, point filter; correct colours in Linear)
    ///   VBS_Palette_Bee  -> Art/_Shared/VBS_ArtBee         (same, a little brighter, two-sided for wings)
    ///   VBS_Glow         -> Art/_Shared/VBS_ArtGlow        (bright: lit hex face, embers)
    ///   VBS_GlowSoft     -> Art/_Shared/VBS_ArtGlowSoft    (additive halo)
    ///   VBS_PaneLive / VBS_PaneFallback -> Materials/VBS_PaneLive / VBS_Pane (what FutureWindow drives)
    /// Per slot: Hive HeatGlow -> Materials/VBS_Glow (HiveBody tints it), Portal Glow -> Materials/VBS_PortalGlow,
    /// Rocket Flame -> Materials/VBS_Flame.
    /// The worker bee for the swarm is not this prefab's material: ArtSlots bakes Art/Bee/Bee_unit.asset (1 unit long,
    /// linear vertex colours) and every pool keeps its own instanced material (VBS_Bee).
    /// </summary>
    public static class ArtImport
    {
        /// <summary>Slot name (Art/&lt;Slot&gt;/&lt;Slot&gt;.prefab) and the staging item it comes from (&lt;item&gt;/&lt;item&gt;.fbx).</summary>
        public static readonly string[,] Items =
        {
            { "Bee", "worker_bee" }, { "Beeswax", "beeswax" }, { "Hive", "hive" }, { "HexTile", "hexagon_tile" },
            { "Portal", "portal_frame" }, { "Smoker", "smoker" }, { "HoneyJar", "honey_jar" },
            { "SyrupBottle", "syrup_bottle" }, { "DeepBowl", "deep_bowl" }, { "PebbleDish", "pebble_dish" },
            { "Wasp", "wasp" }, { "Hornet", "giant_hornet" }, { "Ember", "ember" }, { "Flowers", "flowers" },
            { "GrassTuft", "grass_tuft" }, { "Rocket", "rocket" }, { "Moon", "moon" }, { "Earth", "earth" },
            { "Clipboard", "clipboard" }, { "SeedPacket", "seed_packet" },
            // v3.1 props
            { "Swatter", "swatter" }, { "SprayBottle", "spray_bottle" }, { "Crowbar", "crowbar" },
            { "Reducer", "reducer" }, { "RainCloud", "rain_cloud" }, { "StampApproved", "stamp_approved" },
            { "StampGrievance", "stamp_grievance" }, { "SignHere", "clipboard_sign" }, { "ScoreCard", "score_card" },
        };

        /// <summary>Renderer (by object name) -> material in Materials/, per slot. Applied when the prefab is made.</summary>
        static readonly Dictionary<string, string[,]> SlotMaterials = new Dictionary<string, string[,]>
        {
            { "Hive", new[,] { { "HeatGlow", "VBS_Glow" } } },
            { "Portal", new[,] { { "LivePane", "VBS_PaneLive" }, { "FallbackPane", "VBS_Pane" }, { "Glow", "VBS_PortalGlow" } } },
            { "Rocket", new[,] { { "Flame", "VBS_Flame" } } },
            // wing-tagged meshes (art/blender/scripts/wings.py): VBS_ArtBee + the VBS_WINGS flap
            { "Wasp", new[,] { { "WingL", "VBS_ArtWing" }, { "WingR", "VBS_ArtWing" } } },
            { "Hornet", new[,] { { "WingL", "VBS_ArtWing" }, { "WingR", "VBS_ArtWing" } } },
        };

        const string DefaultStaging = "~/immerse-the-bay/art-staging/final";
        const string PaletteFile = "VBS_Palette.png";

        public static string SharedDir => ArtSlots.ArtRoot + "/_Shared";

        [MenuItem("Virtual Bee Swarm/Art/Import staged art (FBX)...")]
        static void ImportMenu()
        {
            string dir = EditorUtility.OpenFolderPanel("Art staging folder (the one with MANIFEST.json)", Expand(DefaultStaging), "");
            if (!string.IsNullOrEmpty(dir)) Import(dir);
        }

        [MenuItem("Virtual Bee Swarm/Art/Rebuild art prefabs")]
        static void RebuildMenu() => RebuildPrefabs();

        /// <summary>-executeMethod entry: imports from VBS_ART_DIR (default ~/immerse-the-bay/art-staging/final). Exit 1 on failure.</summary>
        public static void ImportBatch()
        {
            try
            {
                string dir = Environment.GetEnvironmentVariable("VBS_ART_DIR");
                Import(Expand(string.IsNullOrEmpty(dir) ? DefaultStaging : dir));
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[VBS][Art] import FAILED: " + e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        /// <summary>-executeMethod entry: rebuild the prefabs from the FBX files already in Art/.</summary>
        public static void RebuildBatch()
        {
            try
            {
                RebuildPrefabs();
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[VBS][Art] rebuild FAILED: " + e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        static string Expand(string p) =>
            p.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Substring(2)) : p;

        public static void Import(string stagingDir)
        {
            if (!Directory.Exists(stagingDir)) throw new DirectoryNotFoundException(stagingDir);
            string art = ArtSlots.ArtRoot;
            EnsureFolder(SharedDir);
            string pal = Path.Combine(stagingDir, "_shared", PaletteFile);
            if (!File.Exists(pal)) throw new FileNotFoundException("palette texture missing", pal);
            File.Copy(pal, SharedDir + "/" + PaletteFile, true);
            int copied = 0;
            var missing = new List<string>();
            for (int i = 0; i < Items.GetLength(0); i++)
            {
                string slot = Items[i, 0], item = Items[i, 1];
                string src = Path.Combine(stagingDir, item, item + ".fbx");
                if (!File.Exists(src)) { missing.Add(item); continue; }
                EnsureFolder(art + "/" + slot);
                File.Copy(src, art + "/" + slot + "/" + item + ".fbx", true);
                copied++;
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log("[VBS][Art] copied " + copied + " FBX + " + PaletteFile + " from " + stagingDir +
                      (missing.Count > 0 ? " (not in staging: " + string.Join(", ", missing) + ")" : ""));
            RebuildPrefabs();
        }

        /// <summary>Materials, FBX import settings + material remaps, then one prefab per slot that has an FBX.</summary>
        public static void RebuildPrefabs()
        {
            var mats = EnsureMaterials();
            int made = 0;
            var report = new List<string>();
            for (int i = 0; i < Items.GetLength(0); i++)
            {
                string slot = Items[i, 0];
                string fbx = FindModel(slot, Items[i, 1]);
                if (fbx == null) continue;
                ConfigureModel(fbx, mats);
                var prefab = MakePrefab(slot, fbx);
                if (prefab == null) continue;
                made++;
                report.Add(Describe(slot, prefab));
            }
            AssetDatabase.SaveAssets();
            var unit = ArtSlots.BeeUnitMesh();
            foreach (var line in report) Debug.Log("[VBS][Art] " + line);
            Debug.Log("[VBS][Art] " + made + " prefabs in " + ArtSlots.ArtRoot + "/<Slot>/<Slot>.prefab" +
                      (unit != null ? "; swarm bee " + AssetDatabase.GetAssetPath(unit) + " (" + unit.vertexCount + " verts, " +
                                      unit.subMeshCount + " submesh, " + (unit.triangles.Length / 3) + " tris)" : ""));
        }

        /// <summary>Art/&lt;Slot&gt;/&lt;item&gt;.fbx, or the only .fbx in Art/&lt;Slot&gt;/ (so a hand-made model with another name works).</summary>
        static string FindModel(string slot, string item)
        {
            string dir = ArtSlots.ArtRoot + "/" + slot;
            string p = dir + "/" + item + ".fbx";
            if (File.Exists(p)) return p;
            if (!Directory.Exists(dir)) return null;
            var all = Directory.GetFiles(dir, "*.fbx");
            return all.Length == 1 ? all[0].Replace('\\', '/') : null;
        }

        // ------------------------------------------------------------------------------------------ materials
        static Dictionary<string, Material> EnsureMaterials()
        {
            EnsureFolder(SharedDir);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(SharedDir + "/" + PaletteFile);
            var m = new Dictionary<string, Material>
            {
                ["VBS_Palette"] = Mat("VBS_ArtPalette", GameMaterials.Blend.Opaque, tex, 1f, false),
                ["VBS_Palette_Bee"] = Mat("VBS_ArtBee", GameMaterials.Blend.Opaque, tex, 1.12f, true),
                ["VBS_Glow"] = Mat("VBS_ArtGlow", GameMaterials.Blend.Opaque, tex, 1.35f, false),
                ["VBS_GlowSoft"] = Mat("VBS_ArtGlowSoft", GameMaterials.Blend.Additive, tex, 1f, true, 0.35f),
            };
            string matDir = ArtSlots.VbsRoot + "/Materials/";
            var live = AssetDatabase.LoadAssetAtPath<Material>(matDir + "VBS_PaneLive.mat");
            var pane = AssetDatabase.LoadAssetAtPath<Material>(matDir + "VBS_Pane.mat");
            if (live != null) m["VBS_PaneLive"] = live;
            if (pane != null) m["VBS_PaneFallback"] = pane;
            return m;
        }

        static Material Mat(string name, GameMaterials.Blend blend, Texture tex, float emission, bool twoSided, float alpha = 1f)
        {
            string path = SharedDir + "/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = GameMaterials.Create(name, new Color(1f, 1f, 1f, alpha), blend, vertexColors: false, emission: emission,
                    instancing: true, doubleSided: twoSided);
                if (tex != null && mat.HasProperty(GameMaterials.MainTexId)) mat.SetTexture(GameMaterials.MainTexId, tex);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (tex != null && mat.HasProperty(GameMaterials.MainTexId) && mat.GetTexture(GameMaterials.MainTexId) == null)
            {
                mat.SetTexture(GameMaterials.MainTexId, tex);
                EditorUtility.SetDirty(mat);
            }
            return mat;
        }

        // ------------------------------------------------------------------------------------------ models
        /// <summary>The import settings every art FBX gets (also applied by ArtImportSettings on a first import).</summary>
        public static void ApplySettings(ModelImporter mi)
        {
            mi.globalScale = 1f;
            mi.useFileScale = true;
            mi.bakeAxisConversion = false;
            mi.preserveHierarchy = false;
            mi.sortHierarchyByName = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importVisibility = false;
            mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.importBlendShapes = false;
            mi.addCollider = false;
            mi.isReadable = false;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.importNormals = ModelImporterNormals.Import;
            mi.importTangents = ModelImporterTangents.None;
            mi.generateSecondaryUV = false;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
        }

        static void ConfigureModel(string path, Dictionary<string, Material> mats)
        {
            var mi = (ModelImporter)AssetImporter.GetAtPath(path);
            ApplySettings(mi);
            // remap the file's materials (by name) to ours
            var map = mi.GetExternalObjectMap();
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (!(o is Material src)) continue;
                if (!mats.TryGetValue(src.name, out var dst) || dst == null) continue;
                var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), src.name);
                if (map.TryGetValue(id, out var cur) && cur == dst) continue;
                mi.AddRemap(id, dst);
            }
            foreach (var kv in mats)
            {
                var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key);
                if (kv.Value != null && (!map.TryGetValue(id, out var cur) || cur != kv.Value)) mi.AddRemap(id, kv.Value);
            }
            mi.SaveAndReimport();
        }

        static GameObject MakePrefab(string slot, string fbx)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            if (model == null) { Debug.LogWarning("[VBS][Art] " + fbx + " did not import as a model"); return null; }
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
                }
                if (SlotMaterials.TryGetValue(slot, out var overrides))
                {
                    for (int i = 0; i < overrides.GetLength(0); i++)
                    {
                        var t = ArtSlots.FindPath(go.transform, overrides[i, 0]);
                        var mat = AssetDatabase.LoadAssetAtPath<Material>(ArtSlots.VbsRoot + "/Materials/" + overrides[i, 1] + ".mat");
                        var r = t != null ? t.GetComponent<Renderer>() : null;
                        if (r != null && mat != null) r.sharedMaterial = mat;
                        else Debug.LogWarning("[VBS][Art] " + slot + ": no " + overrides[i, 0] + " renderer or " + overrides[i, 1] + " material");
                    }
                }
                string path = ArtSlots.PrefabPath(slot);
                var prefab = PrefabUtility.SaveAsPrefabAsset(go, path, out bool ok);
                if (!ok) throw new Exception("SaveAsPrefabAsset failed: " + path);
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        static string Describe(string slot, GameObject prefab)
        {
            int tris = 0, renderers = 0;
            var b = new Bounds();
            bool any = false;
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                renderers++;
                tris += mf.sharedMesh.triangles.Length / 3;
                var m = prefab.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(c);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            var rot = new List<string>();
            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
                if (t != prefab.transform && (Quaternion.Angle(t.localRotation, Quaternion.identity) > 0.5f || (t.localScale - Vector3.one).sqrMagnitude > 1e-6f))
                    rot.Add(t.name);
            return slot + ": " + renderers + " meshes, " + tris + " tris, size " + b.size.x.ToString("0.000") + " x " +
                   b.size.y.ToString("0.000") + " x " + b.size.z.ToString("0.000") + " m, center " + b.center.ToString("F3") +
                   (rot.Count > 0 ? ", ROTATED/SCALED nodes: " + string.Join(", ", rot) : ", all nodes unrotated");
        }

        // ------------------------------------------------------------------------------------------ screenshot
        /// <summary>
        /// -executeMethod entry (needs graphics: no -nographics; on a headless Linux box wrap Unity in xvfb-run -a):
        /// opens the built main scene and renders it from GameRig/DesktopCamera (the desktop player's viewpoint) into
        /// VBS_SHOT (default Logs/art-scene.png), 1600 x 1000. The beat UI that is hidden at runtime (pre-UI text, the
        /// level map, the what-if UI, the choice buttons) is switched off for the shot, the scene file is not saved,
        /// and 60 swarm bees are drawn around the hive with the scene's own bee mesh + material through
        /// Graphics.RenderMeshInstanced (one instanced draw), so the shot also proves the instanced art bee.
        /// VBS_SHOT_RAW=1 keeps the scene exactly as built (everything the builder left active).
        /// </summary>
        public static void ScreenshotBatch()
        {
            try
            {
                string outPath = Environment.GetEnvironmentVariable("VBS_SHOT");
                if (string.IsNullOrEmpty(outPath)) outPath = "Logs/art-scene.png";
                bool raw = Environment.GetEnvironmentVariable("VBS_SHOT_RAW") == "1";
                var scene = EditorSceneManager.OpenScene(BuildGameScene.MainScenePath, OpenSceneMode.Single);
                Camera cam = null;
                Transform hive = null;
                SwarmSystem swarm = null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (t.name == "DesktopCamera") cam = t.GetComponent<Camera>();
                        if (!raw && (t.name == "PreUI" || t.name == "LevelMap" || t.name == "WhatIfUI" || t.name == "Buttons" ||
                                     t.name == "PlacementMarkers" || t.name == "MoonScene"))
                            t.gameObject.SetActive(false);
                    }
                    if (hive == null) hive = root.GetComponentInChildren<HiveBody>(true)?.transform;
                    if (swarm == null) swarm = root.GetComponentInChildren<SwarmSystem>(true);
                }
                if (cam == null) throw new Exception("no DesktopCamera in " + BuildGameScene.MainScenePath);
                cam.enabled = true;
                cam.gameObject.SetActive(true);
                const int w = 1600, h = 1000;
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
                cam.targetTexture = rt;
                cam.aspect = (float)w / h;

                string beeInfo = "no swarm";
                if (swarm != null && hive != null)
                {
                    var so = new SerializedObject(swarm);
                    var mesh = so.FindProperty("beeMesh").objectReferenceValue as Mesh;
                    var mat = so.FindProperty("beeMaterial").objectReferenceValue as Material;
                    float len = so.FindProperty("beeLength").floatValue;
                    if (mesh != null && mat != null)
                    {
                        const int n = 60;
                        var mats = new Matrix4x4[n];
                        var cols = new Vector4[n];
                        var center = hive.position + Vector3.up * 0.22f;
                        var rnd = new System.Random(7);
                        for (int i = 0; i < n; i++)
                        {
                            float a = (float)(rnd.NextDouble() * Math.PI * 2), r = 0.16f + (float)rnd.NextDouble() * 0.16f;
                            var p = center + new Vector3(Mathf.Cos(a) * r, (float)rnd.NextDouble() * 0.22f - 0.05f, Mathf.Sin(a) * r * 0.8f);
                            var fwd = new Vector3(-Mathf.Sin(a), 0.15f, Mathf.Cos(a));
                            mats[i] = Matrix4x4.TRS(p, Quaternion.LookRotation(fwd), Vector3.one * len * 1.3f);
                            cols[i] = Color.white;
                        }
                        var mpb = new MaterialPropertyBlock();
                        mpb.SetVectorArray(GameMaterials.ColorId, cols);
                        var rp = new RenderParams(mat) { camera = cam, matProps = mpb, shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off };
                        Graphics.RenderMeshInstanced(rp, mesh, 0, mats);
                        beeInfo = n + " bees, mesh " + mesh.name + " (" + mesh.subMeshCount + " submesh, " + mesh.triangles.Length / 3 +
                                  " tris), material " + mat.name + " (instancing " + mat.enableInstancing + ")";
                    }
                }
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                cam.targetTexture = null;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
                File.WriteAllBytes(outPath, tex.EncodeToPNG());
                Debug.Log("[VBS][Art] screenshot " + Path.GetFullPath(outPath) + " from " + cam.name + " at " + cam.transform.position.ToString("F2") +
                          (raw ? " (scene as built)" : " (beat UI hidden)") + "; " + beeInfo);
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[VBS][Art] screenshot FAILED: " + e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }

    /// <summary>Import settings for anything dropped into Assets/VirtualBeeSwarm/Art/ (first import only, so hand
    /// tweaks in the inspector stick): FBX as in ArtImport.ApplySettings, the palette texture point-filtered,
    /// uncompressed, no mipmaps.</summary>
    class ArtImportSettings : AssetPostprocessor
    {
        static bool IsArt(string path) => path.Replace('\\', '/').Contains("/VirtualBeeSwarm/Art/");

        void OnPreprocessModel()
        {
            if (!IsArt(assetPath) || !(assetImporter is ModelImporter mi) || !mi.importSettingsMissing) return;
            ArtImport.ApplySettings(mi);
        }

        void OnPreprocessTexture()
        {
            if (!IsArt(assetPath) || !(assetImporter is TextureImporter ti)) return;
            if (!assetPath.Replace('\\', '/').Contains("/Art/_Shared/")) return;
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.mipmapEnabled = false;
            ti.filterMode = FilterMode.Point;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.maxTextureSize = 256;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
        }
    }
}
