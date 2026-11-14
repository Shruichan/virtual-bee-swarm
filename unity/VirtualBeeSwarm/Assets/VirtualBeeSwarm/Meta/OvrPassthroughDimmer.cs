#if VBS_META
using UnityEngine;

namespace VirtualBeeSwarm.Meta
{
    /// <summary>
    /// Dims Meta passthrough for the Moon (T0Sequence finds any IPassthroughDimmer). Lowers the passthrough layer's
    /// texture opacity, so the camera's clear color (black/transparent in MR) shows through. Put it anywhere; the
    /// OVRPassthroughLayer is found if not assigned (the Building Blocks passthrough block adds one).
    /// </summary>
    public class OvrPassthroughDimmer : MonoBehaviour, IPassthroughDimmer
    {
        [SerializeField] OVRPassthroughLayer layer;
        [Range(0f, 1f)] [SerializeField] float minOpacity = 0.15f;

        float _baseOpacity = 1f;
        bool _captured;

        void Awake()
        {
            if (layer == null) layer = FindAnyObjectByType<OVRPassthroughLayer>();
        }

        public void SetDim(float amount)
        {
            if (layer == null) return;
            if (!_captured)
            {
                _baseOpacity = layer.textureOpacity;
                _captured = true;
            }
            layer.textureOpacity = Mathf.Lerp(_baseOpacity, minOpacity, Mathf.Clamp01(amount));
        }
    }
}
#endif
