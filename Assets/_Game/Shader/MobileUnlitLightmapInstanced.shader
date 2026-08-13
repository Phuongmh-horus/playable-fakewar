Shader "RealWar/Mobile/Unlit (Supports Lightmap Instanced)"
{
    Properties
    {
        _MainTex ("Base (RGB)", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry"
            "RenderType" = "Opaque"
        }

        LOD 100

        Pass
        {
            Tags { "LightMode" = "Vertex" }
            Lighting Off

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float2 lightmapUv : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
#if defined(LIGHTMAP_ON)
                float2 lightmapUv : TEXCOORD1;
#endif
                UNITY_FOG_COORDS(2)
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            v2f vert(appdata input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
#if defined(LIGHTMAP_ON)
                output.lightmapUv = input.lightmapUv * unity_LightmapST.xy + unity_LightmapST.zw;
#endif
                UNITY_TRANSFER_FOG(output, output.vertex);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, input.uv);
#if defined(LIGHTMAP_ON)
                color.rgb *= DecodeLightmap(UNITY_SAMPLE_TEX2D(unity_Lightmap, input.lightmapUv));
#endif
                color.a = 1;
                UNITY_APPLY_FOG(input.fogCoord, color);
                return color;
            }
            ENDCG
        }
    }

    Fallback Off
}
