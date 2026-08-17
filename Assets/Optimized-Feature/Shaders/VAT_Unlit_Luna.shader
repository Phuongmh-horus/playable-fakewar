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
        _FrameIndexLower ("Current State Frame Index", Float) = 0
        _FrameIndexUpper ("Target State Frame Index", Float) = 0
        _BlendWeight ("Cross-fade Blend Weight (0 to 1)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR; // Vertex Color: Reserved for future Animation Layer mask
                float2 uv : TEXCOORD0;
                float2 uv2 : TEXCOORD1; // uv2.x = Vertex Index
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            sampler2D _VATTex;
            float4 _MainTex_ST;
            float4 _BoundingMin;
            float4 _BoundingMax;
            float _NumFrames;
            float _NumVertices;

            UNITY_INSTANCING_BUFFER_START(VATPerInstance)
                UNITY_DEFINE_INSTANCED_PROP(float, _FrameIndexLower)
                UNITY_DEFINE_INSTANCED_PROP(float, _FrameIndexUpper)
                UNITY_DEFINE_INSTANCED_PROP(float, _BlendWeight)
            UNITY_INSTANCING_BUFFER_END(VATPerInstance)

            v2f vert(appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                v2f o;
                float frameLower = UNITY_ACCESS_INSTANCED_PROP(VATPerInstance, _FrameIndexLower);
                float frameUpper = UNITY_ACCESS_INSTANCED_PROP(VATPerInstance, _FrameIndexUpper);
                float blendW = UNITY_ACCESS_INSTANCED_PROP(VATPerInstance, _BlendWeight);

                float u = (v.uv2.x + 0.5) / _NumVertices;
                
                // Sample Current State Position from VAT
                float v_lower = (frameLower + 0.5) / _NumFrames;
                float3 rawPosLower = tex2Dlod(_VATTex, float4(u, v_lower, 0, 0)).rgb;

                // Sample Target State Position from VAT
                float v_upper = (frameUpper + 0.5) / _NumFrames;
                float3 rawPosUpper = tex2Dlod(_VATTex, float4(u, v_upper, 0, 0)).rgb;

                // Cross-fade blending between current and target animation states
                float3 rawPos = lerp(rawPosLower, rawPosUpper, blendW);

                // Unpack from normalized [0, 1] range to Object Space Bounding Box
                float3 objectPos = lerp(_BoundingMin.xyz, _BoundingMax.xyz, rawPos);

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
    }
}
