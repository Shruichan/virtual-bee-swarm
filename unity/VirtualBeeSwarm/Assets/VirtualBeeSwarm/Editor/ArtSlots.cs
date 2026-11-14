using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace VirtualBeeSwarm.Editor
{
    /// <summary>
    /// S11.2 art slots: the scene builder makes every object out of primitives first (so all gameplay wiring stays in
    /// BuildGameScene), then <see cref="Apply"/> swaps in Art/&lt;Slot&gt;/&lt;Slot&gt;.prefab wherever that prefab exists.
    /// No prefab = the primitive stays, exactly as before. VBS_ART=0 skips the pass (quick A/B, or if art breaks a build).
    ///
    /// How a placeholder is dressed (one row of the slot table below):
    ///  1. The placeholder object stays (its name, components and every reference to it). The art prefab is
    ///     instantiated (unpacked) under it, or under its Host child (Hive: LiftRoot, so lifting/tilting still works),
    ///     turned by Yaw (the art faces +Z; the player looks along +Z at the table, so 180 shows them its front) and
    ///     scaled.
    ///  2. Map "placeholder path>art path": every serialized reference in the scene to the placeholder object (its
    ///     Transform, GameObject or Renderer) now points at the art object, which also takes its active/enabled
    ///     state. That's how Door, DoorPoint, SwarmCenter, HeatGlow, Seat, Perch, Flame, LivePane, FallbackPane,
    ///     Glow, Mouth, WingL/WingR... keep working.
    ///  3. Align "placeholder path>art path": placeholder objects that do something (captions, the countdown text,
    ///     the hexagon panel, the smoke puff) are kept and moved onto the art's anchor, keeping their rotation.
    ///  4. Fields: component settings the art needs (HiveBody body renderers + an untinted body colour, Beeswax's
    ///     dusty renderers and mouth scales, the hornet's renderers).
    ///  5. Sweep: the placeholder's leftover primitives are removed. Anything with a component other than a
    ///     MeshFilter/MeshRenderer/Collider is never touched, colliders are kept (renderer removed), and a primitive
    ///     some component still references keeps its renderer with no mesh (invisible, nothing breaks).
    /// The worker bee is special: every reference to Meshes/VBS_Bee.asset (SwarmSystem.beeMesh, and any other pool
    /// wired to it) is pointed at Art/Bee/Bee_unit.asset, a copy of the art bee scaled to 1 unit long (the pools scale
    /// by beeLength) with linear vertex colours. One mesh, one submesh; each pool keeps its own instanced material.
    ///
    /// Slots without a placeholder in today's scene (Swatter, SprayBottle, Crowbar, Reducer, RainCloud, StampApproved,
    /// StampGrievance, SignHere) dress any placeholder that the v3.1 builder code creates with that name. Builder code
    /// that needs the art object itself can call <see cref="Spawn"/>.
    /// </summary>
    public static class ArtSlots
    {
        sealed class Slot
        {
            public string Name;                     // Art/<Name>/<Name>.prefab
            public string[] Match;                  // path suffixes under the rig; "Wasp*" = name prefix
            public string Host;                     // placeholder child that receives the art (null = the placeholder)
            public float Yaw = 180f;
            public float Scale = 1f;
            public bool FitSelf;                    // single-primitive placeholder: art as big as the primitive
            public string FitWidth;                 // "ph>art": scale so the art renderer is as wide as the placeholder's
            public string AnchorAt;                 // art transform that lands on the host's origin
            public string[] Map = new string[0];
            public string[] Align = new string[0];  // "ph>art", or "ph>art>scale" to scale the kept object with the art
            public string[] Drop = new string[0];   // placeholder children removed even though they have components
            public string[] Hide = new string[0];   // art objects switched off
            public Action<Ctx> Fields;
        }

        /// <summary>What a Fields callback can use.</summary>
        sealed class Ctx
        {
            public Transform Placeholder, Art;
            public Scene Scene;
            public Transform A(string path) => FindPath(Art, path) ?? throw new Exception(Art.name + " has no " + path);
            public Renderer R(string path) => A(path).GetComponent<Renderer>() ?? throw new Exception(path + " has no renderer");
            public Transform P(string path) => FindPath(Placeholder, path);
            public T Find<T>() where T : Component
            {
                foreach (var root in Scene.GetRootGameObjects())
                {
                    var c = root.GetComponentInChildren<T>(true);
                    if (c != null) return c;
                }
                return null;
            }
        }

        static readonly Color White = Color.white;

        // ------------------------------------------------------------------------------------------------ the table
        static readonly Slot[] Slots =
        {
            new Slot
            {
                Name = "Hive", Match = new[] { "TableAnchor/Hive" }, Host = "LiftRoot", Scale = 0.8f,
                Map = new[] { "LiftRoot/Door>Door", "LiftRoot/DoorPoint>DoorPoint", "LiftRoot/SwarmCenter>SwarmCenter", "LiftRoot/HeatGlow>HeatGlow" },
                Align = new[] { "LiftRoot/Hexagons>Hexagons", "LiftRoot/Hexagons/Hex1>HexSlot1>scale", "LiftRoot/Hexagons/Hex2>HexSlot2>scale",
                                "LiftRoot/Hexagons/Hex3>HexSlot3>scale", "LiftRoot/Hexagons/Hex4>HexSlot4>scale" },
                Hide = new[] { "EntranceReducer" }, // the delivered Reducer (V15) is the one the player sees
                Fields = c =>
                {
                    var hive = c.Placeholder.GetComponent<HiveBody>();
                    Set(hive, "bodyRenderers", new Object[] { c.R("Stand"), c.R("BoxLower"), c.R("BoxUpper"), c.R("Lid") });
                    Set(hive, "bodyColor", White);                               // the art carries its own colours
                    Set(hive, "sickColor", new Color(0.62f, 0.7f, 0.55f, 1f));
                    FitBox(c.P("LiftRoot/Body"), c.R("BoxLower"), c.R("BoxUpper")); // the grab/lift collider
                },
            },
            new Slot
            {
                Name = "Beeswax", Match = new[] { "TableAnchor/Beeswax" }, Yaw = 0f, Scale = 0.55f, // he turns himself to the player
                Map = new[] { "Body>Body", "Body/Mouth>Body/Mouth", "Body/WingL>Body/WingL", "Body/WingR>Body/WingR" },
                Align = new[] { "Caption>CaptionAnchor" },
                Fields = c =>
                {
                    var bw = c.Placeholder.GetComponent<BeeswaxController>();
                    Set(bw, "dustyRenderers", new Object[] { c.R("Body/Torso"), c.R("Body/Head") });
                    Set(bw, "mouthClosedScale", new Vector3(1f, 0.25f, 1f));
                    Set(bw, "mouthOpenScale", new Vector3(1f, 1.2f, 1f));
                },
            },
            new Slot
            {
                Name = "Rocket", Match = new[] { "TableAnchor/HONEYMOON-1" }, Scale = 0.7f,
                Map = new[] { "Flame>Flame", "Seat>Seat", "Perch>Perch" },
                Align = new[] { "CountdownDisplay>CountdownDisplay" },
            },
            new Slot
            {
                Name = "Portal", Match = new[] { "FutureWindow/Portal" }, FitWidth = "FallbackPane>FallbackPane", AnchorAt = "FallbackPane",
                Map = new[] { "LivePane (Orbis)>LivePane", "FallbackPane>FallbackPane", "Glow>Glow" },
            },
            new Slot { Name = "Smoker", Match = new[] { "Props/Smoker" }, Scale = 0.6f, Align = new[] { "SmokePuff>SmokePoint" } },
            new Slot { Name = "HoneyJar", Match = new[] { "Props/HoneyJar" }, Scale = 1f, Drop = new[] { "Label" } },
            new Slot { Name = "SyrupBottle", Match = new[] { "Props/SyrupBottle" }, Scale = 0.75f, Drop = new[] { "Label" } },
            new Slot { Name = "DeepBowl", Match = new[] { "Props/DeepBowl" }, Scale = 0.7f, Map = new[] { "Water>Water" } },
            new Slot
            {
                Name = "PebbleDish", Match = new[] { "Props/PebbleDish" }, Scale = 0.7f,
                Map = new[] { "Pebble0/Spot>LandingSpot1", "Pebble1/Spot>LandingSpot2", "Pebble2/Spot>LandingSpot3",
                              "Pebble3/Spot>LandingSpot4", "Pebble4/Spot>LandingSpot5" },
            },
            new Slot { Name = "Wasp", Match = new[] { "Props/Wasp*" }, Yaw = 0f, Scale = 0.75f }, // flown nose-first (+Z)
            new Slot
            {
                Name = "Hornet", Match = new[] { "TableAnchor/Hornet" }, Yaw = 0f, Scale = 0.42f,
                Fields = c =>
                {
                    var beat = c.Find<HornetBeat>();
                    if (beat != null) Set(beat, "hornetRenderers", new Object[] { c.R("Body"), c.R("Mandibles") });
                },
            },
            new Slot { Name = "Moon", Match = new[] { "MoonScene/Moon" }, FitSelf = true },
            new Slot { Name = "Earth", Match = new[] { "MoonScene/Earth" }, FitSelf = true },
            new Slot { Name = "ScoreCard", Match = new[] { "ScoreCard" }, AnchorAt = "TextAnchor" },
            // v3.1 props: dressed when the builder makes a placeholder with one of these names
            new Slot { Name = "SignHere", Match = new[] { "SignHere", "SignHereClipboard", "Clipboard" } },
            new Slot { Name = "Swatter", Match = new[] { "Swatter" } },
            new Slot { Name = "SprayBottle", Match = new[] { "SprayBottle" } },
            new Slot { Name = "Crowbar", Match = new[] { "Crowbar" } },
            new Slot { Name = "Reducer", Match = new[] { "Reducer" } },
            new Slot { Name = "RainCloud", Match = new[] { "RainCloud" } },
            new Slot { Name = "StampApproved", Match = new[] { "StampApproved", "ApprovedStamp" } },
            new Slot { Name = "StampGrievance", Match = new[] { "StampGrievance", "GrievanceStamp" } },
        };

        // ------------------------------------------------------------------------------------------------ paths
        /// <summary>Assets/VirtualBeeSwarm (the folder of VirtualBeeSwarm.asmdef).</summary>
        public static string VbsRoot
        {
            get
            {
                foreach (var guid in AssetDatabase.FindAssets("VirtualBeeSwarm t:AssemblyDefinitionAsset"))
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileName(p) == "VirtualBeeSwarm.asmdef") return Path.GetDirectoryName(p).Replace('\\', '/');
                }
                return "Assets/VirtualBeeSwarm";
            }
        }

        public static string ArtRoot => VbsRoot + "/Art";
        public static string PrefabPath(string slot) => ArtRoot + "/" + slot + "/" + slot + ".prefab";
        public static GameObject Prefab(string slot) => AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(slot));
        public static bool Has(string slot) => Prefab(slot) != null;
        public static bool Enabled => Environment.GetEnvironmentVariable("VBS_ART") != "0";

        /// <summary>
        /// For builder code that makes a NEW object and wants the art itself (not just a dressed placeholder): an
        /// unpacked instance of Art/&lt;slot&gt;/&lt;slot&gt;.prefab under parent, front turned to the player (yaw 180), or null
        /// when there's no prefab (build the primitive then). Mind that Apply also dresses placeholders by name.
        /// </summary>
        public static GameObject Spawn(string slot, Transform parent, Vector3 localPos, float yaw = 180f, float scale = 1f)
        {
            var prefab = Enabled ? Prefab(slot) : null;
            if (prefab == null) return null;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent != null ? parent.gameObject.scene : SceneManager.GetActiveScene());
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = slot;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            go.transform.localScale = Vector3.one * scale;
            return go;
        }

        // ------------------------------------------------------------------------------------------------ the pass
        sealed class Job
        {
            public Slot Slot;
            public Transform Placeholder, Art;
            public readonly HashSet<Transform> Keep = new HashSet<Transform>();
            public readonly HashSet<Transform> Drop = new HashSet<Transform>();
            public readonly List<string> Notes = new List<string>();
            public int Removed, Hidden;
        }

        /// <summary>
        /// Swaps art into the scene under rig (GameRig). Call it once the scene is fully built and wired, before it's
        /// saved. Returns a one-line summary (also logged as [VBS][Art] lines).
        /// </summary>
        public static string Apply(Transform rig)
        {
            if (rig == null) throw new ArgumentNullException(nameof(rig));
            if (!Enabled)
            {
                Debug.Log("[VBS][Art] VBS_ART=0: art slots skipped, primitives everywhere");
                return "off (VBS_ART=0)";
            }
            var scene = rig.gameObject.scene;
            var all = rig.GetComponentsInChildren<Transform>(true);
            var paths = new Dictionary<Transform, string>();
            foreach (var t in all) paths[t] = PathOf(t, rig);

            // 1. which placeholders get which slot (an object inside another matched placeholder is left to that one)
            var claims = new List<(Slot slot, Transform ph)>();
            var waiting = new List<string>();
            var noPrefab = new List<string>();
            foreach (var slot in Slots)
            {
                var hits = all.Where(t => t != rig && Matches(paths[t], slot.Match)).ToList();
                bool prefab = Has(slot.Name);
                if (hits.Count == 0) { if (prefab) waiting.Add(slot.Name); continue; }
                if (!prefab) { noPrefab.Add(slot.Name); continue; }
                foreach (var h in hits) claims.Add((slot, h));
            }
            var claimed = new HashSet<Transform>(claims.Select(c => c.ph));
            claims.RemoveAll(c => Ancestors(c.ph).Any(claimed.Contains));

            // 2. instantiate + map + fields + align
            var map = new Dictionary<Object, Object>();
            var jobs = new List<Job>();
            foreach (var (slot, ph) in claims)
            {
                try { jobs.Add(Dress(slot, ph, scene, map)); }
                catch (Exception e) { Debug.LogError("[VBS][Art] " + slot.Name + " on " + paths[ph] + " FAILED, placeholder kept: " + e); }
            }

            // 3. the swarm bee
            string beeNote = "";
            var unit = BeeUnitMesh();
            var oldBee = AssetDatabase.LoadAssetAtPath<Mesh>(VbsRoot + "/Meshes/VBS_Bee.asset");
            if (unit != null && oldBee != null) map[oldBee] = unit;

            // 4. every reference in the scene to a placeholder object now points at its art object
            int remapped = Remap(scene, map, out int beeRefs);
            if (unit != null) beeNote = "bee mesh " + AssetDatabase.GetAssetPath(unit) + " (" + unit.triangles.Length / 3 + " tris, " + unit.subMeshCount + " submesh) in " + beeRefs + " pool(s)";

            // 5. sweep the leftover primitives
            var refs = CollectRefs(scene);
            foreach (var job in jobs) Sweep(job, refs);

            // 6. report
            foreach (var job in jobs)
            {
                var s = job.Slot;
                Debug.Log("[VBS][Art] " + s.Name + " -> " + paths[job.Placeholder] + (s.Host != null ? "/" + s.Host : "") +
                          " (yaw " + s.Yaw.ToString("0") + ", scale " + job.Art.localScale.x.ToString("0.###") + "), " +
                          job.Removed + " primitive(s) removed" + (job.Hidden > 0 ? ", " + job.Hidden + " kept invisible (still referenced)" : "") +
                          (job.Notes.Count > 0 ? "; " + string.Join("; ", job.Notes) : ""));
            }
            int dangling = CheckDangling(scene);
            string summary = jobs.Count + " placeholder(s) dressed from " + jobs.Select(j => j.Slot.Name).Distinct().Count() + " slot(s), " +
                             remapped + " reference(s) moved" + (beeNote.Length > 0 ? ", " + beeNote : "") +
                             (waiting.Count > 0 ? "; prefab but no placeholder yet: " + string.Join(", ", waiting) : "") +
                             (noPrefab.Count > 0 ? "; no prefab (primitive kept): " + string.Join(", ", noPrefab) : "") +
                             (dangling > 0 ? "; " + dangling + " DANGLING reference(s), see errors above" : "");
            Debug.Log("[VBS][Art] " + summary);
            return summary;
        }

        static Job Dress(Slot slot, Transform ph, Scene scene, Dictionary<Object, Object> map)
        {
            var job = new Job { Slot = slot, Placeholder = ph };
            var host = slot.Host != null ? FindPath(ph, slot.Host) : ph;
            if (host == null) { job.Notes.Add("no host " + slot.Host + ", art put on the placeholder"); host = ph; }

            // a single primitive (the placeholder itself is the mesh): measure it, then take its scale off so the art
            // and the placeholder's children aren't stretched
            Vector3 selfSize = Vector3.zero;
            if (IsVisual(ph))
            {
                selfSize = Vector3.Scale(ph.GetComponent<MeshFilter>().sharedMesh.bounds.size, Abs(ph.lossyScale));
                Unscale(ph);
            }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(Prefab(slot.Name), scene);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            go.name = slot.Name + " (art)";
            var art = go.transform;
            art.SetParent(host, false);
            art.localPosition = Vector3.zero;
            art.localRotation = Quaternion.Euler(0f, slot.Yaw, 0f);
            art.localScale = Vector3.one;
            job.Art = art;
            job.Keep.Add(art);

            float scale = slot.Scale;
            if (slot.FitSelf && selfSize != Vector3.zero)
            {
                var ab = LocalBounds(art, art);
                scale = Mathf.Max(selfSize.x, selfSize.y, selfSize.z) / Mathf.Max(1e-5f, Mathf.Max(ab.size.x, ab.size.y, ab.size.z)) /
                        Mathf.Max(1e-5f, host.lossyScale.x);
            }
            if (slot.FitWidth != null)
            {
                var (p, a) = Pair(slot.FitWidth);
                var pt = FindPath(ph, p);
                var at = FindPath(art, a);
                float pw = Width(pt, host), aw = Width(at, host);
                if (pw > 0f && aw > 0f) scale = pw / aw;
                else job.Notes.Add("FitWidth " + slot.FitWidth + " not measurable, scale " + scale);
            }
            art.localScale = Vector3.one * scale;
            if (slot.AnchorAt != null)
            {
                var a = FindPath(art, slot.AnchorAt);
                if (a != null) art.position += host.position - a.position;
                else job.Notes.Add("no anchor " + slot.AnchorAt);
            }

            foreach (var entry in slot.Map)
            {
                var (p, a) = Pair(entry);
                var pt = FindPath(ph, p);
                var at = FindPath(art, a);
                if (pt == null || at == null) { job.Notes.Add("map " + entry + ": " + (pt == null ? "no placeholder" : "no art") + " object"); continue; }
                map[pt] = at;
                map[pt.gameObject] = at.gameObject;
                var pr = pt.GetComponent<Renderer>();
                var ar = at.GetComponent<Renderer>();
                if (pr != null && ar != null) { map[pr] = ar; ar.enabled = pr.enabled; }
                else if (pr != null) job.Notes.Add("map " + entry + ": art object has no renderer");
                at.gameObject.SetActive(pt.gameObject.activeSelf);
            }
            foreach (var name in slot.Hide)
            {
                var t = FindPath(art, name);
                if (t != null) t.gameObject.SetActive(false);
            }
            slot.Fields?.Invoke(new Ctx { Placeholder = ph, Art = art, Scene = scene });
            // resolve every pair first: moving a parent (Hexagons) must not break the paths of its children (Hex1..4)
            var aligns = new List<(string entry, string[] parts, Transform pt, Transform at)>();
            foreach (var entry in slot.Align)
            {
                var parts = entry.Split('>');
                aligns.Add((entry, parts, FindPath(ph, parts[0]), FindPath(art, parts[1])));
            }
            foreach (var (entry, parts, pt, at) in aligns)
            {
                if (pt == null || at == null) { job.Notes.Add("align " + entry + ": missing object"); continue; }
                var rot = pt.rotation;
                var ls = pt.localScale;
                pt.SetParent(at, true);
                pt.position = at.position;
                pt.rotation = rot;
                if (parts.Length > 2 && parts[2] == "scale") pt.localScale = ls;
                job.Keep.Add(pt);
            }
            foreach (var name in slot.Drop)
            {
                var t = FindPath(ph, name);
                if (t != null) job.Drop.Add(t);
            }
            return job;
        }

        // ------------------------------------------------------------------------------------------------ sweep
        static void Sweep(Job job, HashSet<Object> refs)
        {
            foreach (var c in Children(job.Placeholder)) SweepNode(c, job, refs);
            if (IsVisual(job.Placeholder)) StripVisual(job.Placeholder, job, refs);
        }

        static void SweepNode(Transform t, Job job, HashSet<Object> refs)
        {
            if (t == null || job.Keep.Contains(t)) return;
            if (job.Drop.Contains(t))
            {
                if (Referenced(t, refs)) { t.gameObject.SetActive(false); job.Notes.Add(t.name + " hidden (referenced)"); }
                else Object.DestroyImmediate(t.gameObject);
                return;
            }
            if (IsFunctional(t)) return; // a TextMesh, particle system, button, ... is never ours to remove
            foreach (var c in Children(t)) SweepNode(c, job, refs);
            bool referenced = Referenced(t, refs);
            StripVisual(t, job, refs);
            if (!referenced && t.childCount == 0 && t.GetComponents<Component>().Length == 1) Object.DestroyImmediate(t.gameObject);
        }

        static void StripVisual(Transform t, Job job, HashSet<Object> refs)
        {
            var mr = t.GetComponent<MeshRenderer>();
            var mf = t.GetComponent<MeshFilter>();
            if (mr == null || mf == null) return;
            if (refs.Contains(mr) || refs.Contains(mf))
            {
                mf.sharedMesh = null; // still referenced by some component: keep it, but draw nothing
                job.Hidden++;
                job.Notes.Add(t.name + " kept without a mesh (referenced)");
                return;
            }
            Object.DestroyImmediate(mr);
            Object.DestroyImmediate(mf);
            job.Removed++;
        }

        static bool IsVisual(Transform t) => t.GetComponent<MeshRenderer>() != null && t.GetComponent<MeshFilter>() != null;

        static bool IsFunctional(Transform t)
        {
            foreach (var c in t.GetComponents<Component>())
                if (!(c is Transform || c is MeshFilter || c is MeshRenderer || c is Collider)) return true;
            return false;
        }

        static bool Referenced(Transform t, HashSet<Object> refs)
        {
            if (refs.Contains(t.gameObject)) return true;
            foreach (var c in t.GetComponents<Component>()) if (refs.Contains(c)) return true;
            return false;
        }

        /// <summary>Takes a placeholder primitive's own scale off, keeping its children and colliders where they were.</summary>
        static void Unscale(Transform t)
        {
            var s = t.localScale;
            if ((s - Vector3.one).sqrMagnitude < 1e-10f) return;
            foreach (var c in Children(t))
            {
                c.localPosition = Vector3.Scale(c.localPosition, s);
                c.localScale = Vector3.Scale(c.localScale, s);
            }
            foreach (var col in t.GetComponents<Collider>())
            {
                switch (col)
                {
                    case BoxCollider b: b.center = Vector3.Scale(b.center, s); b.size = Vector3.Scale(b.size, Abs(s)); break;
                    case SphereCollider sp: sp.center = Vector3.Scale(sp.center, s); sp.radius *= Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)); break;
                    case CapsuleCollider cc: cc.center = Vector3.Scale(cc.center, s); cc.radius *= Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z)); cc.height *= Mathf.Abs(s.y); break;
                }
            }
            t.localScale = Vector3.one;
        }

        /// <summary>Moves/sizes a kept collider object (e.g. the hive's grab box) to the art renderers' bounds.</summary>
        static void FitBox(Transform t, params Renderer[] rs)
        {
            if (t == null || t.parent == null || rs.Length == 0) return;
            var b = LocalBounds(t.parent, rs.Select(r => r.transform).ToArray());
            t.localPosition = b.center;
            t.localRotation = Quaternion.identity;
            t.localScale = b.size;
            var box = t.GetComponent<BoxCollider>();
            if (box != null) { box.center = Vector3.zero; box.size = Vector3.one; }
        }

        // ------------------------------------------------------------------------------------------------ references
        static int Remap(Scene scene, Dictionary<Object, Object> map, out int beeRefs)
        {
            beeRefs = 0;
            if (map.Count == 0) return 0;
            int n = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform) continue;
                    var so = new SerializedObject(c);
                    var it = so.GetIterator();
                    bool changed = false;
                    while (it.Next(true))
                    {
                        if (it.propertyType != SerializedPropertyType.ObjectReference || it.propertyPath == "m_GameObject") continue;
                        var v = it.objectReferenceValue;
                        if (v == null || !map.TryGetValue(v, out var to)) continue;
                        it.objectReferenceValue = to;
                        changed = true;
                        n++;
                        if (v is Mesh) beeRefs++;
                    }
                    if (changed) so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            return n;
        }

        static HashSet<Object> CollectRefs(Scene scene)
        {
            var refs = new HashSet<Object>();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform) continue;
                    var it = new SerializedObject(c).GetIterator();
                    while (it.Next(true))
                    {
                        if (it.propertyType != SerializedPropertyType.ObjectReference || it.propertyPath == "m_GameObject") continue;
                        var v = it.objectReferenceValue;
                        if (v != null && v != c) refs.Add(v);
                    }
                }
            }
            return refs;
        }

        /// <summary>Errors for serialized references to objects that no longer exist (should be 0).</summary>
        static int CheckDangling(Scene scene)
        {
            int n = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform) continue;
                    var it = new SerializedObject(c).GetIterator();
                    while (it.Next(true))
                    {
                        if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                        if (it.objectReferenceValue == null && it.objectReferenceInstanceIDValue != 0)
                        {
                            n++;
                            Debug.LogError("[VBS][Art] dangling reference " + c.GetType().Name + "." + it.propertyPath + " on " + c.gameObject.name);
                        }
                    }
                }
            }
            return n;
        }

        // ------------------------------------------------------------------------------------------------ the swarm bee
        /// <summary>
        /// Art/Bee/Bee_unit.asset: the art bee's mesh scaled to 1 unit long on Z (the bee pools scale by beeLength,
        /// ~3.5 cm), pivot kept at the body center, one submesh, vertex colours converted to linear when the project
        /// is in Linear colour space (they're exported as sRGB) so VBS_Bee (vertex colours, instanced) shows the
        /// palette colours, wing-flap UVs kept (uv1 hinge moved/scaled with the mesh, uv2 flags). Rewritten from
        /// Art/Bee/Bee.prefab on every call; null if there's no Bee prefab.
        /// </summary>
        public static Mesh BeeUnitMesh()
        {
            var prefab = Prefab("Bee");
            if (prefab == null) return null;
            var mf = prefab.GetComponentInChildren<MeshFilter>(true);
            if (mf == null || mf.sharedMesh == null) { Debug.LogWarning("[VBS][Art] Art/Bee/Bee.prefab has no mesh"); return null; }
            var src = mf.sharedMesh;
            var m = prefab.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            var v = src.vertices;
            var nrm = src.normals;
            float zmin = float.MaxValue, zmax = float.MinValue;
            for (int i = 0; i < v.Length; i++)
            {
                v[i] = m.MultiplyPoint3x4(v[i]);
                zmin = Mathf.Min(zmin, v[i].z);
                zmax = Mathf.Max(zmax, v[i].z);
            }
            float len = zmax - zmin;
            if (len < 1e-5f) return null;
            for (int i = 0; i < v.Length; i++) v[i] /= len;
            if (nrm != null && nrm.Length == v.Length)
                for (int i = 0; i < nrm.Length; i++) nrm[i] = m.MultiplyVector(nrm[i]).normalized;
            var cols = src.colors;
            bool linear = PlayerSettings.colorSpace == ColorSpace.Linear;
            if (cols != null && cols.Length == v.Length && linear)
                for (int i = 0; i < cols.Length; i++) { var a = cols[i].a; cols[i] = cols[i].linear; cols[i].a = a; }
            var tris = new List<int>();
            for (int s = 0; s < src.subMeshCount; s++) tris.AddRange(src.GetTriangles(s));

            var mesh = new Mesh { name = "Bee_unit" };
            mesh.vertices = v;
            if (nrm != null && nrm.Length == v.Length) mesh.normals = nrm;
            if (cols != null && cols.Length == v.Length) mesh.colors = cols;
            var uv = src.uv;
            if (uv != null && uv.Length == v.Length) mesh.uv = uv;
            // wing-flap tags (art/blender/scripts/wings.py): uv1 = hinge (x, y) in mesh space, moved and scaled like
            // the vertices; uv2 = (wing side +-1, blur side) as is. VirtualBeeSwarm/Unlit flaps the tagged vertices.
            var hinge = src.uv2;
            var flag = src.uv3;
            if (hinge != null && hinge.Length == v.Length && flag != null && flag.Length == v.Length)
            {
                for (int i = 0; i < hinge.Length; i++)
                    if (flag[i].x != 0f) hinge[i] = (Vector2)(m.MultiplyPoint3x4(new Vector3(hinge[i].x, hinge[i].y, 0f)) / len);
                mesh.uv2 = hinge;
                mesh.uv3 = flag;
            }
            mesh.subMeshCount = 1;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

            string path = ArtRoot + "/Bee/Bee_unit.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }
            existing.Clear();
            EditorUtility.CopySerialized(mesh, existing);
            existing.name = "Bee_unit";
            Object.DestroyImmediate(mesh);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        // ------------------------------------------------------------------------------------------------ helpers
        /// <summary>"a/b/c" relative to root (exact path), or a bare name found anywhere below root (breadth first).</summary>
        public static Transform FindPath(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return null;
            if (path.Contains('/'))
            {
                var exact = root.Find(path);
                if (exact != null) return exact;
                // tolerate an extra wrapper node (an FBX with several roots imports under one named after the file):
                // find the first segment anywhere below root, then the rest under it
                int slash = path.IndexOf('/');
                var head = FindPath(root, path.Substring(0, slash));
                return head != null ? head.Find(path.Substring(slash + 1)) : null;
            }
            var q = new Queue<Transform>();
            q.Enqueue(root);
            while (q.Count > 0)
            {
                var t = q.Dequeue();
                if (t != root && t.name == path) return t;
                foreach (Transform c in t) q.Enqueue(c);
            }
            return null;
        }

        static (string, string) Pair(string s)
        {
            int i = s.IndexOf('>');
            return (s.Substring(0, i), s.Substring(i + 1));
        }

        static string PathOf(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var p = t; p != null; p = p.parent) { parts.Add(p.name); if (p == root) break; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        static bool Matches(string path, string[] patterns)
        {
            var segs = path.Split('/');
            foreach (var pat in patterns)
            {
                var ps = pat.Split('/');
                if (ps.Length > segs.Length) continue;
                bool ok = true;
                for (int i = 0; i < ps.Length && ok; i++)
                {
                    string s = segs[segs.Length - ps.Length + i], q = ps[i];
                    ok = q.EndsWith("*") ? s.StartsWith(q.Substring(0, q.Length - 1), StringComparison.Ordinal) : s == q;
                }
                if (ok) return true;
            }
            return false;
        }

        static IEnumerable<Transform> Ancestors(Transform t)
        {
            for (var p = t.parent; p != null; p = p.parent) yield return p;
        }

        static Transform[] Children(Transform t)
        {
            var list = new Transform[t.childCount];
            for (int i = 0; i < list.Length; i++) list[i] = t.GetChild(i);
            return list;
        }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        /// <summary>Bounds of the meshes under (or of) the given transforms, in space's local coordinates.</summary>
        static Bounds LocalBounds(Transform space, params Transform[] roots)
        {
            var b = new Bounds();
            bool any = false;
            foreach (var root in roots)
            {
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null) continue;
                    var m = space.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                    var mb = mf.sharedMesh.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                        var p = m.MultiplyPoint3x4(c);
                        if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                        else b.Encapsulate(p);
                    }
                }
            }
            return b;
        }

        /// <summary>Width (local X extent) of t's mesh, measured in host's space.</summary>
        static float Width(Transform t, Transform host)
        {
            if (t == null) return 0f;
            var mf = t.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return 0f;
            return LocalBounds(host, t).size.x;
        }

        static void Set(Object target, string field, object value)
        {
            if (target == null) return;
            var so = new SerializedObject(target);
            var p = so.FindProperty(field);
            if (p == null) throw new Exception(target.GetType().Name + "." + field + " is not a serialized field");
            switch (value)
            {
                case Object[] arr:
                    p.arraySize = arr.Length;
                    for (int i = 0; i < arr.Length; i++) p.GetArrayElementAtIndex(i).objectReferenceValue = arr[i];
                    break;
                case Object o: p.objectReferenceValue = o; break;
                case Color col: p.colorValue = col; break;
                case Vector3 v3: p.vector3Value = v3; break;
                case float f: p.floatValue = f; break;
                case bool b: p.boolValue = b; break;
                default: throw new Exception("unsupported value type for " + field + ": " + value.GetType());
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
