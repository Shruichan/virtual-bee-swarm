// The floor meadow (Bloom/MeadowField.cs): grass blades, wild flowers and a few tall accents in 0.45 m tiles, one
// GPU-instanced draw per 1023 tiles. Opaque, unlit vertex colour, Cull Off, no alpha test: anything hidden is collapsed
// to a point in the VERTEX stage (Adreno keeps early depth).
// Vertex data (MeadowField.BuildTile):
//   COLOR      rgb = healthy colour, a = petal mask (how far the per-instance palette moves it to TEXCOORD3)
//   TEXCOORD0  x = health threshold (the part shows once the local health passes it), y = sway weight,
//              z = thinning rank 0..1 (high ranks drop out with distance), w = 1 for a tall accent (never near the player),
//              -1 for the ground disc (always there; lifted a hair per tile so overlapping discs never z-fight)
//   TEXCOORD1  pivot: the blade / flower root in tile space (it scales from here)
//   TEXCOORD2  rgb = wilted colour (sick hive)
//   TEXCOORD3  rgb = alt petal colour
// Per instance _MeadowData = (palette 0..1, sway phase, height scale, health offset: < 0 near the hive).
// Globals: _VbsMeadow = (health, clock, -, -), _VbsMeadowPlayer = (x, z, clear radius, fade),
//          _VbsMeadowHive = (x, z, keep-out radius, path half width), _VbsMeadowStart = (player start x, z, -, -),
//          _VbsMeadowYard = (centre x, z, half across, half along; < 0 = none), _VbsMeadowYardFwd = (fwd x, z, height kept, edge m).
// Depth API occlusion exactly as Bloom.shader.
Shader "VirtualBeeSwarm/Meadow"
{
    Properties
    {
        _Emission ("Brightness", Range(0, 2)) = 1
        _Wind ("Sway (m at weight 1)", Float) = 0.03
        _WindHz ("Sway speed (rad/s)", Float) = 1.3
        _ThinNear ("Thinning starts (m from the player)", Float) = 2.6
        _ThinFar ("Thinning ends (m)", Float) = 5.5
        _ThinKeep ("Share kept far away", Range(0, 1)) = 0.45
        _MeadowData ("Per instance: palette, phase, height, health offset", Vector) = (0, 0, 1, 0)
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
                float4 info : TEXCOORD0;
                float3 pivot : TEXCOORD1;
                float3 wilt : TEXCOORD2;
                float3 alt : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
            #ifdef VBS_OCCLUSION
                META_DEPTH_VERTEX_OUTPUT(0)
                float2 occ : TEXCOORD1;
            #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            half _Emission;
            float _Wind, _WindHz, _ThinNear, _ThinFar, _ThinKeep;
            float4 _VbsMeadow, _VbsMeadowPlayer, _VbsMeadowHive, _VbsMeadowStart, _VbsMeadowYard, _VbsMeadowYardFwd;

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _MeadowData)
            #ifdef VBS_OCCLUSION
                UNITY_DEFINE_INSTANCED_PROP(float, _EnvironmentDepthBias)
            #endif
            UNITY_INSTANCING_BUFFER_END(Props)
            #ifdef VBS_OCCLUSION
            half _Occlude;
            #endif

            float SegDist(float2 p, float2 a, float2 b)
            {
                float2 ab = b - a;
                float t = saturate(dot(p - a, ab) / max(dot(ab, ab), 1e-4));
                return length(p - (a + ab * t));
            }

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float4 d = UNITY_ACCESS_INSTANCED_PROP(Props, _MeadowData);
                float3 root = mul(unity_ObjectToWorld, float4(v.pivot, 1.0)).xyz;

                // health: the sick hive wilts what is near it; every solved station brings more back
                float h = saturate(_VbsMeadow.x + d.w);
                float show = smoothstep(v.info.x, v.info.x + 0.1, h);

                // the player's circle, the stand and distance thinning
                float toPlayer = length(root.xz - _VbsMeadowPlayer.xy);
                float feet = saturate((toPlayer - _VbsMeadowPlayer.z) / max(_VbsMeadowPlayer.w, 0.01));
                feet = feet * feet * (3.0 - 2.0 * feet);
                float toHive = length(root.xz - _VbsMeadowHive.xy);
                // under the hive stand: cut short (grass between the legs, nothing up into the box), not bare
                float stand = saturate((toHive - _VbsMeadowHive.z) / 0.12);
                float keep = lerp(1.0, _ThinKeep, saturate((toPlayer - _ThinNear) / max(_ThinFar - _ThinNear, 0.01)));
                float thin = v.info.z <= keep ? 1.0 : 0.0;
                float accent = v.info.w > 0.5 ? step(1.6, length(root.xz - _VbsMeadowStart.xy)) * step(0.9, toHive) : 1.0;
                // a trodden path from where the player started to the stand: shorter there
                float path = saturate((SegDist(root.xz, _VbsMeadowStart.xy, _VbsMeadowHive.xy) - _VbsMeadowHive.w) / 0.25);

                // the mown yard round the big hive and its props: short grass and low flowers, nothing buried
                float2 yd = root.xz - _VbsMeadowYard.xy;
                float2 yf = _VbsMeadowYardFwd.xy;
                float yx = abs(dot(yd, float2(yf.y, -yf.x))) - _VbsMeadowYard.z;
                float yz = abs(dot(yd, yf)) - _VbsMeadowYard.w;
                float yard = _VbsMeadowYard.z < 0.0 ? 1.0 : saturate(max(yx, yz) / max(_VbsMeadowYardFwd.w, 0.01));
                accent *= v.info.w > 0.5 ? step(0.5, yard) : 1.0; // only the tall spikes leave the yard

                float s = show * thin * accent;
                // under the player's feet it is trampled flat, not gone: the floor never shows
                float tall = d.z * lerp(0.62, 1.0, h) * lerp(0.55, 1.0, path) * lerp(0.45, 1.0, feet) * lerp(_VbsMeadowYardFwd.z, 1.0, yard) * lerp(0.25, 1.0, stand);

                float3 local = v.vertex.xyz - v.pivot;
                local.xz *= s;
                local.y *= s * tall;
                // sick: droop the tips sideways a little
                float droop = (1.0 - h) * v.info.y;
                local.x += droop * 0.05 * s;
                float3 p = v.pivot + local;
                if (v.info.w < -0.5) p.y += frac(d.y * 0.1591) * 0.004; // the ground disc

                float4 vtx = float4(p, 1.0);
                float4 world = mul(unity_ObjectToWorld, vtx);
                float w = v.info.y * s;
                float t = _VbsMeadow.y * _WindHz + d.y + dot(root.xz, float2(1.7, 1.1));
                world.xz += _Wind * w * float2(sin(t), cos(t * 0.83 + 0.6));

                o.pos = mul(UNITY_MATRIX_VP, world);
                fixed3 c = lerp(v.color.rgb, v.alt, saturate(v.color.a * d.x));
                fixed3 sick = v.wilt;
                c = lerp(sick, c, smoothstep(0.1, 0.95, h));
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
