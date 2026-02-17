using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.Settings {
    /// <summary>
    /// Re-applies runtime video settings that are scene-local (for example per-scene post-processing volumes).
    /// </summary>
    public static class VideoSettingsRuntimeApplier {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize() {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;

            GameSettings.OnSettingsChanged -= ApplyFromSavedSettings;
            GameSettings.OnSettingsChanged += ApplyFromSavedSettings;

            ApplyFromSavedSettings();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            ApplyFromSavedSettings();
        }

        private static void ApplyFromSavedSettings() {
            var video = GameSettings.Data.video;
            var bloomEnabled = video == null || video.bloomEnabled;
            ApplyBloomEnabled(bloomEnabled);
        }

        public static void ApplyBloomEnabled(bool enabled) {
            var volumes = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for(var i = 0; i < volumes.Length; i++) {
                var volume = volumes[i];
                if(volume == null) continue;

                ApplyBloomEnabled(volume.profile, enabled);
                ApplyBloomEnabled(volume.sharedProfile, enabled);
            }
        }

        private static void ApplyBloomEnabled(VolumeProfile profile, bool enabled) {
            if(profile == null) return;
            if(!profile.TryGet<Bloom>(out var bloom) || bloom == null) return;
            bloom.active = enabled;
        }
    }
}
