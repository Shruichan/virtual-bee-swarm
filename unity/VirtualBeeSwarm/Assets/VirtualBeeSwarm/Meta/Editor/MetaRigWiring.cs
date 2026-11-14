#if VBS_META
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VirtualBeeSwarm.Meta.Editor
{
    /// <summary>
    /// Scene-builder step for the Meta rig (called by BuildGameScene through reflection, so the builder itself
    /// doesn't need the Meta SDK). Works on the OVRCameraRig that the builder copied in from the rig template
    /// (Scenes/RigTemplate.unity, made by ProjectBootstrap: passthrough underlay, boundary suppression, mic + scene
    /// permission requests). Idempotent:
    ///   * tracking origin = floor level (the table anchor's height is measured from the floor);
    ///   * an OVRHand + OVRSkeleton per hand under the hand anchors (no hand mesh: the real hands show in passthrough);
    ///   * OvrHandPoseSource / OvrPassthroughDimmer on GameRig/Input get the rig, hands and passthrough layer.
    /// </summary>
    public static class MetaRigWiring
    {
        public static string Wire(GameObject inputRoot)
        {
            var log = new StringBuilder();
            var rig = Object.FindAnyObjectByType<OVRCameraRig>(FindObjectsInactive.Include);
            if (rig == null) return "no OVRCameraRig in the scene (rig template missing?)";

            var manager = rig.GetComponent<OVRManager>();
            if (manager != null)
            {
                var so = new SerializedObject(manager);
                Set(so, "_trackingOriginType", (int)OVRManager.TrackingOrigin.FloorLevel);
                so.ApplyModifiedPropertiesWithoutUndo();
                log.Append("origin=").Append(manager.trackingOriginType)
                   .Append(" passthrough=").Append(manager.isInsightPassthroughEnabled)
                   .Append(" boundarySuppressed=").Append(manager.shouldBoundaryVisibilityBeSuppressed)
                   .Append(" micPermission=").Append(Get(so, "requestRecordAudioPermissionOnStartup"))
                   .Append(" scenePermission=").Append(Get(so, "requestScenePermissionOnStartup"));
            }
            else log.Append("OVRManager MISSING");

            var tracking = rig.transform.Find("TrackingSpace");
            var leftAnchor = tracking != null ? tracking.Find("LeftHandAnchor") : null;
            var rightAnchor = tracking != null ? tracking.Find("RightHandAnchor") : null;
            if (leftAnchor == null || rightAnchor == null) return log.Append(" | hand anchors not found under TrackingSpace").ToString();

            var version = OVRRuntimeSettings.Instance.HandSkeletonVersion;
            var left = EnsureHand(leftAnchor, OVRHand.Hand.HandLeft, version, tracking);
            var right = EnsureHand(rightAnchor, OVRHand.Hand.HandRight, version, tracking);
            log.Append(" | hands=").Append(version);

            var passthrough = rig.GetComponentInChildren<OVRPassthroughLayer>(true);
            if (passthrough == null) passthrough = Object.FindAnyObjectByType<OVRPassthroughLayer>(FindObjectsInactive.Include);
            log.Append(" passthroughLayer=").Append(passthrough != null ? passthrough.gameObject.name : "MISSING");

            if (inputRoot != null)
            {
                var source = inputRoot.GetComponent<OvrHandPoseSource>();
                if (source == null) source = inputRoot.AddComponent<OvrHandPoseSource>();
                var so = new SerializedObject(source);
                SetRef(so, "rig", rig);
                SetRef(so, "leftHand", left);
                SetRef(so, "rightHand", right);
                SetRef(so, "leftSkeleton", left.GetComponent<OVRSkeleton>());
                SetRef(so, "rightSkeleton", right.GetComponent<OVRSkeleton>());
                so.ApplyModifiedPropertiesWithoutUndo();

                var dimmer = inputRoot.GetComponent<OvrPassthroughDimmer>();
                if (dimmer == null) dimmer = inputRoot.AddComponent<OvrPassthroughDimmer>();
                var sd = new SerializedObject(dimmer);
                SetRef(sd, "layer", passthrough);
                sd.ApplyModifiedPropertiesWithoutUndo();
                log.Append(" | OvrHandPoseSource + OvrPassthroughDimmer wired");
            }
            return log.ToString();
        }

        static OVRHand EnsureHand(Transform anchor, OVRHand.Hand side, OVRHandSkeletonVersion version, Transform trackingSpace)
        {
            var hand = anchor.GetComponentInChildren<OVRHand>(true);
            if (hand == null)
            {
                var go = new GameObject(side == OVRHand.Hand.HandLeft ? "OVRHand Left" : "OVRHand Right");
                go.transform.SetParent(anchor, false);
                hand = go.AddComponent<OVRHand>();
            }
            var skeleton = hand.GetComponent<OVRSkeleton>();
            if (skeleton == null) skeleton = hand.gameObject.AddComponent<OVRSkeleton>();

            var sh = new SerializedObject(hand);
            Set(sh, "HandType", (int)side);
            SetRef(sh, "_pointerPoseRoot", trackingSpace);
            sh.ApplyModifiedPropertiesWithoutUndo();

            var ss = new SerializedObject(skeleton);
            Set(ss, "_skeletonType", (int)side.AsSkeletonType(version));
            ss.ApplyModifiedPropertiesWithoutUndo();
            return hand;
        }

        static void Set(SerializedObject so, string field, int value)
        {
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning("[VBS] " + so.targetObject.GetType().Name + "." + field + " not found (Meta SDK changed?)"); return; }
            p.intValue = value;
        }

        static void SetRef(SerializedObject so, string field, Object value)
        {
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning("[VBS] " + so.targetObject.GetType().Name + "." + field + " not found"); return; }
            p.objectReferenceValue = value;
        }

        static string Get(SerializedObject so, string field)
        {
            var p = so.FindProperty(field);
            return p == null ? "?" : p.propertyType == SerializedPropertyType.Boolean ? p.boolValue.ToString() : p.intValue.ToString();
        }
    }
}
#endif
