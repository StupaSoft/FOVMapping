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
			float2 GetparallaxCoords(float3 viewDir, float2 uv)
			{
				float layerInterval = 1.0f / _NumLayers;
				float currentLayerHeight = 1.0f; // Start from above
				float2 deltaTexCoords = (viewDir.xy * _HeightScale) / (viewDir.z * _NumLayers); // Shift of texture coordinates per layer (toward the view vector)
	
				float2 currentTexCoords = uv + deltaTexCoords * _NumLayers; // Same: start from above
				float currentMapHeight = tex2D(_LevelHeightMap, currentTexCoords).r;
				
				// Go down the layer until we meet a surface
				while (currentLayerHeight > currentMapHeight)
				{
					currentTexCoords -= deltaTexCoords;
					currentMapHeight = tex2Dlod(_LevelHeightMap, float4(currentTexCoords, 0, 0)).r;
        
					currentLayerHeight -= layerInterval;
				}
				
				// Interpolation for finding the final sampling coordinates
				float2 prevTexCoords = currentTexCoords + deltaTexCoords;
				
				float beforeLength = (currentLayerHeight + layerInterval) - tex2D(_LevelHeightMap, prevTexCoords).r;
				float afterLength = currentMapHeight - currentLayerHeight;
				
				float weight = beforeLength / (beforeLength + afterLength);
				float2 finalTexCoords = prevTexCoords - weight * deltaTexCoords;
				
				return finalTexCoords;
			}

			float4 frag(v2f i) : SV_Target
			{
				float3 viewDir = normalize(i.viewDir);
				float2 parallaxCoords = GetparallaxCoords(viewDir, i.uv);

				return tex2D(_FOWTexture, parallaxCoords);
			}

			ENDCG
		}
	}
}