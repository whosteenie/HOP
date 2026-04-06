using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using Events;

namespace Game.Settings {
    /// <summary>
    /// Re-applies runtime video settings that are scene-local (for example per-scene post-processing volumes).
    /// </summary>
    public static class VideoSettingsRuntimeApplier {
        private static readonly List<Volume> CachedVolumes = new();
        private static SceneHandle cachedSceneHandle = SceneHandle.None;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize() {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;

            EventBus.Unsubscribe<GameSettingsChangedEvent>(OnGameSettingsChanged);
            EventBus.Subscribe<GameSettingsChangedEvent>(OnGameSettingsChanged);

            ApplySaved();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            ApplySaved();
        }

        private static void OnGameSettingsChanged(GameSettingsChangedEvent _) {
            ApplySaved();
        }

        private static void ApplySaved() {
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
            var volumes = GetSceneVolumes();
            foreach(var volume in volumes) {
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

        private static IReadOnlyList<Volume> GetSceneVolumes() {
            var activeScene = SceneManager.GetActiveScene();
            if(activeScene.IsValid() == false) {
                CachedVolumes.Clear();
                cachedSceneHandle = SceneHandle.None;
                return CachedVolumes;
            }

            if(cachedSceneHandle == activeScene.handle) {
                return CachedVolumes;
            }

            CachedVolumes.Clear();
            cachedSceneHandle = activeScene.handle;
            var roots = activeScene.GetRootGameObjects();
            foreach(var root in roots) {
                if(root == null) continue;
                CachedVolumes.AddRange(root.GetComponentsInChildren<Volume>(true));
            }

            return CachedVolumes;
        }
    }
}
