Shader "Unlit/TextureGradient"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        [KeywordEnum(Basic, Radial, Reflected)] _GradientType ("Gradient Type", Float) = 0
        _GradientColor ("Gradient Color", Color) = (1,1,1,0)
        _ReflectedCenterWidth ("Reflected Center Width", Range(0.1, 4)) = 1
        _ReflectedEdgeDistance ("Reflected Edge Distance", Range(0, 1)) = 1
        [Toggle(_WATER_MOVE)] _WaterMove ("Water Move", Float) = 0
        _WaterMoveSpeedX ("Water Move X Speed", Range(0, 2)) = 0.1
        _WaterMoveSpeedY ("Water Move Y Cycles Per Second", Range(0, 5)) = 1
        _WaterMoveMaxOffsetY ("Water Move Y Max Offset", Float) = 0.05
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local _GRADIENTTYPE_BASIC _GRADIENTTYPE_RADIAL _GRADIENTTYPE_REFLECTED
            #pragma shader_feature_local _WATER_MOVE
            #pragma multi_compile_fog
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float2 gradientUV : TEXCOORD1;
                UNITY_FOG_COORDS(2)
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _GradientColor;
            half _ReflectedCenterWidth;
            half _ReflectedEdgeDistance;
            half _WaterMoveSpeedX;
            half _WaterMoveSpeedY;
            half _WaterMoveMaxOffsetY;
            float _TextureGradientWaterMovePlayMode;

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);

                #if defined(_WATER_MOVE)
                    float waterMoveTime = _Time.y * _TextureGradientWaterMovePlayMode;
                    half waterMoveY = 1.0h - abs(frac(waterMoveTime * _WaterMoveSpeedY) * 2.0h - 1.0h);
                    waterMoveY = waterMoveY * waterMoveY * (3.0h - 2.0h * waterMoveY);
                    output.uv += float2(waterMoveTime * _WaterMoveSpeedX, waterMoveY * _WaterMoveMaxOffsetY);
                #endif

                output.gradientUV = input.uv;
                UNITY_TRANSFER_FOG(output, output.vertex);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, input.uv);
                half gradient = input.gradientUV.y;

                #if defined(_GRADIENTTYPE_RADIAL)
                    gradient = saturate(length(input.gradientUV - 0.5h) * 2.0h);
                #elif defined(_GRADIENTTYPE_REFLECTED)
                    half reflected = abs(input.gradientUV.x * 2.0h - 1.0h);
                    reflected = _ReflectedEdgeDistance > 0.0h
                        ? saturate(reflected / _ReflectedEdgeDistance)
                        : 1.0h;
                    gradient = pow(reflected, _ReflectedCenterWidth);
                #endif

                color.rgb = lerp(color.rgb, _GradientColor.rgb, saturate(gradient) * _GradientColor.a);
                UNITY_APPLY_FOG(input.fogCoord, color);
                return color;
            }
            ENDCG
        }
    }

    Fallback "Unlit/Texture"
}
