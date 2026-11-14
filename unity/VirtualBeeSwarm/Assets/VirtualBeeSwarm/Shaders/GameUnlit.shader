// Cheap unlit shader for every Virtual Bee Swarm placeholder: color (per-instance for GPU instancing) x optional texture
// x optional vertex color, an emission multiplier, and blend/zwrite/cull as material properties.
// One pass, no LightMode tag: renders in URP (SRPDefaultUnlit) and the built-in pipeline.
// Single-pass instanced / multiview stereo macros included for the Quest.
// Depth API occlusion (Meta XR Core 209, BiRP include; PLAN.md V9): EnvironmentDepthManager turns on the global
// keyword HARD_OCCLUSION or SOFT_OCCLUSION; with neither on (desktop, Linux, ar.occlusion=off) every occlusion line
// below compiles away and the variant is the same as before. _Occlude = 0 opts a material out (window, markers,
// shadows); _EnvironmentDepthBias (per instance) pushes contact surfaces 6 cm back so they don't flicker.
Shader "VirtualBeeSwarm/Unlit"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Texture", 2D) = "white" {}
        _Emission ("Emission (brightness multiplier)", Range(0, 4)) = 1
        [Toggle] _UseVertexColor ("Use vertex color (bees, particles)", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Toggle] _Occlude ("Hidden by real objects (Depth API)", Float) = 1
        _EnvironmentDepthBias ("Env depth bias (0.06 on contact surfaces)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            #pragma target 3.0
            #include "UnityCG.cginc"
            #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                #define VBS_OCCLUSION 1
                #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/BiRP/EnvironmentOcclusionBiRP.cginc"
            #endif

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            #ifdef VBS_OCCLUSION
                META_DEPTH_VERTEX_OUTPUT(1)
                float2 occ : TEXCOORD2; // x = depth bias, y = _Occlude
            #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            half _Emission;
            half _UseVertexColor;

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(fixed4, _Color)
            #ifdef VBS_OCCLUSION
                UNITY_DEFINE_INSTANCED_PROP(float, _EnvironmentDepthBias)
            #endif
            UNITY_INSTANCING_BUFFER_END(Props)
            #ifdef VBS_OCCLUSION
            half _Occlude;
            #endif

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                fixed4 vc = lerp(fixed4(1, 1, 1, 1), v.color, _UseVertexColor);
                o.color = vc * UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
            #ifdef VBS_OCCLUSION
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(o, v.vertex);
                o.occ = float2(UNITY_ACCESS_INSTANCED_PROP(Props, _EnvironmentDepthBias), _Occlude);
            #endif
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                c.rgb *= _Emission;
            #ifdef VBS_OCCLUSION
                if (i.occ.y > 0.5) { META_DEPTH_OCCLUDE_OUTPUT_PREMULTIPLY(i, c, i.occ.x); }
            #endif
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
