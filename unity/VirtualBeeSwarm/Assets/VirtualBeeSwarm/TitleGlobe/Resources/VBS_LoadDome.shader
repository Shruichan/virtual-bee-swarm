// The load wall's dome (LoadWall.cs): an inside-out sphere round the player's eyes that closes the monitor cylinder
// above and below. A warm dark gradient, a faint honeycomb on the ceiling and the floor with a few cells breathing, some
// pollen motes, and a soft honey glow round the feet. Opaque at _Alpha 1 (it writes alpha 1 into the eye buffer, so the
// Quest compositor shows no passthrough), drawn in an overlay queue with no depth test: nothing in the room pokes through,
// whatever its depth. _Collapse burns it away with the same noise as the wall's rows. Object space = world axes (the
// dome never turns). Single-pass instanced / multiview stereo macros.
Shader "VirtualBeeSwarm/LoadDome"
{
    Properties
    {
        _Alpha ("Alpha", Range(0, 1)) = 1
        _Collapse ("Collapse", Range(0, 1)) = 0
        _Clock ("Clock (s, from the script)", Float) = 0
        _EyeHeight ("Eyes over the floor (m)", Float) = 1.55
        _CeilingHeight ("Ceiling over the eyes (m)", Float) = 1.3
        _Cell ("Honeycomb cell (m)", Float) = 0.34
        _Horizon ("Horizon", Color) = (0.085, 0.056, 0.03, 1)
        _Cap ("Ceiling and floor", Color) = (0.017, 0.012, 0.008, 1)
        _Honey ("Honey", Color) = (1, 0.72, 0.2, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Overlay" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZTest Always
            ZWrite Off
            Cull Front // seen from inside

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _Horizon, _Cap, _Honey;
            float _Alpha, _Collapse, _Clock, _EyeHeight, _CeilingHeight, _Cell;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            float Hash1(float n) { return frac(sin(n * 12.9898 + 4.1414) * 43758.5453); }
            // integer cell ids in, no sin: the same value over a whole cell (no per-pixel speckle), steady on mobile GPUs
            float Hash2(float2 p)
            {
                float3 p3 = frac(p.xyx * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            float Noise3(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                float n = i.x + i.y * 57.0 + i.z * 113.0;
                return lerp(lerp(lerp(Hash1(n), Hash1(n + 1.0), f.x), lerp(Hash1(n + 57.0), Hash1(n + 58.0), f.x), f.y),
                            lerp(lerp(Hash1(n + 113.0), Hash1(n + 114.0), f.x), lerp(Hash1(n + 170.0), Hash1(n + 171.0), f.x), f.y), f.z);
            }

            // pointy-top hex cells one unit across: xy = offset from the cell's centre, zw = the centre (its id)
            float4 Hex(float2 p)
            {
                const float2 r = float2(1.0, 1.7320508);
                const float2 h = r * 0.5;
                float2 a = p - r * floor(p / r) - h;
                float2 q = p - h;
                float2 b = q - r * floor(q / r) - h;
                float2 gv = dot(a, a) < dot(b, b) ? a : b;
                return float4(gv, p - gv);
            }

            // 0.5 at a cell's centre, 0 on its edge
            float HexInset(float2 gv)
            {
                gv = abs(gv);
                return 0.5 - max(dot(gv, float2(0.5, 0.8660254)), gv.x);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 d = normalize(i.dir);
                float e = d.y;
                float ae = abs(e);
                fixed3 col = lerp(_Horizon.rgb, _Cap.rgb, smoothstep(0.05, 0.8, ae));
                // a faint warm band at eye level, behind the screens
                col += _Honey.rgb * 0.02 * (1.0 - smoothstep(0.0, 0.35, ae));

                // the caps: honeycomb on a ceiling over the head and on the floor
                float plane = e > 0.0 ? _CeilingHeight : _EyeHeight;
                float2 q = d.xz / max(ae, 0.06) * plane; // metres on that plane
                float cap = smoothstep(0.24, 0.62, ae);
                float4 hx = Hex(q / _Cell);
                float inset = HexInset(hx.xy);
                float w = fwidth(inset) * 1.5 + 1e-4;
                float wax = 1.0 - smoothstep(0.018, 0.018 + w, inset); // the wax walls between cells
                float id = Hash2(floor(hx.zw * 2.0 + 0.5) + (e > 0.0 ? 0.0 : 37.0)); // the centre rounded: exactly one id per cell
                float breathe = 0.5 + 0.5 * sin(_Clock * 0.8 + id * 6.2831853);
                col += _Honey.rgb * wax * cap * 0.06;
                col += _Honey.rgb * cap * step(0.86, id) * breathe * 0.035 * smoothstep(0.0, 0.25, inset); // a few warm cells
                float mote = step(0.92, frac(id * 7.13)) * (1.0 - smoothstep(0.025, 0.06, length(hx.xy)));
                col += fixed3(1.0, 0.9, 0.65) * mote * cap * (0.25 + 0.25 * sin(_Clock * 2.3 + id * 40.0));

                // a soft honey pool of light round the feet
                if (e < 0.0)
                {
                    float r2 = dot(q, q);
                    col += _Honey.rgb * exp(-r2 / 0.7) * 0.15 * smoothstep(0.15, 0.5, ae);
                }

                float a = _Alpha;
                if (_Collapse > 0.001)
                {
                    float n = Noise3(d * 5.0 + 1.3) * 0.6 + Noise3(d * 13.0) * 0.4;
                    float cut = _Collapse * 1.12 - 0.04;
                    clip(n - cut);
                    float edge = 1.0 - saturate((n - cut) / 0.07);
                    col = lerp(col, fixed3(1.0, 0.83, 0.38) * 1.3, edge);
                }
                return fixed4(col, a);
            }
            ENDCG
        }
    }
    Fallback Off
}
