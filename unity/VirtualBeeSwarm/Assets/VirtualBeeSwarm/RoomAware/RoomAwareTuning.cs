using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.RoomAware
{
    /// <summary>
    /// Room awareness switches (tuning.json room.*, then env VBS_ROOM_FLAGS="room.depth=0,room.turnCap=45").
    ///   room.aware          true   furniture keep-outs for every placement (rig, portal, ORDER UP board, lever, props,
    ///                              Beeswax). false = the old behaviour everywhere (also env VBS_ROOM_AWARE=0)
    ///   room.requestCapture false  no scan on the headset: open Space Setup once (MRUK requestSceneCaptureIfNoDataFound)
    ///   room.depth          true   headset: sample depth raycasts in `pre` and mark occupied 20 cm cells (the fallback
    ///                              when the scan has no furniture, and extra clutter the scan missed)
    ///   room.source         auto   auto | scan | depth (depth: ignore the scan's furniture; desktop: raycasts against the
    ///                              capture room's furniture colliders, to test the fallback headless)
    ///   room.turnCap        35     deg, how far the automatic rig placement may turn away from where the player looks
    ///   room.turnWide       60     deg, only when nothing inside turnCap keeps the hive, rocket and the view of the hive
    ///                              clear: how far it may look further round
    ///   room.headGap        0.4    m, the ORDER UP board and lever never come closer to the eyes than this
    /// </summary>
    public static class RoomAwareTuning
    {
        public enum SourceMode { Auto, Scan, Depth }

        public static bool On = true;
        public static bool RequestSceneCapture;
        public static bool Depth = true;
        public static SourceMode Source = SourceMode.Auto;
        public static float TurnCap = 35f;
        public static float TurnWide = 60f;
        public static float HeadGap = 0.4f;
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
            RequestSceneCapture = false;
            Depth = true;
            Source = SourceMode.Auto;
            TurnCap = 35f;
            TurnWide = 60f;
            HeadGap = 0.4f;
            LoadedFrom = "defaults";
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            Defaults();
            var from = new List<string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            TryFile(Path.Combine(Application.streamingAssetsPath, "tuning.json"), from);
#endif
            TryFile(Path.Combine(Application.persistentDataPath, "tuning.json"), from);
            string env = null;
            try { env = Environment.GetEnvironmentVariable("VBS_ROOM_FLAGS"); } catch (Exception) { }
            if (!string.IsNullOrEmpty(env))
            {
                int n = 0;
                foreach (var pair in env.Split(','))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0 && Apply(pair.Substring(0, eq).Trim(), pair.Substring(eq + 1))) n++;
                }
                from.Add("env VBS_ROOM_FLAGS (" + n + ")");
            }
            string off = null;
            try { off = Environment.GetEnvironmentVariable("VBS_ROOM_AWARE"); } catch (Exception) { }
            if (!string.IsNullOrEmpty(off) && Apply("room.aware", off)) from.Add("env VBS_ROOM_AWARE");
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
                    if (kv.Key.StartsWith("room.", StringComparison.Ordinal) && Apply(kv.Key, kv.Value)) n++;
                if (n > 0) from.Add(path + " (" + n + " room keys)");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VBS][Room] tuning: could not read " + path + ": " + e.Message);
            }
        }

        static bool Apply(string key, string v)
        {
            v = (v ?? "").Trim().Trim('"');
            if (!key.StartsWith("room.", StringComparison.Ordinal)) key = "room." + key;
            switch (key)
            {
                case "room.aware": return Bool(v, ref On);
                case "room.requestCapture": return Bool(v, ref RequestSceneCapture);
                case "room.depth": return Bool(v, ref Depth);
                case "room.source":
                    switch (v.ToLowerInvariant())
                    {
                        case "auto": Source = SourceMode.Auto; return true;
                        case "scan": Source = SourceMode.Scan; return true;
                        case "depth": Source = SourceMode.Depth; return true;
                        default: return false;
                    }
                case "room.turnCap": return Num(v, 10f, 180f, ref TurnCap);
                case "room.turnWide": return Num(v, 10f, 180f, ref TurnWide);
                case "room.headGap": return Num(v, 0.2f, 0.8f, ref HeadGap);
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
            return "aware=" + On + " requestCapture=" + RequestSceneCapture + " depth=" + Depth + " source=" + Source +
                   " turnCap=" + TurnCap.ToString("0", c) + " turnWide=" + TurnWide.ToString("0", c) + " headGap=" + HeadGap.ToString("0.00", c) + " (from " + LoadedFrom + ")";
        }
    }
}
