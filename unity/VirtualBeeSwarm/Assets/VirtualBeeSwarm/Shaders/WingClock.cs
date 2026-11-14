using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Drives the wing flap in VirtualBeeSwarm/Unlit (GameUnlit.shader): sets the global _VbsWingFrame to the rendered
    /// frame count once per frame, as URP starts rendering. The shader steps the wing stroke 0.382 of a cycle per frame, so
    /// every frame shows a new point of the stroke at 72, 90 or 120 Hz alike. Nothing to place in the scene; without
    /// it (edit mode) the shader falls back to a time-based flap.
    /// The flap itself is the material keyword VBS_WINGS: <see cref="EnableOn"/> turns it on for a bee pool's material.
    /// </summary>
    public static class WingClock
    {
        static readonly int FrameId = Shader.PropertyToID("_VbsWingFrame");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            RenderPipelineManager.beginContextRendering -= Tick;
            RenderPipelineManager.beginContextRendering += Tick;
            Set();
        }

        static void Tick(ScriptableRenderContext context, System.Collections.Generic.List<Camera> cameras) => Set();

        /// <summary>
        /// Turns the wing flap (keyword VBS_WINGS, toggle _Wings) on for a material drawn with `mesh`, but only when the
        /// mesh carries the wing tags (uv1 + uv2, art/blender/scripts/wings.py or GameMeshes.CreateBeeMesh): the
        /// flap reads undefined values from a mesh without them. Returns whether it's on.
        /// </summary>
        public static bool EnableOn(Material mat, Mesh mesh)
        {
            if (mat == null) return false;
            bool tagged = mesh != null && mesh.HasVertexAttribute(VertexAttribute.TexCoord1) && mesh.HasVertexAttribute(VertexAttribute.TexCoord2);
            if (tagged) mat.EnableKeyword(Keyword); else mat.DisableKeyword(Keyword);
            if (mat.HasProperty(WingsId)) mat.SetFloat(WingsId, tagged ? 1f : 0f);
            return tagged;
        }

        public const string Keyword = "VBS_WINGS";
        static readonly int WingsId = Shader.PropertyToID("_Wings");

        public static void Set() => Shader.SetGlobalFloat(FrameId, 1 + (Time.frameCount & 0xFFFF));
    }
}
