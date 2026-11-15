#if VBS_META && VBS_MRUK
using System;
using System.Threading.Tasks;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace VirtualBeeSwarm.Bloom.Meta
{
    /// <summary>
    /// The headset's room scan for the bloom (asmdef VirtualBeeSwarm.Bloom.Meta; only with com.meta.xr.sdk.core and
    /// com.meta.xr.mrutilitykit). Registers with BloomPlatform only where the Meta natives exist (Android, Windows /
    /// macOS); RoomBloom uses it only with bloom.mruk = true (default off: the team rule S1.10 keeps MRUK scene loading
    /// out of the scene, so this adds the MRUK component at RUNTIME, once, in `pre`):
    ///   LoadSceneOnStartup = false, EnableWorldLock = FALSE (else MRUK rewrites the tracking space every frame and the
    ///   hive slides against the real room), then LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: false), so there
    ///   is never a Space Setup prompt. No scan (the venue) = NoRoomsFound and the bloom falls back to probes / ring.
    ///   An MRUK some scene already has (NamTest) is used as it is: its settings are never touched, and nothing is
    ///   loaded on top of it.
    ///   floor = the floor anchor's boundary; walls = WALL_FACE planes; openings = WINDOW_FRAME / DOOR_FRAME planes;
    ///   volumes = every anchor with a volume; pillars = OTHER / UNKNOWN volumes taller than 1.5 m and under 0.8 m wide
    ///   (209 has no column label; INNER_WALL_FACE needs the V2 model, which 209 loads as V1).
    /// </summary>
    public sealed class MrukBloomSource : IBloomSceneSource
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.Android:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.OSXPlayer:
                    BloomPlatform.Scene = new MrukBloomSource();
                    break;
                default:
                    BloomPlatform.Scene = null;
                    break;
            }
        }

        bool _begun;

        public string Name => "MRUK";
        public string Status { get; private set; } = "not started";

        public void Begin()
        {
            if (_begun) return;
            _begun = true;
            try
            {
                if (MRUK.Instance == null)
                {
                    if (UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>() == null) { Status = "no OVRCameraRig"; return; }
                    var go = new GameObject("MRUK (bloom, runtime)");
                    go.SetActive(false);
                    var mruk = go.AddComponent<MRUK>();
                    mruk.SceneSettings ??= new MRUK.MRUKSettings();
                    mruk.SceneSettings.LoadSceneOnStartup = false;
                    mruk.EnableWorldLock = false;
                    go.SetActive(true);
                }
                else
                {
                    Status = "using the scene's MRUK (" + MRUK.Instance.Rooms.Count + " room(s) now)";
                    return;
                }
                Status = "loading";
                _ = Load();
            }
            catch (Exception e)
            {
                Status = "error: " + e.Message;
                Debug.LogWarning("[VBS][Bloom] MRUK: " + e.Message);
            }
        }

        async Task Load()
        {
            try
            {
                // RoomAware: room.requestCapture (default off) opens Space Setup once when the headset has no scan
                VirtualBeeSwarm.RoomAware.RoomAwareTuning.EnsureLoaded();
                bool ask = VirtualBeeSwarm.RoomAware.RoomAwareTuning.RequestSceneCapture;
                var r = await MRUK.Instance.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: ask, removeMissingRooms: true);
                bool ok = r == MRUK.LoadDeviceResult.Success;
                Status = r.ToString() + (ok ? ", " + MRUK.Instance.Rooms.Count + " room(s)" : "");
                Debug.Log("[VBS][Bloom] MRUK load: " + Status + (ask ? " (Space Setup allowed)" : " (no Space Setup prompt)"));
                Debug.Log("[VBS][Room] room scan: " + ScanSummary(r));
            }
            catch (Exception e)
            {
                Status = "error: " + e.Message;
                Debug.LogWarning("[VBS][Bloom] MRUK load: " + e.Message);
            }
        }

        /// <summary>RoomAware: say plainly whether this headset has a scan with furniture (the keep-outs need it).</summary>
        static string ScanSummary(MRUK.LoadDeviceResult r)
        {
            if (r == MRUK.LoadDeviceResult.NoScenePermission)
                return "NONE (NoScenePermission): the app may not read the room scan (USE_SCENE denied), so the depth fallback can't run either. Grant Spatial data in the app's permissions.";
            if (r != MRUK.LoadDeviceResult.Success || MRUK.Instance == null || MRUK.Instance.Rooms.Count == 0)
                return "NONE (" + r + "): no furniture known, placement uses the depth fallback. Run Space Setup (or room.requestCapture=true) to scan the room.";
            var sb = new System.Text.StringBuilder();
            int furniture = 0;
            foreach (var room in MRUK.Instance.Rooms)
            {
                if (room == null) continue;
                foreach (var a in room.Anchors)
                {
                    if (a == null || !a.VolumeBounds.HasValue || a.HasAnyLabel(MRUKAnchor.SceneLabels.GLOBAL_MESH)) continue;
                    furniture++;
                    if (sb.Length < 400) sb.Append(sb.Length > 0 ? ", " : "").Append(a.Label);
                }
            }
            return furniture > 0
                ? "found " + MRUK.Instance.Rooms.Count + " room(s) with " + furniture + " furniture volume(s): " + sb
                : "found " + MRUK.Instance.Rooms.Count + " room(s) but NO furniture volumes (walls/floor only): add furniture in Space Setup; the depth fallback covers it meanwhile";
        }

        public bool TryFill(BloomScene s)
        {
            s.Clear("MRUK");
            if (MRUK.Instance == null || MRUK.Instance.Rooms == null || MRUK.Instance.Rooms.Count == 0) return false;
            var room = MRUK.Instance.GetCurrentRoom();
            if (room == null && MRUK.Instance.Rooms.Count > 0) room = MRUK.Instance.Rooms[0];
            if (room == null) { Status = "loaded, no room"; return false; }
            var floor = room.FloorAnchor;
            if (floor == null) { Status = "room without a floor"; return false; }
            s.FloorY = floor.transform.position.y;
            foreach (var p in floor.PlaneBoundary2D)
            {
                Vector3 w = floor.transform.TransformPoint(new Vector3(p.x, p.y, 0f));
                s.FloorPolygon.Add(new Vector2(w.x, w.z));
            }
            int walls = 0, pillars = 0, vols = 0;
            foreach (var a in room.Anchors)
            {
                if (a == null) continue;
                var t = a.transform;
                if (a.PlaneRect.HasValue && a.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.WINDOW_FRAME | MRUKAnchor.SceneLabels.DOOR_FRAME))
                {
                    var pr = a.PlaneRect.Value;
                    var rect = new BloomRect
                    {
                        center = t.TransformPoint(new Vector3(pr.center.x, pr.center.y, 0f)), right = t.right, up = t.up, normal = t.forward,
                        half = new Vector2(Mathf.Abs(pr.width) * 0.5f, Mathf.Abs(pr.height) * 0.5f),
                    };
                    if (a.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE)) { s.Walls.Add(rect); walls++; }
                    else s.Openings.Add(rect);
                }
                if (a.VolumeBounds.HasValue && !a.HasAnyLabel(MRUKAnchor.SceneLabels.GLOBAL_MESH))
                {
                    var b = a.VolumeBounds.Value; // local; +Z is up
                    Vector3 right = t.right;
                    right.y = 0f;
                    if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
                    var rot = Quaternion.LookRotation(Vector3.Cross(right.normalized, Vector3.up), Vector3.up);
                    float height = Mathf.Abs(b.size.z);
                    bool pillar = a.HasAnyLabel(MRUKAnchor.SceneLabels.OTHER | MRUKAnchor.SceneLabels.UNKNOWN) && height > 1.5f &&
                                  Mathf.Abs(b.size.x) < 0.8f && Mathf.Abs(b.size.y) < 0.8f;
                    s.Volumes.Add(new BloomBox
                    {
                        center = t.TransformPoint(b.center), rotation = rot,
                        half = new Vector3(Mathf.Abs(b.extents.x), height * 0.5f, Mathf.Abs(b.extents.y)), pillar = pillar,
                        label = a.Label.ToString(),
                    });
                    vols++;
                    if (pillar) pillars++;
                }
            }
            Status = "room: floor " + s.FloorPolygon.Count + " pts, " + walls + " walls, " + s.Openings.Count + " openings, " + vols +
                     " volumes (" + pillars + " pillars)";
            return s.FloorPolygon.Count >= 3;
        }
    }
}
#endif
