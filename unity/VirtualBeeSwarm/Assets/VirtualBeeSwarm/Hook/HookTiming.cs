using System;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Timing helpers for the hook (V14). The dark swarm pours on the words "angry swarms" of room_hook_1: where they
    /// fall is estimated from the clip length and where the words sit in the caption (speech is close to even per
    /// character). For today's clip (5.25 s) that is 1.45 s in; the waveform has the pause after "die," end at 1.31 s
    /// and "angry" right after "and". Without a clip (caption only, a streamed line), or when the estimate lands
    /// outside the spoken part, the caller keeps its fixed timer.
    /// </summary>
    public static class HookTiming
    {
        public const string ThreatWords = "angry swarms";
        /// <summary>Seconds into room_hook_1 when the words' position can't be estimated (StakesDirector's pourDelayIntoLine).</summary>
        public const float DefaultThreatOffset = 1.3f;

        /// <summary>Seconds into a line at which `words` are spoken, or `fallback`.</summary>
        public static float WordOffset(float clipSeconds, string caption, string words, float fallback)
        {
            if (clipSeconds < 1f || string.IsNullOrEmpty(caption) || string.IsNullOrEmpty(words)) return fallback;
            int at = caption.IndexOf(words, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return fallback;
            float t = clipSeconds * at / caption.Length;
            if (t < 0.2f || t > clipSeconds - 0.6f) return fallback; // the clip timing doesn't allow it
            return t;
        }

        /// <summary>When the threat's words fall in room_hook_1 (the line as it started), or `fallback`.</summary>
        public static float ThreatOffset(GameNet.VoiceLineInfo info, float fallback)
            => info.hasClip ? WordOffset(info.clipSeconds, info.caption, ThreatWords, fallback) : fallback;
    }
}
