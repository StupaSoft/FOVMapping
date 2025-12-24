using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

using UnityEngine.Profiling;


namespace FOVMapping 
{
/// <summary>
/// Semi-batched FOV map generation strategy
///
/// This implementation combines the benefits of batching for ground detection
/// with the proven reliability of single-threaded direction sampling and binary search.
/// 
/// STAGE 1: BATCHED GROUND HEIGHT DETECTION
/// - Uses Unity's RaycastCommand API for parallel ground detection
/// - Batches all ground detection raycasts for maximum performance
/// - One raycast per grid cell (FOVMapWidth × FOVMapHeight total rays)
/// 
/// STAGE 2: SINGLE-THREADED DIRECTION SAMPLING
/// - Uses the proven single-threaded approach for direction sampling
/// - Ensures exact compatibility with the original algorithm
/// - Processes each cell-direction combination sequentially
/// 
/// STAGE 3: SINGLE-THREADED BINARY SEARCH
/// - Uses the proven single-threaded binary search for edge refinement
/// - Ensures exact compatibility with the original algorithm
/// - Processes each binary search sequentially
/// 
/// This approach provides significant performance improvement for ground detection
/// while maintaining the exact behavior of the original algorithm for the complex
/// direction sampling and binary search phases.
/// </summary>
public sealed class FOVGeneratorBatchedRaycasts : IFOVGenerator
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
        
        Debug.Log($"ProcessGroundRaycastsInBatches: Raycast Count (Cell Count): {totalCells} Batch size: {batchSize}, Total batches: {totalBatches}");
        
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
            
            handle.Complete(); // Block main thread to await completion
            
            // Process results
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

        // STAGE 2 & 3: SINGLE-THREADED DIRECTION SAMPLING AND BINARY SEARCH
        Debug.Log("FOVGeneratorBatchedRaycasts: Starting Stage 2 & 3 - Single-threaded Direction Sampling and Binary Search");

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
    static readonly ProfilerMarker kGather      = new("FOV/Gather");
    static readonly ProfilerMarker kRaycast    = new("FOV/Raycast");
    static readonly ProfilerMarker kConsume     = new("FOV/Consume");

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
            return;
        }

        // Active pool (bounded) + free list to reuse slots
        int batchSize = Mathf.Max(1, g.maxBatchSize);
        int activeDirCap   = Mathf.Clamp(batchSize * 8, batchSize, Mathf.Min(totalDirs, batchSize * 32)); // headroom
        var activeDirList             = new List<ActiveDir>(activeDirCap);
        var activeDirFreeSlotIndicies = new Stack<int>(activeDirCap);

        // Prealloc physics buffers
        var cmdBuffer       = new NativeArray<RaycastCommand>(batchSize, Allocator.TempJob);
        var hitResultBuffer = new NativeArray<RaycastHit>(batchSize, Allocator.TempJob);
        var mapBackPoolIdx  = new NativeArray<int>(batchSize, Allocator.TempJob); // pool index per emitted ray

        int LiveCount() => activeDirList.Count - activeDirFreeSlotIndicies.Count;

        // Helper: start new directions for a cell up to capacity
        const int K_PER_CELL = 2; // max concurrent directions per cell (tune)
        void TryStartForCell(int cell)
        {
            ref var cur = ref cursors[cell];
            while ((activeDirFreeSlotIndicies.Count > 0 || activeDirList.Count < activeDirCap) && cur.nextDir < directionsPerSquare)
            {
                int dir = cur.nextDir++;
                int slot = activeDirFreeSlotIndicies.Count > 0 ? activeDirFreeSlotIndicies.Pop() : activeDirList.Count;
                if (slot == activeDirList.Count) activeDirList.Add(default);
                activeDirList[slot] = new ActiveDir
                {
                    cell = cell,
                    dir = dir,
                    phase = DirPhase.InitSample,
                    sampleIdx = 0,
                    bsIter = 0,
                    obstacleHit = false,
                    lastHitAngleDeg = float.NaN,
                    lastMissAngleDeg = float.NaN,
                    maxSight = 0f,
                    alive = true
                };
            }
        }

        // Top-up: keep pool saturated using the readyCells queue
        void TopUpPool()
        {
            using (kTopUp.Auto())
            {
                // Debug.Log($"TopUpPool:" +
                //           $"CellQ {readyCells.Count} | Live {LiveCount()} / Cap {activeDirCap} | " +
                //           $"ActiveListCount {activeDirList.Count} | Free {activeDirFreeSlotIndicies.Count}");
                
                 // how many new actives can we admit right now?
                int freeLiveBudget = activeDirCap - LiveCount();
                if (freeLiveBudget <= 0 || readyCells.Count == 0) return;

                // cap by available free slots + append room to avoid extra checks in the loop
                int appendRoom = Mathf.Max(0, activeDirCap - activeDirList.Count);
                int freeSlots  = activeDirFreeSlotIndicies.Count;
                int maxCreatable = Mathf.Min(freeLiveBudget, freeSlots + appendRoom);

                // optional: don’t overfill—emit at most batchSize per wave for stability
                int target = Mathf.Min(maxCreatable, batchSize);

                for (int i = 0; i < target && readyCells.Count > 0; )
                {
                    int cell = readyCells.Dequeue();
                    var cur  = cursors[cell];

                    while (cur.nextDir < directionsPerSquare && i < target)
                    {
                        // choose slot: reuse first, append if needed
                        int slot;
                        if (activeDirFreeSlotIndicies.Count > 0)
                        {
                            slot = activeDirFreeSlotIndicies.Pop();
                        }
                        else
                        {
                            // guaranteed by maxCreatable that we have append room
                            slot = activeDirList.Count;
                            activeDirList.Add(default);
                        }

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
                        activeDirList[slot] = ad;
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
            kWaveLoop.Begin();

            // Keep enough actives to fill a batch (or as many as possible)
            TopUpPool();

            // 1) Gather from pool: at most one ray per active direction this wave
            int thisBatchCount = 0;
            using (kGather.Auto())
            {
                // Debug.Log($"Wave{wave} GATHER Begin: Done {doneCount}/{totalDirs} | " +
                //           $"CellQ {readyCells.Count} | Live {LiveCount()} / Cap {activeDirCap} | " +
                //           $"ActiveListCount {activeDirList.Count} | Free {activeDirFreeSlotIndicies.Count}");

                for (int p = 0; p < activeDirList.Count && thisBatchCount < batchSize; p++)
                {
                    var ad = activeDirList[p];
                    if (!ad.alive || ad.phase == DirPhase.Done) continue;

                    // choose vertical angle
                    float vDeg;
                    if (ad.phase == DirPhase.BinarySearch)
                    {
                        vDeg = 0.5f * (ad.lastHitAngleDeg + ad.lastMissAngleDeg);
                    }
                    else // InitSample/Sampling
                    {
                        vDeg = -g.samplingAngle / 2f + ad.sampleIdx * anglePerSample;
                        ad.phase = DirPhase.Sampling; // ensure sampling after first emission
                        activeDirList[p] = ad; // write back phase change
                    }

                    Vector3 rayDir = baseDirVec[ad.dir];
                    rayDir.y = Mathf.Tan(vDeg * Mathf.Deg2Rad);
                    rayDir.Normalize(); // required by RaycastCommand

                    var origin = ground[ad.cell].centerPosition;

                    cmdBuffer[thisBatchCount] = new RaycastCommand
                    {
                        from = origin,
                        direction = rayDir,
                        distance = RAY_DISTANCE,
                        queryParameters = new QueryParameters
                        {
                            layerMask = g.levelLayer,
                            hitTriggers = QueryTriggerInteraction.Ignore
                        }
                    };
                    
                    mapBackPoolIdx[thisBatchCount] = p; // map back to pool entry
                    thisBatchCount++;
                }

                // If count == 0 here, we ran out of actives before finishing;
                // try to top up again (e.g., when K_PER_CELL is small)
                if (thisBatchCount == 0)
                {
                    TopUpPool();
                    for (int p = 0; p < activeDirList.Count && thisBatchCount < batchSize; p++)
                    {
                        var ad = activeDirList[p];
                        if (!ad.alive || ad.phase == DirPhase.Done) continue;
                        float vDeg = (ad.phase == DirPhase.BinarySearch)
                            ? 0.5f * (ad.lastHitAngleDeg + ad.lastMissAngleDeg)
                            : (-g.samplingAngle / 2f + ad.sampleIdx * anglePerSample);

                        Vector3 rayDir = baseDirVec[ad.dir];
                        rayDir.y = Mathf.Tan(vDeg * Mathf.Deg2Rad);
                        rayDir.Normalize();

                        var origin = ground[ad.cell].centerPosition;

                        cmdBuffer[thisBatchCount] = new RaycastCommand
                        {
                            from = origin,
                            direction = rayDir,
                            distance = RAY_DISTANCE,
                            queryParameters = new QueryParameters
                            {
                                layerMask = g.levelLayer,
                                hitTriggers = QueryTriggerInteraction.Ignore
                            }
                        };

                        mapBackPoolIdx[thisBatchCount] = p;
                        // ensure sampling tag after first emission
                        if (ad.phase == DirPhase.InitSample) { ad.phase = DirPhase.Sampling; activeDirList[p] = ad; }
                        thisBatchCount++;
                    }
                }
            }

            // Nothing to cast? Finalize blanks and break.
            if (thisBatchCount == 0)
            {
                // fill white for any cells without ground (already handled by not scheduling them)
                // write remaining active entries (shouldn't happen but safe-guard)
                int notDoneCount = 0;
                for (int p = 0; p < activeDirList.Count; p++)
                {
                    var ad = activeDirList[p];
                    if (!ad.alive || ad.phase == DirPhase.Done) continue;
                    WriteChannel(g, FOVMapTexels, ad.cell, ad.dir, ad.maxSight, g.samplingRange);
                    if (ad.phase != DirPhase.Done) {
                        notDoneCount++;
                        ad.phase = DirPhase.Done;
                    }
                    ad.alive = false; 
                    activeDirFreeSlotIndicies.Push(p);
                    activeDirList[p] = ad;
                    doneCount++;
                }
                Debug.LogError($"Wave{wave} GATHER EXIT (Starved Work QUEUE). thisBatchCount == 0. NotDone? {notDoneCount}. ActiveList: {activeDirList.Count} / {activeDirCap}. FreeSlotCount: {activeDirFreeSlotIndicies.Count}");
                kWaveLoop.End();
                break;
            }

            // 2) Physics
            using (kRaycast.Auto())
            {
                // Debug.Log($"Wave{wave} RAYCAST Dispatch: Active Dirs: {activeDirList.Count} This batch Size: {thisBatchCount} / {batchSize} ({(100*(float)(thisBatchCount / batchSize))}%) fill rate");
                
                // zero-pad the tail to speed up unused batch raycast slots
                for (int i = thisBatchCount; i < cmdBuffer.Length; i++) cmdBuffer[i] = new RaycastCommand();

                var handle = RaycastCommand.ScheduleBatch(cmdBuffer, hitResultBuffer, minCommandsPerJob: 8);
                handle.Complete();
            }

            // 3) Consume back into pool entries
            using (kConsume.Auto())
            {
                for (int i = 0; i < thisBatchCount; i++)
                {
                    int p = mapBackPoolIdx[i];
                    var ad = activeDirList[p];
                    if (!ad.alive) continue; // might have been finalized earlier

                    Vector3 center = ground[ad.cell].centerPosition;
                    RaycastHit hit = hitResultBuffer[i];

                    if (ad.phase == DirPhase.Sampling || ad.phase == DirPhase.InitSample)
                    {
                        float samplingAngle = -g.samplingAngle / 2f + ad.sampleIdx * anglePerSample;

                        if (hit.collider != null)
                        {
                            ad.obstacleHit = true;
                            float blocked = XZDistance(center, hit.point);
                            if (blocked > ad.maxSight) ad.maxSight = Mathf.Clamp(blocked, 0f, g.samplingRange);

                            // steep surface -> finalize
                            if (Vector3.Angle(hit.normal, Vector3.up) >= g.blockingSurfaceAngleThreshold &&
                                samplingAngle >= g.blockedRayAngleThreshold)
                            {
                                WriteChannel(g, FOVMapTexels, ad.cell, ad.dir, ad.maxSight, g.samplingRange);
                                ad.alive = false; ad.phase = DirPhase.Done;
                                activeDirList[p] = ad;
                                activeDirFreeSlotIndicies.Push(p);
                                doneCount++;
                                continue;
                            }

                            ad.lastHitAngleDeg = samplingAngle;
                        }
                        else
                        {
                            // below eye-line miss => max sight
                            if (ad.sampleIdx <= (g.samplesPerDirection + 2 - 1) / 2)
                            {
                                ad.maxSight = g.samplingRange;
                                WriteChannel(g, FOVMapTexels, ad.cell, ad.dir, ad.maxSight, g.samplingRange);
                                ad.alive = false; ad.phase = DirPhase.Done;
                                activeDirList[p] = ad;
                                activeDirFreeSlotIndicies.Push(p);
                                doneCount++;
                                continue;
                            }

                            // transition to binary (hit then miss)
                            if (ad.obstacleHit && !float.IsNaN(ad.lastHitAngleDeg))
                            {
                                ad.lastMissAngleDeg = samplingAngle;
                                ad.phase = DirPhase.BinarySearch;
                                ad.bsIter = 0;
                                activeDirList[p] = ad;
                                continue;
                            }
                        }

                        // advance sampling
                        ad.sampleIdx++;
                        if (ad.sampleIdx >= g.samplesPerDirection)
                        {
                            WriteChannel(g, FOVMapTexels, ad.cell, ad.dir, ad.maxSight, g.samplingRange);
                            ad.alive = false; ad.phase = DirPhase.Done;
                            activeDirList[p] = ad;
                            activeDirFreeSlotIndicies.Push(p);
                            doneCount++;
                        }
                        else
                        {
                            activeDirList[p] = ad;
                        }
                    }
                    else // BinarySearch
                    {
                        float searchAngle = 0.5f * (ad.lastHitAngleDeg + ad.lastMissAngleDeg);

                        if (hit.collider != null)
                        {
                            ad.lastHitAngleDeg = searchAngle;
                            float dist = XZDistance(center, hit.point);
                            if (dist > ad.maxSight) ad.maxSight = Mathf.Clamp(dist, 0f, g.samplingRange);
                        }
                        else
                        {
                            ad.lastMissAngleDeg = searchAngle;
                        }

                        ad.bsIter++;
                        if (ad.bsIter >= g.binarySearchCount)
                        {
                            WriteChannel(g, FOVMapTexels, ad.cell, ad.dir, ad.maxSight, g.samplingRange);
                            ad.alive = false; ad.phase = DirPhase.Done;
                            activeDirList[p] = ad;
                            activeDirFreeSlotIndicies.Push(p);
                            doneCount++;
                        }
                        else
                        {
                            activeDirList[p] = ad;
                        }
                    }
                }
            }

            // Progress reporting for FOV raycasting
            if (progressAction.Invoke(FOVProgressStages.FOVRaycasting, doneCount, totalDirs, FOVProgressStages.Directions))
            {
                Debug.LogWarning($"FOV Generation cancelled at Wave {wave} ({doneCount}/{totalDirs} directions completed)");
                kWaveLoop.End();
                break;
            }

            wave++;
            kWaveLoop.End();
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
        mapBackPoolIdx.Dispose();
        
        Debug.Log($"RunDirectionsWavefront Finished Loop: Cells: {cells} | WaveCount: {wave} | DoneCount: {doneCount} | TotalDirs: {totalDirs} | CellsWithGround: {cellsWithGround}");
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
} // End FOVGeneratorBatchedRaycasts
} // End namespace
