Shader"FOV/ProjectorURP" 
{
	Properties 
	{
		_MainTex ("Base (RGB)", 2D) = "white" {}
	}

	Subshader 
	{
		Tags { "Queue"="Transparent+1" }

		ZTest Off
		Blend SrcAlpha OneMinusSrcAlpha

		Pass 
		{
			HLSLPROGRAM
			
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 3.0
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			
			CBUFFER_START(UnityPerMaterial)
			uniform sampler2D _MainTex;
			CBUFFER_END

			uniform sampler2D _CameraDepthTexture;

			uniform float3 _PlanePos;
			uniform float3 _PlaneRight;
			uniform float3 _PlaneForward;
			uniform float3 _PlaneScale;

			struct Attributes
			{
				float4 pos : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct Varyings
			{
				float4 pos : SV_POSITION; // Clip-space position
				float2 uv : TEXCOORD0;
				float3 offsetToPlane : TEXCOORD1; // World space view direction
				float4 screenPos : TEXCOORD2;
			};

			// Vertex shader
			Varyings vert(Attributes input)
			{
				Varyings output;

				output.pos = TransformObjectToHClip(input.pos.xyz);
				output.uv = input.uv;
				output.offsetToPlane = mul(unity_ObjectToWorld, float4(input.pos.xyz, 1.0f)).xyz - _WorldSpaceCameraPos; // Offset from the camera to the shaded point on the FOW plane
				output.screenPos = ComputeScreenPos(output.pos);

				return output;
			}

			float CorrectDepth(float depth)
			{
				float perspective = LinearEyeDepth(depth, _ZBufferParams);
				float orthographic = (_ProjectionParams.z - _ProjectionParams.y) * (1.0f - depth) + _ProjectionParams.y;
				return lerp(perspective, orthographic, unity_OrthoParams.w);
			}
			
			// Find the world position, this time with the depth considered.
			float3 GetWorldPosFromDepth(Varyings i)
			{
				// Get the depth value from the camera (note that this is not the distance traveled by the ray)
				float depth = CorrectDepth(tex2D(_CameraDepthTexture, i.screenPos.xy / i.screenPos.w).r);
	
				// We can derive the following proportional expression from the similarity of triangles.
				// offsetToPlane : dot(offsetToPlane, camNormal) = offsetToPos : depth * camNormal
				// Solve this for offsetToPos.
				float3 offsetToPos = (depth * i.offsetToPlane) / dot(i.offsetToPlane, unity_CameraToWorld._m02_m12_m22);
				float3 worldPos = _WorldSpaceCameraPos + offsetToPos;

				return worldPos;
			}
			
			// Given a world position, convert it to UV coordinates projected upon the plane. 
			float2 WorldPosToPlaneUV(float3 targetPos, float3 planePos, float3 planeRight, float3 planeForward, float3 planeScale)
			{
				float3 relativePos = targetPos - planePos;
				float u = dot(relativePos, planeRight) / planeScale.x;
				float v = dot(relativePos, planeForward) / planeScale.z;

				return float2(u, v);
			}

			float4 frag(Varyings input) : SV_Target
			{
				float3 pointPos = GetWorldPosFromDepth(input);
				float2 pointUV = WorldPosToPlaneUV(pointPos, _PlanePos, _PlaneRight, _PlaneForward, _PlaneScale);
				return tex2D(_MainTex, pointUV);
			}

			ENDHLSL
		}
	}
}