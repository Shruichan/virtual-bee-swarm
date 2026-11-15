using System.Collections.Generic;
using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>A flat rectangle in the room (a wall, a window, a door): right is horizontal, normal points into the room.</summary>
    public struct BloomRect
    {
        public Vector3 center, right, up, normal;
        public Vector2 half;

        public bool Contains(Vector3 p, float pad, float depth)
        {
            Vector3 d = p - center;
            return Mathf.Abs(Vector3.Dot(d, normal)) <= depth && Mathf.Abs(Vector3.Dot(d, right)) <= half.x + pad &&
                   Mathf.Abs(Vector3.Dot(d, up)) <= half.y + pad;
        }
    }

    /// <summary>A furniture / pillar volume, yaw only. half.y is the vertical half height.</summary>
    public struct BloomBox
    {
        public Vector3 center;
        public Quaternion rotation;
        public Vector3 half;
        public bool pillar;

        public bool Contains(Vector3 p, float pad)
        {
            Vector3 l = Quaternion.Inverse(rotation) * (p - center);
            return Mathf.Abs(l.x) <= half.x + pad && Mathf.Abs(l.z) <= half.z + pad;
        }
    }

    /// <summary>The room as a scene source sees it (MRUK on the headset, BloomFakeRoom on the desktop). World space.</summary>
    public sealed class BloomScene
    {
        public string Name = "";
        public float FloorY;
        public readonly List<Vector2> FloorPolygon = new List<Vector2>(); // xz; empty = no floor outline
        public readonly List<BloomRect> Walls = new List<BloomRect>();
        public readonly List<BloomRect> Openings = new List<BloomRect>(); // windows and doors: no vines
        public readonly List<BloomBox> Volumes = new List<BloomBox>();     // furniture + pillars: no flowers

        public void Clear(string name)
        {
            Name = name;
            FloorY = 0f;
            FloorPolygon.Clear();
            Walls.Clear();
            Openings.Clear();
            Volumes.Clear();
        }

        public bool InFloor(Vector3 p)
        {
            int n = FloorPolygon.Count;
            if (n < 3) return true;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = FloorPolygon[i], b = FloorPolygon[j];
                if ((a.y > p.z) != (b.y > p.z) && p.x < (b.x - a.x) * (p.z - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        /// <summary>Flat distance from p to the nearest floor outline edge (the walls); huge with no outline.</summary>
        public float EdgeDist(Vector3 p)
        {
            int n = FloorPolygon.Count;
            if (n < 3) return float.MaxValue;
            var q = new Vector2(p.x, p.z);
            float best = float.MaxValue;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = FloorPolygon[j], ab = FloorPolygon[i] - a;
                float t = Mathf.Clamp01(Vector2.Dot(q - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
                best = Mathf.Min(best, (a + ab * t - q).sqrMagnitude);
            }
            return Mathf.Sqrt(best);
        }

        public bool InVolume(Vector3 p, float pad)
        {
            for (int i = 0; i < Volumes.Count; i++)
                if (Volumes[i].Contains(p, pad)) return true;
            return false;
        }
    }

    /// <summary>A room scan the bloom can read (Meta: Bloom/Meta/MrukBloomSource; desktop: BloomFakeRoom).</summary>
    public interface IBloomSceneSource
    {
        string Name { get; }
        string Status { get; }
        /// <summary>Called once in `pre`: start loading (MRUK: LoadSceneFromDevice, no Space Setup prompt).</summary>
        void Begin();
        /// <summary>Fills the scene if it is loaded and has a room. False = nothing usable (no scan at the venue).</summary>
        bool TryFill(BloomScene scene);
    }

    /// <summary>Registry: VirtualBeeSwarm.Bloom.Meta sets Scene before the first scene loads (Android / Windows / macOS only).</summary>
    public static class BloomPlatform
    {
        public static IBloomSceneSource Scene { get; set; }
    }

    public enum VineKind { Wall, Pillar, Leg }

    /// <summary>Where one vine climbs. Wall: up the plane from root. Pillar / Leg: a spiral round the box (center at the
    /// floor, rotation, half = box half size in x/z).</summary>
    public struct VinePath
    {
        public VineKind kind;
        public Vector3 root, normal, tangent;
        public float height;
        public Vector3 center;
        public Quaternion rotation;
        public Vector2 half;
        public string tier;
        /// <summary>Spirals: start angle (rad). Walls: +1 / -1 drifts and branches only along +/- tangent (the window's
        /// flanking vines lean away from the pane), 0 = either way.</summary>
        public float phase, away;
    }

    /// <summary>What the bloom builds from: the fallback chain, picked once per build.
    ///   floor: scene source (MRUK / desktop fake room) -> depth probes from `pre` -> a ring round the table (RoomModel)
    ///   vines: union of scene walls + pillars, probe wall columns, and RoomModel (both sides of the window, 4 table legs)
    /// Exclusions everywhere: the table + 5 cm (no flowers on or under it), the keep-out footprints, the player's feet
    /// (0.5 m), behind the window wall, more than 4 m from the hive, inside scene furniture; vines stop short of the
    /// window pane (+0.15 m).</summary>
    public sealed class BloomSurfaces
    {
        public const float MaxFromHive = 4f;
        public const float FeetRadius = 0.5f;
        /// <summary>The probe floor tier needs this many clumps, else the ring (a short `pre` sees only a few patches).</summary>
        public const int MinProbePicks = 40;
        /// <summary>Clump centres keep this far off the walls, so a clump's outer flowers don't stand inside one.</summary>
        public const float WallGap = 0.18f;
        public const int MaxVines = 48;
        public const int SpiralsPerPillar = 3, MaxProbeVines = 6;
        /// <summary>The window's flanking vines root this far outside the (moon-sized) pane rectangle.</summary>
        public const float WindowFlank = 0.3f;

        public string FloorTier = "none";
        public string VineTiers = "none";
        public float FloorY;
        public Vector3 HiveFloor;
        public bool HasWindow;
        public BloomRect Window; // exclusion (pane x1.5 when grown, + 0.15 m)
        public System.Predicate<Vector3> WallSightlineKeepOut;
        public readonly List<Vector3> Floor = new List<Vector3>(BloomTuning.MaxClumps);
        public readonly List<VinePath> Vines = new List<VinePath>(MaxVines);
        public int Candidates, SceneCount, ProbeCount, RingCount;
        public float Spacing;
        /// <summary>Where the wave starts: the foot of the window wall right behind the hive (centre of view), else the
        /// hive's floor point.</summary>
        public Vector3 WaveOrigin;
        /// <summary>Flat metres from WaveOrigin where the wave starts (just inside the nearest flower).</summary>
        public float WaveStart;

        /// <summary>Seconds after the wave start when the wave reaches p (before any per-part jitter).</summary>
        public float WaveArrival(Vector3 p, float wave) => Mathf.Max(0f, FlatDist(p, WaveOrigin) - WaveStart) / Mathf.Max(0.1f, wave);

        public struct Inputs
        {
            public RoomModel room;
            public BloomScene scene; // null: no scene source this run
            public BloomProbes probes;
            public Vector3 hive, player;
            public bool hasWindow;
            public BloomRect window;
            public int budget;
            public float spacing;
            public bool vines;
            public int seed;
        }

        public static BloomSurfaces Build(Inputs inp)
        {
            var s = new BloomSurfaces();
            var room = inp.room;
            bool sceneOk = inp.scene != null && inp.scene.FloorPolygon.Count >= 3;
            bool probesOk = inp.probes != null && inp.probes.FloorCellCount >= BloomProbes.MinFloorCells;
            s.FloorY = sceneOk ? inp.scene.FloorY : probesOk ? inp.probes.FloorY : room != null ? room.FloorY : 0f;
            s.HiveFloor = new Vector3(inp.hive.x, s.FloorY, inp.hive.z);
            s.HasWindow = inp.hasWindow;
            s.Window = inp.window;
            s.WaveOrigin = s.HiveFloor;
            if (room != null && room.HasWall)
            {
                var ww = room.WindowWall;
                Vector3 o = inp.hive + ww.normal * (Vector3.Dot(ww.point - inp.hive, ww.normal) + WallGap);
                s.WaveOrigin = new Vector3(o.x, s.FloorY, o.z);
            }

            // ---- floor: candidates on a fine jittered 0.1 m grid; each tier keeps its own, then a greedy blue-noise
            // pass (seeded order) at the spacing that fits the budget into that tier's area: r = sqrt(0.55 A / N)
            var rnd = new System.Random(inp.seed);
            const float cell = 0.1f;
            int half = Mathf.CeilToInt(MaxFromHive / cell);
            var cand = new List<Vector3>(4 * half * half);
            for (int iz = -half; iz <= half; iz++)
            for (int ix = -half; ix <= half; ix++)
            {
                float x = inp.hive.x + (ix + (float)rnd.NextDouble()) * cell;
                float z = inp.hive.z + (iz + (float)rnd.NextDouble()) * cell;
                var p = new Vector3(x, s.FloorY, z);
                if (Allowed(p, inp, s)) cand.Add(p);
            }
            for (int i = cand.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (cand[i], cand[j]) = (cand[j], cand[i]);
            }
            s.Candidates = cand.Count;
            float maxSpacing = Mathf.Max(0.13f, inp.spacing);
            int budget = Mathf.Max(1, inp.budget);

            List<Vector3> pick = null;
            if (sceneOk)
            {
                var scene = inp.scene;
                pick = Spread(cand, cell, budget, maxSpacing, p => scene.InFloor(p) && !scene.InVolume(p, WallGap) && scene.EdgeDist(p) >= WallGap,
                    out s.Spacing);
                s.SceneCount = pick.Count;
                if (pick.Count >= 20) s.FloorTier = "scene (" + scene.Name + ")";
                else pick = null;
            }
            if (pick == null && probesOk)
            {
                var probes = inp.probes;
                pick = Spread(cand, cell, budget, maxSpacing, p => probes.IsFloor(p), out s.Spacing);
                s.ProbeCount = pick.Count;
                if (pick.Count >= MinProbePicks) s.FloorTier = "probes (" + probes.FloorCellCount + " cells)";
                else pick = null;
            }
            if (pick == null)
            {
                Vector3 h = inp.hive;
                pick = Spread(cand, cell, budget, maxSpacing, p => { float d = FlatDist(p, h); return d >= 0.6f && d <= 2.4f; }, out s.Spacing);
                s.RingCount = pick.Count;
                s.FloorTier = "room ring";
            }
            // nearest the hive first, with a ragged edge; keep the budget
            var keys = new float[pick.Count];
            for (int i = 0; i < pick.Count; i++) keys[i] = FlatDist(pick[i], inp.hive) + (float)rnd.NextDouble() * 0.45f;
            var order = new int[pick.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            System.Array.Sort(keys, order);
            for (int i = 0; i < order.Length && s.Floor.Count < inp.budget; i++) s.Floor.Add(pick[order[i]]);
            // the wave starts at the flower nearest the origin (no dead second before the first bloom)
            float near = float.MaxValue;
            for (int i = 0; i < s.Floor.Count; i++) near = Mathf.Min(near, FlatDist(s.Floor[i], s.WaveOrigin));
            s.WaveStart = s.Floor.Count > 0 ? Mathf.Clamp(near - 0.1f, 0f, 1.5f) : 0f;

            // ---- vines
            if (inp.vines) BuildVines(inp, s, sceneOk, rnd);
            return s;
        }

        static bool Allowed(Vector3 p, Inputs inp, BloomSurfaces s)
        {
            if (FlatDist(p, inp.hive) > MaxFromHive) return false;
            if (FlatDist(p, inp.player) < FeetRadius + 0.05f) return false;
            var room = inp.room;
            if (room != null)
            {
                if (room.HasTable && room.InsideTable(p, -0.05f)) return false;
                if (room.InsideFootprint(p, 0.05f)) return false;
                if (room.HasWall && Vector3.Dot(p - room.WindowWall.point, room.WindowWall.normal) < WallGap) return false;
            }
            // clump centres keep WallGap off every volume (pillars, furniture) and every solid probe wall column
            if (inp.scene != null && inp.scene.Volumes.Count > 0 && inp.scene.InVolume(p, WallGap)) return false;
            if (inp.probes != null && inp.probes.NearWall(p, WallGap)) return false;
            return true;
        }

        static List<Vector3> Spread(List<Vector3> cand, float cell, int budget, float maxSpacing, System.Func<Vector3, bool> keep,
            out float r)
        {
            var mine = new List<Vector3>(cand.Count);
            for (int i = 0; i < cand.Count; i++) if (keep(cand[i])) mine.Add(cand[i]);
            r = Mathf.Clamp(Mathf.Sqrt(0.55f * mine.Count * cell * cell / budget), 0.13f, maxSpacing);
            var kept = new List<Vector3>(512);
            var grid = new Dictionary<long, List<Vector3>>();
            float r2 = r * r;
            for (int i = 0; i < mine.Count; i++)
            {
                Vector3 p = mine[i];
                int cx = Mathf.FloorToInt(p.x / r), cz = Mathf.FloorToInt(p.z / r);
                bool ok = true;
                for (int dz = -1; dz <= 1 && ok; dz++)
                for (int dx = -1; dx <= 1 && ok; dx++)
                {
                    if (!grid.TryGetValue(Key(cx + dx, cz + dz), out var list)) continue;
                    for (int k = 0; k < list.Count; k++)
                    {
                        float ex = list[k].x - p.x, ez = list[k].z - p.z;
                        if (ex * ex + ez * ez < r2) { ok = false; break; }
                    }
                }
                if (!ok) continue;
                long key = Key(cx, cz);
                if (!grid.TryGetValue(key, out var l2)) grid[key] = l2 = new List<Vector3>(2);
                l2.Add(p);
                kept.Add(p);
            }
            return kept;
        }

        static void BuildVines(Inputs inp, BloomSurfaces s, bool sceneOk, System.Random rnd)
        {
            var room = inp.room;
            var all = new List<VinePath>(64);
            var tiers = new List<string>(3);
            float floorY = s.FloorY;

            // the 4 table legs: only where a scan / the fake room has a table volume (or bloom.legs), never on a guess
            bool tableVolume = false;
            if (sceneOk && room != null && room.HasTable)
                for (int i = 0; i < inp.scene.Volumes.Count && !tableVolume; i++)
                    tableVolume = !inp.scene.Volumes[i].pillar && inp.scene.Volumes[i].Contains(room.Table.center, 0.1f);
            if (room != null && room.HasTable && (tableVolume || BloomTuning.Legs))
            {
                float h = room.Table.Y - floorY - 0.03f;
                if (h > 0.3f)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        float lx = (i % 2 == 0 ? -1f : 1f) * (room.Table.half.x - 0.07f);
                        float lz = (i < 2 ? -1f : 1f) * (room.Table.half.y - 0.07f);
                        Vector3 c = room.TableToWorld(lx, lz);
                        c.y = floorY;
                        all.Add(new VinePath
                        {
                            kind = VineKind.Leg, root = c, center = c, rotation = room.Table.rotation, half = new Vector2(0.025f, 0.025f),
                            height = h, normal = Vector3.up, tangent = room.Table.rotation * Vector3.right, tier = "legs",
                        });
                    }
                    tiers.Add("table legs");
                }
            }
            if (sceneOk)
            {
                var sc = inp.scene;
                int before = all.Count;
                for (int w = 0; w < sc.Walls.Count; w++)
                {
                    var wall = sc.Walls[w];
                    float top = wall.center.y + wall.half.y;
                    for (float x = -wall.half.x + 0.3f; x <= wall.half.x - 0.3f; x += 0.38f)
                    {
                        float jx = x + ((float)rnd.NextDouble() - 0.5f) * 0.14f;
                        Vector3 root = wall.center + wall.right * jx;
                        root.y = floorY;
                        if (InDoor(sc, root)) continue;
                        all.Add(WallVine(root, wall.normal, wall.right, Mathf.Min(1.7f + (float)rnd.NextDouble() * 0.8f, top - floorY - 0.15f), "scene"));
                    }
                }
                for (int v = 0; v < sc.Volumes.Count; v++)
                {
                    var b = sc.Volumes[v];
                    if (!b.pillar) continue;
                    Vector3 c = b.center;
                    c.y = floorY;
                    float ph = (float)rnd.NextDouble() * 6.28f;
                    for (int k = 0; k < SpiralsPerPillar; k++) // floor to top, evenly round the pillar
                        all.Add(new VinePath
                        {
                            kind = VineKind.Pillar, root = c, center = c, rotation = b.rotation, half = new Vector2(b.half.x, b.half.z),
                            height = Mathf.Min(2.6f, b.center.y + b.half.y - floorY - 0.12f), normal = Vector3.up,
                            tangent = b.rotation * Vector3.right, tier = "scene", phase = ph + k * 6.2832f / SpiralsPerPillar,
                        });
                }
                if (all.Count > before) tiers.Add("scene (" + sc.Name + ")");
            }
            if (inp.probes != null && BloomTuning.ProbeVines && inp.probes.WallColumnCount > 0)
            {
                int before = all.Count;
                foreach (var col in inp.probes.WallColumns())
                {
                    if (all.Count - before >= MaxProbeVines) break;
                    Vector3 root = new Vector3(col.point.x, floorY, col.point.z) + col.normal * 0.02f;
                    if (room != null && room.HasTable && room.InsideTable(root, -0.15f)) continue;
                    Vector3 right = Vector3.Cross(Vector3.up, col.normal).normalized;
                    all.Add(WallVine(root, col.normal, right, 1.4f + (float)rnd.NextDouble() * 0.6f, "probes"));
                }
                if (all.Count > before) tiers.Add("probes");
            }
            if (room != null && room.HasWall)
            {
                var w = room.WindowWall;
                Vector3 c = inp.hasWindow ? inp.window.center : w.point;
                float off = inp.hasWindow ? inp.window.half.x + WindowFlank : w.halfWidth * 0.6f;
                float wallTop = Mathf.Min(2.7f, room.CeilingOrDefault - floorY - 0.15f);
                for (int side = -1; side <= 1; side += 2)
                for (int k = 0; k < 2; k++) // two vines frame each side of the window, leaning away from it
                {
                    Vector3 root = c + w.right * (side * (off + k * 0.3f));
                    root += w.normal * Vector3.Dot(w.point - root, w.normal); // onto the plane
                    root.y = floorY;
                    var v = WallVine(root + w.normal * 0.02f, w.normal, w.right * side, wallTop - k * 0.35f - (float)rnd.NextDouble() * 0.2f, "window");
                    v.away = 1f;
                    all.Add(v);
                }
                tiers.Add("window wall");
            }

            // keep: the window's vines first (the hero shot), then pillars, then walls nearest the hive, legs last (the vine
            // mesh stops at its triangle budget in this order); no two wall roots closer than 0.3 m, nothing past 4.5 m
            var idx = new List<int>(all.Count);
            for (int i = 0; i < all.Count; i++) idx.Add(i);
            System.Func<VinePath, int> rank = v => v.tier == "window" ? 0 : v.kind == VineKind.Pillar ? 1 : v.kind == VineKind.Wall ? 2 : 3;
            idx.Sort((a, b) =>
            {
                int ka = rank(all[a]), kb = rank(all[b]);
                if (ka != kb) return ka.CompareTo(kb);
                int d = FlatDist(all[a].root, inp.hive).CompareTo(FlatDist(all[b].root, inp.hive));
                return d != 0 ? d : a.CompareTo(b);
            });
            foreach (int i in idx)
            {
                if (s.Vines.Count >= MaxVines) break;
                var v = all[i];
                if (FlatDist(v.root, inp.hive) > MaxFromHive + 0.5f) continue;
                if (v.kind == VineKind.Wall && FlatDist(v.root, inp.player) < 0.7f) continue;
                float gap = v.kind == VineKind.Wall ? 0.28f : 0.25f;
                bool clash = false;
                if (v.kind != VineKind.Pillar) // a pillar's spirals share one root
                    for (int k = 0; k < s.Vines.Count && !clash; k++)
                        if (FlatDist(s.Vines[k].root, v.root) < gap && s.Vines[k].kind == v.kind) clash = true;
                if (!clash) s.Vines.Add(v);
            }
            s.VineTiers = tiers.Count > 0 ? string.Join(" + ", tiers) : "none";
        }

        static VinePath WallVine(Vector3 root, Vector3 normal, Vector3 right, float height, string tier) => new VinePath
        {
            kind = VineKind.Wall, root = root, normal = normal, tangent = right, height = Mathf.Max(0.3f, height), center = root,
            rotation = Quaternion.identity, tier = tier,
        };

        static bool InDoor(BloomScene sc, Vector3 root)
        {
            for (int i = 0; i < sc.Openings.Count; i++)
            {
                var o = sc.Openings[i];
                if (o.center.y - o.half.y > sc.FloorY + 0.4f) continue; // a window: the vine stops below it instead
                if (o.Contains(new Vector3(root.x, o.center.y, root.z), 0.15f, 0.3f)) return true;
            }
            return false;
        }

        public static float FlatDist(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x, z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }

        public static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
