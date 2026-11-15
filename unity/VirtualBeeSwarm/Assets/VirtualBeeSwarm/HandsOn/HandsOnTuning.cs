using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using VirtualBeeSwarm.AR;

namespace VirtualBeeSwarm.HandsOn
{
    public enum HandsOnMode { Off, Lever, Full }

    /// <summary>
    /// tuning.json keys for the hands-on layer (same files as GameTuning): handsOn.mode = full | lever | off,
    /// handsOn.leverSide (left | right), handsOn.assistSeconds, handsOn.hintSeconds, handsOn.launchWaitSeconds, handsOn.heatPump,
    /// handsOn.voiceOrder. Env VBS_HANDSON_MODE wins. VBS_AUTOPILOT_HANDSON=1: the autopilot plays the hand steps.
    /// </summary>
    public static class HandsOnTuning
    {
        public const HandsOnMode DefaultMode = HandsOnMode.Full;

        public static HandsOnMode Mode = DefaultMode;
        public static bool LeverLeft = true;
        public static float AssistSeconds = 12f, HintSeconds = 6f, LaunchWaitSeconds = 5f;
        public static bool HeatPump = true, VoiceOrder;
        public static bool AutoDrive;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void LoadAtStart() => Reload();

        public static void Reload()
        {
            var flat = new Dictionary<string, string>();
#if !UNITY_ANDROID || UNITY_EDITOR
            Read(Path.Combine(Application.streamingAssetsPath, "tuning.json"), flat);
#endif
            Read(Path.Combine(Application.persistentDataPath, "tuning.json"), flat);

            Mode = ParseMode(Get(flat, "handsOn.mode"), DefaultMode);
            LeverLeft = !string.Equals(Get(flat, "handsOn.leverSide"), "right", StringComparison.OrdinalIgnoreCase);
            AssistSeconds = Mathf.Clamp(Number(flat, "handsOn.assistSeconds", 12f), 2f, 15f); // below the station's no-pick timeout
            HintSeconds = Number(flat, "handsOn.hintSeconds", 6f);
            LaunchWaitSeconds = Number(flat, "handsOn.launchWaitSeconds", 5f);
            HeatPump = Flag(flat, "handsOn.heatPump", true);
            VoiceOrder = Flag(flat, "handsOn.voiceOrder", false);
            try
            {
                Mode = ParseMode(Environment.GetEnvironmentVariable("VBS_HANDSON_MODE"), Mode);
                AutoDrive = Environment.GetEnvironmentVariable("VBS_AUTOPILOT_HANDSON") == "1";
            }
            catch (Exception) { }
            Debug.Log("[VBS][HandsOn] mode=" + ModeName(Mode) + " lever=" + (LeverLeft ? "left" : "right") +
                      " assist=" + AssistSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s heatPump=" + (HeatPump ? "on" : "off") +
                      " voiceOrder=" + (VoiceOrder ? "on" : "off") + (AutoDrive ? " autopilot plays the hand steps" : ""));
        }

        public static string ModeName(HandsOnMode m) => m == HandsOnMode.Full ? "full" : m == HandsOnMode.Lever ? "lever" : "off";

        public static HandsOnMode ParseMode(string s, HandsOnMode fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            switch (s.Trim().ToLowerInvariant())
            {
                case "full": return HandsOnMode.Full;
                case "lever": return HandsOnMode.Lever;
                case "off": case "false": case "0": return HandsOnMode.Off;
                default: return fallback;
            }
        }

        static string Get(Dictionary<string, string> flat, string key) => flat.TryGetValue(key, out var v) ? v : null;

        static float Number(Dictionary<string, string> flat, string key, float fallback) =>
            float.TryParse(Get(flat, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Mathf.Max(0f, v) : fallback;

        static bool Flag(Dictionary<string, string> flat, string key, bool fallback)
        {
            string v = Get(flat, key);
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
                Debug.LogWarning("[VBS][HandsOn] tuning: could not read " + path + ": " + e.Message);
            }
        }
    }
}
