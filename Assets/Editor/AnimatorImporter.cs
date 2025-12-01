using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Collections.Generic;

public class AnimatorImporter : EditorWindow
{
    [MenuItem("Tools/Import Animation Clip from CSV")]
    public static void ShowWindow()
    {
        GetWindow<AnimatorImporter>("Animation Clip Importer");
    }

    private AnimationClip selectedClip;
    private TextAsset csvFile;
    private string[] parameterNames = new string[]
    {
        "Right Arm Down-Up",
        "Right Arm Front-Back",
        "Right Forearm Twist In-Out"
    };

    private void OnGUI()
    {
        GUILayout.Label("Animation Clip Importer", EditorStyles.boldLabel);
        
        selectedClip = (AnimationClip)EditorGUILayout.ObjectField(
            "Animation Clip", 
            selectedClip, 
            typeof(AnimationClip), 
            false
        );

        csvFile = (TextAsset)EditorGUILayout.ObjectField(
            "CSV File", 
            csvFile, 
            typeof(TextAsset), 
            false
        );

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Expected Parameters:", EditorStyles.boldLabel);
        foreach (var param in parameterNames)
        {
            EditorGUILayout.LabelField($"  - {param}");
        }

        EditorGUILayout.Space();
        
        if (GUILayout.Button("Import from CSV") && selectedClip != null && csvFile != null)
        {
            ImportFromCSV(selectedClip, csvFile);
        }
    }

    private void ImportFromCSV(AnimationClip clip, TextAsset csv)
    {
        string[] lines = csv.text.Split('\n');
        if (lines.Length < 2)
        {
            Debug.LogError("CSV file is empty or invalid");
            return;
        }

        // Parse header to find column indices
        string[] header = ParseCSVLine(lines[0]);
        int timeIndex = -1;
        Dictionary<string, int> parameterIndices = new Dictionary<string, int>();

        for (int i = 0; i < header.Length; i++)
        {
            string col = header[i].Trim();
            if (col.Equals("Time", System.StringComparison.OrdinalIgnoreCase) || 
                col.Equals("Frame", System.StringComparison.OrdinalIgnoreCase))
            {
                timeIndex = i;
            }
            else
            {
                // Check if this column matches any of our parameter names
                foreach (var paramName in parameterNames)
                {
                    if (col.Contains(paramName) || paramName.Contains(col))
                    {
                        parameterIndices[paramName] = i;
                        break;
                    }
                }
            }
        }

        if (timeIndex == -1)
        {
            Debug.LogError("Could not find 'Time' or 'Frame' column in CSV");
            return;
        }

        if (parameterIndices.Count == 0)
        {
            Debug.LogError("Could not find any matching parameter columns in CSV");
            Debug.Log("Available columns: " + string.Join(", ", header));
            return;
        }

        Debug.Log($"Found {parameterIndices.Count} parameters to import");

        // Parse data rows
        Dictionary<string, List<Keyframe>> keyframesByParameter = new Dictionary<string, List<Keyframe>>();
        foreach (var paramName in parameterIndices.Keys)
        {
            keyframesByParameter[paramName] = new List<Keyframe>();
        }

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            string[] values = ParseCSVLine(lines[i]);
            if (values.Length <= timeIndex) continue;

            if (!float.TryParse(values[timeIndex], out float time)) continue;

            foreach (var kvp in parameterIndices)
            {
                string paramName = kvp.Key;
                int colIndex = kvp.Value;

                if (colIndex < values.Length && float.TryParse(values[colIndex], out float value))
                {
                    keyframesByParameter[paramName].Add(new Keyframe(time, value));
                }
            }
        }

        // Get the bindings for the animation clip
        EditorCurveBinding[] allBindings = AnimationUtility.GetCurveBindings(clip);
        
        // Find bindings that match our parameters
        foreach (var kvp in keyframesByParameter)
        {
            string paramName = kvp.Key;
            List<Keyframe> keyframes = kvp.Value;

            if (keyframes.Count == 0)
            {
                Debug.LogWarning($"No keyframes found for parameter: {paramName}");
                continue;
            }

            // Find the binding for this parameter
            EditorCurveBinding? matchingBinding = null;
            foreach (var binding in allBindings)
            {
                if (binding.type == typeof(Animator) && binding.propertyName == paramName)
                {
                    matchingBinding = binding;
                    break;
                }
            }

            if (!matchingBinding.HasValue)
            {
                Debug.LogWarning($"Could not find binding for parameter: {paramName}. It may need to be created.");
                continue;
            }

            // Create animation curve from keyframes
            AnimationCurve curve = new AnimationCurve(keyframes.ToArray());
            
            // Set tangents to linear for smoother interpolation (or you can use Auto)
            for (int i = 0; i < curve.keys.Length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            }

            // Apply the curve to the clip
            AnimationUtility.SetEditorCurve(clip, matchingBinding.Value, curve);
            Debug.Log($"Imported {keyframes.Count} keyframes for parameter: {paramName}");
        }

        EditorUtility.SetDirty(clip);
        AssetDatabase.SaveAssets();
        Debug.Log("Animation clip updated successfully!");
    }

    private string[] ParseCSVLine(string line)
    {
        List<string> values = new List<string>();
        bool inQuotes = false;
        string currentValue = "";

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                values.Add(currentValue);
                currentValue = "";
            }
            else
            {
                currentValue += c;
            }
        }
        values.Add(currentValue); // Add last value

        return values.ToArray();
    }
}