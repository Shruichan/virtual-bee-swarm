using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace VirtualBeeSwarm.Capture
{
    /// <summary>
    /// Capture only: a cheap prop-through-prop finder. Every 2 s (and on each beat) it takes the solid props under the
    /// hive rig (TableAnchor: hive, rocket, portal, lever, station props, kit, rain cloud; no particles, bees, text, meadow
    /// or vines), groups the renderers by object, and logs "[VBS][Clip] beat: A x B overlap (cm)" once per pair per beat
    /// when their world boxes overlap by more than 3 cm on every axis. Boxes over-report on round or slanted shapes:
    /// it's a list of places to look at in the frames, not a verdict.
    /// </summary>
    public sealed class ClipProbe : MonoBehaviour
    {
        static readonly string[] Skip =
        {
            "Bee", "Swarm", "Wasp", "Meadow", "Vine", "Flower", "Grass", "Petal", "Text", "Caption", "Label", "Glow", "Shadow",
            "Halo", "Swirl", "Vignette", "Spark", "Mote", "Light", "Shaft", "Pane", "Stake", "Island", "Turf", "Crew", "Landing",
            "Choice", "Tag", "Card", "Plate", "Hex", "Beeswax", "Puff", "Smoke", "Flame", "Water", "Ember", "Dust",
        };

        GameDirector _director;
        float _next;
        readonly HashSet<string> _seen = new HashSet<string>();
        readonly Dictionary<Transform, Bounds> _groups = new Dictionary<Transform, Bounds>();
        string _beat = "pre";

        void Start()
        {
            _director = GameDirector.Instance != null ? GameDirector.Instance : FindAnyObjectByType<GameDirector>();
            if (_director != null) _director.BeatChanged += (a, b) => { _beat = b.ToString(); _seen.Clear(); _next = Time.time + 1.5f; };
        }

        void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 2f;
            var anchor = AR.ArRig.Instance != null ? AR.ArRig.Instance.TableAnchor : null;
            if (anchor == null) return;
            _groups.Clear();
            Collect(anchor, anchor);
            var win = FindAnyObjectByType<FutureWindow>();
            if (win != null) Collect(win.transform, win.transform);
            var keys = new List<Transform>(_groups.Keys);
            var sb = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
                for (int j = i + 1; j < keys.Count; j++)
                {
                    var a = _groups[keys[i]];
                    var b = _groups[keys[j]];
                    if (keys[i].IsChildOf(keys[j]) || keys[j].IsChildOf(keys[i])) continue;
                    float ox = Mathf.Min(a.max.x, b.max.x) - Mathf.Max(a.min.x, b.min.x);
                    float oy = Mathf.Min(a.max.y, b.max.y) - Mathf.Max(a.min.y, b.min.y);
                    float oz = Mathf.Min(a.max.z, b.max.z) - Mathf.Max(a.min.z, b.min.z);
                    if (ox < 0.03f || oy < 0.03f || oz < 0.03f) continue;
                    string key = Name(keys[i]) + " x " + Name(keys[j]);
                    if (!_seen.Add(key)) continue;
                    sb.Length = 0;
                    sb.Append("[VBS][Clip] ").Append(_beat).Append(": ").Append(key).Append(" overlap ")
                      .Append((ox * 100f).ToString("0")).Append('x').Append((oy * 100f).ToString("0")).Append('x').Append((oz * 100f).ToString("0")).Append(" cm");
                    Debug.Log(sb.ToString());
                }
        }

        // group = the object right under a "Props" / station / rig level that owns the renderer
        void Collect(Transform root, Transform anchor)
        {
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (IsSkipped(r.transform, anchor)) continue;
                var b = r.bounds;
                if (b.size.sqrMagnitude < 1e-6f || b.size.x > 3f || b.size.y > 3f || b.size.z > 3f) continue;
                var g = GroupOf(r.transform, anchor);
                if (_groups.TryGetValue(g, out var gb)) { gb.Encapsulate(b); _groups[g] = gb; }
                else _groups[g] = b;
            }
        }

        static bool IsSkipped(Transform t, Transform stop)
        {
            for (var p = t; p != null && p != stop; p = p.parent)
                foreach (var s in Skip)
                    if (p.name.IndexOf(s, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static Transform GroupOf(Transform t, Transform anchor)
        {
            var g = t;
            for (var p = t; p != null && p != anchor; p = p.parent)
            {
                var up = p.parent;
                if (up == null || up == anchor || up.name == "Props" || up.name.StartsWith("[VBS]")) { g = p; break; }
                g = p;
            }
            return g;
        }

        static string Name(Transform t) => t.parent != null ? t.parent.name + "/" + t.name : t.name;
    }
}
