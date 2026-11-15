using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The swarm bee with Daniel's Hover/Walk animation: Art/BeeVAT/Resources/BeeVAT_Mesh + BeeVAT.mat
    /// (Shaders/BeeVAT.shader, a vertex animation texture), still one instanced draw per pool. Each bee pool calls
    /// <see cref="Apply"/> once after it has its mesh and material; with swarm.vat on (the default) it gets the VAT
    /// mesh and its own copy of the VAT material, otherwise it keeps what it had (the temp bee and its shader flap).
    /// Per-instance _Color, _WingFlap and _EnvironmentDepthBias work as in VirtualBeeSwarm/Unlit.
    /// Switch: env VBS_SWARM_VAT (0/1) wins, then "swarm.vat" (on/off) in tuning.json (persistentDataPath, and
    /// StreamingAssets off-Android), else on.
    /// </summary>
    public static class BeeVat
    {
        public const string MeshResource = "BeeVAT_Mesh";
        public const string MaterialResource = "BeeVAT";

        static bool? _enabled;
        static string _source = "default";
        static bool _loaded;
        static Mesh _mesh;
        static Material _material;

        /// <summary>swarm.vat: env VBS_SWARM_VAT, then tuning.json, else on.</summary>
        public static bool Enabled
        {
            get
            {
                if (_enabled == null) _enabled = ReadFlag(out _source);
                return _enabled.Value;
            }
        }

        /// <summary>Re-read the switch next time (tests, tuning reload).</summary>
        public static void Reload() { _enabled = null; }

        static Mesh _workerRest;
        static Mesh _workerFar;

        /// <summary>Resolve the authored bee independently of scene generation and component Awake order.</summary>
        internal static Mesh AuthoredWorkerMesh(Mesh sceneMesh = null)
        {
            if (sceneMesh != null && sceneMesh.name == "Bee_unit")
            {
                if (_workerRest == null) _workerRest = sceneMesh;
                return sceneMesh;
            }
            if (_workerRest == null) _workerRest = CreateWorkerRestMesh(Resources.Load<Mesh>(MeshResource));
            return _workerRest;
        }

        /// <summary>Copy the authored rest pose and turn its VAT weight channel into shader-flap hinge tags.</summary>
        public static Mesh CreateWorkerRestMesh(Mesh source)
        {
            if (source == null || !source.isReadable) return null;
            var weights = source.uv2;
            if (weights.Length != source.vertexCount) return null;
            var mesh = UnityEngine.Object.Instantiate(source);
            mesh.name = "Bee_unit";

            // VAT UV1 carries the rig's wing weights. Keep the same wing vertices and tag them for GameUnlit.
            var verts = source.vertices;
            var hinges = new Vector2[verts.Length];
            var flags = new Vector2[verts.Length];
            var roots = new[] { new Vector2(float.NegativeInfinity, 0f), new Vector2(float.PositiveInfinity, 0f) };
            for (int i = 0; i < verts.Length; i++)
            {
                if (weights[i].y <= 0.5f) continue;
                var p = verts[i];
                int side = p.x < 0f ? 0 : 1;
                if (Mathf.Abs(p.x) < Mathf.Abs(roots[side].x)) roots[side] = new Vector2(p.x, p.y);
            }
            for (int i = 0; i < verts.Length; i++)
            {
                if (weights[i].y <= 0f) continue;
                int side = verts[i].x < 0f ? 0 : 1;
                if (float.IsInfinity(roots[side].x)) continue;
                hinges[i] = roots[side];
                flags[i] = new Vector2((side == 0 ? -1f : 1f) * Mathf.Clamp01(weights[i].y), 0f);
            }
            mesh.uv2 = hinges;
            mesh.uv3 = flags;
            return mesh;
        }

        /// <summary>Reduce the authored rest mesh once for Quest's high-count pools, keeping its stripes and wings.</summary>
        public static Mesh AuthoredWorkerFarMesh(Mesh source = null)
        {
            if (_workerFar != null) return _workerFar;
            source = AuthoredWorkerMesh(source);
            if (source == null || !source.isReadable) return source;

            var vertices = source.vertices;
            var colors = source.colors32;
            var hinges = source.uv2;
            var wingFlags = source.uv3;
            if (colors.Length != vertices.Length || hinges.Length != vertices.Length || wingFlags.Length != vertices.Length)
                return source;
            // Separate wings and material boundaries, but share cell centers across colors so stripes do not split.
            var cells = new Dictionary<(Vector3Int, int), (Vector3 sum, int count)>();
            var indices = new Dictionary<(Vector3Int, int, Color32), int>();
            var remap = new int[vertices.Length];
            var farColors = new List<Color32>();
            var farHinges = new List<Vector2>();
            var farWings = new List<Vector2>();
            for (int i = 0; i < vertices.Length; ++i)
            {
                int wing = wingFlags[i].x > 0.5f ? 1 : wingFlags[i].x < -0.5f ? -1 : 0;
                Vector3 p = vertices[i];
                Vector3Int cell = Vector3Int.RoundToInt(p / (wing == 0 ? 0.19f : 0.14f));
                cells.TryGetValue((cell, wing), out var center);
                cells[(cell, wing)] = (center.sum + p, center.count + 1);
                var key = (cell, wing, colors[i]);
                if (!indices.TryGetValue(key, out int index))
                {
                    index = indices.Count;
                    indices.Add(key, index);
                    farColors.Add(colors[i]);
                    farHinges.Add(hinges[i]);
                    farWings.Add(wingFlags[i]);
                }
                remap[i] = index;
            }
            var farVertices = new Vector3[indices.Count];
            foreach (var entry in indices)
            {
                var (cell, wing, _) = entry.Key;
                var center = cells[(cell, wing)];
                farVertices[entry.Value] = center.sum / center.count;
            }

            var sourceTriangles = source.triangles;
            var farTriangles = new List<int>(sourceTriangles.Length / 4);
            var seen = new HashSet<Vector3Int>();
            for (int i = 0; i < sourceTriangles.Length; i += 3)
            {
                int a = remap[sourceTriangles[i]], b = remap[sourceTriangles[i + 1]], c = remap[sourceTriangles[i + 2]];
                if (a == b || a == c || b == c) continue;
                int lo = Mathf.Min(a, Mathf.Min(b, c)), hi = Mathf.Max(a, Mathf.Max(b, c));
                if (!seen.Add(new Vector3Int(lo, a + b + c - lo - hi, hi))) continue;
                farTriangles.Add(a);
                farTriangles.Add(b);
                farTriangles.Add(c);
            }
            if (farTriangles.Count == 0) return source;
            _workerFar = new Mesh { name = "Bee_worker_far" };
            _workerFar.vertices = farVertices;
            _workerFar.SetColors(farColors);
            _workerFar.SetUVs(1, farHinges);
            _workerFar.SetUVs(2, farWings);
            _workerFar.SetTriangles(farTriangles, 0);
            _workerFar.RecalculateNormals();
            _workerFar.RecalculateBounds();
            Debug.Log("[VBS][VAT] authored far bee: " + Tris(_workerFar) + " tris from " + Tris(source));
            return _workerFar;
        }

        /// <summary>
        /// Swap a bee pool to the VAT bee when swarm.vat is on and the assets load. Returns true if it swapped.
        /// <paramref name="pool"/> only names the pool in the log.
        /// </summary>
        public static bool Apply(ref Mesh mesh, ref Material material, string pool)
        {
            if (!Enabled)
            {
                Log("swarm.vat=off (" + _source + "): " + pool + " keeps " + (mesh != null ? mesh.name : "no mesh"));
                return false;
            }
            if (!Load())
            {
                Log("swarm.vat=on but Resources/" + MeshResource + " or " + MaterialResource + " is missing or unsupported: " +
                    pool + " keeps " + (mesh != null ? mesh.name : "no mesh"));
                return false;
            }
            mesh = _mesh;
            material = new Material(_material) { name = "BeeVAT (" + pool + ")", enableInstancing = true };
            Log("swarm.vat=on (" + _source + "): " + pool + " -> BeeVAT (" + _mesh.vertexCount + " verts, " + Tris(_mesh) + " tris)");
            return true;
        }

        static bool Load()
        {
            if (_loaded) return _mesh != null && _material != null;
            _loaded = true;
            _mesh = Resources.Load<Mesh>(MeshResource);
            _material = Resources.Load<Material>(MaterialResource);
            if (_mesh == null || _material == null) return false;
            // a real GPU that can't run it (no vertex texture fetch) keeps the old bee; the null device (smoke runs) can't tell
            if (!_material.shader.isSupported && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Debug.LogWarning("[VBS][VAT] " + _material.shader.name + " is not supported on " + SystemInfo.graphicsDeviceName);
                _mesh = null;
                return false;
            }
            return true;
        }

        static int Tris(Mesh m) => m.isReadable ? m.triangles.Length / 3 : (int)(m.GetIndexCount(0) / 3);

        static void Log(string s)
        {
            Debug.Log("[VBS][VAT] " + s);
        }

        static bool ReadFlag(out string source)
        {
            string env = Environment.GetEnvironmentVariable("VBS_SWARM_VAT");
            if (!string.IsNullOrEmpty(env) && TryBool(env, out bool e)) { source = "env VBS_SWARM_VAT"; return e; }
            var flat = new Dictionary<string, string>();
            source = "default";
            bool on = true;
            var paths = new List<string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            paths.Add(Path.Combine(Application.streamingAssetsPath, "tuning.json"));
#endif
            paths.Add(Path.Combine(Application.persistentDataPath, "tuning.json"));
            foreach (var p in paths)
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    flat.Clear();
                    ArTuning.FlatJson.Parse(File.ReadAllText(p), flat);
                    if (flat.TryGetValue("swarm.vat", out var v) && TryBool(v, out bool b)) { on = b; source = p; }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[VBS][VAT] tuning: could not read " + p + ": " + ex.Message);
                }
            }
            return on;
        }

        static bool TryBool(string s, out bool b)
        {
            switch (s.Trim().Trim('"').ToLowerInvariant())
            {
                case "1": case "on": case "true": case "yes": b = true; return true;
                case "0": case "off": case "false": case "no": b = false; return true;
                default: b = true; return false;
            }
        }
    }
}
