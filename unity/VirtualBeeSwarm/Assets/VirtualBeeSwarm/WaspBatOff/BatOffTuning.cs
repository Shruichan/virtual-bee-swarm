using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.BatOff
{
    /// <summary>
    /// tuning.json keys for the wasp bat-off (same files as GameTuning): wasps.batOff (on | off, default on),
    /// wasps.batSeconds (length of the burst, default 15), wasps.batCount (wasps in the burst, 4-14, default 12).
    /// Env VBS_BATOFF=0|1 wins; VBS_BATOFF_AUTO=1 makes the ghost paw bat even when hands are tracked.
    /// </summary>
    public static class BatOffTuning
    {
        public static bool Enabled = true;
        public static float Seconds = 15f;
        public static int Count = 12;
        public static bool ForceAuto;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void LoadAtStart() => Reload();

        public static void Reload()
        {
            var flat = new Dictionary<string, string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            Read(Path.Combine(Application.streamingAssetsPath, "tuning.json"), flat);
#endif
            Read(Path.Combine(Application.persistentDataPath, "tuning.json"), flat);
            Enabled = Flag(Get(flat, "wasps.batOff"), true);
            Seconds = Mathf.Clamp(Number(Get(flat, "wasps.batSeconds"), 15f), 6f, 30f);
            Count = Mathf.Clamp(Mathf.RoundToInt(Number(Get(flat, "wasps.batCount"), 12f)), 4, 14);
            try
            {
                Enabled = Flag(Environment.GetEnvironmentVariable("VBS_BATOFF"), Enabled);
                ForceAuto = Environment.GetEnvironmentVariable("VBS_BATOFF_AUTO") == "1";
            }
            catch (Exception) { }
            Debug.Log("[VBS][BatOff] batOff=" + (Enabled ? "on" : "off") + " seconds=" +
                      Seconds.ToString("0.#", CultureInfo.InvariantCulture) + " wasps=" + Count + (ForceAuto ? " auto-bat forced" : ""));
        }

        static string Get(Dictionary<string, string> flat, string key) => flat.TryGetValue(key, out var v) ? v : null;

        static float Number(string s, float fallback) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        static bool Flag(string v, bool fallback)
        {
            if (string.IsNullOrEmpty(v)) return fallback;
            v = v.Trim().ToLowerInvariant();
            if (v == "on" || v == "true" || v == "1") return true;
            if (v == "off" || v == "false" || v == "0") return false;
            return fallback;
        }

        static void Read(string path, Dictionary<string, string> into)
        {
            try
            {
                if (File.Exists(path)) ArTuning.FlatJson.Parse(File.ReadAllText(path), into);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VBS][BatOff] tuning: could not read " + path + ": " + e.Message);
            }
        }
    }
}
