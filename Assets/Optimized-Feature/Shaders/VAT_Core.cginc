#ifndef VAT_CORE_INCLUDED
#define VAT_CORE_INCLUDED

// Shared VAT Properties to be included in Shader Properties block
// _VATTex, _BoundingMin, _BoundingMax, _NumFrames, _NumVertices

sampler2D _VATTex;
float4 _BoundingMin;
float4 _BoundingMax;
float _NumFrames;
float _NumVertices;

UNITY_INSTANCING_BUFFER_START(VATProps)
    UNITY_DEFINE_INSTANCED_PROP(float, _FrameIndexLower)
    UNITY_DEFINE_INSTANCED_PROP(float, _FrameIndexUpper)
    UNITY_DEFINE_INSTANCED_PROP(float, _BlendWeight)
UNITY_INSTANCING_BUFFER_END(VATProps)

/// <summary>
/// Calculates final vertex position displaced by VAT texture.
/// Handles cross-fade blending between current and target animation states.
/// </summary>
inline float3 ApplyVATOffset(float2 uv2, float4 vertexColor, float3 defaultPos)
{
    float frameLower = UNITY_ACCESS_INSTANCED_PROP(VATProps, _FrameIndexLower);
    float frameUpper = UNITY_ACCESS_INSTANCED_PROP(VATProps, _FrameIndexUpper);
    float blendW = UNITY_ACCESS_INSTANCED_PROP(VATProps, _BlendWeight);

    // Calculate U coordinate based on Vertex Index (uv2.x)
    float u = (uv2.x + 0.5) / _NumVertices;

    // Sample Current State Position
    float v_lower = (frameLower + 0.5) / _NumFrames;
    float3 rawPosLower = tex2Dlod(_VATTex, float4(u, v_lower, 0, 0)).rgb;

    // Sample Target State Position
    float v_upper = (frameUpper + 0.5) / _NumFrames;
    float3 rawPosUpper = tex2Dlod(_VATTex, float4(u, v_upper, 0, 0)).rgb;

    // Cross-fade blending between current and target animation states
    float3 rawPos = lerp(rawPosLower, rawPosUpper, blendW);

    // Unpack from normalized [0, 1] range to Object Space Bounding Box
    float3 objectPos = lerp(_BoundingMin.xyz, _BoundingMax.xyz, rawPos);

    return objectPos;
}

#endif // VAT_CORE_INCLUDED
