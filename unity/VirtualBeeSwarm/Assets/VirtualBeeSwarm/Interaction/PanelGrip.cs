using UnityEngine;
using VirtualBeeSwarm.HandsOn;

namespace VirtualBeeSwarm
{
    /// <summary>A small grab tab outside a panel's interactive content.</summary>
    [DisallowMultipleComponent]
    public sealed class PanelGrip : MonoBehaviour, IGameGrabTarget
    {
        Transform _panel;
        bool _delivered;
        Vector3 _holdOffset, _start;

        public bool Ready { get; set; } = true;
        public bool Held { get; private set; }
        public bool ManuallyPlaced { get; private set; }
        public bool Grabbable => isActiveAndEnabled && Ready && !Held &&
            (!_delivered || BeeDelivery.Instance == null || !BeeDelivery.Instance.IsFlying(_panel));

        public static PanelGrip Add(Transform panel, Transform parent, Vector3 localPosition, bool delivered = false)
        {
            var tab = new GameObject("Panel Grab Tab");
            tab.transform.SetParent(parent, false);
            tab.transform.localPosition = localPosition;
            var trigger = tab.AddComponent<BoxCollider>();
            trigger.size = new Vector3(0.074f, 0.018f, 0.028f);
            trigger.isTrigger = true;
            HandsOnKit.Prim(PrimitiveType.Cube, "Tab", tab.transform, Vector3.zero,
                new Vector3(0.074f, 0.017f, 0.007f), HandsOnKit.Honey);
            HandsOnKit.Text("Grab Label", tab.transform, Vector3.back * 0.005f, "GRAB", 0.0019f, HandsOnKit.Ink);
            var grip = tab.AddComponent<PanelGrip>();
            grip._panel = panel;
            grip._delivered = delivered;
            return grip;
        }

        public void BeginGrab(Vector3 grabPoint)
        {
            if (!Grabbable) return;
            Held = true;
            _start = _panel.position;
            _holdOffset = _start - grabPoint;
        }

        public void UpdateGrab(Vector3 handPoint)
        {
            if (!Held || !Ready) return;
            Vector3 position = handPoint + _holdOffset;
            _panel.position = position;
            if ((position - _start).sqrMagnitude > 0.0001f) ManuallyPlaced = true;
        }

        public void EndGrab() => Held = false;

        public void ResetPlacement()
        {
            Held = false;
            ManuallyPlaced = false;
        }

        void OnDisable() => EndGrab();
    }
}
