using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>
    /// No table: the hive rig (TableAnchor and everything under it) is scaled up meadow.rigScale times and stands on the
    /// floor in the meadow. The hive box sits on a little wooden hive stand (body ~0.3-1.2 m), the price-tag choices and
    /// START stand on garden stakes at meadow.choiceHeight, the Moon scene keeps its old size and height, and the props
    /// stand in a mown yard round the hive (MeadowField). Relative layout under the TableAnchor is unchanged, so every
    /// lane's offsets still hold.
    ///
    /// The hook for placement code (Nam's room-scan placement, AR/Meta/SpawnObject.cs, or anything else):
    ///   MeadowLayout.SetHivePose(pose)     the TableAnchor goes to pose: position = a point ON THE FLOOR (a floor hit works
    ///                                      as is; a point far off the floor is dropped to it), +Z = away from the player
    ///                                      (the hive faces -Z, toward the player). The meadow, the mown yard, the wall
    ///                                      vines and the bees follow within a frame. Saved for the session.
    ///   MeadowLayout.FreeStandPose(head)   the automatic start pose: the choice row meadow.distance in front of the eyes,
    ///                                      turned toward the most open floor when the room scan is in (the rig needs
    ///                                      ~3.5 x 2 m).
    ///   MeadowLayout.HivePoseSet           event after every SetHivePose.
    /// Moving the TableAnchor directly works too (the meadow re-lays itself when the hive moves); keep its y on the floor.
    /// meadow.rig=false gives back the old table layout untouched.
    /// </summary>
    public static class MeadowLayout
    {
        public static bool Active { get; private set; }
        /// <summary>Old name (ShowCapture): true when there is no table.</summary>
        public static bool FreeStanding => Active;
        public static float Scale { get; private set; } = 1f;
        public static string LastPose { get; private set; } = "scene default";
        public static string RigStats { get; private set; } = "not applied";
        public static event Action<Pose> HivePoseSet;
        public static Transform Anchor { get; private set; }

        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = false;
            Scale = 1f;
            LastPose = "scene default";
            RigStats = "not applied";
            HivePoseSet = null;
            Anchor = null;
        }

        public static float FloorY
        {
            get
            {
                if (MeadowField.Instance != null && MeadowField.Instance.HasFloor) return MeadowField.Instance.FloorY;
                var rig = ArRig.Instance;
                return rig != null ? rig.Room.FloorY : 0f;
            }
        }

        /// <summary>The swarm's orbits widen with the rig, so the bees fly round the big hive, not inside it.</summary>
        public static float SwarmSpread => Active ? Mathf.Lerp(1f, Scale, 0.75f) : 1f;

        /// <summary>The meadow window's centre height (ArRig): its bottom just over the roof line the player sees.</summary>
        public static float WindowCentreY => FloorY + Mathf.Clamp(WindowLowest + WindowWidth * 0.33f, 1.2f, 1.9f);
        /// <summary>The window's lowest bottom edge (m above the floor): from 1.5-1.75 m eyes 0.55 m from the hive front,
        /// the 1.23 m roof hides a wall ~2 m behind the hive only below ~0.8 m.</summary>
        const float WindowLowest = 0.95f;

        /// <summary>ArRig: the window's bottom edge (world y) at this spot on the wall: just above the line from the
        /// player's eyes over the hive roof's back edge (so the roof never overlaps it from the player's spot), and never
        /// below WindowLowest.</summary>
        public static float WindowBottomY(Vector3 windowPos)
        {
            float floor = FloorY, low = floor + WindowLowest;
            if (!Active || Anchor == null) return RoofY + 0.15f;
            var hive = Anchor.Find("Hive");
            Vector3 fwd = Anchor.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) return low;
            fwd.Normalize();
            var cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : Anchor.position - fwd * 0.7f + Vector3.up * 1.65f;
            Vector3 back = (hive != null ? hive.position : Anchor.position) + fwd * (0.11f * Scale); // the roof's back edge
            float dRoof = Vector3.Dot(back - eye, fwd), dWall = Vector3.Dot(windowPos - eye, fwd);
            if (dRoof < 0.1f || dWall <= dRoof) return Mathf.Max(low, RoofY + 0.15f);
            float y = eye.y + (RoofY - eye.y) * (dWall / dRoof);
            return Mathf.Max(low, Mathf.Min(y + 0.12f, RoofY + 0.15f));
        }
        public static float WindowWidth => MeadowTuning.WindowWidth;
        /// <summary>The big hive's roof (world y): the window's bottom stays above it.</summary>
        public static float RoofY => FloorY + MeadowTuning.HiveStand + 0.31f * Scale;

        /// <summary>ArRig: how far to slide the window (width m) along the wall (its right axis). Over the roof line it stays
        /// centred behind the hive, straight ahead; only under a ceiling too low for that it slides clear of the roof, to
        /// the side away from the rocket.</summary>
        public static float WindowSideShift(Vector3 windowRight, float width, bool clearsRoof)
        {
            if (!Active || Anchor == null || clearsRoof) return 0f;
            var rocket = Anchor.Find("HONEYMOON-1");
            var hive = Anchor.Find("Hive");
            float side = -1f;
            if (rocket != null && hive != null) side = Vector3.Dot(rocket.position - hive.position, windowRight) > 0f ? -1f : 1f;
            return side * (0.15f * Scale + width * 0.5f + 0.15f);
        }
        public static float WindowBehindHive => 0.25f * Scale + 0.25f;

        // ------------------------------------------------------------------ the hook
        /// <summary>The hook: put the hive rig (TableAnchor) at this floor pose. See the class summary.</summary>
        public static void SetHivePose(Pose pose)
        {
            MeadowTuning.EnsureLoaded();
            var final = Grounded(pose);
            var placement = ArRig.Instance != null ? ArRig.Instance.Placement : UnityEngine.Object.FindAnyObjectByType<HivePlacement>();
            if (placement != null) placement.PlaceAt(final);
            else if (ArRig.Instance != null && ArRig.Instance.TableAnchor != null) ArRig.Instance.TableAnchor.SetPositionAndRotation(final.position, final.rotation);
            LastPose = Describe(final) + " (SetHivePose)";
            Debug.Log("[VBS][Meadow] hive pose " + LastPose);
            HivePoseSet?.Invoke(final);
        }

        /// <summary>Yaw only, and on the floor (always: the big rig never stands on a table, a bed or a sofa).</summary>
        public static Pose Grounded(Pose pose)
        {
            if (!Active) return pose;
            Vector3 p = pose.position;
            p.y = FloorY;
            Vector3 fwd = pose.rotation * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            return new Pose(p, Quaternion.LookRotation(fwd.normalized, Vector3.up));
        }

        // the hive front sits ~0.05 rig units in front of the rig origin; at table scale the choice rows stood ~0.26 in front of it.
        // Scaled up, that gap would grow with the rig and push the hive out of reach, so it only grows up to 1.5x.
        const float HiveFrontZ = 0.05f;
        static float RowPull(float s) => Mathf.Min(1f, 1.5f / Mathf.Max(1f, s));
        /// <summary>How far (rig units) the choice rows stand in front of the rig origin at rig scale s.</summary>
        public static float RowOffset(float s) => 0.26f * RowPull(s) - HiveFrontZ;

        /// <summary>The automatic pose for a head: on the floor ahead, the choice row meadow.distance from the eyes.</summary>
        public static Pose FreeStandPose(Transform head)
        {
            MeadowTuning.EnsureLoaded();
            Vector3 hp = head != null ? head.position : new Vector3(0f, 1.6f, 0f);
            Vector3 fwd = head != null ? head.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            float s = Active ? Scale : MeadowTuning.RigScale;
            float dist = RowOffset(s) * s + MeadowTuning.Distance;
            var scene = MeadowField.Instance != null ? MeadowField.Instance.CurrentScene() : null;
            if (scene != null)
            {
                // the room scan is in: turn toward the most open floor (no walls / furniture inside the rig)
                Vector3 best = fwd;
                float bestFit = -1f;
                float[] turns = { 0f, 25f, -25f, 50f, -50f, 75f, -75f, 105f, -105f, 140f, -140f, 180f };
                foreach (var a in turns)
                {
                    Vector3 d = Quaternion.Euler(0f, a, 0f) * fwd;
                    float fit = Fit(scene, hp + d * dist, d, s);
                    if (fit > bestFit + 0.02f) { bestFit = fit; best = d; }
                    if (fit >= 0.99f) break;
                }
                LastFit = "open floor " + (bestFit * 100f).ToString("0", C) + "% at " + Vector3.SignedAngle(fwd, best, Vector3.up).ToString("0", C) + " deg from the view";
                fwd = best;
            }
            else LastFit = "no room scan yet: straight ahead";
            Vector3 p = hp + fwd * dist;
            p.y = FloorY;
            return new Pose(p, Quaternion.LookRotation(fwd, Vector3.up));
        }

        public static string LastFit { get; private set; } = "";

        /// <summary>Share of the rig's footprint (scaled local x -0.5..0.5, z -0.25..0.45) on open floor.</summary>
        static float Fit(BloomScene scene, Vector3 anchor, Vector3 fwd, float s)
        {
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            int ok = 0, all = 0;
            for (int ix = 0; ix < 7; ix++)
                for (int iz = 0; iz < 5; iz++)
                {
                    float x = (-0.5f + ix / 6f) * s, z = (-0.25f + 0.7f * iz / 4f) * s;
                    Vector3 p = anchor + right * x + fwd * z;
                    p.y = scene.FloorY + 0.1f;
                    all++;
                    if (scene.InFloor(p) && !scene.InVolume(p, 0.05f)) ok++;
                }
            return ok / (float)all;
        }

        /// <summary>HivePlacement's automatic / key placement.</summary>
        public static void NoteAuto(Pose pose, string why)
        {
            LastPose = Describe(pose) + " (" + why + (LastFit.Length > 0 ? ", " + LastFit : "") + ")";
            Debug.Log("[VBS][Meadow] auto placement " + LastPose);
        }

        public static string Describe(Pose p)
        {
            float yaw = p.rotation.eulerAngles.y;
            return "(" + p.position.x.ToString("0.00", C) + "," + p.position.y.ToString("0.00", C) + "," + p.position.z.ToString("0.00", C) +
                   ") yaw " + yaw.ToString("0", C) + " deg, " + (p.position.y - FloorY).ToString("0.00", C) + " m above the floor, rig x" +
                   Scale.ToString("0.0", C);
        }

        // ------------------------------------------------------------------ the rig
        /// <summary>Once per scene, before any Start: scale the TableAnchor up and stand it on the floor (see the summary).</summary>
        public static void ApplyRig(Transform anchor, bool desktop)
        {
            MeadowTuning.EnsureLoaded();
            if (anchor == null || !MeadowTuning.Rig || anchor.GetComponent<MeadowRigMark>() != null) return;
            anchor.gameObject.AddComponent<MeadowRigMark>();
            Anchor = anchor;
            float s = MeadowTuning.RigScale;
            Scale = s;
            Active = true;
            var moon = anchor.Find("MoonScene");
            Vector3 moonPos = moon != null ? moon.localPosition : Vector3.zero, moonScale = moon != null ? moon.localScale : Vector3.one;
            float oldTop = anchor.position.y;

            anchor.localScale = Vector3.one * s;
            Vector3 ap = anchor.position;
            ap.y = FloorY;
            anchor.position = ap;
            if (desktop)
            {
                // the player's spot in the scene: the choice row meadow.distance in front of the desktop camera
                var cam = Camera.main;
                if (cam != null)
                {
                    var pose = FreeStandPose(cam.transform);
                    anchor.SetPositionAndRotation(pose.position, pose.rotation);
                }
            }

            // the Moon keeps its table-layout size and height (it is a sky scene, not a prop)
            if (moon != null)
            {
                moon.localPosition = (moonPos + Vector3.up * Mathf.Max(0f, oldTop - FloorY)) / s;
                moon.localScale = moonScale / s;
            }

            // the SIGN HERE clipboard is handed to the player: it keeps its hand size
            foreach (var clip in anchor.GetComponentsInChildren<SignHereClipboard>(true)) { clip.transform.localScale /= s; Remeasure(clip.transform); }

            // the hive box on its stand
            var hive = anchor.Find("Hive");
            int standTris = 0;
            if (hive != null)
            {
                var hp = hive.localPosition;
                hive.localPosition = new Vector3(hp.x, MeadowTuning.HiveStand / s, hp.z);
                standTris = HiveStand.Build(hive, MeadowTuning.HiveStand / s);
            }

            // no base plate, no fake table top: the plate's collider still grabs, the fake table is the yard's footprint
            var plate = anchor.Find("BasePlate (grab to place)");
            if (plate != null) foreach (var r in plate.GetComponentsInChildren<Renderer>()) r.enabled = false;
            var ft = GameObject.Find("FakeTable");
            if (ft != null)
            {
                foreach (var r in ft.GetComponentsInChildren<Renderer>()) r.enabled = false;
                var t = ft.transform;
                var parentScale = t.parent != null ? t.parent.lossyScale : Vector3.one;
                t.SetPositionAndRotation(anchor.TransformPoint(new Vector3(0f, 0f, 0.1f)) + Vector3.down * 0.001f, anchor.rotation);
                var world = new Vector3(1.4f * s * 0.75f, 0.002f, 0.8f * s * 0.6f);
                t.localScale = new Vector3(world.x / parentScale.x, world.y / parentScale.y, world.z / parentScale.z);
            }

            // choices and START at hand height, each on a garden stake
            int rows = 0, stakes = 0;
            var stakeMat = GameMaterials.Create("VBS_MeadowStake (runtime)", new Color(0.55f, 0.38f, 0.22f));
            foreach (var t in anchor.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "Buttons" && t.name != "StartRow") continue;
                Vector3 l = anchor.InverseTransformPoint(t.position);
                l.y = MeadowTuning.ChoiceHeight / s;
                if (l.z < HiveFrontZ) l.z = HiveFrontZ + (l.z - HiveFrontZ) * RowPull(s); // keep the hive within arm's reach
                t.position = anchor.TransformPoint(l);
                rows++;
                for (int i = 0; i < t.childCount; i++)
                    if (Stake(anchor, t.GetChild(i), stakeMat)) stakes++;
            }
            string sizes = Grow(anchor, hive);
            RigStats = "rig x" + s.ToString("0.0", C) + " on the floor at " + V(anchor.position) + ", hive stand " +
                       MeadowTuning.HiveStand.ToString("0.00", C) + " m (" + standTris + " tris), hive body " +
                       MeadowTuning.HiveStand.ToString("0.00", C) + "-" + (MeadowTuning.HiveStand + 0.31f * s).ToString("0.00", C) +
                       " m, " + rows + " choice rows at " + MeadowTuning.ChoiceHeight.ToString("0.00", C) + " m on " + stakes + " stakes" +
                       (moon != null ? ", Moon kept at table scale" : "") + ", " + sizes + (desktop ? " (desktop: in front of the camera)" : "");
            LastPose = Describe(new Pose(anchor.position, anchor.rotation)) + (desktop ? " (desktop: in front of the camera)" : " (scene default, before placement)");
            Debug.Log("[VBS][Meadow] " + RigStats);
        }

        /// <summary>The room-filling sizes on top of the rig scale: the rocket meadow.rocketHeight tall, the hand props
        /// (the things the bees deliver: smoker, jar, sprayer, ...) meadow.propScale bigger. Re-measures delivery items.</summary>
        static string Grow(Transform anchor, Transform hive)
        {
            var parts = new List<string>();
            var rocket = anchor.Find("HONEYMOON-1");
            if (rocket != null && MeadowTuning.RocketX > 0f)
            {
                // beside the hive and a step behind it, so from the player's spot it stands whole in view: not cut off at
                // the edge of the view, not behind the hive's roof corner, nose under eye height
                var lp = rocket.localPosition;
                rocket.localPosition = new Vector3(Mathf.Sign(lp.x == 0f ? 1f : lp.x) * MeadowTuning.RocketX / Scale, lp.y, MeadowTuning.RocketZ / Scale);
                parts.Add("rocket at " + MeadowTuning.RocketX.ToString("0.00", C) + " m right, " + MeadowTuning.RocketZ.ToString("0.00", C) + " m back");
            }
            if (rocket != null && MeadowTuning.RocketHeight > 0.1f && WorldBounds(rocket, out var rb) && rb.size.y > 0.05f)
            {
                float f = Mathf.Clamp(MeadowTuning.RocketHeight / rb.size.y, 0.5f, 4f);
                rocket.localScale *= f;
                Remeasure(rocket);
                parts.Add("rocket " + rb.size.y.ToString("0.00", C) + " -> " + (rb.size.y * f).ToString("0.00", C) + " m");
            }
            // the hand props (each child of a station's "Props": smoker, honey jar, sprayer, ...) grow toward
            // meadow.propSize (largest side), by at most meadow.propScale on top of the rig scale
            float k = MeadowTuning.PropScale;
            int grown = 0;
            var names = new List<string>();
            foreach (var props in anchor.GetComponentsInChildren<Transform>(true))
            {
                if (props.name != "Props") continue;
                for (int i = 0; i < props.childCount; i++)
                {
                    var t = props.GetChild(i);
                    if (hive != null && (t == hive || hive.IsChildOf(t))) continue;
                    if (t.localScale.x < 0.01f || !WorldBounds(t, out var b)) continue;
                    float m = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                    float f = Mathf.Clamp(MeadowTuning.PropSize / Mathf.Max(0.01f, m), 1f, k);
                    if (m < 0.04f || f < 1.01f) { names.Add(t.name + " " + m.ToString("0.00", C)); continue; }
                    t.localScale *= f;
                    Remeasure(t);
                    names.Add(t.name + " " + m.ToString("0.00", C) + "->" + (m * f).ToString("0.00", C));
                    grown++;
                }
            }
            parts.Add(grown + " props grown [" + string.Join(", ", names) + "] m");
            return string.Join(", ", parts);
        }

        static void Remeasure(Transform t)
        {
            var di = t.GetComponent<DeliveryItem>();
            if (di != null && di.measured) di.Measure();
        }

        /// <summary>World bounds from the meshes (works while the prop is still hidden, unlike Renderer.bounds).</summary>
        static bool WorldBounds(Transform t, out Bounds b)
        {
            var acc = new Bounds();
            bool any = false;
            foreach (var mf in t.GetComponentsInChildren<MeshFilter>(true))
                if (mf.GetComponent<Renderer>() != null && mf.name != "MeadowStake") AddMesh(mf.sharedMesh, mf.transform.localToWorldMatrix, ref acc, ref any);
            foreach (var sk in t.GetComponentsInChildren<SkinnedMeshRenderer>(true)) AddMesh(sk.sharedMesh, sk.transform.localToWorldMatrix, ref acc, ref any);
            b = acc;
            return any;
        }

        static void AddMesh(Mesh mesh, Matrix4x4 m, ref Bounds acc, ref bool any)
        {
            if (mesh == null) return;
            var mb = mesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var w = m.MultiplyPoint3x4(c);
                if (!any) { acc = new Bounds(w, Vector3.zero); any = true; }
                else acc.Encapsulate(w);
            }
        }

        static bool Stake(Transform anchor, Transform button, Material mat)
        {
            if (button.Find("MeadowStake") != null) return false;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "MeadowStake";
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.layer = button.gameObject.layer;
            var t = go.transform;
            t.SetParent(button, false);
            float len = Mathf.Max(0.2f, MeadowTuning.ChoiceHeight - 0.06f);
            Vector3 foot = anchor.InverseTransformPoint(button.position);
            foot.y = 0f;
            Vector3 mid = anchor.TransformPoint(foot) + Vector3.up * (len * 0.5f) + anchor.forward * 0.015f;
            t.SetPositionAndRotation(mid, anchor.rotation);
            Vector3 ls = button.lossyScale;
            var world = new Vector3(0.02f, len * 0.5f, 0.02f);
            t.localScale = new Vector3(world.x / Mathf.Max(1e-4f, ls.x), world.y / Mathf.Max(1e-4f, ls.y), world.z / Mathf.Max(1e-4f, ls.z));
            return true;
        }

        static string V(Vector3 v) => "(" + v.x.ToString("0.00", C) + "," + v.y.ToString("0.00", C) + "," + v.z.ToString("0.00", C) + ")";
    }

    /// <summary>Marks a TableAnchor the meadow rig has been applied to (once per scene).</summary>
    public sealed class MeadowRigMark : MonoBehaviour { }

    /// <summary>
    /// The hive stand under the big hive box: four square wooden legs, a rail frame and a sloped landing board down
    /// from the entrance to the meadow. One vertex-coloured mesh (light baked in), one draw, a child of the Hive
    /// transform (built in its local units; the hive box's bottom board is at local y 0).
    /// </summary>
    public static class HiveStand
    {
        static readonly Vector3 Light = new Vector3(0.35f, 0.8f, -0.45f).normalized;

        public static int Build(Transform hive, float lift)
        {
            if (lift < 0.005f || hive.Find("HiveStand") != null) return 0;
            var go = new GameObject("HiveStand");
            go.layer = hive.gameObject.layer;
            go.transform.SetParent(hive, false);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = GameMaterials.Create("VBS_HiveStand (runtime)", Color.white, vertexColors: true, doubleSided: true);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var b = new Builder();
            var wood = new Color(0.56f, 0.40f, 0.25f);
            var dark = new Color(0.40f, 0.28f, 0.17f);
            const float hx = 0.118f, hz = 0.108f, leg = 0.013f;
            for (int i = 0; i < 4; i++)
            {
                float x = (i % 2 == 0 ? -1f : 1f) * hx, z = (i < 2 ? -1f : 1f) * hz;
                b.Box(new Vector3(x, -lift * 0.5f, z), new Vector3(leg * 2f, lift, leg * 2f), Quaternion.identity, dark);
            }
            float ry = -Mathf.Min(0.012f, lift * 0.2f);
            b.Box(new Vector3(0f, ry, -hz), new Vector3(hx * 2f + leg * 2f, 0.016f, 0.012f), Quaternion.identity, wood);
            b.Box(new Vector3(0f, ry, hz), new Vector3(hx * 2f + leg * 2f, 0.016f, 0.012f), Quaternion.identity, wood);
            b.Box(new Vector3(-hx, ry, 0f), new Vector3(0.012f, 0.016f, hz * 2f), Quaternion.identity, wood);
            b.Box(new Vector3(hx, ry, 0f), new Vector3(0.012f, 0.016f, hz * 2f), Quaternion.identity, wood);
            // a lower brace
            b.Box(new Vector3(0f, -lift * 0.65f, -hz), new Vector3(hx * 2f, 0.01f, 0.008f), Quaternion.identity, dark);
            b.Box(new Vector3(0f, -lift * 0.65f, hz), new Vector3(hx * 2f, 0.01f, 0.008f), Quaternion.identity, dark);
            // the landing board: from under the entrance down to the meadow, toward the player
            Vector3 top = new Vector3(0f, 0.002f, -0.125f), bottom = new Vector3(0f, -lift + 0.004f, -0.125f - lift * 1.1f);
            Vector3 mid = (top + bottom) * 0.5f;
            float len = Vector3.Distance(top, bottom);
            b.Box(mid, new Vector3(0.11f, 0.006f, len), Quaternion.LookRotation(top - bottom, Vector3.up), wood * 1.08f);
            var mesh = new Mesh { name = "HiveStand" };
            b.Apply(mesh);
            mf.sharedMesh = mesh;
            return b.TriCount;
        }

        sealed class Builder
        {
            readonly List<Vector3> _v = new List<Vector3>(512);
            readonly List<Color> _c = new List<Color>(512);
            readonly List<int> _t = new List<int>(1024);
            public int TriCount => _t.Count / 3;

            public void Box(Vector3 c, Vector3 size, Quaternion rot, Color col)
            {
                Vector3 h = size * 0.5f;
                Face(c, rot, Vector3.up, Vector3.right, Vector3.forward, h, col);
                Face(c, rot, Vector3.down, Vector3.right, Vector3.back, h, col);
                Face(c, rot, Vector3.right, Vector3.forward, Vector3.up, h, col);
                Face(c, rot, Vector3.left, Vector3.back, Vector3.up, h, col);
                Face(c, rot, Vector3.forward, Vector3.left, Vector3.up, h, col);
                Face(c, rot, Vector3.back, Vector3.right, Vector3.up, h, col);
            }

            void Face(Vector3 c, Quaternion rot, Vector3 n, Vector3 u, Vector3 w, Vector3 h, Color col)
            {
                Vector3 nn = Vector3.Scale(n, h), uu = Vector3.Scale(u, h), ww = Vector3.Scale(w, h);
                Vector3 wn = rot * n;
                float lit = 0.62f + 0.38f * Mathf.Max(0f, Vector3.Dot(wn, Light));
                Color k = QualitySettings.activeColorSpace == ColorSpace.Linear ? col.linear : col;
                k = new Color(k.r * lit, k.g * lit, k.b * lit, 1f);
                int s = _v.Count;
                _v.Add(c + rot * (nn - uu - ww));
                _v.Add(c + rot * (nn + uu - ww));
                _v.Add(c + rot * (nn + uu + ww));
                _v.Add(c + rot * (nn - uu + ww));
                for (int i = 0; i < 4; i++) _c.Add(k);
                _t.Add(s); _t.Add(s + 2); _t.Add(s + 1);
                _t.Add(s); _t.Add(s + 3); _t.Add(s + 2);
            }

            public void Apply(Mesh m)
            {
                m.SetVertices(_v);
                m.SetColors(_c);
                m.SetTriangles(_t, 0);
                m.RecalculateNormals();
                m.RecalculateBounds();
            }
        }
    }
}
