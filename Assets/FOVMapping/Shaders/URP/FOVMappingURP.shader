﻿Shader "FOV/FOVMappingURP"
{
	Properties
	{
	}

	SubShader
	{
		Tags {"RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline"}
		Blend SrcAlpha OneMinusSrcAlpha

		Pass
		{
			HLSLPROGRAM

			#pragma vertex vert
			#pragma fragment frag
			#pragma target 3.5

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			// Declarataions
			CBUFFER_START(UnityPerMaterial)
			CBUFFER_END

			uniform sampler2D _MainTex;
			
			uniform float4 _FOWColor;
			TEXTURE2D_ARRAY(_FOVMap);
			SAMPLER(sampler_FOVMap);

			uniform float _PlaneSizeX;
			uniform float _PlaneSizeZ;

			uniform int _AgentCount;

			StructuredBuffer<float3> _Positions; // World coordinate agent positions
			StructuredBuffer<float3> _Forwards; // World coordinate agent forwards
			StructuredBuffer<float> _Ranges;
			StructuredBuffer<float> _AngleCosines;

			uniform float _SamplingRange;
			uniform int _LayerCount;

			uniform float _BlockOffset;
			
			static const int CHANNELS_PER_TEXEL = 4;
			

			struct Attributes
			{
				float4 pos : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct Varyings
			{
				float4 pos: SV_POSITION;
				float2 uv : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
			};

			// Vertex shader
			Varyings vert(Attributes input)
			{
				Varyings output;

				output.uv = input.uv; // [0, 1]
				output.pos = TransformObjectToHClip(input.pos.xyz); // Clip-space position
				output.worldPos = float3(input.uv.x * _PlaneSizeX, 0.0f, input.uv.y * _PlaneSizeZ);

				return output;
			}

			// Fragment shader
			float4 frag(Varyings input) : COLOR
			{
				int directionsPerSquare = CHANNELS_PER_TEXEL * _LayerCount;
				float anglePerDirection =  2.0f * PI / directionsPerSquare;

				float3 FOWPixelPosition = input.worldPos;

				float4 color = _FOWColor;
				float alphaFactor = 1.0f;
	
				for (int i = 0; i < _AgentCount; ++i)
				{
					float3 agentPosition = _Positions[i];
					float3 agentForward = _Forwards[i];
					float agentSightRange = _Ranges[i];
					float agentSightAngleCosine = _AngleCosines[i];

					// About the target FOW pixel
					float3 direction = FOWPixelPosition - agentPosition;
					direction.y = 0.0f; // Assume same height between the pixel and the agent
					direction = normalize(direction);

					float distanceToAgent = distance(FOWPixelPosition.xz, agentPosition.xz);
					float angle = atan2(direction.z, direction.x); // [-PI, PI]
					angle = fmod(angle + 2 * PI, 2 * PI); // Remap to [0, 2 * PI]

					// Sample the FOV map
					float directionFactor = angle / anglePerDirection;

					int directionIdx0 = (int)directionFactor;
					int directionIdx1 = (directionIdx0 + 1) % directionsPerSquare;

					int layerIdx0 = directionIdx0 / CHANNELS_PER_TEXEL;
					int layerIdx1 = directionIdx1 / CHANNELS_PER_TEXEL;

					int channelIdx0 = directionIdx0 % CHANNELS_PER_TEXEL;
					int channelIdx1 = directionIdx1 % CHANNELS_PER_TEXEL;

					float distanceRatio0 = SAMPLE_TEXTURE2D_ARRAY(_FOVMap, sampler_FOVMap, float2(agentPosition.x / _PlaneSizeX, agentPosition.z / _PlaneSizeZ), layerIdx0)[channelIdx0];
					float distanceRatio1 = SAMPLE_TEXTURE2D_ARRAY(_FOVMap, sampler_FOVMap, float2(agentPosition.x / _PlaneSizeX, agentPosition.z / _PlaneSizeZ), layerIdx1)[channelIdx1];

					float interpolationFactor = directionFactor - directionIdx0;
					float distanceRatio = distanceRatio0 * (1.0f - interpolationFactor) + distanceRatio1 * interpolationFactor; // Interpolate distances sampled from the FOV maps

					// Compare distances
					float distanceToObstacle = distanceRatio * _SamplingRange;

					float obstacleAlphaFactor = distanceToAgent > distanceToObstacle + _BlockOffset; // Sight blocked by obstacles
					float rangeAlphaFactor = distanceToAgent > agentSightRange; // Sight limited by range
					float angleAlphaFactor = agentSightAngleCosine > dot(agentForward, direction); // Sight limited by angle

					float agentAlphaFactor = max(max(obstacleAlphaFactor, rangeAlphaFactor), angleAlphaFactor); // Constrain vision

					// Add vision
					alphaFactor = min(alphaFactor, agentAlphaFactor);
				}

				color.a *= alphaFactor;

				return color;
			}
			ENDHLSL
		}
	}
	FallBack "Diffuse"
}
