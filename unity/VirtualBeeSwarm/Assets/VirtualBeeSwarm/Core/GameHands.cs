using System.Collections.Generic;
using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Registry of hand/controller sources. The highest-priority source that tracks a hand answers for it
    /// (OvrHandPoseSource registers at 10, XRControllerHandSource at 0). Also edge detection for pinch/fist,
    /// updated once per frame by whoever asks first (allocation-free).
    /// </summary>
    public static class GameHands
    {
        struct Entry { public IHandPoseSource source; public int priority; }

        static readonly List<Entry> Sources = new List<Entry>(4);
        static int _edgeFrame = -1;
        static readonly bool[] PinchNow = new bool[2], PinchPrev = new bool[2];
        static readonly bool[] FistNow = new bool[2], FistPrev = new bool[2];

        public static void Register(IHandPoseSource source, int priority)
        {
            if (source == null) return;
            Unregister(source);
            int i = 0;
            while (i < Sources.Count && Sources[i].priority >= priority) i++;
            Sources.Insert(i, new Entry { source = source, priority = priority });
        }

        public static void Unregister(IHandPoseSource source)
        {
            for (int i = Sources.Count - 1; i >= 0; i--)
                if (ReferenceEquals(Sources[i].source, source)) Sources.RemoveAt(i);
        }

        public static bool HasAnySource => Sources.Count > 0;

        /// <summary>The source answering for this hand now (highest priority that tracks it), or null.</summary>
        public static IHandPoseSource For(GameHand hand)
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                var s = Sources[i].source;
                if (s is Object o && o == null) continue; // destroyed MonoBehaviour that forgot to unregister
                if (s.IsTracked(hand)) return s;
            }
            return null;
        }

        public static bool IsTracked(GameHand hand) => For(hand) != null;
        public static bool IsPinching(GameHand hand) { var s = For(hand); return s != null && s.IsPinching(hand); }
        public static bool IsFist(GameHand hand) { var s = For(hand); return s != null && s.IsFist(hand); }
        public static bool IsTalkHeld(GameHand hand) { var s = For(hand); return s != null && s.IsTalkHeld(hand); }

        public static bool TryGetPointerRay(GameHand hand, out Ray ray)
        {
            var s = For(hand);
            if (s != null) return s.TryGetPointerRay(hand, out ray);
            ray = default;
            return false;
        }

        public static bool TryGetIndexTip(GameHand hand, out Vector3 position)
        {
            var s = For(hand);
            if (s != null) return s.TryGetIndexTip(hand, out position);
            position = default;
            return false;
        }

        // ------------------------------------------------------------ edges (this frame)
        static void UpdateEdges()
        {
            int f = Time.frameCount;
            if (f == _edgeFrame) return;
            _edgeFrame = f;
            for (int h = 0; h < 2; h++)
            {
                PinchPrev[h] = PinchNow[h];
                FistPrev[h] = FistNow[h];
                PinchNow[h] = IsPinching((GameHand)h);
                FistNow[h] = IsFist((GameHand)h);
            }
        }

        public static bool PinchDown(GameHand hand) { UpdateEdges(); return PinchNow[(int)hand] && !PinchPrev[(int)hand]; }
        public static bool PinchUp(GameHand hand) { UpdateEdges(); return !PinchNow[(int)hand] && PinchPrev[(int)hand]; }
        public static bool PinchHeld(GameHand hand) { UpdateEdges(); return PinchNow[(int)hand]; }

        /// <summary>Either hand makes a fist, or key H (desktop).</summary>
        public static bool AnyFist()
        {
            UpdateEdges();
            return FistNow[0] || FistNow[1] || GameInput.KeyHeld(KeyCode.H);
        }

        /// <summary>Either hand pinches (or presses the trigger).</summary>
        public static bool AnyPinchHeld()
        {
            UpdateEdges();
            return PinchNow[0] || PinchNow[1];
        }

        public static bool AnyTalkHeld() => IsTalkHeld(GameHand.Left) || IsTalkHeld(GameHand.Right);
    }
}
