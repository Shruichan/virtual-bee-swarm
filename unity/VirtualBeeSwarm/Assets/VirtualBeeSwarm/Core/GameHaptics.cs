using UnityEngine.XR;

namespace VirtualBeeSwarm
{
    public static class GameHaptics
    {
        public static void Pulse(GameHand hand, float strength, float seconds)
        {
            if (!(GameHands.For(hand) is IControllerPoseSource source) || !source.IsController(hand)) return;
            var device = InputDevices.GetDeviceAtXRNode(hand == GameHand.Left ? XRNode.LeftHand : XRNode.RightHand);
            if (!device.isValid || (device.characteristics & InputDeviceCharacteristics.Controller) == 0 ||
                !device.TryGetHapticCapabilities(out var capabilities) || !capabilities.supportsImpulse ||
                capabilities.numChannels == 0) return;
            device.SendHapticImpulse(0, strength, seconds);
        }
    }
}
