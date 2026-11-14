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

        public static void Cue(string evt, Transform at = null, float delay = 0f)
        {
            var s = Sink;
            if (s == null) return;
            try { s(evt, at, at != null ? at.position : Vector3.zero, false, delay); }
            catch (Exception e) { Debug.LogWarning("[VBS][Sound] cue " + evt + " failed: " + e); }
        }

        public static void Cue(string evt, Vector3 at, float delay = 0f)
        {
            var s = Sink;
            if (s == null) return;
            try { s(evt, null, at, true, delay); }
            catch (Exception e) { Debug.LogWarning("[VBS][Sound] cue " + evt + " failed: " + e); }
        }
    }
}
