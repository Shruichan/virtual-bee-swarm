using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VirtualBeeSwarm.Editor
{
    /// <summary>
    /// Stills of the rigged Beeswax mid-clip, rendered with the built scene's DesktopCamera (moved in close, same camera
    /// settings) through his real AnimatorController (base Hover, then each layer's clip on its mask bones only = an override layer at
    /// weight 1; skinned meshes baked for the render), so a still shows what the layers make of a clip. Needs graphics (no -nographics; xvfb-run on Linux):
    ///   xvfb-run -a Unity -batchmode -quit -projectPath P -executeMethod VirtualBeeSwarm.Editor.BeeswaxRigStills.RenderBatch
    /// VBS_STILLS_DIR = output folder (default Logs/beeswax-stills). The scene file is not saved.
    /// </summary>
    public static class BeeswaxRigStills
    {
        // (file, layer, state, normalized time of the layer clip; -1 = where it moves most)
        static readonly (string file, BeeswaxRig.Layer layer, string state, float t)[] Shots =
        {
            ("beeswax-1-hover", BeeswaxRig.Layer.Base, "Hover", 0.3f),
            ("beeswax-2-talk", BeeswaxRig.Layer.Talk, "Talk", 0.2f),
            ("beeswax-3-yes-nod", BeeswaxRig.Layer.Gesture, "Yes Nod", -1f),
            ("beeswax-4-no-shake", BeeswaxRig.Layer.Gesture, "No Shake", -1f),
            ("beeswax-5-startle", BeeswaxRig.Layer.Gesture, "Startle", -1f),
            ("beeswax-6-celebrate", BeeswaxRig.Layer.Gesture, "Celebrate", -1f),
            ("beeswax-7-angry-talk", BeeswaxRig.Layer.Face, "Angry", 0.5f),
            ("beeswax-8-sooty-shake-dusty", BeeswaxRig.Layer.Gesture, "Sooty Shake", -1f),
        };

        public static void RenderBatch()
        {
            try
            {
                Render();
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[VBS][BeeswaxRig] stills FAILED: " + e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        [MenuItem("Virtual Bee Swarm/Art/Render Beeswax stills")]
        public static void Render()
        {
            string dir = Environment.GetEnvironmentVariable("VBS_STILLS_DIR");
            if (string.IsNullOrEmpty(dir)) dir = "Logs/beeswax-stills";
            Directory.CreateDirectory(dir);
            var scene = EditorSceneManager.OpenScene(BuildGameScene.MainScenePath, OpenSceneMode.Single);
            Camera cam = null;
            BeeswaxRig rig = null;
            BeeswaxController bw = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "DesktopCamera") cam = t.GetComponent<Camera>();
                    if (t.name == "PreUI" || t.name == "WhatIfUI" || t.name == "Buttons" || t.name == "PlacementMarkers" || t.name == "MoonScene" || t.name == "LevelMap" || t.name == "ScoreCard")
                        t.gameObject.SetActive(false);
                }
                if (rig == null) rig = root.GetComponentInChildren<BeeswaxRig>(true);
                if (bw == null) bw = root.GetComponentInChildren<BeeswaxController>(true);
            }
            if (cam == null) throw new Exception("no DesktopCamera in " + BuildGameScene.MainScenePath);
            if (rig == null) throw new Exception("no BeeswaxRig in the scene: build the rig, then the scene");
            var animator = rig.GetComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; // nothing has "seen" him in edit mode: culling would skip the bones
            var ctrl = animator.runtimeAnimatorController ?? throw new Exception("Beeswax has no controller");
            var caption = bw != null ? bw.transform.Find("Caption") : null;
            if (caption != null) caption.gameObject.SetActive(false);

            // camera: the desktop camera, moved to 42 cm in front of Beeswax, a little above, looking at him
            cam.gameObject.SetActive(true);
            cam.enabled = true;
            Transform bee = bw != null ? bw.transform : rig.transform;
            var renderers = rig.GetComponentsInChildren<Renderer>(true);
            Vector3 toCam = cam.transform.position - bee.position;
            toCam.y = 0f;
            if (toCam.sqrMagnitude < 1e-4f) toCam = Vector3.back;
            toCam.Normalize();
            bee.rotation = Quaternion.LookRotation(toCam, Vector3.up); // he faces the player (BeeswaxController does it at runtime)
            var bodyBone = rig.GetComponentsInChildren<Transform>(true).First(x => x.name == "Body");
            var center = bodyBone.position;
            var side = Vector3.Cross(Vector3.up, toCam);
            cam.transform.position = center + toCam * 0.40f + Vector3.up * 0.06f + side * 0.05f;
            cam.transform.rotation = Quaternion.LookRotation(center - cam.transform.position, Vector3.up);
            cam.fieldOfView = 34f;
            const int w = 1280, h = 960;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8 };
            cam.targetTexture = rt;
            cam.aspect = (float)w / h;

            // his layers by hand: sample the base clip on every bone, then each layer's clip and put back every bone outside
            // its mask (= an override layer at weight 1). AnimationClip.SampleAnimation works in edit mode, no Animator update.
            var clips = new Dictionary<string, AnimationClip>();
            foreach (var c in ctrl.animationClips) clips[c.name] = c;
            var bones = new Dictionary<string, Transform>();
            var armature = bodyBone.parent.parent; // Body <- Root <- the armature node
            foreach (var t in armature.GetComponentsInChildren<Transform>(true)) if (!bones.ContainsKey(t.name)) bones[t.name] = t;
            var written = new List<string>();
            try
            {
                foreach (var (file, layer, state, t0) in Shots)
                {
                    bool dusty = state == "Sooty Shake";
                    if (dusty) GameMaterials.TintAll(renderers, new Color(0.62f, 0.6f, 0.58f, 1f));
                    clips["Hover"].SampleAnimation(animator.gameObject, 0.3f * clips["Hover"].length);
                    float t = t0 >= 0f ? t0 : Peak(animator.gameObject, clips[state], layer == BeeswaxRig.Layer.Base ? null : BeeswaxRig.Masks[layer], bones);
                    clips["Hover"].SampleAnimation(animator.gameObject, (layer == BeeswaxRig.Layer.Base ? t : 0.3f) * clips["Hover"].length);
                    if (layer == BeeswaxRig.Layer.Face) Layered(animator.gameObject, clips["Talk"], 0.2f, BeeswaxRig.Masks[BeeswaxRig.Layer.Talk], bones);
                    if (layer != BeeswaxRig.Layer.Base) Layered(animator.gameObject, clips[state], t, BeeswaxRig.Masks[layer], bones);
                    // edit mode doesn't re-skin between renders: bake each skinned mesh (CPU) and draw the bake instead
                    var baked = new List<GameObject>();
                    foreach (var r in renderers)
                    {
                        if (!(r is SkinnedMeshRenderer smr) || smr.sharedMesh == null) continue;
                        var m = new Mesh();
                        smr.BakeMesh(m, false);
                        var go = new GameObject("baked " + smr.name);
                        go.transform.SetParent(smr.transform, false);
                        go.AddComponent<MeshFilter>().sharedMesh = m;
                        var mr = go.AddComponent<MeshRenderer>();
                        mr.sharedMaterials = smr.sharedMaterials;
                        var mpb = new MaterialPropertyBlock();
                        smr.GetPropertyBlock(mpb);
                        mr.SetPropertyBlock(mpb);
                        smr.enabled = false;
                        baked.Add(go);
                    }
                    cam.Render();
                    foreach (var go in baked)
                    {
                        go.transform.parent.GetComponent<SkinnedMeshRenderer>().enabled = true;
                        UnityEngine.Object.DestroyImmediate(go.GetComponent<MeshFilter>().sharedMesh);
                        UnityEngine.Object.DestroyImmediate(go);
                    }
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    tex.Apply();
                    RenderTexture.active = prev;
                    string path = Path.Combine(dir, file + ".png");
                    File.WriteAllBytes(path, tex.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(tex);
                    if (dusty) GameMaterials.ClearAll(renderers);
                    written.Add(path);
                    Debug.Log("[VBS][BeeswaxRig] still " + path + ": " + state + " (" + layer + " layer) at " + t.ToString("0.00", CultureInfo.InvariantCulture) +
                              (dusty ? ", dusty tint" : "") + "; Head " + bones["Head"].localEulerAngles.ToString("F0") + ", Mouth O scale " +
                              bones["Mouth O"].localScale.x.ToString("0.00", CultureInfo.InvariantCulture) + ", Blush.L scale " +
                              bones["Blush.L"].localScale.x.ToString("0.00", CultureInfo.InvariantCulture));
                }
            }
            finally
            {
                cam.targetTexture = null;
                rt.Release();
            }
            Debug.Log("[VBS][BeeswaxRig] " + written.Count + " stills in " + Path.GetFullPath(dir));
        }

        /// <summary>The normalized time where the clip's mask bones are furthest from the Hover pose (its "mid" moment).</summary>
        static float Peak(GameObject go, AnimationClip clip, string[] mask, Dictionary<string, Transform> bones)
        {
            if (mask == null) return 0.3f;
            var rest = mask.Where(bones.ContainsKey).Select(n => (bones[n], bones[n].localRotation, bones[n].localScale, bones[n].localPosition)).ToList();
            float best = 0.3f, bestD = -1f;
            for (float t = 0.1f; t <= 0.9f; t += 0.05f)
            {
                clip.SampleAnimation(go, t * clip.length);
                float d = 0f;
                foreach (var (tr, r, sc, pos) in rest)
                    d += Quaternion.Angle(tr.localRotation, r) + 40f * (tr.localScale - sc).magnitude + 400f * (tr.localPosition - pos).magnitude;
                if (d > bestD) { bestD = d; best = t; }
            }
            return best;
        }

        /// <summary>Sample clip at t (normalized) on go, keeping every bone outside mask as it was.</summary>
        static void Layered(GameObject go, AnimationClip clip, float t, string[] mask, Dictionary<string, Transform> bones)
        {
            var keep = new List<(Transform tr, Vector3 p, Quaternion r, Vector3 s)>();
            var inMask = new HashSet<string>(mask);
            foreach (var kv in bones)
                if (!inMask.Contains(kv.Key)) keep.Add((kv.Value, kv.Value.localPosition, kv.Value.localRotation, kv.Value.localScale));
            clip.SampleAnimation(go, t * clip.length);
            foreach (var (tr, p, r, s) in keep) { tr.localPosition = p; tr.localRotation = r; tr.localScale = s; }
        }

        static Vector3 Center(Renderer[] rs)
        {
            var b = new Bounds();
            bool any = false;
            foreach (var r in rs)
            {
                if (r.bounds.size.sqrMagnitude < 1e-10f) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return b.center;
        }
    }
}
