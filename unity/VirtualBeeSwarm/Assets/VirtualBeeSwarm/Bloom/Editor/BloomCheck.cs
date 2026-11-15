using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using VirtualBeeSwarm.AR;
using Debug = UnityEngine.Debug;

namespace VirtualBeeSwarm.Bloom.Editor
{
    /// <summary>
    /// The bloom without a player: compiles VirtualBeeSwarm/Bloom for the Quest (Vulkan, every occlusion x instancing x
    /// multiview variant, via ArShaderCheck), checks the Resources material, and lays the bloom out against the desktop
    /// fake room (scene tier) and against the RoomModel alone (ring tier): counts, exclusions, tris, build ms.
    ///   Unity -batchmode -nographics -quit -projectPath P -executeMethod VirtualBeeSwarm.Bloom.Editor.BloomCheck.Run
    /// Logs "[VBS][Bloom] check: PASS/FAIL"; exit code 0 = pass.
    /// </summary>
    public static class BloomCheck
    {
        [MenuItem("Virtual Bee Swarm/Bloom/Check shader + layout")]
        static void Menu() => Check();

        public static void Run()
        {
            bool ok = Check();
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        static bool Check()
        {
            var fails = new List<string>();
            // a fresh import first: without one, a batch run's variant compiler can't resolve Packages/ includes
            var sh = Shader.Find("VirtualBeeSwarm/Bloom");
            string path = sh != null ? AssetDatabase.GetAssetPath(sh) : "";
            if (!string.IsNullOrEmpty(path)) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            if (!AR.Editor.ArShaderCheck.Check("VirtualBeeSwarm/Bloom", out _)) fails.Add("shader variants");
            var mat = Resources.Load<Material>(RoomBloom.MaterialResource);
            if (mat == null) fails.Add("Resources/" + RoomBloom.MaterialResource + " missing");
            else
            {
                if (mat.shader == null || mat.shader.name != "VirtualBeeSwarm/Bloom") fails.Add("material shader is " + (mat.shader != null ? mat.shader.name : "null"));
                if (!mat.enableInstancing) fails.Add("material has GPU instancing off (instancing variants would be stripped)");
            }

            var room = new RoomModel();
            FakeRoomSource.Build(room, null, null);
            var scene = new BloomScene();
            BloomFakeRoom.Fill(room, scene);
            Vector3 hive = room.Table.center + Vector3.up * 0.1f;
            Vector3 player = new Vector3(0f, 1.6f, -0.7f);
            var w = room.WindowWall;
            var window = new BloomRect
            {
                center = new Vector3(w.point.x, 1.3f, w.point.z) + w.normal * 0.04f, right = w.right, up = Vector3.up, normal = w.normal,
                half = new Vector2(0.75f, 0.5f),
            };
            bool artKit = BloomMeshes.ArtKitPresent(out string artWhy);
            Debug.Log("[VBS][Bloom] check: " + artWhy);
            foreach (var tier in artKit ? new[] { "scene", "ring", "art" } : new[] { "scene", "ring" })
            {
                bool art = tier == "art";
                var inp = new BloomSurfaces.Inputs
                {
                    room = room, scene = tier != "ring" ? scene : null, hive = hive, player = player, hasWindow = true, window = window,
                    budget = BloomTuning.ClumpsAtDensity1, spacing = 0.32f, vines = true, seed = 1001,
                };
                var sw = Stopwatch.StartNew();
                var field0 = art ? new FlowerField(mat, true) : null;
                if (field0 != null) inp.budget = Mathf.Min(inp.budget, field0.MaxClumps);
                var s = BloomSurfaces.Build(inp);
                var field = new FlowerField(mat != null ? mat : new Material(Shader.Find("VirtualBeeSwarm/Unlit")), art);
                field.Build(s, 1.2f, 78);
                var vines = Vines.Build(s, 1.2f, 32, out int vineTris, out float vineDone);
                double ms = sw.Elapsed.TotalMilliseconds;
                int inTable = 0, nearFeet = 0, behindWall = 0, far = 0, inPillar = 0, onWindow = 0;
                foreach (var p in s.Floor)
                {
                    if (tier != "ring") foreach (var b in scene.Volumes) if (b.pillar && b.Contains(p, BloomSurfaces.WallGap - 0.01f)) inPillar++;
                    if (room.InsideTable(p, -0.05f)) inTable++;
                    if (BloomSurfaces.FlatDist(p, player) < BloomSurfaces.FeetRadius) nearFeet++;
                    if (Vector3.Dot(p - w.point, w.normal) < 0.1f) behindWall++;
                    if (BloomSurfaces.FlatDist(p, hive) > BloomSurfaces.MaxFromHive) far++;
                }
                if (vines != null)
                    foreach (var vv in vines.vertices) if (window.Contains(vv, 0.05f, 0.3f)) onWindow++;
                int kinds0 = 0, kinds1 = 0, kinds2 = 0;
                foreach (var v in s.Vines) { if (v.kind == VineKind.Wall) kinds0++; else if (v.kind == VineKind.Pillar) kinds1++; else kinds2++; }
                int total = field.Tris + vineTris + BloomVisitors.Max * 1200;
                Debug.Log("[VBS][Bloom] check " + tier + ": floor=" + s.FloorTier + " (" + s.Floor.Count + " clumps x " + field.TrisPerClump +
                          " tris = " + field.Tris + ", " + s.Spacing.ToString("0.00") + " m apart) vines=" + s.VineTiers + " (" + s.Vines.Count + ": " + kinds0 + " wall, " + kinds1 + " pillar, " +
                          kinds2 + " leg; " + vineTris + " tris, " + (vines != null ? vines.vertexCount : 0) + " verts) grown at " +
                          Mathf.Max(field.Done, vineDone).ToString("0.0") + " s, build " + ms.ToString("0.0") + " ms, est. total tris with 12 bees " + total +
                          " | bad: in table " + inTable + ", at feet " + nearFeet + ", behind wall " + behindWall + ", >4 m " + far +
                          ", in pillars " + inPillar + ", vine verts on the window " + onWindow +
                          " | art " + BloomMeshes.ArtUsed);
                if (inTable + nearFeet + behindWall + far + inPillar > 0) fails.Add(tier + ": " + (inTable + nearFeet + behindWall + far + inPillar) + " flowers in keep-outs");
                if (onWindow > 0) fails.Add(tier + ": " + onWindow + " vine vertices on the window");
                if (vineTris > Vines.MaxTris + 3000) fails.Add(tier + ": vines " + vineTris + " tris > cap");
                if (field.Tris > 120000) fails.Add(tier + ": flowers " + field.Tris + " tris > 120k");
                if (tier == "scene" && (s.Floor.Count < 300 || kinds1 != 2 * BloomSurfaces.SpiralsPerPillar || kinds2 != 4 || kinds0 < 12))
                    fails.Add("scene: expected 300+ clumps, " + 2 * BloomSurfaces.SpiralsPerPillar + " pillar spirals, 4 legs, 12+ wall vines");
                if (tier == "ring" && s.Floor.Count < 100) fails.Add("ring: only " + s.Floor.Count + " clumps");
                if (field.Tris + vineTris > 150000) fails.Add(tier + ": " + (field.Tris + vineTris) + " tris > 150k");
                if (vines != null) Object.DestroyImmediate(vines);
            }
            bool ok = fails.Count == 0;
            Debug.Log("[VBS][Bloom] check: " + (ok ? "PASS" : "FAIL: " + string.Join("; ", fails)));
            return ok;
        }
    }
}
