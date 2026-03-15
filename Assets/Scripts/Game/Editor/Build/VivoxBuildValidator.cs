using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Editor.Build {
    /// <summary>
    /// Prevents shipping builds that still have Vivox Test Mode enabled or a Vivox signing key present in project settings.
    /// If either is present, Vivox will generate tokens locally (insecure) and log warnings at runtime.
    /// </summary>
    public sealed class VivoxBuildValidator : IPreprocessBuildWithReport {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report) {
            var isDevelopmentBuild = (report.summary.options & BuildOptions.Development) != 0;

            var settingsPath = Path.Combine(Directory.GetCurrentDirectory(),
                "ProjectSettings", "Packages", "com.unity.services.vivox", "Settings.json");

            if(!File.Exists(settingsPath)) {
                var missingSettingsMessage = $"[VivoxBuildValidator] Vivox settings file not found at '{settingsPath}'. Cannot validate Test Mode/token key.";
                if(!isDevelopmentBuild) throw new BuildFailedException(missingSettingsMessage);
                Debug.LogWarning(missingSettingsMessage);
                return;
            }

            var settingsText = File.ReadAllText(settingsPath);
            if(string.IsNullOrEmpty(settingsText)) {
                var emptySettingsMessage = $"[VivoxBuildValidator] Vivox settings file is empty at '{settingsPath}'. Cannot validate Test Mode/token key.";
                if(!isDevelopmentBuild) throw new BuildFailedException(emptySettingsMessage);
                Debug.LogWarning(emptySettingsMessage);
                return;
            }

            var parsed = TryReadVivoxSettings(settingsText, out var isTestMode, out var tokenKey);
            if(!parsed) {
                const string parseFailedMessage = "[VivoxBuildValidator] Failed to parse Vivox settings; cannot validate Test Mode/token key.";
                if(!isDevelopmentBuild) throw new BuildFailedException(parseFailedMessage);
                Debug.LogWarning(parseFailedMessage);
                return;
            }

            var hasTokenKey = !string.IsNullOrEmpty(tokenKey);
            if(!isTestMode && !hasTokenKey) return;

            var invalidConfigMessage =
                "[VivoxBuildValidator] Vivox is configured to generate tokens locally (insecure)." +
                "\n- Fix: Edit > Project Settings > Services > Vivox, disable Test Mode." +
                "\n- Fix: Remove/clear the Vivox Token Key from the project settings so the signing key is not present in builds." +
                "\n- Then use server-side tokens (UGS Cloud Code token provider is already wired in VoiceManager)." +
                $"\nDetected: isTestMode={isTestMode}, tokenKeyPresent={hasTokenKey}";

            if(!isDevelopmentBuild) throw new BuildFailedException(invalidConfigMessage);
            Debug.LogWarning(invalidConfigMessage);
        }

        private static bool TryReadVivoxSettings(string settingsText, out bool isTestMode, out string tokenKey) {
            isTestMode = false;
            tokenKey = string.Empty;

            try {
                var root = JObject.Parse(settingsText);
                var dict = root["m_Dictionary"];
                if(dict == null) return false;

                var values = dict["m_DictionaryValues"] as JArray;
                if(values == null) return false;

                foreach(var entry in values) {
                    if(entry == null) continue;

                    var key = entry["key"];
                    if(key == null) continue;

                    var keyStr = key.Value<string>();
                    if(string.IsNullOrEmpty(keyStr)) continue;

                    switch(keyStr) {
                        case "isTestMode":
                            isTestMode = ReadWrappedBool(entry["value"]);
                            break;
                        case "tokenKey":
                            tokenKey = ReadWrappedString(entry["value"]);
                            break;
                    }
                }
                return true;
            } catch(Exception e) {
                Debug.LogWarning($"[VivoxBuildValidator] Failed to parse Vivox settings. Exception: {e.Message}");
                return false;
            }
        }

        private static string ReadWrappedString(JToken valueToken) {
            if(valueToken == null) return string.Empty;

            var raw = valueToken.Value<string>();
            if(string.IsNullOrEmpty(raw)) return string.Empty;

            try {
                var inner = JObject.Parse(raw);
                var innerValue = inner["m_Value"];
                if(innerValue == null) return string.Empty;
                var s = innerValue.Value<string>();
                return string.IsNullOrEmpty(s) ? string.Empty : s;
            } catch {
                return string.Empty;
            }
        }

        private static bool ReadWrappedBool(JToken valueToken) {
            if(valueToken == null) return false;

            var raw = valueToken.Value<string>();
            if(string.IsNullOrEmpty(raw)) return false;

            try {
                var inner = JObject.Parse(raw);
                var innerValue = inner["m_Value"];
                return innerValue != null && innerValue.Value<bool>();
            } catch {
                return false;
            }
        }
    }
}

