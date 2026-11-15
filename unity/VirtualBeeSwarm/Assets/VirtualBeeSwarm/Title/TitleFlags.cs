using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.Title
{
    /// <summary>
    /// title.on (tuning.json, default on): the title scene in pre (the logo floating in the room over flowers, Beeswax
    /// bringing the SIGN HERE clipboard). Off: pre as before (a crew of bees parks the clipboard).
    /// Env VBS_TITLE (0/1) wins (smoke runs), then persistentDataPath/tuning.json, then StreamingAssets off-Android.
    /// </summary>
    public static class TitleFlags
    {
        static bool? _on;
        public static string Source { get; private set; } = "default";

        public static bool On
        {
            get
            {
                if (_on == null) _on = Read();
                return _on.Value;
            }
            set { _on = value; Source = "set by code"; }
        }

        /// <summary>Re-read the switch next time (tuning reload, tests).</summary>
        public static void Reload() => _on = null;

        static bool Read()
        {
            Source = "default";
            string env = null;
            try { env = Environment.GetEnvironmentVariable("VBS_TITLE"); } catch (Exception) { }
            if (!string.IsNullOrEmpty(env) && TryBool(env, out bool e)) { Source = "env VBS_TITLE"; return e; }
            bool on = true;
            var paths = new List<string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            paths.Add(Path.Combine(Application.streamingAssetsPath, "tuning.json"));
#endif
            paths.Add(Path.Combine(Application.persistentDataPath, "tuning.json"));
            var flat = new Dictionary<string, string>();
            foreach (var p in paths)
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    flat.Clear();
                    ArTuning.FlatJson.Parse(File.ReadAllText(p), flat);
                    if (flat.TryGetValue("title.on", out var v) && TryBool(v, out bool b)) { on = b; Source = p; }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[VBS][Title] tuning: could not read " + p + ": " + ex.Message);
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
