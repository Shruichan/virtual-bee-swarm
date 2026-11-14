using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace VirtualBeeSwarm
{
    /// <summary>
    /// Cheap unlit materials that work in URP and the built-in pipeline, MaterialPropertyBlock helpers (allocation-free
    /// tinting) and caption wrapping. The "VirtualBeeSwarm/Unlit" shader (Shaders/GameUnlit.shader) is preferred: one
    /// pass, instancing + single-pass-stereo ready, optional vertex colors, blend modes as properties.
    /// </summary>
    public static class GameMaterials
    {
        public const string ShaderName = "VirtualBeeSwarm/Unlit";

        public static readonly int ColorId = Shader.PropertyToID("_Color");
        public static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"); // URP Unlit
        public static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        public static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");     // URP Unlit
        public static readonly int MainTexStId = Shader.PropertyToID("_MainTex_ST");
        public static readonly int BaseMapStId = Shader.PropertyToID("_BaseMap_ST");
        public static readonly int EmissionId = Shader.PropertyToID("_Emission");
        public static readonly int UseVertexColorId = Shader.PropertyToID("_UseVertexColor");
        public static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
        public static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
        public static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");
        public static readonly int CullId = Shader.PropertyToID("_Cull");

        public enum Blend { Opaque, Alpha, Additive }

        /// <summary>VirtualBeeSwarm/Unlit, else URP Unlit, else Unlit/Color.</summary>
        public static Shader FindUnlitShader()
        {
            var s = Shader.Find(ShaderName);
            if (s != null) return s;
            s = Shader.Find("Universal Render Pipeline/Unlit");
            if (s != null && GraphicsSettings.currentRenderPipeline != null) return s;
            s = Shader.Find("Unlit/Color");
            return s != null ? s : Shader.Find("Hidden/InternalErrorShader");
        }

        public static Material Create(string name, Color color, Blend blend = Blend.Opaque, bool vertexColors = false,
            float emission = 1f, bool instancing = true, bool doubleSided = false)
        {
            var mat = new Material(FindUnlitShader()) { name = name, enableInstancing = instancing };
            SetColor(mat, color);
            if (mat.HasProperty(EmissionId)) mat.SetFloat(EmissionId, emission);
            if (mat.HasProperty(UseVertexColorId)) mat.SetFloat(UseVertexColorId, vertexColors ? 1f : 0f);
            if (mat.HasProperty(CullId)) mat.SetFloat(CullId, doubleSided ? (float)CullMode.Off : (float)CullMode.Back);
            if (mat.HasProperty(SrcBlendId))
            {
                switch (blend)
                {
                    case Blend.Opaque:
                        mat.SetFloat(SrcBlendId, (float)BlendMode.One);
                        mat.SetFloat(DstBlendId, (float)BlendMode.Zero);
                        mat.SetFloat(ZWriteId, 1f);
                        mat.renderQueue = (int)RenderQueue.Geometry;
                        break;
                    case Blend.Alpha:
                        mat.SetFloat(SrcBlendId, (float)BlendMode.SrcAlpha);
                        mat.SetFloat(DstBlendId, (float)BlendMode.OneMinusSrcAlpha);
                        mat.SetFloat(ZWriteId, 0f);
                        mat.renderQueue = (int)RenderQueue.Transparent;
                        break;
                    case Blend.Additive:
                        mat.SetFloat(SrcBlendId, (float)BlendMode.SrcAlpha);
                        mat.SetFloat(DstBlendId, (float)BlendMode.One);
                        mat.SetFloat(ZWriteId, 0f);
                        mat.renderQueue = (int)RenderQueue.Transparent + 10;
                        break;
                }
            }
            return mat;
        }

        public static void SetColor(Material mat, Color c)
        {
            if (mat == null) return;
            if (mat.HasProperty(ColorId)) mat.SetColor(ColorId, c);
            if (mat.HasProperty(BaseColorId)) mat.SetColor(BaseColorId, c);
        }

        // ------------------------------------------------------------ per-renderer tint (no material instances, no GC)
        static MaterialPropertyBlock _mpb;

        public static void Tint(Renderer r, Color c)
        {
            if (r == null) return;
            _mpb = _mpb ?? new MaterialPropertyBlock();
            r.GetPropertyBlock(_mpb);
            _mpb.SetColor(ColorId, c);
            _mpb.SetColor(BaseColorId, c);
            r.SetPropertyBlock(_mpb);
        }

        public static void SetTexture(Renderer r, Texture tex, Vector4 scaleOffset)
        {
            if (r == null) return;
            _mpb = _mpb ?? new MaterialPropertyBlock();
            r.GetPropertyBlock(_mpb);
            if (tex != null)
            {
                _mpb.SetTexture(MainTexId, tex);
                _mpb.SetTexture(BaseMapId, tex);
            }
            _mpb.SetVector(MainTexStId, scaleOffset);
            _mpb.SetVector(BaseMapStId, scaleOffset);
            r.SetPropertyBlock(_mpb);
        }

        public static void ClearBlock(Renderer r)
        {
            if (r != null) r.SetPropertyBlock(null);
        }

        public static void TintAll(Renderer[] renderers, Color c)
        {
            if (renderers == null) return;
            for (int i = 0; i < renderers.Length; i++) Tint(renderers[i], c);
        }

        public static void ClearAll(Renderer[] renderers)
        {
            if (renderers == null) return;
            for (int i = 0; i < renderers.Length; i++) ClearBlock(renderers[i]);
        }

        // ------------------------------------------------------------ captions
        static readonly StringBuilder Sb = new StringBuilder(256);

        /// <summary>Greedy word wrap for TextMesh (which can't wrap). Allocates the result: call it when the text changes.</summary>
        public static string Wrap(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text ?? "";
            Sb.Length = 0;
            int lineLen = 0;
            int i = 0;
            while (i < text.Length)
            {
                int j = text.IndexOf(' ', i);
                if (j < 0) j = text.Length;
                int wordLen = j - i;
                if (lineLen > 0 && lineLen + 1 + wordLen > maxChars)
                {
                    Sb.Append('\n');
                    lineLen = 0;
                }
                else if (lineLen > 0)
                {
                    Sb.Append(' ');
                    lineLen++;
                }
                Sb.Append(text, i, wordLen);
                lineLen += wordLen;
                i = j + 1;
            }
            return Sb.ToString();
        }

        /// <summary>m:ss</summary>
        public static string Clock(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            int s = Mathf.FloorToInt(seconds);
            return (s / 60) + ":" + (s % 60).ToString("00");
        }
    }
}
