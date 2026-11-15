using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using VirtualBeeSwarm.Bloom;
using VirtualBeeSwarm.HandsOn;

namespace VirtualBeeSwarm.RoomAware
{
    /// <summary>
    /// Desktop verification only (env VBS_ROOM_AUDIT=1, or any capture room VBS_CAPTURE_ROOM): once a second and on every
    /// beat it measures every placed object the player sees (hive, rocket, portal, ORDER UP board, lever, Beeswax, the SIGN
    /// HERE clipboard, the choice stakes, each delivered prop and bench prop) mesh by mesh against the room's TRUE furniture
    /// (the scene source's volumes, whatever room.source says) and walls, and logs
    ///   "[VBS][RoomAudit] SUMMARY overlaps=N maxDepth=D cm noBeeswax=N/D cm worst=obj/furniture hidden=H face=F sight=S objs=[...]"
    /// (cumulative, on each beat and at quit): N objects that ever sat more than 2 cm inside furniture or a wall, the
    /// deepest, props hidden for not fitting, samples with the board / lever / prompt closer than 0.35 m to the eyes or
    /// across the eye-to-hive line, samples with furniture between the eyes and the hive.
    /// </summary>
    [DefaultExecutionOrder(10050)]
    public sealed class RoomAudit : MonoBehaviour
    {
        public static bool Wanted
        {
            get
            {
                try
                {
                    return Environment.GetEnvironmentVariable("VBS_ROOM_AUDIT") == "1" ||
                           !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VBS_CAPTURE_ROOM"));
                }
                catch (Exception) { return false; }
            }
        }

        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly string[] Skip =
        {
            "Glow", "Shadow", "Halo", "Puff", "Smoke", "Spark", "Text", "Caption", "Label", "Shaft", "Particle", "Mote", "Dust", "Vignette",
            "Swirl", "Flame", "Ember", "Light", "Grass", "Flower", "Vine", "Petal", "Swarm", "Wasp", "Crew", "Stream", "Water",
        };

        readonly Dictionary<string, float> _depth = new Dictionary<string, float>();
        readonly Dictionary<string, string> _where = new Dictionary<string, string>();
        readonly Dictionary<string, float> _flight = new Dictionary<string, float>();
        readonly Dictionary<string, Vector3> _last = new Dictionary<string, Vector3>();
        readonly List<Renderer> _rs = new List<Renderer>(128);
        GameDirector _director;
        float _next;
        int _hidden, _relocated, _face, _cross, _sight, _samples;
        float _minHead = float.MaxValue;
        string _beat = "pre";
        bool _done;

        void Start()
        {
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (_director != null) _director.BeatChanged += OnBeat;
            Application.logMessageReceived += OnLog;
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            if (_director != null) _director.BeatChanged -= OnBeat;
        }

        void OnApplicationQuit() => Summary("quit");

        void OnLog(string msg, string stack, LogType type)
        {
            if (msg.Contains("hidden: no room")) _hidden++;
            else if (msg.StartsWith("[VBS][Room] ", StringComparison.Ordinal) && msg.Contains("relocated")) _relocated++;
        }

        void OnBeat(GameBeat from, GameBeat to)
        {
            Summary(_beat);
            _beat = to.ToString().ToLowerInvariant();
            _next = Time.time + 0.5f;
        }

        Camera _wide;
        bool _wideLooked;

        /// <summary>Capture rooms: the "wide" view goes to the room corner farthest from the rig, under the ceiling, looking
        /// at the rig (ShowCapture's fixed wide spot is outside most fixture rooms).</summary>
        void LateUpdate()
        {
            if (!_wideLooked) { _wideLooked = true; var g = GameObject.Find("WideCamera"); _wide = g != null ? g.GetComponent<Camera>() : null; }
            var anchor = MeadowLayout.Anchor;
            var field = MeadowField.Instance;
            if (_wide == null || anchor == null || field == null) return;
            var scene = field.CurrentScene();
            if (scene == null || scene.FloorPolygon.Count < 3 || !scene.Name.StartsWith("capture room", StringComparison.Ordinal)) return;
            Vector2 mn = scene.FloorPolygon[0], mx = mn;
            foreach (var p in scene.FloorPolygon) { mn = Vector2.Min(mn, p); mx = Vector2.Max(mx, p); }
            float ceil = AR.ArRig.Instance != null ? AR.ArRig.Instance.Room.CeilingOrDefault : 2.4f;
            Vector3 best = default;
            float far = -1f;
            for (int i = 0; i < 4; i++)
            {
                var c = new Vector3((i & 1) == 0 ? mn.x + 0.3f : mx.x - 0.3f, ceil - 0.3f, (i & 2) == 0 ? mn.y + 0.3f : mx.y - 0.3f);
                float d = (new Vector2(c.x - anchor.position.x, c.z - anchor.position.z)).sqrMagnitude;
                if (d > far) { far = d; best = c; }
            }
            Vector3 look = anchor.position + Vector3.up * 0.7f;
            _wide.transform.SetPositionAndRotation(best, Quaternion.LookRotation(look - best, Vector3.up));
            _wide.fieldOfView = 78f;
        }

        void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 1f;
            var field = MeadowField.Instance;
            var scene = field != null ? field.CurrentScene() : null;
            var cam = Camera.main;
            if (scene == null || cam == null) return;
            _samples++;
            Vector3 eye = cam.transform.position;
            var anchor = MeadowLayout.Anchor;
            var hive = FindAnyObjectByType<HiveBody>();
            Measure(scene, "hive", hive != null ? hive.transform : null);
            Measure(scene, "rocket", anchor != null ? anchor.Find("HONEYMOON-1") : null);
            var win = FindAnyObjectByType<FutureWindow>();
            Measure(scene, "portal", win != null ? win.transform : null);
            var board = FindAnyObjectByType<OrderBoard>();
            Measure(scene, "board", board != null ? board.transform : null);
            var lever = FindAnyObjectByType<HoneyLever>();
            Measure(scene, "lever", lever != null ? lever.transform : null);
            var bw = FindAnyObjectByType<BeeswaxController>();
            Measure(scene, "beeswax", bw != null ? bw.transform : null);
            var clip = FindAnyObjectByType<SignHereClipboard>();
            Measure(scene, "clipboard", clip != null ? clip.transform : null);
            if (anchor != null)
                foreach (var t in anchor.GetComponentsInChildren<Transform>())
                    if (t.name == "Buttons" || t.name == "StartRow") Measure(scene, "stakes", t);
            foreach (var di in FindObjectsByType<DeliveryItem>(FindObjectsSortMode.None))
            {
                if (di == null || (hive != null && (di.transform == hive.transform || hive.transform.IsChildOf(di.transform))) || di.name.StartsWith("HONEYMOON")) continue;
                Measure(scene, "prop:" + di.name, di.transform);
            }
            if (anchor != null)
                foreach (var t in anchor.GetComponentsInChildren<Transform>())
                    if (t.name.StartsWith("NP_", StringComparison.Ordinal) && t.parent != null && !t.parent.name.StartsWith("NP_", StringComparison.Ordinal))
                        Measure(scene, "newprop:" + t.name, t);

            // the controls in the face / across the eye-to-hive line
            Bounds hb = default;
            bool haveHive = hive != null && RoomKeepOut.MeshBounds(hive.transform, out hb, true);
            foreach (var t in new[] { board != null && board.Visible ? board.transform : null, lever != null ? lever.transform : null, Prompt() })
            {
                if (t == null || !RoomKeepOut.MeshBounds(t, out var b, true)) continue;
                float d = Vector3.Distance(eye, b.ClosestPoint(eye));
                _minHead = Mathf.Min(_minHead, d);
                if (d < 0.35f) _face++;
                if (haveHive && t != Prompt())
                {
                    Vector3 aim = hb.center, dir = aim - eye;
                    if (b.IntersectRay(new Ray(eye, dir.normalized), out float hit) && hit < dir.magnitude) _cross++;
                }
            }
            if (haveHive && Blocked(scene, eye, hb.center)) _sight++;
        }

        Transform _prompt;
        Transform Prompt()
        {
            if (_prompt == null) { var g = GameObject.Find("[VBS] HandsOnPrompt"); _prompt = g != null ? g.transform : null; }
            return _prompt != null && _prompt.gameObject.activeInHierarchy ? _prompt : null;
        }

        void Measure(BloomScene scene, string name, Transform root)
        {
            if (root == null || !root.gameObject.activeInHierarchy) return;
            root.GetComponentsInChildren(false, _rs);
            float worst = 0f;
            string where = null;
            Bounds all = default;
            bool any = false;
            foreach (var r in _rs)
            {
                if (r == null || !r.enabled || r.forceRenderingOff || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                if (r.GetComponent<TextMesh>() != null || SkipName(r.transform, root)) continue;
                var b = r.bounds;
                if (b.size.sqrMagnitude < 1e-6f || b.size.x > 4f || b.size.y > 4f || b.size.z > 4f) continue;
                if (!any) { all = b; any = true; } else all.Encapsulate(b);
                // shrink a little: an AABB of a round or slanted part over-reports at the corners
                b.Expand(-Mathf.Min(0.02f, b.size.x * 0.1f));
                for (int i = 0; i < scene.Volumes.Count; i++)
                {
                    var v = scene.Volumes[i];
                    float d = RoomKeepOut.Overlap(new KeepBox { c = v.center, rot = v.rotation, half = v.half }, b, 0f);
                    if (d > worst) { worst = d; where = (string.IsNullOrEmpty(v.label) ? "furniture " + i : v.label) + " (" + r.name + ")"; }
                }
                foreach (var w in scene.Walls)
                {
                    Vector3 dd = b.center - w.center;
                    if (Mathf.Abs(Vector3.Dot(dd, w.right)) > w.half.x) continue;
                    float reach = Mathf.Abs(Vector3.Dot(b.extents, new Vector3(Mathf.Abs(w.normal.x), Mathf.Abs(w.normal.y), Mathf.Abs(w.normal.z))));
                    float behind = reach - Vector3.Dot(dd, w.normal);
                    if (behind > worst && behind < 1.5f) { worst = behind; where = "wall (" + r.name + ")"; }
                }
            }
            _rs.Clear();
            // at rest (moved < 2 cm since the last sample) = where it was put; moving = carried / flying in (the crew's
            // path, a launch): logged apart. Beeswax counts always (he must never be inside anything).
            string key = name + "#" + root.GetInstanceID();
            bool moving = any && _last.TryGetValue(key, out var lastC) && (all.center - lastC).sqrMagnitude > 0.02f * 0.02f;
            if (!_last.ContainsKey(key)) moving = true; // first sight: not known to be at rest yet
            if (any) _last[key] = all.center;
            if (worst <= 0.02f) return;
            if (moving && name != "beeswax")
            {
                if (!_flight.TryGetValue(name, out float fw) || worst > fw) _flight[name] = worst;
                return;
            }
            if (!_depth.TryGetValue(name, out float was) || worst > was)
            {
                _depth[name] = worst;
                _where[name] = where + "@" + _beat;
                Debug.Log("[VBS][RoomAudit] " + _beat + ": " + name + " " + (worst * 100f).ToString("0", C) + " cm into " + where);
            }
        }

        static bool SkipName(Transform t, Transform stop)
        {
            for (var p = t; p != null; p = p.parent)
            {
                for (int i = 0; i < Skip.Length; i++)
                    if (p.name.IndexOf(Skip[i], StringComparison.Ordinal) >= 0) return true;
                if (p == stop) break;
            }
            return false;
        }

        static bool Blocked(BloomScene scene, Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            foreach (var v in scene.Volumes)
            {
                var inv = Quaternion.Inverse(v.rotation);
                Vector3 o = inv * (a - v.center), dir = inv * d;
                Vector3 h = v.half - Vector3.one * 0.03f;
                float t0 = 0f, t1 = 1f;
                bool hit = h.x > 0f && h.y > 0f && h.z > 0f;
                for (int ax = 0; ax < 3 && hit; ax++)
                {
                    if (Mathf.Abs(dir[ax]) < 1e-6f) { if (Mathf.Abs(o[ax]) > h[ax]) hit = false; continue; }
                    float ta = (-h[ax] - o[ax]) / dir[ax], tb = (h[ax] - o[ax]) / dir[ax];
                    if (ta > tb) (ta, tb) = (tb, ta);
                    t0 = Mathf.Max(t0, ta);
                    t1 = Mathf.Min(t1, tb);
                    if (t0 > t1) hit = false;
                }
                if (hit && t1 > 0.02f && t0 < 0.98f) return true;
            }
            _ = len;
            return false;
        }

        void Summary(string at)
        {
            if (_done) return;
            if (at == "quit") _done = true;
            int n = 0;
            float max = 0f;
            string worst = "-";
            var sb = new StringBuilder();
            int noBw = 0;
            float maxNoBw = 0f;
            foreach (var kv in _depth)
            {
                n++;
                if (kv.Key != "beeswax") { noBw++; maxNoBw = Mathf.Max(maxNoBw, kv.Value); }
                if (kv.Value > max) { max = kv.Value; worst = kv.Key + "/" + _where[kv.Key]; }
                if (sb.Length > 0) sb.Append(';');
                sb.Append(kv.Key).Append('=').Append((kv.Value * 100f).ToString("0", C)).Append("cm@").Append(_where[kv.Key]);
            }
            float flightMax = 0f;
            foreach (var kv in _flight) flightMax = Mathf.Max(flightMax, kv.Value);
            Debug.Log("[VBS][RoomAudit] SUMMARY at=" + at + " overlaps=" + n + " maxDepth=" + (max * 100f).ToString("0", C) + "cm noBeeswax=" + noBw + "/" + (maxNoBw * 100f).ToString("0", C) + "cm worst=" + worst.Replace(' ', '_') +
                      " hidden=" + _hidden + " relocated=" + _relocated + " face=" + _face + " cross=" + _cross + " sight=" + _sight + " samples=" + _samples + " moving=" + _flight.Count + "/" + (flightMax * 100f).ToString("0", C) + "cm" +
                      " minHead=" + (_minHead < 1e5f ? _minHead.ToString("0.00", C) : "-") + " objs=[" + sb.ToString().Replace(' ', '_') + "]" +
                      " rig=[" + MeadowLayout.LastFit.Replace(' ', '_') + "]");
        }
    }
}
