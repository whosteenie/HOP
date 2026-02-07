using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Game.Editor {
    /// <summary>
    /// Prevents shipping builds that still have Vivox Test Mode enabled or a Vivox signing key present in project settings.
    /// If either is present, Vivox will generate tokens locally (insecure) and log warnings at runtime.
    /// </summary>
    public sealed class VivoxBuildValidator : IPreprocessBuildWithReport {
        public int callbackOrder { get { return 0; } }

        public void OnPreprocessBuild(BuildReport report) {
            var isDevelopmentBuild = (report.summary.options & BuildOptions.Development) != 0;

            var settingsPath = Path.Combine(Directory.GetCurrentDirectory(),
                "ProjectSettings", "Packages", "com.unity.services.vivox", "Settings.json");

            if(!File.Exists(settingsPath)) {
                var missingSettingsMessage = $"[VivoxBuildValidator] Vivox settings file not found at '{settingsPath}'. Cannot validate Test Mode/token key.";
                if(isDevelopmentBuild) {
                    Debug.LogWarning(missingSettingsMessage);
                    return;
                }
                throw new BuildFailedException(missingSettingsMessage);
            }

            var settingsText = File.ReadAllText(settingsPath);
            if(string.IsNullOrEmpty(settingsText)) {
                var emptySettingsMessage = $"[VivoxBuildValidator] Vivox settings file is empty at '{settingsPath}'. Cannot validate Test Mode/token key.";
                if(isDevelopmentBuild) {
                    Debug.LogWarning(emptySettingsMessage);
                    return;
                }
                throw new BuildFailedException(emptySettingsMessage);
            }

            bool isTestMode;
            string tokenKey;
            var parsed = TryReadVivoxSettings(settingsText, out isTestMode, out tokenKey);
            if(!parsed) {
                var parseFailedMessage = "[VivoxBuildValidator] Failed to parse Vivox settings; cannot validate Test Mode/token key.";
                if(isDevelopmentBuild) {
                    Debug.LogWarning(parseFailedMessage);
                    return;
                }
                throw new BuildFailedException(parseFailedMessage);
            }

            var hasTokenKey = !string.IsNullOrEmpty(tokenKey);
            if(!isTestMode && !hasTokenKey) return;

            var invalidConfigMessage =
                "[VivoxBuildValidator] Vivox is configured to generate tokens locally (insecure)." +
                "\n- Fix: Edit > Project Settings > Services > Vivox, disable Test Mode." +
                "\n- Fix: Remove/clear the Vivox Token Key from the project settings so the signing key is not present in builds." +
                "\n- Then use server-side tokens (UGS Cloud Code token provider is already wired in VoiceManager)." +
                $"\nDetected: isTestMode={isTestMode}, tokenKeyPresent={hasTokenKey}";

            if(isDevelopmentBuild) {
                Debug.LogWarning(invalidConfigMessage);
                return;
            }

            throw new BuildFailedException(invalidConfigMessage);
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

                    if(keyStr == "isTestMode") {
                        isTestMode = ReadUnitySettingsWrappedBool(entry["value"]);
                    } else if(keyStr == "tokenKey") {
                        tokenKey = ReadUnitySettingsWrappedString(entry["value"]);
                    }
                }
                return true;
            } catch(Exception e) {
                Debug.LogWarning($"[VivoxBuildValidator] Failed to parse Vivox settings. Exception: {e.Message}");
                return false;
            }
        }

        private static string ReadUnitySettingsWrappedString(JToken valueToken) {
            if(valueToken == null) return string.Empty;

            var raw = valueToken.Value<string>();
            if(string.IsNullOrEmpty(raw)) return string.Empty;

            try {
                var inner = JObject.Parse(raw);
                var innerValue = inner["m_Value"];
                if(innerValue == null) return string.Empty;
                var s = innerValue.Value<string>();
                if(string.IsNullOrEmpty(s)) return string.Empty;
                return s;
            } catch {
                return string.Empty;
            }
        }

        private static bool ReadUnitySettingsWrappedBool(JToken valueToken) {
            if(valueToken == null) return false;

            var raw = valueToken.Value<string>();
            if(string.IsNullOrEmpty(raw)) return false;

            try {
                var inner = JObject.Parse(raw);
                var innerValue = inner["m_Value"];
                if(innerValue == null) return false;
                return innerValue.Value<bool>();
            } catch {
                return false;
            }
        }
    }
}

