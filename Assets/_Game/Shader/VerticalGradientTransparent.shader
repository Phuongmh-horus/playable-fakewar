Shader "RealWar/Vertical Gradient Transparent"
{
    Properties
    {
        _TopColor ("Head Color", Color) = (0.1, 0.1, 0.1, 1)
        _TailColor ("Tail Color", Color) = (0.8, 0.8, 0.8, 1)
        _TailZ ("Tail Z", Float) = -1
        _HeadZ ("Head Z", Float) = 1
        _GradientPower ("Gradient Power", Range(0.1, 4)) = 1
        [Enum(Opaque, 0, Transparent, 1)] _RenderMode ("Render Mode", Float) = 0
        [HideInInspector] _SrcBlend ("Source Blend", Float) = 1
        [HideInInspector] _DstBlend ("Destination Blend", Float) = 0
        [HideInInspector] _ZWrite ("Z Write", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
            "RenderType" = "Opaque"
        }

        LOD 100
        Cull Off
        ZWrite [_ZWrite]
        ZTest LEqual
        Blend [_SrcBlend] [_DstBlend]

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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float localZ : TEXCOORD0;
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _TopColor;
            fixed4 _TailColor;
            float _TailZ;
            float _HeadZ;
            float _GradientPower;

            v2f vert(appdata input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.localZ = input.vertex.z;
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                float zRange = max(_HeadZ - _TailZ, 0.0001);
                float gradient = pow(saturate((input.localZ - _TailZ) / zRange), _GradientPower);
                return lerp(_TailColor, _TopColor, gradient);
            }
            ENDCG
        }
    }

    CustomEditor "RealWar.Editor.VerticalGradientTransparentShaderGUI"
}
