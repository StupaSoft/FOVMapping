Shader "FOV/Projector" 
{
	Properties 
	{
	}

	Subshader 
	{
		Tags {"Queue"="Transparent"}
		Pass 
		{
			ZWrite Off
			Blend SrcAlpha OneMinusSrcAlpha

			CGPROGRAM

			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_fog
			#include "UnityCG.cginc"

			float4x4 unity_Projector;
			float4x4 unity_ProjectorClip;

			sampler2D _FOWTexture;
			uniform float _Blend;

			struct v2f 
			{
				float4 uvShadow : TEXCOORD0;
				UNITY_FOG_COORDS(2)
				float4 pos : SV_POSITION;
			};

			v2f vert(float4 vertex : POSITION)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(vertex);
				o.uvShadow = mul(unity_Projector, vertex);
				UNITY_TRANSFER_FOG(o, o.pos);
				return o;
			}

			fixed4 frag(v2f i) : SV_Target
			{
				fixed4 col = tex2Dproj(_FOWTexture, UNITY_PROJ_COORD(i.uvShadow));
				return col;
			}

			ENDCG
		}
	}
}