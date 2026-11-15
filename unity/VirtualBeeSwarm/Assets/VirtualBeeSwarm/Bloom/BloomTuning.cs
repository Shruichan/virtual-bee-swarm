using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.Bloom
{
    /// <summary>
    /// The room bloom switches (tuning.json, same files and order as ArTuning / BeeVat: StreamingAssets/tuning.json off
    /// Android, then persistentDataPath/tuning.json), then env VBS_BLOOM_FLAGS="bloom.density=0.5,bloom.vines=off".
    /// Re-read at every escape, so an adb push between judges takes effect without a restart.
    ///   bloom.on       true   the whole bloom (false: nothing is built or drawn)
    ///   bloom.density  1      0..1 = share of the flower triangle budget (art kit: ~150 clumps at 1, one instanced
    ///                         draw); above 1 changes nothing
    ///   bloom.vines    true   vines on walls, pillars and table legs
    ///   bloom.bees     true   gold bees visiting the flowers (12, comfort 8)
    ///   bloom.mruk     false  load the headset's room scan (MRUK, world lock off) for floor / walls / pillars
    ///   bloom.probes   true   depth raycasts in `pre` that find floor and walls when there's no scan
    ///   bloom.probevines false vines on probe-found walls (off: people standing near the table read as walls)
    ///   bloom.legs     false  spirals on the 4 assumed table legs when there's no scanned table (fake room / MRUK)
    ///   bloom.wave     1.2    m/s the bloom spreads from the hive
    ///   bloom.wilt     true   the flowers fold away on "...Not one flower." (T-0) and regrow on the crash back
    ///   bloom.art      auto   auto/on: the art lane's bloom kit when all of it is in Resources; off: procedural flowers
    /// Comfort mode keeps the bloom (only fewer visiting bees). Set() overrides (the Board) survive the re-read.
    /// </summary>
    public static class BloomTuning
    {
        public const int ClumpsAtDensity1 = 400;
        public const int MaxClumps = 500;

        public static bool On = true;
        public static float Density = 1f;
        public static bool Vines = true;
        public static bool Bees = true;
        public static bool Mruk;
        public static bool Probes = true;
        public static bool ProbeVines;
        public static bool Legs;
        public static float Wave = 1.2f;
        public static bool Wilt = true;
        public enum ArtMode { Auto, On, Off }
        public static ArtMode Art = ArtMode.Auto;
        public static string LoadedFrom = "defaults";

        public static int ClumpBudget => Mathf.Clamp(Mathf.RoundToInt(ClumpsAtDensity1 * Density), 0, MaxClumps);

        static bool _loaded;
        static readonly Dictionary<string, string> _overrides = new Dictionary<string, string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _loaded = false;
            _overrides.Clear();
            Defaults();
        }

        static void Defaults()
        {
            On = true;
            Density = 1f;
            Vines = true;
            Bees = true;
            Mruk = false;
            Probes = true;
            ProbeVines = false;
            Legs = false;
            Wave = 1.2f;
            Wilt = true;
            Art = ArtMode.Auto;
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
            try { env = Environment.GetEnvironmentVariable("VBS_BLOOM_FLAGS"); } catch (Exception) { }
            if (!string.IsNullOrEmpty(env))
            {
                int n = 0;
                foreach (var pair in env.Split(','))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0 && Apply(pair.Substring(0, eq).Trim(), pair.Substring(eq + 1))) n++;
                }
                from.Add("env VBS_BLOOM_FLAGS (" + n + ")");
            }
            if (_overrides.Count > 0)
            {
                foreach (var kv in _overrides) Apply(kv.Key, kv.Value);
                from.Add("Set (" + _overrides.Count + ")");
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
                    if (kv.Key.StartsWith("bloom.", StringComparison.Ordinal) && Apply(kv.Key, kv.Value)) n++;
                if (n > 0) from.Add(path + " (" + n + " bloom keys)");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VBS][Bloom] tuning: could not read " + path + ": " + e.Message);
            }
        }

        /// <summary>Live override, e.g. Set("bloom.density", "0.5"). False for an unknown key or a bad value.</summary>
        public static bool Set(string key, string value)
        {
            EnsureLoaded();
            if (key == null) return false;
            if (!key.StartsWith("bloom.", StringComparison.Ordinal)) key = "bloom." + key;
            if (!Apply(key, value)) return false;
            _overrides[key] = value;
            return true;
        }

        static bool Apply(string key, string v)
        {
            v = (v ?? "").Trim().Trim('"');
            switch (key)
            {
                case "bloom.on": return Bool(v, ref On);
                case "bloom.vines": return Bool(v, ref Vines);
                case "bloom.bees": return Bool(v, ref Bees);
                case "bloom.mruk": return Bool(v, ref Mruk);
                case "bloom.probes": return Bool(v, ref Probes);
                case "bloom.probevines": return Bool(v, ref ProbeVines);
                case "bloom.legs": return Bool(v, ref Legs);
                case "bloom.wilt": return Bool(v, ref Wilt);
                case "bloom.density": return Num(v, 0f, 2f, ref Density);
                case "bloom.wave": return Num(v, 0.2f, 10f, ref Wave);
                case "bloom.art":
                    switch (v.ToLowerInvariant())
                    {
                        case "auto": Art = ArtMode.Auto; return true;
                        case "on": case "true": case "1": Art = ArtMode.On; return true;
                        case "off": case "false": case "0": Art = ArtMode.Off; return true;
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
            return "on=" + On + " density=" + Density.ToString("0.##", c) + " vines=" + Vines +
                   " bees=" + Bees + " mruk=" + Mruk + " probes=" + Probes + " probevines=" + ProbeVines + " legs=" + Legs + " wave=" + Wave.ToString("0.##", c) +
                   " m/s wilt=" + Wilt + " art=" + Art.ToString().ToLowerInvariant() + " (from " + LoadedFrom + ")";
        }
    }
}
