using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace VirtualBeeSwarm.AR
{
    /// <summary>
    /// What the game knows about the real room, as a few planes cached once per placement (PLAN.md V3). Plain C#, no
    /// Meta types, no per-frame queries: the bees and the delivery code read it every frame for free.
    ///   table top: pose (y up, +Z = away from the player), half extents, height
    ///   one window wall (optional): point, normal into the room, right, half width
    ///   floor and ceiling heights (ceiling NaN = unknown; CeilingOrDefault falls back to ar.ceilingY)
    ///   up to 24 table landing spots outside the footprints (hive, stations, choice arc, rocket)
    ///   up to 8 wall spots around the window
    /// Filled by FakeRoomSource (desktop/Linux) or PlacementRoomSource (headset: the placed table + the raycast wall).
    /// </summary>
    public sealed class RoomModel
    {
        public const int MaxTableSpots = 24;
        public const int MaxWallSpots = 8;

        public struct TableTop
        {
            public bool valid;
            public Vector3 center;      // on the top surface
            public Quaternion rotation; // yaw only
            public Vector2 half;        // x = half width, y = half depth (along +Z)
            public Matrix4x4 toWorld, toLocal;
            public float Y => center.y;
        }

        public struct Wall
        {
            public bool valid;
            public Vector3 point;  // on the wall plane
            public Vector3 normal; // horizontal, into the room (toward the player)
            public Vector3 right;
            public float halfWidth;
            public float distance; // from the eye when it was found (m)
        }

        /// <summary>A keep-out rectangle on the table (oriented, in world space).</summary>
        public struct Footprint
        {
            public Vector3 center, right, forward;
            public Vector2 half;
        }

        public string Source = "none";
        public TableTop Table;
        public Wall WindowWall;
        public float FloorY;
        public float CeilingY = float.NaN;
        public float CeilingOrDefault => float.IsNaN(CeilingY) ? ArTuning.CeilingY : CeilingY;

        public readonly List<Footprint> Footprints = new List<Footprint>(8);
        public readonly Vector3[] TableSpots = new Vector3[MaxTableSpots];
        public int TableSpotCount { get; private set; }
        public readonly Vector3[] WallSpots = new Vector3[MaxWallSpots];
        public readonly Vector3[] WallSpotNormals = new Vector3[MaxWallSpots];
        public int WallSpotCount { get; private set; }

        public bool HasTable => Table.valid;
        public bool HasWall => WindowWall.valid;

        public void Clear(string source)
        {
            Source = source;
            Table = default;
            WindowWall = default;
            FloorY = 0f;
            CeilingY = float.NaN;
            Footprints.Clear();
            TableSpotCount = 0;
            WallSpotCount = 0;
        }

        public void SetTable(Vector3 topCenter, Quaternion yaw, Vector2 half)
        {
            Vector3 fwd = yaw * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            var rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            Table = new TableTop
            {
                valid = true,
                center = topCenter,
                rotation = rot,
                half = new Vector2(Mathf.Max(0.05f, half.x), Mathf.Max(0.05f, half.y)),
                toWorld = Matrix4x4.TRS(topCenter, rot, Vector3.one),
            };
            Table.toLocal = Table.toWorld.inverse;
        }

        public void SetWall(Vector3 point, Vector3 normal, float halfWidth, float distance)
        {
            normal.y = 0f;
            if (normal.sqrMagnitude < 1e-6f) { WindowWall = default; return; }
            normal.Normalize();
            WindowWall = new Wall
            {
                valid = true,
                point = point,
                normal = normal,
                right = Vector3.Cross(Vector3.up, normal).normalized,
                halfWidth = halfWidth,
                distance = distance,
            };
        }

        /// <summary>Keep-out rectangle given in an anchor's local XZ (e.g. the TableAnchor).</summary>
        public void AddFootprint(Vector3 anchorPos, Quaternion anchorRot, Vector2 localCenterXZ, Vector2 halfXZ)
        {
            Vector3 right = anchorRot * Vector3.right; right.y = 0f;
            Vector3 fwd = anchorRot * Vector3.forward; fwd.y = 0f;
            if (right.sqrMagnitude < 1e-6f || fwd.sqrMagnitude < 1e-6f) return;
            right.Normalize();
            fwd.Normalize();
            Footprints.Add(new Footprint
            {
                center = anchorPos + right * localCenterXZ.x + fwd * localCenterXZ.y,
                right = right,
                forward = fwd,
                half = halfXZ,
            });
        }

        /// <summary>The default keep-outs around the TableAnchor: hive, rocket, stations, Beeswax, and the choice arc
        /// over the front edge (anchor-local x within ±0.40 m, z from -0.26 to 0.42 m).</summary>
        public void AddDefaultFootprints(Transform tableAnchor)
        {
            if (tableAnchor == null) return;
            AddFootprint(tableAnchor.position, tableAnchor.rotation, new Vector2(0f, 0.08f), new Vector2(0.40f, 0.34f));
        }

        public Vector3 TableToWorld(float x, float z) => Table.toWorld.MultiplyPoint3x4(new Vector3(x, 0f, z));

        public bool InsideTable(Vector3 world, float margin = 0f)
        {
            if (!Table.valid) return false;
            Vector3 l = Table.toLocal.MultiplyPoint3x4(world);
            return Mathf.Abs(l.x) <= Table.half.x - margin && Mathf.Abs(l.z) <= Table.half.y - margin;
        }

        public bool InsideFootprint(Vector3 world, float pad = 0f)
        {
            for (int i = 0; i < Footprints.Count; i++)
            {
                var f = Footprints[i];
                Vector3 d = world - f.center;
                if (Mathf.Abs(Vector3.Dot(d, f.right)) <= f.half.x + pad && Mathf.Abs(Vector3.Dot(d, f.forward)) <= f.half.y + pad)
                    return true;
            }
            return false;
        }

        /// <summary>Keeps a point at least `clearance` above the table top when it is over the table (for the swarm:
        /// bees can't sink into the real table). Returns how far it was pushed up.</summary>
        public float ClampAboveTable(ref Vector3 p, float clearance = 0.01f)
        {
            if (!Table.valid || !InsideTable(p)) return 0f;
            float minY = Table.Y + clearance;
            if (p.y >= minY || p.y < Table.Y - 0.25f) return 0f; // well below the top = under the table: leave it
            float d = minY - p.y;
            p.y = minY;
            return d;
        }

        /// <summary>Up to 24 table spots on a jittered grid, at least `minGap` apart, inside the top by `margin`, and
        /// outside every footprint. Deterministic for a given seed.</summary>
        public void ComputeTableSpots(int seed = 7, float margin = 0.04f, float minGap = 0.05f)
        {
            TableSpotCount = 0;
            if (!Table.valid) return;
            var rnd = new System.Random(seed);
            var candidates = new List<Vector3>(256);
            float step = minGap;
            for (float z = -Table.half.y + margin; z <= Table.half.y - margin + 1e-4f; z += step)
            for (float x = -Table.half.x + margin; x <= Table.half.x - margin + 1e-4f; x += step)
            {
                float jx = ((float)rnd.NextDouble() - 0.5f) * step * 0.3f;
                float jz = ((float)rnd.NextDouble() - 0.5f) * step * 0.3f;
                float lx = Mathf.Clamp(x + jx, -Table.half.x + margin, Table.half.x - margin);
                float lz = Mathf.Clamp(z + jz, -Table.half.y + margin, Table.half.y - margin);
                Vector3 w = TableToWorld(lx, lz);
                if (InsideFootprint(w, 0.02f)) continue;
                candidates.Add(w);
            }
            // Fisher-Yates with the seed, then keep the first that are minGap apart from the kept ones.
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }
            float gap2 = minGap * minGap * 0.8f;
            for (int i = 0; i < candidates.Count && TableSpotCount < MaxTableSpots; i++)
            {
                bool ok = true;
                for (int k = 0; k < TableSpotCount && ok; k++)
                    if ((TableSpots[k] - candidates[i]).sqrMagnitude < gap2) ok = false;
                if (ok) TableSpots[TableSpotCount++] = candidates[i];
            }
        }

        /// <summary>Up to 8 spots on the wall around the window frame (center, right/up axes, half size of the pane).</summary>
        public void ComputeWallSpots(Vector3 windowCenter, Vector3 right, Vector3 up, Vector2 halfSize, Vector3 wallNormal)
        {
            WallSpotCount = 0;
            if (!WindowWall.valid) return;
            float ox = halfSize.x + 0.08f, oy = halfSize.y + 0.08f;
            Vector2[] k =
            {
                new Vector2(-ox, oy * 0.5f), new Vector2(ox, oy * 0.5f), new Vector2(-ox, -oy * 0.4f), new Vector2(ox, -oy * 0.4f),
                new Vector2(-halfSize.x * 0.5f, oy), new Vector2(halfSize.x * 0.5f, oy), new Vector2(0f, oy), new Vector2(-ox, 0f),
            };
            for (int i = 0; i < k.Length && WallSpotCount < MaxWallSpots; i++)
            {
                WallSpots[WallSpotCount] = windowCenter + right * k[i].x + up * k[i].y + wallNormal * 0.01f;
                WallSpotNormals[WallSpotCount] = wallNormal;
                WallSpotCount++;
            }
        }

        public string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            string t = Table.valid
                ? "table " + (Table.half.x * 2f).ToString("0.00", c) + "x" + (Table.half.y * 2f).ToString("0.00", c) + " m top y=" +
                  Table.Y.ToString("0.000", c)
                : "no table";
            string w = WindowWall.valid
                ? "wall at " + WindowWall.distance.ToString("0.00", c) + " m normal=" + V(WindowWall.normal)
                : "no wall";
            return Source + ": " + t + ", " + w + ", floor y=" + FloorY.ToString("0.00", c) + ", ceiling " +
                   (float.IsNaN(CeilingY) ? "unknown (" + ArTuning.CeilingY.ToString("0.0", c) + " assumed)" : "y=" + CeilingY.ToString("0.00", c)) +
                   ", " + TableSpotCount + " table spots, " + WallSpotCount + " wall spots, " + Footprints.Count + " footprint(s)";
        }

        static string V(Vector3 v) => "(" + v.x.ToString("0.00", CultureInfo.InvariantCulture) + "," +
                                      v.y.ToString("0.00", CultureInfo.InvariantCulture) + "," +
                                      v.z.ToString("0.00", CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>Desktop / Linux: the FakeTable from the scene (or a 1.4 x 0.8 m table at 0.75 m) plus a wall 1.5 m
    /// behind its back edge. Keeps the Linux autopilot exercising every AR code path.</summary>
    public static class FakeRoomSource
    {
        public const float WallBehindTable = 1.5f;

        public static void Build(RoomModel room, Transform fakeTable, Transform tableAnchor)
        {
            room.Clear("fake");
            Vector3 center;
            Vector2 half;
            Quaternion rot;
            if (fakeTable != null)
            {
                Vector3 s = fakeTable.lossyScale;
                rot = fakeTable.rotation;
                center = fakeTable.position + rot * new Vector3(0f, s.y * 0.5f, 0f);
                half = new Vector2(Mathf.Abs(s.x) * 0.5f, Mathf.Abs(s.z) * 0.5f);
            }
            else
            {
                rot = Quaternion.identity;
                center = new Vector3(0f, 0.75f, 0.1f);
                half = new Vector2(0.7f, 0.4f);
            }
            room.SetTable(center, rot, half);
            Vector3 fwd = room.Table.rotation * Vector3.forward;
            Vector3 wallPoint = center + fwd * (half.y + WallBehindTable);
            wallPoint.y = 1.4f;
            room.SetWall(wallPoint, -fwd, 2.5f, half.y + WallBehindTable + 0.8f);
            room.FloorY = 0f;
            room.CeilingY = 2.4f;
            room.AddDefaultFootprints(tableAnchor);
            room.ComputeTableSpots();
        }
    }

    /// <summary>Headset: the table from the placed TableAnchor (front edge `inset` in front of it, 1.0 m wide,
    /// ar.tableDepth deep) and the wall found by the raycast (or none).</summary>
    public static class PlacementRoomSource
    {
        public const float DefaultWidth = 1.0f;

        public static void Build(RoomModel room, Transform tableAnchor, float inset, RoomModel.Wall wall, float floorY)
        {
            room.Clear("placement");
            if (tableAnchor == null) return;
            float k = Mathf.Max(0.01f, tableAnchor.lossyScale.x); // meadow: the rig may be scaled up on the floor
            float depth = ArTuning.TableDepth * k;
            inset *= k;
            Vector3 fwd = tableAnchor.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 center = tableAnchor.position + fwd * (depth * 0.5f - inset);
            room.SetTable(center, Quaternion.LookRotation(fwd, Vector3.up), new Vector2(DefaultWidth * 0.5f * k, depth * 0.5f));
            if (wall.valid) room.WindowWall = wall;
            room.FloorY = floorY;
            room.AddDefaultFootprints(tableAnchor);
            room.ComputeTableSpots();
        }
    }
}
