using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using FOVMapping;
using UnityEngine.Rendering;

namespace FOVMapping
{
public class FOVManager : MonoBehaviour
{
	// Basic fields
	private IEnumerator FOVCoroutine;

	// Fog of war fields
	private RenderTexture FOWRenderTexture;

	private bool isURP;

	[SerializeField]
	[Tooltip("Size of the fog of war RenderTexture that will be projected with the Plane")]
	private int FOWTextureSize = 2048;

	[SerializeField]
	[Tooltip("Color of the fog of war")]
	private Color FOWColor = new Color(0.1f, 0.1f, 0.1f, 0.7f);

	[Range(0, 4096)]
	[SerializeField]
	[Tooltip("Maximum number of friendly agents (contributeToFOW == true)")]
	private int maxFriendlyAgentCount = 128;

	[Range(0, 4096)]
	[SerializeField]
	[Tooltip("Maximum number of enemy agents (disappearInFOW == true)")]
	private int maxEnemyAgentCount = 128;

	// Agent visibility
	private ComputeBuffer outputAlphaBuffer;
	private int kernelID;
	private List<FOVAgent> visibilityTargetAgents = new List<FOVAgent>();
	private bool needAgentVisibilityUpdate = true;

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

	private List<Vector3> positions = new List<Vector3>();
	ComputeBuffer positionsBuffer;
	private List<Vector3> forwards = new List<Vector3>();
	ComputeBuffer forwardsBuffer;
	private List<float> ranges = new List<float>();
	ComputeBuffer rangesBuffer;
	private List<float> angleCosines = new List<float>();
	ComputeBuffer angleCosinesBuffer;

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
	[Tooltip("(Essential) FOV map Texture2DArray for runtime FOV mapping")]
	private Texture2DArray FOVMapArray;

	[SerializeField]
	[Tooltip("(Do not modify) FOV mapping shader")]
	private Shader FOVShader;

	[SerializeField]
	[Tooltip("(Do not modify) Fog of war projector shader")]
	private Shader FOWProjectorShader;

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
		FOVMaterial = new Material(FOVShader);

		FOWMaterial = new Material(FOWProjectorShader);
		GetComponent<MeshRenderer>().material = FOWMaterial;

		blurMaterial = new Material(GaussianShader);

		if (GraphicsSettings.defaultRenderPipeline != null)
        {
            if (GraphicsSettings.defaultRenderPipeline.GetType().Name == "UniversalRenderPipelineAsset")
            {
				isURP = true;
			}
			else
			{
				isURP = false;
			}
        }
        else
        {
			isURP = false;
		}
	}

	private void Start()
	{
		FindAllFOVAgents();
		
		if (FOVMapArray)
		{
			FOVMaterial.SetFloat("_SamplingRange", FOVMapArray.mipMapBias);
			FOVMaterial.SetTexture("_FOVMap", FOVMapArray);

			FOVMaterial.SetInt("_LayerCount", FOVMapArray.depth);
		}
		else
		{
			print("FOV map has not been set");
			return;
		}

		FOWRenderTexture = new RenderTexture(FOWTextureSize, FOWTextureSize, 1, RenderTextureFormat.ARGB32);
		FOWMaterial.SetTexture("_MainTex", FOWRenderTexture); // It will be projected using a Plane.

		outputAlphaBuffer = new ComputeBuffer(1, sizeof(float) * maxEnemyAgentCount, ComputeBufferType.IndirectArguments);
		kernelID = pixelReader.FindKernel("ReadPixels");
		pixelReader.SetTexture(kernelID, "inputTexture", FOWRenderTexture);
		pixelReader.SetBuffer(kernelID, "outputBuffer", outputAlphaBuffer);

		positionsBuffer = new ComputeBuffer(maxFriendlyAgentCount, sizeof(float) * 3, ComputeBufferType.IndirectArguments);
		forwardsBuffer = new ComputeBuffer(maxFriendlyAgentCount, sizeof(float) * 3, ComputeBufferType.IndirectArguments);
		rangesBuffer = new ComputeBuffer(maxFriendlyAgentCount, sizeof(float), ComputeBufferType.IndirectArguments);
		angleCosinesBuffer = new ComputeBuffer(maxFriendlyAgentCount, sizeof(float), ComputeBufferType.IndirectArguments);

		EnableFOV();
	}

	private void OnEnable()
	{
		Camera.main.depthTextureMode = DepthTextureMode.Depth;
	}

	private void OnDisable() 
	{
		if (Camera.main != null) Camera.main.depthTextureMode = DepthTextureMode.None;
	}

	private void OnDestroy()
	{
		if (positionsBuffer != null) positionsBuffer.Release();
		if (forwardsBuffer != null) forwardsBuffer.Release();
		if (rangesBuffer != null) rangesBuffer.Release();
		if (angleCosinesBuffer != null)	angleCosinesBuffer.Release();

		if (outputAlphaBuffer != null) outputAlphaBuffer.Release();
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
		yield return new WaitForEndOfFrame();

		float elapsedTimeFromLastUpdate = 0.0f;
		while (true)
		{
			if (elapsedTimeFromLastUpdate >= updateInterval)
			{
				SetShaderValues();
				if (needAgentVisibilityUpdate) RequestUpdateAgentVisibility(); // Call before ApplyFOWPass, as calling it after the pass will stall the main thread for a while.
				ApplyFOWPass(); // Call after the rendering has finished to prevent flickers.

				elapsedTimeFromLastUpdate = 0.0f;
			}

			yield return new WaitForEndOfFrame();
			elapsedTimeFromLastUpdate += Time.unscaledDeltaTime;
		}

		// Aggregate the status of agents and transfer to the GPU 
		void SetShaderValues()
		{
			// Set agents' data
			positions.Clear();
			ranges.Clear();
			forwards.Clear();
			angleCosines.Clear();

			for (int i = 0; i < FOVAgents.Count; i++)
			{
				FOVAgent agent = FOVAgents[i];
				if (agent.enabled && agent.contributeToFOV)
				{
					Vector3 relativePos = Vector3.Scale(transform.InverseTransformPoint(agent.transform.position), transform.lossyScale); // Position of the agent relative to the FOW plane
					Vector3 relativeForward = transform.InverseTransformDirection(agent.transform.forward); // Ditto

					positions.Add(relativePos);
					forwards.Add(relativeForward);
					ranges.Add(agent.sightRange);
					angleCosines.Add(Mathf.Cos(agent.sightAngle * 0.5f * Mathf.Deg2Rad));
				}
			}

			if (positions.Count > maxFriendlyAgentCount)
			{
				Debug.LogError($"Maximum friendly agent count ({maxFriendlyAgentCount}) exceeded.");
			}

			FOVMaterial.SetInt("_AgentCount", positions.Count);

			// Bind buffers
			positionsBuffer.SetData(positions);
			forwardsBuffer.SetData(forwards);
			rangesBuffer.SetData(ranges);
			angleCosinesBuffer.SetData(angleCosines);

			FOVMaterial.SetBuffer("_Positions", positionsBuffer);
			FOVMaterial.SetBuffer("_Forwards", forwardsBuffer);
			FOVMaterial.SetBuffer("_Ranges", rangesBuffer);
			FOVMaterial.SetBuffer("_AngleCosines", angleCosinesBuffer);

			// Set uniform values for FOVMaterial
			FOVMaterial.SetFloat("_PlaneSizeX", transform.lossyScale.x);
			FOVMaterial.SetFloat("_PlaneSizeZ", transform.lossyScale.z);

			FOVMaterial.SetColor("_FOWColor", FOWColor);
			FOVMaterial.SetFloat("_BlockOffset", blockOffset);

			// Set uniform values for FOWMaterial
			FOWMaterial.SetVector("_PlanePos", transform.position);
			FOWMaterial.SetVector("_PlaneRight", transform.right);
			FOWMaterial.SetVector("_PlaneForward", transform.forward);
			FOWMaterial.SetVector("_PlaneScale", transform.localScale);
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

			if (isURP)
			{
				for (int i = 0; i < blurIterationCount; ++i)
				{
					blurMaterial.SetVector("_Direction", new Vector2(1, 0));
					Graphics.Blit(FOWRenderTexture, temp, blurMaterial);
					blurMaterial.SetVector("_Direction", new Vector2(0, 1));
					Graphics.Blit(temp, FOWRenderTexture, blurMaterial);
				}
			}
			else
			{
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
			}


			RenderTexture.ReleaseTemporary(temp);
		}

		// Set visibility of agents according to the current FOV
		void RequestUpdateAgentVisibility()
		{
			needAgentVisibilityUpdate = false;

			visibilityTargetAgents.Clear();
			List<Vector4> targetAgentUVs = new List<Vector4>();

			for (int i = 0; i < FOVAgents.Count; i++)
			{
				FOVAgent agent = FOVAgents[i];
				if (agent.disappearInFOW)
				{
					Vector3 agentPosition = agent.transform.position;

					// Process agents inside the camera viewport only
					Vector3 viewportPosition = Camera.main.WorldToViewportPoint(agentPosition);
					if ((viewportPosition.x < -0.5f || viewportPosition.x > 1.5f) || (viewportPosition.y < -0.5f || viewportPosition.y > 1.5f) || viewportPosition.z < -0.5f)
					{
						continue;
					}

					// Convert the agent position to a Plane UV coordinate [0, FOWTextureSize]
					Vector3 agentLocalPosition = transform.InverseTransformPoint(agentPosition);
					Vector2 agentUV = new Vector2(agentLocalPosition.x, agentLocalPosition.z);
					agentUV *= FOWTextureSize;

					visibilityTargetAgents.Add(agent);
					targetAgentUVs.Add(agentUV);
				}
			}

			if (visibilityTargetAgents.Count > maxEnemyAgentCount)
			{
				Debug.LogError($"Maximum enemy agent count ({maxEnemyAgentCount}) exceeded.");
			}

			// Use the compute shader to retrieve pixel data from the GPU to CPU
			pixelReader.SetInt("targetAgentCount", visibilityTargetAgents.Count);
			pixelReader.SetVectorArray("targetAgentUVs", targetAgentUVs.ToArray());

			pixelReader.Dispatch(kernelID, 1, 1, 1);

			// Asynchronous request to the GPU
			AsyncGPUReadback.Request(outputAlphaBuffer, OnRetrieveAgentVisibility);
		}
	}

	private void OnRetrieveAgentVisibility(AsyncGPUReadbackRequest request)
	{
		float[] alphaSamples = request.GetData<float>().ToArray(); // Sampling result of the FOW at the locations of agents

		// Set visibility
		for (int i = 0; i < visibilityTargetAgents.Count; ++i)
		{
			FOVAgent agent = visibilityTargetAgents[i];

			bool isInSight = alphaSamples[i] <= agent.disappearAlphaThreshold;
			agent.SetUnderFOW(isInSight);
		}

		needAgentVisibilityUpdate = true;
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
}
