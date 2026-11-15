using System.Diagnostics;
using UnityEngine;

namespace VirtualBeeSwarm.SwarmTouch
{
    /// <summary>How a swarm takes a hand passing through it.</summary>
    public enum TouchMood
    {
        Pet,   // calm: part round the fingers, swirl in the wake, settle back (hum swell, motes, the line)
        Angry, // the smoker swarm before it's calmed: dart off harder and buzz up (no stinging)
        Aloof, // the dark swarm: just parts, no reaction
    }

    /// <summary>
    /// The field the bees feel: up to 4 hand spheres (fingertip / palm, either hand, or a controller tip), filled each
    /// frame by SwarmTouchDirector. The swarms call it from their own per-bee update ([SwarmTouch hook] lines in
    /// World/SwarmSystem.cs and Stakes/DarkSwarm.cs):
    ///   bool touch = SwarmTouchField.Begin(swarm, mood);           once a frame
    ///   float tw = touch ? SwarmTouchField.Push(swarm, i, ref p, ref v, dt) : 0f;   per bee, after the steer
    ///   speed cap x (1 + 3 tw)                                       so a parted bee can outrun its mode's cap
    /// Begin is false with no hand tracked (and nothing still settling), so the hook costs one static read then. Bees
    /// outside the spheres' box (radius + falloff) return after six compares. Never pushes a bee further into the hive
    /// body box or toward the face (0.5 m); SwarmSystem's own hive clearance runs after Push. No allocations.
    /// </summary>
    public static class SwarmTouchField
    {
        public const int MaxSpheres = 4, MaxSwarms = 2, MaxBees = 640;
        public static bool Enabled = true;

        /// <summary>The soft shell around each sphere where bees feel the hand (m).</summary>
        public const float Falloff = 0.12f;
        const float Repel = 18f, Drag = 3.5f, Swirl = 5f, Settle = 0.7f;

        static readonly Vector3[] C = new Vector3[MaxSpheres], V = new Vector3[MaxSpheres];
        static readonly float[] R = new float[MaxSpheres];
        static int _count;
        static Vector3 _lo, _hi;
        static bool _hasHead, _hasHive;
        static Vector3 _head;
        static Bounds _hive;
        static readonly TouchMood[] Mood = new TouchMood[MaxSwarms];
        static readonly float[] LastAny = { -9f, -9f };
        static readonly float[,] Last = new float[MaxSwarms, MaxBees];
        static float _now;

        static SwarmTouchField() => ClearLast();

        static void ClearLast()
        {
            for (int a = 0; a < MaxSwarms; a++)
                for (int b = 0; b < MaxBees; b++) Last[a, b] = -9f;
        }

        // this frame (SwarmTouchDirector reads them in LateUpdate, then Reset)
        public static readonly int[] Touched = new int[MaxSwarms];
        public static float NearestShell = 9f; // closest bee centre to any sphere surface (m); < 0 = was inside, put back out
        public static int PutBack;             // bees found inside a sphere and moved to its surface
        public static long Ticks;              // Stopwatch ticks spent inside Push past the box test

        public static int Count => _count;

        /// <summary>Desktop demo only (SwarmTouchDemo): while set, Push also averages where the hive swarm's bees on
        /// the ProbeToward side of ProbeFrom, outside the hive box, actually are (ProbeSum / ProbeN), so the ghost hand goes where they fly.</summary>
        public static bool Probe;
        public static Vector3 ProbeFrom, ProbeToward, ProbeSum;
        public static float ProbeRadius; // summed horizontal distance from ProbeFrom
        public static int ProbeN;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _count = 0;
            LastAny[0] = LastAny[1] = -9f;
            ClearLast();
            Enabled = true;
        }

        public static void BeginFrame(float now, bool hasHead, Vector3 head, bool hasHive, Bounds hive)
        {
            _now = now;
            _count = 0;
            _hasHead = hasHead;
            _head = head;
            _hasHive = hasHive;
            _hive = hive;
            _hive.Expand(0.1f); // 5 cm round the hive body
            Touched[0] = Touched[1] = 0;
            NearestShell = 9f;
            PutBack = 0;
            Ticks = 0;
        }

        public static void Add(Vector3 centre, Vector3 velocity, float radius)
        {
            if (_count >= MaxSpheres) return;
            C[_count] = centre;
            V[_count] = velocity;
            R[_count] = radius;
            Vector3 e = Vector3.one * (radius + Falloff);
            if (_count == 0) { _lo = centre - e; _hi = centre + e; }
            else { _lo = Vector3.Min(_lo, centre - e); _hi = Vector3.Max(_hi, centre + e); }
            _count++;
        }

        /// <summary>Once a frame per swarm, before its bee loop. False: skip Push for every bee.</summary>
        public static bool Begin(int swarm, TouchMood mood)
        {
            if (!Enabled || swarm < 0 || swarm >= MaxSwarms) return false;
            Mood[swarm] = mood;
            return _count > 0 || _now - LastAny[swarm] < 1.5f || (Probe && swarm == 0);
        }

        /// <summary>One bee: parts it round the hand spheres, drags and swirls it in the hand's wake. Returns 0..1, how
        /// stirred up it is (fades over ~0.7 s after the hand leaves) for the caller's speed cap.</summary>
        public static float Push(int swarm, int i, ref Vector3 p, ref Vector3 v, float dt)
        {
            if (i < 0 || i >= MaxBees) return 0f;
            if (Probe && swarm == 0)
            {
                Vector3 o = p - ProbeFrom;
                if (o.sqrMagnitude < 0.8f * 0.8f && Vector3.Dot(o, ProbeToward) > 0.05f && (!_hasHive || !_hive.Contains(p))) { ProbeSum += p; ProbeRadius += new Vector2(o.x, o.z).magnitude; ProbeN++; }
            }
            float since = _now - Last[swarm, i];
            float stir = since < Settle * 3f ? Mathf.Exp(-since / Settle) : 0f;
            if (_count == 0 || p.x < _lo.x || p.y < _lo.y || p.z < _lo.z || p.x > _hi.x || p.y > _hi.y || p.z > _hi.z) return stir;

            long t0 = Stopwatch.GetTimestamp();
            TouchMood mood = Mood[swarm];
            float repel = mood == TouchMood.Angry ? Repel * 1.6f : Repel;
            Vector3 acc = Vector3.zero;
            float wMax = 0f;
            for (int s = 0; s < _count; s++)
            {
                Vector3 d = p - C[s];
                float reach = R[s] + Falloff;
                float d2 = d.sqrMagnitude;
                if (d2 >= reach * reach) continue;
                float dist = Mathf.Sqrt(d2);
                Vector3 n = dist > 1e-4f ? d / dist : Vector3.up;
                float w = 1f - Mathf.Clamp01((dist - R[s]) / Falloff);
                w = w * w * (3f - 2f * w);
                acc += n * (repel * w);
                Vector3 hv = V[s];
                float hs = hv.magnitude;
                if (hs > 0.05f)
                {
                    acc += hv * (Drag * w);                                                // pulled along in the wake
                    acc += Vector3.Cross(hv / hs, n) * (Swirl * w * Mathf.Min(hs, 1.5f));  // curled round the hand's path
                }
                float shell = dist - R[s];
                if (shell < NearestShell) NearestShell = shell;
                if (dist < R[s]) // inside the finger: out to its surface (the swarm's own hive clearance runs after this)
                {
                    p = C[s] + n * R[s];
                    float vin = Vector3.Dot(v - hv, n);
                    if (vin < 0f) v -= n * vin;
                    PutBack++;
                }
                if (w > wMax) wMax = w;
            }
            if (wMax > 0f)
            {
                if (_hasHive && _hive.Contains(p)) acc = NotToward(acc, _hive.center - p);
                if (_hasHead)
                {
                    Vector3 toHead = _head - p;
                    if (toHead.sqrMagnitude < 0.25f) acc = NotToward(acc, toHead);
                }
                v += acc * dt;
                Last[swarm, i] = _now;
                LastAny[swarm] = _now;
                Touched[swarm]++;
                stir = Mathf.Max(stir, wMax);
            }
            Ticks += Stopwatch.GetTimestamp() - t0;
            return stir;
        }

        static Vector3 NotToward(Vector3 a, Vector3 dir)
        {
            float k = Vector3.Dot(a, dir);
            return k > 0f ? a - dir * (k / Mathf.Max(1e-6f, dir.sqrMagnitude)) : a;
        }
    }
}
