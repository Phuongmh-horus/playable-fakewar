Shader "OptimizedFeature/VAT_Unlit_Luna"
{
    Properties
    {
        _MainTex ("Base Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
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
        [HideInInspector] _VATBatchMode ("VAT Runtime Batch Mode", Float) = 0

        [HeaderGroup(Outline)]
        [Toggle(OUTLINE)] _Outline ("Enable Outline", Float) = 0
        _OutlineWidth ("Outline Width", Range(0, 0.1)) = 0.002
        [Toggle(OUTLINE_WIDTH_INDEPENDENT)] _OutlineWidthIndependent ("Outline Width Camera-Independent", Float) = 0
        _OutlineColor ("Outline Color", Color) = (0, 0, 0, 1)
        _OutlineZPos ("Outline Z Offset", Range(-0.1, 1)) = 0
        [Enum(Show, 8, Hide, 6)] _OutlineComp ("Interior Outlines", Float) = 8
        _OutlineGroup ("Outline Group", Float) = 0
        [HideInInspector] _OutlinePass ("Outline Pass", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="ForwardBase" }

            Stencil
            {
                Ref [_OutlineGroup]
                Pass [_OutlinePass]
            }

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
                float4 color : COLOR; // Vertex Color: Reserved for future Animation Layer mask
                float2 uv : TEXCOORD0;
                float2 uv2 : TEXCOORD1; // uv2.x = Vertex Index
                float4 vatBatchTransform0 : TEXCOORD2;
                float4 vatBatchTransform1 : TEXCOORD3;
                float4 vatBatchTransform2 : TEXCOORD4;
                float4 vatBatchFrame : TEXCOORD5;
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
            float4 _MainTex_ST;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                float instanceFrameLower = UNITY_ACCESS_INSTANCED_PROP(VATProps, _FrameIndexLower);
                float instanceFrameUpper = UNITY_ACCESS_INSTANCED_PROP(VATProps, _FrameIndexUpper);
                float instanceBlendW = UNITY_ACCESS_INSTANCED_PROP(VATProps, _BlendWeight);
                float frameLower = _VATBatchMode > 0.5 ? v.vatBatchFrame.x : instanceFrameLower;
                float frameUpper = _VATBatchMode > 0.5 ? v.vatBatchFrame.y : instanceFrameUpper;
                float blendW = _VATBatchMode > 0.5 ? v.vatBatchFrame.z : instanceBlendW;

                float textureWidth = max(1.0, _VATTextureWidth);
                float textureHeight = max(1.0, _VATTextureHeight);
                
                // Sample Current State Position from VAT
                float lowerTexelIndex = frameLower * _NumVertices + v.uv2.x;
                float lowerTexelRow = floor(lowerTexelIndex / textureWidth);
                float2 lowerUV = float2(
                    (lowerTexelIndex - lowerTexelRow * textureWidth + 0.5) / textureWidth,
                    (lowerTexelRow + 0.5) / textureHeight);
                float3 rawPosLower = tex2Dlod(_VATTex, float4(lowerUV, 0, 0)).rgb;

                // Sample Target State Position from VAT
                float upperTexelIndex = frameUpper * _NumVertices + v.uv2.x;
                float upperTexelRow = floor(upperTexelIndex / textureWidth);
                float2 upperUV = float2(
                    (upperTexelIndex - upperTexelRow * textureWidth + 0.5) / textureWidth,
                    (upperTexelRow + 0.5) / textureHeight);
                float3 rawPosUpper = tex2Dlod(_VATTex, float4(upperUV, 0, 0)).rgb;

                // Cross-fade blending between current and target animation states
                float3 rawPos = lerp(rawPosLower, rawPosUpper, blendW);

                // Unpack from normalized [0, 1] range to Object Space Bounding Box
                float3 objectPos = lerp(_BoundingMin.xyz, _BoundingMax.xyz, rawPos);
                if (_VATBatchMode > 0.5)
                {
                    objectPos = ApplyVATBatchTransform(
                        objectPos,
                        v.vatBatchTransform0,
                        v.vatBatchTransform1,
                        v.vatBatchTransform2);
                }

                o.vertex = UnityObjectToClipPos(float4(objectPos, 1.0));
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv) * _Color;
                return col;
            }
            ENDCG
        }

        Pass
        {
            Name "Outline"
            Tags { "LightMode"="Always" }
            Cull Front
            Blend One Zero

            Stencil
            {
                Ref [_OutlineGroup]
                Comp [_OutlineComp]
            }

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex outlineVert
            #pragma fragment outlineFrag
            #pragma multi_compile_instancing
            #pragma shader_feature_local OUTLINE
            #pragma shader_feature_local OUTLINE_WIDTH_INDEPENDENT
            #include "UnityCG.cginc"
            #include "Includes/VAT_Core.cginc"

            struct outlineAppdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv2 : TEXCOORD1; // uv2.x = Vertex Index
                float4 vatBatchTransform0 : TEXCOORD2;
                float4 vatBatchTransform1 : TEXCOORD3;
                float4 vatBatchTransform2 : TEXCOORD4;
                float4 vatBatchFrame : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct outlineV2f
            {
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float _OutlineWidth;
            float _OutlineZPos;
            float _OutlineWidthIndependent;
            fixed4 _OutlineColor;

            float3 SampleVATPosition(float2 vertexIndexUV, float4 batchFrame)
            {
                float instanceFrameLower = UNITY_ACCESS_INSTANCED_PROP(VATProps, _FrameIndexLower);
                float instanceFrameUpper = UNITY_ACCESS_INSTANCED_PROP(VATProps, _FrameIndexUpper);
                float instanceBlendW = UNITY_ACCESS_INSTANCED_PROP(VATProps, _BlendWeight);
                float frameLower = _VATBatchMode > 0.5 ? batchFrame.x : instanceFrameLower;
                float frameUpper = _VATBatchMode > 0.5 ? batchFrame.y : instanceFrameUpper;
                float blendW = _VATBatchMode > 0.5 ? batchFrame.z : instanceBlendW;
                float textureWidth = max(1.0, _VATTextureWidth);
                float textureHeight = max(1.0, _VATTextureHeight);

                float lowerTexelIndex = frameLower * _NumVertices + vertexIndexUV.x;
                float lowerTexelRow = floor(lowerTexelIndex / textureWidth);
                float2 lowerUV = float2(
                    (lowerTexelIndex - lowerTexelRow * textureWidth + 0.5) / textureWidth,
                    (lowerTexelRow + 0.5) / textureHeight);
                float3 rawPosLower = tex2Dlod(_VATTex, float4(lowerUV, 0, 0)).rgb;

                float upperTexelIndex = frameUpper * _NumVertices + vertexIndexUV.x;
                float upperTexelRow = floor(upperTexelIndex / textureWidth);
                float2 upperUV = float2(
                    (upperTexelIndex - upperTexelRow * textureWidth + 0.5) / textureWidth,
                    (upperTexelRow + 0.5) / textureHeight);
                float3 rawPosUpper = tex2Dlod(_VATTex, float4(upperUV, 0, 0)).rgb;

                float3 rawPos = lerp(rawPosLower, rawPosUpper, blendW);
                return lerp(_BoundingMin.xyz, _BoundingMax.xyz, rawPos);
            }

            outlineV2f outlineVert(outlineAppdata v)
            {
                outlineV2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                #if !defined(OUTLINE)
                    // Keep the pass present for URP ordering, but avoid rasterizing
                    // a second full VAT mesh when the feature is disabled.
                    o.vertex = 0;
                    return o;
                #else
                    float outlineWidth = _OutlineWidth;
                    float3 objectPos = SampleVATPosition(v.uv2, v.vatBatchFrame);
                    if (_VATBatchMode > 0.5)
                    {
                        objectPos = ApplyVATBatchTransform(
                            objectPos,
                            v.vatBatchTransform0,
                            v.vatBatchTransform1,
                            v.vatBatchTransform2);
                    }

                    float objDepth = _VATBatchMode > 0.5
                        ? -UnityObjectToViewPos(objectPos).z
                        : -UnityObjectToViewPos(float3(0, 0, 0)).z;

                    #if defined(OUTLINE_WIDTH_INDEPENDENT)
                        float objDepthLog = objDepth;
                        if (objDepthLog > 1.0)
                            objDepthLog = 1.0 + log(objDepthLog);
                        outlineWidth *= objDepthLog;
                    #else
                        // Keep serialized VAT materials compatible when the
                        // Toggle keyword has not been resynchronized yet.
                        if (_OutlineWidthIndependent > 0.5)
                        {
                            float objDepthLog = objDepth;
                            if (objDepthLog > 1.0)
                                objDepthLog = 1.0 + log(objDepthLog);
                            outlineWidth *= objDepthLog;
                        }
                    #endif

                    float3 normalOS = normalize(v.normal);
                    if (dot(normalOS, normalOS) < 0.0001)
                        normalOS = float3(0, 1, 0);

                    objectPos += outlineWidth * normalOS;
                    o.vertex = UnityObjectToClipPos(float4(objectPos, 1.0));

                    float outlineOffset = _OutlineZPos / -100.0 / max(0.001, objDepth);
                    #if !defined(UNITY_REVERSED_Z)
                        outlineOffset = -outlineOffset;
                    #endif
                    o.vertex.z += outlineOffset;
                    return o;
                #endif
            }

            fixed4 outlineFrag(outlineV2f i) : SV_Target
            {
                return _OutlineColor;
            }
            ENDCG
        }
    }
}
