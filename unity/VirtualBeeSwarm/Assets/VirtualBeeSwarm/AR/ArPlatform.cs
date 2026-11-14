using UnityEngine;

namespace VirtualBeeSwarm.AR
{
    public enum ArHitStatus { Hit, NoHit, NotReady, NotSupported, Occluded, OutsideView }

    public struct ArHit
    {
        public ArHitStatus status;
        public Vector3 point;
        public Vector3 normal;
        public float normalConfidence;
    }

    /// <summary>Environment raycasts (Meta: EnvironmentRaycastManager; desktop: FakeEnvRaycaster against the RoomModel).</summary>
    public interface IEnvRaycaster
    {
        string Name { get; }
        bool IsSupported { get; }
        /// <summary>Switches the raycaster on/off (Meta: only in `pre`, it costs GPU).</summary>
        bool Enabled { get; set; }
        bool Raycast(Ray ray, float maxDistance, out ArHit hit);
        /// <summary>Meta's PlaceBox: box.x/y = footprint, z = clearance along the normal; y aligned with `upwards`.</summary>
        bool PlaceBox(Ray ray, Vector3 boxSize, Vector3 upwards, out ArHit hit);
    }

    /// <summary>Depth API occlusion (Meta: EnvironmentDepthManager). Null on platforms without it.</summary>
    public interface IArDepth
    {
        bool IsSupported { get; }
        bool IsDepthAvailable { get; }
        /// <summary>ms from enabling to the first depth frame, or -1.</summary>
        float FirstDepthMs { get; }
        void SetMode(ArTuning.Occlusion mode);
    }

    /// <summary>Everything the Meta glue (AR/Meta, asmdef VirtualBeeSwarm.AR) supplies. The core never names a Meta type.</summary>
    public interface IArProvider
    {
        string HeadsetName { get; }
        /// <summary>true / false, or null when the platform has no such permission.</summary>
        bool? ScenePermissionGranted { get; }
        IEnvRaycaster CreateRaycaster(GameObject host);
        IArDepth CreateDepth(GameObject host);
    }

    /// <summary>Registry: VirtualBeeSwarm.AR (Meta) sets Provider before the first scene loads, only on platforms where
    /// the Meta natives exist (Android player, Windows/macOS editor or player). Linux leaves it null: fakes are used.</summary>
    public static class ArPlatform
    {
        public static IArProvider Provider { get; set; }
    }

    /// <summary>Analytic raycasts against the RoomModel (table top, window wall, floor, ceiling): desktop and Linux.</summary>
    public sealed class FakeEnvRaycaster : IEnvRaycaster
    {
        readonly RoomModel _room;
        public FakeEnvRaycaster(RoomModel room) { _room = room; }

        public string Name => "fake (RoomModel planes)";
        public bool IsSupported => true;
        public bool Enabled { get; set; }

        public bool Raycast(Ray ray, float maxDistance, out ArHit hit)
        {
            hit = new ArHit { status = ArHitStatus.NoHit };
            if (!Enabled) { hit.status = ArHitStatus.NotReady; return false; }
            float best = maxDistance;
            bool any = false;
            // table top (from above only)
            if (_room.HasTable && ray.direction.y < -1e-4f)
            {
                float t = (_room.Table.Y - ray.origin.y) / ray.direction.y;
                if (t > 0f && t < best && _room.InsideTable(ray.GetPoint(t)))
                {
                    best = t; any = true;
                    hit.point = ray.GetPoint(t); hit.normal = Vector3.up;
                }
            }
            // window wall (front face only)
            if (_room.HasWall)
            {
                var w = _room.WindowWall;
                float denom = Vector3.Dot(ray.direction, w.normal);
                if (denom < -1e-4f)
                {
                    float t = Vector3.Dot(w.point - ray.origin, w.normal) / denom;
                    if (t > 0f && t < best && Mathf.Abs(Vector3.Dot(ray.GetPoint(t) - w.point, w.right)) <= w.halfWidth)
                    {
                        best = t; any = true;
                        hit.point = ray.GetPoint(t); hit.normal = w.normal;
                    }
                }
            }
            // floor
            if (ray.direction.y < -1e-4f)
            {
                float t = (_room.FloorY - ray.origin.y) / ray.direction.y;
                if (t > 0f && t < best) { best = t; any = true; hit.point = ray.GetPoint(t); hit.normal = Vector3.up; }
            }
            // ceiling
            if (ray.direction.y > 1e-4f)
            {
                float t = (_room.CeilingOrDefault - ray.origin.y) / ray.direction.y;
                if (t > 0f && t < best) { best = t; any = true; hit.point = ray.GetPoint(t); hit.normal = Vector3.down; }
            }
            if (!any) return false;
            hit.status = ArHitStatus.Hit;
            hit.normalConfidence = 1f;
            return true;
        }

        public bool PlaceBox(Ray ray, Vector3 boxSize, Vector3 upwards, out ArHit hit)
        {
            if (!Raycast(ray, 10f, out hit)) return false;
            if (hit.normal.y < 0.9f) return false;
            if (!_room.HasTable || Mathf.Abs(hit.point.y - _room.Table.Y) > 0.01f) return true; // the floor counts as free
            // the footprint must fit on the top
            Vector3 f = Vector3.ProjectOnPlane(upwards, Vector3.up);
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            f.Normalize();
            Vector3 r = Vector3.Cross(Vector3.up, f);
            float hx = boxSize.x * 0.5f, hy = boxSize.y * 0.5f;
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
                if (!_room.InsideTable(hit.point + r * (hx * sx) + f * (hy * sy))) return false;
            return true;
        }
    }
}
