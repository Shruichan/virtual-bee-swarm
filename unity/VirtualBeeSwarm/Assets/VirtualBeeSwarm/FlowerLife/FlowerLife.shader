// Flower life (FlowerLife/FlowerLife.cs): the small flowers on the real table, along its rim, on the window sill and in
// a thin ring on the real floor. One GPU-instanced draw per species; opaque, unlit vertex colour, Cull Off, no alpha
// test, no discard. Everything that moves is in the VERTEX stage (Adreno keeps its early depth test):
//   sway      per-instance phase + a gust that travels across the room (_VbsFlWind), weight = vertex alpha
//   wilt      vitality 0 = drooped over, shrunk and dry-brown; 1 = upright and in colour (stations perk it up)
//   lean      heads lean toward the player's head (_VbsFlPlayer)
//   hands     blooms bend away from a fingertip that comes close (_VbsFlHand0 / _VbsFlHand1)
//   bees      a perched bee dips the bloom (load)
// Mesh: POSITION metres, pivot at the stem base, +Y up; COLOR rgb = baked colour, a = sway weight (0 at the base, 1 at
// the head). The art lane's bloom kit (Art/Bloom/Resources/bloom_*.fbx) has this layout; FlowerLifeMeshes builds the
// same when the kit isn't there.
// Per instance: _FlData = (vitality 0..1.3, phase, bee load 0..1, grow 0..1).
// Globals (FlowerLife.cs, once a frame): _VbsFlClock, _VbsFlWind (dir x, dir z, tremble 0..1, 0),
// _VbsFlPlayer (xyz, lean 0..1), _VbsFlHand0/1 (xyz, radius; radius 0 = no hand).
// Depth API occlusion exactly as GameUnlit.shader (HARD_OCCLUSION / SOFT_OCCLUSION, _Occlude, _EnvironmentDepthBias).
Shader "VirtualBeeSwarm/FlowerLife"
{
    Properties
    {
        _Emission ("Brightness", Range(0, 2)) = 1
        _FlData ("Per instance: vitality, phase, bee load, grow", Vector) = (1, 0, 0, 1)
        _Wind ("Sway (fraction of the height, at weight 1)", Float) = 0.07
        _WindHz ("Sway speed (rad/s)", Float) = 1.7
        _Lean ("Lean toward the player (fraction of the height)", Float) = 0.14
        _Droop ("Wilt: how far a dead bloom hangs over (fraction of the height)", Float) = 0.6
        _DryColor ("Wilted tint", Color) = (0.66, 0.55, 0.33, 1)
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
            float _Wind, _WindHz, _Lean, _Droop;
            fixed4 _DryColor;
            float _VbsFlClock;
            float4 _VbsFlWind, _VbsFlPlayer, _VbsFlHand0, _VbsFlHand1;

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _FlData)
            #ifdef VBS_OCCLUSION
                UNITY_DEFINE_INSTANCED_PROP(float, _EnvironmentDepthBias)
            #endif
            UNITY_INSTANCING_BUFFER_END(Props)
            #ifdef VBS_OCCLUSION
            half _Occlude;
            #endif

            float2 HandPush(float3 wp, float4 hand)
            {
                float3 d = wp - hand.xyz;
                float k = saturate(1.0 - length(d) / max(hand.w, 1e-4)) * step(1e-4, hand.w);
                return normalize(d.xz + float2(1e-5, 0.0)) * (k * k);
            }

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float4 d = UNITY_ACCESS_INSTANCED_PROP(Props, _FlData);
                float vit = saturate(d.x);
                float wilt = 1.0 - vit;
                float w = v.color.a;
                float w2 = w * w;

                float3 p = v.vertex.xyz * (d.w * lerp(0.74, 1.0, vit));
                float3 wp = mul(unity_ObjectToWorld, float4(p, 1.0)).xyz;
                float3 origin = float3(unity_ObjectToWorld[0].w, unity_ObjectToWorld[1].w, unity_ObjectToWorld[2].w);
                float hgt = max(wp.y - origin.y, 0.0);

                // wilt: the head hangs over to one side and sags; a perched bee dips it a little more
                float2 side = float2(cos(d.y * 1.7), sin(d.y * 1.7));
                wp.xz += side * (hgt * _Droop * wilt * w);
                wp.y -= hgt * (0.42 * wilt * w + 0.16 * d.z * w2);

                // sway: own phase + a gust travelling across the room; the angry swarm makes them tremble
                float t = _VbsFlClock * _WindHz + d.y;
                float gust = 0.5 + 0.5 * sin(dot(wp.xz, _VbsFlWind.xy) * 2.6 - _VbsFlClock * 1.3);
                float amp = _Wind * (0.4 + 0.6 * gust) * lerp(0.4, 1.0, vit);
                wp.xz += hgt * amp * w2 * float2(sin(t + wp.x * 5.0), cos(t * 0.83 + wp.z * 4.0));
                wp.xz += _VbsFlWind.xy * (hgt * amp * w2 * gust * 0.8);
                wp.xz += hgt * 0.035 * _VbsFlWind.z * w * float2(sin(_VbsFlClock * 31.0 + d.y * 7.0), cos(_VbsFlClock * 27.0 + d.y * 5.0));

                // heads lean toward the player; blooms bend away from a fingertip
                float2 toPlayer = _VbsFlPlayer.xz - origin.xz;
                wp.xz += normalize(toPlayer + float2(1e-5, 0.0)) * (hgt * _Lean * vit * w2 * _VbsFlPlayer.w);
                wp.xz += (HandPush(wp, _VbsFlHand0) + HandPush(wp, _VbsFlHand1)) * (0.06 * w);

                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1.0));

                fixed3 c = v.color.rgb;
                // petals (anything that isn't leaf-green) get a small per-plant shift so neighbours differ
                float petal = step(c.g, max(c.r, c.b) + 0.02);
                c *= 1.0 + 0.10 * sin(d.y * 3.0);
                c.r += petal * 0.07 * sin(d.y * 5.0);
                c.b += petal * 0.07 * cos(d.y * 4.0);
                fixed lum = dot(c, fixed3(0.3, 0.59, 0.11));
                fixed3 dry = lerp(fixed3(lum, lum, lum), c, 0.3) * _DryColor.rgb * 1.25;
                c = lerp(dry, c, smoothstep(0.15, 0.9, vit));
                o.color = fixed4(saturate(c) * (_Emission * (1.0 + 0.6 * max(d.x - 1.0, 0.0))), 1.0);
            #ifdef VBS_OCCLUSION
                o.posWorld = wp;
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
