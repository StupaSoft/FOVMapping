using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FOWCameraManager : MonoBehaviour
{
	[SerializeField] private float sigma = 30.0f;
	[SerializeField] private int blurIterationCount = 2;

	[SerializeField] private RenderTexture FOWRenderTexture;
	private Material blurMaterial;

	private void Awake()
	{
		// Set the renderTexture
		GetComponentInParent<Projector>().material.SetTexture("_FOWTexture", FOWRenderTexture);
		GetComponent<Camera>().targetTexture = FOWRenderTexture;

		// Set the blur material
		blurMaterial = new Material(Shader.Find("FOV/GaussianBlur"));
	}

	private void OnRenderImage(RenderTexture source, RenderTexture destination)
	{
		blurMaterial.SetFloat("_Sigma", sigma);

		RenderTexture temp = RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
		for (int i = 0; i < blurIterationCount; ++i)
		{
			if (i % 2 == 0)
			{
				Graphics.Blit(source, temp, blurMaterial);
			}
			else
			{
				Graphics.Blit(temp, source, blurMaterial);
			}
		}

		if (blurIterationCount % 2 == 0)
		{
			Graphics.Blit(source, destination);
		}
		else
		{
			Graphics.Blit(temp, destination);
		}

		RenderTexture.ReleaseTemporary(temp);
	}
}
