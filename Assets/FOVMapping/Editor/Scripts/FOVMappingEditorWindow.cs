using UnityEditor;
using UnityEngine;
using FOVMapping;

namespace FOVMapping
{
    public class FOVMappingEditorWindow : EditorWindow
    {
        [SerializeField] private FOVBakeSettings settings;
        [SerializeField] private FOVManager fovManager; // This IS the projector plane (FOVManager is attached to the plane GameObject)

        [MenuItem("Window/FOV Mapping")]
        public static void ShowWindow()
        {
            GetWindow(typeof(FOVMappingEditorWindow));
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginVertical(new GUIStyle(GUI.skin.window));
            {
                EditorGUILayout.LabelField("FOV Mapping Settings", EditorStyles.boldLabel);
                EditorGUILayout.Space();

                // FOVManager (which is attached to the projector plane GameObject) - Show first
                EditorGUILayout.LabelField("Runtime Components", EditorStyles.boldLabel);
                
                // Auto-select if only one FOVManager in scene
                if (fovManager == null)
                {
                    FOVManager[] managers = FindObjectsOfType<FOVManager>();
                    if (managers.Length == 1)
                    {
                        fovManager = managers[0];
                    }
                    else if (managers.Length > 1)
                    {
                        EditorGUILayout.HelpBox($"Multiple FOVManagers found in scene ({managers.Length}). Please select one.", MessageType.Info);
                    }
                }
                
                fovManager = (FOVManager)EditorGUILayout.ObjectField("FOV Manager (Plane GameObject)", fovManager, typeof(FOVManager), true);
                
                if (fovManager == null)
                {
                    EditorGUILayout.HelpBox("Please assign a FOV Manager (Plane GameObject) for FOV mapping.", MessageType.Warning);
                    EditorGUILayout.EndVertical();
                    return;
                }

                EditorGUILayout.Space();

                // Auto-populate settings from FOVManager if available
                if (settings == null && fovManager.Settings != null)
                {
                    settings = fovManager.Settings;
                }

                // Settings ScriptableObject
                EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
                FOVBakeSettings newSettings = (FOVBakeSettings)EditorGUILayout.ObjectField("FOV Bake Settings", settings, typeof(FOVBakeSettings), false);
                
                if (newSettings != settings)
                {
                    settings = newSettings;
                    if (settings != null)
                    {
                        fovManager.Settings = settings;
                        EditorUtility.SetDirty(fovManager);
                    }
                }
                
                if (settings == null)
                {
                    EditorGUILayout.HelpBox("Please assign a FOVBakeSettings ScriptableObject.", MessageType.Warning);
                    if (GUILayout.Button("Create New Settings"))
                    {
                        CreateNewSettings();
                    }
                    EditorGUILayout.EndVertical();
                    return;
                }

                EditorGUILayout.Space();

                // Inline editor for settings
                EditorGUILayout.LabelField("Settings Configuration", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                SerializedObject serializedSettings = new SerializedObject(settings);
                SerializedProperty settingsProperty = serializedSettings.GetIterator();
                settingsProperty.NextVisible(true);
                
                while (settingsProperty.NextVisible(false))
                {
                    EditorGUILayout.PropertyField(settingsProperty, true);
                }
                serializedSettings.ApplyModifiedProperties();
                EditorGUI.indentLevel--;

                EditorGUILayout.Space();

                // Bake button
                EditorGUILayout.LabelField("Bake FOV Map", EditorStyles.boldLabel);
                EditorGUI.BeginDisabledGroup(settings.levelLayer == 0);
                {
                    if (GUILayout.Button("Bake FOV Map", GUILayout.Height(30)))
                    {
                        BakeFOVMap();
                    }
                }
                EditorGUI.EndDisabledGroup();

                if (settings.levelLayer == 0)
                {
                    EditorGUILayout.HelpBox("Please assign a Level Layer for sampling.", MessageType.Warning);
                }

                EditorGUILayout.Space();

                // Settings mismatch warning
                if (fovManager.Settings != settings)
                {
                    EditorGUILayout.HelpBox("FOVManager has different settings assigned.", MessageType.Info);
                    if (GUILayout.Button("Assign Current Settings to FOVManager"))
                    {
                        fovManager.Settings = settings;
                        EditorUtility.SetDirty(fovManager);
                    }
                }

            }
            EditorGUILayout.EndVertical();
        }

        private void CreateNewSettings()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Create FOV Bake Settings",
                "FOVBakeSettings",
                "asset",
                "Choose where to save the FOV Bake Settings");

            if (!string.IsNullOrEmpty(path))
            {
                FOVBakeSettings newSettings = CreateInstance<FOVBakeSettings>();
                AssetDatabase.CreateAsset(newSettings, path);
                AssetDatabase.Refresh();
                settings = newSettings;
            }
        }

        private void BakeFOVMap()
        {
            if (settings == null || fovManager == null) return;

            FOVMapGenerator.BakeFOVMapWithDialog(settings, fovManager.transform);
        }

    }
}
