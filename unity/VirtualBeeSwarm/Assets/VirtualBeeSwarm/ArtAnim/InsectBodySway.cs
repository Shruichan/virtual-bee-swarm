using UnityEngine;

namespace VirtualBeeSwarm
{
    /// <summary>An in-place figure-eight rotation layered over the insect's current pose.</summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class InsectBodySway : MonoBehaviour
    {
        [SerializeField, Range(0f, 2f)] float strength = 1f;

        Quaternion _baseRotation, _appliedRotation;
        bool _applied;

        internal static Quaternion Offset(float seconds, float phase, float amount)
        {
            float cycle = seconds * (2f * Mathf.PI / 1.8f) + phase;
            float side = Mathf.Sin(cycle);
            return Quaternion.Euler(
                Mathf.Sin(2f * cycle) * 5f * amount,
                side * 12f * amount,
                -side * 8f * amount);
        }

        void Update() => RemoveOffset();

        void LateUpdate()
        {
            _baseRotation = transform.localRotation;
            _appliedRotation = _baseRotation * Offset(Time.time, 0f, strength);
            transform.localRotation = _appliedRotation;
            _applied = true;
        }

        void OnDisable() => RemoveOffset();

        void RemoveOffset()
        {
            if (!_applied) return;
            // A gameplay or animation write since last frame takes precedence.
            if (transform.localRotation == _appliedRotation)
                transform.localRotation = _baseRotation;
            _applied = false;
        }
    }
}
