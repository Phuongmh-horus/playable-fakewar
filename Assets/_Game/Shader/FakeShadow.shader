Shader "Horus/FakeShadow"
{
    Properties
    {
        [Enum(Opacity, 10, Additive, 1)] _BlendMode ("Mode", Float) = 10
        _MainColor ("Main Color", Color) = (0, 0, 0, 1)
        _ZeroOpacityStrength ("Zero Opacity Strength", Range(0.1, 8)) = 1
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
        }

        LOD 100
        Blend SrcAlpha [_BlendMode]
        Cull Off
        Lighting Off
        ZWrite Off

        Pass
        {
            Name "FAKE_SHADOW"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            fixed4 _MainColor;
            half _ZeroOpacityStrength;

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 color = _MainColor;
                color.a *= pow(saturate(input.uv.y), _ZeroOpacityStrength);
                return color;
            }
            ENDCG
        }
    }
}
