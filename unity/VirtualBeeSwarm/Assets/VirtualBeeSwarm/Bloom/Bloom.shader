// The room bloom (Bloom/RoomBloom.cs): flower clumps on the real floor (one GPU-instanced draw) and vines on the real
// walls / pillars / table legs (one plain draw). Opaque, unlit vertex colour, Cull Off, no alpha test, no discard:
// growth happens in the VERTEX stage, so a part that hasn't been born yet is collapsed to a point (zero area, nothing
// rasterised) and Adreno keeps its early depth test.
// Vertex data (BloomMeshes.cs / BloomVines.cs):
//   COLOR      rgb = baked colour, a = petal mask (0..1: how much the per-instance palette moves it to the alt colour)
//   TEXCOORD0  x = birth offset (s, after the instance's birth), y = sway weight, z = bloom slot (0..11; < 0 = always)
//   TEXCOORD1  pivot: the part scales up from here (a bloom's base, a vine ring's spine point)
//   TEXCOORD2  from: the pivot slides here -> pivot while it grows (a vine ring starts at the previous ring)
//   TEXCOORD3  rgb = alt petal colour
// Per instance: _BloomData = (birth s, palette 0..1, sway phase, slot mask bits 0..4095). Per draw (MPB): _BloomT
// (seconds since the wave started), _BloomClock (Time.time, sway), _BloomScale (1; 0 = wilted away).
// Global _VbsBloomPlayer = (x, z, radius, fade): instanced clumps fold away under the player's feet (_FeetFold 1).
// Depth API occlusion exactly as GameUnlit.shader (HARD_OCCLUSION / SOFT_OCCLUSION, _Occlude, _EnvironmentDepthBias).
Shader "VirtualBeeSwarm/Bloom"
{
    Properties
    {
        _Emission ("Brightness", Range(0, 2)) = 1
        _BloomT ("Seconds since the wave started (code)", Float) = 1000
        _BloomClock ("Sway clock (code)", Float) = 0
        _BloomScale ("Overall size (code; 0 = wilted)", Range(0, 1)) = 1
        _BloomGrow ("Grow seconds per part", Float) = 0.6
        _Wind ("Sway (m at weight 1)", Float) = 0.02
        _WindHz ("Sway speed (rad/s)", Float) = 1.6
        _FeetFold ("Fold away under the player's feet (instanced clumps)", Float) = 1
        _BloomData ("Per instance: birth, palette, phase, mask", Vector) = (0, 0, 0, 4095)
        [Toggle] _Occlude ("Hidden by real objects (Depth API)", Float) = 1
        _EnvironmentDepthBias ("Env depth bias (0.06 on contact surfaces)", Float) = 0.06
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            Cull Off
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling
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
                fixed4 color : COLOR;
                float4 grow : TEXCOORD0;
                float3 pivot : TEXCOORD1;
                float3 from : TEXCOORD2;
                float3 alt : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
            #ifdef VBS_OCCLUSION
                META_DEPTH_VERTEX_OUTPUT(0)
                float2 occ : TEXCOORD1; // x = depth bias, y = _Occlude
            #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            half _Emission;
            float _BloomT, _BloomClock, _BloomScale, _BloomGrow, _Wind, _WindHz, _FeetFold;
            float4 _VbsBloomPlayer; // global: x, z, radius, fade

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BloomData)
            #ifdef VBS_OCCLUSION
                UNITY_DEFINE_INSTANCED_PROP(float, _EnvironmentDepthBias)
            #endif
            UNITY_INSTANCING_BUFFER_END(Props)
            #ifdef VBS_OCCLUSION
            half _Occlude;
            #endif

            float EaseOutBack(float k)
            {
                const float c1 = 1.70158;
                const float c3 = c1 + 1.0;
                float x = k - 1.0;
                return 1.0 + c3 * x * x * x + c1 * x * x;
            }

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float4 d = UNITY_ACCESS_INSTANCED_PROP(Props, _BloomData);
                float k = saturate((_BloomT - d.x - v.grow.x) / max(_BloomGrow, 0.01));
                float s = (k > 0.0 ? EaseOutBack(k) : 0.0) * _BloomScale;
                if (v.grow.z >= 0.0) s *= fmod(floor(d.w / exp2(v.grow.z)), 2.0); // this slot is off for this clump

                // feet: the clump (instance origin = its base on the floor) folds away inside the player's circle
                float3 origin = float3(unity_ObjectToWorld[0].w, unity_ObjectToWorld[1].w, unity_ObjectToWorld[2].w);
                float feet = saturate((length(origin.xz - _VbsBloomPlayer.xy) - _VbsBloomPlayer.z) / max(_VbsBloomPlayer.w, 0.01));
                s *= lerp(1.0, feet * feet * (3.0 - 2.0 * feet), _FeetFold);

                float3 base = lerp(v.from, v.pivot, saturate(k * 1.4));
                float3 p = base + (v.vertex.xyz - v.pivot) * s;
                float w = v.grow.y * s;
                float t = _BloomClock * _WindHz + d.z;
                p.xz += _Wind * w * float2(sin(t + p.y * 3.0), cos(t * 0.83 + p.y * 2.3));
                float4 vtx = float4(p, 1.0);

                o.pos = UnityObjectToClipPos(vtx);
                fixed3 c = lerp(v.color.rgb, v.alt, saturate(v.color.a * d.y));
                o.color = fixed4(c * _Emission, 1.0);
            #ifdef VBS_OCCLUSION
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(o, vtx);
                o.occ = float2(UNITY_ACCESS_INSTANCED_PROP(Props, _EnvironmentDepthBias), _Occlude);
            #endif
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                fixed4 c = i.color;
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
