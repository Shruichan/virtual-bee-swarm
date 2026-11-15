using System;
using UnityEngine;

namespace VirtualBeeSwarm.HandsOn
{
    /// <summary>
    /// Lets the player carry a delivered prop: pinch it (or ray-grab, or mouse-drag) and it follows the hand; let go
    /// and it drifts back to where it was parked. Kinematic, no physics. HandsOnDirector adds it to the ordered item,
    /// switches it Live for the set-up step and off again after (it stays on the prop, disabled).
    /// Tip: degrees the prop leans over while carried (the bottle pours itself over the feeder).
    /// </summary>
    public class CarryProp : MonoBehaviour, IGameGrabTarget
    {
        public event Action Grabbed;

        public bool Live;
        public bool Held { get; private set; }
        public Pose Home;
        public float FloorY = float.NegativeInfinity;
        public float Tip;

        public bool Grabbable => this != null && enabled && Live;

        Vector3 _offset, _target;
        float _tip;

        public void Arm(Pose home, float floorY)
        {
            Home = home;
            FloorY = floorY;
            Tip = _tip = 0f;
            Held = false;
            Live = true;
            enabled = true;
        }

        public void Disarm()
        {
            Live = false;
            Held = false;
            Tip = _tip = 0f;
            enabled = false;
        }

        public void BeginGrab(Vector3 grabPoint)
        {
            if (!Live) return;
            Held = true;
            _offset = transform.position - grabPoint;
            _target = transform.position;
            Grabbed?.Invoke();
        }

        public void UpdateGrab(Vector3 handPoint)
        {
            _target = handPoint + _offset;
            if (_target.y < FloorY) _target.y = FloorY;
        }

        public void EndGrab() => Held = false;

        void OnEnable() => BeeswaxBody.MarkDirty(); // [BeeswaxBody hook] a new prop: Beeswax's body rescans now

        void Update()
        {
            if (!Live) return;
            float dt = Time.deltaTime;
            Vector3 to = Held ? _target : Home.position;
            transform.position = Vector3.Lerp(transform.position, to, 1f - Mathf.Exp(-(Held ? 28f : 9f) * dt));
            _tip = Mathf.Lerp(_tip, Held ? Tip : 0f, 1f - Mathf.Exp(-10f * dt));
            transform.rotation = Home.rotation * Quaternion.Euler(0f, 0f, _tip);
        }
    }
}
