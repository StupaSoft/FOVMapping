using UnityEditor;
using UnityEngine;
using FOVMapping;

namespace FOVMapping
{
[CustomEditor(typeof(FOVBakeSettings))]
public class FOVBakeSettingsEditor : Editor
{
    private int previewLayer = 0;
    private bool showPreview = true;

    public override void OnInspectorGUI()
    {
        FOVBakeSettings settings = (FOVBakeSettings)target;

        // Draw default inspector
        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("Use the FOV Mapping Editor Window to bake FOV maps. The plane must be selected in the scene.", MessageType.Info);

        // Show current FOV map info and preview
        if (settings.FOVMapArray != null)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Current FOV Map", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Size: {settings.FOVMapArray.width}x{settings.FOVMapArray.height}");
            EditorGUILayout.LabelField($"Layers: {settings.FOVMapArray.depth}");
            EditorGUILayout.LabelField($"Sampling Range: {settings.FOVMapArray.mipMapBias}");

            // Preview section
            EditorGUILayout.Space(5);
            showPreview = EditorGUILayout.Foldout(showPreview, "Texture Array Preview", true);

            if (showPreview && settings.FOVMapArray.depth > 0)
            {
                EditorGUI.indentLevel++;

                // Layer selection
                previewLayer = EditorGUILayout.IntSlider("Preview Layer", previewLayer, 0, settings.FOVMapArray.depth - 1);

                // Create a preview of the selected layer
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField($"Layer {previewLayer} Preview:", EditorStyles.miniLabel);

                // Create a temporary texture for preview
                Texture2D previewTexture = new Texture2D(settings.FOVMapArray.width, settings.FOVMapArray.height);
                Color[] pixels = settings.FOVMapArray.GetPixels(previewLayer, 0);
                previewTexture.SetPixels(pixels);
                previewTexture.Apply();

                // Display the preview with a reasonable size
                int previewSize = Mathf.Min(256, settings.FOVMapArray.width, settings.FOVMapArray.height);
                Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.ExpandWidth(false));
                EditorGUI.DrawPreviewTexture(previewRect, previewTexture);

                // Show layer information
                EditorGUILayout.LabelField($"Layer {previewLayer}: Direction range {previewLayer * 4}-{(previewLayer + 1) * 4 - 1}", EditorStyles.miniLabel);

                // Clean up
                DestroyImmediate(previewTexture);

                EditorGUI.indentLevel--;
            }
        }
    }
}
}
