using UnityEngine;
using FOVMapping;

namespace FOVMapping
{
    [CreateAssetMenu(fileName = "FOVBakeSettings", menuName = "FOV Mapping/Bake Settings")]
    public class FOVBakeSettings : ScriptableObject
    {
        [Header("FOV Map Generation")]
        [Tooltip("(Essential) Path to save the generated FOV map")] 
        public string path = "FOVMapping/FOVMaps";
        
        [Tooltip("(Essential) Name of the FOV map file")] 
        public string fileName = "FOVMap1024";
        
        
        [Tooltip("(Essential) Layer of the level to be sampled")] 
        public LayerMask levelLayer;
        
        [Tooltip("Width of the generated FOV map")] 
        public int FOVMapWidth = 1024;
        
        [Tooltip("Height of the generated FOV map")] 
        public int FOVMapHeight = 1024;
        
        [Tooltip("Number of layers in the generated FOV map")] 
        public int layerCount = 90;
        
        [Tooltip("Height of the 'sampling eye'")] 
        public float eyeHeight = 1.8f;
        
        [Tooltip("Maximum sampling range; sight system does not work beyond this boundary")] 
        public float samplingRange = 50.0f;
        
        [Tooltip("(Advanced) Vertical angular range from the sampling eye")] 
        public float samplingAngle = 140.0f;
        
        [Tooltip("(Advanced) How many rays will be fired toward a direction at a location?")] 
        public int samplesPerDirection = 9;
        
        [Tooltip("(Advanced) How many iterations for the binary search to find an edge?")] 
        public int binarySearchCount = 10;
        
        [Tooltip("(Advanced) Surfaces steeper than this angle are considered vertical and there will be no further sampling toward the direction at the location.")] 
        public float blockingSurfaceAngleThreshold = 85.0f;
        
        [Tooltip("(Advanced) Surfaces located below this vertical angle are never considered vertical.")] 
        public float blockedRayAngleThreshold = 0.0f;


        [Header("Generated Assets")]
        [Tooltip("Generated FOV map Texture2DArray")]
        public Texture2DArray FOVMapArray;

        /// <summary>
        /// Converts the settings to FOVMapGenerationInfo for baking
        /// Note: The plane must be set separately since it's a scene object
        /// </summary>
        public FOVMapGenerationInfo ToGenerationInfo(Transform plane)
        {
            return new FOVMapGenerationInfo
            {
                path = this.path,
                fileName = this.fileName,
                plane = plane,
                levelLayer = this.levelLayer,
                FOVMapWidth = this.FOVMapWidth,
                FOVMapHeight = this.FOVMapHeight,
                layerCount = this.layerCount,
                eyeHeight = this.eyeHeight,
                samplingRange = this.samplingRange,
                samplingAngle = this.samplingAngle,
                samplesPerDirection = this.samplesPerDirection,
                binarySearchCount = this.binarySearchCount,
                blockingSurfaceAngleThreshold = this.blockingSurfaceAngleThreshold,
                blockedRayAngleThreshold = this.blockedRayAngleThreshold
            };
        }
    }
}

/*
FOWTextureSize Mismatch Analysis
Based on the code examination, here's what happens when the FOVManager's FOWTextureSize doesn't match the editor's baked FOV map dimensions:
The Problem:
FOVManager creates a RenderTexture with size FOWTextureSize × FOWTextureSize (line 148)
FOVMapGenerator creates a Texture2DArray with dimensions FOVMapWidth × FOVMapHeight (line 250)
Agent visibility calculation uses FOWTextureSize to convert world coordinates to UV coordinates (lines 358-361)
What Happens with Mismatch:
Case 1: FOWTextureSize > FOVMap dimensions
The runtime FOW texture is larger than the baked FOV map
Result: The FOV map gets stretched/upsampled when sampled by the shader
Visual: Blurry/less precise obstacle detection, but no crashes
Case 2: FOWTextureSize < FOVMap dimensions
The runtime FOW texture is smaller than the baked FOV map
Result: The FOV map gets downsampled when sampled by the shader
Visual: Loss of detail, potential aliasing, but still functional
Case 3: Agent visibility calculation mismatch
The agentUV *= FOWTextureSize calculation (line 361) assumes the FOW texture size
If this doesn't match the actual FOV map resolution, agent visibility sampling will be incorrect
Result: Agents may appear/disappear at wrong locations relative to obstacles
The Real Issue:
The most critical problem is that agent visibility sampling coordinates are calculated using FOWTextureSize, but the actual FOV map may have different dimensions. This means:
Agent visibility checks will sample the wrong pixels in the FOV map
Agents may be visible when they should be hidden (or vice versa)
The mismatch creates a coordinate system misalignment
Recommendation:
The system should either:
Enforce matching sizes - Add validation to ensure FOWTextureSize matches the FOV map dimensions
Use FOV map dimensions - Calculate agent UV coordinates based on the actual FOV map size rather than FOWTextureSize
Add a scaling factor - Account for the size difference in the UV coordinate calculation
*/