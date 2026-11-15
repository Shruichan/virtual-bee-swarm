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

        /// <summary>The static worker mesh wired into the scene by the art-slot pass.</summary>
        internal static Mesh AuthoredWorkerMesh()
        {
            var swarm = UnityEngine.Object.FindAnyObjectByType<SwarmSystem>(FindObjectsInactive.Include);
            return swarm != null ? swarm.WorkerBeeMesh : null;
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
