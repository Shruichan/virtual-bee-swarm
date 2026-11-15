using System.Globalization;
using UnityEngine;
using VirtualBeeSwarm.AR;
using VirtualBeeSwarm.Bloom;

namespace VirtualBeeSwarm.RoomAware
{
    /// <summary>
    /// The automatic floor pose for the whole hive rig against the room's keep-outs (MeadowLayout.FreeStandPose's hook).
    /// Each candidate pose (turns within ±room.turnCap of the view, side steps up to 0.8 m, up to 0.45 m further back, which costs: the
    /// choice row moves out toward 0.9 m) is scored on everything the rig puts in the room, measured at runtime in the rig's
    /// own units, as oriented boxes:
    ///   hard  the hive + stand (and the space its lid swings into at the escape), the mown yard with the station props, the rocket at its meadow spot (none of them may stand
    ///         in real furniture, a wall or off the scanned floor), the eye-to-hive sightline (a desk or monitor in front of
    ///         the hive hides it: the depth occlusion makes it "disappear"), and not on the player's own feet
    ///   soft  the ORDER UP / lever row with its stakes and the portal behind the hive (both move themselves clear
    ///         afterwards: ControlKeeper, PortalKeeper), then the turn, the step aside and the step back
    /// Only if nothing inside the cone keeps the hive, rocket and sightline clear does it look further round (10° steps
    /// past the cap, up to room.turnWide) and take a pose that is clearly better. If nothing beats standing straight ahead
    /// on those, it hands over to MeadowLayout's own open-floor search (dartiros). Logged as MeadowLayout.LastFit ("room fit ...").
    /// Re-placing a rig that is still auto-placed (the scan / depth arrived late) happens at most twice per group, and only
    /// for a pose that is clearly better than where it stands.
    /// </summary>
    public static class RigFit
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly float[] Sides = { 0f, 0.2f, -0.2f, 0.4f, -0.4f, 0.6f, -0.6f, 0.8f, -0.8f };
        static readonly float[] Backs = { 0f, 0.15f, 0.3f, 0.45f };

        public struct Score
        {
            public float hive, lid, yard, rocket, row, portal, cost, rocketWeight;
            public bool sight, lowSight, feet;
            public string hiveWhat, yardWhat, rocketWhat, sightWhat;
            /// <summary>What must never happen: the hive or rocket in something real, the hive hidden, the rig on the player.
            /// The rocket weighs less with a furnished scan: MeadowLayout.FitRocketHome moves its pad clear anyway.</summary>
            public float Core => hive * 3f + rocket * rocketWeight + (sight ? 0.3f : 0f) + (lowSight ? 0.12f : 0f) + (feet ? 0.5f : 0f);
            /// <summary>Core plus the yard (its props move themselves out of furniture: RoomKeepOut.PushOut / KeepClear) and the
            /// open lid (a few seconds at the escape: never worth turning the stage out of view for).</summary>
            public float Hard => Core + yard * 0.6f + lid;
        }

        struct Parts
        {
            public Bounds hive, lid, yard, rocket, row, portal;
            public Vector3 hiveAim, hiveLow; // rig-local points the eyes must see (hive body, its lower box)
        }

        public static string LastWhy { get; private set; } = "";
        public static Score LastScore { get; private set; }

        /// <summary>The hook. eye = head position, fwd = flat view direction, s = rig scale, dist = the default distance from
        /// the eyes to the rig origin. False (keep the old placement) when the room is unknown or room.aware is off.</summary>
        public static bool TryChoose(Vector3 eye, Vector3 fwd, float s, float dist, out Pose pose, out string why)
        {
            pose = default;
            why = "";
            RoomKeepOut.ReadSceneNow();
            if (!RoomKeepOut.Ready || !MeadowLayout.Active) return false;
            RoomKeepOut.EnsureSampled();
            var k = RoomKeepOut.Instance;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var parts = Measure(s);
            float floorY = MeadowLayout.FloorY;
            var field = MeadowField.Instance;
            bool scanned = k.Boxes.Count > 0 && field != null && field.CurrentScene() != null;
            float rocketWeight = scanned ? 0.5f : 2f;
            Score straight = default;
            float cap = RoomAwareTuning.TurnCap;
            var cone = new Acc();
            int tried = 0;
            // inside the cone, small turns first
            for (int ti = 0; ti <= 10; ti++)
            {
                float turn = ti == 0 ? 0f : (ti % 2 == 1 ? 1f : -1f) * cap * ((ti + 1) / 2) / 5f;
                Eval(cone, turn);
            }
            var pick = cone;
            bool widened = false;
            float wideCap = Mathf.Max(cap, RoomAwareTuning.TurnWide);
            if (cone.best.Core > 0.02f && wideCap > cap)
            {
                var wide = new Acc();
                for (float a = cap + 10f; ; a += 10f)
                {
                    a = Mathf.Min(a, wideCap);
                    Eval(wide, a);
                    if (a < 180f) Eval(wide, -a);
                    if (a >= wideCap) break;
                }
                // only a clearly better core (hive / rocket / sightline) justifies turning the stage further from the view
                if (wide.best.Core < cone.best.Core - 0.05f && wide.best.Core < cone.best.Core * 0.5f) { pick = wide; widened = true; }
            }
            if (scanned && straight.Core > 0.02f && pick.best.Core >= straight.Core - 0.01f)
            {
                // nothing here does better than straight ahead on the hive / rocket / sightline: dartiros' open-floor search
                why = "room fit found nothing better than straight ahead (" + Describe(straight) + "): open-floor search";
                if (why != LastWhy) Debug.Log("[VBS][Room] " + why);
                LastWhy = why;
                return false;
            }
            pose = pick.pose;
            LastScore = pick.best;
            why = "room fit (" + k.Source + "): turn " + pick.turn.ToString("0", C) + " deg" + (widened ? " (past the " + cap.ToString("0", C) + " deg cone; inside it: " + Describe(cone.best) + ")" : "") +
                  ", side " + pick.side.ToString("0.0", C) + " m, back " + pick.back.ToString("0.00", C) + " m; " + Describe(pick.best) + "; " + tried + " poses";
            LastWhy = why;
            return true;

            void Eval(Acc acc, float turn)
            {
                Vector3 dir = Quaternion.Euler(0f, turn, 0f) * fwd;
                Vector3 right = Vector3.Cross(Vector3.up, dir);
                var rot = Quaternion.LookRotation(dir, Vector3.up);
                foreach (float back in Backs)
                foreach (float side in Sides)
                {
                    Vector3 p = eye + dir * (dist + back) + right * side;
                    p.y = floorY;
                    tried++;
                    var sc = Evaluate(k, parts, p, rot, s, eye, rocketWeight);
                    if (turn == 0f && back == 0f && side == 0f) straight = sc;
                    sc.cost = sc.Hard * 10f + (sc.row * 0.5f + sc.portal * 0.3f) * 3f + Mathf.Abs(turn) / Mathf.Max(1f, cap) * 0.3f + back * 0.4f + Mathf.Abs(side) * 0.1f;
                    if (sc.cost < acc.best.cost - 1e-5f)
                    {
                        acc.best = sc;
                        acc.pose = new Pose(p, rot);
                        acc.turn = turn;
                        acc.side = side;
                        acc.back = back;
                    }
                }
            }
        }

        sealed class Acc
        {
            public Score best = new Score { cost = float.MaxValue };
            public Pose pose;
            public float turn, side, back;
        }

        static Score Evaluate(RoomKeepOut k, in Parts parts, Vector3 pos, Quaternion rot, float s, Vector3 eye, float rocketWeight)
        {
            var m = Matrix4x4.TRS(pos, rot, Vector3.one * s);
            var sc = new Score { rocketWeight = rocketWeight };
            sc.hive = k.Penetration(Obb.From(parts.hive, m), 0.03f, out sc.hiveWhat);
            sc.lid = k.Penetration(Obb.From(parts.lid, m), 0.0f);
            var yard = Obb.From(parts.yard, m);
            sc.yard = k.Penetration(yard, 0.0f, out sc.yardWhat);
            sc.rocket = k.Penetration(Obb.From(parts.rocket, m), 0.03f, out sc.rocketWhat);
            sc.row = k.Penetration(Obb.From(parts.row, m), 0.0f);
            sc.portal = k.Penetration(Obb.From(parts.portal, m), 0.0f);
            sc.sight = k.SightBlocked(eye, m.MultiplyPoint3x4(parts.hiveAim), out sc.sightWhat);
            sc.lowSight = !sc.sight && k.SightBlocked(eye, m.MultiplyPoint3x4(parts.hiveLow), out _);
            var feet = new Obb(new Vector3(yard.c.x, eye.y, yard.c.z), yard.rot, new Vector3(yard.half.x + 0.3f, 1f, yard.half.z + 0.3f));
            sc.feet = feet.Contains(eye);
            return sc;
        }

        /// <summary>The rig's parts in its own (unscaled) units: measured from the live hierarchy where it exists.</summary>
        static Parts Measure(float s)
        {
            var anchor = MeadowLayout.Anchor;
            var p = new Parts();
            float inv = 1f / Mathf.Max(0.01f, s);
            // the hive + stand
            p.hive = new Bounds(new Vector3(0f, (MeadowTuning.HiveStand + 0.15f * s) * inv, 0.08f), new Vector3(0.3f, (MeadowTuning.HiveStand + 0.31f * s) * inv, 0.28f));
            var hive = anchor != null ? Object.FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include) : null;
            if (hive != null && hive.transform.IsChildOf(anchor) && RoomKeepOut.MeshBounds(hive.transform, out var hb))
            {
                var lb = RoomKeepOut.WorldBox(hb, anchor.worldToLocalMatrix);
                if (lb.size.x < 2f && lb.size.y < 2f && lb.size.z < 2f)
                {
                    // the stand reaches the floor
                    var mn = lb.min;
                    mn.y = Mathf.Min(mn.y, 0.02f * inv);
                    lb.SetMinMax(mn, lb.max);
                    p.hive = lb;
                }
            }
            p.hiveAim = p.hive.center + Vector3.up * (p.hive.extents.y * 0.35f);
            p.hiveLow = p.hive.center - Vector3.up * (p.hive.extents.y * 0.2f); // a box on the floor in front hides its lower half
            // the lid swings up and back over the far edge at the escape (HiveBody's lid hinge): keep that space clear too
            float lidD = Mathf.Max(0.12f, p.hive.size.z * 0.8f);
            p.lid = new Bounds(new Vector3(p.hive.center.x, p.hive.max.y + lidD * 0.15f, p.hive.max.z + lidD * 0.45f), new Vector3(p.hive.size.x, lidD * 1.1f, lidD * 0.9f));
            // the mown yard with the station props round the hive (MeadowLayout.Fit's yard)
            p.yard = new Bounds(new Vector3(0f, 0.2f * inv, 0.1f), new Vector3(1f, 0.34f * inv, 0.7f));
            // the rocket at its meadow spot, whole height
            float side = 1f;
            var rocket = anchor != null ? anchor.Find("HONEYMOON-1") : null;
            if (rocket != null && Mathf.Abs(rocket.localPosition.x) > 1e-3f) side = Mathf.Sign(rocket.localPosition.x);
            float rh = Mathf.Max(0.15f, MeadowTuning.RocketHeight), rw = Mathf.Clamp(rh * 0.45f, 0.1f, 0.44f);
            p.rocket = new Bounds(new Vector3(side * MeadowTuning.RocketX * inv, rh * 0.5f * inv, MeadowTuning.RocketZ * inv), new Vector3(rw, rh, rw) * inv);
            // the ORDER UP / lever row with the stakes to the floor
            float rowZ = -MeadowLayout.RowOffset(s);
            float top = MeadowTuning.ChoiceHeight + 0.15f;
            p.row = new Bounds(new Vector3(0f, top * 0.5f * inv, rowZ), new Vector3(1.1f * inv, top * inv, 0.2f * inv));
            // the portal behind the hive
            float behind = 1.45f;
            p.portal = new Bounds(new Vector3(0f, 1.75f * inv, p.hive.center.z + behind * inv), new Vector3(1.6f * inv, 1.0f * inv, 0.16f * inv));
            return p;
        }

        static string Describe(in Score s)
        {
            string P(float d, string what) => d > 0f ? (d * 100f).ToString("0", C) + " cm into " + what : "clear";
            return "hive " + P(s.hive, s.hiveWhat) + (s.lid > 0f ? ", open lid " + (s.lid * 100f).ToString("0", C) + " cm" : "") + ", yard " + P(s.yard, s.yardWhat) + ", rocket " + P(s.rocket, s.rocketWhat) +
                   ", sightline " + (s.sight ? "blocked by " + s.sightWhat : "clear") + (s.feet ? ", ON THE PLAYER" : "") +
                   ", row " + (s.row > 0f ? (s.row * 100f).ToString("0", C) + " cm" : "clear") + ", portal " + (s.portal > 0f ? (s.portal * 100f).ToString("0", C) + " cm" : "clear");
        }

        /// <summary>The rig's current pose against the keep-outs (for the audit and MaybeReplace).</summary>
        public static Score Current()
        {
            var anchor = MeadowLayout.Anchor;
            var cam = Camera.main;
            if (!RoomKeepOut.Ready || anchor == null || cam == null) return default;
            float s = MeadowLayout.Scale;
            var k = RoomKeepOut.Instance;
            var field = MeadowField.Instance;
            bool scanned = k.Boxes.Count > 0 && field != null && field.CurrentScene() != null;
            return Evaluate(k, Measure(s), anchor.position, anchor.rotation, s, cam.transform.position, scanned ? 0.5f : 2f);
        }

        const int MaxReplaces = 2;
        static float _lastReplace = -100f;
        static int _replaces;

        /// <summary>A new group: it may be re-placed again (RoomKeepOut calls it on GameDirector.GroupResetDone).</summary>
        public static void ResetForGroup() { _replaces = 0; _lastReplace = -100f; }

        /// <summary>Headset: the room scan / depth cells came in after the automatic placement. If the rig is still the
        /// auto-placed one that follows the player (nothing placed by hand), it overlaps something real now, and a fresh
        /// fit from where the player looks is clearly better, place it again: at most twice per group, 4 s apart.</summary>
        public static void MaybeReplace(string why)
        {
            if (!RoomKeepOut.Ready || !MeadowLayout.Active || _replaces >= MaxReplaces || Time.time - _lastReplace < 4f) return;
            var rig = ArRig.Instance;
            if (rig == null || rig.DesktopMode || rig.Placement == null || !rig.Placement.AutoFollowing) return;
            var cam = Camera.main;
            if (cam == null) return;
            var now = Current();
            if (now.Hard <= 0.05f) return;
            float s = MeadowLayout.Scale;
            if (!TryChoose(cam.transform.position, cam.transform.forward, s, MeadowLayout.RowOffset(s) * s + MeadowTuning.Distance, out _, out _)) return;
            var next = LastScore;
            if (next.Hard > now.Hard - 0.05f || next.Hard > now.Hard * 0.6f) return; // not clearly better: stay put
            _lastReplace = Time.time;
            _replaces++;
            Debug.Log("[VBS][Room] the auto-placed rig overlaps the room (" + Describe(now) + "); a fresh fit is clearly better (" + Describe(next) +
                      "): placing it again, " + _replaces + "/" + MaxReplaces + " (" + why + ")");
            rig.Placement.ReplaceAuto("room keep-outs: " + why);
        }
    }
}
