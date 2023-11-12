using NUnit.Framework.Internal;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using FOVMapping;

namespace FOVMapping
{
public class FOVMappingEditorWindow : EditorWindow
{
	[SerializeField] private FOVMapGenerationInfo generationInfo;

	[MenuItem("Window/FOV Mapping")]
	public static void ShowWindow()
	{
		GetWindow(typeof(FOVMappingEditorWindow));
	}

	private void OnGUI()
	{
		EditorGUILayout.BeginVertical(new GUIStyle(GUI.skin.window));
		{
			if (generationInfo == null) generationInfo = new FOVMapGenerationInfo();
			SerializedObject so = new SerializedObject(this);
			SerializedProperty sp = so.FindProperty("generationInfo");
			EditorGUILayout.PropertyField(sp);
			so.ApplyModifiedProperties();

			if (GUILayout.Button("Create an FOV map"))
			{
				double startTime = Time.realtimeSinceStartup;

				bool isSuccessful = FOVMapGenerator.CreateFOVMap
				(
					generationInfo,
					(y, height) =>
					{
						return EditorUtility.DisplayCancelableProgressBar("Progress", $"Processed {y} / {height} rows", (float)y / height);
					}
				);

				EditorUtility.ClearProgressBar();

				double endTime = Time.realtimeSinceStartup;
				if (isSuccessful) EditorUtility.DisplayDialog("FOV Map", $"An FOV map set has been created successfully in {(int)(endTime - startTime)} seconds", "OK");
			}
		}
		EditorGUILayout.EndVertical();
	}
}
}
