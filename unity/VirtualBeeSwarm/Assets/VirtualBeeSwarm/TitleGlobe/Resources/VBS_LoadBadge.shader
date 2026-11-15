// The load wall's sticker (LoadWall.cs): Resources/load_badge ("Loading the meadow..." on a cream card) with a honey
// progress ring drawn here over its dark disc, and a honeycomb cell in the ring filling up with honey. Drawn after the
// dome and the wall, with no depth test.
Shader "VirtualBeeSwarm/LoadBadge"
{
    Properties
    {
        _MainTex ("Card", 2D) = "white" {}
        _Alpha ("Alpha", Range(0, 1)) = 1
        _Progress ("Progress", Range(0, 1)) = 0
        _Clock ("Clock (s, from the script)", Float) = 0
        _Ring ("Ring centre (uv) xy, radius (uv height) z, card aspect w", Vector) = (0.1465, 0.514, 0.3, 3.5556)
        _Honey ("Honey", Color) = (0.97, 0.73, 0.2, 1)
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
            Cull Off

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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Honey;
            float4 _Ring;
            float _Alpha, _Progress, _Clock;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                fixed4 c = tex2D(_MainTex, i.uv);
                float2 p = (i.uv - _Ring.xy) * float2(_Ring.w, 1.0) / _Ring.z; // 1 = the ring's outer radius
                float r = length(p);
                float aa = fwidth(r) * 1.2 + 1e-4;

                // the ring: a dim track, filled clockwise from the top
                float band = smoothstep(0.66 - aa, 0.66, r) * (1.0 - smoothstep(0.9, 0.9 + aa, r));
                float turn = frac(atan2(p.x, p.y) / 6.2831853 + 1.0);
                float filled = 1.0 - smoothstep(_Progress - 0.006, _Progress, turn);
                filled = _Progress >= 0.999 ? 1.0 : filled;
                fixed3 track = _Honey.rgb * 0.3;
                fixed3 fill = _Honey.rgb * (1.05 + 0.12 * sin(_Clock * 5.0 - turn * 12.0));
                c.rgb = lerp(c.rgb, lerp(track, fill, filled), band);

                // a honeycomb cell inside it, filling from the bottom
                float2 h = abs(p);
                float hexd = max(dot(h, float2(0.8660254, 0.5)), h.y); // flat-top hexagon
                float cell = 1.0 - smoothstep(0.42 - aa, 0.42, hexd);
                float wall = cell * smoothstep(0.35 - aa, 0.35, hexd);
                float level = lerp(-0.4, 0.4, _Progress) + 0.025 * sin(_Clock * 3.0 + p.x * 9.0);
                float honey = cell * (1.0 - wall) * step(p.y, level);
                c.rgb = lerp(c.rgb, _Honey.rgb * 0.55, wall);
                c.rgb = lerp(c.rgb, _Honey.rgb * (0.95 + 0.25 * saturate(level - p.y)), honey);

                c.a *= _Alpha;
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
