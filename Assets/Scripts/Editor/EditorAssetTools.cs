using UnityEngine;
using UnityEditor;

public class EditorAssetTools {
    [MenuItem("Tools/Force Unload Assets")]
    public static void ForceUnload() {
        Resources.UnloadUnusedAssets().completed += (op) => {
            System.GC.Collect();
            Debug.Log("✅ Assets Unloaded and GC Collected. Next Play Mode should treat assets as fresh.");
        };
    }
}
