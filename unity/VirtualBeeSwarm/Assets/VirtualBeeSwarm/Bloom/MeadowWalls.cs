using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>
    /// The walls of the meadow: vines, climbing flowers and leaves up every known wall all ride long, built with Bloom's
    /// own vine system (Vines.Build + Bloom.shader) on wall paths of their own: a root every ~0.3 m along each wall
    /// (room scan / desktop fake room walls, else the window wall the AR layer found; never a guessed wall). The growth
    /// follows the hive's health: while it is sick the green already reaches meadow.wallStart (~2.2 m), smaller and
    /// paler, every fix fills it out, and at the escape the walls are covered to the ceiling (RoomBloom then adds its own
    /// window-framing vines on top). Up to three meshes (Vines.MaxTris each), three draws. Doors and the window pane stay clear.
    /// </summary>
    public sealed class MeadowWalls
    {
        public static float Spacing => QuestPerf.On ? 0.24f : 0.17f; // meadow: walls covered wall to wall (headset: a bit sparser)
        const int MaxPerMesh = 40, MaxMeshes = 3;
        static readonly int BloomTId = Shader.PropertyToID("_BloomT");
        static readonly int ClockId = Shader.PropertyToID("_BloomClock");
        static readonly int ScaleId = Shader.PropertyToID("_BloomScale");
        static readonly int FeetId = Shader.PropertyToID("_FeetFold");
        static readonly int DataId = Shader.PropertyToID("_BloomData");

        readonly Material _mat;
        readonly MaterialPropertyBlock _mpb;
        readonly List<Mesh> _meshes = new List<Mesh>(2);
        float _bt = 0f;
        bool _grown;
        readonly List<VinePath> _spots = new List<VinePath>(96); // where the room bees land on the walls
        public float Climb { get; private set; }
        public int Tris { get; private set; }
        public int VineCount { get; private set; }
        public int Draws => _meshes.Count;
        public string Source { get; private set; } = "none";
        BloomRect _lastOpening;
        bool _hadOpening;
        bool _windowWasOpen;
        bool _hasWalls;
        FutureWindow _window;

        public MeadowWalls()
        {
            var template = Resources.Load<Material>(RoomBloom.MaterialResource);
            if (template == null)
            {
                var sh = Shader.Find("VirtualBeeSwarm/Bloom");
                if (sh != null) template = new Material(sh);
            }
            if (template == null) return;
            _mat = new Material(template) { name = "VBS_MeadowWalls (runtime)", enableInstancing = false };
            _mat.SetFloat(FeetId, 0f);
            _mpb = new MaterialPropertyBlock();
            _mpb.SetVector(DataId, new Vector4(0f, 0f, 0f, 4095f));
        }

        public bool Ok => _mat != null;

        /// <summary>Lay the vines out again (start, placement, room scan).</summary>
        public void Build(BloomScene scene, AR.RoomModel room, float floorY, float ceilingY, Vector3 hive, Vector3 player, int seed)
        {
            foreach (var m in _meshes) if (m != null) Object.Destroy(m);
            _meshes.Clear();
            _spots.Clear();
            Tris = 0;
            VineCount = 0;
            if (_mat == null) return;
            var rnd = new System.Random(seed);
            var walls = new List<BloomRect>();
            var openings = new List<BloomRect>();
            if (scene != null && scene.Walls.Count > 0)
            {
                walls.AddRange(scene.Walls);
                openings.AddRange(scene.Openings);
                Source = "scene walls (" + scene.Name + ", " + scene.Walls.Count + ")";
            }
            else if (room != null && room.HasWall)
            {
                var w = room.WindowWall;
                float h = ceilingY - floorY;
                walls.Add(new BloomRect
                {
                    center = new Vector3(w.point.x, floorY + h * 0.5f, w.point.z), right = w.right, up = Vector3.up, normal = w.normal,
                    half = new Vector2(Mathf.Max(1.2f, w.halfWidth + 1.2f), h * 0.5f),
                });
                Source = "the window wall";
            }
            else Source = "no wall known";

            _hasWalls = walls.Count > 0;
            var window = WindowRect(out bool hasWindow);
            System.Predicate<Vector3> sightline = null;
            if (MeadowLayout.Active && hasWindow)
                sightline = point => BlocksPortalView(window, player, point);
            var paths = new List<VinePath>(96);
            foreach (var wall in walls)
            {
                float top = Mathf.Min(wall.center.y + wall.half.y, ceilingY) - floorY;
                for (float x = -wall.half.x + 0.15f; x <= wall.half.x - 0.15f; x += Spacing)
                {
                    float jx = x + ((float)rnd.NextDouble() - 0.5f) * 0.12f;
                    Vector3 root = wall.center + wall.right * jx + wall.normal * 0.02f;
                    root.y = floorY;
                    if (InDoor(openings, root, floorY)) continue;
                    if (new Vector2(root.x - player.x, root.z - player.z).magnitude < 0.6f) continue;
                    float h = Mathf.Clamp(top - 0.08f - (float)rnd.NextDouble() * 0.3f, 0.4f, 2.9f); // up to the ceiling edge
                    paths.Add(new VinePath
                    {
                        kind = VineKind.Wall, root = root, normal = wall.normal, tangent = wall.right * (rnd.NextDouble() < 0.5 ? -1f : 1f),
                        height = h, center = root, rotation = Quaternion.identity, tier = "meadow walls",
                    });
                }
            }
            // nearest the hive first (the part the player looks at), so a cap drops the far corners
            paths.Sort((a, b) => Flat(a.root - hive).CompareTo(Flat(b.root - hive)));
            VineCount = Mathf.Min(paths.Count, MaxPerMesh * MaxMeshes);
            for (int i = 0; i < VineCount; i++) _spots.Add(paths[i]);
            for (int k = 0; k < MaxMeshes; k++)
            {
                int from = k * MaxPerMesh, n = Mathf.Min(MaxPerMesh, VineCount - from);
                if (n <= 0) break;
                var s = new BloomSurfaces
                {
                    FloorY = floorY, HasWindow = hasWindow, Window = window, WallSightlineKeepOut = sightline,
                    WaveOrigin = new Vector3(hive.x, floorY, hive.z), WaveStart = 100f,
                };
                for (int i = 0; i < n; i++) s.Vines.Add(paths[from + i]);
                var mesh = Vines.Build(s, 50f, seed + 17 * k, out int tris, out _);
                if (mesh != null) { _meshes.Add(mesh); Tris += tris; }
            }
        }

        static float Flat(Vector3 v) => new Vector2(v.x, v.z).sqrMagnitude;

        static bool InDoor(List<BloomRect> openings, Vector3 root, float floorY)
        {
            foreach (var o in openings)
            {
                if (o.center.y - o.half.y > floorY + 0.4f) continue; // a window: the vine stops below it instead
                if (o.Contains(new Vector3(root.x, o.center.y, root.z), 0.15f, 0.3f)) return true;
            }
            return false;
        }

        public bool OpeningMoved()
        {
            if (!MeadowLayout.Active || !_hasWalls || _window == null) return false;
            bool found = _window.TryGetVineOpening(out var current);
            return found != _hadOpening || (found &&
                (Vector3.SqrMagnitude(current.center - _lastOpening.center) > 0.01f ||
                 Vector3.Angle(current.normal, _lastOpening.normal) > 8f ||
                 Vector2.SqrMagnitude(current.half - _lastOpening.half) > 0.01f ||
                 (_window.IsOpen && !_windowWasOpen)));
        }

        BloomRect WindowRect(out bool ok)
        {
            _window = _window != null ? _window : Object.FindAnyObjectByType<FutureWindow>(FindObjectsInactive.Include);
            ok = false;
            if (_window == null) return default;
            if (MeadowLayout.Active)
            {
                _hadOpening = ok = _window.TryGetVineOpening(out _lastOpening);
                _windowWasOpen = _window.IsOpen;
                return _lastOpening;
            }
            if (!_window.gameObject.activeInHierarchy || _window.PaneTransform == null) return default;
            var t = _window.PaneTransform;
            Vector3 scale = t.lossyScale;
            ok = Mathf.Abs(scale.x) > 0.01f;
            return new BloomRect
            {
                center = t.position, right = t.right, up = t.up, normal = -t.forward,
                half = new Vector2(Mathf.Abs(scale.x) * 1.2f, Mathf.Abs(scale.y) * 1.2f),
            };
        }

        public static bool BlocksPortalView(BloomRect window, Vector3 eye, Vector3 point)
        {
            float eyeDepth = Vector3.Dot(eye - window.center, window.normal);
            float pointDepth = Vector3.Dot(point - window.center, window.normal);
            if (eyeDepth <= 0.01f || pointDepth < -0.16f || pointDepth >= eyeDepth - 0.01f) return false;
            Vector3 onPane = eye + (point - eye) * (eyeDepth / (eyeDepth - pointDepth));
            return window.Contains(onPane, 0.24f, 0.01f);
        }

        public void ResetGrowth() { _bt = 0f; _grown = false; }

        /// <summary>Health 0.3 (sick) .. 1.5 (escape): how far up the walls the green has climbed.</summary>
        public void Draw(float health, int layer)
        {
            if (_mat == null || _meshes.Count == 0) return;
            float h01 = Mathf.Clamp01((health - 0.3f) / 1.15f);
            float climb = Mathf.Lerp(MeadowTuning.WallStart, 2.8f, Mathf.Clamp01(h01 * 1.6f)); // meadow: up the walls from the start
            Climb = Mathf.Max(0f, (_bt - 0.85f) * Vines.Climb);
            float target = 0.35f + 0.5f + climb / Vines.Climb; // births ~0.15-0.35 s, blossoms 0.5 s behind the tip
            if (!_grown) { _bt = target; _grown = true; } // covered from the first frame (title), not growing in from the floor
            _bt = Mathf.MoveTowards(_bt, target, Time.deltaTime * 1.2f);
            _mpb.SetFloat(BloomTId, _bt);
            _mpb.SetFloat(ClockId, Time.time);
            _mpb.SetFloat(ScaleId, Mathf.Lerp(0.75f, 1f, h01));
            foreach (var m in _meshes) Vines.Draw(m, _mat, _mpb, layer);
        }

        /// <summary>A landing spot on a wall vine, below how far the green has climbed now. False when there are none.</summary>
        public bool Spot(System.Random rnd, out Vector3 point, out Vector3 normal)
        {
            point = default;
            normal = Vector3.forward;
            if (_spots.Count == 0 || Climb < 0.2f) return false;
            var v = _spots[rnd.Next(_spots.Count)];
            float h = Mathf.Min(v.height, Climb) * (0.15f + 0.8f * (float)rnd.NextDouble());
            point = v.root + Vector3.up * h + v.normal * (0.07f + 0.05f * (float)rnd.NextDouble()) + v.tangent * (((float)rnd.NextDouble() - 0.5f) * 0.2f);
            normal = v.normal;
            return true;
        }

        public string Describe() =>
            VineCount + " wall vines on " + Source + ", " + Tris + " tris, " + Draws + " draws, climb now " +
            Mathf.Max(0f, (_bt - 0.85f) * Vines.Climb).ToString("0.00", CultureInfo.InvariantCulture) + " m";
    }
}
