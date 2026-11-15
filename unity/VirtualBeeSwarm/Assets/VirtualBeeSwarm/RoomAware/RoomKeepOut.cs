using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using VirtualBeeSwarm.AR;
using VirtualBeeSwarm.Bloom;

namespace VirtualBeeSwarm.RoomAware
{
    /// <summary>A real thing in the room the game must never put anything inside (yaw-only box, world space).</summary>
    public struct KeepBox
    {
        public Vector3 c;
        public Quaternion rot;
        public Vector3 half; // y = vertical half height
        public string label;
        public bool depth;   // a column of occupied depth cells, not a scanned volume
    }

    /// <summary>A yaw-only oriented box in world space (a rig part, the portal rim, the ORDER UP board in its row's frame).
    /// Tested as it stands, not as a world AABB, so placement doesn't depend on which way the player faced at boot.</summary>
    public struct Obb
    {
        public Vector3 c;
        public Quaternion rot;
        public Vector3 half;

        public Obb(Vector3 c, Quaternion rot, Vector3 half) { this.c = c; this.rot = rot; this.half = half; }

        public static Obb FromBounds(Bounds b) => new Obb(b.center, Quaternion.identity, b.extents);

        /// <summary>A local box under a matrix whose rotation is a yaw (the rig anchor, the window, a row).</summary>
        public static Obb From(Bounds local, Matrix4x4 m)
        {
            Vector3 ax = m.GetColumn(0), ay = m.GetColumn(1), az = m.GetColumn(2);
            Vector3 f = az;
            f.y = 0f;
            var r = f.sqrMagnitude > 1e-10f ? Quaternion.LookRotation(f.normalized, Vector3.up) : Quaternion.identity;
            return new Obb(m.MultiplyPoint3x4(local.center), r,
                new Vector3(local.extents.x * ax.magnitude, local.extents.y * ay.magnitude, local.extents.z * az.magnitude));
        }

        public Vector3 Right => rot * Vector3.right;
        public Vector3 Fwd => rot * Vector3.forward;

        public Bounds Aabb
        {
            get
            {
                Vector3 r = Right, f = Fwd;
                float ex = Mathf.Abs(r.x) * half.x + Mathf.Abs(f.x) * half.z, ez = Mathf.Abs(r.z) * half.x + Mathf.Abs(f.z) * half.z;
                return new Bounds(c, new Vector3(ex, half.y, ez) * 2f);
            }
        }

        public Obb Moved(Vector3 v) => new Obb(c + v, rot, half);
        public Obb Grown(float e) => new Obb(c, rot, half + Vector3.one * e);

        public Vector3 ClosestPoint(Vector3 p)
        {
            Vector3 l = Quaternion.Inverse(rot) * (p - c);
            l = new Vector3(Mathf.Clamp(l.x, -half.x, half.x), Mathf.Clamp(l.y, -half.y, half.y), Mathf.Clamp(l.z, -half.z, half.z));
            return c + rot * l;
        }

        public bool Contains(Vector3 p)
        {
            Vector3 l = Quaternion.Inverse(rot) * (p - c);
            return Mathf.Abs(l.x) <= half.x && Mathf.Abs(l.y) <= half.y && Mathf.Abs(l.z) <= half.z;
        }

        /// <summary>Does the segment a-b pass through the box?</summary>
        public bool Segment(Vector3 a, Vector3 b)
        {
            var inv = Quaternion.Inverse(rot);
            Vector3 o = inv * (a - c), v = inv * (b - a);
            float t0 = 0f, t1 = 1f;
            for (int ax = 0; ax < 3; ax++)
            {
                float oo = o[ax], vv = v[ax], hh = half[ax];
                if (Mathf.Abs(vv) < 1e-7f) { if (Mathf.Abs(oo) > hh) return false; continue; }
                float ta = (-hh - oo) / vv, tb = (hh - oo) / vv;
                if (ta > tb) (ta, tb) = (tb, ta);
                t0 = Mathf.Max(t0, ta);
                t1 = Mathf.Min(t1, tb);
                if (t0 > t1) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Where the real stuff is, for every placement (one shared service; RoomAware/ owns it).
    ///   scan    the room scan's furniture volumes (MRUK on the headset: TABLE, COUCH, STORAGE, BED, SCREEN, LAMP, PLANT,
    ///           OTHER...; never the floor, ceiling or walls), its walls and floor outline. Read through MeadowField's
    ///           scene source, so the desktop fake room and the capture room fixtures (ops/room-playtest.py) take the same path.
    ///   depth   headset, in `pre`, only when there is no usable scan with furniture: a fan of depth raycasts from the eyes
    ///           (EnvironmentRaycastManager) marks 20 cm cells that hold something real between 12 cm over the floor and
    ///           2.1 m, each filled down to the floor. A cell counts only once it was hit again at least 1 s later (a person
    ///           walking past doesn't), a ray that passes through a cell clears it again (someone who walked away), floor
    ///           hits are skipped, and every group reset starts empty. Hands and anything within 0.8 m of the eyes are
    ///           ignored. Desktop: room.source=depth casts against the capture room's furniture colliders instead, so the
    ///           fallback runs headless.
    ///   sanity  a scan whose floor outline doesn't hold the player (stale, another room, mislocalized) is ignored.
    ///   API     IsFree(bounds), Penetration(bounds), NearestFree(bounds, prefer, maxDist), FreeFloorSpot(footprint, near),
    ///           SightBlocked(a, b), PushOut(item pose) for deliveries, KeepClear(prop) for the bench props.
    ///   Beeswax every keep-out gets an invisible proxy box (RoomKeepOutProxy) that BeeswaxBody plans around; each wall a slab
    ///           (RoomWallProxy) he avoids, which is never folded into a detour box.
    ///   log     "[VBS][Room] keep-outs ..." each time the set changes.
    /// Installs itself into any scene with a GameDirector. room.aware=false (or VBS_ROOM_AWARE=0) switches all of it off.
    /// </summary>
    [DefaultExecutionOrder(58)]
    public sealed class RoomKeepOut : MonoBehaviour
    {
        public static RoomKeepOut Instance { get; private set; }
        public const float Cell = 0.2f;
        const float DepthMinUp = 0.12f, DepthMaxUp = 2.1f, DepthIgnoreNear = 0.8f, HandIgnore = 0.25f;

        /// <summary>True when the service is on and knows something about the room.</summary>
        public static bool Ready => Instance != null && RoomAwareTuning.On && Instance.HasData;
        public static bool On => RoomAwareTuning.On;

        public readonly List<KeepBox> Boxes = new List<KeepBox>(32);       // scanned furniture
        public readonly List<BloomRect> Walls = new List<BloomRect>(8);
        readonly List<Vector2> _floor = new List<Vector2>(16);
        readonly Dictionary<Vector3Int, float> _firstHit = new Dictionary<Vector3Int, float>(512); // seen once, not yet confirmed
        readonly HashSet<Vector3Int> _direct = new HashSet<Vector3Int>();   // confirmed depth hits
        readonly HashSet<Vector3Int> _cells = new HashSet<Vector3Int>();    // occupied depth cells (hits filled down to the floor)
        readonly List<Vector3> _snap = new List<Vector3>(64);               // the scan as last read (hysteresis)
        bool _snapInside;
        public float FloorY { get; private set; }
        public string Source { get; private set; } = "none";
        public int Version { get; private set; }
        public int CellCount => _cells.Count;
        public bool HasData => Boxes.Count > 0 || _cells.Count > 0 || _floor.Count >= 3 || Walls.Count > 0;
        public event Action Changed;

        GameDirector _director;
        float _nextScene, _nextChange = -1f, _nextLog;
        int _cellsAtLastChange, _rays, _rayHits, _carved;
        bool _desktop, _depthLogged, _changedPending, _scanRead;
        int _fanIndex;
        readonly RaycastHit[] _phys = new RaycastHit[16];
        GameObject _proxyRoot;
        readonly List<GameObject> _proxies = new List<GameObject>(48);
        int _proxyVersion = -1;
        Mesh _cube;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------ install
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            RoomAwareTuning.EnsureLoaded();
            var gd = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (gd == null || FindAnyObjectByType<RoomKeepOut>() != null) return;
            var go = new GameObject("RoomKeepOut");
            go.transform.SetParent(gd.transform.parent, false);
            go.AddComponent<RoomKeepOut>();
            go.AddComponent<ControlKeeper>();
            if (RoomAudit.Wanted) go.AddComponent<RoomAudit>();
            Debug.Log("[VBS][Room] installed: " + RoomAwareTuning.Describe());
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { enabled = false; return; }
            Instance = this;
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
        }

        void Start()
        {
            var rig = ArRig.Instance;
            _desktop = rig == null || rig.DesktopMode;
            if (_director != null) _director.GroupResetDone += OnGroupReset;
        }

        void OnDestroy()
        {
            if (_director != null) _director.GroupResetDone -= OnGroupReset;
            if (Instance == this) Instance = null;
        }

        /// <summary>A new group: what the last group's depth fan saw (people at the booth) is forgotten.</summary>
        void OnGroupReset()
        {
            RigFit.ResetForGroup();
            if (_cells.Count == 0 && _firstHit.Count == 0) return;
            _cells.Clear();
            _direct.Clear();
            _firstHit.Clear();
            _rays = _rayHits = _carved = 0;
            _cellsAtLastChange = 0;
            Bump("group reset: depth cells cleared");
        }

        bool InPre => _director == null || _director.Beat == GameBeat.Pre;

        void Update()
        {
            if (!RoomAwareTuning.On) return;
            float now = Time.time;
            if (now >= _nextScene)
            {
                _nextScene = now + 0.5f;
                ReadScene();
            }
            if (WantDepth()) SampleDepth(_desktop ? 120 : 12);
            if (Mathf.Abs(_cells.Count - _cellsAtLastChange) >= 6 && now >= _nextLog)
            {
                _cellsAtLastChange = _cells.Count;
                Bump("depth: " + _cells.Count + " occupied cells from " + _rayHits + "/" + _rays + " hits, " + _carved + " cleared again");
            }
            if (_changedPending && now >= _nextChange)
            {
                _changedPending = false;
                _nextChange = now + 1.5f;
                try { Changed?.Invoke(); } catch (Exception e) { Debug.LogWarning("[VBS][Room] change handler: " + e.Message); }
                if (InPre)
                {
                    RigFit.MaybeReplace("room keep-outs changed");
                    PortalKeeper.Refit("room keep-outs changed"); // never while it swells at the escape
                }
            }
            if (_proxyVersion != Version) UpdateProxies();
        }

        void Bump(string what)
        {
            Version++;
            _changedPending = true;
            _nextLog = Time.time + 2f;
            Debug.Log("[VBS][Room] keep-outs v" + Version + " (" + what + "): " + Describe());
        }

        // ------------------------------------------------------------------ the scan (or the desktop / fixture room)
        /// <summary>Reads the scene source now (cheap when nothing changed): placement code calls it before asking.</summary>
        public static void ReadSceneNow()
        {
            if (Instance != null && RoomAwareTuning.On) Instance.ReadScene();
        }

        void ReadScene()
        {
            var field = MeadowField.Instance;
            var scene = field != null ? field.CurrentScene() : null;
            if (scene == null)
            {
                // no scan (the venue): the floor is wherever the meadow stands it (MeadowField / ArRig.Room), not 0
                if (Boxes.Count == 0 && _floor.Count == 0) FloorY = MeadowLayout.FloorY;
                return;
            }
            bool useVolumes = RoomAwareTuning.Source != RoomAwareTuning.SourceMode.Depth;
            bool inside = PlayerInside(scene);
            if (_scanRead && !Moved(scene, useVolumes) && inside == _snapInside) return;
            _scanRead = true;
            _snapInside = inside;
            Snapshot(scene, useVolumes);
            Boxes.Clear();
            Walls.Clear();
            _floor.Clear();
            if (!inside)
            {
                // a stale or mislocalized scan (or another room's): phantom walls and furniture would steer everything
                FloorY = MeadowLayout.FloorY;
                Source = "scan '" + scene.Name + "' IGNORED";
                Bump("the player stands outside the scan's floor outline: scan ignored, depth fallback " + (WantDepth() ? "on" : "off"));
                return;
            }
            FloorY = scene.FloorY;
            Walls.AddRange(scene.Walls);
            _floor.AddRange(scene.FloorPolygon);
            var labels = new StringBuilder();
            if (useVolumes)
                foreach (var v in scene.Volumes)
                {
                    if (v.half.y < 0.01f || v.center.y + v.half.y < FloorY + 0.03f) continue; // a rug or a floor-flat volume
                    Boxes.Add(new KeepBox { c = v.center, rot = v.rotation, half = v.half, label = string.IsNullOrEmpty(v.label) ? (v.pillar ? "pillar" : "furniture") : v.label });
                    if (labels.Length > 0) labels.Append(", ");
                    labels.Append(Boxes[Boxes.Count - 1].label);
                }
            if (Boxes.Count > 0 && _cells.Count > 0 && RoomAwareTuning.Source == RoomAwareTuning.SourceMode.Auto)
            {
                // a furnished scan wins: the depth cells (people, noise) go
                _cells.Clear();
                _direct.Clear();
                _firstHit.Clear();
                _cellsAtLastChange = 0;
            }
            Source = "scan '" + scene.Name + "'";
            Bump(Boxes.Count > 0 ? "room scan with " + Boxes.Count + " furniture: " + labels
                : useVolumes ? "room scan WITHOUT furniture (walls/floor only; depth fallback " + (WantDepth() ? "on" : "off") + ")"
                : "room.source=depth: scan furniture ignored");
        }

        /// <summary>Is the head over the scan's floor outline (0.3 m of slack)? No outline or no head: assume yes.</summary>
        bool PlayerInside(BloomScene scene)
        {
            var cam = Camera.main;
            if (cam == null || scene.FloorPolygon.Count < 3) return true;
            Vector3 e = cam.transform.position;
            if (e.sqrMagnitude < 1e-6f) return true; // the head isn't tracked yet
            for (int i = 0; i < 5; i++)
            {
                Vector3 p = e + (i == 0 ? Vector3.zero : Quaternion.Euler(0f, i * 90f, 0f) * Vector3.forward * 0.3f);
                if (scene.InFloor(p)) return true;
            }
            return false;
        }

        /// <summary>Did the scan change by more than anchor drift (5 cm, 2 deg)? Rounding a signature flickers at the edges.</summary>
        bool Moved(BloomScene scene, bool useVolumes)
        {
            int n = scene.FloorPolygon.Count + scene.Walls.Count + (useVolumes ? scene.Volumes.Count * 3 : 0);
            if (n != _snap.Count) return true;
            int i = 0;
            foreach (var f in scene.FloorPolygon) if (Far(_snap[i++], new Vector3(f.x, 0f, f.y))) return true;
            foreach (var w in scene.Walls) if (Far(_snap[i++], w.center)) return true;
            if (useVolumes)
                foreach (var v in scene.Volumes)
                {
                    if (Far(_snap[i++], v.center) || Far(_snap[i++], v.half) || Far(_snap[i++], v.rotation * Vector3.forward * 1.5f)) return true;
                }
            return false;

            static bool Far(Vector3 a, Vector3 b) => (a - b).sqrMagnitude > 0.05f * 0.05f;
        }

        void Snapshot(BloomScene scene, bool useVolumes)
        {
            _snap.Clear();
            foreach (var f in scene.FloorPolygon) _snap.Add(new Vector3(f.x, 0f, f.y));
            foreach (var w in scene.Walls) _snap.Add(w.center);
            if (useVolumes)
                foreach (var v in scene.Volumes) { _snap.Add(v.center); _snap.Add(v.half); _snap.Add(v.rotation * Vector3.forward * 1.5f); }
        }

        // ------------------------------------------------------------------ depth fallback
        bool WantDepth()
        {
            if (!RoomAwareTuning.Depth || !InPre) return false;
            if (_desktop) return RoomAwareTuning.Source == RoomAwareTuning.SourceMode.Depth;
            if (RoomAwareTuning.Source == RoomAwareTuning.SourceMode.Scan) return false;
            if (RoomAwareTuning.Source == RoomAwareTuning.SourceMode.Auto && Boxes.Count > 0) return false; // the scan knows the furniture
            var rig = ArRig.Instance;
            return rig != null && rig.Raycaster != null && rig.Raycaster.IsSupported && rig.Raycaster.Enabled;
        }

        static readonly float[] FanYaw = { 0f, -12f, 12f, -24f, 24f, -36f, 36f, -48f, 48f, -60f, 60f };
        static readonly float[] FanPitch = { 4f, 14f, 24f, 34f, 46f, 58f };

        /// <summary>Casts `count` rays of the fan (continuing where the last call stopped).</summary>
        public void SampleDepth(int count)
        {
            var cam = Camera.main;
            if (cam == null) return;
            Transform head = cam.transform;
            Vector3 eye = head.position;
            Vector3 fwd = head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) return;
            fwd.Normalize();
            if (!_depthLogged)
            {
                _depthLogged = true;
                Debug.Log("[VBS][Room] depth sampling on (" + (_desktop ? "desktop: capture room colliders" : ArRig.Instance.Raycaster.Name) + ")");
            }
            int total = FanYaw.Length * FanPitch.Length;
            Vector3 handL = default, handR = default;
            bool hl = !_desktop && GameHands.TryGetIndexTip(GameHand.Left, out handL);
            bool hr = !_desktop && GameHands.TryGetIndexTip(GameHand.Right, out handR);
            for (int n = 0; n < count; n++)
            {
                int i = _fanIndex++ % total;
                float yaw = FanYaw[i % FanYaw.Length], pitch = FanPitch[i / FanYaw.Length];
                Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * (Quaternion.AngleAxis(pitch, Vector3.Cross(Vector3.up, fwd).normalized) * fwd);
                if (!Cast(new Ray(eye, dir), 5f, out Vector3 p, out Vector3 nrm)) continue;
                Carve(eye, dir, Vector3.Distance(eye, p));
                Vector3 flat = p - eye;
                flat.y = 0f;
                if (flat.magnitude < DepthIgnoreNear) continue;
                if ((hl && (p - handL).sqrMagnitude < HandIgnore * HandIgnore) || (hr && (p - handR).sqrMagnitude < HandIgnore * HandIgnore)) continue;
                float up = p.y - FloorY;
                if (up < DepthMinUp || up > DepthMaxUp) continue;
                if (up < 0.3f && nrm.y > 0.8f) continue; // the floor seen at a grazing angle (depth noise far out)
                _rayHits++;
                var k = Key(p);
                if (_direct.Contains(k)) continue;
                if (!_desktop)
                {
                    // confirmed only by a second hit at least 1 s later (a person walking past is gone by then)
                    float now = Time.time;
                    if (!_firstHit.TryGetValue(k, out float first) || now - first > 8f) { _firstHit[k] = now; continue; }
                    if (now - first < 1f) continue;
                    _firstHit.Remove(k);
                }
                _direct.Add(k);
                int floorJ = Mathf.FloorToInt((FloorY + 0.001f) / Cell);
                for (int j = k.y; j >= floorJ; j--) _cells.Add(new Vector3Int(k.x, j, k.z));
            }
        }

        /// <summary>A ray that reached `dist` went through every cell before it: whatever stood there has left. A column
        /// loses its fill-down cells too once none of its confirmed hits remain.</summary>
        void Carve(Vector3 eye, Vector3 dir, float dist)
        {
            if (_cells.Count == 0 && _firstHit.Count == 0) return;
            for (float t = DepthIgnoreNear; t < dist - 0.3f; t += Cell * 0.5f)
            {
                var k = Key(eye + dir * t);
                _firstHit.Remove(k);
                if (!_cells.Remove(k)) continue;
                _carved++;
                if (!_direct.Remove(k)) continue;
                int floorJ = Mathf.FloorToInt((FloorY + 0.001f) / Cell), topJ = Mathf.FloorToInt((FloorY + DepthMaxUp) / Cell);
                bool any = false;
                for (int j = floorJ; j <= topJ && !any; j++) any = _direct.Contains(new Vector3Int(k.x, j, k.z));
                if (!any)
                    for (int j = floorJ; j <= topJ; j++) _cells.Remove(new Vector3Int(k.x, j, k.z));
            }
        }

        bool Cast(Ray ray, float max, out Vector3 point, out Vector3 normal)
        {
            point = default;
            normal = Vector3.zero;
            _rays++;
            if (_desktop)
            {
                int n = Physics.RaycastNonAlloc(ray, _phys, max, ~0, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    var t = _phys[i].collider.transform;
                    if (t.parent == null || t.parent.name != "CaptureRoom" || _phys[i].distance >= best) continue;
                    best = _phys[i].distance;
                    point = _phys[i].point;
                    normal = _phys[i].normal;
                }
                return best < float.MaxValue;
            }
            var rc = ArRig.Instance != null ? ArRig.Instance.Raycaster : null;
            if (rc == null || !rc.Enabled) return false;
            if (!rc.Raycast(ray, max, out var hit) || hit.status != ArHitStatus.Hit) return false;
            point = hit.point;
            normal = hit.normalConfidence > 0f ? hit.normal : Vector3.zero;
            return true;
        }

        static Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell), Mathf.FloorToInt(p.z / Cell));

        /// <summary>Desktop (or a forced refresh): sweep the whole fan now, so a placement right after start sees it.</summary>
        public static void EnsureSampled()
        {
            var k = Instance;
            if (k == null || !RoomAwareTuning.On || !k._desktop || !k.WantDepth() || k._rays > 0) return;
            k.SampleDepth(FanYaw.Length * FanPitch.Length);
            k._cellsAtLastChange = k._cells.Count;
            k.Bump("depth sweep: " + k._cells.Count + " occupied cells");
        }

        // ------------------------------------------------------------------ queries
        /// <summary>How deep the oriented box sits inside the deepest keep-out (0 = clear). `what` names it.</summary>
        public float Penetration(Obb o, float pad, out string what)
        {
            what = null;
            float worst = 0f;
            for (int i = 0; i < Boxes.Count; i++)
            {
                float d = Overlap(Boxes[i], o, pad);
                if (d > worst) { worst = d; what = Boxes[i].label; }
            }
            if (_cells.Count > 0)
            {
                var ab = o.Aabb;
                Vector3 mn = ab.min - Vector3.one * pad, mx = ab.max + Vector3.one * pad;
                Vector3Int a = Key(mn), z = Key(mx);
                var cell = new KeepBox { rot = Quaternion.identity, half = Vector3.one * (Cell * 0.5f) };
                for (int x = a.x; x <= z.x; x++)
                for (int y = a.y; y <= z.y; y++)
                for (int w = a.z; w <= z.z; w++)
                    if (_cells.Contains(new Vector3Int(x, y, w)))
                    {
                        cell.c = new Vector3((x + 0.5f) * Cell, (y + 0.5f) * Cell, (w + 0.5f) * Cell);
                        float d = Overlap(cell, o, pad);
                        if (d > worst) { worst = d; what = "depth"; }
                    }
            }
            for (int i = 0; i < Walls.Count; i++)
            {
                float d = BehindWall(Walls[i], o);
                if (d > worst) { worst = d; what = "wall"; }
            }
            if (_floor.Count >= 3 && o.c.y - o.half.y < FloorY + 0.35f)
            {
                // a floor-standing thing whose foot is outside the floor outline (through a wall the scan knows)
                if (!InFloor(new Vector3(o.c.x, 0f, o.c.z)) && 0.1f > worst) { worst = 0.1f; what = "outside the floor"; }
            }
            return worst;
        }

        public float Penetration(Bounds b, float pad, out string what) => Penetration(Obb.FromBounds(b), pad, out what);
        public float Penetration(Bounds b, float pad = 0f) => Penetration(Obb.FromBounds(b), pad, out _);
        public float Penetration(Obb o, float pad = 0f) => Penetration(o, pad, out _);

        public bool IsFree(Bounds b, float pad = 0.03f) => Penetration(Obb.FromBounds(b), pad, out _) <= 0f;
        public bool IsFree(Obb o, float pad = 0.03f) => Penetration(o, pad, out _) <= 0f;

        public static bool Free(Bounds b, float pad = 0.03f) => !Ready || Instance.IsFree(b, pad);
        public static bool Free(Obb o, float pad = 0.03f) => !Ready || Instance.IsFree(o, pad);

        /// <summary>The overlap depth of a yaw-only keep-out and a yaw-only box (separating axes in plan, plus height).</summary>
        public static float Overlap(KeepBox k, Obb o, float pad)
        {
            float oy = k.half.y + o.half.y + pad - Mathf.Abs(o.c.y - k.c.y);
            if (oy <= 0f) return oy;
            Vector3 kr = k.rot * Vector3.right, kf = k.rot * Vector3.forward, oR = o.Right, oF = o.Fwd;
            Vector3 d = o.c - k.c;
            float best = oy;
            for (int i = 0; i < 4; i++)
            {
                Vector3 n = i == 0 ? kr : i == 1 ? kf : i == 2 ? oR : oF;
                n.y = 0f;
                if (n.sqrMagnitude < 1e-8f) continue;
                n.Normalize();
                float rk = Mathf.Abs(Vector3.Dot(kr, n)) * k.half.x + Mathf.Abs(Vector3.Dot(kf, n)) * k.half.z;
                float ro = Mathf.Abs(Vector3.Dot(oR, n)) * o.half.x + Mathf.Abs(Vector3.Dot(oF, n)) * o.half.z;
                float ov = rk + ro + pad - Mathf.Abs(Vector3.Dot(d, n));
                if (ov <= 0f) return ov;
                best = Mathf.Min(best, ov);
            }
            return best;
        }

        /// <summary>The same for a world AABB (RoomAudit).</summary>
        public static float Overlap(KeepBox k, Bounds b, float pad) => Overlap(k, Obb.FromBounds(b), pad);

        /// <summary>How far the box's real corners reach behind a wall plane (inside the wall's width); 0 if not.</summary>
        static float BehindWall(BloomRect w, Obb o)
        {
            if (o.c.y + o.half.y < w.center.y - w.half.y || o.c.y - o.half.y > w.center.y + w.half.y) return 0f;
            float worst = 0f;
            Vector3 r = o.Right * o.half.x, f = o.Fwd * o.half.z;
            for (int i = 0; i < 4; i++)
            {
                Vector3 p = o.c + ((i & 1) == 0 ? -r : r) + ((i & 2) == 0 ? -f : f);
                Vector3 d = p - w.center;
                d.y = 0f;
                float behind = -Vector3.Dot(d, w.normal);
                if (behind <= 0.01f || behind > 1.0f) continue;              // in front, or well past (another room)
                if (Mathf.Abs(Vector3.Dot(d, w.right)) > w.half.x + 0.05f) continue;
                worst = Mathf.Max(worst, behind);
            }
            return worst;
        }

        public bool InFloor(Vector3 p)
        {
            int n = _floor.Count;
            if (n < 3) return true;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = _floor[i], c = _floor[j];
                if ((a.y > p.z) != (c.y > p.z) && p.x < (c.x - a.x) * (p.z - a.y) / (c.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        static readonly Vector3[] Dirs = MakeDirs(16);

        static Vector3[] MakeDirs(int n)
        {
            var d = new Vector3[n];
            for (int i = 0; i < n; i++) d[i] = Quaternion.Euler(0f, i * 360f / n, 0f) * Vector3.forward;
            return d;
        }

        /// <summary>The smallest flat move (rings 5 cm apart, up to maxDist) that takes `b` out of every keep-out; among
        /// equal rings the one ending nearest `prefer`. allowUp also tries lifting it (up to 0.5 m). `ok` can veto a spot
        /// (the hive, the player). False: none.</summary>
        public bool NearestFree(Obb b, Vector3 prefer, float maxDist, out Vector3 move, float pad = 0.03f, bool allowUp = false, Func<Obb, bool> ok = null)
        {
            move = Vector3.zero;
            if (IsFree(b, pad) && (ok == null || ok(b))) return true;
            for (float r = 0.05f; r <= maxDist + 1e-4f; r += 0.05f)
            {
                float best = float.MaxValue;
                bool any = false;
                for (int i = 0; i < Dirs.Length; i++)
                {
                    Vector3 m = Dirs[i] * r;
                    var nb = b.Moved(m);
                    if (!IsFree(nb, pad) || (ok != null && !ok(nb))) continue;
                    float d = Flat(nb.c - prefer).sqrMagnitude;
                    if (d < best) { best = d; move = m; any = true; }
                }
                if (any) return true;
                if (allowUp && r <= 0.5f)
                {
                    var ub = b.Moved(Vector3.up * r);
                    if (IsFree(ub, pad) && (ok == null || ok(ub))) { move = Vector3.up * r; return true; }
                }
            }
            return false;
        }

        public bool NearestFree(Bounds b, Vector3 prefer, float maxDist, out Vector3 move, float pad = 0.03f, bool allowUp = false)
            => NearestFree(Obb.FromBounds(b), prefer, maxDist, out move, pad, allowUp);

        /// <summary>A free floor spot for a footprint (half x/z, height h) near `near`, within maxDist.</summary>
        public bool FreeFloorSpot(Vector2 halfFootprint, float height, Vector3 near, float maxDist, out Vector3 spot)
        {
            var b = new Bounds(new Vector3(near.x, FloorY + height * 0.5f + 0.02f, near.z), new Vector3(halfFootprint.x * 2f, height, halfFootprint.y * 2f));
            bool ok = NearestFree(b, near, maxDist, out var move);
            spot = new Vector3(near.x, FloorY, near.z) + move;
            return ok;
        }

        /// <summary>Does a real keep-out (scan furniture or depth cells, not walls) stand between a and b? (the depth
        /// occlusion would hide b from a).</summary>
        public bool SightBlocked(Vector3 a, Vector3 b, out string what, float shrink = 0.04f)
        {
            what = null;
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-3f) return false;
            for (int i = 0; i < Boxes.Count; i++)
            {
                var k = Boxes[i];
                var inv = Quaternion.Inverse(k.rot);
                Vector3 o = inv * (a - k.c), v = inv * d;
                Vector3 h = k.half - Vector3.one * shrink;
                if (h.x <= 0f || h.y <= 0f || h.z <= 0f) continue;
                float t0 = 0f, t1 = 1f;
                bool hit = true;
                for (int ax = 0; ax < 3 && hit; ax++)
                {
                    float oo = o[ax], vv = v[ax], hh = h[ax];
                    if (Mathf.Abs(vv) < 1e-6f) { if (Mathf.Abs(oo) > hh) hit = false; continue; }
                    float ta = (-hh - oo) / vv, tb = (hh - oo) / vv;
                    if (ta > tb) (ta, tb) = (tb, ta);
                    t0 = Mathf.Max(t0, ta);
                    t1 = Mathf.Min(t1, tb);
                    if (t0 > t1) hit = false;
                }
                if (hit && t1 > 0.02f && t0 < 0.98f) { what = k.label; return true; }
            }
            if (_cells.Count > 0)
            {
                int steps = Mathf.CeilToInt(len / 0.1f);
                for (int s = 1; s < steps; s++)
                    if (_cells.Contains(Key(a + d * (s / (float)steps)))) { what = "depth"; return true; }
            }
            return false;
        }

        // ------------------------------------------------------------------ helpers for the lanes
        const float PushReach = 0.7f;

        /// <summary>BeeDelivery hook: a set-down / park pose that would put the item inside real furniture moves the shortest
        /// flat way out (up to 0.7 m, toward the player when it can, never into the hive). A delivery home or into a socket
        /// on the rig (targetParent under the rig anchor) is left alone: the rig placement keeps those clear, and an item
        /// pushed off its socket would stay off it. True if it moved.</summary>
        public static bool PushOut(Transform item, Transform targetParent, ref Vector3 pos, Quaternion rot, DeliveryItem di)
        {
            if (!Ready || item == null || di == null) return false;
            var anchor = MeadowLayout.Anchor;
            if (targetParent != null && anchor != null && (targetParent == anchor || targetParent.IsChildOf(anchor))) return false;
            return Push(item, ref pos, rot, di, "would land inside real furniture");
        }

        /// <summary>T-0 hook: the crash pose of the rocket, out of real furniture (and still clear of the hive).</summary>
        public static bool PushRocket(Transform rocket, ref Vector3 pos, Quaternion rot, DeliveryItem body)
        {
            if (!Ready || rocket == null || body == null) return false;
            return Push(rocket, ref pos, rot, body, "crash pose was inside real furniture");
        }

        static bool Push(Transform item, ref Vector3 pos, Quaternion rot, DeliveryItem di, string note)
        {
            if (!di.measured) di.Measure();
            var b = Obb.FromBounds(WorldBox(di.localBounds, Matrix4x4.TRS(pos, rot, item.lossyScale)));
            var k = Instance;
            if (k.IsFree(b, 0.02f)) return false;
            var cam = Camera.main;
            Vector3 prefer = cam != null ? cam.transform.position : b.c;
            var hive = FindAnyObjectByType<HiveBody>();
            Bounds hb = default;
            bool haveHive = hive != null && !item.IsChildOf(hive.transform) && MeshBounds(hive.transform, out hb, true);
            Vector3 eye = prefer;
            Func<Obb, bool> ok = o =>
            {
                var ab = o.Aabb;
                if (haveHive && ab.Intersects(hb)) return false;
                ab.Expand(new Vector3(0.3f, 10f, 0.3f));
                return cam == null || !ab.Contains(eye); // not on the player's feet
            };
            if (!k.NearestFree(b, prefer, PushReach, out var move, 0.02f, false, ok)) return false;
            pos += move;
            Debug.Log("[VBS][Room] " + item.name + " " + note + ": moved " + (move.magnitude * 100f).ToString("0", C) + " cm");
            return true;
        }

        static readonly Dictionary<int, Vector3> _kept = new Dictionary<int, Vector3>();

        /// <summary>NewProps hook (floor rig only): keeps a prop (localHome under parent) out of real furniture by moving it
        /// up to maxDist, never hiding it. Returns the local position to use. Logs only when its spot changes.</summary>
        public static Vector3 KeepClear(Transform t, Vector3 localHome, Transform parent, float maxDist)
        {
            if (!Ready || t == null || parent == null || !MeadowLayout.Active) return localHome;
            if (!MeshBounds(t, out var bw)) return localHome;
            Vector3 worldHome = parent.TransformPoint(localHome);
            bw.center += worldHome - t.position;
            var b = Obb.FromBounds(bw);
            var k = Instance;
            Vector3 move = Vector3.zero;
            if (!k.IsFree(b, 0.02f))
            {
                var cam = Camera.main;
                Vector3 prefer = cam != null ? cam.transform.position : b.c;
                if (!k.NearestFree(b, prefer, maxDist, out move, 0.02f)) move = Vector3.zero;
            }
            int id = t.GetInstanceID();
            if (!_kept.TryGetValue(id, out var was) || (was - move).sqrMagnitude > 0.02f * 0.02f)
            {
                _kept[id] = move;
                if (move.sqrMagnitude > 1e-6f)
                    Debug.Log("[VBS][Room] " + t.name + " stood in real furniture: relocated " + (move.magnitude * 100f).ToString("0", C) + " cm (not hidden)");
            }
            if (move.sqrMagnitude < 1e-8f) return localHome;
            Vector3 l = parent.InverseTransformPoint(worldHome + move);
            l.y = localHome.y;
            return l;
        }

        public static Bounds WorldBox(Bounds local, Matrix4x4 m)
        {
            Bounds acc = default;
            for (int i = 0; i < 8; i++)
            {
                var c = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var w = m.MultiplyPoint3x4(c);
                if (i == 0) acc = new Bounds(w, Vector3.zero);
                else acc.Encapsulate(w);
            }
            return acc;
        }

        static readonly List<MeshFilter> _mf = new List<MeshFilter>(64);
        static readonly List<SkinnedMeshRenderer> _sk = new List<SkinnedMeshRenderer>(8);
        static readonly string[] SkipBits = { "Glow", "Shadow", "Halo", "Puff", "Smoke", "Spark", "Text", "Caption", "Label", "Shaft", "Particle", "Mote", "Dust" };

        /// <summary>World bounds from the meshes under t (works while hidden, unlike Renderer.bounds). Skips glows, text and
        /// effects, and any single mesh over 4 m.</summary>
        public static bool MeshBounds(Transform t, out Bounds b, bool activeOnly = false)
        {
            b = default;
            bool any = false;
            t.GetComponentsInChildren(!activeOnly, _mf);
            foreach (var mf in _mf)
            {
                if (mf.sharedMesh == null) continue;
                var r = mf.GetComponent<Renderer>();
                if (r == null || (activeOnly && (!r.enabled || r.forceRenderingOff || !mf.gameObject.activeInHierarchy))) continue;
                if (Skipped(mf.transform, t)) continue;
                var wb = WorldBox(mf.sharedMesh.bounds, mf.transform.localToWorldMatrix);
                if (wb.size.x > 4f || wb.size.y > 4f || wb.size.z > 4f) continue;
                if (!any) { b = wb; any = true; } else b.Encapsulate(wb);
            }
            t.GetComponentsInChildren(!activeOnly, _sk);
            foreach (var sk in _sk)
            {
                if (sk.sharedMesh == null || Skipped(sk.transform, t)) continue;
                if (activeOnly && (!sk.enabled || !sk.gameObject.activeInHierarchy)) continue;
                var wb = WorldBox(sk.localBounds, (sk.rootBone != null ? sk.rootBone : sk.transform).localToWorldMatrix);
                if (wb.size.x > 4f || wb.size.y > 4f || wb.size.z > 4f) continue;
                if (!any) { b = wb; any = true; } else b.Encapsulate(wb);
            }
            _mf.Clear();
            _sk.Clear();
            return any;
        }

        /// <summary>The meshes under t as one box in `frame`'s axes (yaw-only frame: the rig anchor, a row).</summary>
        public static bool MeshObb(Transform t, Transform frame, out Obb o, bool activeOnly = false)
        {
            o = default;
            if (frame == null) { if (!MeshBounds(t, out var wb, activeOnly)) return false; o = Obb.FromBounds(wb); return true; }
            Matrix4x4 toFrame = frame.worldToLocalMatrix;
            Bounds b = default;
            bool any = false;
            t.GetComponentsInChildren(!activeOnly, _mf);
            foreach (var mf in _mf)
            {
                if (mf.sharedMesh == null) continue;
                var r = mf.GetComponent<Renderer>();
                if (r == null || (activeOnly && (!r.enabled || r.forceRenderingOff || !mf.gameObject.activeInHierarchy))) continue;
                if (Skipped(mf.transform, t)) continue;
                var lb = WorldBox(mf.sharedMesh.bounds, toFrame * mf.transform.localToWorldMatrix);
                var ws = Vector3.Scale(lb.size, frame.lossyScale);
                if (ws.x > 4f || ws.y > 4f || ws.z > 4f) continue;
                if (!any) { b = lb; any = true; } else b.Encapsulate(lb);
            }
            _mf.Clear();
            if (!any) return false;
            o = Obb.From(b, frame.localToWorldMatrix);
            return true;
        }

        static bool Skipped(Transform t, Transform stop)
        {
            for (var p = t; p != null; p = p.parent)
            {
                string n = p.name;
                for (int i = 0; i < SkipBits.Length; i++)
                    if (n.IndexOf(SkipBits[i], StringComparison.Ordinal) >= 0) return true;
                if (p == stop) break;
            }
            return false;
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Source).Append(", ").Append(Boxes.Count).Append(" furniture, ").Append(Walls.Count).Append(" walls, floor ")
              .Append(_floor.Count).Append(" pts, ").Append(_cells.Count).Append(" depth cells");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ Beeswax obstacles
        void UpdateProxies()
        {
            _proxyVersion = Version;
            if (_proxyRoot == null)
            {
                _proxyRoot = new GameObject("RoomKeepOut proxies (Beeswax obstacles)");
                _proxyRoot.transform.SetParent(transform, false);
            }
            if (_cube == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _cube = tmp.GetComponent<MeshFilter>().sharedMesh;
                Destroy(tmp);
            }
            var boxes = new List<KeepBox>(Boxes);
            if (_cells.Count > 0) AddCellColumns(boxes, 40 - Mathf.Min(40, boxes.Count));
            int furniture = boxes.Count;
            // the walls as 1 m slabs behind the plane (he flew into them in the desk room): his path sweep and push-out see
            // them as oriented boxes, but they are never part of a detour box (RoomWallProxy; an axis box round a wall or
            // the whole room would leave only detours outside the room)
            foreach (var w in Walls)
            {
                if (w.half.x < 0.05f || w.half.y < 0.05f || w.normal.sqrMagnitude < 0.5f) continue;
                boxes.Add(new KeepBox
                {
                    c = w.center - w.normal * 0.5f, rot = Quaternion.LookRotation(w.normal, Vector3.up),
                    half = new Vector3(w.half.x, w.half.y, 0.5f), label = "wall",
                });
            }
            while (_proxies.Count < boxes.Count) _proxies.Add(null);
            for (int i = 0; i < boxes.Count; i++)
            {
                bool wall = i >= furniture;
                var go = _proxies[i];
                if (go != null && (go.GetComponent<RoomWallProxy>() != null) != wall) { Destroy(go); go = null; }
                if (go != null) continue;
                go = new GameObject(wall ? "RoomWall" : "RoomKeepOut");
                go.transform.SetParent(_proxyRoot.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = _cube;
                var mr = go.AddComponent<MeshRenderer>();
                mr.forceRenderingOff = true; // never drawn: BeeswaxBody reads the mesh box
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (wall) go.AddComponent<RoomWallProxy>(); else go.AddComponent<RoomKeepOutProxy>();
                _proxies[i] = go;
            }
            BeeswaxBody.MarkDirty();
            for (int i = 0; i < _proxies.Count; i++)
            {
                bool on = i < boxes.Count;
                if (_proxies[i].activeSelf != on) _proxies[i].SetActive(on);
                if (!on) continue;
                var t = _proxies[i].transform;
                t.SetPositionAndRotation(boxes[i].c, boxes[i].rot);
                t.localScale = boxes[i].half * 2f;
            }
        }

        /// <summary>Depth cells as per-column boxes (floor to the top cell), the nearest the rig first, at most `max`.</summary>
        void AddCellColumns(List<KeepBox> into, int max)
        {
            if (max <= 0) return;
            var tops = new Dictionary<Vector2Int, int>();
            foreach (var c in _cells)
            {
                var k2 = new Vector2Int(c.x, c.z);
                if (!tops.TryGetValue(k2, out int top) || c.y > top) tops[k2] = c.y;
            }
            var anchor = MeadowLayout.Anchor;
            Vector3 centre = anchor != null ? anchor.position : Vector3.zero;
            var list = new List<KeyValuePair<Vector2Int, int>>(tops);
            list.Sort((a, b) =>
            {
                float da = (new Vector2((a.Key.x + 0.5f) * Cell - centre.x, (a.Key.y + 0.5f) * Cell - centre.z)).sqrMagnitude;
                float db = (new Vector2((b.Key.x + 0.5f) * Cell - centre.x, (b.Key.y + 0.5f) * Cell - centre.z)).sqrMagnitude;
                return da.CompareTo(db);
            });
            for (int i = 0; i < list.Count && i < max; i++)
            {
                float top = (list[i].Value + 1) * Cell;
                float h = Mathf.Max(0.05f, top - FloorY);
                into.Add(new KeepBox
                {
                    c = new Vector3((list[i].Key.x + 0.5f) * Cell, FloorY + h * 0.5f, (list[i].Key.y + 0.5f) * Cell),
                    rot = Quaternion.identity, half = new Vector3(Cell * 0.5f, h * 0.5f, Cell * 0.5f), label = "depth", depth = true,
                });
            }
        }
    }

    /// <summary>Marks the invisible keep-out boxes BeeswaxBody avoids (one per scanned piece of furniture / depth column).</summary>
    public sealed class RoomKeepOutProxy : MonoBehaviour { }

    /// <summary>Marks a wall slab: BeeswaxBody avoids it but never folds it into a detour box.</summary>
    public sealed class RoomWallProxy : MonoBehaviour { }
}
