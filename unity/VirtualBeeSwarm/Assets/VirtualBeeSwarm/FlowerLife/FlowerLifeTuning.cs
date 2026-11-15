using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.FlowerLife
{
    /// <summary>
    /// The flower life switches (tuning.json, same files and order as ArTuning: StreamingAssets/tuning.json off Android,
    /// then persistentDataPath/tuning.json), then env VBS_FLOWERLIFE_FLAGS="flowerlife.density=0.5,flowerlife.bees=0".
    ///   flowerlife.on       true   everything here (false: nothing is built or drawn)
    ///   flowerlife.density  1      0..2 (0.75 on the headset); 1 = ~280 plants around the hive, ~480 on the floor, ~22 on the window sill
    ///   flowerlife.floor    true   flowers and grass around the hive across the real floor (sparse and drooped at the start)
    ///   flowerlife.sill     true   flowers spilling out along the bottom of the meadow window
    ///   flowerlife.bees     8      bees that leave the hive to visit healthy blooms (0 = none; comfort mode: half)
    ///   flowerlife.petals   true   petals and pollen drifting in from the window and popping off blooms that perk up
    ///   flowerlife.lean     1      0..2 how far the heads lean toward the player
    ///   flowerlife.art      auto   auto: the art lane's bloom kit where it is in Resources; off: the built-in flowers
    /// </summary>
    public static class FlowerLifeTuning
    {
        public static bool On = true;
        public static float Density = 1f;
        public static bool Floor = true;
        public static bool Sill = true;
        public static int Bees = 6;
        public static bool Petals = true;
        public static float Lean = 1f;
        public static bool Art = true;
        public static string LoadedFrom = "defaults";

        static bool _loaded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _loaded = false;
            Defaults();
        }

        static void Defaults()
        {
            On = true;
#if UNITY_ANDROID && !UNITY_EDITOR
            Density = 0.75f; // the headset: about 3/4 of the desktop garden
#else
            Density = 1f;
#endif
            Floor = true;
            Sill = true;
            Bees = 8;
            Petals = true;
            Lean = 1f;
            Art = true;
            LoadedFrom = "defaults";
        }

        public static void EnsureLoaded()
        {
            if (!_loaded) Load();
        }

        public static void Load()
        {
            _loaded = true;
            Defaults();
            var from = new List<string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            TryFile(Path.Combine(Application.streamingAssetsPath, "tuning.json"), from);
#endif
            TryFile(Path.Combine(Application.persistentDataPath, "tuning.json"), from);
            string env = null;
            try { env = Environment.GetEnvironmentVariable("VBS_FLOWERLIFE_FLAGS"); } catch (Exception) { }
            if (!string.IsNullOrEmpty(env))
            {
                int n = 0;
                foreach (var pair in env.Split(','))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0 && Apply(pair.Substring(0, eq).Trim(), pair.Substring(eq + 1))) n++;
                }
                from.Add("env VBS_FLOWERLIFE_FLAGS (" + n + ")");
            }
            if (from.Count > 0) LoadedFrom = string.Join(" + ", from);
        }

        static void TryFile(string path, List<string> from)
        {
            try
            {
                if (!File.Exists(path)) return;
                var flat = new Dictionary<string, string>();
                ArTuning.FlatJson.Parse(File.ReadAllText(path), flat);
                int n = 0;
                foreach (var kv in flat)
                    if (kv.Key.StartsWith("flowerlife.", StringComparison.Ordinal) && Apply(kv.Key, kv.Value)) n++;
                if (n > 0) from.Add(path + " (" + n + " flowerlife keys)");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VBS][FlowerLife] tuning: could not read " + path + ": " + e.Message);
            }
        }

        /// <summary>Live override, e.g. Set("flowerlife.density", "0.5"). False for an unknown key or a bad value.</summary>
        public static bool Set(string key, string value)
        {
            EnsureLoaded();
            if (key == null) return false;
            if (!key.StartsWith("flowerlife.", StringComparison.Ordinal)) key = "flowerlife." + key;
            return Apply(key, value);
        }

        static bool Apply(string key, string v)
        {
            v = (v ?? "").Trim().Trim('"');
            switch (key)
            {
                case "flowerlife.on": return Bool(v, ref On);
                case "flowerlife.floor": return Bool(v, ref Floor);
                case "flowerlife.sill": return Bool(v, ref Sill);
                case "flowerlife.petals": return Bool(v, ref Petals);
                case "flowerlife.density": return Num(v, 0f, 2f, ref Density);
                case "flowerlife.lean": return Num(v, 0f, 2f, ref Lean);
                case "flowerlife.bees":
                {
                    float f = Bees;
                    if (!Num(v, 0f, FlowerBees.Max, ref f)) return false;
                    Bees = Mathf.RoundToInt(f);
                    return true;
                }
                case "flowerlife.art":
                    switch (v.ToLowerInvariant())
                    {
                        case "auto": case "on": case "true": case "1": Art = true; return true;
                        case "off": case "false": case "0": Art = false; return true;
                        default: return false;
                    }
                default: return false;
            }
        }

        static bool Bool(string v, ref bool into)
        {
            switch (v.ToLowerInvariant())
            {
                case "1": case "on": case "true": case "yes": into = true; return true;
                case "0": case "off": case "false": case "no": into = false; return true;
                default: return false;
            }
        }

        static bool Num(string v, float min, float max, ref float into)
        {
            if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return false;
            into = Mathf.Clamp(f, min, max);
            return true;
        }

        public static string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            return "on=" + On + " density=" + Density.ToString("0.##", c) + " floor=" + Floor + " sill=" + Sill + " bees=" + Bees +
                   " petals=" + Petals + " lean=" + Lean.ToString("0.##", c) + " art=" + (Art ? "auto" : "off") + " (from " + LoadedFrom + ")";
        }
    }
}
