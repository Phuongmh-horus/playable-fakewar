#ifndef VAT_CORE_INCLUDED
#define VAT_CORE_INCLUDED

// Shared VAT Properties to be included in Shader Properties block
// _VATTex, _BoundingMin, _BoundingMax, _NumFrames, _NumVertices, _VATTextureWidth, _VATTextureHeight

sampler2D _VATTex;
float4 _BoundingMin;
float4 _BoundingMax;
float _NumFrames;
float _NumVertices;
float _VATTextureWidth;
float _VATTextureHeight;
float _VATBatchMode;

inline float3 ApplyVATBatchTransform(
    float3 position,
    float4 transformRow0,
    float4 transformRow1,
    float4 transformRow2)
{
    return float3(
        dot(transformRow0.xyz, position) + transformRow0.w,
        dot(transformRow1.xyz, position) + transformRow1.w,
        dot(transformRow2.xyz, position) + transformRow2.w);
}

UNITY_INSTANCING_BUFFER_START(VATProps)
    UNITY_DEFINE_INSTANCED_PROP(float4, _VATFrameData)
UNITY_INSTANCING_BUFFER_END(VATProps)

/// <summary>
/// Calculates final vertex position displaced by VAT texture.
/// Handles cross-fade blending between current and target animation states.
/// </summary>
inline float3 ApplyVATOffset(float2 uv2, float4 vertexColor, float3 defaultPos)
{
    float4 frameData = UNITY_ACCESS_INSTANCED_PROP(VATProps, _VATFrameData);
    float frameLower = frameData.x;
    float frameUpper = frameData.y;
    float blendW = frameData.z;

    float textureWidth = max(1.0, _VATTextureWidth);
    float textureHeight = max(1.0, _VATTextureHeight);

    // Sample Current State Position
    float lowerTexelIndex = frameLower * _NumVertices + uv2.x;
    float lowerTexelRow = floor(lowerTexelIndex / textureWidth);
    float2 lowerUV = float2(
        (lowerTexelIndex - lowerTexelRow * textureWidth + 0.5) / textureWidth,
        (lowerTexelRow + 0.5) / textureHeight);
    float3 rawPosLower = tex2Dlod(_VATTex, float4(lowerUV, 0, 0)).rgb;

    // Sample Target State Position
    float upperTexelIndex = frameUpper * _NumVertices + uv2.x;
    float upperTexelRow = floor(upperTexelIndex / textureWidth);
    float2 upperUV = float2(
        (upperTexelIndex - upperTexelRow * textureWidth + 0.5) / textureWidth,
        (upperTexelRow + 0.5) / textureHeight);
    float3 rawPosUpper = tex2Dlod(_VATTex, float4(upperUV, 0, 0)).rgb;

    // Cross-fade blending between current and target animation states
    float3 rawPos = lerp(rawPosLower, rawPosUpper, blendW);

    // Unpack from normalized [0, 1] range to Object Space Bounding Box
    float3 objectPos = lerp(_BoundingMin.xyz, _BoundingMax.xyz, rawPos);

    return objectPos;
}

#endif // VAT_CORE_INCLUDED
