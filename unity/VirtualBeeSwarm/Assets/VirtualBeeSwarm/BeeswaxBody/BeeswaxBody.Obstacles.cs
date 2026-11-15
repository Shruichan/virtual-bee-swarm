using System;
using System.Collections.Generic;
using UnityEngine;
using VirtualBeeSwarm.HandsOn;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// BeeswaxBody's obstacles: one oriented box per mesh of the things on the table (fitted to the mesh bounds in its own
    /// transform, so it follows any move, lid swing or rescale every frame), spheres for the player's head and hands, and
    /// the table top. The list of things is rescanned once a second (props come and go); the boxes update every frame.
    /// </summary>
    public partial class BeeswaxBody
    {
        public enum Group { Head, Hand, Surface, Hive, Rocket, Window, Card, Lever, Board, Socket, Carry, Prop, NewProp, Station, Room, RoomWall, Count }
        static readonly string[] GroupNames = { "head", "hand", "table", "hive", "rocket", "window", "card", "lever", "board", "socket", "carry", "prop", "newprop", "station", "room", "wall" };
        public static string GroupName(int g) => g >= 0 && g < GroupNames.Length ? GroupNames[g] : "?";

        const int MaskAll = (1 << (int)Group.Count) - 1;
        static int Bit(Group g) => 1 << (int)g;

        enum Kind : byte { Mesh, Sphere, Surface }

        struct Part
        {
            public Kind kind;
            public int group;
            public Renderer r;
            public Transform t;
            public Vector3 lc, le;      // local bounds (Mesh)
            public float sr;            // sphere radius (Sphere)
            public bool on;
            public BodyMath.Obb box;    // world (Mesh / Surface); Sphere: box.c = centre
            public float bound;         // broadphase radius around box.c
            public Vector3 prevC, vel;
            public bool hadPrev;
            public float onSince;       // when it last switched on (or was first seen)
            public bool clip;           // the SIGN HERE clipboard: in his hands while the title carries him
        }

        bool _holdingClip;
        bool Ignored(in Part p) => !p.on || (p.clip && _holdingClip);

        readonly List<Part> _parts = new List<Part>(512);
        readonly List<Renderer> _rbuf = new List<Renderer>(256);
        readonly HashSet<int> _seen = new HashSet<int>();
        float _nextScan, _lastScanAt;
        int _scanCount;
        // motion carried across a rescan (by renderer), so a prop's speed (and how hard it bumps him) never resets
        readonly Dictionary<int, Vector3> _carryPrev = new Dictionary<int, Vector3>(512);
        readonly Dictionary<int, Vector3> _carryVel = new Dictionary<int, Vector3>(512);
        HiveBody _hive;
        Transform _rocket, _anchor, _window;
        const int HeadIdx = 0, HandLIdx = 1, HandRIdx = 2, SurfaceIdx = 3;

        static readonly string[] SkipNames = { "Glow", "Shadow", "LightShaft", "Pane", "Flame", "Halo", "Puff", "Beam", "Caption", "Particles", "Embers", "Marker", "Highlight", "Outline" };

        void ScanObstacles()
        {
            _scanCount++;
            _lastScanAt = Time.time;
            _carryPrev.Clear();
            _carryVel.Clear();
            Part keepHead = default, keepL = default, keepR = default, keepTop = default;
            bool kept = _parts.Count > SurfaceIdx;
            if (kept) { keepHead = _parts[HeadIdx]; keepL = _parts[HandLIdx]; keepR = _parts[HandRIdx]; keepTop = _parts[SurfaceIdx]; }
            for (int i = SurfaceIdx + 1; i < _parts.Count; i++)
            {
                var p = _parts[i];
                if (p.r == null || !p.hadPrev) continue;
                int id = p.r.GetInstanceID();
                _carryPrev[id] = p.prevC;
                _carryVel[id] = p.vel;
            }
            _parts.Clear();
            _seen.Clear();
            // fixed slots: head, hands, the table top (or the meadow floor)
            if (kept) { _parts.Add(keepHead); _parts.Add(keepL); _parts.Add(keepR); _parts.Add(keepTop); }
            else
            {
                _parts.Add(new Part { kind = Kind.Sphere, group = (int)Group.Head, sr = HeadKeepOut });
                _parts.Add(new Part { kind = Kind.Sphere, group = (int)Group.Hand, sr = HandKeepOut });
                _parts.Add(new Part { kind = Kind.Sphere, group = (int)Group.Hand, sr = HandKeepOut });
                _parts.Add(new Part { kind = Kind.Surface, group = (int)Group.Surface, t = _anchor });
            }

            if (_hive == null) _hive = FindAnyObjectByType<HiveBody>(FindObjectsInactive.Include);
            if (_hive != null) AddRoot(_hive.transform, Group.Hive);
            if (_rocket == null) _rocket = FindRocket();
            if (_rocket != null) AddRoot(_rocket, Group.Rocket);
            if (_window == null)
            {
                var w = FindAnyObjectByType<FutureWindow>(FindObjectsInactive.Include);
                _window = w != null ? w.transform : null;
            }
            if (_window != null) AddRoot(_window, Group.Window);
            FindPane();
            AddAll(UnionCardType, Group.Card);
            AddAll<HoneyLever>(Group.Lever);
            AddAll<OrderBoard>(Group.Board);
            AddAll<SetupSocket>(Group.Socket);
            AddAll<CarryProp>(Group.Carry);
            AddAll<DeliveryItem>(Group.Prop);
            AddAll<NewPropAct>(Group.NewProp);
            AddAll<StationBase>(Group.Station); // the station props (the smoker on its stand...) while they are out
            AddAll<RoomAware.RoomKeepOutProxy>(Group.Room); // [RoomAware] hook: real furniture from the room scan / depth
            AddAll<RoomAware.RoomWallProxy>(Group.RoomWall); // [RoomAware] hook: the scan's walls (never in a detour box, see BoundsNear)
        }

        // ------------------------------------------------------------ the window's opening
        // The frame, its trim and vines are rings: one box per mesh would close the opening he (and the logo) fly out
        // through. Every window box that spans the pane is cut into four bars around it.
        Transform _paneT;
        Vector3 _paneLc, _paneLe;
        bool _hasPane;
        BodyMath.Obb _pane;

        void FindPane()
        {
            _paneT = null;
            var fw = _window != null ? _window.GetComponent<FutureWindow>() : null;
            Transform t = fw != null ? fw.PaneTransform : null;
            var mf = t != null ? t.GetComponent<MeshFilter>() : null;
            if (mf == null || mf.sharedMesh == null) return;
            _paneT = t;
            _paneLc = mf.sharedMesh.bounds.center;
            _paneLe = mf.sharedMesh.bounds.extents;
        }

        void UpdatePane()
        {
            _hasPane = _paneT != null && BodyMath.Obb.From(_paneT.localToWorldMatrix, _paneLc, _paneLe, out _pane);
            if (!_hasPane) return;
            // the quad's flat axis is the normal: make it z
            if (_pane.h.x < _pane.h.z) { var a = _pane.ax; _pane.ax = _pane.az; _pane.az = a; _pane.h = new Vector3(_pane.h.z, _pane.h.y, _pane.h.x); }
            else if (_pane.h.y < _pane.h.z) { var a = _pane.ay; _pane.ay = _pane.az; _pane.az = a; _pane.h = new Vector3(_pane.h.x, _pane.h.z, _pane.h.y); }
            _hasPane = _pane.h.x > 1e-3f && _pane.h.y > 1e-3f;
        }

        /// <summary>A window box that spans the opening: the gap to the four bars around the pane instead.</summary>
        bool RingGap(in Part p, Vector3 a, Vector3 b, float r, out float gap, out Vector3 n)
        {
            gap = 0f; n = Vector3.zero;
            if (!_hasPane || p.group != (int)Group.Window || p.kind != Kind.Mesh) return false;
            Vector3 d = p.box.c - _pane.c;
            float ox = Vector3.Dot(d, _pane.ax), oy = Vector3.Dot(d, _pane.ay), oz = Vector3.Dot(d, _pane.az);
            float ex = Proj(p.box, _pane.ax), ey = Proj(p.box, _pane.ay), ez = Proj(p.box, _pane.az);
            float hx = _pane.h.x, hy = _pane.h.y;
            if (ox - ex > -0.8f * hx || ox + ex < 0.8f * hx || oy - ey > -0.8f * hy || oy + ey < 0.8f * hy) return false; // not across the opening
            gap = float.PositiveInfinity;
            Bar(ox - ex, -hx, oy - ey, oy + ey, oz, ez, a, b, r, ref gap, ref n);
            Bar(hx, ox + ex, oy - ey, oy + ey, oz, ez, a, b, r, ref gap, ref n);
            Bar(-hx, hx, oy - ey, -hy, oz, ez, a, b, r, ref gap, ref n);
            Bar(-hx, hx, hy, oy + ey, oz, ez, a, b, r, ref gap, ref n);
            if (float.IsPositiveInfinity(gap)) gap = 1e3f;
            return true;
        }

        static float Proj(in BodyMath.Obb o, Vector3 axis) =>
            Mathf.Abs(Vector3.Dot(o.ax, axis)) * o.h.x + Mathf.Abs(Vector3.Dot(o.ay, axis)) * o.h.y + Mathf.Abs(Vector3.Dot(o.az, axis)) * o.h.z;

        void Bar(float x0, float x1, float y0, float y1, float zc, float hz, Vector3 a, Vector3 b, float r, ref float gap, ref Vector3 n)
        {
            if (x1 - x0 < 1e-4f || y1 - y0 < 1e-4f) return;
            var bar = new BodyMath.Obb
            {
                c = _pane.c + _pane.ax * (0.5f * (x0 + x1)) + _pane.ay * (0.5f * (y0 + y1)) + _pane.az * zc,
                ax = _pane.ax, ay = _pane.ay, az = _pane.az,
                h = new Vector3(0.5f * (x1 - x0), 0.5f * (y1 - y0), Mathf.Max(hz, 1e-4f)),
            };
            float g = BodyMath.CapsuleBox(a, b, r, bar, out Vector3 bn);
            if (g < gap) { gap = g; n = bn; }
        }

        Transform FindRocket()
        {
            var perch = FindUnder(_anchor, "Perch");
            if (perch != null && perch.parent != null && perch.parent != _anchor) return perch.parent;
            return FindUnder(_anchor, "HONEYMOON-1");
        }

        static Transform FindUnder(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var f = FindUnder(root.GetChild(i), name);
                if (f != null) return f;
            }
            return null;
        }

        // the union card lives in the UiTheme assembly (which references this one): found by name
        static Type _unionCardType;
        static bool _unionCardLooked;
        static Type UnionCardType
        {
            get
            {
                if (!_unionCardLooked) { _unionCardLooked = true; _unionCardType = Type.GetType("VirtualBeeSwarm.Theme.UnionCard, VirtualBeeSwarm.UiTheme"); }
                return _unionCardType;
            }
        }

        void AddAll(Type t, Group g)
        {
            if (t == null) return;
            var all = FindObjectsByType(t, FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++) if (all[i] is Component c && c != null) AddRoot(c.transform, g);
        }

        void AddAll<T>(Group g) where T : Component
        {
            var all = FindObjectsByType<T>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++) if (all[i] != null) AddRoot(all[i].transform, g);
        }

        void AddRoot(Transform root, Group g)
        {
            if (root == null || root.IsChildOf(transform)) return;
            root.GetComponentsInChildren(true, _rbuf);
            for (int i = 0; i < _rbuf.Count; i++)
            {
                var r = _rbuf[i];
                if (r == null || r.transform.IsChildOf(transform)) continue; // his own badge, his clipboard hand
                if (!_seen.Add(r.GetInstanceID())) continue;
                if (Skip(r)) continue;
                Vector3 lc, le;
                Transform t;
                if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mb = mf.sharedMesh.bounds;
                    lc = mb.center; le = mb.extents; t = r.transform;
                }
                else if (r is SkinnedMeshRenderer smr)
                {
                    var lb = smr.localBounds;
                    lc = lb.center; le = lb.extents; t = smr.rootBone != null ? smr.rootBone : smr.transform;
                }
                else if (r is SpriteRenderer sr && sr.sprite != null)
                {
                    var sb = sr.sprite.bounds;
                    lc = sb.center; le = sb.extents; t = r.transform;
                }
                else continue;
                var part = new Part { kind = Kind.Mesh, group = (int)g, r = r, t = t, lc = lc, le = le, clip = r.GetComponentInParent<SignHereClipboard>(true) != null };
                if (_carryPrev.TryGetValue(r.GetInstanceID(), out part.prevC)) { part.hadPrev = true; _carryVel.TryGetValue(r.GetInstanceID(), out part.vel); }
                _parts.Add(part);
            }
            _rbuf.Clear();
        }

        static bool Skip(Renderer r)
        {
            if (r.GetComponent<TextMesh>() != null || r.GetComponent("TextMeshPro") != null) return true; // text sits on its card
            string n = r.gameObject.name;
            for (int i = 0; i < SkipNames.Length; i++) if (n.IndexOf(SkipNames[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>Every frame: the boxes follow their meshes; head and hands from the rig.</summary>
        void UpdateObstacles(float dt)
        {
            UpdatePane();
            Transform head = _b.HeadTransform;
            for (int i = 0; i < _parts.Count; i++)
            {
                var p = _parts[i];
                bool on = false;
                switch (p.kind)
                {
                    case Kind.Mesh:
                        if (p.r != null && p.t != null && p.r.enabled && p.r.gameObject.activeInHierarchy &&
                            BodyMath.Obb.From(p.t.localToWorldMatrix, p.lc, p.le, out p.box))
                        {
                            on = p.box.h.x + p.box.h.y + p.box.h.z > _tiny;
                            p.bound = p.box.h.magnitude;
                        }
                        break;
                    case Kind.Surface:
                        on = SurfaceBox(out p.box);
                        if (on) p.bound = p.box.h.magnitude;
                        break;
                    case Kind.Sphere:
                        Vector3 c = default;
                        if (i == HeadIdx) { on = head != null; if (on) c = head.position; }
                        else if (i == HandLIdx) on = GameHands.TryGetIndexTip(GameHand.Left, out c);
                        else on = GameHands.TryGetIndexTip(GameHand.Right, out c);
                        p.box.c = c;
                        p.bound = p.sr;
                        break;
                }
                if (on && p.hadPrev && dt > 1e-5f) p.vel = (p.box.c - p.prevC) / dt; else p.vel = Vector3.zero;
                if (on && !p.on && !p.hadPrev) p.onSince = Time.time;
                p.prevC = p.box.c;
                p.hadPrev = on;
                p.on = on;
                _parts[i] = p;
            }
        }

        /// <summary>Signed gap between a capsule and one part (negative = overlap), n = the way out for the capsule.</summary>
        float Gap(in Part p, Vector3 a, Vector3 b, float r, out Vector3 n)
        {
            if (p.kind == Kind.Sphere) return BodyMath.CapsuleSphere(a, b, r, p.box.c, p.sr, out n);
            if (RingGap(p, a, b, r, out float rg, out n)) return rg;
            return BodyMath.CapsuleBox(a, b, r, p.box, out n);
        }

        /// <summary>Quick reject: the capsule is surely farther than `beyond` from this part.</summary>
        static bool Far(in Part p, Vector3 mid, float capReach, float beyond) =>
            (mid - p.box.c).sqrMagnitude > Sq(p.bound + capReach + beyond);

        static float Sq(float x) => x * x;

        /// <summary>
        /// The smallest push that gets the capsule `clearance` clear of every part in `mask` (a few Gauss-Seidel passes).
        /// worstGroup: the group that needed the most (-1 if none).
        /// </summary>
        static readonly Vector3[] WedgeDirs =
        {
            Vector3.up, new Vector3(0.707f, 0.707f, 0f), new Vector3(-0.707f, 0.707f, 0f), new Vector3(0f, 0.707f, 0.707f), new Vector3(0f, 0.707f, -0.707f),
            Vector3.right, Vector3.left, Vector3.forward, Vector3.back,
            new Vector3(0.707f, 0f, 0.707f), new Vector3(-0.707f, 0f, 0.707f), new Vector3(0.707f, 0f, -0.707f), new Vector3(-0.707f, 0f, -0.707f),
        };
        int _wedgeFails;

        Vector3 Resolve(Vector3 a, Vector3 b, float r, float clearance, int mask, out int worstGroup, out Vector3 worstN)
        {
            Vector3 off = Vector3.zero;
            worstGroup = -1;
            worstN = Vector3.zero;
            float worst = 0f;
            float reach = 0.5f * Vector3.Distance(a, b) + r;
            for (int it = 0; it < 6; it++)
            {
                bool any = false;
                Vector3 mid = (a + b) * 0.5f + off;
                for (int i = 0; i < _parts.Count; i++)
                {
                    var p = _parts[i];
                    if (Ignored(p) || (mask & (1 << p.group)) == 0 || Far(p, mid, reach, clearance) || (_hardPass && Time.time - p.onSince < NewGrace)) continue;
                    float d = Gap(p, a + off, b + off, r, out Vector3 n) + Slack(p.group, clearance);
                    if (d >= clearance) continue;
                    float need = clearance - d;
                    off += n * need;
                    mid += n * need;
                    any = true;
                    if (need > worst) { worst = need; worstGroup = p.group; worstN = n; }
                }
                if (!any) break;
            }
            // wedged (a crevice between the table, the hive and the rocket is narrower than he is): pushing out of one
            // pushes him into the next, so he rises out of it instead
            if (MinGap(a + off, b + off, r, mask, clearance, out _) < clearance - 1e-4f)
            {
                // nearest clear spot, up first (rarely happens: no allocation, a fixed set of directions)
                float dist = 0.08f * Mathf.Max(_size, 2f * r, 0.02f);
                bool found = false;
                for (int k = 0; k < 14 && !found; k++, dist *= 1.35f) // out to ~4 body sizes
                    for (int d = 0; d < WedgeDirs.Length; d++)
                    {
                        Vector3 t = off + WedgeDirs[d] * dist;
                        if (MinGap(a + t, b + t, r, mask, clearance, out _) >= clearance - 1e-4f) { off = t; found = true; break; }
                    }
                if (!found) _wedgeFails++;
            }
            return off;
        }

        /// <summary>How much less than `clearance` this group needs (the rocket while he lands on its seat).</summary>
        float Slack(int group, float clearance) => group == (int)Group.Rocket && _rocketClr < clearance ? clearance - Mathf.Max(0f, _rocketClr) : 0f;

        /// <summary>
        /// What he may not sink below: the meadow floor (a plane at its height), the real table top from the room scan (a
        /// block down to the floor), or, before either is known, a table-sized block under the rig's anchor.
        /// </summary>
        bool SurfaceBox(out BodyMath.Obb box)
        {
            box = default;
            box.ax = Vector3.right; box.ay = Vector3.up; box.az = Vector3.forward;
            if (VirtualBeeSwarm.Bloom.MeadowLayout.Active)
            {
                Vector3 at = _anchor != null ? _anchor.position : transform.position;
                box.c = new Vector3(at.x, VirtualBeeSwarm.Bloom.MeadowLayout.FloorY - 1f, at.z);
                box.h = new Vector3(60f, 1f, 60f);
                return true;
            }
            var rig = VirtualBeeSwarm.AR.ArRig.Instance;
            if (rig != null && rig.Room.HasTable)
            {
                var t = rig.Room.Table;
                float depth = Mathf.Max(0.05f, t.Y - rig.Room.FloorY);
                box.ax = t.rotation * Vector3.right; box.az = t.rotation * Vector3.forward;
                box.c = t.center + Vector3.down * (0.5f * depth);
                box.h = new Vector3(t.half.x, 0.5f * depth, t.half.y);
                return true;
            }
            if (_anchor == null) return false;
            return BodyMath.Obb.From(_anchor.localToWorldMatrix, new Vector3(0f, -0.25f, 0.1f), new Vector3(0.7f, 0.25f, 0.45f), out box);
        }

        /// <summary>The smallest gap to any part in mask (and which group).</summary>
        float MinGap(Vector3 a, Vector3 b, float r, int mask, float cutoff, out int group)
        {
            group = -1;
            float best = cutoff;
            Vector3 mid = (a + b) * 0.5f;
            float reach = 0.5f * Vector3.Distance(a, b) + r;
            for (int i = 0; i < _parts.Count; i++)
            {
                var p = _parts[i];
                if (Ignored(p) || (mask & (1 << p.group)) == 0 || Far(p, mid, reach, best) || (_hardPass && Time.time - p.onSince < NewGrace)) continue;
                float d = Gap(p, a, b, r, out _) + Slack(p.group, cutoff);
                if (d < best) { best = d; group = p.group; }
            }
            return best;
        }

        /// <summary>World AABB of the parts in `mask` that come within `near` of the segment p-q (the thing to go around).</summary>
        bool BoundsNear(Vector3 p, Vector3 q, float near, int mask, out Bounds bounds, bool growCluster)
        {
            bounds = default;
            bool any = false;
            Vector3 pq = q - p;
            float l2 = Mathf.Max(1e-8f, pq.sqrMagnitude);
            for (int i = 0; i < _parts.Count; i++)
            {
                var part = _parts[i];
                if (Ignored(part) || part.kind != Kind.Mesh || (mask & (1 << part.group)) == 0) continue;
                if (part.group == (int)Group.RoomWall) continue; // [RoomAware] hook: a wall's axis box would swallow the room
                float t = Mathf.Clamp01(Vector3.Dot(part.box.c - p, pq) / l2);
                if ((p + pq * t - part.box.c).sqrMagnitude > Sq(part.bound + near)) continue;
                var bb = new Bounds(part.box.c, 2f * part.box.AabbExtents);
                if (!any) { bounds = bb; any = true; } else bounds.Encapsulate(bb);
            }
            if (!any || !growCluster) return any;
            // everything touching that box too (the whole hive, not only the roof the path grazed)
            for (int pass = 0; pass < 2; pass++)
            {
                var grow = bounds;
                grow.Expand(near);
                for (int i = 0; i < _parts.Count; i++)
                {
                    var part = _parts[i];
                    if (Ignored(part) || part.kind != Kind.Mesh || (mask & (1 << part.group)) == 0) continue;
                    if (part.group == (int)Group.Room || part.group == (int)Group.RoomWall) continue; // [RoomAware] hook: furniture against a wall would grow the detour to the whole room
                    var bb = new Bounds(part.box.c, 2f * part.box.AabbExtents);
                    if (grow.Intersects(bb)) bounds.Encapsulate(bb);
                }
            }
            return true;
        }
    }
}
