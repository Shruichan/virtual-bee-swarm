using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>
    /// Desktop / Linux stand-in for a room scan (RoomModel.cs stays untouched): the window wall from the RoomModel, two
    /// side walls 2.97 m either side of the table centre (the inner faces of the capture's stand-in room walls: any
    /// further out and the vines' stems hide inside them, leaving the blossoms floating), a floor 5.95 m
    /// deep, and two pillars (0.35 m square, +-1.4 m along the window wall, 0.9 m out from it) so the autopilot and the
    /// captures exercise the scene path: floor polygon, wall vines, pillar spirals. In desktop mode it also draws the
    /// pillars (and the side walls, unless the capture room already has them) as grey boxes, no colliders.
    /// </summary>
    public sealed class BloomFakeRoom : IBloomSceneSource
    {
        public const float SideWall = 2.97f, Depth = 5.95f, // meadow: the whole capture room floor (6 m)
            PillarSize = 0.35f, PillarAlong = 1.4f, PillarOut = 0.9f;

        GameObject _boxes;
        Material _grey, _wallGrey;

        public string Name => "fake room";
        public string Status { get; private set; } = "not filled";

        public void Begin() { }

        public bool TryFill(BloomScene scene)
        {
            var rig = ArRig.Instance;
            bool ok = rig != null && Fill(rig.Room, scene);
            Status = ok ? "3 walls, 2 pillars, table" : "no table yet";
            return ok;
        }

        /// <summary>Builds the fake scene from a RoomModel (static so the editor check can run it without a scene).</summary>
        public static bool Fill(RoomModel room, BloomScene s)
        {
            s.Clear("fake room");
            if (room == null || !room.HasTable) return false;
            float ceil = room.CeilingOrDefault;
            s.FloorY = room.FloorY;
            Vector3 c = room.Table.center;
            Vector3 n, right, wallPoint;
            if (room.HasWall)
            {
                n = room.WindowWall.normal;
                right = room.WindowWall.right;
                wallPoint = room.WindowWall.point;
            }
            else
            {
                Vector3 f = room.Table.rotation * Vector3.forward;
                n = -f;
                right = Vector3.Cross(Vector3.up, n).normalized;
                wallPoint = c + f * (room.Table.half.y + FakeRoomSource.WallBehindTable);
            }
            // the window wall's foot right behind the table centre
            Vector3 foot = c + n * Vector3.Dot(wallPoint - c, n);
            foot.y = s.FloorY;
            Vector3 back = foot + n * Depth;
            Vector3 l0 = foot - right * SideWall, r0 = foot + right * SideWall, l1 = back - right * SideWall, r1 = back + right * SideWall;
            s.FloorPolygon.Add(new Vector2(l0.x, l0.z));
            s.FloorPolygon.Add(new Vector2(r0.x, r0.z));
            s.FloorPolygon.Add(new Vector2(r1.x, r1.z));
            s.FloorPolygon.Add(new Vector2(l1.x, l1.z));
            float h = ceil - s.FloorY;
            Vector3 up = Vector3.up;
            s.Walls.Add(new BloomRect { center = foot + up * (h * 0.5f), right = right, up = up, normal = n, half = new Vector2(SideWall, h * 0.5f) });
            s.Walls.Add(new BloomRect
            {
                center = foot - right * SideWall + n * (Depth * 0.5f) + up * (h * 0.5f), right = n, up = up, normal = right,
                half = new Vector2(Depth * 0.5f, h * 0.5f),
            });
            s.Walls.Add(new BloomRect
            {
                center = foot + right * SideWall + n * (Depth * 0.5f) + up * (h * 0.5f), right = -n, up = up, normal = -right,
                half = new Vector2(Depth * 0.5f, h * 0.5f),
            });
            var rot = Quaternion.LookRotation(-n, Vector3.up);
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 pc = foot + right * (side * PillarAlong) + n * (PillarOut + PillarSize * 0.5f) + up * (h * 0.5f);
                s.Volumes.Add(new BloomBox { center = pc, rotation = rot, half = new Vector3(PillarSize * 0.5f, h * 0.5f, PillarSize * 0.5f), pillar = true });
            }
            float th = room.Table.Y - s.FloorY;
            if (!MeadowLayout.Active) // meadow: no table, the floor under the hive rig is meadow too
            s.Volumes.Add(new BloomBox
            {
                center = room.Table.center - up * (th * 0.5f), rotation = room.Table.rotation,
                half = new Vector3(room.Table.half.x, th * 0.5f, room.Table.half.y),
            });
            return true;
        }

        /// <summary>Desktop only: grey stand-ins for the pillars and side walls so captures make sense.</summary>
        public void ShowBoxes(BloomScene s, Transform parent, bool on)
        {
            if (!on || s == null)
            {
                if (_boxes != null) _boxes.SetActive(false);
                return;
            }
            if (_boxes != null) Object.Destroy(_boxes);
            _boxes = new GameObject("BloomFakeRoom (desktop)");
            _boxes.transform.SetParent(parent, false);
            if (_grey == null) _grey = GameMaterials.Create("VBS_BloomFakeRoom", new Color(0.62f, 0.62f, 0.6f));
            if (_wallGrey == null) _wallGrey = GameMaterials.Create("VBS_BloomFakeWall", new Color(0.8f, 0.79f, 0.76f));
            var grey = _grey;
            var wallGrey = _wallGrey;
            foreach (var b in s.Volumes)
                if (b.pillar) Box("Pillar", b.center, b.rotation, b.half * 2f, grey);
            bool captureRoom = GameObject.Find("CaptureRoom") != null; // ShowCapture's stand-in room: walls at the same place
            for (int i = 1; i < s.Walls.Count && !captureRoom; i++) // [0] is the window wall: the game / capture room has its own
            {
                var w = s.Walls[i];
                Box("SideWall", w.center - w.normal * 0.03f, Quaternion.LookRotation(w.normal, Vector3.up),
                    new Vector3(w.half.x * 2f, w.half.y * 2f, 0.06f), wallGrey);
            }
        }

        void Box(string name, Vector3 c, Quaternion r, Vector3 size, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            go.transform.SetParent(_boxes.transform, false);
            go.transform.SetPositionAndRotation(c, r);
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = m;
        }
    }
}
