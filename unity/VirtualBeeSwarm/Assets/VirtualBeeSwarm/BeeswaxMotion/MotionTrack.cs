using System;
using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>What every playing primitive adds this frame (all additive; the biggest look wins).</summary>
    public struct MotionSample
    {
        /// <summary>Offset in the stage frame (x = right as the player sees him, y = up, z = toward the eye), m.</summary>
        public Vector3 stage;
        /// <summary>Offset in world space (toward a prop, away from a threat), m.</summary>
        public Vector3 world;
        /// <summary>Body tilt, degrees: x pitch (+ = nose down), y yaw, z roll. Pitch and roll are clamped to 25.</summary>
        public Vector3 rot;
        /// <summary>Whole turns that are not tilts (pirouette, spin, barrel roll), degrees.</summary>
        public float spinYaw, spinRoll;
        /// <summary>Scale minus one.</summary>
        public Vector3 squash;
        /// <summary>Added to the bob multiplier (-0.5 = half the bob).</summary>
        public float bob;
        /// <summary>Look at lookAt with this weight (0 = the player).</summary>
        public float lookW;
        public Vector3 lookAt;

        public void Add(in MotionSample o, float w)
        {
            if (w <= 0f) return;
            stage += o.stage * w;
            world += o.world * w;
            rot += o.rot * w;
            spinYaw += o.spinYaw * w;
            spinRoll += o.spinRoll * w;
            squash += o.squash * w;
            bob += o.bob * w;
            if (o.lookW * w > lookW) { lookW = o.lookW * w; lookAt = o.lookAt; }
        }
    }

    /// <summary>
    /// One playing primitive: a small tween player. eval(track, t) gives its shape at local time t (motion clock: it stops
    /// during the hitstop). A held track plays until Release(); a cancelled one fades out over 0.2 s.
    /// </summary>
    public sealed class MotionTrack
    {
        public string name;
        public float start;                       // motion clock
        public float length = float.PositiveInfinity; // local seconds (infinite = held until released)
        public float released = -1f;              // motion clock time Release() was called (held tracks)
        public float outSeconds = 0.4f;
        public float cancelledAt = -1f;
        public bool primary;                      // a new primary cancels the old one
        public bool pausesTick;                   // the P4 metronome holds its breath while this plays
        public bool ambient;                      // G6: nobody ordered it (clamped while he talks)
        public bool spin;                         // a whole turn: never cut short by a cancel
        public Func<MotionTrack, float, MotionSample> eval;
        public Vector3 target;                    // a point the primitive looks at / leans toward (may be updated)
        public Transform targetT;
        public float minEye = float.PositiveInfinity;
        public string why;

        /// <summary>Local release time (seconds after start), or -1 while held.</summary>
        public float Rel => released < 0f ? -1f : released - start;
        public Vector3 Target => targetT != null ? targetT.position : target;

        public bool Done(float now)
        {
            float t = now - start;
            if (t >= length) return true;
            if (cancelledAt >= 0f && now - cancelledAt >= 0.2f) return true;
            if (released >= 0f && now - released >= outSeconds && float.IsPositiveInfinity(length)) return true;
            return false;
        }

        public float CancelWeight(float now) => cancelledAt < 0f ? 1f : 1f - MotionCurves.OutCubic((now - cancelledAt) / 0.2f);
    }

    /// <summary>The set of playing tracks and their mix.</summary>
    public sealed class MotionTrackPlayer
    {
        readonly List<MotionTrack> _tracks = new List<MotionTrack>();
        readonly List<MotionTrack> _finished = new List<MotionTrack>();
        public IReadOnlyList<MotionTrack> Tracks => _tracks;
        public List<MotionTrack> Finished => _finished;

        public MotionTrack Play(MotionTrack t, float now)
        {
            t.start = now;
            if (t.primary)
                for (int i = 0; i < _tracks.Count; i++)
                    if (_tracks[i].primary && _tracks[i].cancelledAt < 0f && !_tracks[i].spin) _tracks[i].cancelledAt = now;
            // same name restarts
            for (int i = 0; i < _tracks.Count; i++)
                if (_tracks[i].name == t.name && _tracks[i].cancelledAt < 0f) _tracks[i].cancelledAt = now;
            _tracks.Add(t);
            return t;
        }

        public MotionTrack Find(string name)
        {
            for (int i = _tracks.Count - 1; i >= 0; i--)
                if (_tracks[i].name == name && _tracks[i].cancelledAt < 0f && _tracks[i].released < 0f) return _tracks[i];
            return null;
        }

        public bool Playing(string name) => Find(name) != null;

        public bool Any(Func<MotionTrack, bool> pred)
        {
            for (int i = 0; i < _tracks.Count; i++)
                if (_tracks[i].cancelledAt < 0f && pred(_tracks[i])) return true;
            return false;
        }

        public void Release(string name, float now)
        {
            for (int i = 0; i < _tracks.Count; i++)
                if (_tracks[i].name == name && _tracks[i].released < 0f && _tracks[i].cancelledAt < 0f) _tracks[i].released = now;
        }

        public void Cancel(string name, float now)
        {
            for (int i = 0; i < _tracks.Count; i++)
                if (_tracks[i].name == name && _tracks[i].cancelledAt < 0f) _tracks[i].cancelledAt = now;
        }

        public void CancelAll(float now, bool immediate)
        {
            if (immediate) { _finished.AddRange(_tracks); _tracks.Clear(); return; }
            for (int i = 0; i < _tracks.Count; i++)
                if (_tracks[i].cancelledAt < 0f) _tracks[i].cancelledAt = now;
        }

        /// <summary>Mix every track at `now` (motion clock) into s; finished tracks move to Finished.</summary>
        public void Evaluate(float now, ref MotionSample s, ref MotionSample ambient, float eyeDistance)
        {
            for (int i = _tracks.Count - 1; i >= 0; i--)
            {
                var tr = _tracks[i];
                if (tr.Done(now)) { _finished.Add(tr); _tracks.RemoveAt(i); continue; }
                if (eyeDistance < tr.minEye) tr.minEye = eyeDistance;
                float w = tr.CancelWeight(now);
                if (tr.eval == null || w <= 0f) continue;
                var one = tr.eval(tr, now - tr.start);
                s.Add(one, w);
                if (tr.ambient) ambient.Add(one, w);
            }
        }
    }
}
