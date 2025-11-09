Shader"FOV/GaussianBlurURP" 
{
	Properties 
	{ 
		_MainTex ("Base (RGB)", 2D) = "white" {}
	}

	SubShader
	{
		Tags { "Queue" = "Overlay" }

		Lighting Off
		Cull Off
		ZWrite Off
		ZTest Always

		Pass
		{
			HLSLPROGRAM

			#pragma target 3.0
			#pragma vertex vert
			#pragma fragment frag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "GaussianBlurURP.hlsl"

			CBUFFER_START(UnityPerMaterial)
			uniform sampler2D _MainTex;
			uniform float4 _MainTex_TexelSize;
			CBUFFER_END
		
			uniform sampler2D _GrabTexture;
			uniform float4 _GrabTexture_TexelSize;
			uniform float _Sigma;

			uniform float2 _Direction;

			struct Attributes
			{
				float4 pos : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct Varyings
			{
				float4 pos : SV_POSITION;
				float2 uv : TEXCOORD0;
			};

			Varyings vert(Attributes input)
			{
				Varyings output;

				output.uv = input.uv; // [0, 1]
				output.pos = TransformObjectToHClip(input.pos.xyz); // Clip-space position

				return output;
			}
	
			float4 frag(Varyings input) : COLOR
			{
				pixel_info pinfo;
				pinfo.tex = _MainTex;
				pinfo.uv = input.uv;
				pinfo.texelSize = _MainTex_TexelSize;
				return GaussianBlurLinearSampling(pinfo, _Sigma, _Direction);
			}

			ENDHLSL
		}
	}
}