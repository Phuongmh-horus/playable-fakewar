Shader "Horus/BakeShadow Animation Instancing"
{
    Properties
    {
        [Header(Texture)]
        _MainTex ("Texture", 2D) = "white" {}
        _Brightness ("Brightness", Float) = 1
        [HideInInspector] _Grayscale ("Grayscale", Range(0, 1)) = 0

        [TCP2Separator]
        [Header(Shadow Setting)]
        [Toggle] _EnableShadow ("Enable Shadow", Float) = 1
        [Toggle] _ShadowRota ("Rota Shadow", Float) = 0
        _ShadowColor("Shadow Color", Color) = (0,0,0,0.3)
        _ShadowHeight("Shadow Height", Float) = 0
        _LightDirection("Light Direction", Vector) = (0.15, 0.7, 0.3, 0.3)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            Name "Texture"

            Stencil
            {
                Ref 0
                Comp Always
                Pass IncrWrap
                ZFail Keep
            }

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma fragmentoption ARB_precision_hint_fastest
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"
            #include "Assets/ThirdParty/Animation-Instancing/Assets/AniInstancing/Shader/AnimationInstancingBase.cginc"

            #if (SHADER_TARGET < 30 || SHADER_API_GLES)
                uniform float _Grayscale;
            #else
                UNITY_INSTANCING_BUFFER_START(AppearanceProps)
                    UNITY_DEFINE_INSTANCED_PROP(float, _Grayscale)
                UNITY_INSTANCING_BUFFER_END(AppearanceProps)
            #endif

            struct v2f
            {
                float2 uv : TEXCOORD0;
                half grayscale : TEXCOORD1;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            half _Brightness;

            v2f vert(appdata_full v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                v.vertex = skinningPosition(v);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                #if (SHADER_TARGET < 30 || SHADER_API_GLES)
                    o.grayscale = saturate(_Grayscale);
                #else
                    o.grayscale = saturate(UNITY_ACCESS_INSTANCED_PROP(AppearanceProps, _Grayscale));
                #endif
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, i.uv);
                half gray = dot(color.rgb, half3(0.299h, 0.587h, 0.114h));
                color.rgb = lerp(color.rgb, gray.xxx, i.grayscale);
                color.rgb *= _Brightness;
                return color;
            }
            ENDCG
        }

        Pass
        {
            Name "Shadow"

            Tags { "Queue" = "Transparent+2" "IgnoreProjector" = "True" "RenderType" = "Transparent" }

            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            Stencil
            {
                Ref 0
                Comp Equal
                Pass IncrWrap
                ZFail Keep
            }

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local _ENABLESHADOW_ON
            #pragma shader_feature_local _SHADOWROTA_ON
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"
            #include "Assets/ThirdParty/Animation-Instancing/Assets/AniInstancing/Shader/AnimationInstancingBase.cginc"

            struct vsOut
            {
                float4 pos : SV_POSITION;
            };

            fixed _ShadowHeight;
            fixed4 _ShadowColor;
            fixed4 _LightDirection;

            vsOut vert(appdata_full v)
            {
                vsOut o;
                UNITY_SETUP_INSTANCE_ID(v);
                #if _ENABLESHADOW_ON
                    v.vertex = skinningShadow(v);
                    #if _SHADOWROTA_ON
                        v.vertex.y = 0;
                        float4 vPosWorld = mul(unity_ObjectToWorld, v.vertex);
                        o.pos = mul(UNITY_MATRIX_VP, float4(vPosWorld.x, vPosWorld.y + _ShadowHeight, vPosWorld.z, 1));
                    #else
                        float objectOriginY = min(
                            unity_ObjectToWorld._m10 + unity_ObjectToWorld._m11 + unity_ObjectToWorld._m12 + unity_ObjectToWorld._m13,
                            unity_ObjectToWorld._m13) + _ShadowHeight;
                        float4 vPosWorld = mul(unity_ObjectToWorld, v.vertex);
                        float opposite = vPosWorld.y - objectOriginY;
                        float cosTheta = -_LightDirection.y;
                        float hypotenuse = opposite / cosTheta;
                        float3 vPos = vPosWorld.xyz + (_LightDirection * hypotenuse);
                        o.pos = mul(UNITY_MATRIX_VP, float4(vPos.x, objectOriginY, vPos.z, 1));
                    #endif
                #else
                    o.pos = float4(0, 0, 0, 1);
                #endif

                return o;
            }

            fixed4 frag(vsOut i) : COLOR
            {
                return _ShadowColor;
            }
            ENDCG
        }
    }

    Fallback "VertexLit"
}
