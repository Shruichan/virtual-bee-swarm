// Beeswax (the rigged hero, skinned): VirtualBeeSwarm/Unlit plus a cheap fixed-direction wrap shade and a rim, so his round
// shapes read in passthrough without scene lights. Colour = _Albedo (his Blender colour) x _Color (white; BeeswaxController's
// dusty tint sets _Color/_BaseColor through a MaterialPropertyBlock and so greys him without losing the face).
// One pass, no LightMode tag (URP SRPDefaultUnlit + built-in), single-pass instanced / multiview stereo macros, the same
// Depth API occlusion keywords as GameUnlit (real objects hide him).
Shader "VirtualBeeSwarm/BeeswaxToon"
{
    Properties
    {
        _Albedo ("Albedo (his colour)", Color) = (1,1,1,1)
        _Color ("Tint (dusty, set by script)", Color) = (1,1,1,1)
        _Emission ("Brightness", Range(0, 4)) = 1
        _Shade ("Shade strength", Range(0, 1)) = 0.35
        _LightDir ("Light direction (world)", Vector) = (0.35, 1, 0.45, 0)
        _Rim ("Rim", Range(0, 1)) = 0.18
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Toggle] _Occlude ("Hidden by real objects (Depth API)", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
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
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 nrm : TEXCOORD0;
                float3 view : TEXCOORD3;
            #ifdef VBS_OCCLUSION
                META_DEPTH_VERTEX_OUTPUT(1)
                float occ : TEXCOORD2;
            #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _Albedo;
            half _Emission, _Shade, _Rim;
            float4 _LightDir;
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(fixed4, _Color)
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
                o.nrm = UnityObjectToWorldNormal(v.normal);
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.view = _WorldSpaceCameraPos.xyz - wp;
            #ifdef VBS_OCCLUSION
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(o, v.vertex);
                o.occ = _Occlude;
            #endif
                return o;
            }

            fixed4 frag (v2f i, fixed facing : VFACE) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 n = normalize(i.nrm) * (facing > 0 ? 1 : -1);   // two-sided wings shade from both sides
                float3 v = normalize(i.view);
                float wrap = saturate(dot(n, normalize(_LightDir.xyz)) * 0.5 + 0.5);
                half shade = lerp(1 - _Shade, 1, wrap);
                half rim = pow(1 - saturate(dot(n, v)), 3) * _Rim;
                fixed4 c = _Albedo * UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                c.rgb = c.rgb * shade * _Emission + rim;
                c.a = 1;
            #ifdef VBS_OCCLUSION
                if (i.occ > 0.5) { META_DEPTH_OCCLUDE_OUTPUT_PREMULTIPLY(i, c, 0); }
            #endif
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
