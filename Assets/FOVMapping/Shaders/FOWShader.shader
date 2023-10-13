Shader"FOV/FOW" 
{
	Properties 
	{
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

			uniform sampler2D _FOWTexture;
			uniform sampler2D _LevelHeightMap;
			uniform float _HeightScale;
			uniform int _NumLayers;
			uniform float _X;
			uniform float _Y;

			struct v2f
			{
				float2 uv : TEXCOORD0;
				float4 pos : SV_POSITION;
				float3 viewDir : TEXCOORD1;
			};

			// Vertex shader
			v2f vert(appdata_full v)
			{
				v2f o;

				o.uv = v.texcoord;
				o.pos = UnityObjectToClipPos(v.vertex);
	
				TANGENT_SPACE_ROTATION;
				o.viewDir = mul(rotation, ObjSpaceViewDir(v.vertex));

				return o;
			}

			// Parallax occlusion mapping
			float2 GetParallaxCoord(float3 viewDir, float2 uv)
			{
				float layerHeight = 1.0f / _NumLayers;
				float currentLayerHeight = 0.0f;
				float2 deltaTexCoord = (viewDir.xy * _HeightScale) / (viewDir.z * _NumLayers);
	
				float2 currentTexCoord = uv;
				float currentMapHeight = tex2D(_LevelHeightMap, currentTexCoord).r;
				
				while (currentLayerHeight < currentMapHeight)
				{
					currentTexCoord += deltaTexCoord;
					currentMapHeight = tex2Dlod(_LevelHeightMap, float4(currentTexCoord, 0, 0)).r;
        
					currentLayerHeight += layerHeight;
				}
	
				float2 prevTexCoord = currentTexCoord - deltaTexCoord;
				
				float beforeLength = tex2D(_LevelHeightMap, prevTexCoord).r - (currentLayerHeight - layerHeight);
				float afterLength = currentLayerHeight - currentMapHeight;
	
				float weight = beforeLength / (beforeLength + afterLength);
				float2 finalTexCoord = prevTexCoord + weight * (currentTexCoord - prevTexCoord);
				
				return finalTexCoord;
			}

			float4 frag(v2f i) : SV_Target
			{
				float3 viewDir = normalize(i.viewDir);
				float2 parallaxCoord = GetParallaxCoord(viewDir, i.uv);

				return tex2D(_FOWTexture, parallaxCoord);
			}

			ENDCG
		}
	}
}