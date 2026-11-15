using System.Globalization;
using UnityEngine;
using VirtualBeeSwarm.AR;
using VirtualBeeSwarm.Bloom;

namespace VirtualBeeSwarm.RoomAware
{
    /// <summary>
    /// The free-floating meadow portal against the room (ArRig.PlaceMeadowPortal's hook, and again when the keep-outs
    /// change): if its rim box sits in real furniture or a wall (a monitor, a shelf), or furniture stands between the eyes
    /// and its centre, it slides sideways (up to 1.4 m), up (under the ceiling), toward the player (staying behind the
    /// hive) or shrinks to 70%, whichever small change clears it, keeping 8 cm of air to the furniture. Then its swell
    /// (escape grow, T-0 moon) is capped so the grown portal stays clear too. Only in `pre`: it never moves while it swells.
    /// The rim is tested as an oriented box (the portal turned 30-45 deg to the world axes isn't inflated).
    /// Logs "[VBS][Room] portal ...".
    /// </summary>
    public static class PortalKeeper
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly float[] Lat = { 0f, 0.2f, -0.2f, 0.4f, -0.4f, 0.6f, -0.6f, 0.8f, -0.8f, 1.0f, -1.0f, 1.2f, -1.2f, 1.4f, -1.4f };
        static readonly float[] Ups = { 0f, 0.15f, 0.3f };
        static readonly float[] Ins = { 0f, -0.3f, 0.3f, -0.6f };
        static readonly float[] Sizes = { 1f, 0.85f, 0.7f };
        static string _last = "";
        const float Pad = 0.08f; // air between the rim and real furniture (a monitor top, a shelf)

        public static void Refit(string why)
        {
            if (!RoomKeepOut.Ready || !MeadowLayout.Active) return;
            var gd = GameDirector.Instance;
            if (gd != null && gd.Beat != GameBeat.Pre) return;
            var w = Object.FindAnyObjectByType<FutureWindow>();
            if (w != null) Fit(w, why);
        }

        /// <summary>The hook (ArRig, after it placed the portal).</summary>
        public static void Fit(FutureWindow window, string why)
        {
            if (!RoomKeepOut.Ready || window == null || !MeadowLayout.Active) return;
            var k = RoomKeepOut.Instance;
            var wt = window.transform;
            var cam = Camera.main;
            if (cam == null || !Rim(window, out var local)) return;
            Vector3 eye = cam.transform.position;
            float ceil = ArRig.Instance != null ? ArRig.Instance.Room.CeilingOrDefault : MeadowLayout.FloorY + 2.4f;
            var hive = Object.FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            Bounds hiveB = default, rocketB = default;
            bool haveHive = hive != null && RoomKeepOut.MeshBounds(hive.transform, out hiveB, true);
            var rocket = MeadowLayout.Anchor != null ? MeadowLayout.Anchor.Find("HONEYMOON-1") : null;
            bool haveRocket = rocket != null && rocket.gameObject.activeInHierarchy && RoomKeepOut.MeshBounds(rocket, out rocketB, true);
            Vector3 pos0 = wt.position;
            Vector3 scale0 = wt.localScale;
            Vector3 right = wt.right; right.y = 0f;
            Vector3 fwd = wt.forward; fwd.y = 0f;
            if (right.sqrMagnitude < 1e-6f || fwd.sqrMagnitude < 1e-6f) return;
            right.Normalize();
            fwd.Normalize();
            float minBehind = haveHive ? Vector3.Dot(hiveB.center - eye, fwd) + 0.45f : 0f;

            var cur = World(wt.localToWorldMatrix, local);
            float curPen = k.Penetration(cur, Pad, out string curWhat);
            bool curSight = k.SightBlocked(eye, cur.c, out string sightWhat);
            if (curPen <= 0f && !curSight && !Hits(cur))
            {
                CapSwell(window, wt, local, k);
                Note("clear (" + why + ")");
                return;
            }
            float bestCost = float.MaxValue;
            Vector3 bestPos = pos0;
            float bestSize = 1f;
            foreach (float size in Sizes)
            foreach (float up in Ups)
            foreach (float inward in Ins)
            foreach (float lat in Lat)
            {
                Vector3 p = pos0 + right * lat + Vector3.up * up + fwd * inward;
                // a smaller portal keeps its visual centre
                Matrix4x4 m = Matrix4x4.TRS(p, wt.rotation, scale0 * size);
                var b = World(m, local);
                if (b.c.y + b.half.y > ceil - 0.03f) continue;
                if (haveHive && Vector3.Dot(b.c - eye, fwd) < minBehind) continue;
                if (k.Penetration(b, Pad) > 0f || Hits(b) || k.SightBlocked(eye, b.c, out _)) continue;
                float cost = Mathf.Abs(lat) + up * 1.5f + Mathf.Abs(inward) * 1.2f + (1f - size) * 3f;
                if (cost < bestCost) { bestCost = cost; bestPos = p; bestSize = size; }
            }
            if (bestCost == float.MaxValue)
            {
                Note("in " + (curPen > 0f ? curWhat + " by " + (curPen * 100f).ToString("0", C) + " cm" : "view of " + sightWhat) + " and no free spot within 1.4 m: left in place (" + why + ")");
                return;
            }
            Vector3 face = bestPos - eye; face.y = 0f;
            if (face.sqrMagnitude < 1e-4f) face = fwd;
            wt.SetPositionAndRotation(bestPos, Quaternion.LookRotation(face.normalized, Vector3.up));
            wt.localScale = scale0 * bestSize;
            CapSwell(window, wt, local, k);
            Vector3 d = bestPos - pos0;
            Note("was " + (curPen > 0f ? (curPen * 100f).ToString("0", C) + " cm into " + curWhat : curSight ? "hidden behind " + sightWhat : "touching the hive/rocket") +
                 ": moved " + Vector3.Dot(d, right).ToString("+0.00;-0.00", C) + " m side, " + d.y.ToString("+0.00;-0.00", C) + " m up, " +
                 Vector3.Dot(d, fwd).ToString("+0.00;-0.00", C) + " m back, size x" + bestSize.ToString("0.00", C) + " (" + why + ")");

            bool Hits(Obb o)
            {
                var bx = o.Aabb;
                bx.Expand(0.05f);
                return (haveHive && bx.Intersects(hiveB)) || (haveRocket && bx.Intersects(rocketB));
            }
        }

        /// <summary>Lowers FutureWindow.MaxSize until the swollen portal stays clear of the keep-outs.</summary>
        static void CapSwell(FutureWindow window, Transform wt, Bounds local, RoomKeepOut k)
        {
            float max = Mathf.Min(window.MaxSize, 3f);
            if (max <= 1f) return;
            var b = World(wt.localToWorldMatrix, local);
            for (float g = max; g > 1f; g -= 0.1f)
            {
                var grown = new Obb(b.c, b.rot, new Vector3(b.half.x * g, b.half.y * g, b.half.z));
                if (k.Penetration(grown, 0.03f) <= 0f)
                {
                    if (g < window.MaxSize - 0.05f) window.MaxSize = g;
                    return;
                }
            }
            window.MaxSize = 1f;
        }

        /// <summary>The portal's rim box in window-local units (PortalFx outer size), else the pane's mesh.</summary>
        static bool Rim(FutureWindow window, out Bounds local)
        {
            var fx = window.Fx;
            if (fx != null && fx.OuterSize.x > 1e-4f)
            {
                local = new Bounds(fx.OuterCenter, new Vector3(fx.OuterSize.x, fx.OuterSize.y, 0.2f / Mathf.Max(1e-3f, Mathf.Abs(window.transform.lossyScale.z))));
                return true;
            }
            local = default;
            var pane = window.PaneTransform;
            var mf = pane != null ? pane.GetComponent<MeshFilter>() : null;
            if (mf == null || mf.sharedMesh == null) return false;
            local = RoomKeepOut.WorldBox(mf.sharedMesh.bounds, window.transform.worldToLocalMatrix * pane.localToWorldMatrix);
            return true;
        }

        static Obb World(Matrix4x4 m, Bounds local) => Obb.From(local, m);

        static void Note(string what)
        {
            if (what == _last) return;
            _last = what;
            Debug.Log("[VBS][Room] portal " + what);
        }
    }
}
