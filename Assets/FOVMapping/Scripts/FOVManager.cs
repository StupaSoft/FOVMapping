using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

public class FOVManager : MonoBehaviour
{
	// Basic fields
	private Projector FOWProjector;
	private IEnumerator FOVCoroutine;

	// Fog of war fields
	private RenderTexture FOWRenderTexture;

	[SerializeField]
	[Tooltip("Size of the fog of war RenderTexture that will be projected with the Projector")]
	private int FOWTextureSize = 2048;

	[SerializeField]
	[Tooltip("Color of the fog of war")]
	private Color FOWColor = new Color(0.1f, 0.1f, 0.1f, 0.7f);

	// Agent visibility
	private ComputeBuffer outputAlphaBuffer;
	private const int MAX_ENEMY_AGENT_COUNT = 128;
	private int kernelID;

	// Runtime adjustments
	[Range(0.01f, 1.0f)]
	[SerializeField]
	[Tooltip("How frequently will the fog of war be updated?")]
	private float updateInterval = 0.02f;

	[Range(0.0f, 3.0f)]
	[SerializeField]
	[Tooltip("How much will the blocked sight be 'pushed away' to prevent flickers on vertical obstacles?")]
	private float blockOffset = 1.0f;

	// Agents status
	private List<FOVAgent> FOVAgents;
	private List<Vector4> positions = new List<Vector4>();
	private List<Vector4> forwards = new List<Vector4>();
	private List<float> ranges = new List<float>();
	private List<float> angleCosines = new List<float>();

	// Postprocessing
	[Range(1.0f, 100.0f)]
	[SerializeField]
	[Tooltip("Deviation of the Gaussian filter(larger value strengthens filtering effect to some extent)")]
	private float sigma = 30.0f;

	[Range(0, 10)]
	[SerializeField]
	[Tooltip("How many times will the Gaussian filter be applied? More iterations lead to a smoother fog of war, but with worse performance.")]
	private int blurIterationCount = 1;
	private Material blurMaterial;

	// Shaders and materials
	[SerializeField]
	[Tooltip("(Essential) FOV map Texture2DArray to use for FOV mapping")]
	private Texture2DArray FOVMapArray;

	[SerializeField]
	[Tooltip("(Do not modify) FOV map core shader")]
	private Shader FOVShader;

	[SerializeField]
	[Tooltip("(Do not modify) Fog of war projection shader")]
	private Shader FOWShader;

	[SerializeField]
	[Tooltip("(Do not modify) Gaussian filter shader")]
	private Shader GaussianShader;

	private Material FOVMaterial; // Field of view material
	private Material FOWMaterial; // Fog of war material

	[SerializeField]
	[Tooltip("(Do not modify) Pixel reader computer shader")]
	private ComputeShader pixelReader;

	private void Awake()
	{
		FOWProjector = GetComponent<Projector>();

		FOVMaterial = new Material(FOVShader);

		FOWMaterial = new Material(FOWShader);
		FOWProjector.material = FOWMaterial;

		blurMaterial = new Material(GaussianShader);
	}

	private void Start()
	{
		FindAllFOVAgents();
		
		if (FOVMapArray && FOVMaterial)
		{
			FOVMaterial.SetFloat("_SamplingRange", FOVMapArray.mipMapBias);
			FOVMaterial.SetTexture("_FOVMap", FOVMapArray);

			FOVMaterial.SetInt("_LayerCount", FOVMapArray.depth);
		}
		else
		{
			print("FOV map or material not set");
			return;
		}

		FOWRenderTexture = new RenderTexture(FOWTextureSize, FOWTextureSize, 1, RenderTextureFormat.ARGB32);
		FOWMaterial.SetTexture("_FOWTexture", FOWRenderTexture); // It will be projected using a Projector.

		outputAlphaBuffer = new ComputeBuffer(1, sizeof(float) * MAX_ENEMY_AGENT_COUNT);
		kernelID = pixelReader.FindKernel("ReadPixels");
		pixelReader.SetTexture(kernelID, "inputTexture", FOWRenderTexture);
		pixelReader.SetBuffer(kernelID, "outputBuffer", outputAlphaBuffer);

		EnableFOV();
	}

	private void OnDestroy()
	{
		outputAlphaBuffer.Release();
	}

	public void EnableFOV()
	{
		FOVCoroutine = UpdateFOV();
		StartCoroutine(FOVCoroutine);
	}

	public void DisableFOV() 
	{
		StopCoroutine(FOVCoroutine);
	}

	// Runtime updater
	private IEnumerator UpdateFOV()
	{
		while (true)
		{
			yield return new WaitForSeconds(updateInterval);
			
			SetShaderValues();
			SetAgentVisibility(); // Call before ApplyFOWPass, as calling it after the pass will stall the main thread for a while.

			yield return new WaitForEndOfFrame();

			ApplyFOWPass(); // Call after the rendering has finished to prevent flickers.
		}

		// Aggregate the status of agents and transfer to the GPU 
		void SetShaderValues()
		{
			// Set agents' data
			positions.Clear();
			ranges.Clear();
			forwards.Clear();
			angleCosines.Clear();

			if (FOVAgents.Count == 0) return;

			for (int i = 0; i < FOVAgents.Count; i++)
			{
				FOVAgent agent = FOVAgents[i];
				if (agent.enabled && agent.contributeToFOV)
				{
					positions.Add(agent.transform.position);
					forwards.Add(agent.transform.forward);
					ranges.Add(agent.sightRange);
					angleCosines.Add(Mathf.Cos(agent.sightAngle * 0.5f * Mathf.Deg2Rad));
				}
			}

			// Set uniform values
			FOVMaterial.SetVector("_ProjectorPosition", transform.position);
			FOVMaterial.SetVector("_ProjectorLeft", transform.right);
			FOVMaterial.SetVector("_ProjectorBackward", transform.up);

			FOVMaterial.SetFloat("_ProjectorSizeX", FOWProjector.orthographicSize * FOWProjector.aspectRatio * 2.0f);
			FOVMaterial.SetFloat("_ProjectorSizeY", FOWProjector.orthographicSize * 2.0f);

			FOVMaterial.SetColor("_FOWColor", FOWColor);

			FOVMaterial.SetInt("_AgentCount", positions.Count);

			FOVMaterial.SetVectorArray("_Positions", positions);
			FOVMaterial.SetVectorArray("_Forwards", forwards);
			FOVMaterial.SetFloatArray("_Ranges", ranges);
			FOVMaterial.SetFloatArray("_AngleCosines", angleCosines);

			FOVMaterial.SetFloat("_BlockOffset", blockOffset);
		}

		// Apply FOVMapping and Gaussian blur passes to a RenderTexture
		void ApplyFOWPass()
		{
			RenderTexture backup = RenderTexture.active;
			RenderTexture.active = FOWRenderTexture;
			GL.Clear(true, true, Color.clear);
			RenderTexture.active = backup;

			Graphics.Blit(null, FOWRenderTexture, FOVMaterial); // Render FOV to FOWRenderTexture

			// Blur
			RenderTexture temp = RenderTexture.GetTemporary(FOWRenderTexture.width, FOWRenderTexture.height, 0, FOWRenderTexture.format);
			blurMaterial.SetFloat("_Sigma", sigma);

			// Apply Gaussian blur shader multiple times
			// Render to one another alternately
			for (int i = 0; i < blurIterationCount; ++i)
			{
				if (i % 2 == 0)
				{
					Graphics.Blit(FOWRenderTexture, temp, blurMaterial);
				}
				else
				{
					Graphics.Blit(temp, FOWRenderTexture, blurMaterial);
				}
			}

			// If the final result is in temp, copy the content to FOWRenderTexture
			if (blurIterationCount % 2 != 0)
			{
				Graphics.Blit(temp, FOWRenderTexture);
			}

			RenderTexture.ReleaseTemporary(temp);
		}

		// Set visibility of agents according to the current FOV
		void SetAgentVisibility()
		{
			List<FOVAgent> targetAgents = new List<FOVAgent>();
			List<Vector4> targetAgentUVs = new List<Vector4>();
			
			for (int i = 0; i < FOVAgents.Count; i++)
			{
				FOVAgent agent = FOVAgents[i];
				if (agent.disappearInFOW)
				{
					Vector3 agentPosition = agent.transform.position;

					// Process agents inside the camera viewport only
					Vector3 viewportPosition = Camera.main.WorldToViewportPoint(agentPosition);
					if (viewportPosition.x < 0.0f || viewportPosition.x > 1.0f && viewportPosition.y < 0.0f || viewportPosition.y > 1.0f || viewportPosition.z <= 0.0f)
					{
						continue;
					}

					// Convert the agent position to a Projector UV coordinate
					Vector2 agentLocalPosition = FOWProjector.transform.InverseTransformPoint(agentPosition);
					agentLocalPosition.x /= FOWProjector.orthographicSize * FOWProjector.aspectRatio; // Remap to [-1, 1]
					agentLocalPosition.y /= FOWProjector.orthographicSize; // Remap to [-1, 1]
					agentLocalPosition = agentLocalPosition / 2.0f + 0.5f * Vector2.one; // [-1, 1] -> [0, 1]
					Vector2 agentUV = FOWTextureSize * agentLocalPosition;

					targetAgents.Add(agent);
					targetAgentUVs.Add(agentUV);
				}
			}

			// Use the compute shader to retrieve pixel data from the GPU to CPU
			pixelReader.SetInt("targetAgentCount", targetAgents.Count);
			pixelReader.SetVectorArray("targetAgentUVs", targetAgentUVs.ToArray());

			pixelReader.Dispatch(kernelID, 1, 1, 1);

			float[] outputAlphaArray = new float[MAX_ENEMY_AGENT_COUNT];
			outputAlphaBuffer.GetData(outputAlphaArray);

			// Set visibility
			for (int i = 0; i < targetAgents.Count; ++i) 
			{
				FOVAgent agent = targetAgents[i];

				bool isInSight = outputAlphaArray[i] <= agent.disappearAlphaThreshold;
				agent.SetUnderFOW(isInSight);
			}
		}
	}

	// Interfaces
	public void FindAllFOVAgents()
	{
		FOVAgents = FindObjectsOfType<FOVAgent>().ToList();
	}

	public void AddFOVAgent(FOVAgent agent)
	{
		FOVAgents.Add(agent);
	}

	public void RemoveFOVAgent(FOVAgent agent) 
	{
		FOVAgents.Remove(agent);
	}

	public FOVAgent GetAgent(int idx)
	{
		return FOVAgents[idx];
	}

	public int GetFOVAgentCount()
	{
		return FOVAgents.Count;
	}

	public void ClearFOVAgents()
	{
		FOVAgents.Clear();
	}
}

