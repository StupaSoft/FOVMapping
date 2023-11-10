Shader"FOV/Projector" 
{
	Properties 
	{
		_MainTex ("Base (RGB)", 2D) = "white" {}
	}

	Subshader 
	{
		Tags { "Queue"="Overlay+1" }
		ZTest Always
		Pass 
		{
			Blend SrcAlpha
			OneMinusSrcAlpha

			CGPROGRAM
			
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 3.0
			#include "UnityCG.cginc"

			uniform sampler2D _MainTex;
			uniform sampler2D _CameraDepthTexture;

			uniform float3 _PlanePos;
			uniform float3 _PlaneRight;
			uniform float3 _PlaneForward;
			uniform float3 _PlaneScale;

			struct v2f
			{
				float4 pos : SV_POSITION; // Clip-space position
				float2 uv : TEXCOORD0;
				float3 camRelativeWorldPos : TEXCOORD1; // World space view direction
				float4 screenPos : TEXCOORD2;
			};

			// Vertex shader
			v2f vert(appdata_full v)
			{
				v2f o;

				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = v.texcoord;
				o.camRelativeWorldPos = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz - _WorldSpaceCameraPos;
				o.screenPos = ComputeScreenPos(o.pos);

				return o;
			}

			float3 GetWorldPosFromDepth(v2f i)
			{
				float2 screenUV = i.screenPos.xy / i.screenPos.w;
                float depth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, screenUV));
                float3 viewPlane = i.camRelativeWorldPos.xyz / dot(i.camRelativeWorldPos.xyz, unity_WorldToCamera._m20_m21_m22);
                float3 worldPos = viewPlane * depth + _WorldSpaceCameraPos;

				return worldPos;
			}

			float2 WorldPosToPlaneUV(float3 targetPos, float3 planePos, float3 planeRight, float3 planeForward, float3 planeScale)
			{
				float3 relativePos = targetPos - planePos;
				float u = dot(relativePos, planeRight) / planeScale.x;
				float v = dot(relativePos, planeForward) / planeScale.z;

				return float2(u, v);
			}

			float4 frag(v2f i) : SV_Target
			{
				float3 pointPos = GetWorldPosFromDepth(i);
				float2 pointUV = WorldPosToPlaneUV(pointPos, _PlanePos, _PlaneRight, _PlaneForward, _PlaneScale);
				return tex2D(_MainTex, pointUV);
			}

			ENDCG
		}
	}
}