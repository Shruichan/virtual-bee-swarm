using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.Title
{
    /// <summary>
    /// title.globe (tuning.json, default on): the Orbis globe beside the title logo, and the dive into it on START.
    /// title.globeSize: its diameter in metres (default 0.42). Needs title.on.
    /// title.wall (default on): inside the globe, the documentary wall of scrolling bee monitors (TitleWall); off = the
    /// inside-out bee flight. Env VBS_TITLE_WALL (0/1) wins. Comfort mode never wraps the player, so never shows it.
    /// Env VBS_TITLE_GLOBE (0/1) wins, then StreamingAssets/tuning.json off-Android, then persistentDataPath/tuning.json.
    /// </summary>
    public static class GlobeFlags
    {
        static bool _read, _on = true, _wall = true;
        static float _size = 0.42f;
        public static string Source { get; private set; } = "default";

        public static bool On
        {
            get { Read(); return _on; }
            set { Read(); _on = value; Source = "set by code"; }
        }

        public static float Size
        {
            get { Read(); return _size; }
        }

        public static bool Wall
        {
            get { Read(); return _wall; }
            set { Read(); _wall = value; }
        }

        public static void Reload() => _read = false;

        static void Read()
        {
            if (_read) return;
            _read = true;
            _on = true;
            _wall = true;
            _size = 0.42f;
            Source = "default";
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
                    if (flat.TryGetValue("title.globe", out var v) && TryBool(v, out bool b)) { _on = b; Source = p; }
                    if (flat.TryGetValue("title.wall", out var w) && TryBool(w, out bool wb)) _wall = wb;
                    if (flat.TryGetValue("title.globeSize", out var s) &&
                        float.TryParse(s.Trim().Trim('"'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                        _size = Mathf.Clamp(f, 0.2f, 1f);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[VBS][Globe] tuning: could not read " + p + ": " + ex.Message);
                }
            }
            string env = null;
            try { env = Environment.GetEnvironmentVariable("VBS_TITLE_GLOBE"); } catch (Exception) { }
            if (!string.IsNullOrEmpty(env) && TryBool(env, out bool e)) { _on = e; Source = "env VBS_TITLE_GLOBE"; }
            string wenv = null;
            try { wenv = Environment.GetEnvironmentVariable("VBS_TITLE_WALL"); } catch (Exception) { }
            if (!string.IsNullOrEmpty(wenv) && TryBool(wenv, out bool we)) _wall = we;
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
