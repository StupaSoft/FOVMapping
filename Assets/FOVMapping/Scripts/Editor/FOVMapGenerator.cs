using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using FOVMapping;

namespace FOVMapping
{
[Serializable]
public class FOVMapGenerationInfo
{
	[Tooltip("(Essential) Path to save the generated FOV map")] public string path = "FOVMapping/FOVMaps";
	[Tooltip("(Essential) Name of the FOV map file")] public string fileName = "FOVMap1024";
	[Tooltip("(Essential) Plane for FOV mapping")] public Transform plane;
	[Tooltip("(Essential) Layer of the level to be sampled")] public LayerMask levelLayer;
	[Tooltip("Width of the generated FOV map")] public int FOVMapWidth = 1024;
	[Tooltip("Height of the generated FOV map")] public int FOVMapHeight = 1024;
	[Tooltip("Number of layers in the generated FOV map")] public int layerCount = 90;
	[Tooltip("Height of the 'sampling eye'")] public float eyeHeight = 1.8f;
	[Tooltip("Maximum sampling range; sight system does not work beyond this boundary")] public float samplingRange = 50.0f;
	[Tooltip("(Advanced) Vertical angular range from the sampling eye")] public float samplingAngle = 140.0f;
	[Tooltip("(Advanced) How many rays will be fired toward a direction at a location?")] public int samplesPerDirection = 9;
	[Tooltip("(Advanced) How many iterations for the binary search to find an edge?")] public int binarySearchCount = 10;
	[Tooltip("(Advanced) Surfaces steeper than this angle are considered vertical and there will be no further sampling toward the direction at the location.")] public float blockingSurfaceAngleThreshold = 85.0f;
	[Tooltip("(Advanced) Surfaces located below this vertical angle are never considered vertical.")] public float blockedRayAngleThreshold = 0.0f;
}

public class FOVMapGenerator : MonoBehaviour
{
	private const int CHANNELS_PER_TEXEL = 4;

	public static bool CreateFOVMap(FOVMapGenerationInfo generationInfo, Func<int, int, bool> progressAction)
	{
		string FOVMapPath = $"Assets/{generationInfo.path}/{generationInfo.fileName}.asset";

		Texture2DArray FOVMapArray = GenerateFOVMap(generationInfo, progressAction);
		if (FOVMapArray == null) return false;

		// Save the maps
		FOVMapArray.mipMapBias = generationInfo.samplingRange; // Store sampling range in mipMapBias field to use in FOVManager
		try
		{
			AssetDatabase.CreateAsset(FOVMapArray, FOVMapPath);
			AssetDatabase.Refresh();
		}
		catch (Exception e)
		{
			print(e.ToString());
			return false;
		}

		return true;
	}

	private static Texture2DArray GenerateFOVMap(FOVMapGenerationInfo generationInfo, Func<int, int, bool> progressAction)
	{
		// Basic checks
		bool checkPassed = true;

		if (generationInfo.plane == null)
		{
			Debug.LogError("No FOW plane has been assigned.");
			checkPassed = false;
		}

		if (string.IsNullOrEmpty(generationInfo.path) || string.IsNullOrEmpty(generationInfo.fileName))
		{
			Debug.LogError("Either path or file name have not been assigned.");
			checkPassed = false;
		}

		if (generationInfo.FOVMapWidth == 0 || generationInfo.FOVMapHeight == 0)
		{
			Debug.LogError("Incorrect texture size.");
			checkPassed = false;
		}

		if (generationInfo.samplingRange <= 0.0f)
		{
			Debug.LogError("Sampling range must be greater than zero.");
			checkPassed = false;
		}

		if (generationInfo.levelLayer == 0)
		{
			Debug.LogError("Level layer must be non-zero.");
			checkPassed = false;
		}

		if (progressAction == null)
		{
			Debug.LogError("progressAction must be pased.");
			checkPassed = false;
		}

		if (!checkPassed) return null;

		// Set variables and constants
		const float MAX_HEIGHT = 5000.0f;
		const float RAY_DISTANCE = 1000.0f;

		float planeSizeX = generationInfo.plane.localScale.x;
		float planeSizeZ = generationInfo.plane.localScale.z;

		float squareSizeX = planeSizeX / generationInfo.FOVMapWidth;
		float squareSizeZ = planeSizeZ / generationInfo.FOVMapHeight;

		int directionsPerSquare = CHANNELS_PER_TEXEL * generationInfo.layerCount;

		float anglePerDirection = 360.0f / directionsPerSquare;
		float anglePerSample = generationInfo.samplingAngle / (generationInfo.samplesPerDirection - 1); // ex) 10 samples for 180 degrees: -90, -70, -50, ..., 70, 90 

		Func<float, Vector3> directionFromAngle = (angle) =>
		{
			return new Vector3(Mathf.Cos(angle * Mathf.Deg2Rad), 0.0f, Mathf.Sin(angle * Mathf.Deg2Rad));
		};

		Func<Vector3, Vector3, float> XZDistance = (v1, v2) =>
		{
			v1.y = 0.0f;
			v2.y = 0.0f;
			return Vector3.Distance(v1, v2);
		};

		// Create an array of FOV maps
		Color[][] FOVMapTexels = Enumerable.Range(0, generationInfo.layerCount).Select(_ => new Color[generationInfo.FOVMapWidth * generationInfo.FOVMapHeight]).ToArray();

		for (int squareZ = 0; squareZ < generationInfo.FOVMapHeight; ++squareZ)
		{
			for (int squareX = 0; squareX < generationInfo.FOVMapWidth; ++squareX)
			{
				// Position above the sampling point
				// Add 0.5f to align to the center of each square.
				Vector3 rayOriginPosition =
					generationInfo.plane.position +
					((squareZ + 0.5f) / generationInfo.FOVMapHeight) * planeSizeZ * generationInfo.plane.forward +
					((squareX + 0.5f) / generationInfo.FOVMapWidth) * planeSizeX * generationInfo.plane.right;
				rayOriginPosition.y = MAX_HEIGHT;

				RaycastHit hitLevel;
				if (Physics.Raycast(rayOriginPosition, Vector3.down, out hitLevel, 2 * MAX_HEIGHT, generationInfo.levelLayer)) // Level found
				{
					Vector3 centerPosition = hitLevel.point + generationInfo.eyeHeight * Vector3.up; // Apply the center height(possibly the height of the unit)
					float height = hitLevel.point.y - generationInfo.plane.position.y;
					if (height < 0.0f)
					{
						Debug.Log("The FOW plane should be located completely below the level.");
						return null;
					}

					// For all possible directions at this square
					for (int directionIdx = 0; directionIdx < directionsPerSquare; ++directionIdx)
					{
						// Sample a distance to an obstacle
						float angleToward = Vector3.SignedAngle(generationInfo.plane.right, Vector3.right, Vector3.up) + directionIdx * anglePerDirection;

						// Level-adaptive multisampling
						float maxSight = 0.0f; // Maximum sight viewed from the center
						Vector3 samplingDirection = directionFromAngle(angleToward);
						float samplingInterval = generationInfo.samplingRange / generationInfo.samplesPerDirection;
						bool obstacleHit = false;
						for (int samplingIdx = 0; samplingIdx < generationInfo.samplesPerDirection; ++samplingIdx)
						{
							// For each vertical angle
							float samplingAngle = -generationInfo.samplingAngle / 2.0f + samplingIdx * anglePerSample;

							// Apply the sampling angle
							Vector3 samplingLine = samplingDirection;
							samplingLine.y = samplingLine.magnitude * Mathf.Tan(samplingAngle * Mathf.Deg2Rad);

							// Update max sight
							RaycastHit hitBlocked;
							if (Physics.Raycast(centerPosition, samplingLine, out hitBlocked, RAY_DISTANCE, generationInfo.levelLayer)) // Blocking level exists
							{
								obstacleHit = true;
								float blockedDistance = XZDistance(centerPosition, hitBlocked.point);
								if (blockedDistance > maxSight)
								{
									maxSight = Mathf.Clamp(blockedDistance, 0.0f, generationInfo.samplingRange);
								}

								// If the surface is almost vertical and high enough, stop sampling here
								if (Vector3.Angle(hitBlocked.normal, Vector3.up) >= generationInfo.blockingSurfaceAngleThreshold && samplingAngle >= generationInfo.blockedRayAngleThreshold)
								{
									break;
								}
							}
							else if (samplingIdx <= (generationInfo.samplesPerDirection + 2 - 1) / 2) // No hit below the eye line yields a maximum sight
							{
								maxSight = generationInfo.samplingRange;
							}
							else if (obstacleHit) // Previous ray hit an obstacle, but this one hasn't
							{
								// Binary search to find an edge
								float angularInterval = anglePerSample / 2.0f;
								float searchingAngle = samplingAngle - angularInterval;
								for (int i = 0; i < generationInfo.binarySearchCount; ++i)
								{
									angularInterval /= 2.0f;

									Vector3 searchingLine = samplingDirection;
									searchingLine.y = searchingLine.magnitude * Mathf.Tan(searchingAngle * Mathf.Deg2Rad);

									RaycastHit hitSearched;
									if (Physics.Raycast(centerPosition, searchingLine, out hitSearched, RAY_DISTANCE, generationInfo.levelLayer))
									{
										searchingAngle = searchingAngle + angularInterval; // Next range is the upper half

										// Update maxSight
										float searchedDistance = XZDistance(centerPosition, hitSearched.point);
										if (searchedDistance >= maxSight)
										{
											maxSight = Mathf.Clamp(searchedDistance, 0.0f, generationInfo.samplingRange);
										}
									}
									else
									{
										searchingAngle = searchingAngle - angularInterval; // Next range is the lower half
									}
								}

								break;
							}
						}
						float distanceRatio = maxSight == 0.0f ? 1.0f : maxSight / generationInfo.samplingRange;

						// Find the location to store
						int layerIdx = directionIdx / CHANNELS_PER_TEXEL;
						int channelIdx = directionIdx % CHANNELS_PER_TEXEL;

						// Store
						FOVMapTexels[layerIdx][squareZ * generationInfo.FOVMapWidth + squareX][channelIdx] = distanceRatio;
					}
				}
				else // No level found
				{
					// Fill all the layers with white
					for (int layerIdx = 0; layerIdx < generationInfo.layerCount; ++layerIdx)
					{
						FOVMapTexels[layerIdx][squareZ * generationInfo.FOVMapWidth + squareX] = Color.white;
					}
				}
			}

			if (progressAction.Invoke(squareZ, generationInfo.FOVMapHeight)) return null;
		}

		// Store the FOV info in a texture array
		Texture2DArray textureArray = new Texture2DArray(generationInfo.FOVMapWidth, generationInfo.FOVMapHeight, generationInfo.layerCount, TextureFormat.RGBA32, false, false);
		textureArray.filterMode = FilterMode.Bilinear;
		textureArray.wrapMode = TextureWrapMode.Clamp;

		for (int layerIdx = 0; layerIdx < generationInfo.layerCount; ++layerIdx)
		{
			textureArray.SetPixels(FOVMapTexels[layerIdx], layerIdx, 0);
		}

		return textureArray;
	}
}
}