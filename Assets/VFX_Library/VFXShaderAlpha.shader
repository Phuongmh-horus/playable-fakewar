Shader "VFXLib/ColosoShaderAlpha_BuiltIn_Optimized"
{
	Properties
	{
		[HideInInspector] _EmissionColor("Emission Color", Color) = (1,1,1,1)
		[HDR][Header(_____Base_____)] _MainColor( "MainColor", Color ) = ( 1, 1, 1, 0 )
		_OpacityStrength( "OpacityStrength", Range( 0, 6 ) ) = 1
		_TextureIntensity( "TextureIntensity", Range( 0, 15 ) ) = 1
		_FD( "FD", Range( 0, 2 ) ) = 0
		_CameraOffset( "CameraOffset", Float ) = 0
		_CDFOffset( "CDF Offset", Float ) = 0
		_CDF( "CDF", Float ) = 0
		[Enum(UnityEngine.Rendering.CullMode)] _CullMode( "CullMode", Float ) = 0
		[Enum(Off,0,On,1)] _ZwriteMode( "ZwriteMode", Float ) = 0
		
        [Header(_____Main_____)] 
        _MainTex( "MainTex", 2D ) = "white" {}
		_OpacityPower( "OpacityPower", Range( 0, 55 ) ) = 1
		[Toggle] _Main90degreeUVFlip( "Main90degree UV Flip", Float ) = 0
		[Toggle] _MainUVSwitch( "MainUVSwitch", Float ) = 0
		[Toggle] _UseMainUVCustom1ZW( "UseMainUVCustom1ZW", Float ) = 0
		[Toggle] _MainRIsAlpha( "Main R Is Alpha", Float ) = 0
		
        [Toggle(_MAINRBGOFFSET_ON)] _MainRBGOffset( "MainRBGOffset", Float ) = 1
		
        _MainFlowX( "MainFlow X", Float ) = 0
		_MainFlowY( "MainFlow Y", Float ) = 0
		[Toggle] _MainClampX( "MainClamp X", Float ) = 0
		[Toggle] _MainClampY( "MainClamp Y", Float ) = 0
		_MainOffsetU( "MainOffset U", Range( -0.025, 0.025 ) ) = 0.015
		_MainOffsetV( "MainOffset V", Range( -0.025, 0.025 ) ) = 0.015

		_MaskTex( "MaskTex", 2D ) = "white" {}
		_MaskFlowX( "MaskFlow X", Float ) = 0
		_MaskFlowY( "MaskFlow Y", Float ) = 0
		
        [Header(_____Dissolve_____)] 
        _DissolveTex( "DissolveTex", 2D ) = "white" {}
		[Toggle] _MainTexRDissolve( "MainTex R Dissolve", Float ) = 0
		[Toggle] _UseManual_DebugDissolve( "UseManual_DebugDissolve", Float ) = 1
		_ManualDissolve( "ManualDissolve", Range( 0, 1 ) ) = 1
		_SmoothDissolve( "SmoothDissolve", Range( 0, 15 ) ) = 1
		_DissolveFlowX( "DissolveFlow X", Float ) = 0
		_DissolveFlowY( "DissolveFlow Y", Float ) = 0
		
        [Header(____NoiseDistortion____)] 
        _NoiseTex( "NoiseTex", 2D ) = "white" {}
		[Toggle( _USENOISE_ON )] _UseNoise( "Use Noise", Float ) = 0
		[Toggle( _USECUSTOM1YDISTORTION_ON )] _UseCustom1YDistortion( "Use Custom1Y Distortion", Float ) = 0
		_MainTexDistortion( "MainTex Distortion", Float ) = 0
		_NoiseFlowX( "NoiseFlow X", Float ) = 0
		_NoiseFlowY( "NoiseFlow Y", Float ) = 0
		_NoiseAffectsXAxis( "Noise Affects X Axis", Float ) = 0
		_NoiseAffectsYAxis( "Noise Affects Y Axis", Float ) = 0
	}

	SubShader
	{
		Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }
		Cull [_CullMode]
		ZWrite [_ZwriteMode]
		Blend SrcAlpha OneMinusSrcAlpha

		Pass
		{
			CGPROGRAM
			#pragma target 3.0
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_fog

            // THƯ VIỆN LÕI CỦA BUILT-IN PIPELINE
			#include "UnityCG.cginc"

			#pragma shader_feature_local_fragment _MAINRBGOFFSET_ON
			#pragma shader_feature_local _USECUSTOM1YDISTORTION_ON
			#pragma shader_feature_local _USENOISE_ON

			struct appdata_t
			{
				float4 vertex : POSITION;
				float4 texcoord : TEXCOORD0;
				float4 texcoord1 : TEXCOORD1;
				half4 color : COLOR;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct v2f
			{
				float4 pos : SV_POSITION;
				float4 texcoord5 : TEXCOORD0; // xy = BaseUV, z = CustomDissolve, w = CustomDistortion
				float4 texcoord6 : TEXCOORD1; // xy = UV2, z = CustomEye
				half4 color : COLOR;
				float4 screenPos : TEXCOORD2;
				UNITY_FOG_COORDS(3)
				UNITY_VERTEX_OUTPUT_STEREO
			};

			sampler2D _MainTex; float4 _MainTex_ST;
			sampler2D _NoiseTex; float4 _NoiseTex_ST;
			sampler2D _DissolveTex; float4 _DissolveTex_ST;
			sampler2D _MaskTex; float4 _MaskTex_ST;
            
            // Kéo Texture đo chiều sâu của Built-in
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

			half4 _MainColor;
			half _OpacityStrength;
			half _TextureIntensity;
			half _FD;
			half _CameraOffset;
			half _CDFOffset;
			half _CDF;

			half _OpacityPower;
			half _Main90degreeUVFlip;
			half _MainUVSwitch;
			half _UseMainUVCustom1ZW;
			half _MainRIsAlpha;
			
			half _MainFlowX, _MainFlowY;
			half _MainClampX, _MainClampY;
			half _MainOffsetU, _MainOffsetV;

			half _MaskFlowX, _MaskFlowY;

			half _MainTexRDissolve;
			half _UseManual_DebugDissolve;
			half _ManualDissolve;
			half _SmoothDissolve;
			half _DissolveFlowX, _DissolveFlowY;

			half _MainTexDistortion;
			half _NoiseFlowX, _NoiseFlowY;
			half _NoiseAffectsXAxis, _NoiseAffectsYAxis;

			v2f vert (appdata_t v)
			{
				v2f o;
				UNITY_SETUP_INSTANCE_ID(v);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                // Tính toán vị trí thế giới (Built-in Standard)
				float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
				
                // Tối ưu Camera Offset
				float3 viewDir = _WorldSpaceCameraPos - worldPos;
				float3 normViewDir = normalize(viewDir + 1e-6);
                float3 localCameraOffset = mul((float3x3)unity_WorldToObject, normViewDir) * _CameraOffset;

				v.vertex.xyz += localCameraOffset;

				o.pos = UnityObjectToClipPos(v.vertex);
				o.screenPos = ComputeScreenPos(o.pos);
				
                // Tính Custom Eye (Khoảng cách từ Camera đến Pixel)
                float customEye = -mul(UNITY_MATRIX_V, mul(unity_ObjectToWorld, v.vertex)).z;
				
				o.texcoord5 = v.texcoord;
				o.texcoord6.xy = v.texcoord1.xy;
                o.texcoord6.z = customEye;
				o.color = v.color;

                UNITY_TRANSFER_FOG(o, o.pos);
				return o;
			}

			half4 frag (v2f i) : SV_Target
			{
                half timeFlow = (half)_Time.y;
                
                half2 baseUV = i.texcoord5.xy;
                half2 uv2 = i.texcoord6.xy;
                half customDissolve = i.texcoord5.z;
                half customDistortion = i.texcoord5.w;
                float customEye = i.texcoord6.z;

				half2 uv_MainTex = baseUV * _MainTex_ST.xy + _MainTex_ST.zw;
				half2 uv2_MainTex = uv2 * _MainTex_ST.xy + _MainTex_ST.zw;

                // Tối ưu rẽ nhánh ALU
                half2 currentUV = lerp(uv_MainTex, uv2_MainTex, _MainUVSwitch);
                currentUV = lerp(currentUV, currentUV + uv2, _UseMainUVCustom1ZW);
                
                half2 flippedUV = lerp(currentUV, currentUV.yx, _Main90degreeUVFlip);
                half2 clampedUV = half2(
                    lerp(flippedUV.x, saturate(flippedUV.x), _MainClampX),
                    lerp(flippedUV.y, saturate(flippedUV.y), _MainClampY)
                );

                // --- Distortion ---
                half2 distortion = 0.0h;
                #ifdef _USENOISE_ON
                    half2 uv_Noise = baseUV * _NoiseTex_ST.xy + _NoiseTex_ST.zw + half2(_NoiseFlowX, _NoiseFlowY) * timeFlow;
                    half noiseVal = tex2D(_NoiseTex, uv_Noise).r;
                    distortion = noiseVal * half2(_NoiseAffectsXAxis, _NoiseAffectsYAxis);
                #endif

                half distMod = _MainTexDistortion;
                #ifdef _USECUSTOM1YDISTORTION_ON
                    distMod *= customDistortion;
                #endif

                half2 finalMainUV = clampedUV + half2(_MainFlowX, _MainFlowY) * timeFlow + (distMod * distortion);
                
                // --- RGB Offset Logic ---
                half4 mainTexColor = tex2D(_MainTex, finalMainUV);
                #ifdef _MAINRBGOFFSET_ON
                    half2 offsetUV1 = finalMainUV + half2(_MainOffsetU, _MainOffsetV);
                    half2 offsetUV2 = finalMainUV + half2(_MainOffsetU * 2.0h, _MainOffsetV * 2.0h);
                    half g = tex2D(_MainTex, offsetUV1).g;
                    half2 ba = tex2D(_MainTex, offsetUV2).ba;
                    mainTexColor = half4(mainTexColor.r, g, ba.x, ba.y);
                #endif

                // --- Dissolve ---
                half2 uv_Dissolve = baseUV * _DissolveTex_ST.xy + _DissolveTex_ST.zw + half2(_DissolveFlowX, _DissolveFlowY) * timeFlow;
                half dissolveVal = tex2D(_DissolveTex, uv_Dissolve).r;
                
                half dissolveRef = lerp(dissolveVal, mainTexColor.r, _MainTexRDissolve);
                half manualDiss = lerp(customDissolve, _ManualDissolve, _UseManual_DebugDissolve);
                half dissolveThreshold = lerp(_SmoothDissolve, -1.0h, manualDiss);
                half dissolveFinal = saturate((dissolveRef * _SmoothDissolve) - dissolveThreshold);

                // --- Mask ---
                half2 uv_Mask = baseUV * _MaskTex_ST.xy + _MaskTex_ST.zw + half2(_MaskFlowX, _MaskFlowY) * timeFlow;
                half maskVal = tex2D(_MaskTex, uv_Mask).r;

                // --- Fades (Tính toán Depth chuẩn Built-in) ---
                float sceneDepth = SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screenPos));
                sceneDepth = LinearEyeDepth(sceneDepth);
                float fragDepth = i.screenPos.z / i.screenPos.w;
                
                half depthFade = saturate(abs(sceneDepth - fragDepth) / max(0.001h, _FD));
                half cameraFade = saturate((customEye - _ProjectionParams.y - _CDFOffset) / max(0.001h, _CDF));

                // --- Final Color & Alpha ---
                half3 finalColor = _MainColor.rgb * mainTexColor.rgb * _TextureIntensity * i.color.rgb;

                half baseAlpha = saturate(lerp(mainTexColor.a, mainTexColor.r, _MainRIsAlpha) * _OpacityPower);
                half finalAlpha = i.color.a * baseAlpha * dissolveFinal * maskVal * _OpacityStrength * depthFade * cameraFade;

                UNITY_APPLY_FOG(i.fogCoord, finalColor);

				return half4( finalColor, finalAlpha );
			}
			ENDCG
		}
	}
}