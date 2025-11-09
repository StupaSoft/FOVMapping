using UnityEngine;
using UnityEngine.Assertions;
using FOVMapping;

namespace FOVMapping
{
    [RequireComponent(typeof(LineRenderer))]
    public class FOVCircle3D : MonoBehaviour {
        [Header("FOV Settings")]
        [Tooltip("Reference to FOVManager to access the baked FOV map")]
        public FOVManager fovManager;
        
        [Header("Circle Settings")]
        [Tooltip("Quantize position to FOV cell centers for consistent sampling")]
        public bool quantizeToCellCenter = true;
		
		[Tooltip("Optional hard cap on raw distance (in meters) before multipliers; 0 = disabled")] 
		public float clampMaxRadius = 0f;
        
        // These will be set automatically from FOV settings
        private int segments;
        private float maxRadius;
        
        private LineRenderer lr;
        private FOVBakeSettings settings;
        private Texture2DArray fovMapArray;
        
        [Header("Performance")]
        [Tooltip("Minimum distance to move before updating circle (meters)")]
        public float updateThreshold = 0.1f;
        
        private Vector3 lastUpdatePosition;
        private bool hasInitialized = false;
        
        private void Awake() {
            lr = GetComponent<LineRenderer>();
            if (fovManager != null) {
                settings = fovManager.Settings;
                if (settings != null) {
                    fovMapArray = settings.FOVMapArray;
                }
            }
        }
        
        private void Start() {
            if (fovManager == null) {
                Debug.LogError("FOVManager not assigned to FOVCircle3D!");
                return;
            }
            
            if (settings == null) {
                Debug.LogError("FOVBakeSettings not found in FOVManager!");
                return;
            }
            
            if (fovMapArray == null) {
                Debug.LogError("FOVMapArray not found in FOVBakeSettings!");
                return;
            }
            
            // Set segments to match the baked FOV map resolution
            segments = 4 * fovMapArray.depth; // 4 channels per layer, total directions baked
            
            // Set max radius to the sampling range from bake settings
            maxRadius = fovMapArray.mipMapBias; // This stores the sampling range
            
            Debug.Log($"FOVCircle3D initialized: segments={segments}, maxRadius={maxRadius}");
            
            // Initialize position tracking
            lastUpdatePosition = transform.position;
            hasInitialized = true;
            UpdateCircle();
        }
        
        private void Update() {
            if (!hasInitialized) return;
            
            // Check if position has moved enough to warrant an update
            float distanceMoved = Vector3.Distance(transform.position, lastUpdatePosition);
            if (distanceMoved >= updateThreshold) {
                lastUpdatePosition = transform.position;
                UpdateCircle();
            }
        }
        
        public void UpdateCircle() {
            if (lr == null) {
                lr = GetComponent<LineRenderer>();
                Assert.IsNotNull(lr);
            }
            
            if (fovMapArray == null) {
                Debug.LogWarning("FOVMapArray not available, using default circle");
                SetLrPositions(10f); // Default radius
                return;
            }
            
            // Sample FOV data for all directions and create a circle with varying radius
            CreateFOVCircle();
        }
        
        private void CreateFOVCircle() {
            // Use the GameObject's world position (like FOVAgent does)
            Vector3 worldPosition = transform.position;
            
            // Get the quantized cell center position for drawing (if enabled)
            Vector3 offsetFromTransform = Vector3.zero;
            if (quantizeToCellCenter) {
                Vector3 quantizedCenter = GetQuantizedCellCenter(worldPosition);
                offsetFromTransform = quantizedCenter - transform.position;
            }
            
            // Create circle points with radius based on FOV data
            lr.positionCount = segments;
            lr.loop = true;
            
            float angleStep = 360f / segments;
            
            for (int i = 0; i < segments; i++) {
                float angle = i * angleStep;
                
                // Get FOV distance ratio for this direction
                float distanceRatio = GetFOVDistanceRatio(worldPosition, angle);
                
                // Calculate raw distance based on FOV data and baked sampling range
                float rawDistance = distanceRatio * maxRadius;
                
                // Optional clamp before applying multipliers
                if (clampMaxRadius > 0f && rawDistance > clampMaxRadius) {
                    rawDistance = clampMaxRadius;
                }
                
                // Final radius
                float radius = rawDistance;
                
                // Create circle point (0° at +X to match shader)
                float x = Mathf.Cos(Mathf.Deg2Rad * angle) * radius;
                float z = Mathf.Sin(Mathf.Deg2Rad * angle) * radius;
                
                // Position relative to quantized center, not transform position
                lr.SetPosition(i, new Vector3(x, 0, z) + offsetFromTransform);
            }
        }
        
        private void SetLrPositions(float radius) {
            lr.positionCount = segments;
            lr.loop = true;
            
            float x;
            float z;
            float angle = 360f / segments;
            
            for (int i = 0; i < segments; i++) {
                x = Mathf.Sin(Mathf.Deg2Rad * angle) * radius;
                z = Mathf.Cos(Mathf.Deg2Rad * angle) * radius;
                
                lr.SetPosition(i, new Vector3(x, 0, z));
                angle += (360f / segments);
            }
        }
        
        /// <summary>
        /// Gets the quantized world position at the center of the FOV cell
        /// </summary>
        /// <param name="worldPos">World position to quantize</param>
        /// <returns>World position at the center of the nearest FOV cell</returns>
        private Vector3 GetQuantizedCellCenter(Vector3 worldPos) {
            if (fovMapArray == null || fovManager == null) return worldPos;
            
            // Convert world position to FOV plane local coordinates
            Vector3 relativePos = Vector3.Scale(
                fovManager.transform.InverseTransformPoint(worldPos), 
                fovManager.transform.lossyScale
            );
            
            // Convert to UV coordinates (0-1 range)
            float uvX = relativePos.x / fovManager.transform.lossyScale.x;
            float uvZ = relativePos.z / fovManager.transform.lossyScale.z;
            
            // Clamp UV coordinates to valid range
            uvX = Mathf.Clamp01(uvX);
            uvZ = Mathf.Clamp01(uvZ);
            
            // Convert to cell coordinates
            float cellX = uvX * fovMapArray.width;
            float cellZ = uvZ * fovMapArray.height;
            
            // Snap to cell center
            int cellCenterX = Mathf.RoundToInt(cellX);
            int cellCenterZ = Mathf.RoundToInt(cellZ);
            
            // Clamp to texture bounds
            cellCenterX = Mathf.Clamp(cellCenterX, 0, fovMapArray.width - 1);
            cellCenterZ = Mathf.Clamp(cellCenterZ, 0, fovMapArray.height - 1);
            
            // Convert back to UV coordinates (cell center uses +0.5f offset)
            float quantizedUVX = (cellCenterX + 0.5f) / fovMapArray.width;
            float quantizedUVZ = (cellCenterZ + 0.5f) / fovMapArray.height;
            
            // Convert back to relative position
            Vector3 quantizedRelativePos = new Vector3(
                quantizedUVX * fovManager.transform.lossyScale.x,
                relativePos.y, // Keep original Y
                quantizedUVZ * fovManager.transform.lossyScale.z
            );
            
            // Convert back to world coordinates
            Vector3 quantizedWorldPos = fovManager.transform.TransformPoint(
                quantizedRelativePos / fovManager.transform.lossyScale.x // Undo the Scale multiplication
            );
            
            return quantizedWorldPos;
        }
        
        /// <summary>
        /// Gets the FOV distance ratio at a world position for a specific direction
        /// Uses the same logic as the FOVMapping shader for consistency
        /// </summary>
        /// <param name="worldPos">World position to sample (agent position in shader)</param>
        /// <param name="directionDegrees">Direction in degrees (0-360, where 0 = +X axis, 90 = +Z axis)</param>
        /// <returns>Distance ratio (0-1) from the FOV map</returns>
        private float GetFOVDistanceRatio(Vector3 worldPos, float directionDegrees) {
            if (fovMapArray == null || fovManager == null) {
                return 1f; // Default to maximum visibility
            }
            
            // Convert world position to FOV plane local coordinates
            // The shader samples using: agentPosition.x / _PlaneSizeX, agentPosition.z / _PlaneSizeZ
            Vector3 relativePos = Vector3.Scale(
                fovManager.transform.InverseTransformPoint(worldPos), 
                fovManager.transform.lossyScale
            );
            
            // Convert to UV coordinates (0-1 range) - matching shader line 104-105
            // UV = agentPosition / PlaneSize (in plane local space)
            float uvX = relativePos.x / fovManager.transform.lossyScale.x;
            float uvZ = relativePos.z / fovManager.transform.lossyScale.z;
            
            // Clamp UV coordinates to valid range
            uvX = Mathf.Clamp01(uvX);
            uvZ = Mathf.Clamp01(uvZ);
            
            // Convert degrees to radians and match shader's angle calculation
            // Shader uses: angle = atan2(direction.z, direction.x) remapped to [0, 2*PI]
            // Our input is already in degrees [0, 360], but we need to convert to match shader's coordinate system
            float angleRadians = directionDegrees * Mathf.Deg2Rad;
            
            // Calculate direction index (matching shader lines 93-96)
            int directionsPerSquare = 4 * fovMapArray.depth; // CHANNELS_PER_TEXEL * _LayerCount
            float anglePerDirection = (2f * Mathf.PI) / directionsPerSquare;
            float directionFactor = angleRadians / anglePerDirection;
            
            // Sample two adjacent directions and interpolate (matching shader)
            int directionIdx0 = Mathf.FloorToInt(directionFactor);
            int directionIdx1 = (directionIdx0 + 1) % directionsPerSquare;
            
            int layerIdx0 = directionIdx0 / 4;
            int layerIdx1 = directionIdx1 / 4;
            
            int channelIdx0 = directionIdx0 % 4;
            int channelIdx1 = directionIdx1 % 4;
            
            // Read from texture
            float distanceRatio0 = ReadFOVMapPixel(uvX, uvZ, layerIdx0, channelIdx0);
            float distanceRatio1 = ReadFOVMapPixel(uvX, uvZ, layerIdx1, channelIdx1);
            
            // Interpolate (matching shader line 108)
            float interpolationFactor = directionFactor - directionIdx0;
            float distanceRatio = distanceRatio0 * (1f - interpolationFactor) + distanceRatio1 * interpolationFactor;
            
            return distanceRatio;
        }
        
        // Cache for texture data to avoid repeated GetPixels calls
        private Color[][] cachedLayerPixels;
        private bool isPixelDataCached = false;
        
        /// <summary>
        /// Caches all pixel data from the FOV map for fast access
        /// </summary>
        private void CacheFOVMapPixels() {
            if (fovMapArray == null || isPixelDataCached) return;
            
            try {
                cachedLayerPixels = new Color[fovMapArray.depth][];
                
                for (int layer = 0; layer < fovMapArray.depth; layer++) {
                    cachedLayerPixels[layer] = fovMapArray.GetPixels(layer, 0); // mip level 0
                }
                
                isPixelDataCached = true;
                Debug.Log($"Cached FOV map pixels: {fovMapArray.depth} layers, {fovMapArray.width}x{fovMapArray.height} each");
            }
            catch (System.Exception e) {
                Debug.LogError($"Failed to cache FOV map pixels: {e.Message}");
                isPixelDataCached = false;
            }
        }
        
        /// <summary>
        /// Reads a pixel from the FOV map texture array
        /// </summary>
        /// <param name="uvX">X coordinate in UV space (0-1)</param>
        /// <param name="uvZ">Z coordinate in UV space (0-1)</param>
        /// <param name="layer">Layer index</param>
        /// <param name="channel">Channel index (0=R, 1=G, 2=B, 3=A)</param>
        /// <returns>Pixel value (0-1)</returns>
        private float ReadFOVMapPixel(float uvX, float uvZ, int layer, int channel) {
            if (fovMapArray == null) return 1f;
            
            // Cache pixels on first access
            if (!isPixelDataCached) {
                CacheFOVMapPixels();
            }
            
            if (!isPixelDataCached || cachedLayerPixels == null) {
                return 1f; // Failed to cache, return default
            }
            
            // Clamp layer to valid range
            layer = Mathf.Clamp(layer, 0, fovMapArray.depth - 1);
            channel = Mathf.Clamp(channel, 0, 3);
            
            // Convert UV to pixel coordinates
            int pixelX = Mathf.FloorToInt(uvX * fovMapArray.width);
            int pixelY = Mathf.FloorToInt(uvZ * fovMapArray.height);
            
            // Clamp to texture bounds
            pixelX = Mathf.Clamp(pixelX, 0, fovMapArray.width - 1);
            pixelY = Mathf.Clamp(pixelY, 0, fovMapArray.height - 1);
            
            // Calculate pixel index (row-major order)
            int pixelIndex = pixelY * fovMapArray.width + pixelX;
            
            try {
                Color[] layerPixels = cachedLayerPixels[layer];
                if (layerPixels == null || pixelIndex >= layerPixels.Length) {
                    return 1f;
                }
                
                Color pixel = layerPixels[pixelIndex];
                
                // Return the appropriate channel
                switch (channel) {
                    case 0: return pixel.r;
                    case 1: return pixel.g;
                    case 2: return pixel.b;
                    case 3: return pixel.a;
                    default: return 1f;
                }
            }
            catch (System.Exception e) {
                Debug.LogWarning($"Failed to read FOV map pixel at ({pixelX},{pixelY}) layer {layer}: {e.Message}");
                return 1f; // Default to maximum visibility
            }
        }
        
        /// <summary>
        /// Sets the FOV manager reference and updates the circle
        /// </summary>
        /// <param name="manager">FOVManager instance</param>
        public void SetFOVManager(FOVManager manager) {
            fovManager = manager;
            if (manager != null) {
                settings = manager.Settings;
                if (settings != null) {
                    fovMapArray = settings.FOVMapArray;
                    
                    // Update segments and max radius from FOV settings
                    if (fovMapArray != null) {
                        segments = 4 * fovMapArray.depth;
                        maxRadius = fovMapArray.mipMapBias;
                        
                        // Reset cache when FOV manager changes
                        isPixelDataCached = false;
                        cachedLayerPixels = null;
                        
                        Debug.Log($"FOVCircle3D updated: segments={segments}, maxRadius={maxRadius}");
                    }
                }
            }
            
            // Reset position tracking when FOV manager changes
            lastUpdatePosition = transform.position;
            hasInitialized = true;
            UpdateCircle();
        }
        
        /// <summary>
        /// Gets FOV data for all directions around the current position
        /// </summary>
        /// <param name="sampleCount">Number of directions to sample</param>
        /// <returns>Array of distance ratios for each direction</returns>
        public float[] GetFOVDataForAllDirections(int sampleCount = 16) {
            float[] fovData = new float[sampleCount];
            float angleStep = 360f / sampleCount;
            
            for (int i = 0; i < sampleCount; i++) {
                float angle = i * angleStep;
                fovData[i] = GetFOVDistanceRatio(transform.position, angle);
            }
            
            return fovData;
        }
        
        
        private void OnValidate() {
            if (Application.isPlaying) {
                UpdateCircle();
            }
        }
    }
}
