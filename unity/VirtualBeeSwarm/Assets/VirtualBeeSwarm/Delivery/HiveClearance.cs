using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Keeps delivered props out of the hive model. BeeDelivery runs every set-down / park / home pose through Push:
    /// the prop's measured box (DeliveryItem.localBounds, at the pose and its current scale) is tested against each
    /// part of the hive (body, roof/lid, stand: one box per mesh, in the hive's own axes), and if they overlap the
    /// pose moves the shortest way out: up onto the roof, toward the player, or to either side (never down, never
    /// behind). Everything is measured at runtime, so it holds at any meadow.rigScale and prop size. The hive itself
    /// and anything inside it (or carrying it) are left alone.
    /// </summary>
    public static class HiveClearance
    {
        static readonly string[] SkipParts = { "Glow", "Bee", "Shadow", "Text", "Caption", "Halo", "Puff", "Smoke", "Flame", "[VBS]", "Spot", "Door" };
        static HiveBody _hive;
        static readonly HashSet<string> Logged = new HashSet<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _hive = null; Logged.Clear(); }

        /// <summary>Moves pos (the item's world position at worldRot) out of the hive if its box would overlap it.
        /// True if it moved.</summary>
        public static bool Push(Transform item, ref Vector3 pos, Quaternion rot, DeliveryItem di)
        {
            if (item == null || di == null) return false;
            if (_hive == null) _hive = Object.FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            if (_hive == null) return false;
            var hive = _hive.transform;
            if (item == hive || item.IsChildOf(hive) || hive.IsChildOf(item)) return false;
            if (item.name == "ScoreCard") return false; // the end card floats over the hive on purpose (EscapeSequence places it)
            Parts.Clear();
            if (!HiveParts(hive, Parts)) return false;
            if (!di.measured) di.Measure();

            // the item's box at the target pose, in hive space
            Vector3 s = item.lossyScale;
            var lb = di.localBounds;
            float hs = Mathf.Max(1e-4f, Mathf.Abs(hive.lossyScale.y));
            float m = 0.025f / hs; // 2.5 cm margin, in hive space
            Vector3 total = Vector3.zero;
            string how = "";
            for (int pass = 0; pass < 4; pass++)
            {
                Matrix4x4 toHive = hive.worldToLocalMatrix * Matrix4x4.TRS(pos + hive.TransformVector(total), rot, s);
                Bounds ib = default;
                for (int k = 0; k < 8; k++)
                {
                    var c = lb.center + Vector3.Scale(lb.extents, new Vector3((k & 1) == 0 ? -1f : 1f, (k & 2) == 0 ? -1f : 1f, (k & 4) == 0 ? -1f : 1f));
                    var p = toHive.MultiplyPoint3x4(c);
                    if (k == 0) ib = new Bounds(p, Vector3.zero); else ib.Encapsulate(p);
                }
                bool hit = false;
                foreach (var part in Parts)
                {
                    var box = part;
                    box.Expand(2f * m);
                    if (!box.Intersects(ib)) continue;
                    // the shortest way out of this part (hive space: +y up, -z toward the player)
                    float up = box.max.y - ib.min.y, front = ib.max.z - box.min.z, right = box.max.x - ib.min.x, left = ib.max.x - box.min.x;
                    if (up <= 0f || front <= 0f || right <= 0f || left <= 0f) continue;
                    if (up <= front && up <= right && up <= left) { total.y += up; how = "up onto the roof"; }
                    else if (front <= right && front <= left) { total.z -= front; how = "toward the player"; }
                    else if (right <= left) { total.x += right; how = "to the side"; }
                    else { total.x -= left; how = "to the side"; }
                    hit = true;
                    break;
                }
                if (!hit) break;
            }
            if (total == Vector3.zero) return false;
            Vector3 wd = hive.TransformVector(total);
            pos += wd;
            if (Logged.Add(item.name + how))
                Debug.Log("[VBS][Delivery] " + item.name + " would sit inside the hive: moved " + (wd.magnitude * 100f).ToString("0") + " cm " + how);
            return true;
        }

        static readonly List<Bounds> Parts = new List<Bounds>(16);

        /// <summary>The hive's parts as boxes in its own space (body, roof, lid, stand), from their meshes (works while parts are hidden; skips glows, bees, text).</summary>
        internal static bool HiveParts(Transform hive, List<Bounds> parts) // [Throwables hook] internal: the throw bounces off these boxes
        {
            var toLocal = hive.worldToLocalMatrix;
            float hs = Mathf.Max(1e-4f, Mathf.Abs(hive.lossyScale.y));
            foreach (var mf in hive.GetComponentsInChildren<MeshFilter>(false))
            {
                if (mf.sharedMesh == null) continue;
                var r = mf.GetComponent<MeshRenderer>();
                if (r == null || !r.enabled) continue;
                if (Skipped(mf.transform, hive)) continue;
                var mb = mf.sharedMesh.bounds;
                var mm = toLocal * mf.transform.localToWorldMatrix;
                Bounds pb = default;
                for (int k = 0; k < 8; k++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((k & 1) == 0 ? -1f : 1f, (k & 2) == 0 ? -1f : 1f, (k & 4) == 0 ? -1f : 1f));
                    var p = mm.MultiplyPoint3x4(c);
                    if (k == 0) pb = new Bounds(p, Vector3.zero); else pb.Encapsulate(p);
                }
                if (pb.size.x * hs > 3f || pb.size.y * hs > 3f || pb.size.z * hs > 3f) continue; // a meadow disc, a big effect
                parts.Add(pb);
            }
            return parts.Count > 0;
        }

        static bool Skipped(Transform t, Transform stop)
        {
            for (var p = t; p != null && p != stop; p = p.parent)
                foreach (var s in SkipParts)
                    if (p.name.IndexOf(s, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
