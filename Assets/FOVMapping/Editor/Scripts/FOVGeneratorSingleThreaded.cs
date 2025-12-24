using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FOVMapping 
{
/// <summary>
/// Single-threaded FOV map generation strategy
///
/// ALGORITHM OVERVIEW:
/// The FOV map generation process consists of three distinct stages that work together to create
/// a comprehensive visibility map for each grid cell across multiple directions.
/// 
/// STAGE 1: GROUND HEIGHT DETECTION
/// - Raycast downward from maximum height (5000 units) at each grid cell center
/// - Find the terrain/ground level to establish the base height for visibility sampling
/// - Calculate the "eye position" by adding eyeHeight to the ground level
/// - One raycast per grid cell (FOVMapWidth × FOVMapHeight total rays)
/// - Cells without ground are filled with white (maximum visibility)
/// 
/// STAGE 2: OBSTACLE DETECTION PER DIRECTION
/// - For each valid grid cell, sample multiple directions (4 channels × layerCount directions)
/// - For each direction, cast multiple rays at different vertical angles (samplesPerDirection rays)
/// - Uses "level-adaptive multisampling" to find maximum visible distance before hitting obstacles
/// - Initial sampling uses fixed vertical angles from -samplingAngle/2 to +samplingAngle/2
/// - Tracks the maximum sight distance achieved before hitting terrain obstacles
/// 
/// STAGE 3: BINARY SEARCH EDGE REFINEMENT
/// - When a ray transitions from "hit obstacle" to "no hit", perform binary search
/// - Find the precise vertical angle where terrain visibility ends (e.g., top of a wall)
/// - Uses binarySearchCount iterations to narrow down the exact angle
/// - Early termination if surface is nearly vertical (blockingSurfaceAngleThreshold)
/// - This creates smooth transitions between visible and blocked areas
/// 
/// 
/// </summary>
public sealed class FOVGeneratorSingleThreaded : IFOVGenerator 
{
	public Color[][] Generate(FOVMapGenerationInfo generationInfo, Func<string, int, int, string, bool> progressAction) {
		return GenerateFOVMap(generationInfo, progressAction);
	}
	
	private static Color[][] GenerateFOVMap(FOVMapGenerationInfo generationInfo, Func<string, int, int, string, bool> progressAction)
	{
		// Basic checks
		bool checkPassed = generationInfo.CheckSettings();

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

		int directionsPerSquare = FOVMapGenerator.CHANNELS_PER_TEXEL * generationInfo.layerCount;

		float anglePerDirection = 360.0f / directionsPerSquare;
		float anglePerSample = generationInfo.samplingAngle / (generationInfo.samplesPerDirection - 1); // ex) 10 samples for 180 degrees: -90, -70, -50, ..., 70, 90 

		// Create an array of FOV maps
		Color[][] FOVMapTexels = Enumerable.Range(0, generationInfo.layerCount).Select(_ => new Color[generationInfo.FOVMapWidth * generationInfo.FOVMapHeight]).ToArray();

		for (int squareZ = 0; squareZ < generationInfo.FOVMapHeight; ++squareZ)
		{
			for (int squareX = 0; squareX < generationInfo.FOVMapWidth; ++squareX)
			{
				// STAGE 1: GROUND HEIGHT DETECTION
				
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
						
						// STAGE 2 & 3
						var distanceRatio = FindNearestObstacle(generationInfo, angleToward, anglePerSample, centerPosition);

						// Find the location to store
						int layerIdx = directionIdx / FOVMapGenerator.CHANNELS_PER_TEXEL;
						int channelIdx = directionIdx % FOVMapGenerator.CHANNELS_PER_TEXEL;

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

			if (progressAction.Invoke(FOVProgressStages.FOVRaycasting, squareZ, generationInfo.FOVMapHeight, FOVProgressStages.Cells)) return null;
		}

		return FOVMapTexels;
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
}
}
