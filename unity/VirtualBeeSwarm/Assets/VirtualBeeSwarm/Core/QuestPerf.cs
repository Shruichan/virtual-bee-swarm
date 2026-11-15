using System;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The headset profile: lighter defaults so the Quest holds 72 fps (fewer bees, a thinner meadow, low-poly far bees,
    /// no light shafts, half the particles). On for the Android player; VBS_PERF=quest turns it on in a desktop run to
    /// check the numbers, VBS_PERF=full turns it off on the headset. tuning.json / env keys still win over these defaults.
    /// </summary>
    public static class QuestPerf
    {
        static int _on = -1;

        public static bool On
        {
            get
            {
                if (_on < 0)
                {
                    bool on = Application.platform == RuntimePlatform.Android;
                    string env = null;
                    try { env = Environment.GetEnvironmentVariable("VBS_PERF"); } catch (Exception) { }
                    if (env == "quest") on = true;
                    else if (env == "full") on = false;
                    _on = on ? 1 : 0;
                }
                return _on == 1;
            }
        }

        /// <summary>A particle cap for this profile (half on the headset, at least 4).</summary>
        public static int Particles(int max) => On ? Mathf.Max(4, max / 2) : max;
    }
}
