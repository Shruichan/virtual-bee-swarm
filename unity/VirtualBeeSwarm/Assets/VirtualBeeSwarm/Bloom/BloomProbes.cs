using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>
    /// The no-scan fallback (venue): while ArRig has the depth raycaster on (all of `pre`), 6 rays a frame from the head
    /// (4 down at the floor, yaw +-35 deg, pitch 20-75 deg; 2 near level at the walls) collect what the headset has
    /// actually seen. Only Hit with normalConfidence > 0.5 counts (209 maps a bad orientation to Hit + zero normal).
    ///   floor: normal.y > 0.85 and 0.3 m below the table top, in 0.25 m cells; floor height = median of the hits
    ///   walls: |normal.y| &lt; 0.3, in 0.2 m columns; a vine root only with 6+ hits over 3+ s whose normals agree
    ///          (|sum| / n > 0.85): a person standing near the table gives a few scattered hits, a wall gives a column
    ///   floor hits more than 8 cm off the running median (low furniture, a bag) are dropped once 16 are in
    /// Never switches the raycaster on itself (ArRig owns that), so it costs nothing after `pre`. Cleared when the table
    /// moves more than 0.3 m (recentre / new placement): the cached points would be stale.
    /// </summary>
    public sealed class BloomProbes : MonoBehaviour
    {
        public const int MinFloorCells = 8;
        const int MinWallHits = 6;
        const float MinWallSeconds = 3f, MinWallCoherence = 0.85f;
        const float FloorCell = 0.25f, WallCell = 0.2f, MaxDistance = 4.5f;
        const int RaysPerFrame = 6, MaxFloorYs = 1024;

        struct WallAcc
        {
            public int n;
            public float first, last;
            public Vector3 normalSum, pointSum;
        }

        public struct Column
        {
            public Vector3 point, normal;
        }

        readonly HashSet<long> _floor = new HashSet<long>();
        readonly List<float> _floorYs = new List<float>(MaxFloorYs);
        readonly List<float> _tmpYs = new List<float>(MaxFloorYs);
        readonly Dictionary<long, WallAcc> _wall = new Dictionary<long, WallAcc>();
        readonly System.Random _rnd = new System.Random(11);
        Vector3 _tableAt;
        bool _haveTable;
        float _medianY;
        bool _medianDirty = true;

        public long Rays { get; private set; }
        public long Hits { get; private set; }
        public double TotalMicros { get; private set; }
        public int FloorCellCount => _floor.Count;
        public double MicrosPerRay => Rays > 0 ? TotalMicros / Rays : 0.0;

        public int WallColumnCount
        {
            get
            {
                int n = 0;
                foreach (var kv in _wall) if (Solid(kv.Value)) n++;
                return n;
            }
        }

        public float FloorY
        {
            get
            {
                if (_floorYs.Count == 0) return 0f;
                if (_medianDirty)
                {
                    _tmpYs.Clear();
                    _tmpYs.AddRange(_floorYs);
                    _tmpYs.Sort();
                    _medianY = _tmpYs[_tmpYs.Count / 2];
                    _medianDirty = false;
                }
                return _medianY;
            }
        }

        static bool Solid(WallAcc a) =>
            a.n >= MinWallHits && a.last - a.first >= MinWallSeconds && a.normalSum.magnitude / a.n > MinWallCoherence;

        /// <summary>True if p is within `pad` of a solid probe wall column (no flowers inside a wall or a pillar).</summary>
        public bool NearWall(Vector3 p, float pad)
        {
            if (_wall.Count == 0) return false;
            int cx = Mathf.FloorToInt(p.x / WallCell), cz = Mathf.FloorToInt(p.z / WallCell), r = Mathf.CeilToInt(pad / WallCell);
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (!_wall.TryGetValue(BloomSurfaces.Key(cx + dx, cz + dz), out var a) || !Solid(a)) continue;
                Vector3 q = a.pointSum / a.n;
                float ex = q.x - p.x, ez = q.z - p.z;
                if (ex * ex + ez * ez < pad * pad) return true;
            }
            return false;
        }

        public bool IsFloor(Vector3 p) => _floor.Contains(BloomSurfaces.Key(Mathf.FloorToInt(p.x / FloorCell), Mathf.FloorToInt(p.z / FloorCell)));

        public IEnumerable<Column> WallColumns()
        {
            foreach (var kv in _wall)
            {
                var a = kv.Value;
                if (!Solid(a)) continue;
                Vector3 n = a.normalSum;
                n.y = 0f;
                if (n.sqrMagnitude < 1e-6f) continue;
                yield return new Column { point = a.pointSum / a.n, normal = n.normalized };
            }
        }

        public void Clear()
        {
            _floor.Clear();
            _floorYs.Clear();
            _wall.Clear();
            _medianDirty = true;
        }

        /// <summary>RoomBloom calls this on ArRig.RoomChanged.</summary>
        public void OnRoom(RoomModel room)
        {
            if (room == null || !room.HasTable) return;
            Vector3 c = room.Table.center;
            if (_haveTable && (c - _tableAt).sqrMagnitude > 0.09f && (_floor.Count > 0 || _wall.Count > 0))
            {
                UnityEngine.Debug.Log("[VBS][Bloom] probes: the table moved " + (c - _tableAt).magnitude.ToString("0.00") +
                                      " m: cleared " + _floor.Count + " floor cells, " + _wall.Count + " wall cells");
                Clear();
            }
            _tableAt = c;
            _haveTable = true;
        }

        public string Describe() =>
            "probes: " + Rays + " rays (" + MicrosPerRay.ToString("0.0") + " us each), " + Hits + " hits, floor cells " + FloorCellCount +
            (FloorCellCount > 0 ? " (floor y " + FloorY.ToString("0.00") + ")" : "") + ", wall columns " + WallColumnCount;

        float R(float a, float b) => a + (float)_rnd.NextDouble() * (b - a);

        void Update()
        {
            if (!BloomTuning.Probes || !BloomTuning.On) return;
            var rig = ArRig.Instance;
            var rc = rig != null ? rig.Raycaster : null;
            if (rc == null || !rc.Enabled || !rc.IsSupported) return;
            var cam = Camera.main;
            if (cam == null) return;
            Transform t = cam.transform;
            Vector3 eye = t.position;
            float yaw = t.eulerAngles.y;
            var room = rig.Room;
            float topY = room.HasTable ? room.Table.Y : eye.y - 0.8f;

            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < RaysPerFrame; i++)
            {
                bool floorRay = i < 4;
                float pitch = floorRay ? R(20f, 75f) : R(-25f, 5f); // Unity: positive pitch looks down
                Vector3 dir = Quaternion.Euler(pitch, yaw + R(-35f, 35f), 0f) * Vector3.forward;
                if (!rc.Raycast(new Ray(eye, dir), MaxDistance, out var hit)) continue;
                if (hit.status != ArHitStatus.Hit || hit.normalConfidence <= 0.5f) continue;
                Hits++;
                Vector3 p = hit.point, n = hit.normal;
                if (n.y > 0.85f && p.y < topY - 0.3f)
                {
                    if (_floorYs.Count >= 16 && Mathf.Abs(p.y - FloorY) > 0.08f) continue; // low furniture, not the floor
                    _floor.Add(BloomSurfaces.Key(Mathf.FloorToInt(p.x / FloorCell), Mathf.FloorToInt(p.z / FloorCell)));
                    if (_floorYs.Count < MaxFloorYs) { _floorYs.Add(p.y); _medianDirty = true; }
                }
                else if (Mathf.Abs(n.y) < 0.3f)
                {
                    if (room.HasTable && room.InsideTable(p, -0.15f)) continue; // the table's own front: not a wall
                    long k = BloomSurfaces.Key(Mathf.FloorToInt(p.x / WallCell), Mathf.FloorToInt(p.z / WallCell));
                    _wall.TryGetValue(k, out var a);
                    if (a.n == 0) a.first = Time.time;
                    a.last = Time.time;
                    a.n++;
                    a.normalSum += n;
                    a.pointSum += p;
                    _wall[k] = a;
                }
            }
            Rays += RaysPerFrame;
            TotalMicros += (Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency;
        }
    }
}
