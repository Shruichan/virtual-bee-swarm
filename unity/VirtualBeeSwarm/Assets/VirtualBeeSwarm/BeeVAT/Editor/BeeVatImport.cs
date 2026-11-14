using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace VirtualBeeSwarm.BeeVatEditor
{
    /// <summary>
    /// Turns Art/BeeVAT/worker_bee.vat.bytes (art/blender/scripts/bake_vat.py) into what BeeVat.Apply loads:
    /// Art/BeeVAT/Resources/BeeVAT_Mesh.asset (the rest pose, vertex colours, uv1 = VAT column + wing weight, bounds
    /// over every baked frame), BeeVAT_Pos.asset (RGBAHalf, column = source vertex, row = frame), BeeVAT_Nrm.asset
    /// (RGBA32) and BeeVAT.mat (Shaders/BeeVAT.shader; hand tweaks to speed/shade are kept on a rebuild).
    /// Runs by itself when the .vat.bytes file is (re)imported; also the menu item and, headless,
    ///   Unity -batchmode -nographics -quit -projectPath P -executeMethod VirtualBeeSwarm.BeeVatEditor.BeeVatImport.RebuildBatch
    /// </summary>
    public static class BeeVatImport
    {
        public const string Dir = "Assets/VirtualBeeSwarm/Art/BeeVAT";
        public const string Source = Dir + "/worker_bee.vat.bytes";
        public const string Res = Dir + "/Resources";
        public const string ShaderName = "VirtualBeeSwarm/BeeVAT";

        [Serializable]
        class Clip { public string name; public int start; public int frames; public float fps; public int wing_beats; }

        [Serializable]
        class Header
        {
            public int version;
            public string rig, mesh, blender, source;
            public int src_verts, verts, tris, rows, wing_verts;
            public Clip[] clips;
            public float[] bounds_min, bounds_max;
        }

        [MenuItem("Virtual Bee Swarm/Art/Rebuild swarm bee VAT")]
        public static void Menu() => Rebuild();

        public static void RebuildBatch()
        {
            bool ok;
            try { ok = Rebuild() != null; }
            catch (Exception e) { Debug.LogError("[VBS][VAT] rebuild FAILED: " + e); ok = false; }
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        public static Material Rebuild()
        {
            if (!File.Exists(Source)) { Debug.LogError("[VBS][VAT] no " + Source); return null; }
            var bytes = File.ReadAllBytes(Source);
            if (bytes.Length < 8 || Encoding.ASCII.GetString(bytes, 0, 4) != "VAT1") throw new Exception(Source + " is not a VAT1 file");
            int hl = BitConverter.ToInt32(bytes, 4);
            var h = JsonUtility.FromJson<Header>(Encoding.UTF8.GetString(bytes, 8, hl));
            int o = 8 + hl;
            int S = h.src_verts, R = h.rows, V = h.verts, T = h.tris;
            float[] pos = Floats(bytes, ref o, R * S * 3);
            float[] nrm = Floats(bytes, ref o, R * S * 3);
            int[] src = Ints(bytes, ref o, V);
            float[] rgba = Floats(bytes, ref o, V * 4);
            float[] uv = Floats(bytes, ref o, V * 2);
            int[] tris = Ints(bytes, ref o, T * 3);
            if (o != bytes.Length) throw new Exception(Source + ": " + (bytes.Length - o) + " bytes left over (format mismatch)");

            EnsureFolder(Res);

            // positions: RGBAHalf, column = source vertex, row = frame
            var posData = new ushort[S * R * 4];
            var nrmData = new Color32[S * R];
            for (int r = 0; r < R; r++)
                for (int s = 0; s < S; s++)
                {
                    int k = (r * S + s) * 3, t = r * S + s;
                    posData[t * 4 + 0] = Mathf.FloatToHalf(pos[k]);
                    posData[t * 4 + 1] = Mathf.FloatToHalf(pos[k + 1]);
                    posData[t * 4 + 2] = Mathf.FloatToHalf(pos[k + 2]);
                    posData[t * 4 + 3] = Mathf.FloatToHalf(1f);
                    nrmData[t] = new Color32(B(nrm[k]), B(nrm[k + 1]), B(nrm[k + 2]), 255);
                }
            var posTex = new Texture2D(S, R, TextureFormat.RGBAHalf, false, true) { name = "BeeVAT_Pos", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, anisoLevel = 0 };
            posTex.SetPixelData(posData, 0);
            posTex.Apply(false, false);
            var nrmTex = new Texture2D(S, R, TextureFormat.RGBA32, false, true) { name = "BeeVAT_Nrm", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, anisoLevel = 0 };
            nrmTex.SetPixels32(nrmData);
            nrmTex.Apply(false, false);
            posTex = Save(posTex, Res + "/BeeVAT_Pos.asset");
            nrmTex = Save(nrmTex, Res + "/BeeVAT_Nrm.asset");

            // mesh: rest pose (row 0), one vertex per (source vertex, material)
            bool linear = PlayerSettings.colorSpace == ColorSpace.Linear;
            var verts = new Vector3[V];
            var norms = new Vector3[V];
            var cols = new Color[V];
            var uv1 = new Vector2[V];
            for (int i = 0; i < V; i++)
            {
                int k = src[i] * 3;
                verts[i] = new Vector3(pos[k], pos[k + 1], pos[k + 2]);
                norms[i] = new Vector3(nrm[k], nrm[k + 1], nrm[k + 2]);
                var c = new Color(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
                cols[i] = linear ? c : c.gamma; // Blender's base colours are scene-linear
                uv1[i] = new Vector2(uv[i * 2], uv[i * 2 + 1]);
            }
            var mesh = new Mesh { name = "BeeVAT_Mesh", indexFormat = V > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.colors = cols;
            mesh.uv = new Vector2[V];
            mesh.SetUVs(1, uv1);
            mesh.SetTriangles(tris, 0);
            var bmin = new Vector3(h.bounds_min[0], h.bounds_min[1], h.bounds_min[2]);
            var bmax = new Vector3(h.bounds_max[0], h.bounds_max[1], h.bounds_max[2]);
            var b = new Bounds((bmin + bmax) * 0.5f, bmax - bmin);
            b.Expand(0.05f);
            mesh.bounds = b; // every baked frame fits, so instanced culling never clips a wing
            mesh = Save(mesh, Res + "/BeeVAT_Mesh.asset");

            // material: keep a hand-tuned one, refresh only what the bake decides
            string matPath = Res + "/BeeVAT.mat";
            var shader = Shader.Find(ShaderName);
            if (shader == null) throw new Exception("shader " + ShaderName + " not found");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(shader) { name = "BeeVAT" };
                mat.SetFloat("_Cull", (float)CullMode.Off); // the wings are single planes
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.shader = shader;
            mat.enableInstancing = true;
            mat.SetTexture("_VatPos", posTex);
            mat.SetTexture("_VatNrm", nrmTex);
            mat.SetVector("_VatSize", new Vector4(S, R, 0, 0));
            Clip hover = Find(h, "Hover") ?? h.clips[0], walk = Find(h, "Walk");
            mat.SetVector("_VatHover", ClipVec(hover));
            mat.SetVector("_VatWalk", walk != null ? ClipVec(walk) : new Vector4(0, 0, 30, 0));
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();

            Debug.Log("[VBS][VAT] rebuilt from " + Source + " (" + h.source + ", " + h.mesh + ", Blender " + h.blender + "): " + V + " verts (" + S +
                      " source), " + T + " tris, " + h.wing_verts + " wing verts, textures " + S + " x " + R + ", clips " +
                      string.Join(", ", Array.ConvertAll(h.clips, c => c.name + " rows " + c.start + "+" + c.frames + " @" + c.fps + " fps, " + c.wing_beats + " wing beats")) +
                      ", bounds " + b.min.ToString("F3") + ".." + b.max.ToString("F3"));
            return mat;
        }

        static Clip Find(Header h, string name)
        {
            foreach (var c in h.clips) if (c.name == name) return c;
            return null;
        }

        static Vector4 ClipVec(Clip c) => new Vector4(c.start, c.frames, c.fps, c.wing_beats);

        static byte B(float n) => (byte)Mathf.Clamp(Mathf.RoundToInt((n * 0.5f + 0.5f) * 255f), 0, 255);

        static float[] Floats(byte[] b, ref int o, int n)
        {
            var a = new float[n];
            Buffer.BlockCopy(b, o, a, 0, n * 4);
            o += n * 4;
            return a;
        }

        static int[] Ints(byte[] b, ref int o, int n)
        {
            var a = new int[n];
            Buffer.BlockCopy(b, o, a, 0, n * 4);
            o += n * 4;
            return a;
        }

        /// <summary>Create the asset, or overwrite the existing one in place (keeps its GUID and every reference).</summary>
        static T Save<T>(T obj, string path) where T : Object
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(obj, path);
                return obj;
            }
            EditorUtility.CopySerialized(obj, existing);
            Object.DestroyImmediate(obj);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        // ------------------------------------------------------------------------------------------ checks + still
        /// <summary>
        /// Headless check: the assets load through BeeVat, every pool's swap works, and (via ArShaderCheck) the shader
        /// compiles for Vulkan in every occlusion/instancing/multiview variant. Logs "[VBS][VAT] check: PASS/FAIL".
        ///   Unity -batchmode -nographics -projectPath P -executeMethod VirtualBeeSwarm.BeeVatEditor.BeeVatImport.CheckBatch
        /// (no -quit: it runs a few editor ticks after start-up and exits by itself, 0 = PASS)
        /// </summary>
        public static void CheckBatch()
        {
            // the Packages/ include of the occlusion variants doesn't always resolve straight after start-up (the same
            // happens to VirtualBeeSwarm/Unlit), so run a few editor ticks later; run it WITHOUT -quit
            int ticks = 0;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (++ticks < 30) return;
                EditorApplication.update -= tick;
                Check();
            };
            if (Application.isBatchMode) EditorApplication.update += tick;
            else Check();
        }

        static void Check()
        {
            var fails = new List<string>();
            // the occlusion variants' Packages/ include only resolves in a run that imported the shader (seen with
            // VirtualBeeSwarm/Unlit too), so reimport both before compiling the variants
            foreach (var sn in new[] { ShaderName, GameMaterials.ShaderName })
            {
                var sh = Shader.Find(sn);
                if (sh != null) AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(sh), ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            }
            try
            {
                var mesh = Resources.Load<Mesh>(BeeVat.MeshResource);
                var mat = Resources.Load<Material>(BeeVat.MaterialResource);
                if (mesh == null) fails.Add("no Resources/" + BeeVat.MeshResource);
                if (mat == null) fails.Add("no Resources/" + BeeVat.MaterialResource);
                if (mat != null && mat.shader.name != ShaderName) fails.Add("material shader is " + mat.shader.name);
                if (mat != null && (mat.GetTexture("_VatPos") == null || mat.GetTexture("_VatNrm") == null)) fails.Add("material without VAT textures");
                if (mat != null && !mat.enableInstancing) fails.Add("material without instancing");
                if (mesh != null && mesh.uv2 == null || mesh != null && mesh.uv2.Length != mesh.vertexCount) fails.Add("mesh without uv1 (VAT column)");
                Mesh m = null; Material mt = null;
                BeeVat.Reload();
                if (!BeeVat.Apply(ref m, ref mt, "check")) fails.Add("BeeVat.Apply refused");
                else if (m != mesh || mt == null || mt == mat) fails.Add("BeeVat.Apply did not hand out the VAT mesh + its own material");
                var check = Type.GetType("VirtualBeeSwarm.AR.Editor.ArShaderCheck, VirtualBeeSwarm.AR.Editor");
                var method = check?.GetMethod("Check");
                if (method == null) fails.Add("ArShaderCheck not found (shader variants not compiled)");
                else
                {
                    var args = new object[] { ShaderName, null };
                    bool ok = (bool)method.Invoke(null, args);
                    var hashes = args[1] as Dictionary<string, string>;
                    if (!ok) fails.Add("shader variants failed to compile (see [VBS][AR] lines)");
                    else Debug.Log("[VBS][VAT] " + (hashes != null ? hashes.Count : 0) + " Vulkan variant stages compiled");
                    // control: the project's own unlit shader through the same check, same process
                    var ctl = new object[] { GameMaterials.ShaderName, null };
                    bool ctlOk = (bool)method.Invoke(null, ctl);
                    Debug.Log("[VBS][VAT] control " + GameMaterials.ShaderName + ": " + (ctlOk ? "PASS" : "FAIL") +
                              (ctlOk == ok ? "" : " (differs from BeeVAT!)"));
                }
                Debug.Log("[VBS][VAT] check: " + (fails.Count == 0 ? "PASS" : "FAIL: " + string.Join("; ", fails)) +
                          (mesh != null ? " (" + mesh.vertexCount + " verts, " + mesh.triangles.Length / 3 + " tris)" : ""));
            }
            catch (Exception e)
            {
                fails.Add(e.Message);
                Debug.LogError("[VBS][VAT] check: FAIL " + e);
            }
            if (Application.isBatchMode) EditorApplication.Exit(fails.Count == 0 ? 0 : 1);
        }

        /// <summary>
        /// Close-up still of the VAT swarm (needs graphics: xvfb-run, no -nographics). Opens the built main scene,
        /// draws 4 rows of bees in the pools' colours (idle, angry, dark, gold) plus 6 landed walkers (_WingFlap 0)
        /// in an empty spot away from the room with one Graphics.RenderMeshInstanced per group, from a camera 30 cm away.
        /// VBS_SHOT = png path (default Logs/beevat.png), VBS_SHOT_N = how many frames 1/30 s apart (default 1),
        /// VBS_SHOT_OLD=1 = the same layout with the scene's old swarm bee (for A/B).
        ///   xvfb-run -a Unity -batchmode -quit -projectPath P -executeMethod VirtualBeeSwarm.BeeVatEditor.BeeVatImport.ShotBatch
        /// </summary>
        public static void ShotBatch()
        {
            int code = 0;
            try
            {
                string outPath = Environment.GetEnvironmentVariable("VBS_SHOT");
                if (string.IsNullOrEmpty(outPath)) outPath = "Logs/beevat.png";
                int frames = int.TryParse(Environment.GetEnvironmentVariable("VBS_SHOT_N"), out var nf) ? Mathf.Max(1, nf) : 1;
                bool old = Environment.GetEnvironmentVariable("VBS_SHOT_OLD") == "1";
                var scene = EditorSceneManager.OpenScene(VirtualBeeSwarm.Editor.BuildGameScene.MainScenePath, OpenSceneMode.Single);
                SwarmSystem swarm = null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (swarm == null) swarm = root.GetComponentInChildren<SwarmSystem>(true);
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        if (t.name == "PreUI" || t.name == "LevelMap" || t.name == "WhatIfUI" || t.name == "Buttons" || t.name == "PlacementMarkers" || t.name == "MoonScene")
                            t.gameObject.SetActive(false);
                }
                Mesh mesh; Material mat;
                if (old)
                {
                    var so = new SerializedObject(swarm);
                    mesh = so.FindProperty("beeMesh").objectReferenceValue as Mesh;
                    mat = so.FindProperty("beeMaterial").objectReferenceValue as Material;
                }
                else
                {
                    mesh = null; mat = null;
                    BeeVat.Reload();
                    if (!BeeVat.Apply(ref mesh, ref mat, "shot")) throw new Exception("BeeVat.Apply refused");
                }
                // a clear spot well away from the room (nothing of the scene in the frame), lit by nothing: unlit bees
                Vector3 c = new Vector3(0f, 40f, 40f);
                var camGo = new GameObject("BeeVatShotCam");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.55f, 0.62f, 0.68f);
                cam.nearClipPlane = 0.02f;
                cam.fieldOfView = 40f;
                camGo.transform.position = c + new Vector3(0.03f, 0.05f, -0.30f);
                camGo.transform.LookAt(c + new Vector3(0f, 0.0f, 0f));
                const int w = 1600, hgt = 1000;
                var rt = new RenderTexture(w, hgt, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
                cam.targetTexture = rt;
                cam.aspect = (float)w / hgt;

                Color[] tints =
                {
                    new Color(1f, 0.85f, 0.3f) * 1.3f,  // idle (SwarmSystem)
                    new Color(1f, 0.35f, 0.15f) * 1.3f, // angry
                    new Color(0.16f, 0.12f, 0.1f),      // dark swarm
                    new Color(1f, 0.78f, 0.2f) * 1.4f,  // gold
                };
                const float len = 0.035f;
                for (int f = 0; f < frames; f++)
                {
                    for (int row = 0; row < 4; row++)
                    {
                        const int n = 7;
                        var mats = new Matrix4x4[n];
                        var cols = new Vector4[n];
                        for (int i = 0; i < n; i++)
                        {
                            var p = c + new Vector3((i - 3) * 0.045f, 0.075f - row * 0.045f, (i % 2) * 0.02f);
                            var fwd = Quaternion.Euler(0f, -120f + i * 40f + row * 10f, 0f) * Vector3.forward;
                            mats[i] = Matrix4x4.TRS(p, Quaternion.LookRotation(fwd), Vector3.one * len);
                            cols[i] = tints[row];
                            cols[i].w = 1f;
                        }
                        var mpb = new MaterialPropertyBlock();
                        mpb.SetVectorArray(GameMaterials.ColorId, cols);
                        Graphics.RenderMeshInstanced(new RenderParams(mat) { camera = cam, matProps = mpb, shadowCastingMode = ShadowCastingMode.Off }, mesh, 0, mats);
                    }
                    {
                        const int n = 6;
                        var mats = new Matrix4x4[n];
                        var cols = new Vector4[n];
                        var flap = new float[n];
                        for (int i = 0; i < n; i++)
                        {
                            var p = c + new Vector3((i - 2.5f) * 0.05f, -0.115f, 0.03f);
                            mats[i] = Matrix4x4.TRS(p, Quaternion.Euler(0f, 60f + i * 50f, 0f), Vector3.one * len);
                            cols[i] = tints[0];
                            cols[i].w = 1f;
                        }
                        var mpb = new MaterialPropertyBlock();
                        mpb.SetVectorArray(GameMaterials.ColorId, cols);
                        mpb.SetFloatArray("_WingFlap", flap);
                        Graphics.RenderMeshInstanced(new RenderParams(mat) { camera = cam, matProps = mpb, shadowCastingMode = ShadowCastingMode.Off }, mesh, 0, mats);
                    }
                    cam.Render();
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(w, hgt, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, w, hgt), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    string p2 = frames == 1 ? outPath : Path.ChangeExtension(outPath, null) + "-" + f + ".png";
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(p2)));
                    File.WriteAllBytes(p2, tex.EncodeToPNG());
                    Debug.Log("[VBS][VAT] still " + Path.GetFullPath(p2) + " (" + (old ? "old bee " : "VAT bee ") + mesh.name + ", " + mat.shader.name + ")");
                    if (f + 1 < frames) System.Threading.Thread.Sleep(33);
                }
                cam.targetTexture = null;
            }
            catch (Exception e)
            {
                Debug.LogError("[VBS][VAT] still FAILED: " + e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }
    }

    /// <summary>
    /// Rough GPU A/B (needs graphics: xvfb-run): 300 bees in one instanced draw, old swarm bee vs VAT bee, 240 frames
    /// each into a 1920 x 1080 4x MSAA target, every frame synced by a 1-pixel read-back. Desktop numbers, so only
    /// the ratio means anything for the Quest. Logs "[VBS][VAT] bench ...".
    ///   xvfb-run -a Unity -batchmode -quit -projectPath P -executeMethod VirtualBeeSwarm.BeeVatEditor.BeeVatBench.Run
    /// </summary>
    public static class BeeVatBench
    {
        public static void Run()
        {
            int code = 0;
            try
            {
                var scene = EditorSceneManager.OpenScene(VirtualBeeSwarm.Editor.BuildGameScene.MainScenePath, OpenSceneMode.Single);
                SwarmSystem swarm = null;
                foreach (var root in scene.GetRootGameObjects())
                    if (swarm == null) swarm = root.GetComponentInChildren<SwarmSystem>(true);
                var so = new SerializedObject(swarm);
                var oldMesh = so.FindProperty("beeMesh").objectReferenceValue as Mesh;
                var oldMat = so.FindProperty("beeMaterial").objectReferenceValue as Material;
                Mesh vatMesh = null; Material vatMat = null;
                BeeVat.Reload();
                if (!BeeVat.Apply(ref vatMesh, ref vatMat, "bench")) throw new Exception("BeeVat.Apply refused");
                var camGo = new GameObject("BeeVatBenchCam");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.gray;
                cam.nearClipPlane = 0.02f;
                Vector3 c = new Vector3(0f, 40f, 40f);
                camGo.transform.position = c - Vector3.forward * 0.9f;
                camGo.transform.LookAt(c);
                var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                cam.targetTexture = rt;
                const int n = 300;
                var mats = new Matrix4x4[n];
                var cols = new Vector4[n];
                var rnd = new System.Random(3);
                for (int i = 0; i < n; i++)
                {
                    var p = c + new Vector3((float)rnd.NextDouble() - 0.5f, ((float)rnd.NextDouble() - 0.5f) * 0.6f, ((float)rnd.NextDouble() - 0.5f) * 0.6f);
                    mats[i] = Matrix4x4.TRS(p, Quaternion.Euler(0f, (float)rnd.NextDouble() * 360f, 0f), Vector3.one * 0.035f);
                    cols[i] = new Vector4(1.3f, 1.1f, 0.4f, 1f);
                }
                var mpb = new MaterialPropertyBlock();
                mpb.SetVectorArray(GameMaterials.ColorId, cols);
                var px = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                double Bench(Mesh mesh, Material mat, int frames)
                {
                    var sw = new System.Diagnostics.Stopwatch();
                    for (int f = -20; f < frames; f++)
                    {
                        if (f == 0) sw.Start();
                        Graphics.RenderMeshInstanced(new RenderParams(mat) { camera = cam, matProps = mpb, shadowCastingMode = ShadowCastingMode.Off }, mesh, 0, mats);
                        cam.Render();
                        var prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        px.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                        RenderTexture.active = prev;
                    }
                    sw.Stop();
                    return sw.Elapsed.TotalMilliseconds / frames;
                }
                double empty = Bench(oldMesh, oldMat, 0); // warm-up only
                double a1 = Bench(oldMesh, oldMat, 240), b1 = Bench(vatMesh, vatMat, 240);
                double a2 = Bench(oldMesh, oldMat, 240), b2 = Bench(vatMesh, vatMat, 240);
                int Tris(Mesh m) => (int)(m.GetIndexCount(0) / 3);
                Debug.Log("[VBS][VAT] bench on " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + "), " + n +
                          " bees, 1920x1080 4xMSAA: old " + oldMesh.name + " (" + Tris(oldMesh) + " tris) " + ((a1 + a2) / 2).ToString("0.000") +
                          " ms/frame, VAT " + vatMesh.name + " (" + Tris(vatMesh) + " tris) " + ((b1 + b2) / 2).ToString("0.000") +
                          " ms/frame (runs " + a1.ToString("0.000") + "/" + a2.ToString("0.000") + " vs " + b1.ToString("0.000") + "/" + b2.ToString("0.000") + ")");
                cam.targetTexture = null;
            }
            catch (Exception e)
            {
                Debug.LogError("[VBS][VAT] bench FAILED: " + e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }
    }

    /// <summary>Re-bakes the assets whenever the .vat.bytes file is imported (a new bake dropped in).</summary>
    class BeeVatPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var p in imported)
                if (p == BeeVatImport.Source)
                {
                    EditorApplication.delayCall += () => BeeVatImport.Rebuild();
                    return;
                }
        }
    }
}
