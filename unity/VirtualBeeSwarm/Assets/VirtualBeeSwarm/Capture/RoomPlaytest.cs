using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using VirtualBeeSwarm.AR;
using VirtualBeeSwarm.Bloom;
using VirtualBeeSwarm.HandsOn;

namespace VirtualBeeSwarm.Capture
{
    /// <summary>The same box is used for the recorded furniture and the fake room scan.</summary>
    internal readonly struct RoomPlaytestBox
    {
        public readonly string Name;
        public readonly Vector3 Center, Size;
        public readonly Quaternion Rotation;
        public readonly bool Pillar;

        public RoomPlaytestBox(string name, float x, float y, float z, float width, float height, float depth,
            float yaw = 0f, bool pillar = false)
        {
            Name = name;
            Center = new Vector3(x, y, z);
            Size = new Vector3(width, height, depth);
            Rotation = Quaternion.Euler(0f, yaw, 0f);
            Pillar = pillar;
        }
    }

    internal sealed class RoomPlaytestConfig
    {
        public readonly string Id, Name;
        public readonly float Width, Depth, Ceiling, WallZ;
        public readonly Vector3 Eye, Look;
        public readonly RoomPlaytestBox[] Furniture;

        public RoomPlaytestConfig(string id, string name, float width, float depth, float ceiling, float wallZ,
            Vector3 eye, Vector3 look, params RoomPlaytestBox[] furniture)
        {
            Id = id;
            Name = name;
            Width = width;
            Depth = depth;
            Ceiling = ceiling;
            WallZ = wallZ;
            Eye = eye;
            Look = look;
            Furniture = furniture;
        }
    }

    /// <summary>Opt-in scripted room for a generated desktop capture build.</summary>
    [DefaultExecutionOrder(-120)]
    public sealed class RoomPlaytest : MonoBehaviour
    {
        const string RoomEnv = "VBS_CAPTURE_ROOM";
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        readonly List<BoxCollider> _furniture = new List<BoxCollider>();
        readonly List<OrderTicket> _tickets = new List<OrderTicket>();
        readonly List<Renderer> _renderers = new List<Renderer>();
        readonly StringBuilder _line = new StringBuilder(448);
        readonly HashSet<string> _blockers = new HashSet<string>(StringComparer.Ordinal);
        Transform _clipboard, _hive, _window, _rocket, _dish, _title;
        OrderBoard _board;
        HoneyLever _lever;
        float _sampleAt = -1f;
        GameBeat _lastBeat = (GameBeat)(-1);

        internal RoomPlaytestConfig Config { get; private set; }
        internal string Error { get; private set; }
        internal bool ScanReady => MeadowField.Instance != null &&
                                   MeadowField.Instance.CurrentScene()?.Name == "capture room " + Config.Id;

        void Awake()
        {
            string id = Environment.GetEnvironmentVariable(RoomEnv)?.Trim();
            if (string.IsNullOrEmpty(id)) return;
            foreach (var fixture in RoomPlaytestFixtures.All)
                if (string.Equals(fixture.Id, id, StringComparison.OrdinalIgnoreCase) ||
                    fixture.Id.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase))
                {
                    Config = fixture;
                    break;
                }
            if (Config == null)
            {
                Error = RoomEnv + " has an unknown fixture: " + id;
                return;
            }
            FakeRoomSource.CaptureOverride = ApplyRoom;
            BloomFakeRoom.CaptureOverride = FillScan;
            var desktop = FindAnyObjectByType<DesktopTestCamera>(FindObjectsInactive.Include);
            if (desktop != null)
                desktop.transform.SetPositionAndRotation(Config.Eye, Quaternion.LookRotation(Config.Look - Config.Eye, Vector3.up));
        }

        void OnDestroy()
        {
            if (Config == null) return;
            FakeRoomSource.CaptureOverride = null;
            BloomFakeRoom.CaptureOverride = null;
        }

        void ApplyRoom(RoomModel room)
        {
            room.Source = "capture room " + Config.Id;
            room.FloorY = 0f;
            room.CeilingY = Config.Ceiling;
            room.SetWall(new Vector3(0f, Config.Ceiling * 0.5f, Config.WallZ),
                Vector3.back, Config.Width * 0.5f, Config.WallZ - Config.Eye.z);
        }

        bool FillScan(RoomModel room, BloomScene scene)
        {
            scene.Clear("capture room " + Config.Id);
            float half = Config.Width * 0.5f;
            float back = Config.WallZ - Config.Depth;
            float midZ = Config.WallZ - Config.Depth * 0.5f;
            float midY = Config.Ceiling * 0.5f;
            scene.FloorPolygon.Add(new Vector2(-half, Config.WallZ));
            scene.FloorPolygon.Add(new Vector2(half, Config.WallZ));
            scene.FloorPolygon.Add(new Vector2(half, back));
            scene.FloorPolygon.Add(new Vector2(-half, back));
            AddWall(new Vector3(0f, midY, Config.WallZ), Vector3.back, Config.Width);
            AddWall(new Vector3(-half, midY, midZ), Vector3.right, Config.Depth);
            AddWall(new Vector3(half, midY, midZ), Vector3.left, Config.Depth);
            AddWall(new Vector3(0f, midY, back), Vector3.forward, Config.Width);
            foreach (var box in Config.Furniture)
                scene.Volumes.Add(new BloomBox { center = box.Center, rotation = box.Rotation, half = box.Size * 0.5f, pillar = box.Pillar, label = box.Name });
            if (!MeadowLayout.Active && room != null && room.HasTable)
            {
                var table = room.Table;
                scene.Volumes.Add(new BloomBox
                {
                    center = table.center - Vector3.up * (table.Y * 0.5f), rotation = table.rotation,
                    half = new Vector3(table.half.x, table.Y * 0.5f, table.half.y),
                });
            }
            return true;

            void AddWall(Vector3 center, Vector3 normal, float length)
            {
                scene.Walls.Add(new BloomRect
                {
                    center = center, normal = normal, right = Vector3.Cross(Vector3.up, normal), up = Vector3.up,
                    half = new Vector2(length * 0.5f, midY),
                });
            }
        }

        internal void AddFurniture(BoxCollider box)
        {
            if (box != null) _furniture.Add(box);
        }

        internal void PlaceAndDescribe()
        {
            // Desktop HivePlacement does not auto-place like a tracked headset. Exercise the same stock floor-fit.
            Physics.SyncTransforms();
            var placement = FindAnyObjectByType<HivePlacement>();
            if (placement == null) throw new InvalidOperationException("No HivePlacement in the capture scene");
            placement.PlaceInFrontOfCamera();
            _clipboard = FindAnyObjectByType<SignHereClipboard>(FindObjectsInactive.Include)?.transform;
            _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include)?.transform;
            var window = FindAnyObjectByType<FutureWindow>(FindObjectsInactive.Include)?.transform;
            _window = window != null ? window.Find("Portal/FallbackPane") ?? window : null;
            Debug.Log("[VBSRoomTest] room=" + Config.Id + " name=" + Config.Name + " size=" +
                      Vec(new Vector3(Config.Width, Config.Ceiling, Config.Depth)) + " eye=" + Vec(Config.Eye) +
                      " look=" + Vec(Config.Look) + " furniture=" + Config.Furniture.Length);
            foreach (var b in Config.Furniture)
                Debug.Log("[VBSRoomTest] box=" + b.Name + " centre=" + Vec(b.Center) + " size=" + Vec(b.Size) +
                          " yaw=" + F(b.Rotation.eulerAngles.y));

            var anchor = MeadowLayout.Anchor;
            if (anchor == null) return;
            _rocket = anchor.Find("HONEYMOON-1");
            int outside = 0;
            _blockers.Clear();
            for (int ix = 0; ix < 7; ix++)
            for (int iz = 0; iz < 5; iz++)
            {
                Vector3 p = anchor.TransformPoint(new Vector3(-0.5f + ix / 6f, 0f, -0.25f + iz * 0.7f / 4f));
                if (Mathf.Abs(p.x) > Config.Width * 0.5f || p.z > Config.WallZ || p.z < Config.WallZ - Config.Depth) outside++;
                for (int iy = 0; iy < 4; iy++)
                {
                    p.y = 0.1f + iy * 0.4f;
                    var box = InsideFurniture(p);
                    if (box != null) _blockers.Add(box.name);
                }
            }
            Debug.Log("[VBSRoomTest] placement=" + Vec(anchor.position) + " yaw=" + F(anchor.eulerAngles.y) +
                      " fit=" + MeadowLayout.LastFit.Replace(' ', '_') + " floorOutside=" + outside + "/35 volumeHits=" +
                      (_blockers.Count > 0 ? string.Join(",", _blockers) : "clear") +
                      " eyeVolume=" + (InsideFurniture(Config.Eye)?.name ?? "clear"));
        }

        internal void Sample(Camera camera, GameBeat beat, bool clipboardReady)
        {
            if (camera == null || (beat == _lastBeat && Time.time < _sampleAt)) return;
            _lastBeat = beat;
            _sampleAt = Time.time + 1.25f;
            _line.Clear().Append("[VBSRoomTest] beat=").Append(beat).Append(" t=").Append(F(Time.time));
            if (beat == GameBeat.Pre)
            {
                if (_title == null) _title = GameObject.Find("TitleLogo")?.transform;
                Marker(camera, "title", _title, false);
                if (clipboardReady) Marker(camera, "clipboard", _clipboard, true);
            }
            else
            {
                Marker(camera, "hive", _hive, true);
                Marker(camera, "window", _window, false);
                Marker(camera, "rocket", _rocket, false);
            }
            if (beat >= GameBeat.Smoker && beat <= GameBeat.Heat)
            {
                if (_board == null) _board = FindAnyObjectByType<OrderBoard>(FindObjectsInactive.Include);
                if (_lever == null) _lever = FindAnyObjectByType<HoneyLever>(FindObjectsInactive.Include);
                if (_board != null && _board.Visible)
                {
                    _board.GetComponentsInChildren(false, _tickets);
                    foreach (var ticket in _tickets) Marker(camera, "ticket-" + ticket.Choice, ticket.transform, true);
                }
                Marker(camera, "lever", _lever?.transform, true);
                if (beat == GameBeat.Heat)
                {
                    if (_dish == null) _dish = GameObject.Find("PebbleDish")?.transform;
                    Marker(camera, "dish", _dish, true);
                }
            }
            if (beat == GameBeat.T0) Marker(camera, "lever", _lever?.transform, true);
            Debug.Log(_line.ToString());
        }

        void Marker(Camera camera, string label, Transform target, bool reachable)
        {
            if (target == null || !target.gameObject.activeInHierarchy) return;
            target.GetComponentsInChildren(false, _renderers);
            Bounds bounds = default;
            bool any = false;
            foreach (var r in _renderers)
            {
                if (!r.enabled || r.forceRenderingOff) continue;
                if (any) bounds.Encapsulate(r.bounds);
                else { bounds = r.bounds; any = true; }
            }
            if (!any) return;
            Vector3 point = bounds.center;
            Vector3 origin = camera.transform.position;
            Vector3 to = point - origin;
            float distance = to.magnitude;
            Vector3 projected = camera.WorldToViewportPoint(point);
            bool framed = projected.z > camera.nearClipPlane &&
                          projected.x >= 0f && projected.x <= 1f && projected.y >= 0f && projected.y <= 1f;
            string blocked = "-";
            float nearest = distance - 0.02f;
            if (nearest > 0f)
            {
                var ray = new Ray(origin, to / distance);
                foreach (var box in _furniture)
                    if (box.Raycast(ray, out var hit, nearest))
                    {
                        nearest = hit.distance;
                        blocked = box.name;
                    }
            }
            _line.Append(' ').Append(label).Append('=').Append(framed ? "view" : "off").Append(',')
                 .Append(F(distance)).Append("m,").Append(blocked);
            if (reachable) _line.Append(",near=").Append(F(Vector3.Distance(origin, bounds.ClosestPoint(origin)))).Append('m');
            var inside = InsideFurniture(point);
            if (inside != null) _line.Append(",inside=").Append(inside.name);
        }

        BoxCollider InsideFurniture(Vector3 point)
        {
            foreach (var box in _furniture)
            {
                Vector3 p = box.transform.InverseTransformPoint(point) - box.center;
                Vector3 half = box.size * 0.5f;
                if (Mathf.Abs(p.x) < half.x && Mathf.Abs(p.y) < half.y && Mathf.Abs(p.z) < half.z) return box;
            }
            return null;
        }

        static string F(float value) => value.ToString("0.00", C);
        static string Vec(Vector3 value) => F(value.x) + "," + F(value.y) + "," + F(value.z);
    }
}
