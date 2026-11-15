#if VBS_META
using UnityEngine;

namespace VirtualBeeSwarm.Meta
{
    /// <summary>
    /// Meta XR hands + Touch controllers for the gameplay layer (compiled only with com.meta.xr.sdk.core).
    /// Hands: pinch = OVRHand index pinch, pointer = OVRHand.PointerPose, index tip + fist from OVRSkeleton bones
    /// (works with both the OpenXR XRHand_* and the legacy Hand_* skeletons). Controllers: trigger = pinch,
    /// grip = fist, B/Y = push-to-talk, pose from the OVRCameraRig controller anchors.
    /// Registers at priority 10 (wins over XRControllerHandSource). Fields left empty are found at runtime.
    /// </summary>
    public class OvrHandPoseSource : MonoBehaviour, IHandPoseSource, IControllerPoseSource
    {
        [SerializeField] OVRCameraRig rig;
        [SerializeField] OVRHand leftHand;
        [SerializeField] OVRHand rightHand;
        [SerializeField] OVRSkeleton leftSkeleton;
        [SerializeField] OVRSkeleton rightSkeleton;
        [SerializeField] int priority = 10;
        [Tooltip("Fist when the middle/ring/little tips are closer to the wrist than this x the wrist-to-knuckle length.")]
        [SerializeField] float fistRatio = 1.3f;
        [Range(0.1f, 0.95f)] [SerializeField] float triggerThreshold = 0.55f;
        [Range(0.1f, 0.95f)] [SerializeField] float gripThreshold = 0.6f;
        [SerializeField] float controllerTipOffset = 0.05f;

        struct Bones
        {
            public bool ok;
            public Transform wrist, knuckle, indexTip, middleTip, ringTip, littleTip;
        }

        readonly Bones[] _bones = new Bones[2];
        float _nextResolve;

        void OnEnable()
        {
            Resolve();
            GameHands.Register(this, priority);
        }

        void OnDisable() => GameHands.Unregister(this);

        void Resolve()
        {
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            if (leftHand == null || rightHand == null)
            {
                var hands = FindObjectsByType<OVRHand>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                for (int i = 0; i < hands.Length; i++)
                {
                    var type = hands[i].GetHand();
                    if (type == OVRPlugin.Hand.HandLeft && leftHand == null) leftHand = hands[i];
                    else if (type == OVRPlugin.Hand.HandRight && rightHand == null) rightHand = hands[i];
                }
            }
            if (leftSkeleton == null && leftHand != null) leftSkeleton = leftHand.GetComponent<OVRSkeleton>();
            if (rightSkeleton == null && rightHand != null) rightSkeleton = rightHand.GetComponent<OVRSkeleton>();
        }

        void Update()
        {
            if ((rig == null || leftHand == null || rightHand == null) && Time.unscaledTime >= _nextResolve)
            {
                _nextResolve = Time.unscaledTime + 2f;
                Resolve();
            }
        }

        OVRHand Hand(GameHand h) => h == GameHand.Left ? leftHand : rightHand;
        OVRSkeleton Skeleton(GameHand h) => h == GameHand.Left ? leftSkeleton : rightSkeleton;
        static OVRInput.Controller Ctrl(GameHand h) => h == GameHand.Left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;

        bool HandTracked(GameHand h)
        {
            var hand = Hand(h);
            return hand != null && hand.IsTracked;
        }

        bool ControllerTracked(GameHand h)
        {
            var c = Ctrl(h);
            return OVRInput.IsControllerConnected(c) && OVRInput.GetControllerPositionTracked(c);
        }

        Transform ControllerAnchor(GameHand h)
        {
            if (rig == null) return null;
            return h == GameHand.Left ? rig.leftControllerAnchor : rig.rightControllerAnchor;
        }

        // ------------------------------------------------------------ IHandPoseSource
        public bool IsTracked(GameHand hand) => HandTracked(hand) || ControllerTracked(hand);

        public bool IsController(GameHand hand) => !HandTracked(hand) && ControllerTracked(hand);

        public bool IsPinching(GameHand hand)
        {
            if (HandTracked(hand)) return Hand(hand).GetFingerIsPinching(OVRHand.HandFinger.Index);
            if (ControllerTracked(hand)) return OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, Ctrl(hand)) >= triggerThreshold;
            return false;
        }

        public bool IsFist(GameHand hand)
        {
            if (HandTracked(hand))
            {
                if (!TryBones(hand, out var b)) return false;
                Vector3 w = b.wrist.position;
                float knuckle = Vector3.Distance(w, b.knuckle.position);
                if (knuckle < 0.01f) return false;
                float tips = (Vector3.Distance(w, b.middleTip.position) + Vector3.Distance(w, b.ringTip.position) +
                              Vector3.Distance(w, b.littleTip.position)) / 3f;
                return tips < knuckle * fistRatio;
            }
            if (ControllerTracked(hand)) return OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, Ctrl(hand)) >= gripThreshold;
            return false;
        }

        public bool IsTalkHeld(GameHand hand) =>
            !HandTracked(hand) && ControllerTracked(hand) && OVRInput.Get(OVRInput.Button.Two, Ctrl(hand));

        public bool TryGetPointerRay(GameHand hand, out Ray ray)
        {
            if (HandTracked(hand))
            {
                var h = Hand(hand);
                if (h.IsPointerPoseValid)
                {
                    var p = h.PointerPose;
                    ray = new Ray(p.position, p.forward);
                    return true;
                }
            }
            else if (ControllerTracked(hand))
            {
                var a = ControllerAnchor(hand);
                if (a != null)
                {
                    ray = new Ray(a.position + a.forward * controllerTipOffset, a.forward);
                    return true;
                }
            }
            ray = default;
            return false;
        }

        public bool TryGetIndexTip(GameHand hand, out Vector3 position)
        {
            if (HandTracked(hand) && TryBones(hand, out var b))
            {
                position = b.indexTip.position;
                return true;
            }
            if (!HandTracked(hand) && ControllerTracked(hand))
            {
                var a = ControllerAnchor(hand);
                if (a != null)
                {
                    position = a.position + a.forward * controllerTipOffset;
                    return true;
                }
            }
            position = default;
            return false;
        }

        // ------------------------------------------------------------ skeleton
        bool TryBones(GameHand hand, out Bones b)
        {
            int i = (int)hand;
            b = _bones[i];
            if (b.ok && b.wrist != null) return true;
            var s = Skeleton(hand);
            if (s == null || !s.IsInitialized || s.Bones == null) return false;

            var nb = new Bones();
            var type = s.GetSkeletonType();
            bool xr = type == OVRSkeleton.SkeletonType.XRHandLeft || type == OVRSkeleton.SkeletonType.XRHandRight;
            // BoneId values overlap between the OpenXR (XRHand_*) and legacy (Hand_*) skeletons: pick the set by type.
            var wristId = xr ? OVRSkeleton.BoneId.XRHand_Wrist : OVRSkeleton.BoneId.Hand_WristRoot;
            var knuckleId = xr ? OVRSkeleton.BoneId.XRHand_MiddleProximal : OVRSkeleton.BoneId.Hand_Middle1;
            var indexId = xr ? OVRSkeleton.BoneId.XRHand_IndexTip : OVRSkeleton.BoneId.Hand_IndexTip;
            var middleId = xr ? OVRSkeleton.BoneId.XRHand_MiddleTip : OVRSkeleton.BoneId.Hand_MiddleTip;
            var ringId = xr ? OVRSkeleton.BoneId.XRHand_RingTip : OVRSkeleton.BoneId.Hand_RingTip;
            var littleId = xr ? OVRSkeleton.BoneId.XRHand_LittleTip : OVRSkeleton.BoneId.Hand_PinkyTip;
            var bones = s.Bones;
            for (int k = 0; k < bones.Count; k++)
            {
                var bone = bones[k];
                if (bone == null || bone.Transform == null) continue;
                var id = bone.Id;
                if (id == wristId) nb.wrist = bone.Transform;
                else if (id == knuckleId) nb.knuckle = bone.Transform;
                else if (id == indexId) nb.indexTip = bone.Transform;
                else if (id == middleId) nb.middleTip = bone.Transform;
                else if (id == ringId) nb.ringTip = bone.Transform;
                else if (id == littleId) nb.littleTip = bone.Transform;
            }
            nb.ok = nb.wrist != null && nb.knuckle != null && nb.indexTip != null && nb.middleTip != null &&
                    nb.ringTip != null && nb.littleTip != null;
            _bones[i] = nb;
            b = nb;
            return nb.ok;
        }
    }
}
#endif
