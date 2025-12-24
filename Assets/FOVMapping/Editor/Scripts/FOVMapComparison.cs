using UnityEditor;
using UnityEngine;
using FOVMapping;

namespace FOVMapping
{
public class FOVMapComparison : EditorWindow
{
    private FOVBakeSettings settingsOld;
    private FOVBakeSettings settingsNew;
    private FOVManager fovManager;

    // Cached results
    private Texture2DArray cachedOldMap;
    private Texture2DArray cachedNewMap;
    private double cachedOldDuration;
    private double cachedNewDuration;
    private bool hasComparisonResults = false;

    // Preview settings
    private int previewLayerOld = 0;
    private int previewLayerNew = 0;
    private bool showPreviewOld = true;
    private bool showPreviewNew = true;
    private Vector2 scrollPosition;
    
    // Results display
    private string comparisonResults = "";
    private Vector2 resultsScrollPosition;

    [MenuItem("Window/FOV Mapping/Algorithm Comparison")]
    public static void ShowWindow()
    {
        FOVMapComparison window = GetWindow<FOVMapComparison>("FOV Algorithm Comparison");
        window.minSize = new Vector2(800, 700);
        window.Show();
    }

    private void OnGUI()
    {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        GUILayout.Label("FOV Map Algorithm Comparison", EditorStyles.boldLabel);
        EditorGUILayout.Space(10);

        EditorGUILayout.HelpBox(
            "This tool compares two FOV bake settings by running both and analyzing the differences. " +
            "You can bake each individually or compare existing baked maps.",
            MessageType.Info
        );

        EditorGUILayout.Space(10);

        // Settings selection
        EditorGUILayout.LabelField("Settings to Compare", EditorStyles.boldLabel);
        
        EditorGUI.BeginChangeCheck();
        settingsOld = (FOVBakeSettings)EditorGUILayout.ObjectField(
            "Settings A",
            settingsOld,
            typeof(FOVBakeSettings),
            false
        );

        settingsNew = (FOVBakeSettings)EditorGUILayout.ObjectField(
            "Settings B",
            settingsNew,
            typeof(FOVBakeSettings),
            false
        );
        
        if (EditorGUI.EndChangeCheck())
        {
            // Settings changed, clear cached results
            hasComparisonResults = false;
            comparisonResults = "";
        }

        EditorGUILayout.Space(10);

        // FOVManager transform selection
        EditorGUILayout.LabelField("FOV Manager", EditorStyles.boldLabel);
        fovManager = (FOVManager)EditorGUILayout.ObjectField(
            "FOVManager Scene Object",
            fovManager,
            typeof(FOVManager),
            true
        );

        EditorGUILayout.Space(20);

        // Action buttons
        EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);
        
        EditorGUILayout.BeginHorizontal();
        
        // Bake A button
        GUI.enabled = settingsOld != null && fovManager != null;
        if (GUILayout.Button("Bake A", GUILayout.Height(30)))
        {
            BakeSettingsA();
        }
        
        // Bake B button
        GUI.enabled = settingsNew != null && fovManager != null;
        if (GUILayout.Button("Bake B", GUILayout.Height(30)))
        {
            BakeSettingsB();
        }
        
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(5);

        // Compare button (works with already baked maps)
        GUI.enabled = settingsOld != null && settingsNew != null && 
                        settingsOld.FOVMapArray != null && settingsNew.FOVMapArray != null;
        if (GUILayout.Button("Compare Existing Baked Maps", GUILayout.Height(30)))
        {
            CompareExistingMaps();
        }
        GUI.enabled = true;

        EditorGUILayout.Space(5);

        // Rebake and compare button
        GUI.enabled = settingsOld != null && settingsNew != null && fovManager != null;
        if (GUILayout.Button("Rebake Both & Compare", GUILayout.Height(40)))
        {
            RebakeAndCompare();
        }
        GUI.enabled = true;

        if (settingsOld == null || settingsNew == null || fovManager == null)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox(
                "Please assign both settings assets and a FOVManager transform.",
                MessageType.Warning
            );
        }

        // Display comparison results if available
        if (hasComparisonResults && cachedOldMap != null && cachedNewMap != null)
        {
            DrawComparisonResults();
        }

        // Results text box
        if (!string.IsNullOrEmpty(comparisonResults))
        {
            EditorGUILayout.Space(20);
            EditorGUILayout.LabelField("Comparison Results", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginVertical("box");
            resultsScrollPosition = EditorGUILayout.BeginScrollView(resultsScrollPosition, GUILayout.Height(200));
            
            // Calculate the height needed for the text
            GUIStyle textStyle = EditorStyles.textArea;
            float textHeight = textStyle.CalcHeight(new GUIContent(comparisonResults), EditorGUIUtility.currentViewWidth - 50);
            
            // Use a fixed height that will trigger scrolling
            EditorGUILayout.SelectableLabel(comparisonResults, textStyle, GUILayout.Height(Mathf.Max(200, textHeight)));
            
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.EndScrollView();
    }

    private void BakeSettingsA()
    {
        if (settingsOld == null || fovManager == null) return;

        Debug.Log($"FOVMapComparison: Baking Settings A ({settingsOld.name})...");
        
        bool success = FOVMapGenerator.BakeFOVMap(settingsOld, fovManager, out double duration);
        
        if (success)
        {
            // Auto-compare if both maps exist
            if (settingsOld.FOVMapArray != null && settingsNew != null && settingsNew.FOVMapArray != null)
            {
                cachedOldMap = settingsOld.FOVMapArray;
                cachedNewMap = settingsNew.FOVMapArray;
                cachedOldDuration = duration;
                cachedNewDuration = 0; // B wasn't rebaked
                hasComparisonResults = true;
                LogComparison();
                Repaint();
            }
            
            EditorUtility.DisplayDialog(
                "Bake Complete",
                $"Settings A baked successfully in {duration:F2} seconds ({duration / 60:F2} minutes)!",
                "OK"
            );
        }
        else
        {
            EditorUtility.DisplayDialog(
                "Bake Failed",
                $"Settings A baking failed. Check the console for details.",
                "OK"
            );
        }
    }

    private void BakeSettingsB()
    {
        if (settingsNew == null || fovManager == null) return;

        Debug.Log($"FOVMapComparison: Baking Settings B ({settingsNew.name})...");
        
        bool success = FOVMapGenerator.BakeFOVMap(settingsNew, fovManager, out double duration);
        
        if (success)
        {
            // Auto-compare if both maps exist
            if (settingsNew.FOVMapArray != null && settingsOld != null && settingsOld.FOVMapArray != null)
            {
                cachedOldMap = settingsOld.FOVMapArray;
                cachedNewMap = settingsNew.FOVMapArray;
                cachedOldDuration = 0; // A wasn't rebaked
                cachedNewDuration = duration;
                hasComparisonResults = true;
                LogComparison();
                Repaint();
            }
            
            EditorUtility.DisplayDialog(
                "Bake Complete",
                $"Settings B baked successfully in {duration:F2} seconds ({duration / 60:F2} minutes)!",
                "OK"
            );
        }
        else
        {
            EditorUtility.DisplayDialog(
                "Bake Failed",
                $"Settings B baking failed. Check the console for details.",
                "OK"
            );
        }
    }

    private void CompareExistingMaps()
    {
        if (settingsOld == null || settingsNew == null || 
            settingsOld.FOVMapArray == null || settingsNew.FOVMapArray == null)
        {
            EditorUtility.DisplayDialog(
                "FOV Mapping Error",
                "Both settings must have baked FOV maps assigned.",
                "OK"
            );
            return;
        }

        Debug.Log("FOVMapComparison: Comparing existing baked maps...");

        cachedOldMap = settingsOld.FOVMapArray;
        cachedNewMap = settingsNew.FOVMapArray;
        cachedOldDuration = 0;
        cachedNewDuration = 0;
        hasComparisonResults = true;

        LogComparison();
        Repaint();
    }

    private void RebakeAndCompare()
    {
        if (settingsOld == null || settingsNew == null || fovManager == null)
        {
            EditorUtility.DisplayDialog(
                "FOV Mapping Error",
                "Please assign both settings assets and a FOVManager transform.",
                "OK"
            );
            return;
        }

        Debug.Log("FOVMapComparison: Rebaking both and comparing...");

        // Bake Settings A
        Debug.Log($"FOVMapComparison: Baking Settings A ({settingsOld.name})...");
        double oldDuration;
        bool oldSuccess = FOVMapGenerator.BakeFOVMap(settingsOld, fovManager, out oldDuration);

        if (!oldSuccess)
        {
            EditorUtility.DisplayDialog(
                "Baking Failed",
                "Settings A baking failed. Check the console for details.",
                "OK"
            );
            return;
        }

        // Bake Settings B
        Debug.Log($"FOVMapComparison: Baking Settings B ({settingsNew.name})...");
        double newDuration;
        bool newSuccess = FOVMapGenerator.BakeFOVMap(settingsNew, fovManager, out newDuration);

        if (!newSuccess)
        {
            EditorUtility.DisplayDialog(
                "Baking Failed",
                "Settings B baking failed. Check the console for details.",
                "OK"
            );
            return;
        }

        // Load the baked results for comparison
        cachedOldMap = settingsOld.FOVMapArray;
        cachedNewMap = settingsNew.FOVMapArray;
        cachedOldDuration = oldDuration;
        cachedNewDuration = newDuration;
        hasComparisonResults = true;

        LogComparison();

        EditorUtility.DisplayDialog(
            "Comparison Complete",
            $"Rebake and comparison complete!\n\n" +
            $"Settings A: {oldDuration:F2} seconds\n" +
            $"Settings B: {newDuration:F2} seconds\n\n" +
            $"Check the window for visual comparison and Console for detailed statistics.",
            "OK"
        );

        Repaint();
    }

    private void DrawComparisonResults()
    {
        EditorGUILayout.Space(20);
        EditorGUILayout.LabelField("Visual Comparison", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();

        // Settings A preview
        EditorGUILayout.BeginVertical(GUILayout.Width(350));
        showPreviewOld = EditorGUILayout.Foldout(showPreviewOld, $"Settings A ({settingsOld.name})", true);
        if (showPreviewOld && cachedOldMap.depth > 0)
        {
            previewLayerOld = EditorGUILayout.IntSlider("Layer", previewLayerOld, 0, cachedOldMap.depth - 1);
            
            Texture2D previewTexture = new Texture2D(cachedOldMap.width, cachedOldMap.height, cachedOldMap.format, false);
            Color[] pixels = cachedOldMap.GetPixels(previewLayerOld, 0);
            previewTexture.SetPixels(pixels);
            previewTexture.Apply();
            
            int previewSize = 300;
            GUILayout.Box(previewTexture, GUILayout.Width(previewSize), GUILayout.Height(previewSize));
            EditorGUILayout.LabelField($"Layer {previewLayerOld}: Direction range {previewLayerOld * 4}-{(previewLayerOld + 1) * 4 - 1}", EditorStyles.miniLabel);
            
            DestroyImmediate(previewTexture);
        }
        EditorGUILayout.EndVertical();

        // Settings B preview
        EditorGUILayout.BeginVertical(GUILayout.Width(350));
        showPreviewNew = EditorGUILayout.Foldout(showPreviewNew, $"Settings B ({settingsNew.name})", true);
        if (showPreviewNew && cachedNewMap.depth > 0)
        {
            previewLayerNew = EditorGUILayout.IntSlider("Layer", previewLayerNew, 0, cachedNewMap.depth - 1);
            
            Texture2D previewTexture = new Texture2D(cachedNewMap.width, cachedNewMap.height, cachedNewMap.format, false);
            Color[] pixels = cachedNewMap.GetPixels(previewLayerNew, 0);
            previewTexture.SetPixels(pixels);
            previewTexture.Apply();
            
            int previewSize = 300;
            GUILayout.Box(previewTexture, GUILayout.Width(previewSize), GUILayout.Height(previewSize));
            EditorGUILayout.LabelField($"Layer {previewLayerNew}: Direction range {previewLayerNew * 4}-{(previewLayerNew + 1) * 4 - 1}", EditorStyles.miniLabel);
            
            DestroyImmediate(previewTexture);
        }
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();
    }

    private void LogComparison()
    {
        if (cachedOldMap == null || cachedNewMap == null)
        {
            comparisonResults = "Error: One or both maps are null";
            return;
        }

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("FOV Map Algorithm Comparison Results");
        sb.AppendLine("=====================================");
        sb.AppendLine();
        
        if (cachedOldDuration > 0 || cachedNewDuration > 0)
        {
            sb.AppendLine("Performance Metrics:");
            sb.AppendLine($"  Settings A duration: {cachedOldDuration:F2} seconds");
            sb.AppendLine($"  Settings B duration: {cachedNewDuration:F2} seconds");
            if (cachedOldDuration > 0 && cachedNewDuration > 0)
            {
                sb.AppendLine($"  Speed improvement: {(cachedOldDuration / cachedNewDuration):F2}x");
            }
            sb.AppendLine();
        }

        // Compare texture properties
        sb.AppendLine("Texture Properties:");
        sb.AppendLine($"  Settings A map: {cachedOldMap.width}x{cachedOldMap.height}, {cachedOldMap.depth} layers");
        sb.AppendLine($"  Settings B map: {cachedNewMap.width}x{cachedNewMap.height}, {cachedNewMap.depth} layers");
        sb.AppendLine();

        // Compare pixel values
        int totalPixels = cachedOldMap.width * cachedOldMap.height * cachedOldMap.depth;
        int differentPixels = 0;
        float totalDifference = 0f;
        float maxDifference = 0f;
        int oldDarkPixels = 0;
        int newDarkPixels = 0;
        int oldWhitePixels = 0;
        int newWhitePixels = 0;

        for (int layer = 0; layer < cachedOldMap.depth; ++layer)
        {
            Color[] oldPixels = cachedOldMap.GetPixels(layer, 0);
            Color[] newPixels = cachedNewMap.GetPixels(layer, 0);

            for (int i = 0; i < oldPixels.Length; ++i)
            {
                Color oldPixel = oldPixels[i];
                Color newPixel = newPixels[i];

                // Count dark and white pixels
                if (oldPixel.r >= 0.9f) oldWhitePixels++;
                else oldDarkPixels++;
                if (newPixel.r >= 0.9f) newWhitePixels++;
                else newDarkPixels++;

                // Calculate differences
                float difference = Mathf.Abs(oldPixel.r - newPixel.r);
                if (difference > 0.01f) // Significant difference threshold
                {
                    differentPixels++;
                }
                totalDifference += difference;
                if (difference > maxDifference)
                {
                    maxDifference = difference;
                }
            }
        }

        sb.AppendLine("Pixel Statistics:");
        sb.AppendLine($"  Total pixels: {totalPixels:N0}");
        sb.AppendLine($"  Different pixels: {differentPixels:N0} ({(differentPixels * 100f / totalPixels):F2}%)");
        sb.AppendLine($"  Average difference: {totalDifference / totalPixels:F4}");
        sb.AppendLine($"  Maximum difference: {maxDifference:F4}");
        sb.AppendLine();
        sb.AppendLine("Pixel Distribution:");
        sb.AppendLine($"  Settings A dark pixels: {oldDarkPixels:N0} ({(oldDarkPixels * 100f / totalPixels):F2}%)");
        sb.AppendLine($"  Settings B dark pixels: {newDarkPixels:N0} ({(newDarkPixels * 100f / totalPixels):F2}%)");
        sb.AppendLine($"  Settings A white pixels: {oldWhitePixels:N0} ({(oldWhitePixels * 100f / totalPixels):F2}%)");
        sb.AppendLine($"  Settings B white pixels: {newWhitePixels:N0} ({(newWhitePixels * 100f / totalPixels):F2}%)");
        sb.AppendLine($"  Dark pixel ratio (B/A): {(newDarkPixels * 100f / oldDarkPixels):F2}%");

        comparisonResults = sb.ToString();
        
        // Also log to console for debugging
        Debug.Log(comparisonResults);
    }
}
}

