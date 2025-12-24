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
/// Fully-parallel FOV map generation using Unity Jobs and Burst compilation
///
/// This implementation uses parallel jobs for both ground detection and FOV raycasting,
/// providing maximum performance while maintaining algorithmic correctness.
/// 
/// STAGE 1: BATCHED GROUND HEIGHT DETECTION
/// - Uses Unity's RaycastCommand API for parallel ground detection
/// - Batches all ground detection raycasts for maximum performance
/// - One raycast per grid cell (FOVMapWidth × FOVMapHeight total rays)
/// 
/// STAGE 2: PARALLEL FOV RAYCASTING WITH JOBS
/// - GatherJob: Parallel raycast command generation using Burst compilation
/// - Physics: Main-thread batched raycasting using RaycastCommand.ScheduleBatch
/// - ConsumeJob: Parallel hit processing and state updates using Burst compilation
/// - Persistent native buffers allocated once and reused across waves
/// 
/// This approach provides maximum performance through parallelization while
/// maintaining the exact behavior of the original algorithm through careful
/// job dependency management and state synchronization.
/// </summary>
public sealed class FOVGeneratorBatchedJobs : IFOVGenerator
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

        // Create an array of FOV maps
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

    enum DirPhase : byte { InitSample, Sampling, BinarySearch, Done }

    // --- Packed, per-active-direction state (small; only for currently working dirs) ---
    struct ActiveDir
    {
        public int cell;                 // cell index
        public int dir;                  // 0..directionsPerSquare-1
        public DirPhase phase;           // InitSample/Sampling/BinarySearch/Done
        public int sampleIdx;            // current vertical sample index
        public int bsIter;               // binary-search iteration
        public bool obstacleHit;         // saw a hit previously
        public float lastHitAngleDeg;    // last hit vertical angle (deg)
        public float lastMissAngleDeg;   // last miss vertical angle (deg)
        public float maxSight;           // best XZ distance so far (clamped later)
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
        [ReadOnly] public float samplingAngle;
        [ReadOnly] public float anglePerSample;
        [ReadOnly] public float rayDistance;
        [ReadOnly] public int levelLayer;
        
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

            // Compute vertical angle
            float vDeg;
            if (ad.phase == DirPhase.BinarySearch)
            {
                vDeg = 0.5f * (ad.lastHitAngleDeg + ad.lastMissAngleDeg);
            }
            else // InitSample/Sampling
            {
                vDeg = -samplingAngle / 2f + ad.sampleIdx * anglePerSample;
                // Note: We cannot modify ad.phase here since activeDirs is ReadOnly
                // The ConsumeJob will handle the InitSample -> Sampling transition
            }

            // Build ray direction
            float3 h = baseDirVec[ad.dir];
            float t = math.tan(math.radians(vDeg));
            float3 dir3 = math.normalize(new float3(h.x, t, h.z));
            var origin = ground[ad.cell].centerPosition;

            // Create raycast command
            cmdBuffer[i] = new RaycastCommand
            {
                from = new Vector3(origin.x, origin.y, origin.z),
                direction = new Vector3(dir3.x, dir3.y, dir3.z),
                distance = rayDistance,
                queryParameters = new QueryParameters
                {
                    layerMask = levelLayer,
                    hitTriggers = QueryTriggerInteraction.Ignore
                }
            };
        }
    }

    [BurstCompile]
    struct ConsumeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<RaycastHit> hits;    // length = count
        [ReadOnly] public NativeArray<GroundHeightData> ground;
        [ReadOnly] public float samplingAngleHalf;
        [ReadOnly] public float anglePerSample;
        [ReadOnly] public float samplingRange;
        [ReadOnly] public float blockedRayAngleThreshold;
        [ReadOnly] public float blockingSurfaceAngleThreshold;
        [ReadOnly] public int   samplesPerDirection;
        [ReadOnly] public int   binarySearchCount;

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

            if (ad.phase == DirPhase.BinarySearch)
            {
                float searchAngle = 0.5f * (ad.lastHitAngleDeg + ad.lastMissAngleDeg);
                if (hasHit)
                {
                    float dx = center.x - hit.point.x;
                    float dz = center.z - hit.point.z;
                    float dist = math.min(math.sqrt(dx * dx + dz * dz), samplingRange);
                    if (dist > ad.maxSight) ad.maxSight = dist;
                    ad.lastHitAngleDeg = searchAngle;
                }
                else
                {
                    ad.lastMissAngleDeg = searchAngle;
                }

                ad.bsIter++;
                if (ad.bsIter >= binarySearchCount)
                {
                    ad.phase = DirPhase.Done;
                    ad.alive = false;
                    finished.Enqueue(i);
                    pool[i] = ad;
                    return;
                }
                pool[i] = ad;
                return;
            }

            // Sampling (InitSample treated as Sampling on first emit)
            float samplingAngle = -samplingAngleHalf + ad.sampleIdx * anglePerSample;
            
            // Handle InitSample -> Sampling transition
            if (ad.phase == DirPhase.InitSample)
            {
                ad.phase = DirPhase.Sampling;
            }

            // If phase == Sampling (this must be true)
            if (hasHit)
            {
                float dx = center.x - hit.point.x;
                float dz = center.z - hit.point.z;
                float dist = math.min(math.sqrt(dx * dx + dz * dz), samplingRange);
                if (dist > ad.maxSight) ad.maxSight = dist;

                // Use hit.normal directly (safe in jobs)
                float3 normal = hit.normal;
                float steep = math.degrees(math.acos(math.clamp(math.dot(normal, new float3(0, 1, 0)), -1f, 1f)));
                if (steep >= blockingSurfaceAngleThreshold && samplingAngle >= blockedRayAngleThreshold)
                {
                    ad.phase = DirPhase.Done;
                    ad.alive = false;
                    finished.Enqueue(i);
                    pool[i] = ad;
                    return;
                }

                ad.obstacleHit = true;
                ad.lastHitAngleDeg = samplingAngle;
            }
            else
            {
                // below eye-line miss => max sight
                if (ad.sampleIdx <= (samplesPerDirection + 2 - 1) / 2)
                {
                    ad.maxSight = samplingRange;
                    ad.phase = DirPhase.Done;
                    ad.alive = false;
                    finished.Enqueue(i);
                    pool[i] = ad;
                    return;
                }

                // transition to binary (hit then miss)
                if (ad.obstacleHit && !float.IsNaN(ad.lastHitAngleDeg))
                {
                    ad.lastMissAngleDeg = samplingAngle;
                    ad.phase = DirPhase.BinarySearch;
                    ad.bsIter = 0;
                    pool[i] = ad;
                    return;
                }
            }

            // advance sampling
            ad.sampleIdx++;
            if (ad.sampleIdx >= samplesPerDirection)
            {
                ad.phase = DirPhase.Done;
                ad.alive = false;
                finished.Enqueue(i);
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
        float anglePerSample    = g.samplingAngle / (g.samplesPerDirection - 1);
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
                            phase = DirPhase.InitSample,
                            sampleIdx = 0,
                            bsIter = 0,
                            obstacleHit = false,
                            lastHitAngleDeg  = float.NaN,
                            lastMissAngleDeg = float.NaN,
                            maxSight = 0f,
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
                     samplingAngle = g.samplingAngle,
                     anglePerSample = anglePerSample,
                     rayDistance = RAY_DISTANCE,
                     levelLayer = g.levelLayer,
                     cmdBuffer = cmdBuffer,
                 }.Schedule(activeDirs.Length, 64);

                 // 2) Physics
                 JobHandle raycastJobHandle = RaycastCommand.ScheduleBatch(cmdBuffer, hitResultBuffer, minCommandsPerJob: 8, gatherJobHandle);

                 // 3) Consume back into pool entries
                 finished.Clear();
                 JobHandle consumeJobHandle = new ConsumeJob {
                     hits = hitResultBuffer,
                     ground = groundNA,
                     samplingAngleHalf = g.samplingAngle * 0.5f,
                     anglePerSample = anglePerSample,
                     samplingRange = g.samplingRange,
                     blockedRayAngleThreshold = g.blockedRayAngleThreshold,
                     blockingSurfaceAngleThreshold = g.blockingSurfaceAngleThreshold,
                     samplesPerDirection = g.samplesPerDirection,
                     binarySearchCount = g.binarySearchCount,
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
} // End FOVGeneratorBatchedJobs
} // End namespace
