#if VBS_META
using UnityEngine;

namespace VirtualBeeSwarm.Meta
{
    /// <summary>
    /// Turns on fixed foveated rendering on the Quest. The Meta XR Foveation OpenXR feature is enabled in the project, but
    /// its level defaults to Off until code sets it. Medium + dynamic: only the edges of the view render at lower
    /// resolution, which the flat unlit style hides, and the GPU saves roughly 10-25% of its pixel cost.
    /// Self-installing (no scene or builder change); retries for a few seconds because the level can only be set once
    /// the XR session exists. Headset builds only.
    /// </summary>
    sealed class QuestFoveation : MonoBehaviour
    {
        const float TrySeconds = 10f;
        float _until;

#if UNITY_ANDROID && !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            var go = new GameObject("QuestFoveation");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<QuestFoveation>();
        }
#endif

        void Start() => _until = Time.realtimeSinceStartup + TrySeconds;

        void Update()
        {
            try
            {
                OVRManager.foveatedRenderingLevel = OVRManager.FoveatedRenderingLevel.Medium;
                OVRManager.useDynamicFoveatedRendering = true;
                if (OVRManager.foveatedRenderingLevel != OVRManager.FoveatedRenderingLevel.Off)
                {
                    Debug.Log("[VBS] foveation " + OVRManager.foveatedRenderingLevel + " (dynamic)");
                    Destroy(gameObject);
                    return;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[VBS] foveation not set: " + e.Message);
                Destroy(gameObject);
                return;
            }
            if (Time.realtimeSinceStartup > _until)
            {
                Debug.LogWarning("[VBS] foveation stayed Off (XR session or feature not ready)");
                Destroy(gameObject);
            }
        }
    }
}
#endif
