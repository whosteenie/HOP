using System.IO;
using System.Text.RegularExpressions;
using Diagnostics;
using UnityEditor;
using UnityEngine;

namespace Editor.Build {
    /// <summary>
    /// Ensures steam_appid.txt exists in the project root while running in the Unity Editor.
    /// This is required for Steamworks to initialize when pressing Play in-editor (non-Steam launch).
    /// </summary>
    [InitializeOnLoad]
    public static class SteamAppIdEditorHelper {
        private const uint DefaultTestingAppId = 480;

        static SteamAppIdEditorHelper() {
            EnsureSteamAppIdFileExists();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state) {
            if(state == PlayModeStateChange.EnteredPlayMode) {
                EnsureSteamAppIdFileExists();
            }
        }

        private static void EnsureSteamAppIdFileExists() {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var appIdPath = Path.Combine(projectRoot, "steam_appid.txt");

            if(!TryReadAppIdFromInitScene(projectRoot, out var appId)) {
                appId = DefaultTestingAppId;
                DevLog.LogWarning(
                    "[SteamEditor] Could not read Steam AppID from Assets/Scenes/Init.unity. " +
                    $"Writing {appId} (Spacewar) to steam_appid.txt for editor testing."
                );
            }

            try {
                // Avoid rewriting if already correct (prevents noisy file watchers).
                if(File.Exists(appIdPath)) {
                    var existing = File.ReadAllText(appIdPath).Trim();
                    if(existing == appId.ToString()) {
                        return;
                    }
                }

                File.WriteAllText(appIdPath, appId.ToString());
            } catch(System.Exception e) {
                DevLog.LogError($"[SteamEditor] Failed to write steam_appid.txt: {e.Message}");
            }
        }

        private static bool TryReadAppIdFromInitScene(string projectRoot, out uint appId) {
            appId = 0;

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
    }
}

