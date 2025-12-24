using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using FOVMapping;

namespace FOVMapping
{

/// <summary>
/// Interface for FOV map generation strategies
/// </summary>
public interface IFOVGenerator
{
    /// <summary>
    /// Generates a FOV map using the specific algorithm implementation
    /// </summary>
    /// <param name="generationInfo">The generation parameters and settings</param>
    /// <param name="progressAction">Progress callback function</param>
    /// <returns>Generated Texture2DArray or null if failed</returns>
    Color[][] Generate(FOVMapGenerationInfo generationInfo, Func<string, int, int, string, bool> progressAction);
}

/// <summary>
/// Helper class for managing progress stages with consistent naming
/// </summary>
public static class FOVProgressStages
{
    public static readonly string GroundDetection = "Ground Detection";
    public static readonly string FOVRaycasting = "FOV Raycasting";
    public static readonly string CreatingTexture = "Creating Texture";
    
    public static readonly string Cells = "cells";
    public static readonly string Directions = "directions";
    public static readonly string Waves = "waves";
}

/// <summary>
/// FOVMapGenerator creates Field of View maps for fog of war systems.
/// A Field of View map is a Texture2DArray, where each 2d cell represents a world space position, and the array dimension encodes each
/// direction from that position how far away can be seen from that location.
/// 
/// OUTPUT:
/// Creates a Texture2DArray where each layer represents a direction range, and each
/// texel stores the visibility ratio (0-1) for that direction from that grid position.
/// </summary>
public static class FOVMapGenerator
{
	public const int CHANNELS_PER_TEXEL = 4;
	
	// Dictionary of strategies for each bake algorithm
	private static readonly Dictionary<BakeAlgorithm, IFOVGenerator> _strategies = new Dictionary<BakeAlgorithm, IFOVGenerator>
	{
		{ BakeAlgorithm.SingleThreaded, new FOVGeneratorSingleThreaded() },
		{ BakeAlgorithm.BatchedRaycasts, new FOVGeneratorBatchedRaycasts() },
		{ BakeAlgorithm.BatchedJobs, new FOVGeneratorBatchedJobs() },
		{ BakeAlgorithm.DistanceSearch , new FOVGeneratorDistanceBasedSearch()}
	};

	public static bool CreateFOVMap(FOVMapGenerationInfo generationInfo, Func<string, int, int, string, bool> progressAction)
	{
		// Get the appropriate strategy based on the bake algorithm
		if (!_strategies.TryGetValue(generationInfo.bakeAlgorithm, out IFOVGenerator strategy))
		{
			Debug.LogError($"FOVMapGenerator: Unknown bake algorithm: {generationInfo.bakeAlgorithm}. New strategies must be added to the _strategies dictionary in FOVMapGenerator.cs");
			return false;
		}

		// Use the strategy to generate the FOV map
		Color[][] mapTexels = strategy.Generate(generationInfo, progressAction);
		
		// Save the maps to Asset Database (disk)
		string FOVMapPath = $"Assets/{generationInfo.path}/{generationInfo.fileName}.asset";
		bool isLinear = (PlayerSettings.colorSpace == ColorSpace.Linear);
		
		try
		{
			// Load existing asset to check if we can reuse it
			Texture2DArray existingAsset = AssetDatabase.LoadAssetAtPath<Texture2DArray>(FOVMapPath);
			
			// Check if we can reuse the existing asset
			bool canReuse = existingAsset != null &&
			               existingAsset.width == generationInfo.FOVMapWidth &&
			               existingAsset.height == generationInfo.FOVMapHeight &&
			               existingAsset.depth == generationInfo.layerCount &&
			               existingAsset.format == TextureFormat.RGBA32 &&
			               existingAsset.isDataSRGB == !isLinear;
			
			if (canReuse)
			{
				// Reuse existing asset - just update its data
				for (int layerIdx = 0; layerIdx < generationInfo.layerCount; ++layerIdx)
				{
					existingAsset.SetPixels32(ConvertColorsToColor32(mapTexels[layerIdx]), layerIdx, 0);
				}
				existingAsset.mipMapBias = generationInfo.samplingRange;
				existingAsset.Apply();
				
				// Mark the asset as dirty and save without full refresh
				EditorUtility.SetDirty(existingAsset);
				AssetDatabase.SaveAssets();
			}
			else
			{
				// Need to create new asset (either doesn't exist or dimensions/format changed)
				if (existingAsset != null)
				{
					AssetDatabase.DeleteAsset(FOVMapPath);
				}
				
				// Create new texture array
				Texture2DArray textureArray = new Texture2DArray(generationInfo.FOVMapWidth, generationInfo.FOVMapHeight, generationInfo.layerCount, TextureFormat.RGBA32, mipChain: false, isLinear);
				textureArray.filterMode = FilterMode.Bilinear;
				textureArray.wrapMode = TextureWrapMode.Clamp;

				for (int layerIdx = 0; layerIdx < generationInfo.layerCount; ++layerIdx)
				{
					textureArray.SetPixels(mapTexels[layerIdx], layerIdx, 0);
				}
				textureArray.mipMapBias = generationInfo.samplingRange;
				
				AssetDatabase.CreateAsset(textureArray, FOVMapPath);
				AssetDatabase.SaveAssets();
			}
		}
		catch (Exception e)
		{
			Debug.LogError(e.ToString());
			return false;
		}

		return true;
	}
	
	// Utility Functions

	/// <summary>
	/// Converts Color array to Color32 array for efficient texture updates
	/// </summary>
	/// <param name="colors">Color array to convert</param>
	/// <returns>Color32 array</returns>
	private static Color32[] ConvertColorsToColor32(Color[] colors)
	{
		Color32[] color32s = new Color32[colors.Length];
		for (int i = 0; i < colors.Length; i++)
		{
			color32s[i] = colors[i];
		}
		return color32s;
	}


	/// <summary>
	/// Bakes a FOV map using the provided settings and FOVManager transform
	/// </summary>
	/// <param name="settings">The FOV bake settings to use</param>
	/// <param name="fovManagerTransform">The transform of the FOVManager (used as the plane)</param>
	/// <param name="durationSeconds">Output parameter containing the baking duration in seconds</param>
	/// <returns>True if baking was successful, false otherwise</returns>
	public static bool BakeFOVMap(FOVBakeSettings settings, FOVManager fovManager, out double durationSeconds)
	{
		if (settings == null || fovManager == null)
		{
			Debug.LogError("FOVMapGenerator: Settings or FOVManager transform is null");
			durationSeconds = 0;
			return false;
		}

		double startTime = Time.realtimeSinceStartup;

		// Create generation info with the FOVManager's transform as the plane
		FOVMapGenerationInfo generationInfo = settings.ToGenerationInfo(fovManager.transform);

		bool isSuccessful = CreateFOVMap
		(
			generationInfo,
			(stage, doneItems, itemTotal, itemName) =>
			{
				string algorithmName = generationInfo.bakeAlgorithm.ToString();
				float progress = itemTotal > 0 ? (float)doneItems / itemTotal : 0f;
				string progressText = $"{stage} - {doneItems}/{itemTotal} {itemName} ({progress:P0})";
				
				return EditorUtility.DisplayCancelableProgressBar($"Baking FOV Map ({algorithmName})", progressText, progress);
			}
		);

		EditorUtility.ClearProgressBar();

		double endTime = Time.realtimeSinceStartup;
		durationSeconds = endTime - startTime;

		Debug.Log($"FOVMapGenerator: FOV map generation {(isSuccessful ? "completed successfully" : "FAILED")} after {durationSeconds} seconds ({durationSeconds / 60:F2} minutes).");
		
		if (isSuccessful)
		{
			// Auto-assign the generated FOV map to settings
			string assetPath = $"Assets/{settings.path}/{settings.fileName}.asset";
			
			// Load and assign the generated map
			Texture2DArray generatedFOVMap = AssetDatabase.LoadAssetAtPath<Texture2DArray>(assetPath);
			if (generatedFOVMap != null)
			{
				settings.FOVMapArray = generatedFOVMap;
				EditorUtility.SetDirty(settings);
				AssetDatabase.SaveAssets();
			}
			else
			{
				Debug.LogError($"FOVMapGenerator: Failed to load generated FOV map from: {assetPath}");
			}
		}

		return isSuccessful;
	}

	/// <summary>
	/// Bakes a FOV map and shows a dialog with the result
	/// </summary>
	/// <param name="settings">The FOV bake settings to use</param>
	/// <param name="fovManagerTransform">The transform of the FOVManager (used as the plane)</param>
	public static void BakeFOVMapWithDialog(FOVBakeSettings settings, FOVManager fovManager)
	{
		if (settings == null)
		{
			EditorUtility.DisplayDialog("FOV Mapping Error", "FOVBakeSettings not assigned! Please assign a FOVBakeSettings asset.", "OK");
			return;
		}

		if (fovManager == null)
		{
			EditorUtility.DisplayDialog("FOV Mapping Error", "FOVManager transform is null!", "OK");
			return;
		}

		bool isSuccessful = BakeFOVMap(settings, fovManager, out double durationSeconds);

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