#if VBS_META && VBS_MRUK
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using UnityEngine;

namespace VirtualBeeSwarm.AR.Meta
{
    /// <summary>
    /// Meta XR 209 glue for the AR layer (asmdef VirtualBeeSwarm.AR; compiled only with com.meta.xr.sdk.core and
    /// com.meta.xr.mrutilitykit). Registers itself with ArPlatform before the first scene loads, and only where the Meta
    /// natives exist (Android player, Windows/macOS editor or player): on Linux nothing registers and the core uses its
    /// fakes, so no Meta native is ever touched there.
    ///   Raycaster: one EnvironmentRaycastManager (native OpenXR raycaster in 209; needs USE_SCENE), created at runtime
    ///              under Systems/AR, disabled until ArRig switches it on in `pre`.
    ///   Depth:     one EnvironmentDepthManager (SoftOcclusion by default, RemoveHands = false), created only when
    ///              IsSupported, and disabled for ar.occlusion = off (mode None alone keeps depth acquisition running).
    /// Neither component requests permissions: OVRManager is the only requester (its "Scene" startup request).
    /// </summary>
    public sealed class MetaArProvider : IArProvider
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.Android:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.OSXPlayer:
                    ArPlatform.Provider = new MetaArProvider();
                    break;
                default:
                    ArPlatform.Provider = null; // Linux: no Meta natives (MRUK/OVRPlugin), the fakes run
                    break;
            }
        }

        public string HeadsetName
        {
            get
            {
                string type = OVRPlugin.initialized ? OVRPlugin.GetSystemHeadsetType().ToString() : "OVRPlugin not initialized";
                return type + " (" + SystemInfo.deviceModel + ")";
            }
        }

        public bool? ScenePermissionGranted
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return UnityEngine.Android.Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission);
#else
                return null;
#endif
            }
        }

        public IEnvRaycaster CreateRaycaster(GameObject host)
        {
            if (!EnvironmentRaycastManager.IsSupported) return new Unsupported();
            var go = new GameObject("EnvironmentRaycast");
            go.SetActive(false);
            go.transform.SetParent(host.transform, false);
            var mgr = go.AddComponent<EnvironmentRaycastManager>();
            mgr.enabled = false;
            go.SetActive(true);
            return new Raycaster(mgr);
        }

        public IArDepth CreateDepth(GameObject host)
        {
            var go = new GameObject("EnvironmentDepth");
            go.SetActive(false);
            go.transform.SetParent(host.transform, false);
            var depth = go.AddComponent<Depth>();
            depth.Init();
            go.SetActive(true);
            return depth;
        }

        // ------------------------------------------------------------ raycast
        sealed class Raycaster : IEnvRaycaster
        {
            readonly EnvironmentRaycastManager _mgr;
            public Raycaster(EnvironmentRaycastManager mgr) { _mgr = mgr; }
            public string Name => "EnvironmentRaycastManager";
            public bool IsSupported => EnvironmentRaycastManager.IsSupported;

            public bool Enabled
            {
                get => _mgr != null && _mgr.enabled;
                set { if (_mgr != null) _mgr.enabled = value; }
            }

            public bool Raycast(Ray ray, float maxDistance, out ArHit hit)
            {
                if (!Enabled) { hit = new ArHit { status = ArHitStatus.NotReady }; return false; }
                bool ok = _mgr.Raycast(ray, out var h, maxDistance);
                hit = Convert(h);
                return ok;
            }

            public bool PlaceBox(Ray ray, Vector3 boxSize, Vector3 upwards, out ArHit hit)
            {
                if (!Enabled) { hit = new ArHit { status = ArHitStatus.NotReady }; return false; }
                bool ok = _mgr.PlaceBox(ray, boxSize, upwards, out var h);
                hit = Convert(h);
                return ok;
            }

            static ArHit Convert(EnvironmentRaycastHit h) => new ArHit
            {
                status = h.status switch
                {
                    EnvironmentRaycastHitStatus.Hit => ArHitStatus.Hit,
                    EnvironmentRaycastHitStatus.NoHit => ArHitStatus.NoHit,
                    EnvironmentRaycastHitStatus.NotReady => ArHitStatus.NotReady,
                    EnvironmentRaycastHitStatus.NotSupported => ArHitStatus.NotSupported,
                    EnvironmentRaycastHitStatus.HitPointOutsideOfCameraFrustum => ArHitStatus.OutsideView,
                    _ => ArHitStatus.Occluded,
                },
                point = h.point,
                normal = h.normal,
                normalConfidence = h.normalConfidence,
            };
        }

        sealed class Unsupported : IEnvRaycaster
        {
            public string Name => "EnvironmentRaycastManager (not supported: Quest 3/3S + USE_SCENE + Spatial Data over Link)";
            public bool IsSupported => false;
            public bool Enabled { get; set; }
            public bool Raycast(Ray ray, float maxDistance, out ArHit hit) { hit = new ArHit { status = ArHitStatus.NotSupported }; return false; }
            public bool PlaceBox(Ray ray, Vector3 boxSize, Vector3 upwards, out ArHit hit) { hit = new ArHit { status = ArHitStatus.NotSupported }; return false; }
        }

        // ------------------------------------------------------------ depth
        sealed class Depth : MonoBehaviour, IArDepth
        {
            EnvironmentDepthManager _mgr;
            bool _supported;
            float _enabledAt = -1f;
            public float FirstDepthMs { get; private set; } = -1f;

            public void Init()
            {
                _supported = EnvironmentDepthManager.IsSupported;
                if (!_supported) return;
                // gameObject is inactive here, so the manager's Awake waits until ArRig/SetMode switches it on.
                _mgr = gameObject.AddComponent<EnvironmentDepthManager>();
                _mgr.enabled = false;
                _mgr.OcclusionShadersMode = OcclusionShadersMode.SoftOcclusion;
                _mgr.RemoveHands = false;
            }

            public bool IsSupported => _supported;
            public bool IsDepthAvailable => _mgr != null && _mgr.enabled && _mgr.IsDepthAvailable;

            public void SetMode(ArTuning.Occlusion mode)
            {
                if (_mgr == null) return;
                if (mode == ArTuning.Occlusion.Off)
                {
                    if (_mgr.enabled) _mgr.enabled = false;
                    return;
                }
                _mgr.OcclusionShadersMode = mode == ArTuning.Occlusion.Hard ? OcclusionShadersMode.HardOcclusion : OcclusionShadersMode.SoftOcclusion;
                if (!_mgr.enabled)
                {
                    _mgr.enabled = true;
                    _enabledAt = Time.realtimeSinceStartup;
                    FirstDepthMs = -1f;
                }
            }

            void Update()
            {
                if (_enabledAt < 0f || FirstDepthMs >= 0f || _mgr == null || !_mgr.IsDepthAvailable) return;
                FirstDepthMs = (Time.realtimeSinceStartup - _enabledAt) * 1000f;
                Debug.Log("[VBS][AR] depth: first depth frame after " + FirstDepthMs.ToString("0") + " ms (" + _mgr.OcclusionShadersMode + ")");
            }
        }
    }
}
#endif
