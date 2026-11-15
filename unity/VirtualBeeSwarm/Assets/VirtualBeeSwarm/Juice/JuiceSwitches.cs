using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.Juice
{
    /// <summary>
    /// tuning.json switches for the juice lane (same files as GameTuning): juice.on, juice.hex, juice.stamps,
    /// juice.sting, juice.window, juice.beeswax, juice.beats, juice.captions. All default on. Env VBS_JUICE="off" or
    /// "hex=0,window=0" wins (smoke / capture A-B runs).
    /// </summary>
    public static class JuiceSwitches
    {
        public static bool On = true, Hex = true, Stamps = true, Sting = true, Window = true, Beeswax = true, Beats = true, Captions = true;

        public static void Load()
        {
            var flat = new Dictionary<string, string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            Read(Path.Combine(Application.streamingAssetsPath, "tuning.json"), flat);
#endif
            Read(Path.Combine(Application.persistentDataPath, "tuning.json"), flat);
            foreach (var kv in flat)
                if (kv.Key.StartsWith("juice.", StringComparison.Ordinal)) Set(kv.Key.Substring(6), kv.Value);
            string env = null;
            try { env = Environment.GetEnvironmentVariable("VBS_JUICE"); } catch (Exception) { }
            if (string.IsNullOrEmpty(env)) return;
            env = env.Trim();
            if (env == "off" || env == "0" || env == "false") { On = false; return; }
            foreach (var pair in env.Split(','))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0) Set(pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim());
            }
        }

        static void Read(string path, Dictionary<string, string> into)
        {
            try { if (File.Exists(path)) ArTuning.FlatJson.Parse(File.ReadAllText(path), into); }
            catch (Exception e) { Debug.LogWarning("[VBS][Juice] tuning: could not read " + path + ": " + e.Message); }
        }

        static void Set(string key, string value)
        {
            bool v = !(value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase));
            switch (key)
            {
                case "on": On = v; break;
                case "hex": Hex = v; break;
                case "stamps": Stamps = v; break;
                case "sting": Sting = v; break;
                case "window": Window = v; break;
                case "beeswax": Beeswax = v; break;
                case "beats": Beats = v; break;
                case "captions": Captions = v; break;
            }
        }

        public static string Describe() =>
            "hex=" + Hex + " stamps=" + Stamps + " sting=" + Sting + " window=" + Window + " beeswax=" + Beeswax + " beats=" + Beats + " captions=" + Captions;
    }
}
