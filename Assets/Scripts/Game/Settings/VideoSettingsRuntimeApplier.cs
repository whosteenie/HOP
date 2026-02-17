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
            var motionBlurEnabled = video == null || video.motionBlurEnabled;
            var filmGrainEnabled = video == null || video.filmGrainEnabled;
            var vignetteEnabled = video == null || video.vignetteEnabled;

            ApplyBloomEnabled(bloomEnabled);
            ApplyMotionBlurEnabled(motionBlurEnabled);
            ApplyFilmGrainEnabled(filmGrainEnabled);
            ApplyVignetteEnabled(vignetteEnabled);
        }

        public static void ApplyBloomEnabled(bool enabled) {
            ApplyVolumeComponentEnabled<Bloom>(enabled);
        }

        public static void ApplyMotionBlurEnabled(bool enabled) {
            ApplyVolumeComponentEnabled<MotionBlur>(enabled);
        }

        public static void ApplyFilmGrainEnabled(bool enabled) {
            ApplyVolumeComponentEnabled<FilmGrain>(enabled);
        }

        public static void ApplyVignetteEnabled(bool enabled) {
            ApplyVolumeComponentEnabled<Vignette>(enabled);
        }

        private static void ApplyVolumeComponentEnabled<T>(bool enabled) where T : VolumeComponent {
            var volumes = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for(var i = 0; i < volumes.Length; i++) {
                var volume = volumes[i];
                if(volume == null) continue;

                ApplyVolumeComponentEnabled<T>(volume.profile, enabled);
                ApplyVolumeComponentEnabled<T>(volume.sharedProfile, enabled);
            }
        }

        private static void ApplyVolumeComponentEnabled<T>(VolumeProfile profile, bool enabled) where T : VolumeComponent {
            if(profile == null) return;
            if(!profile.TryGet<T>(out var component) || component == null) return;
            component.active = enabled;
        }
    }
}
