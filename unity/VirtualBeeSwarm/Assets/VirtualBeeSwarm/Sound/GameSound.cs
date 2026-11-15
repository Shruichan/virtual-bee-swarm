using System;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// The one call gameplay code makes for a sound moment: GameSound.Cue("stamp_approved", item). Names are the Satie
    /// scene's events (Sound/SatieScene.cs). No audio here and nothing happens without a Sound.SoundDirector in the scene
    /// (edit-mode tests, sound.on = false), so a cue can never hold or break a beat. delay: seconds (cancelled by a beat change).
    /// </summary>
    public static class GameSound
    {
        /// <summary>(event, transform to follow or null, position, position given, delay). Set by SoundDirector.</summary>
        internal static Action<string, Transform, Vector3, bool, float> Sink;

        /// <summary>Every cue, sound on or off: (event, transform or null, position, delay). Juice/ listens.</summary>
        public static event Action<string, Transform, Vector3, float> Cued;

        static void Notify(string evt, Transform at, Vector3 pos, float delay)
        {
            var c = Cued;
            if (c == null) return;
            try { c(evt, at, pos, delay); }
            catch (Exception e) { Debug.LogWarning("[VBS][Sound] cue listener " + evt + " failed: " + e); }
        }

        public static void Cue(string evt, Transform at = null, float delay = 0f)
        {
            Notify(evt, at, at != null ? at.position : Vector3.zero, delay);
            var s = Sink;
            if (s == null) return;
            try { s(evt, at, at != null ? at.position : Vector3.zero, false, delay); }
            catch (Exception e) { Debug.LogWarning("[VBS][Sound] cue " + evt + " failed: " + e); }
        }

        public static void Cue(string evt, Vector3 at, float delay = 0f)
        {
            Notify(evt, null, at, delay);
            var s = Sink;
            if (s == null) return;
            try { s(evt, null, at, true, delay); }
            catch (Exception e) { Debug.LogWarning("[VBS][Sound] cue " + evt + " failed: " + e); }
        }
    }
}
