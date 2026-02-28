using UnityEditor;
using UnityEngine;

namespace Game.Editor.Tools {
    public static class EditorAssetTools {
    [MenuItem("Tools/Force Unload Assets")]
    public static void ForceUnload() {
        Resources.UnloadUnusedAssets().completed += (op) => {
            System.GC.Collect();
            Debug.Log("✅ Assets Unloaded and GC Collected. Next Play Mode should treat assets as fresh.");
        };
    }
    }
}
