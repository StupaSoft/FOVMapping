using UnityEditor;
using UnityEngine;
using FOVMapping;

namespace FOVMapping
{
    [CustomEditor(typeof(FOVBakeSettings))]
    public class FOVBakeSettingsEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            FOVBakeSettings settings = (FOVBakeSettings)target;

            // Draw default inspector
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Use the FOV Mapping Editor Window to bake FOV maps. The plane must be selected in the scene.", MessageType.Info);

            // Show current FOV map info
            if (settings.FOVMapArray != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Current FOV Map", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Size: {settings.FOVMapArray.width}x{settings.FOVMapArray.height}");
                EditorGUILayout.LabelField($"Layers: {settings.FOVMapArray.depth}");
                EditorGUILayout.LabelField($"Sampling Range: {settings.FOVMapArray.mipMapBias}");
            }
        }

    }
}
