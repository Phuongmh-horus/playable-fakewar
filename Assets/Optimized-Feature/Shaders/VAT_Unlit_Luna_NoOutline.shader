Shader "OptimizedFeature/VAT_Unlit_Luna_NoOutline"
{
    Properties
    {
        _MainTex ("Base Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _Brightness ("Brightness", Range(0, 2)) = 1
        _Contrast ("Contrast", Range(0, 2)) = 1
        _VATTex ("VAT Positions (RGB = Offset)", 2D) = "black" {}
        _BoundingMin ("Bounding Min (XYZ)", Vector) = (-1, -1, -1, 0)
        _BoundingMax ("Bounding Max (XYZ)", Vector) = (1, 1, 1, 0)
        _NumFrames ("Total Frames", Float) = 30
        _NumVertices ("Total Vertices", Float) = 1000
        _VATTextureWidth ("VAT Texture Width", Float) = 1000
        _VATTextureHeight ("VAT Texture Height", Float) = 30
        _FrameIndexLower ("Current State Frame Index", Float) = 0
        _FrameIndexUpper ("Target State Frame Index", Float) = 0
        _BlendWeight ("Cross-fade Blend Weight (0 to 1)", Float) = 0
        [HideInInspector] _VATFrameData ("VAT Frame Data", Vector) = (0, 0, 0, 0)
        [HideInInspector] _DeathDesaturation ("Death Desaturation", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Includes/VAT_Core.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float2 uv2 : TEXCOORD1; // uv2.x = Vertex Index
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            sampler2D _MainTex;
            fixed4 _Color;
            half _Brightness;
            half _Contrast;
            float4 _MainTex_ST;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                float4 instanceFrameData = UNITY_ACCESS_INSTANCED_PROP(VATProps, _VATFrameData);
                float frameLower = instanceFrameData.x;
                float frameUpper = instanceFrameData.y;
                float blendW = instanceFrameData.z;

                float textureWidth = max(1.0, _VATTextureWidth);
                float textureHeight = max(1.0, _VATTextureHeight);

                // Sample Current State Position from VAT
                float lowerTexelIndex = frameLower * _NumVertices + v.uv2.x;
                float lowerTexelRow = floor(lowerTexelIndex / textureWidth);
                float2 lowerUV = float2(
                    (lowerTexelIndex - lowerTexelRow * textureWidth + 0.5) / textureWidth,
                    (lowerTexelRow + 0.5) / textureHeight);
                float3 rawPosLower = tex2Dlod(_VATTex, float4(lowerUV, 0, 0)).rgb;

                float3 rawPos = rawPosLower;
                if (blendW > 0.0001)
                {
                    float upperTexelIndex = frameUpper * _NumVertices + v.uv2.x;
                    float upperTexelRow = floor(upperTexelIndex / textureWidth);
                    float2 upperUV = float2(
                        (upperTexelIndex - upperTexelRow * textureWidth + 0.5) / textureWidth,
                        (upperTexelRow + 0.5) / textureHeight);
                    float3 rawPosUpper = tex2Dlod(_VATTex, float4(upperUV, 0, 0)).rgb;
                    rawPos = lerp(rawPosLower, rawPosUpper, blendW);
                }

                // Unpack from normalized [0, 1] range to Object Space Bounding Box
                float3 objectPos = lerp(_BoundingMin.xyz, _BoundingMax.xyz, rawPos);

                o.vertex = UnityObjectToClipPos(float4(objectPos, 1.0));
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                fixed4 col = tex2D(_MainTex, i.uv) * _Color;
                col.rgb = (col.rgb - 0.5) * _Contrast + 0.5;
                col.rgb *= _Brightness;
                fixed grayscale = dot(col.rgb, fixed3(0.299, 0.587, 0.114));
                col.rgb = lerp(col.rgb, grayscale.xxx, saturate(UNITY_ACCESS_INSTANCED_PROP(VATProps, _DeathDesaturation)));
                return col;
            }
            ENDCG
        }
    }
}
