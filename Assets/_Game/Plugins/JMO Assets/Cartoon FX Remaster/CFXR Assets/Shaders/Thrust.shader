Shader "Custom/RPG/ThrustEffect_FinalSoft_CustomData"
{
    Properties
    {
        [Header(Blend Settings)]
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Source Blend", Float) = 5 // 5 = SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Destination Blend", Float) = 10 // 10 = OneMinusSrcAlpha

        [Space(10)]
        [Header(Main Textures)]
        _MainTex ("Main Mask", 2D) = "white" {}
        _NoiseTex ("Flow Noise", 2D) = "gray" {}
        _DissolveTex ("Dissolve Tex", 2D) = "gray" {}

        [HDR]_ColorInner ("Inner Color", Color) = (1.0, 0.85, 0.35, 1)
        [HDR]_ColorOuter ("Outer Color", Color) = (1.0, 0.45, 0.10, 1)
        [HDR]_EdgeColor  ("Dissolve Edge Color", Color) = (1.0, 0.95, 0.65, 1)

        _NoiseScroll1 ("Noise Scroll 1 (X,Y)", Vector) = (0, -2.0, 0, 0)
        _NoiseScroll2 ("Noise Scroll 2 (X,Y)", Vector) = (0, -3.2, 0, 0)
        _NoiseTiling1 ("Noise Tiling 1", Vector) = (1.0, 2.0, 0, 0)
        _NoiseTiling2 ("Noise Tiling 2", Vector) = (2.0, 3.0, 0, 0)
        _NoiseStrength ("Noise Strength", Range(0,2)) = 0.7

        _Opacity ("Opacity", Range(0,5)) = 1.5
        _LengthFade ("Length Fade", Range(0.1,6)) = 1.8
        _TipFade ("Tip Fade", Range(0.1,6)) = 2.0
        _MainMaskPower ("Main Mask Power", Range(0.1,4)) = 1.0

        _ViewEdgeFade ("View Edge Fade", Range(0,1)) = 0.2

        _FresnelPower ("Fresnel Power", Range(0.1,8)) = 2.0
        _FresnelColorStrength ("Fresnel Color Strength", Range(0,3)) = 0.35

        _VertexWobble ("Vertex Wobble", Range(0,0.5)) = 0.03
        _VertexSpeed ("Vertex Speed", Float) = 3.0
        _VertexFrequency ("Vertex Frequency", Float) = 6.0

        _Dissolve ("Base Dissolve", Range(0,1)) = 0
        _CustomDissolveMultiplier ("Custom Dissolve Multiplier", Range(0,2)) = 1
        _DissolveSoftness ("Dissolve Softness", Range(0.001,0.5)) = 0.08
        _EdgeIntensity ("Edge Intensity", Range(0,5)) = 1.5
        
        _DissolveScroll ("Dissolve Scroll (X,Y)", Vector) = (0, -0.5, 0, 0)
        
        _DirectionalDissolve ("Directional Dissolve", Range(0,1)) = 0.6
        _DissolveDirection ("Dissolve Direction", Range(0,1)) = 1

        _InvertY ("Invert UV Y", Range(0,1)) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "RenderType"="Transparent"
            "IgnoreProjector"="True"
        }

        Blend [_SrcBlend] [_DstBlend]
        ZWrite Off
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _NoiseTex;
            sampler2D _DissolveTex;

            half4 _MainTex_ST;
            float4 _DissolveTex_ST;

            half4 _ColorInner, _ColorOuter, _EdgeColor;

            half4 _NoiseScroll1, _NoiseScroll2;
            half4 _NoiseTiling1, _NoiseTiling2;
            half _NoiseStrength;

            half _Opacity, _LengthFade, _TipFade, _MainMaskPower;
            half _ViewEdgeFade;
            half _FresnelPower, _FresnelColorStrength;

            half _VertexWobble, _VertexSpeed, _VertexFrequency;

            half _Dissolve, _CustomDissolveMultiplier, _DissolveSoftness;
            half _EdgeIntensity, _DirectionalDissolve, _DissolveDirection, _InvertY;
            half4 _DissolveScroll;

            struct appdata
            {
                float4 vertex    : POSITION;
                float3 normal    : NORMAL;
                half4 color      : COLOR;
                float4 texcoord0 : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos       : SV_POSITION;
                half4 color      : COLOR;
                half3 uvCustom   : TEXCOORD0;
                half3 normalVS   : TEXCOORD1;
                half3 viewDirVS  : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;

                half2 uv = v.texcoord0.xy * _MainTex_ST.xy + _MainTex_ST.zw;
                uv.y = lerp(uv.y, 1.0h - uv.y, step(0.5h, _InvertY));

                half angleWave = sin(uv.x * 6.2831853h * _VertexFrequency + _Time.y * _VertexSpeed);
                half inverseLength = saturate(1.0h - uv.y);
                half lengthMask = inverseLength * sqrt(inverseLength);

                float3 localOffset = v.normal * angleWave * _VertexWobble * lengthMask;
                float4 localPos = v.vertex + float4(localOffset, 0.0);

                o.pos = UnityObjectToClipPos(localPos);
                o.uvCustom = half3(uv, v.texcoord0.z);
                o.normalVS = normalize(mul((float3x3)UNITY_MATRIX_IT_MV, v.normal));
                o.viewDirVS = normalize(-mul(UNITY_MATRIX_MV, localPos).xyz);
                o.color = v.color;

                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half2 uv = i.uvCustom.xy;

                half2 noiseUV1 = uv * _NoiseTiling1.xy + _NoiseScroll1.xy * _Time.y;
                half2 noiseUV2 = uv * _NoiseTiling2.xy + _NoiseScroll2.xy * _Time.y;

                half noise1 = tex2D(_NoiseTex, noiseUV1).r;
                half noise2 = tex2D(_NoiseTex, noiseUV2).r;
                half flowNoise = lerp(1.0h, (noise1 + noise2) * 0.5h, _NoiseStrength);

                half mainMask = tex2D(_MainTex, uv).r;
                mainMask = pow(saturate(mainMask), _MainMaskPower);

                half lengthFade = pow(saturate(1.0h - uv.y), _LengthFade);
                half tipFade = saturate(1.0h - pow(saturate(uv.y), _TipFade));

                half fresnel = pow(1.0h - saturate(dot(normalize(i.normalVS), normalize(i.viewDirVS))), _FresnelPower);

                half3 col = lerp(_ColorOuter.rgb, _ColorInner.rgb, lengthFade);

                half alpha = mainMask * flowNoise * lengthFade * tipFade;
                alpha *= _Opacity;

                alpha *= 1.0h - fresnel * _ViewEdgeFade;
                col += _EdgeColor.rgb * fresnel * _FresnelColorStrength * lengthFade;

                half2 dissolveUV = uv * _DissolveTex_ST.xy + _DissolveTex_ST.zw;
                dissolveUV += _DissolveScroll.xy * _Time.y;

                half dissolveNoise = tex2D(_DissolveTex, dissolveUV).r;
                half directionalY = lerp(uv.y, 1.0h - uv.y, _DissolveDirection);
                half dissolveTex = lerp(dissolveNoise, directionalY, _DirectionalDissolve);

                half finalDissolve = saturate(_Dissolve + i.uvCustom.z * _CustomDissolveMultiplier);
                half dissolveFactor = 1.0h - finalDissolve;
                half softness = max(_DissolveSoftness, 0.001h);

                half dissolveMask = smoothstep(
                    dissolveFactor - softness,
                    dissolveFactor + softness,
                    dissolveTex
                );

                alpha *= dissolveMask;

                half halfSoftness = softness * 0.5h;
                half edgeBand = smoothstep(dissolveFactor - halfSoftness, dissolveFactor, dissolveTex) -
                                smoothstep(dissolveFactor, dissolveFactor + halfSoftness, dissolveTex);

                col += _EdgeColor.rgb * edgeBand * _EdgeIntensity;

                col *= i.color.rgb;
                alpha *= i.color.a;

                half finalAlpha = saturate(alpha);
                return half4(col * finalAlpha, finalAlpha);
            }
            ENDCG
        }
    }
}