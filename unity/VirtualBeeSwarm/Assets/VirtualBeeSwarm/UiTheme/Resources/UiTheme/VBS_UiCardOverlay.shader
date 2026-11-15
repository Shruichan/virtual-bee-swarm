// The caption card drawn always on top, just under its always-on-top text (which the open hive lid would otherwise
// leave floating bare when it covers the card). Vertex colour only; the card mesh is ordered back to front.
Shader "VirtualBeeSwarm/UiCardOverlay"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Transparent-1" "IgnoreProjector" = "True" }
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return fixed4(i.color.rgb, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
