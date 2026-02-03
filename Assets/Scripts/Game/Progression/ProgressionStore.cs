using System;
using System.IO;
using UnityEngine;

namespace Game.Progression {
    public static class ProgressionStore {
        private const string FileName = "progression.json";

        public static void Save(PlayerProgressionData data) {
            try {
                var json = JsonUtility.ToJson(data, true);
                var path = Path.Combine(Application.persistentDataPath, FileName);
                File.WriteAllText(path, json);
                // Debug.Log($"[ProgressionStore] Saved to {path}");
            } catch (Exception e) {
                Debug.LogError($"[ProgressionStore] Failed to save: {e.Message}");
            }
        }

        public static PlayerProgressionData Load() {
            var path = Path.Combine(Application.persistentDataPath, FileName);
            if (!File.Exists(path)) {
                return new PlayerProgressionData();
            }

            try {
                var json = File.ReadAllText(path);
                return JsonUtility.FromJson<PlayerProgressionData>(json);
            } catch (Exception e) {
                Debug.LogError($"[ProgressionStore] Failed to load, creating new: {e.Message}");
                return new PlayerProgressionData();
            }
        }
    }
}
