using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using FOVMapping;

namespace FOVMapping
{

public static class FOVMapGenerator
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
			Debug.LogError(e.ToString());
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

		float planeSizeX = generationInfo.plane.localScale.x;
		float planeSizeZ = generationInfo.plane.localScale.z;

		float squareSizeX = planeSizeX / generationInfo.FOVMapWidth;
		float squareSizeZ = planeSizeZ / generationInfo.FOVMapHeight;

		int directionsPerSquare = CHANNELS_PER_TEXEL * generationInfo.layerCount;

		float anglePerDirection = 360.0f / directionsPerSquare;
		float anglePerSample = generationInfo.samplingAngle / (generationInfo.samplesPerDirection - 1); // ex) 10 samples for 180 degrees: -90, -70, -50, ..., 70, 90 

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

				// First, raycast down to find the terrain.
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

						var distanceRatio = FindNearestObstacle(generationInfo, angleToward, anglePerSample, centerPosition);

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
		bool isLinear = (PlayerSettings.colorSpace == ColorSpace.Linear);
		Texture2DArray textureArray = new Texture2DArray(generationInfo.FOVMapWidth, generationInfo.FOVMapHeight, generationInfo.layerCount, TextureFormat.RGBA32, false, isLinear);
		textureArray.filterMode = FilterMode.Bilinear;
		textureArray.wrapMode = TextureWrapMode.Clamp;

		for (int layerIdx = 0; layerIdx < generationInfo.layerCount; ++layerIdx)
		{
			textureArray.SetPixels(FOVMapTexels[layerIdx], layerIdx, 0);
		}

		return textureArray;
	}

	private static float FindNearestObstacle(FOVMapGenerationInfo generationInfo, float angleToward, float anglePerSample, Vector3 centerPosition) {
		Vector3 DirectionFromAngle(float angle) {
			return new Vector3(Mathf.Cos(angle * Mathf.Deg2Rad), 0.0f, Mathf.Sin(angle * Mathf.Deg2Rad));
		}

		float XZDistance(Vector3 v1, Vector3 v2) {
			v1.y = 0.0f;
			v2.y = 0.0f;
			return Vector3.Distance(v1, v2);
		}
		
		const float RAY_DISTANCE = 1000.0f;
		
		// Level-adaptive multisampling
		float maxSight = 0.0f; // Maximum sight viewed from the center

		Vector3 samplingDirection = DirectionFromAngle(angleToward);
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
		return distanceRatio;
	}

	/// <summary>
	/// Bakes a FOV map using the provided settings and FOVManager transform
	/// </summary>
	/// <param name="settings">The FOV bake settings to use</param>
	/// <param name="fovManagerTransform">The transform of the FOVManager (used as the plane)</param>
	/// <param name="durationSeconds">Output parameter containing the baking duration in seconds</param>
	/// <returns>True if baking was successful, false otherwise</returns>
	public static bool BakeFOVMap(FOVBakeSettings settings, Transform fovManagerTransform, out double durationSeconds)
	{
		if (settings == null || fovManagerTransform == null)
		{
			Debug.LogError("FOVMapGenerator: Settings or FOVManager transform is null");
			durationSeconds = 0;
			return false;
		}

		double startTime = Time.realtimeSinceStartup;

		// Create generation info with the FOVManager's transform as the plane
		FOVMapGenerationInfo generationInfo = settings.ToGenerationInfo(fovManagerTransform);

		bool isSuccessful = CreateFOVMap
		(
			generationInfo,
			(y, height) =>
			{
				return EditorUtility.DisplayCancelableProgressBar("Baking FOV Map", $"Processed {y} / {height} rows", (float)y / height);
			}
		);

		EditorUtility.ClearProgressBar();

		double endTime = Time.realtimeSinceStartup;
		durationSeconds = endTime - startTime;
		
		if (isSuccessful)
		{
			// Auto-assign the generated FOV map to settings
			string assetPath = $"Assets/{settings.path}/{settings.fileName}.asset";
			Texture2DArray generatedFOVMap = AssetDatabase.LoadAssetAtPath<Texture2DArray>(assetPath);
			if (generatedFOVMap != null)
			{
				settings.FOVMapArray = generatedFOVMap;
				EditorUtility.SetDirty(settings);
			}
		}

		return isSuccessful;
	}

	/// <summary>
	/// Bakes a FOV map and shows a dialog with the result
	/// </summary>
	/// <param name="settings">The FOV bake settings to use</param>
	/// <param name="fovManagerTransform">The transform of the FOVManager (used as the plane)</param>
	public static void BakeFOVMapWithDialog(FOVBakeSettings settings, Transform fovManagerTransform)
	{
		if (settings == null)
		{
			EditorUtility.DisplayDialog("FOV Mapping Error", "FOVBakeSettings not assigned! Please assign a FOVBakeSettings asset.", "OK");
			return;
		}

		if (fovManagerTransform == null)
		{
			EditorUtility.DisplayDialog("FOV Mapping Error", "FOVManager transform is null!", "OK");
			return;
		}

		bool isSuccessful = BakeFOVMap(settings, fovManagerTransform, out double durationSeconds);

		if (isSuccessful)
		{
			EditorUtility.DisplayDialog("FOV Mapping", $"FOV map baking completed successfully in {(int)durationSeconds} seconds ({durationSeconds / 60:F2} minutes)!", "OK");
		}
		else
		{
			EditorUtility.DisplayDialog("FOV Mapping", $"FOV map baking failed after {(int)durationSeconds} seconds ({durationSeconds / 60:F2} minutes). Check the console for details.", "OK");
		}
	}
}
}