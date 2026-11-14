#if VBS_META
using UnityEngine;
using UnityEngine.XR;

namespace VirtualBeeSwarm.Stakes.Meta
{
    /// <summary>
    /// The Quest output of the stakes dim (PassthroughDim → here → OVRPassthroughLayer). Installs itself next to the
    /// scene's OVRPassthroughLayer, so the scene builder needs no change. Two methods (StakesFlags "stakes.dimMethod"):
    ///   brightness (default)  the layer's brightness / saturation colour map (XR_FB_passthrough BCS): brightness =
    ///                         −dim × brightnessPerDim (an L* offset, ×100 by the SDK), saturation = −dim × saturationPerDim.
    ///                         Doesn't touch textureOpacity, so it never fights OvrPassthroughDimmer (T-0's Moon dim).
    ///   opacity               textureOpacity = base × (1 − dim): the black clear colour shows through. Simple, but it
    ///                         shares textureOpacity with OvrPassthroughDimmer, so use it only if brightness looks wrong.
    /// A no-op off the headset (no XR device and not Android): the desktop keeps its look.
    /// </summary>
    public class StakesPassthroughDimmer : MonoBehaviour, IStakesDimOutput
    {
        [SerializeField] OVRPassthroughLayer layer;
        [Range(0f, 1f)] [SerializeField] float brightnessPerDim = 0.8f;
        [Range(0f, 1f)] [SerializeField] float contrastPerDim = 0f;
        [Range(0f, 1f)] [SerializeField] float saturationPerDim = 0.8f;

        float _baseOpacity = -1f;
        bool _colorMapOn, _logged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (FindAnyObjectByType<StakesPassthroughDimmer>() != null) return;
            var l = FindAnyObjectByType<OVRPassthroughLayer>();
            if (l == null) return;
            var go = new GameObject("StakesPassthroughDimmer");
            go.transform.SetParent(l.transform, false);
            go.AddComponent<StakesPassthroughDimmer>().layer = l;
        }

        void Awake()
        {
            if (layer == null) layer = FindAnyObjectByType<OVRPassthroughLayer>();
            PassthroughDim.Output = this;
            PassthroughDim.Resend();
        }

        void OnDestroy()
        {
            if (ReferenceEquals(PassthroughDim.Output, this)) PassthroughDim.Output = null;
        }

        static bool OnDevice => Application.platform == RuntimePlatform.Android || XRSettings.isDeviceActive;

        public void ApplyDim(float amount)
        {
            if (layer == null || !OnDevice) return;
            amount = Mathf.Clamp01(amount);
            if (!_logged)
            {
                _logged = true;
                Debug.Log("[VBS][Stakes] passthrough dim via " + (StakesFlags.UseOpacity ? "textureOpacity" : "brightness/saturation"));
            }

            if (StakesFlags.UseOpacity)
            {
                if (_colorMapOn) { layer.DisableColorMap(); _colorMapOn = false; }
                if (_baseOpacity < 0f) _baseOpacity = layer.textureOpacity;
                layer.textureOpacity = _baseOpacity * (1f - amount);
                return;
            }

            if (_baseOpacity >= 0f) { layer.textureOpacity = _baseOpacity; _baseOpacity = -1f; } // switched back from opacity
            if (amount <= 0.001f)
            {
                if (_colorMapOn) { layer.DisableColorMap(); _colorMapOn = false; }
                return;
            }
            layer.SetBrightnessContrastSaturation(-amount * brightnessPerDim, -amount * contrastPerDim, -amount * saturationPerDim);
            _colorMapOn = true;
        }
    }
}
#endif
