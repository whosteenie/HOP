using UnityEditor;
using UnityEditor.Callbacks;
using System.IO;
using UnityEngine;
using Network.Steam;

namespace Game.Editor {
    /// <summary>
    /// Automatically handles Steam deployment requirements like steam_appid.txt
    /// after a build is completed.
    /// </summary>
    public static class SteamBuildPostProcessor {
        [PostProcessBuild]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject) {
            // Only handle Windows builds for now as Steam integration is primary on Windows
            if (target != BuildTarget.StandaloneWindows && target != BuildTarget.StandaloneWindows64) {
                return;
            }

            // Path to the directory containing the .exe
            string buildDir = Path.GetDirectoryName(pathToBuiltProject);
            if (string.IsNullOrEmpty(buildDir)) return;

            // 1. Create steam_appid.txt
            uint appId = GetAppIdFromManager();
            string appIdPath = Path.Combine(buildDir, "steam_appid.txt");
            
            try {
                File.WriteAllText(appIdPath, appId.ToString());
                Debug.Log($"[SteamBuild] Successfully created steam_appid.txt with AppID {appId} in {buildDir}");
            }
            catch (System.Exception e) {
                Debug.LogError($"[SteamBuild] Failed to create steam_appid.txt: {e.Message}");
            }

            // 2. DLL Note
            // Unity's default Build Pipeline usually handles DLLs in the _Data folder,
            // but for Steam Sockets (Facepunch), sometimes steam_api64.dll needs to be in the root.
            // We can add logic here to copy it if testing shows initialization failures in builds.
        }

        private static uint GetAppIdFromManager() {
            // Try to find the SteamManager in the project to get the AppID
            // If we can't find it, fallback to 480 (Spacewar)
            var manager = Object.FindFirstObjectByType<SteamManager>();
            if (manager != null) {
                // Since appId is private in SteamManager, we might need to make it public 
                // or just hardcode to 480 if it's the most common case.
                // For now, let's assume 480 or use a serialized field if we can access it via SerializedObject.
                
                var so = new SerializedObject(manager);
                var prop = so.FindProperty("appId");
                if (prop != null) return (uint)prop.intValue;
            }
            
            return 480; 
        }
    }
}
