using UnityEngine;
using UnityEditor;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;

public class AnimatorExporter : EditorWindow
{
    [MenuItem("Tools/Export Animation Clip to CSV")]
    public static void ShowWindow()
    {
        GetWindow<AnimatorExporter>("Animation Clip Exporter");
    }

    private AnimationClip selectedClip;
    private string outputPath = "Assets/ExportedAnimationData.csv";

    private void OnGUI()
    {
        GUILayout.Label("Animation Clip Exporter", EditorStyles.boldLabel);
        
        selectedClip = (AnimationClip)EditorGUILayout.ObjectField(
            "Animation Clip", 
            selectedClip, 
            typeof(AnimationClip), 
            false
        );

        outputPath = EditorGUILayout.TextField("Output Path", outputPath);

        if (GUILayout.Button("Export to CSV") && selectedClip != null)
        {
            ExportToCSV(selectedClip, outputPath);
        }
    }

    private void ExportToCSV(AnimationClip clip, string path)
    {
        var csv = new StringBuilder();
        
        // Get all bindings (curves) in the animation
        EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
        
        // Filter for Animator parameters (they'll have property names like the parameter names)
        // The bindings will have paths like "HumanCharacterDummy_M" and property names like "Right Arm Down-Up"
        var animatorBindings = bindings.Where(b => 
            b.type == typeof(Animator) && 
            (b.propertyName.Contains("Right Arm Down-Up") || 
             b.propertyName.Contains("Right Arm Front-Back") || 
             b.propertyName.Contains("Right Forearm Twist In-Out"))
        ).ToArray();

        if (animatorBindings.Length == 0)
        {
            Debug.LogWarning("No Animator parameter curves found. Make sure the animation clip has curves for the specified parameters.");
            return;
        }

        // Collect all unique time values
        HashSet<float> allTimes = new HashSet<float>();
        Dictionary<string, Dictionary<float, float>> parameterValues = new Dictionary<string, Dictionary<float, float>>();

        foreach (var binding in animatorBindings)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null) continue;

            string parameterName = binding.propertyName;
            parameterValues[parameterName] = new Dictionary<float, float>();

            foreach (var keyframe in curve.keys)
            {
                float time = keyframe.time;
                allTimes.Add(time);
                parameterValues[parameterName][time] = keyframe.value;
            }
        }

        // Build header
        csv.Append("Frame,Time");
        foreach (var paramName in parameterValues.Keys.OrderBy(k => k))
        {
            csv.Append($",{paramName}");
        }
        csv.AppendLine();

        // Write data sorted by time
        List<float> sortedTimes = new List<float>(allTimes);
        sortedTimes.Sort();

        foreach (var time in sortedTimes)
        {
            int frame = sortedTimes.IndexOf(time);
            csv.Append($"{frame},{time:F4}");

            foreach (var paramName in parameterValues.Keys.OrderBy(k => k))
            {
                float value = parameterValues[paramName].ContainsKey(time) 
                    ? parameterValues[paramName][time] 
                    : 0f; // Interpolate or use 0 if no keyframe at this time
                
                csv.Append($",{value:F6}");
            }
            csv.AppendLine();
        }

        File.WriteAllText(path, csv.ToString());
        AssetDatabase.Refresh();
        Debug.Log($"Animation data exported to {path}");
        Debug.Log($"Found {parameterValues.Count} Animator parameters: {string.Join(", ", parameterValues.Keys)}");
    }
}