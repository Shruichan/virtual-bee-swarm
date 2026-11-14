// The swarm bee with Daniel's animation, still one instanced draw per swarm: a vertex animation texture (VAT) baked
// from the worker bee's Hover and Walk clips (art/blender/scripts/bake_vat.py -> BeeVAT/Editor/BeeVatImport.cs).
// _VatPos holds the object-space position of every source vertex (column) for every baked frame (row), RGBAHalf;
// _VatNrm the normal (RGBA8, n * 0.5 + 0.5). uv1 = (column, wing weight). Mesh space = the swarm bee's: 1 unit long
// on +Z, pivot at the body centre, so the pools' matrices (scale beeLength) work unchanged.
//
// Per instance: the phase comes from a hash of the instance index (no extra data from the pools); _Color tints like
// VirtualBeeSwarm/Unlit (the pools' idle/angry/dark/gold colours arrive as before); _WingFlap (the v32 per-instance
// flap amount, 0 = landed) switches to the Walk clip with still wings. The wings run on their own clock, faster than
// the legs: _VatWingHz beats a second, or, when WingClock.cs sets _VbsWingFrame, 0.382 of a beat per rendered frame
// (the same "buzz" as the v32 shader flap). Cheap half-Lambert from the baked normals (_Shade 0 = flat).
// Depth API occlusion exactly as GameUnlit.shader (HARD_OCCLUSION / SOFT_OCCLUSION, _Occlude, per-instance
// _EnvironmentDepthBias). One pass, no LightMode tag: URP draws it as SRPDefaultUnlit.
Shader "VirtualBeeSwarm/BeeVAT"
{
    Properties
    {
        _Color ("Color (per instance)", Color) = (1,1,1,1)
        _MainTex ("Texture", 2D) = "white" {}
        _Emission ("Emission (brightness multiplier)", Range(0, 4)) = 1
        [Toggle] _UseVertexColor ("Use vertex color", Float) = 1
        [NoScaleOffset] _VatPos ("VAT positions (RGBAHalf)", 2D) = "black" {}
        [NoScaleOffset] _VatNrm ("VAT normals (n*0.5+0.5)", 2D) = "gray" {}
        _VatSize ("VAT columns, rows", Vector) = (1, 1, 0, 0)
        _VatHover ("Hover: first row, frames, fps, wing beats", Vector) = (0, 1, 30, 0)
        _VatWalk ("Walk: first row, frames, fps, wing beats (frames 0 = none)", Vector) = (0, 0, 30, 0)
        _VatSpeed ("Clip speed (legs, body)", Range(0, 4)) = 1
        _VatWalkSpeed ("Walk speed", Range(0, 4)) = 1
        _VatWingHz ("Wing beats per second (no frame clock)", Range(0, 60)) = 16
        [Toggle] _VatBuzz ("Frame-stepped buzz when WingClock runs", Float) = 1
        _Shade ("Shading from baked normals (0 = flat)", Range(0, 1)) = 0.45
        _WingFlap ("Wing flap (per instance; < 0.5 = landed: Walk)", Range(0, 1)) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        [Toggle] _Occlude ("Hidden by real objects (Depth API)", Float) = 1
        _EnvironmentDepthBias ("Env depth bias", Float) = 0
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
            #pragma target 3.5
            #include "UnityCG.cginc"
            #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                #define VBS_OCCLUSION 1
                #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/BiRP/EnvironmentOcclusionBiRP.cginc"
            #endif

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float2 vat : TEXCOORD1; // x = VAT column (u), y = wing weight
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
                float2 occ : TEXCOORD2;
            #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            half _Emission;
            half _UseVertexColor;
            sampler2D _VatPos;
            sampler2D _VatNrm;
            float4 _VatSize, _VatHover, _VatWalk;
            float _VatSpeed, _VatWalkSpeed, _VatWingHz, _VatBuzz;
            half _Shade;
            float _VbsWingFrame; // global, WingClock.cs (v32); 0 when it isn't there

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(fixed4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float, _WingFlap)
            #ifdef VBS_OCCLUSION
                UNITY_DEFINE_INSTANCED_PROP(float, _EnvironmentDepthBias)
            #endif
            UNITY_INSTANCING_BUFFER_END(Props)
            #ifdef VBS_OCCLUSION
            half _Occlude;
            #endif

            float Seed()
            {
            #ifdef UNITY_INSTANCING_ENABLED
                return frac(sin((float)unity_InstanceID * 12.9898 + 0.731) * 43758.5453);
            #else
                return 0.37;
            #endif
            }

            float3 Fetch(sampler2D t, float u, float row)
            {
                return tex2Dlod(t, float4(u, (row + 0.5) / _VatSize.y, 0, 0)).xyz;
            }

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float seed = Seed();
                bool walk = UNITY_ACCESS_INSTANCED_PROP(Props, _WingFlap) < 0.5 && _VatWalk.y > 0.5;
                float4 clip = walk ? _VatWalk : _VatHover;
                float speed = (walk ? _VatWalkSpeed : _VatSpeed) * (0.9 + 0.2 * frac(seed * 7.13));
                float f = (_Time.y * speed * clip.z) + seed * clip.y;            // frames into the loop (legs, body)
                if (!walk && clip.w > 0.5 && v.vat.y > 0.5)
                {
                    float beats = (_VbsWingFrame > 0.5 && _VatBuzz > 0.5) ? _VbsWingFrame * 0.381966 : _Time.y * _VatWingHz;
                    f = (beats + seed * 5.0) * clip.y / clip.w;                  // wings: beats -> frames
                }
                float fi = floor(f);
                float a = f - fi;
                float r0 = clip.x + fmod(fi, clip.y);
                float r1 = clip.x + fmod(fi + 1.0, clip.y);
                float3 p = lerp(Fetch(_VatPos, v.vat.x, r0), Fetch(_VatPos, v.vat.x, r1), a);
                float3 n = Fetch(_VatNrm, v.vat.x, a < 0.5 ? r0 : r1) * 2.0 - 1.0;
                v.vertex = float4(p, 1.0);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                float3 nw = normalize(mul((float3x3)unity_ObjectToWorld, n) + 1e-5);
                half lit = saturate(0.5 + 0.5 * dot(nw, float3(0.32, 0.86, -0.4)));
                half shade = 1.0 - _Shade * (1.0 - lit);
                fixed4 vc = lerp(fixed4(1, 1, 1, 1), v.color, _UseVertexColor);
                o.color = vc * UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                o.color.rgb *= shade;
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
