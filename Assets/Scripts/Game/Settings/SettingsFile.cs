using System;
using System.IO;
using Game.Security;
using UnityEngine;

namespace Game.Settings {
    public static class SettingsFile {
        private const string FileName = "settings.json";
        private const string BackupFileName = "settings.bak.json";

        private static string GetSettingsPath() {
            return Path.Combine(Application.persistentDataPath, FileName);
        }

        private static string GetBackupPath() {
            return Path.Combine(Application.persistentDataPath, BackupFileName);
        }

        public static bool TryLoad(out SettingsData data) {
            data = null;

            var path = GetSettingsPath();
            if(!File.Exists(path)) {
                return false;
            }

            try {
                var raw = File.ReadAllText(path);
                if(string.IsNullOrWhiteSpace(raw)) {
                    return false;
                }

                var decodeResult = SecureJsonFile.TryDecode(path, raw, out var json);
                if(decodeResult == SecureJsonFile.DecodeResult.InvalidOrTampered) {
                    Debug.LogWarning("[Settings] settings.json failed integrity checks or is unreadable.");
                    return false;
                }

                var loaded = JsonUtility.FromJson<SettingsData>(json);
                if(loaded == null) {
                    return false;
                }

                data = loaded;
                if(decodeResult == SecureJsonFile.DecodeResult.LegacyPlaintext) {
                    Save(data); // One-time migration from plaintext settings.
                }
                return true;
            } catch(Exception e) {
                Debug.LogWarning($"[Settings] Failed to read/parse settings.json: {e.Message}");
                return false;
            }
        }

        public static void Save(SettingsData data) {
            if(data == null) return;

            var path = GetSettingsPath();
            var backupPath = GetBackupPath();
            var tmpPath = path + ".tmp";

            try {
                var dir = Path.GetDirectoryName(path);
                if(dir is { Length: > 0 } && !Directory.Exists(dir)) {
                    Directory.CreateDirectory(dir);
                }

                // Backup previous settings file (best effort).
                if(File.Exists(path)) {
                    File.Copy(path, backupPath, overwrite: true);
                }

                var json = JsonUtility.ToJson(data, prettyPrint: false);
                var protectedPayload = SecureJsonFile.Encode(path, json);
                File.WriteAllText(tmpPath, protectedPayload);

                if(File.Exists(path)) {
                    File.Delete(path);
                }

                File.Move(tmpPath, path);
            } catch(Exception e) {
                Debug.LogError($"[Settings] Failed to save settings.json: {e.Message}");
                // Best effort cleanup.
                try {
                    if(File.Exists(tmpPath)) File.Delete(tmpPath);
                } catch {
                }
            }
        }

        public static void QuarantineCorruptFile() {
            var path = GetSettingsPath();
            if(!File.Exists(path)) return;

            try {
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var dir = Path.GetDirectoryName(path);
                var name = $"settings.corrupt.{stamp}.json";
                var dst = dir is { Length: > 0 }
                    ? Path.Combine(dir, name)
                    : name;
                File.Move(path, dst);
            } catch {
            }
        }
    }
}

