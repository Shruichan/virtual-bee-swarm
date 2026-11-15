using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>"[VBS][Anim] &lt;object&gt; &lt;clip&gt;" for every art clip start, only with VBS_ANIM_LOG=1 (autopilot animation checks).</summary>
    public static class ArtAnimLog
    {
        public static readonly bool On = System.Environment.GetEnvironmentVariable("VBS_ANIM_LOG") == "1";

        public static void Clip(Component c, string clip)
        {
            if (!On || c == null) return;
            var t = c.transform;
            Debug.Log("[VBS][Anim] " + (t.parent != null ? t.parent.name + "/" : "") + t.name + " " + clip + " t=" + Time.time.ToString("0.00"));
        }
    }
}
