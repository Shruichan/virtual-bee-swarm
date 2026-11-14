// Cheap unlit shader for every Virtual Bee Swarm placeholder: color (per-instance for GPU instancing) x optional texture
// x optional vertex color, an emission multiplier, and blend/zwrite/cull as material properties.
// One pass, no LightMode tag: renders in URP (SRPDefaultUnlit) and the built-in pipeline.
// Single-pass instanced / multiview stereo macros included for the Quest.
// Depth API occlusion (Meta XR Core 209, BiRP include; PLAN.md V9): EnvironmentDepthManager turns on the global
// keyword HARD_OCCLUSION or SOFT_OCCLUSION; with neither on (desktop, Linux, ar.occlusion=off) every occlusion line
// below compiles away and the variant is the same as before. _Occlude = 0 opts a material out (window, markers,
// shadows); _EnvironmentDepthBias (per instance) pushes contact surfaces 6 cm back so they don't flicker.
// Wing flap (keyword VBS_WINGS, material toggle _Wings; ONLY on materials whose meshes carry the tags, since a mesh
// without uv1/uv2 reads undefined values there): vertices tagged by art/blender/scripts/wings.py
// (uv1 = hinge (x, y) in mesh space, uv2 = (wing side +-1, blur side +-1)) rotate about the body axis (mesh Z) through
// the hinge. The stroke phase steps 0.382 of a cycle per rendered frame (_VbsWingFrame, set by WingClock.cs), so
// every frame shows a fresh point of the stroke at any refresh rate: no 72/90 Hz beat, it reads as a ~200 Hz
// buzz. The top and bottom faces of each wing split by up to _WingBlur degrees with the stroke speed (a motion-
// blur wedge, opaque, no alpha test). _WingFlap (per instance, 0..1) stills them, e.g. bees landed on the table.
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
        [Toggle(VBS_WINGS)] _Wings ("Wing flap (wing-tagged meshes only: bees, wasp/hornet wings)", Float) = 0
        _WingFlap ("Wing flap amount (per instance, 0 = still)", Range(0, 1)) = 1
        _WingAmp ("Wing stroke amplitude (deg)", Range(0, 90)) = 48
        _WingBlur ("Wing blur wedge (deg, at mid-stroke)", Range(0, 30)) = 12
        _WingHz ("Wing Hz when no frame clock runs (edit mode)", Float) = 31
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
            #pragma multi_compile_local_vertex _ VBS_WINGS
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
            #ifdef VBS_WINGS
                float2 wingHinge : TEXCOORD1; // wings.py: hinge (x, y), mesh space
                float2 wingFlag : TEXCOORD2;  // wings.py: (wing side +-1, blur side +-1); 0 on the body
            #endif
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
            float _WingAmp, _WingBlur, _WingHz;
            float _VbsWingFrame; // global, WingClock.cs: rendered frame count (0 = not running: _WingHz on _Time)

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(fixed4, _Color)
            #ifdef VBS_WINGS
                UNITY_DEFINE_INSTANCED_PROP(float, _WingFlap)
            #endif
            #ifdef VBS_OCCLUSION
                UNITY_DEFINE_INSTANCED_PROP(float, _EnvironmentDepthBias)
            #endif
            UNITY_INSTANCING_BUFFER_END(Props)
            #ifdef VBS_OCCLUSION
            half _Occlude;
            #endif

        #ifdef VBS_WINGS
            // Per-object stroke offset from the object's rotation, so both wing objects of one wasp (same parent
            // rotation) stay in step while bees and wasps differ. Works instanced or not.
            float WingSeed()
            {
                float3 a = normalize(unity_ObjectToWorld._m00_m10_m20 + 1e-6);
                float3 b = normalize(unity_ObjectToWorld._m01_m11_m21 + 1e-6);
                return frac(sin(dot(a, float3(12.9898, 78.233, 37.719)) + dot(b, float3(39.346, 11.135, 83.155))) * 43758.5453);
            }

            float4 WingFlap(float4 p, float2 hinge, float2 flag, float amount)
            {
                float w = abs(flag.x) * amount;
                if (w == 0.0) return p; // body vertices leave here
                float side = flag.x > 0.0 ? 1.0 : -1.0;
                float ph = _VbsWingFrame > 0.5 ? _VbsWingFrame * 0.381966 : _Time.y * _WingHz;
                float s, c;
                sincos((ph + WingSeed()) * 6.2831853, s, c);
                float2 d = p.xy - hinge;
                float ang = radians(_WingAmp * s + flag.y * _WingBlur * abs(c)) * w * side;
                float sa, ca;
                sincos(ang, sa, ca);
                p.xy = hinge + float2(d.x * ca - d.y * sa, d.x * sa + d.y * ca);
                return p;
            }
        #endif

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
            #ifdef VBS_WINGS
                v.vertex = WingFlap(v.vertex, v.wingHinge, v.wingFlag, UNITY_ACCESS_INSTANCED_PROP(Props, _WingFlap));
            #endif
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
