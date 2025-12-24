using UnityEngine;
using UnityEditor;
using FOVMapping;

namespace FOVMapping
{
/// <summary>
/// Editor tool for debugging FOV map generation for a single cell
/// </summary>
public class FOVMapDebugger : EditorWindow
{
    [MenuItem("Window/FOV Mapping/Debug Single Cell")]
    static void ShowWindow()
    {
        var window = GetWindow<FOVMapDebugger>();
        window.titleContent = new GUIContent("FOV Cell Debugger");
        window.Show();
    }

    // References
    private FOVManager fovManager;
    private FOVBakeSettings settings;
    
    // Cell selection
    private int cellX = 0;
    private int cellY = 0;
    private int directionIndex = 0;
    
    // Visualization settings
    private bool showRaycasts = true;
    private bool showBinarySearch = true;
    private bool showObstacles = true;
    private Color rayHitColor = Color.red;
    private Color rayMissColor = Color.green;
    private Color binarySearchColor = Color.yellow;
    
    // Debug info
    private Vector3 cellWorldPosition;
    private float calculatedDistance = 0f;
    private float distanceRatio = 0f;
    
    void OnGUI()
    {
        GUILayout.Label("FOV Map Cell Debugger", EditorStyles.boldLabel);
        EditorGUILayout.Space();
        
        // References
        fovManager = (FOVManager)EditorGUILayout.ObjectField("FOV Manager", fovManager, typeof(FOVManager), true);
        
        if (fovManager != null)
        {
            settings = fovManager.Settings;
        }
        
        if (settings == null)
        {
            EditorGUILayout.HelpBox("Please assign a FOV Manager with valid settings.", MessageType.Warning);
            return;
        }
        
        EditorGUILayout.Space();
        GUILayout.Label("Cell Selection", EditorStyles.boldLabel);
        
        // Cell coordinates
        cellX = EditorGUILayout.IntSlider("Cell X", cellX, 0, settings.FOVMapWidth - 1);
        cellY = EditorGUILayout.IntSlider("Cell Y", cellY, 0, settings.FOVMapHeight - 1);
        
        // Direction
        int totalDirections = 4 * settings.layerCount;
        directionIndex = EditorGUILayout.IntSlider("Direction Index", directionIndex, 0, totalDirections - 1);
        
        float anglePerDirection = 360f / totalDirections;
        float directionAngle = directionIndex * anglePerDirection;
        EditorGUILayout.LabelField("Direction Angle", $"{directionAngle:F2}°");
        
        EditorGUILayout.Space();
        GUILayout.Label("Visualization", EditorStyles.boldLabel);
        
        showRaycasts = EditorGUILayout.Toggle("Show Raycasts", showRaycasts);
        showBinarySearch = EditorGUILayout.Toggle("Show Binary Search", showBinarySearch);
        showObstacles = EditorGUILayout.Toggle("Show Obstacles", showObstacles);
        
        rayHitColor = EditorGUILayout.ColorField("Ray Hit Color", rayHitColor);
        rayMissColor = EditorGUILayout.ColorField("Ray Miss Color", rayMissColor);
        binarySearchColor = EditorGUILayout.ColorField("Binary Search Color", binarySearchColor);
        
        EditorGUILayout.Space();
        
        if (GUILayout.Button("Calculate & Visualize Cell", GUILayout.Height(30)))
        {
            CalculateCell();
            SceneView.RepaintAll();
        }
        
        EditorGUILayout.Space();
        GUILayout.Label("Debug Info", EditorStyles.boldLabel);
        
        EditorGUILayout.LabelField("Cell World Position", cellWorldPosition.ToString());
        EditorGUILayout.LabelField("Calculated Distance", $"{calculatedDistance:F2} m");
        EditorGUILayout.LabelField("Distance Ratio", $"{distanceRatio:F3}");
        EditorGUILayout.LabelField("Layer", $"{directionIndex / 4}");
        EditorGUILayout.LabelField("Channel", $"{directionIndex % 4}");
        
        EditorGUILayout.Space();
        
        if (GUILayout.Button("Focus Scene View on Cell"))
        {
            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.LookAt(cellWorldPosition, Quaternion.identity, 50f);
            }
        }
    }
    
    private void CalculateCell()
    {
        if (fovManager == null || settings == null) return;
        
        Transform plane = fovManager.transform;
        float planeSizeX = plane.localScale.x;
        float planeSizeZ = plane.localScale.z;
        
        float squareSizeX = planeSizeX / settings.FOVMapWidth;
        float squareSizeZ = planeSizeZ / settings.FOVMapHeight;
        
        // Calculate cell world position
        cellWorldPosition = plane.position +
            ((cellY + 0.5f) / settings.FOVMapHeight) * planeSizeZ * plane.forward +
            ((cellX + 0.5f) / settings.FOVMapWidth) * planeSizeX * plane.right;
        
        // Cast ray down to find ground
        Vector3 rayOriginPosition = cellWorldPosition;
        rayOriginPosition.y = 5000f;
        
        RaycastHit hitLevel;
        if (!Physics.Raycast(rayOriginPosition, Vector3.down, out hitLevel, 10000f, settings.levelLayer))
        {
            Debug.LogWarning("No level found at cell position!");
            return;
        }
        
        Vector3 centerPosition = hitLevel.point + settings.eyeHeight * Vector3.up;
        
        // Calculate direction
        int totalDirections = 4 * settings.layerCount;
        float anglePerDirection = 360f / totalDirections;
        float angleToward = Vector3.SignedAngle(plane.right, Vector3.right, Vector3.up) + directionIndex * anglePerDirection;
        
        Vector3 samplingDirection = new Vector3(
            Mathf.Cos(angleToward * Mathf.Deg2Rad), 
            0f, 
            Mathf.Sin(angleToward * Mathf.Deg2Rad)
        );
        
        // Sample in this direction
        float maxSight = 0f;
        float anglePerSample = settings.samplingAngle / (settings.samplesPerDirection - 1);
        
        for (int samplingIdx = 0; samplingIdx < settings.samplesPerDirection; samplingIdx++)
        {
            float samplingAngle = -settings.samplingAngle / 2f + samplingIdx * anglePerSample;
            
            Vector3 samplingLine = samplingDirection;
            samplingLine.y = samplingLine.magnitude * Mathf.Tan(samplingAngle * Mathf.Deg2Rad);
            
            RaycastHit hitBlocked;
            if (Physics.Raycast(centerPosition, samplingLine, out hitBlocked, 1000f, settings.levelLayer))
            {
                float blockedDistance = Vector3.Distance(
                    new Vector3(centerPosition.x, 0, centerPosition.z),
                    new Vector3(hitBlocked.point.x, 0, hitBlocked.point.z)
                );
                
                if (blockedDistance > maxSight)
                {
                    maxSight = Mathf.Clamp(blockedDistance, 0f, settings.samplingRange);
                }
                
                // Visualize
                if (showRaycasts)
                {
                    Debug.DrawLine(centerPosition, hitBlocked.point, rayHitColor, 5f);
                }
                
                if (showObstacles)
                {
                    Debug.DrawRay(hitBlocked.point, hitBlocked.normal * 2f, Color.blue, 5f);
                }
                
                // Check if vertical surface (stop sampling)
                if (Vector3.Angle(hitBlocked.normal, Vector3.up) >= settings.blockingSurfaceAngleThreshold 
                    && samplingAngle >= settings.blockedRayAngleThreshold)
                {
                    break;
                }
            }
            else
            {
                if (samplingIdx <= (settings.samplesPerDirection + 2 - 1) / 2)
                {
                    maxSight = settings.samplingRange;
                }
                
                if (showRaycasts)
                {
                    Debug.DrawRay(centerPosition, samplingLine * settings.samplingRange, rayMissColor, 5f);
                }
            }
        }
        
        calculatedDistance = maxSight;
        distanceRatio = maxSight == 0f ? 1f : maxSight / settings.samplingRange;
        
        // Draw summary visualization
        if (showRaycasts)
        {
            // Draw center position
            Debug.DrawRay(centerPosition, Vector3.up * 5f, Color.cyan, 5f);
            
            // Draw max sight circle segment
            Vector3 sightEnd = centerPosition + samplingDirection * maxSight;
            Debug.DrawLine(centerPosition, sightEnd, Color.magenta, 5f);
        }
    }
    
    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
    }
    
    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
    }
    
    private void OnSceneGUI(SceneView sceneView)
    {
        if (fovManager == null || settings == null) return;
        
        // Draw cell bounds
        Transform plane = fovManager.transform;
        float planeSizeX = plane.localScale.x;
        float planeSizeZ = plane.localScale.z;
        
        float squareSizeX = planeSizeX / settings.FOVMapWidth;
        float squareSizeZ = planeSizeZ / settings.FOVMapHeight;
        
        Vector3 cellMin = plane.position +
            (cellY / (float)settings.FOVMapHeight) * planeSizeZ * plane.forward +
            (cellX / (float)settings.FOVMapWidth) * planeSizeX * plane.right;
        
        Vector3 cellMax = plane.position +
            ((cellY + 1) / (float)settings.FOVMapHeight) * planeSizeZ * plane.forward +
            ((cellX + 1) / (float)settings.FOVMapWidth) * planeSizeX * plane.right;
        
        // Draw cell rectangle
        Handles.color = Color.yellow;
        Vector3[] corners = new Vector3[4];
        corners[0] = cellMin;
        corners[1] = cellMin + squareSizeX * plane.right;
        corners[2] = cellMax;
        corners[3] = cellMin + squareSizeZ * plane.forward;
        
        Handles.DrawPolyLine(corners[0], corners[1], corners[2], corners[3], corners[0]);
        
        // Draw draggable position handle
        EditorGUI.BeginChangeCheck();
        Vector3 newPosition = Handles.PositionHandle(cellWorldPosition, Quaternion.identity);
        if (EditorGUI.EndChangeCheck())
        {
            // Convert new position to cell coordinates
            Vector3 localPos = plane.InverseTransformPoint(newPosition);
            Vector3 scaledPos = Vector3.Scale(localPos, plane.localScale);
            
            // Convert to UV coordinates (0-1)
            float uvX = (scaledPos.x / planeSizeX);
            float uvZ = (scaledPos.z / planeSizeZ);
            
            // Convert to cell coordinates
            int newCellX = Mathf.FloorToInt(uvX * settings.FOVMapWidth);
            int newCellY = Mathf.FloorToInt(uvZ * settings.FOVMapHeight);
            
            // Clamp to valid range
            newCellX = Mathf.Clamp(newCellX, 0, settings.FOVMapWidth - 1);
            newCellY = Mathf.Clamp(newCellY, 0, settings.FOVMapHeight - 1);
            
            // Update if changed
            if (newCellX != cellX || newCellY != cellY)
            {
                cellX = newCellX;
                cellY = newCellY;
                CalculateCell();
                Repaint(); // Update editor window
            }
        }
        
        // Draw cell center sphere
        Handles.color = Color.cyan;
        Handles.SphereHandleCap(0, cellWorldPosition, Quaternion.identity, 1f, EventType.Repaint);
        
        // Draw text label
        Handles.Label(cellWorldPosition + Vector3.up * 5f, 
            $"Cell ({cellX}, {cellY})\nDir: {directionIndex}\nDist: {calculatedDistance:F2}m\nRatio: {distanceRatio:F3}");
    }
}
}

