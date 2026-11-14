using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VirtualBeeSwarm.AR;
using VirtualBeeSwarm.Stakes;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The venue switches in tuning.json (no rebuild): persistentDataPath/tuning.json (Quest: adb push to
    /// /sdcard/Android/data/dev.drori.virtualbeeswarm/files/), and Assets/StreamingAssets/tuning.json off-Android.
    /// Keys: delivery.mode (on|swirl|off), ar.* (ArTuning: placement, occlusion, windowOnWall, landing, shadows, ...),
    /// stakes.* and comfort (StakesFlags). Loaded once before the scene; Reload() re-reads everything.
    /// Env VBS_DELIVERY_MODE / VBS_STAKES_FLAGS and -deliveryMode still win (smoke runs).
    /// </summary>
    public static class GameTuning
    {
        public static string LoadedFrom { get; private set; } = "defaults";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void LoadAtStart() => Reload();

        public static void Reload()
        {
            var flat = new Dictionary<string, string>();
            var from = new List<string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            Read(Path.Combine(Application.streamingAssetsPath, "tuning.json"), flat, from);
#endif
            Read(Path.Combine(Application.persistentDataPath, "tuning.json"), flat, from);
            LoadedFrom = from.Count > 0 ? string.Join(" + ", from) : "defaults";

            int stakes = 0;
            foreach (var kv in flat)
                if ((kv.Key.StartsWith("stakes.", StringComparison.Ordinal) || kv.Key == "comfort") && StakesFlags.TrySet(kv.Key, kv.Value))
                    stakes++;
            ArTuning.Load();
            BeeDelivery.Mode = BeeDelivery.ModeFromEnvironment(BeeDelivery.ModeFromTuningFiles(DeliveryMode.On));
            Debug.Log("[VBS] tuning from " + LoadedFrom + ": delivery.mode=" + BeeDelivery.ModeName(BeeDelivery.Mode) +
                      " comfort=" + (StakesFlags.Comfort ? "on" : "off") + " (" + stakes + " stakes keys) ar: " + ArTuning.Describe());
        }

        static void Read(string path, Dictionary<string, string> into, List<string> from)
        {
            try
            {
                if (!File.Exists(path)) return;
                ArTuning.FlatJson.Parse(File.ReadAllText(path), into);
                from.Add(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VBS] tuning: could not read " + path + ": " + e.Message);
            }
        }
    }
}
