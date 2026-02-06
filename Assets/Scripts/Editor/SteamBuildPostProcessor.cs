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

            // 2. Copy Steam runtime DLL(s)
            // Facepunch Steamworks requires steam_api(64).dll to be present in the built player output.
            // In practice, having it next to the .exe is the most reliable for overlay/injection.
            CopySteamRuntimeDlls(target, pathToBuiltProject, buildDir);
        }

        private static void CopySteamRuntimeDlls(BuildTarget target, string pathToBuiltProject, string buildDir) {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var exeNameNoExt = Path.GetFileNameWithoutExtension(pathToBuiltProject);
            var dataDir = Path.Combine(buildDir, exeNameNoExt + "_Data");

            if(target == BuildTarget.StandaloneWindows64) {
                var src = ResolveSteamDllPath(projectRoot, "steam_api64.dll");
                if(string.IsNullOrEmpty(src)) {
                    Debug.LogError("[SteamBuild] steam_api64.dll not found in project. " +
                                   "Ensure Facepunch Steamworks redistributables are present.");
                    return;
                }

                CopySteamDllToBuildLocations(src, buildDir, dataDir, "x86_64");
                return;
            }

            // StandaloneWindows (32-bit)
            var src32 = ResolveSteamDllPath(projectRoot, "steam_api.dll");
            if(string.IsNullOrEmpty(src32)) {
                Debug.LogError("[SteamBuild] steam_api.dll not found in project. " +
                               "Ensure Facepunch Steamworks redistributables are present.");
                return;
            }

            CopySteamDllToBuildLocations(src32, buildDir, dataDir, "x86");
        }

        private static string ResolveSteamDllPath(string projectRoot, string dllName) {
            // Known Facepunch redistributable locations in this repo.
            var facepunchBase = Path.Combine(projectRoot, "Assets", "Facepunch.Steamworks.2.4.1", "Unity", "redistributable_bin");

            if(dllName == "steam_api64.dll") {
                var p = Path.Combine(facepunchBase, "win64", dllName);
                if(File.Exists(p)) return p;
            } else if(dllName == "steam_api.dll") {
                var p = Path.Combine(facepunchBase, dllName);
                if(File.Exists(p)) return p;
            }

            // Alternate common Unity plugin locations (if you later move the DLL under Assets/Plugins).
            var alt1 = Path.Combine(projectRoot, "Assets", "Plugins", "x86_64", dllName);
            if(File.Exists(alt1)) return alt1;

            var alt2 = Path.Combine(projectRoot, "Assets", "Plugins", dllName);
            if(File.Exists(alt2)) return alt2;

            return null;
        }

        private static void CopySteamDllToBuildLocations(string srcDllPath, string buildDir, string dataDir, string cpuFolderName) {
            var dllName = Path.GetFileName(srcDllPath);

            try {
                // Copy next to the .exe (most reliable for overlay/injection)
                var dstRoot = Path.Combine(buildDir, dllName);
                File.Copy(srcDllPath, dstRoot, overwrite: true);
                Debug.Log($"[SteamBuild] Copied {dllName} to build root: {dstRoot}");
            } catch(System.Exception e) {
                Debug.LogError($"[SteamBuild] Failed to copy Steam DLL to build root: {e.Message}");
            }

            try {
                // Also copy into Unity's Plugins folder inside the _Data directory
                var pluginsDir = Path.Combine(dataDir, "Plugins", cpuFolderName);
                Directory.CreateDirectory(pluginsDir);
                var dstPlugins = Path.Combine(pluginsDir, dllName);
                File.Copy(srcDllPath, dstPlugins, overwrite: true);
                Debug.Log($"[SteamBuild] Copied {dllName} to data plugins: {dstPlugins}");
            } catch(System.Exception e) {
                Debug.LogError($"[SteamBuild] Failed to copy Steam DLL to data plugins: {e.Message}");
            }
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
