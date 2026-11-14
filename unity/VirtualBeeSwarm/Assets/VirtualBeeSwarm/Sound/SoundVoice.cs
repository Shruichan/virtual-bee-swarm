using UnityEngine;

namespace VirtualBeeSwarm.Sound
{
    /// <summary>A Satie `follow` binding: signal in [inLo, inHi] maps linearly (clamped) to [outLo, outHi].</summary>
    public sealed class SoundFollow
    {
        public readonly string signal;
        public readonly float inLo, inHi, outLo, outHi;

        public SoundFollow(string signal, float inLo, float inHi, float outLo, float outHi)
        {
            this.signal = signal;
            this.inLo = inLo;
            this.inHi = inHi;
            this.outLo = outLo;
            this.outHi = outHi;
        }

        public float Map(float v)
        {
            float k = Mathf.Approximately(inHi, inLo) ? 0f : Mathf.Clamp01((v - inLo) / (inHi - inLo));
            return Mathf.Lerp(outLo, outHi, k);
        }
    }

    /// <summary>
    /// One voice of the Satie scene (a loop, or an `on &lt;event&gt;` bank). Clips are Resources/SatieTakes names, already
    /// mono, trimmed to the voice's duration, faded, filtered and loudness-evened by the bake (so the runtime only sets
    /// gain, pitch and position). host = the Unity object it sits at (null: wherever the event happens).
    /// priority: 0 = never dropped, 1 = normal, 2 = dropped first when voices run out.
    /// </summary>
    public sealed class SoundVoice
    {
        public readonly string id, evt, host;
        public readonly bool loop, muted;
        public readonly string[] clips, zones;
        public readonly float spl, radius, duration, cooldown, fadeIn, fadeOut;
        public readonly SoundFollow volume, pitch, splFollow;
        public readonly int priority;

        public SoundVoice(string id, bool loop, string evt, string[] clips, string host, float spl, float radius, string[] zones,
            float duration, float cooldown, float fadeIn, float fadeOut, SoundFollow volume, SoundFollow pitch, SoundFollow splFollow,
            int priority, bool muted = false)
        {
            this.id = id;
            this.loop = loop;
            this.evt = evt;
            this.clips = clips ?? new string[0];
            this.host = host;
            this.spl = spl;
            this.radius = radius;
            this.zones = zones;
            this.duration = duration;
            this.cooldown = cooldown;
            this.fadeIn = fadeIn;
            this.fadeOut = fadeOut;
            this.volume = volume;
            this.pitch = pitch;
            this.splFollow = splFollow;
            this.priority = priority;
            this.muted = muted;
        }

        public bool InZone(string state)
        {
            if (zones == null || zones.Length == 0) return true;
            for (int i = 0; i < zones.Length; i++)
                if (zones[i] == state) return true;
            return false;
        }
    }
}
