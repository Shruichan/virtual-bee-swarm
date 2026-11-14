using UnityEngine;
using UnityEngine.XR;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// SDK-free fallback hand source: XR controllers through UnityEngine.XR.InputDevices (works with any OpenXR
    /// runtime). Trigger = pinch/select, grip = fist, secondary button (B/Y) = push-to-talk.
    /// Device poses are in tracking space; `trackingSpace` converts them to world (OVRCameraRig/TrackingSpace or the
    /// XR Origin's Camera Offset; empty = the main camera's parent). Registers at priority 0, so the Meta source wins.
    /// </summary>
    public class XRControllerHandSource : MonoBehaviour, IHandPoseSource
    {
        [SerializeField] Transform trackingSpace;
        [SerializeField] int priority = 0;
        [Range(0.1f, 0.95f)] [SerializeField] float triggerThreshold = 0.55f;
        [Range(0.1f, 0.95f)] [SerializeField] float gripThreshold = 0.6f;
        [Tooltip("Ray/tip offset forward from the controller origin (m).")]
        [SerializeField] float tipOffset = 0.05f;

        void OnEnable() => GameHands.Register(this, priority);
        void OnDisable() => GameHands.Unregister(this);

        static InputDevice Device(GameHand hand) =>
            InputDevices.GetDeviceAtXRNode(hand == GameHand.Left ? XRNode.LeftHand : XRNode.RightHand);

        Transform Space()
        {
            if (trackingSpace != null) return trackingSpace;
            var cam = Camera.main;
            return cam != null ? cam.transform.parent : null;
        }

        public bool IsTracked(GameHand hand)
        {
            var d = Device(hand);
            if (!d.isValid) return false;
            return !d.TryGetFeatureValue(CommonUsages.isTracked, out bool t) || t;
        }

        public bool IsPinching(GameHand hand)
        {
            var d = Device(hand);
            if (!d.isValid) return false;
            if (d.TryGetFeatureValue(CommonUsages.trigger, out float v)) return v >= triggerThreshold;
            return d.TryGetFeatureValue(CommonUsages.triggerButton, out bool b) && b;
        }

        public bool IsFist(GameHand hand)
        {
            var d = Device(hand);
            if (!d.isValid) return false;
            if (d.TryGetFeatureValue(CommonUsages.grip, out float v)) return v >= gripThreshold;
            return d.TryGetFeatureValue(CommonUsages.gripButton, out bool b) && b;
        }

        public bool IsTalkHeld(GameHand hand)
        {
            var d = Device(hand);
            return d.isValid && d.TryGetFeatureValue(CommonUsages.secondaryButton, out bool b) && b;
        }

        bool TryPose(GameHand hand, out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = Quaternion.identity;
            var d = Device(hand);
            if (!d.isValid) return false;
            if (!d.TryGetFeatureValue(CommonUsages.devicePosition, out pos)) return false;
            d.TryGetFeatureValue(CommonUsages.deviceRotation, out rot);
            var space = Space();
            if (space != null)
            {
                pos = space.TransformPoint(pos);
                rot = space.rotation * rot;
            }
            return true;
        }

        public bool TryGetPointerRay(GameHand hand, out Ray ray)
        {
            if (TryPose(hand, out var p, out var r))
            {
                var fwd = r * Vector3.forward;
                ray = new Ray(p + fwd * tipOffset, fwd);
                return true;
            }
            ray = default;
            return false;
        }

        public bool TryGetIndexTip(GameHand hand, out Vector3 position)
        {
            if (TryPose(hand, out var p, out var r))
            {
                position = p + r * Vector3.forward * tipOffset;
                return true;
            }
            position = default;
            return false;
        }
    }
}
