// The title globe (TitleGlobe.cs): one sphere, unlit, the Orbis meadow footage inside it.
// Outside (_Inside 0): the video seen through a glass ball (the normal in a frame facing the ball from the eye, world up:
// a lens, the middle magnified, the edge squeezed; turning the head doesn't slide the footage), a honey fresnel rim and two fixed glints. Inside (_Inside 1, Cull Front): the same video wrapped round the
// player as an inside-out sphere, full width every 90 degrees, mirrored so the seams meet. _Dissolve eats it with a
// glowing edge. One pass, alpha blended, no ZWrite; single-pass instanced / multiview stereo macros for the Quest.
Shader "VirtualBeeSwarm/TitleGlobe"
{
    Properties
    {
        _MainTex ("Video", 2D) = "white" {}
        _Tint ("Tint", Color) = (1,1,1,1)
        _RimColor ("Rim", Color) = (1, 0.84, 0.42, 1)
        _Alpha ("Alpha", Range(0, 1)) = 1
        _Inside ("Inside (1 = the player is in it)", Float) = 0
        _Dissolve ("Dissolve", Range(0, 1)) = 0
        _Glow ("Glow (hover, shot change)", Range(0, 2)) = 0
        _VideoAspect ("Video aspect (w/h)", Float) = 1.7333
        _EdgeSoft ("Soft silhouette (while it grows round the player)", Range(0, 1)) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            // alpha channel premultiplied-style: the Quest compositor mixes passthrough by the eye buffer's alpha
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            Cull [_Cull]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 objDir : TEXCOORD0;
                float3 nV : TEXCOORD1;
                float3 wN : TEXCOORD2;
                float3 wV : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Tint, _RimColor;
            half _Alpha, _Inside, _Dissolve, _Glow, _VideoAspect, _EdgeSoft;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.objDir = v.vertex.xyz;
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wN = UnityObjectToWorldNormal(v.normal);
                o.wV = _WorldSpaceCameraPos - wp;
                // a frame looking from the eye at the ball's centre, world up (not the view matrix: head roll/turn)
                float3 c = mul(unity_ObjectToWorld, float4(0, 0, 0, 1)).xyz;
                float3 f = c - _WorldSpaceCameraPos;
                f = dot(f, f) > 1e-6 ? normalize(f) : float3(0, 0, 1);
                float3 r = cross(float3(0, 1, 0), f);
                r = dot(r, r) > 1e-6 ? normalize(r) : float3(1, 0, 0);
                float3 u = cross(f, r);
                o.nV = float3(dot(o.wN, r), dot(o.wN, u), -dot(o.wN, f));
                return o;
            }

            float Hash3(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float Noise3(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(Hash3(i), Hash3(i + float3(1, 0, 0)), f.x),
                                 lerp(Hash3(i + float3(0, 1, 0)), Hash3(i + float3(1, 1, 0)), f.x), f.y),
                            lerp(lerp(Hash3(i + float3(0, 0, 1)), Hash3(i + float3(1, 0, 1)), f.x),
                                 lerp(Hash3(i + float3(0, 1, 1)), Hash3(i + float3(1, 1, 1)), f.x), f.y), f.z);
            }

            float PingPong(float x) { return 1.0 - abs(frac(x * 0.5) * 2.0 - 1.0); }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 d = normalize(i.objDir);
                fixed4 col;
                half a = _Alpha;
                if (_Inside < 0.5)
                {
                    float3 nV = normalize(i.nV);
                    // a glass ball: the frame's middle (full height, a centre crop of the width), edges squeezed
                    float2 uv = nV.xy * (0.5 * (0.8 + 0.2 * saturate(nV.z)));
                    uv.x /= _VideoAspect;
                    col = tex2D(_MainTex, uv + 0.5) * _Tint;
                    float3 n = normalize(i.wN);
                    float3 v = normalize(i.wV);
                    float rim = pow(1.0 - saturate(dot(n, v)), 3.0);
                    col.rgb = lerp(col.rgb, _RimColor.rgb, saturate(rim * (0.8 + _Glow * 0.4)));
                    col.rgb += _RimColor.rgb * (rim * 0.5 + 0.12) * _Glow;
                    // two window glints, fixed toward the viewer, and a faint sheen across the top
                    float g1 = pow(saturate(dot(nV, normalize(float3(-0.38, 0.46, 0.80)))), 260.0);
                    float g2 = pow(saturate(dot(nV, normalize(float3(0.32, -0.38, 0.87)))), 700.0);
                    float sheen = pow(saturate(dot(nV, normalize(float3(-0.1, 0.75, 0.66)))), 24.0) * 0.12;
                    col.rgb += (g1 * 1.2 + g2 * 0.7 + sheen) * fixed3(1.0, 1.0, 0.94);
                    a *= lerp(1.0, saturate(dot(n, v) * 4.0), _EdgeSoft);
                }
                else
                {
                    // full width every 90 degrees, mirrored so the frames meet (and meet again behind)
                    float az = atan2(d.x, d.z) / UNITY_PI * 2.0;
                    float el = asin(clamp(d.y, -1.0, 1.0)) / (UNITY_PI * 0.23);
                    float2 uv = float2(PingPong(az + 0.5), saturate(el * 0.5 + 0.5));
                    col = tex2D(_MainTex, uv) * _Tint;
                    a *= lerp(0.72, 1.0, saturate(d.z * 0.5 + 0.5)); // the room shows through more behind
                    col.rgb = lerp(col.rgb, _RimColor.rgb, pow(abs(d.y), 8.0) * 0.35);
                }
                if (_Dissolve > 0.001)
                {
                    float n = Noise3(d * 4.0) * 0.6 + Noise3(d * 11.0) * 0.4;
                    float cut = _Dissolve * 1.12 - 0.04;
                    clip(n - cut);
                    float edge = 1.0 - saturate((n - cut) / 0.07);
                    col.rgb = lerp(col.rgb, fixed3(1.0, 0.83, 0.38) * 1.3, edge);
                    a = lerp(a, min(1.0, a + 0.25), edge);
                }
                col.a = a;
                return col;
            }
            ENDCG
        }
    }
    Fallback Off
}
