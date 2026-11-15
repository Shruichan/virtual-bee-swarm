using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The headset profile: lighter defaults so the Quest holds 72 fps (fewer bees, a thinner meadow, low-poly far bees,
    /// no light shafts, half the particles). On for the Android player, and in the Editor (or a desktop player) while an
    /// XR headset is running, e.g. Play mode over Quest Link. Desktop runs without a headset (captures, autopilot) keep
    /// the full look. Overrides: VBS_QUEST_PROFILE=1/0 (env, or the EditorPrefs key of the same name, set from
    /// Virtual Bee Swarm > Headset Profile), or VBS_PERF=quest/full. tuning.json / env keys still win over these defaults.
    /// </summary>
    public static class QuestPerf
    {
        public const string Key = "VBS_QUEST_PROFILE";
        static int _on = -1;

        // Enter Play Mode without a domain reload keeps statics: decide again every play session.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetForPlay() => _on = -1;

        public static bool On
        {
            get
            {
                if (_on < 0)
                {
                    bool on = Application.platform == RuntimePlatform.Android;
                    string why = on ? "android player" : "no headset";
                    if (!on && Application.isPlaying && HeadsetActive(out string device))
                    {
                        on = true;
                        why = (Application.isEditor ? "editor" : "desktop player") + " with XR headset " + device;
                    }
                    string force = Setting(Key);
                    if (force == null)
                    {
                        string perf = Setting("VBS_PERF");
                        force = perf == "quest" ? "1" : perf == "full" ? "0" : null;
                    }
                    if (force == "1" || force == "on" || force == "true") { on = true; why += ", forced on"; }
                    else if (force == "0" || force == "off" || force == "false") { on = false; why += ", forced off"; }
                    _on = on ? 1 : 0;
                    if (Application.isPlaying) Debug.Log("[VBS][Perf] quest profile " + (on ? "ON" : "off") + " (" + why + ")");
                }
                return _on == 1;
            }
        }

        static bool HeadsetActive(out string device)
        {
            device = "";
            try
            {
                if (XRSettings.isDeviceActive) { device = XRSettings.loadedDeviceName; return true; }
                var displays = new List<XRDisplaySubsystem>();
                SubsystemManager.GetSubsystems(displays);
                foreach (var d in displays)
                    if (d != null && d.running) { device = d.subsystemDescriptor != null ? d.subsystemDescriptor.id : "display"; return true; }
            }
            catch (Exception) { }
            return false;
        }

        static string Setting(string key)
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(key); } catch (Exception) { }
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(v)) v = UnityEditor.EditorPrefs.GetString(key, "");
#endif
            return string.IsNullOrEmpty(v) ? null : v.Trim().ToLowerInvariant();
        }

        /// <summary>A particle cap for this profile (half on the headset, at least 4).</summary>
        public static int Particles(int max) => On ? Mathf.Max(4, max / 2) : max;
    }
}
