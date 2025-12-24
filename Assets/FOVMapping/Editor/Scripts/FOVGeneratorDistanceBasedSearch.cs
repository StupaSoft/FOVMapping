using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

using UnityEngine.Profiling;

namespace FOVMapping 
{
/// <summary>
/// Distance-based FOV map generation using Unity Jobs and Burst compilation
///
/// This implementation uses a horizontal distance-based binary search algorithm to find
/// the closest obstacle in each direction.
/// 
/// STAGE 1: BATCHED GROUND HEIGHT DETECTION
/// - Uses Unity's RaycastCommand API for parallel ground detection
/// - Batches all ground detection raycasts for maximum performance
/// - One raycast per grid cell (FOVMapWidth × FOVMapHeight total rays)
/// 
/// STAGE 2: DISTANCE-BASED FOV RAYCASTING WITH JOBS
/// - Two-phase raycasting cycle: Ground Detection → Obstacle Detection
/// - Ground Detection: Raycast downward to find ground position at test distance
/// - Obstacle Detection: Raycast eye-to-eye from cell center to target ground position
/// - Binary search on horizontal distance: start at max range, if blocked search for closest obstacle
/// - GatherJob: Parallel raycast command generation for both phases using Burst compilation
/// - Physics: Main-thread batched raycasting using RaycastCommand.ScheduleBatch
/// - ConsumeJob: Parallel hit processing and binary search state updates using Burst compilation
/// - Persistent native buffers allocated once and reused across waves
/// 
/// This approach provides maximum performance through parallelization while
/// using a more intuitive distance-based search that directly finds the closest
/// obstacle in each horizontal direction.
/// </summary>
public sealed class FOVGeneratorDistanceBasedSearch : IFOVGenerator
{
    public Color[][] Generate(FOVMapGenerationInfo generationInfo, Func<string, int, int, string, bool> progressAction)
    {
        return GenerateFOVMap_Wavefront(generationInfo, progressAction);
    }
    
    /// <summary>
    /// Ground height data for a grid cell
    /// </summary>
    public struct GroundHeightData
    {
        public Vector3 centerPosition;
        public float height;
        public bool hasGround;
    }

    /// <summary>
    /// Processes ground detection raycasts in batches using RaycastCommand
    /// </summary>
    public static GroundHeightData[] ProcessGroundRaycastsInBatches(FOVMapGenerationInfo generationInfo, Func<string, int, int, string, bool> progressAction)
    {
        const float MAX_HEIGHT = 5000.0f;
        int totalCells = generationInfo.FOVMapWidth * generationInfo.FOVMapHeight;
        int batchSize = Mathf.Min(generationInfo.maxBatchSize, totalCells);

        GroundHeightData[] groundData = new GroundHeightData[totalCells];

        float planeSizeX = generationInfo.plane.localScale.x;
        float planeSizeZ = generationInfo.plane.localScale.z;

        int totalBatches = (totalCells + batchSize - 1) / batchSize; // Ceiling division

        NativeArray<RaycastCommand> commandBuffer = new NativeArray<RaycastCommand>(batchSize, Allocator.TempJob);
        NativeArray<RaycastHit> resultBuffer = new NativeArray<RaycastHit>(batchSize, Allocator.TempJob);
        
        // Debug.Log($"ProcessGroundRaycastsInBatches: Raycast Count (Cell Count): {totalCells} Batch size: {batchSize}, Total batches: {totalBatches}");
        
        for (int startIndex = 0; startIndex < totalCells; startIndex += batchSize)
        {
            int currentBatchSize = Mathf.Min(batchSize, totalCells - startIndex);
            int currentBatch = startIndex / batchSize;

            // Update progress for ground detection
            int processedCells = Mathf.Min(startIndex + currentBatchSize, totalCells);
            if (progressAction.Invoke(FOVProgressStages.GroundDetection, processedCells, totalCells, FOVProgressStages.Cells)) return null;

            // Build commands for this batch
            for (int i = 0; i < currentBatchSize; ++i)
            {
                int globalIndex = startIndex + i;
                int squareZ = globalIndex / generationInfo.FOVMapWidth;
                int squareX = globalIndex % generationInfo.FOVMapWidth;

                // Position above the sampling point
                Vector3 rayOriginPosition =
                    generationInfo.plane.position +
                    ((squareZ + 0.5f) / generationInfo.FOVMapHeight) * planeSizeZ * generationInfo.plane.forward +
                    ((squareX + 0.5f) / generationInfo.FOVMapWidth) * planeSizeX * generationInfo.plane.right;
                rayOriginPosition.y = MAX_HEIGHT;

                commandBuffer[i] = new RaycastCommand
                {
                    from = rayOriginPosition,
                    direction = Vector3.down,
                    distance = 2 * MAX_HEIGHT,
                    queryParameters = new QueryParameters
                    {
                        layerMask = generationInfo.levelLayer,
                        hitTriggers = QueryTriggerInteraction.Ignore
                    }
                };
            }

            //Fill extras with empty commands
            if (currentBatchSize < commandBuffer.Length) {
                for (int i = currentBatchSize; i < commandBuffer.Length; ++i) {
                    commandBuffer[i] = new RaycastCommand();
                }
            }

            // Execute batch
            JobHandle handle = RaycastCommand.ScheduleBatch(commandBuffer, resultBuffer, 1, 1);
            
            // Process results
            handle.Complete(); // Await completion
            for (int i = 0; i < currentBatchSize; ++i)
            {
                int globalIndex = startIndex + i;
                RaycastHit hit = resultBuffer[i];

                if (hit.collider != null)
                {
                    Vector3 centerPosition = hit.point + generationInfo.eyeHeight * Vector3.up;
                    float height = hit.point.y - generationInfo.plane.position.y;

                    groundData[globalIndex] = new GroundHeightData
                    {
                        centerPosition = centerPosition,
                        height = height,
                        hasGround = true
                    };
                }
                else
                {
                    groundData[globalIndex] = new GroundHeightData
                    {
                        centerPosition = Vector3.zero,
                        height = 0,
                        hasGround = false
                    };
                }
            }
            
        }
        
        // Cleanup
        commandBuffer.Dispose();
        resultBuffer.Dispose();

        return groundData;
    }
    
    private static Color[][] GenerateFOVMap_Wavefront(FOVMapGenerationInfo generationInfo, Func<string, int, int, string, bool> progressAction) {
        // Basic checks
        bool checkPassed = generationInfo.CheckSettings();

        if (progressAction == null) {
            Debug.LogError("progressAction must be passed.");
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
        float anglePerSample = generationInfo.samplingAngle / (generationInfo.samplesPerDirection - 1);

        // Create an array of colors to store the FOVMap results
        Color[][] FOVMapTexels = Enumerable.Range(0, generationInfo.layerCount)
            .Select(_ => new Color[generationInfo.FOVMapWidth * generationInfo.FOVMapHeight]).ToArray();

        // STAGE 1: BATCHED GROUND HEIGHT DETECTION
        int totalCells = generationInfo.FOVMapWidth * generationInfo.FOVMapHeight;
        if (progressAction.Invoke(FOVProgressStages.GroundDetection, 0, totalCells, FOVProgressStages.Cells)) return null;

        GroundHeightData[] groundData = ProcessGroundRaycastsInBatches(generationInfo, progressAction);

        if (progressAction.Invoke(FOVProgressStages.GroundDetection, totalCells, totalCells, FOVProgressStages.Cells)) return null;

        // STAGE 2 & 3: PARALLEL FOV RAYCASTING WITH JOBS
        RunDirectionsWavefront(generationInfo, groundData, FOVMapTexels, progressAction);

        return FOVMapTexels;
    }

    enum DirPhase : byte { InitialTest, GroundDetect, ObstacleDetect, Done }

    // --- Packed, per-active-direction state (small; only for currently working dirs) ---
    struct ActiveDir
    {
        public int cell;                 // cell index
        public int dir;                  // 0..directionsPerSquare-1
        public DirPhase phase;           // InitialTest/GroundDetect/ObstacleDetect/Done
        public int bsIter;               // binary-search iteration
        public bool obstacleHit;         // saw a hit previously
        public float maxSight;           // best XZ distance so far (clamped later)
        public float minDist;            // binary search lower bound
        public float maxDist;            // binary search upper bound
        public float currentTestDistance; // distance being tested
        public Vector3 targetGroundPosition; // cached ground position after ground raycast
        public bool alive;               // false => slot reusable
    }
    
    
    struct CellCursor
    {
        public int nextDir;              // next direction to start for this cell
    }

    static Vector3 DirectionFromAngleDeg(float angleDeg)
    {
        float r = angleDeg * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(r), 0f, Mathf.Sin(r));
    }

    static float XZDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f; b.y = 0f;
        return Vector3.Distance(a, b);
    }

    static Vector3 GetPositionAtDistance(Vector3 cellCenter, Vector3 horizontalDir, float distance)
    {
        return cellCenter + horizontalDir * distance;
    }

    // Profiler Markers
    static readonly ProfilerMarker kWaveLoop    = new("FOV/WaveLoop");
    static readonly ProfilerMarker kTopUp      = new("FOV/TopUp");
    static readonly ProfilerMarker kComplete     = new("FOV/Complete");
    static readonly ProfilerMarker kConsumePost     = new("FOV/ConsumePost");

    [BurstCompile]
    struct GatherJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ActiveDir> activeDirs; // ReadOnly - no modifications
        [ReadOnly] public NativeArray<GroundHeightData> ground;
        [ReadOnly] public NativeArray<float3> baseDirVec;
        [ReadOnly] public float rayDistance;
        [ReadOnly] public int levelLayer;
        [ReadOnly] public float eyeHeight;
        
        public NativeArray<RaycastCommand> cmdBuffer; // Output

        // The purpose of a GatherJob is to fill cmdBuffer with raycast commands for each elt of activeDirs.
        public void Execute(int i)
        {
            var ad = activeDirs[i];
            if (!ad.alive || ad.phase == DirPhase.Done)
            {
                // Mark as invalid by setting a default command
                cmdBuffer[i] = new RaycastCommand();
                return;
            }

            if (ad.phase == DirPhase.GroundDetect)
            {
                // Ground Detection Raycast: Find ground position at currentTestDistance
                float3 h = baseDirVec[ad.dir];
                float3 cellCenter = ground[ad.cell].centerPosition;
                float3 targetXZ = new float3(cellCenter.x, 0, cellCenter.z) + h * ad.currentTestDistance;
                
                // Raycast downward from MAX_HEIGHT
                cmdBuffer[i] = new RaycastCommand
                {
                    from = new Vector3(targetXZ.x, 5000f, targetXZ.z),
                    direction = Vector3.down,
                    distance = 10000f,
                    queryParameters = new QueryParameters
                    {
                        layerMask = levelLayer,
                        hitTriggers = QueryTriggerInteraction.Ignore
                    }
                };
            }
            else if (ad.phase == DirPhase.ObstacleDetect)
            {
                // Obstacle Detection Raycast: Eye-to-eye from cell to target position
                float3 origin = ground[ad.cell].centerPosition;
                float3 target = ad.targetGroundPosition;
                float3 direction = math.normalize(target - origin);
                float distance = math.distance(origin, target);

                cmdBuffer[i] = new RaycastCommand
                {
                    from = new Vector3(origin.x, origin.y, origin.z),
                    direction = new Vector3(direction.x, direction.y, direction.z),
                    distance = distance,
                    queryParameters = new QueryParameters
                    {
                        layerMask = levelLayer,
                        hitTriggers = QueryTriggerInteraction.Ignore
                    }
                };
            }
            else
            {
                // InitialTest or other phases - no raycast needed
                cmdBuffer[i] = new RaycastCommand();
            }
        }
    }

    [BurstCompile]
    struct ConsumeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<RaycastHit> hits;    // length = count
        [ReadOnly] public NativeArray<GroundHeightData> ground;
        [ReadOnly] public float samplingRange;
        [ReadOnly] public int   binarySearchCount;
        [ReadOnly] public float eyeHeight;

        public NativeArray<ActiveDir> pool; // Main In/Out
        public NativeQueue<int>.ParallelWriter finished; // Out

        public void Execute(int i)
        {
            // Use job index directly since pool and command arrays are in sync
            var ad = pool[i];
            
            // Skip if not alive or already done
            if (!ad.alive || ad.phase == DirPhase.Done)
            {
                // If it's done but not marked as finished yet, mark it as finished
                if (ad.phase == DirPhase.Done && ad.alive)
                {
                    ad.alive = false;
                    pool[i] = ad;
                    finished.Enqueue(i);
                }
                return;
            }
            
            var hit = hits[i];
            var center = ground[ad.cell].centerPosition;

            // Check if hit occurred using distance (safer than accessing collider from job)
            bool hasHit = hit.distance > 0f && hit.distance < float.MaxValue;

            if (ad.phase == DirPhase.GroundDetect)
            {
                // After GroundDetect raycast: Store ground position and transition to ObstacleDetect
                if (hasHit)
                {
                    ad.targetGroundPosition = new Vector3(hit.point.x, hit.point.y + eyeHeight, hit.point.z);
                    ad.phase = DirPhase.ObstacleDetect;
                }
                else
                {
                    // No ground found at this distance - treat as blocked
                    ad.maxSight = 0f;
                    ad.phase = DirPhase.Done;
                    ad.alive = false;
                    finished.Enqueue(i);
                }
                pool[i] = ad;
                return;
            }

            if (ad.phase == DirPhase.ObstacleDetect)
            {
                // After ObstacleDetect raycast: Process hit/miss and update binary search
                if (hasHit)
                {
                    // Blocked - update binary search bounds
                    ad.maxDist = ad.currentTestDistance;
                    float dx = center.x - hit.point.x;
                    float dz = center.z - hit.point.z;
                    float dist = math.min(math.sqrt(dx * dx + dz * dz), samplingRange);
                    if (dist > ad.maxSight) ad.maxSight = dist;
                }
                else
                {
                    // Clear - update binary search bounds
                    ad.minDist = ad.currentTestDistance;
                    if (ad.currentTestDistance > ad.maxSight) ad.maxSight = ad.currentTestDistance;
                    
                    // If this was the initial test at max range, we're done (best case)
                    if (ad.bsIter == 0 && math.abs(ad.currentTestDistance - samplingRange) < 0.001f)
                    {
                        ad.phase = DirPhase.Done;
                        ad.alive = false;
                        finished.Enqueue(i);
                        pool[i] = ad;
                        return;
                    }
                }

                // Check binary search termination
                ad.bsIter++;
                if (ad.bsIter >= binarySearchCount)
                {
                    ad.phase = DirPhase.Done;
                    ad.alive = false;
                    finished.Enqueue(i);
                    pool[i] = ad;
                    return;
                }

                // Compute new test distance and continue
                ad.currentTestDistance = (ad.minDist + ad.maxDist) * 0.5f;
                ad.phase = DirPhase.GroundDetect;
                pool[i] = ad;
                return;
            }

            if (ad.phase == DirPhase.InitialTest)
            {
                // Initial test at max distance - transition to GroundDetect
                ad.phase = DirPhase.GroundDetect;
                pool[i] = ad;
                return;
            }

            pool[i] = ad;
        }
    }

    static void RunDirectionsWavefront(FOVMapGenerationInfo g, GroundHeightData[] ground, Color[][] FOVMapTexels, Func<string, int, int, string, bool> progressAction)
    {
        int cells  = g.CellCount;
        int directionsPerSquare = FOVMapGenerator.CHANNELS_PER_TEXEL * g.layerCount;
        float anglePerDirection = 360f / directionsPerSquare;
        const float RAY_DISTANCE = 1000f;

        
        // Precompute base horizontal vectors (unit)
        float planeRightYaw = Vector3.SignedAngle(g.plane.right, Vector3.right, Vector3.up);
        var baseDirVec = new Vector3[directionsPerSquare];
        for (int d = 0; d < directionsPerSquare; d++)
        {
            float yaw = planeRightYaw + d * anglePerDirection;
            baseDirVec[d] = DirectionFromAngleDeg(yaw); // unit-length XZ
        }
        
        // Native copies of base vectors and ground (persistent for entire function scope)
        var baseDirNA = new NativeArray<float3>(directionsPerSquare, Allocator.Persistent);
        for (int d = 0; d < directionsPerSquare; d++)
        {
            var v = baseDirVec[d];
            baseDirNA[d] = new float3(v.x, v.y, v.z);
        }
        var groundNA = new NativeArray<GroundHeightData>(ground.Length, Allocator.Persistent);
        for (int i = 0; i < ground.Length; i++) groundNA[i] = ground[i];
        
        
        // Per-cell cursors + ready queue (only cells with ground participate)
        var cursors = new CellCursor[cells];
        var readyCells = new Queue<int>(Mathf.Min(cells, 1 << 16));
        int totalDirs = 0;
        int cellsWithGround = 0;
        for (int c = 0; c < cells; c++)
        {
            if (!ground[c].hasGround) continue;

            cellsWithGround++;
            cursors[c] = new CellCursor { nextDir = 0 };
            readyCells.Enqueue(c);
            totalDirs += directionsPerSquare;
        }
        
        // Debug.Log($"RunDirectionsWavefront Prep: Cell Count: {cells}, ready cells: {readyCells.Count}. Cells with Ground: {cellsWithGround}. Total Directions with Ground: {totalDirs}");

        // Early out: no ground anywhere
        if (totalDirs == 0)
        {
            Debug.LogWarning($"RunDirectionsWavefront EARLY OUT -- No Directions recorded.");
            // fill white
            for (int cell = 0; cell < cells; cell++)
            for (int layer = 0; layer < g.layerCount; layer++)
                FOVMapTexels[layer][cell] = Color.white;
            baseDirNA.Dispose();
            groundNA.Dispose();
            return;
        }

        // Active pool (bounded) + free list to reuse slots
        int batchSize = Mathf.Max(1, g.maxBatchSize);
        batchSize = Mathf.Min(totalDirs, batchSize);
        var activeDirs = new NativeList<ActiveDir>(batchSize, Allocator.Persistent);
        var freeDirIndicies = new NativeList<int>(batchSize, Allocator.Persistent);

        int FreeCount() => freeDirIndicies.Length;
        int PopFree() { int last = freeDirIndicies[freeDirIndicies.Length - 1]; freeDirIndicies.RemoveAt(freeDirIndicies.Length - 1); return last; }
        void PushFree(int x) { freeDirIndicies.Add(x); }

        // Prealloc physics buffers (persistent)
        var cmdBuffer       = new NativeArray<RaycastCommand>(batchSize, Allocator.Persistent);
        var hitResultBuffer = new NativeArray<RaycastHit>(batchSize, Allocator.Persistent);

        // Per-wave queues (persistent; cleared each wave)
        var finished = new NativeQueue<int>(Allocator.Persistent);

        int LiveCount() => activeDirs.Length - FreeCount();
        
        // Bootstrap: populate initial pool
        TopUpPool();
        // Debug.Log($"Bootstrap: ReadyCells: {readyCells.Count}, TotalDirs: {totalDirs} Pool size: {activeDirs.Length}, Live: {LiveCount()}, Free: {FreeCount()}");

        // Top-up: keep pool saturated using the readyCells queue
        void TopUpPool()
        {
            using (kTopUp.Auto())
            {
                // Debug.Log($"TopUpPool:" +
                //           $"CellQ {readyCells.Count} | Live {LiveCount()} / Cap {activeDirCap} | " +
                //           $"ActiveListCount {activeDirList.Count} | Free {activeDirFreeSlotIndicies.Count}");
                
                 // how many new actives can we admit right now?
                int freeLiveBudget = batchSize - LiveCount();
                if (freeLiveBudget <= 0 || readyCells.Count == 0) return;

                // cap by available free slots + append room to avoid extra checks in the loop
                int appendRoom = Mathf.Max(0, batchSize - activeDirs.Length);
                int freeSlots  = FreeCount();
                int maxCreatable = Mathf.Min(freeLiveBudget, freeSlots + appendRoom);

                // optional: don't overfill—emit at most batchSize per wave for stability
                int target = Mathf.Min(maxCreatable, batchSize);

                for (int i = 0; i < target && readyCells.Count > 0; )
                {
                    int cell = readyCells.Dequeue();
                    var cur  = cursors[cell];

                    while (cur.nextDir < directionsPerSquare && i < target)
                    {
                        // choose slot: reuse first, append if needed
                        int slot;
                        if (FreeCount() > 0) { slot = PopFree(); }
                        else { slot = activeDirs.Length; activeDirs.Add(default); }

                        // init active
                        var ad = new ActiveDir {
                            cell = cell,
                            dir  = cur.nextDir++,
                            phase = DirPhase.InitialTest,
                            currentTestDistance = g.samplingRange, // start at max
                            minDist = 0f,
                            maxDist = g.samplingRange,
                            bsIter = 0,
                            maxSight = 0f,
                            targetGroundPosition = Vector3.zero,
                            alive = true
                        };
                        activeDirs[slot] = ad;
                        i++;
                    }

                    // if the cell still has work, requeue it (one dir per dequeue keeps fairness)
                    if (cur.nextDir < directionsPerSquare) {
                        readyCells.Enqueue(cell);
                    }
                    cursors[cell] = cur;
                }
            }
        }

        int doneCount = 0;
        int wave = 0;

        while (doneCount < totalDirs)
        {
            using (kWaveLoop.Auto()) {
                // Debug.Log($"Wave{wave} TopUpPool Begin: Done {doneCount}/{totalDirs} | " +
                //           $"CellQ {readyCells.Count} | Live {LiveCount()} / Cap {batchSize} | " +
                //           $"Pool: {activeDirs.Length} | Free {FreeCount()}");

                // Keep enough actives to fill a batch (or as many as possible)
                TopUpPool();

                 // 1) Gather raycast commands (parallel)
                 JobHandle gatherJobHandle = new GatherJob {
                     activeDirs = activeDirs,
                     ground = groundNA,
                     baseDirVec = baseDirNA,
                     rayDistance = RAY_DISTANCE,
                     levelLayer = g.levelLayer,
                     eyeHeight = g.eyeHeight,
                     cmdBuffer = cmdBuffer,
                 }.Schedule(activeDirs.Length, 64);

                 // 2) Physics
                 JobHandle raycastJobHandle = RaycastCommand.ScheduleBatch(cmdBuffer, hitResultBuffer, minCommandsPerJob: 8, gatherJobHandle);

                 // 3) Consume back into pool entries
                 finished.Clear();
                 JobHandle consumeJobHandle = new ConsumeJob {
                     hits = hitResultBuffer,
                     ground = groundNA,
                     samplingRange = g.samplingRange,
                     binarySearchCount = g.binarySearchCount,
                     eyeHeight = g.eyeHeight,
                     pool = activeDirs,
                     finished = finished.AsParallelWriter()
                 }.Schedule(activeDirs.Length, 64, raycastJobHandle);

                using (kComplete.Auto()) {
                    consumeJobHandle.Complete();
                }

                using (kConsumePost.Auto()) {
                    // Drain finished on main: write channel, recycle slot, requeue cell
                    int finishedCount = 0;
                    while (finished.TryDequeue(out int pFin)) {
                        if (pFin < 0 || pFin >= activeDirs.Length) continue;

                        var ad = activeDirs[pFin];
                        WriteChannel(g, FOVMapTexels, ad.cell, ad.dir, ad.maxSight, g.samplingRange);
                        ad.alive = false;
                        ad.phase = DirPhase.Done;
                        activeDirs[pFin] = ad;
                        PushFree(pFin);

                        var cur = cursors[ad.cell];
                        if (cur.nextDir < directionsPerSquare) readyCells.Enqueue(ad.cell);

                        doneCount++;
                        finishedCount++;
                    }
                }

                 // Progress reporting for FOV raycasting
                 if (progressAction.Invoke(FOVProgressStages.FOVRaycasting, doneCount, totalDirs, FOVProgressStages.Directions)) {
                     Debug.LogWarning($"FOV Generation cancelled at Wave {wave} ({doneCount}/{totalDirs} directions completed)");
                     break;
                 }

                wave++;
        }
        }
        
        // Cells without ground → white (if any such cells exist)
        for (int cell = 0; cell < cells; cell++)
        {
            if (ground[cell].hasGround) continue;
            for (int layer = 0; layer < g.layerCount; layer++)
                FOVMapTexels[layer][cell] = Color.white;
        }

        // Cleanup
        cmdBuffer.Dispose();
        hitResultBuffer.Dispose();
        finished.Dispose();
        baseDirNA.Dispose();
        groundNA.Dispose();
        activeDirs.Dispose();
        freeDirIndicies.Dispose();
        
        float avgDirectionsPerWave = totalDirs > 0 ? (float)doneCount / wave : 0f;
        Debug.Log($"FOV Generation Complete: {wave} waves, {doneCount}/{totalDirs} directions processed ({cellsWithGround} cells with ground, ~{avgDirectionsPerWave:F1} dirs/wave)");
    }

    static void WriteChannel(
        FOVMapGenerationInfo g, Color[][] texels, int cell, int directionIdx,
        float maxSight, float samplingRange)
    {
        int layerIdx   = directionIdx / FOVMapGenerator.CHANNELS_PER_TEXEL;
        int channelIdx = directionIdx % FOVMapGenerator.CHANNELS_PER_TEXEL;

        float ratio = (maxSight == 0f) ? 1f : Mathf.Clamp01(maxSight / samplingRange);
        var c = texels[layerIdx][cell];
        c[channelIdx] = ratio;
        texels[layerIdx][cell] = c;
    }
} // End FOVGeneratorDistanceBasedSearch
} // End namespace
