// The documentary wall of the load-in screen (TitleWall.cs, owned by LoadWall.cs): one open cylinder round the
// player, rows of small monitors, every row sliding sideways at its own speed. Two mosaic videos feed it (wall_strip_0/1:
// 16 x 4 tiles of 240x135 each, so eight strip rows); the scroll, the bezels, the glow and the collapse all happen here,
// so hundreds of moving screens cost two decoders. Mesh uv.x = 0..1 round the cylinder, uv.y = 0.._Rows (row units).
// One pass, alpha blended (the Quest compositor mixes passthrough by the eye buffer's alpha: at _Alpha 1 over the dark
// dome it writes alpha 1, so no passthrough shows), single-pass instanced / multiview stereo macros.
// By default ZTest Always + ZWrite Off in an overlay queue: drawn over everything in the room, whatever its depth.
Shader "VirtualBeeSwarm/TitleWall"
{
    Properties
    {
        _StripA ("Strip A (16 x 4 tiles)", 2D) = "black" {}
        _StripB ("Strip B (16 x 4 tiles)", 2D) = "black" {}
        _Alpha ("Alpha", Range(0, 1)) = 0.7
        _Rows ("Rows", Float) = 11
        _Around ("Tiles round the cylinder (a multiple of 16)", Float) = 32
        _SlotAspect ("Slot width / row pitch", Float) = 1.72
        _WallTime ("Scroll clock (s, from the script)", Float) = 0
        _Speed ("Scroll speed (tiles/s)", Float) = 0.45
        _Reveal ("Reveal", Range(0, 1)) = 1
        _Collapse ("Collapse", Range(0, 1)) = 0
        _Glow ("Screen glow", Range(0, 2)) = 0.6
        _Bezel ("Bezel color", Color) = (0.035, 0.03, 0.03, 1)
        _GlowColor ("Rim glow", Color) = (1, 0.84, 0.5, 1)
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 8
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            // the load wall hides the whole room: no depth test against it (nothing pokes through), no depth written
            ZTest [_ZTest]
            ZWrite [_ZWrite]
            Cull Back // the inside faces only: from outside (the wide capture camera) you look through to the far side

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 objDir : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _StripA, _StripB;
            fixed4 _Bezel, _GlowColor;
            float _Alpha, _Rows, _Around, _SlotAspect, _WallTime, _Speed, _Reveal, _Collapse, _Glow;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float4 p = v.vertex;
                // the collapse: the rows fold in toward eye level as the wall flies outward
                float c = _Collapse * _Collapse;
                p.y *= 1.0 - 0.92 * c;
                p.xz *= 1.0 + 0.6 * c;
                o.pos = UnityObjectToClipPos(p);
                o.uv = v.uv;
                o.objDir = p.xyz;
                return o;
            }

            float Hash1(float n) { return frac(sin(n * 12.9898 + 4.1414) * 43758.5453); }

            float Noise3(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                float n = i.x + i.y * 57.0 + i.z * 113.0;
                return lerp(lerp(lerp(Hash1(n), Hash1(n + 1.0), f.x), lerp(Hash1(n + 57.0), Hash1(n + 58.0), f.x), f.y),
                            lerp(lerp(Hash1(n + 113.0), Hash1(n + 114.0), f.x), lerp(Hash1(n + 170.0), Hash1(n + 171.0), f.x), f.y), f.z);
            }

            // one strip tile: sub 0..7 (0-3 strip A top to bottom, 4-7 strip B), col 0..15, st 0..1 inside the tile
            fixed3 Tile(float sub, float col, float2 st)
            {
                // half a texel in from the tile's edge: no bleed from the neighbour in the mosaic
                st = clamp(st, float2(0.006, 0.008), float2(0.994, 0.992));
                float row = fmod(sub, 4.0);
                float2 uv = float2((col + st.x) / 16.0, (3.0 - row + st.y) / 4.0);
                // no mips on a video target: tex2Dlod keeps the tile jumps from picking a blurry level
                return sub < 4.0 ? tex2Dlod(_StripA, float4(uv, 0, 0)).rgb : tex2Dlod(_StripB, float4(uv, 0, 0)).rgb;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float h = i.uv.y;
                float row = clamp(floor(h), 0.0, _Rows - 1.0);
                float fy = h - row;
                float hr = Hash1(row + 3.7);
                // every row its own speed; neighbours run opposite ways
                float dir = fmod(row, 2.0) < 0.5 ? 1.0 : -1.0;
                float speed = dir * lerp(0.45, 1.25, hr) * _Speed;
                float x = i.uv.x * _Around + _WallTime * speed + hr * 41.0;
                float slot = floor(x);
                float fx = x - slot;
                // eight strip rows; rows past eight reuse one, half a strip along (other tiles beside each other)
                float sub = fmod(row * 3.0 + 1.0, 8.0);
                float col = fmod(fmod(slot + (row >= 8.0 ? 8.0 : 0.0), 16.0) + 16.0, 16.0);

                // the monitor: a 16:9 screen in its slot, a thin dark bezel round it (same width on all sides)
                float bx = 0.045;
                float by = bx * _SlotAspect;
                float2 st = float2((fx - bx) / (1.0 - 2.0 * bx), (fy - by) / (1.0 - 2.0 * by));
                float2 outside = max(-st, st - 1.0) * float2(1.0 - 2.0 * bx, (1.0 - 2.0 * by) / _SlotAspect);
                float edgeDist = max(outside.x, outside.y); // > 0 on the bezel (in slot widths)
                fixed3 pic = Tile(sub, col, st);
                float tileHash = Hash1(slot * 7.31 + row * 19.17);
                pic *= lerp(0.86, 1.08, tileHash);
                // a monitor's falloff toward its corners, and a faint glass sheen across the top
                float2 cst = saturate(st) - 0.5;
                pic *= 1.0 - dot(cst, cst) * 0.55;
                pic += saturate(st.y - 0.62) * 0.09;
                pic *= 1.0 + _Glow * 0.18;

                fixed3 col3;
                if (edgeDist > 0.0)
                {
                    // the bezel picks up its screen's light near the glass, plus a warm hairline
                    float g = exp(-edgeDist / 0.018);
                    col3 = _Bezel.rgb + pic * g * 0.45 * _Glow + _GlowColor.rgb * g * 0.06 * _Glow;
                }
                else col3 = pic;

                // the reveal: rows light up from eye level outward
                float mid = (_Rows - 1.0) * 0.5;
                float rowOn = saturate(_Reveal * (mid + 2.0) * 1.15 - abs(row - mid));
                col3 *= lerp(0.25, 1.0, rowOn);
                float a = _Alpha * rowOn;
                // the top and bottom rows fade off into the room
                float edge = min(h, _Rows - h);
                a *= smoothstep(0.0, 1.4, edge);

                if (_Collapse > 0.001)
                {
                    float3 d = normalize(i.objDir);
                    float n = Noise3(d * 5.0 + 1.3) * 0.6 + Noise3(d * 13.0) * 0.4;
                    float cut = _Collapse * 1.12 - 0.04;
                    clip(n - cut);
                    float e = 1.0 - saturate((n - cut) / 0.07);
                    col3 = lerp(col3 * (1.0 + _Collapse), fixed3(1.0, 0.83, 0.38) * 1.3, e);
                    a = lerp(a, min(1.0, a + 0.25), e);
                }
                return fixed4(col3, a);
            }
            ENDCG
        }
    }
    Fallback Off
}
