using System;
using System.IO;
using Diagnostics;
using Game.Settings;
using UnityEngine;

namespace Game.Progression {
    public static class ProgressionStore {
        private const string FileName = "progression.json";

        public static void Save(PlayerProgressionData data) {
            try {
                var path = Path.Combine(Application.persistentDataPath, FileName);
                var json = JsonUtility.ToJson(data, false);
                var protectedPayload = SecureJsonFile.Encode(path, json);
                File.WriteAllText(path, protectedPayload);
                // DevLog.Log($"[ProgressionStore] Saved to {path}");
            } catch (Exception e) {
                DevLog.LogError($"[ProgressionStore] Failed to save: {e.Message}");
            }
        }

        public static PlayerProgressionData Load() {
            var path = Path.Combine(Application.persistentDataPath, FileName);
            if (!File.Exists(path)) {
                return new PlayerProgressionData();
            }

            try {
                var raw = File.ReadAllText(path);
                var decodeResult = SecureJsonFile.TryDecode(path, raw, out var json);
                if (decodeResult == SecureJsonFile.DecodeResult.InvalidOrTampered) {
                    DevLog.LogWarning("[ProgressionStore] progression.json failed integrity checks. Resetting progression.");
                    QuarantineCorrupt(path);
                    return new PlayerProgressionData();
                }

                var loaded = JsonUtility.FromJson<PlayerProgressionData>(json);
                if (loaded == null) {
                    DevLog.LogWarning("[ProgressionStore] progression.json parsed as null. Resetting progression.");
                    QuarantineCorrupt(path);
                    return new PlayerProgressionData();
                }

                if (decodeResult == SecureJsonFile.DecodeResult.LegacyPlaintext) {
                    Save(loaded); // One-time migration from plaintext progression.
                }

                return loaded;
            } catch (Exception e) {
                DevLog.LogError($"[ProgressionStore] Failed to load, creating new: {e.Message}");
                QuarantineCorrupt(path);
                return new PlayerProgressionData();
            }
        }

        private static void QuarantineCorrupt(string path) {
            if (!File.Exists(path)) return;

            try {
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var dir = Path.GetDirectoryName(path);
                var dstName = $"progression.corrupt.{stamp}.json";
                var dst = string.IsNullOrEmpty(dir) ? dstName : Path.Combine(dir, dstName);
                File.Move(path, dst);
            } catch(Exception e) {
                DevLog.LogWarning($"[ProgressionStore] Failed to quarantine corrupt progression file '{path}': {e.Message}");
            }
        }
    }
}
