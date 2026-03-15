using UnityEditor;
using UnityEngine;

namespace Editor.Tools {
    public static class EditorAssetTools {
    [MenuItem("Tools/Force Unload Assets")]
    public static void ForceUnload() {
        Resources.UnloadUnusedAssets().completed += _ => {
            System.GC.Collect();
            Debug.Log("✅ Assets Unloaded and GC Collected. Next Play Mode should treat assets as fresh.");
        };
    }
    }
}
