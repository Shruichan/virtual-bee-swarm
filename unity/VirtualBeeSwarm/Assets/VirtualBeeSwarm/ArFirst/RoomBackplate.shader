// Footage only (ArFirst/CaptureBackplate.cs): a photograph of a real room, projected from the point it was shot from
// onto the capture scene's stand-in floor / walls / ceiling, so desktop footage reads as passthrough of a real room
// instead of a CG box. _MainTex is an equirectangular panorama; every fragment looks up the direction from _Origin
// (the panorama's eye point, world space) to itself, turned by _Yaw (the panorama azimuth that faces world +Z).
// _Dim / _Sat follow the game's passthrough dims (stakes dim, the Moon). Never used on the headset: there the room is
// the real passthrough.
Shader "VirtualBeeSwarm/RoomBackplate"
{
    Properties
    {
        _MainTex ("Panorama (equirectangular)", 2D) = "gray" {}
        _Origin ("Panorama eye point (world)", Vector) = (0, 1.6, 0, 0)
        _Yaw ("Panorama azimuth facing world +Z (radians)", Float) = 0
        _Dim ("Brightness", Range(0, 1)) = 1
        _Sat ("Saturation", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry-20" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            Cull Off
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _Origin;
            float _Yaw, _Dim, _Sat;

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = mul(unity_ObjectToWorld, v.vertex).xyz - _Origin.xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float az = _Yaw + atan2(d.x, d.z);
                float2 uv = float2(frac(az / 6.2831853), 0.5 + asin(clamp(d.y, -1.0, 1.0)) / 3.14159265);
                fixed3 c = tex2Dlod(_MainTex, float4(uv, 0, 0)).rgb;
                fixed lum = dot(c, fixed3(0.3, 0.59, 0.11));
                c = lerp(fixed3(lum, lum, lum), c, _Sat) * _Dim;
                return fixed4(c, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
