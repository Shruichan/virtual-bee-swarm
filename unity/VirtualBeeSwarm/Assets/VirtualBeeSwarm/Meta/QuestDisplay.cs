#if VBS_META
using System;
using UnityEngine;
using UnityEngine.XR;

namespace VirtualBeeSwarm.Meta
{
    /// <summary>
    /// Headset frame budget (QuestPerf): fixed foveated rendering High + dynamic, 72 Hz, CPU/GPU sustained high. Runs once after the first scene loads, Android player only (desktop captures keep theirs).
    /// Passthrough stays on: nothing here touches the passthrough layer or the camera's clear colour.
    /// </summary>
    static class QuestDisplay
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Apply()
        {
            if (Application.platform != RuntimePlatform.Android || !QuestPerf.On) return;
            var done = new System.Collections.Generic.List<string>();
            Try(done, "foveation high+dynamic", () =>
            {
                OVRManager.foveatedRenderingLevel = OVRManager.FoveatedRenderingLevel.High;
                OVRManager.useDynamicFoveatedRendering = true;
            });
            Try(done, "srp foveation", () => // the OpenXR SRP-foveation path (MetaQuestFeature foveatedRenderingApi = SRP)
            {
                var displays = new System.Collections.Generic.List<XRDisplaySubsystem>();
                SubsystemManager.GetSubsystems(displays);
                foreach (var d in displays) d.foveatedRenderingLevel = 1f;
            });
            Try(done, "72 Hz", () =>
            {
                if (OVRManager.display != null) OVRManager.display.displayFrequency = 72f;
            });
            Try(done, "cpu/gpu sustained high", () =>
            {
                OVRManager.suggestedCpuPerfLevel = OVRManager.ProcessorPerformanceLevel.SustainedHigh;
                OVRManager.suggestedGpuPerfLevel = OVRManager.ProcessorPerformanceLevel.SustainedHigh;
            });
            Debug.Log("[VBS][Perf] quest profile: " + string.Join(", ", done));
        }

        static void Try(System.Collections.Generic.List<string> done, string what, Action a)
        {
            try { a(); done.Add(what); }
            catch (Exception e) { done.Add(what + " FAILED (" + e.GetType().Name + ": " + e.Message + ")"); }
        }
    }
}
#endif
