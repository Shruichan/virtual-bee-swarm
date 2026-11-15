using UnityEngine;

namespace VirtualBeeSwarm
{
    public enum GameHand { Left = 0, Right = 1 }

    /// <summary>
    /// Hands / controllers, SDK-agnostic. The Meta implementation (VirtualBeeSwarm.Meta/OvrHandPoseSource, OVRHand +
    /// OVRSkeleton + OVRInput) only compiles when com.meta.xr.sdk.core is installed; XRControllerHandSource is the
    /// fallback (UnityEngine.XR controllers, any OpenXR runtime). Sources register with GameHands.
    /// All positions/poses are in world space.
    /// </summary>
    public interface IHandPoseSource
    {
        /// <summary>The hand or controller is tracked right now.</summary>
        bool IsTracked(GameHand hand);
        /// <summary>Index pinch (hand) or trigger (controller): select / click / grab.</summary>
        bool IsPinching(GameHand hand);
        /// <summary>Closed fist (hand) or grip (controller): the hornet ball.</summary>
        bool IsFist(GameHand hand);
        /// <summary>Push-to-talk button held (controller B/Y). Hands: false (WhatIfBeat uses the pinch instead).</summary>
        bool IsTalkHeld(GameHand hand);
        /// <summary>Ray for pointing (hand pointer pose / controller aim).</summary>
        bool TryGetPointerRay(GameHand hand, out Ray ray);
        /// <summary>Index fingertip (hand) or controller tip: poking buttons, grabbing, calibration points.</summary>
        bool TryGetIndexTip(GameHand hand, out Vector3 position);
    }

    /// <summary>Optional capability for pose sources that can identify controller input.</summary>
    public interface IControllerPoseSource
    {
        bool IsController(GameHand hand);
    }

    /// <summary>Anything a pointer (mouse, controller ray, hand ray) can hover and click. Put it on (a parent of) a collider.</summary>
    public interface IGameTarget
    {
        bool Interactable { get; }
        void SetHover(bool hovered);
        void Click();
    }

    /// <summary>A target that is dragged instead of clicked (hive lift, rig placement).</summary>
    public interface IGameGrabTarget
    {
        bool Grabbable { get; }
        /// <summary>grabPoint: world point where the ray / hand first held it.</summary>
        void BeginGrab(Vector3 grabPoint);
        /// <summary>handPoint: where the holding point is now (ray point at the grab distance, or the pinch point).</summary>
        void UpdateGrab(Vector3 handPoint);
        void EndGrab();
    }

    /// <summary>Optional: dims passthrough for the Moon (Meta: OVRPassthroughLayer). amount 0 = normal, 1 = black.</summary>
    public interface IPassthroughDimmer
    {
        void SetDim(float amount);
    }
}
