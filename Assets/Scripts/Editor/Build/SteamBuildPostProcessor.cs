using UnityEditor;
using UnityEditor.Callbacks;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Game.Editor.Build {
    /// <summary>
    /// Post-build helper for Steam runtime DLL placement and local testing conveniences.
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

            // For local/non-Steam launches, Steamworks requires steam_appid.txt to resolve an AppID.
            // We only generate this for Development builds (so we don't accidentally ship it).
            TryCreateSteamAppIdFileForDevelopmentBuild(buildDir);

            // Copy Steam runtime DLL(s).
            // Steam does NOT provide steam_api(64).dll for you at runtime; it must be shipped with your build content.
            // In practice, having it next to the .exe is the most reliable for Steam overlay/injection.
            CopySteamRuntimeDlls(target, pathToBuiltProject, buildDir);
        }

        private static void TryCreateSteamAppIdFileForDevelopmentBuild(string buildDir) {
            // In legacy PostProcessBuild callbacks, BuildReport is unavailable; use editor build setting.
            bool isDevelopmentBuild = EditorUserBuildSettings.development;
            if(!isDevelopmentBuild) return;

            const uint defaultTestingAppId = 480;
            uint appId;
            if(!TryReadAppIdFromInitScene(out appId)) {
                appId = defaultTestingAppId;
                Debug.LogWarning($"[SteamBuild] Could not read Steam AppID from Init scene. Writing {appId} (Spacewar) to steam_appid.txt.");
            }

            var appIdPath = Path.Combine(buildDir, "steam_appid.txt");
            try {
                File.WriteAllText(appIdPath, appId.ToString());
                Debug.Log($"[SteamBuild] Development build: wrote steam_appid.txt ({appId}) to {appIdPath}");
            } catch(System.Exception e) {
                Debug.LogError($"[SteamBuild] Failed to write steam_appid.txt: {e.Message}");
            }
        }

        private static bool TryReadAppIdFromInitScene(out uint appId) {
            appId = 0;

            // This project currently stores SteamManager's serialized AppID in Assets/Scenes/Init.unity.
            // We parse the YAML for an 'appId: <number>' entry to match your configured value.
            // If you move SteamManager to a different bootstrap scene, update this path.
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var initScenePath = Path.Combine(projectRoot, "Assets", "Scenes", "Init.unity");
            if(!File.Exists(initScenePath)) return false;

            string text;
            try {
                text = File.ReadAllText(initScenePath);
            } catch {
                return false;
            }

            var match = Regex.Match(text, @"(?m)^\s*appId:\s*(\d+)\s*$");
            if(!match.Success) return false;

            if(!uint.TryParse(match.Groups[1].Value, out appId)) return false;
            return appId > 0;
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

    }
}
