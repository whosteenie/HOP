using System.Collections.Generic;
using System.IO;
using Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Match {
    /// <summary>
    /// Runtime map selection + gameplay scene detection service.
    /// Load optional pool from Resources/MatchMapPoolDefinition.
    /// If absent, falls back to legacy "Game" scene behavior.
    /// </summary>
    public static class MatchMapService {
        private const string ResourcePath = "MatchMapPoolDefinition";
        private const string LegacyFallbackScene = "Game";
        private const string LegacyFallbackMapId = "legacy_game";
        private const string MainMenuScene = "MainMenu";
        private const string InitScene = "Init";

        private static MapPoolDefinition pool;
        private static bool loaded;
        private static bool missingPoolWarningLogged;
        private static bool noCandidatesWarningLogged;

        public static string DefaultGameplaySceneName {
            get {
                EnsureLoaded();
                if(pool != null && string.IsNullOrWhiteSpace(pool.FallbackGameplaySceneName) == false) {
                    return pool.FallbackGameplaySceneName;
                }

                var inferred = InferFallbackGameplayScene();
                return string.IsNullOrWhiteSpace(inferred) ? LegacyFallbackScene : inferred;
            }
        }

        public static string DefaultMapId {
            get {
                EnsureLoaded();
                if(pool != null && string.IsNullOrWhiteSpace(pool.FallbackMapId) == false) {
                    return pool.FallbackMapId;
                }

                var inferred = DefaultGameplaySceneName;
                return string.IsNullOrWhiteSpace(inferred) ? LegacyFallbackMapId : inferred.ToLowerInvariant();
            }
        }

        /// <summary>
        /// Gets the gameplay scene name for a given map id (from private match draft).
        /// </summary>
        public static bool TryGetSceneByMapId(string mapId, out string sceneName) {
            sceneName = null;
            if(string.IsNullOrWhiteSpace(mapId)) return false;
            EnsureLoaded();
            if(pool == null || pool.Maps == null || pool.Maps.Count == 0) return false;
            foreach(var map in pool.Maps) {
                if(map == null || string.IsNullOrWhiteSpace(map.SceneName)) continue;
                var id = string.IsNullOrWhiteSpace(map.MapId) ? map.name : map.MapId;
                if(!string.Equals(id, mapId, System.StringComparison.OrdinalIgnoreCase)) continue;
                sceneName = map.SceneName;
                return true;
            }
            return false;
        }

        /// <summary>Selects a random enabled map scene that supports the given gamemode.</summary>
        public static bool TrySelectRandomScene(string gamemodeId, out string sceneName, out string mapId) {
            EnsureLoaded();

            if(pool == null || pool.Maps == null || pool.Maps.Count == 0) {
                if(missingPoolWarningLogged == false) {
                    missingPoolWarningLogged = true;
                    DevLog.LogWarning(
                        $"[MatchMapService] Map pool not found or empty at Resources/{ResourcePath}. Using fallback gameplay scene.");
                }
                sceneName = DefaultGameplaySceneName;
                mapId = DefaultMapId;
                return false;
            }

            var candidates = ListPool<MapDefinition>.Get();
            var totalWeight = 0;
            foreach(var map in pool.Maps) {
                if(map == null) continue;
                if(map.EnabledInRotation == false) continue;
                if(string.IsNullOrWhiteSpace(map.SceneName)) continue;
                if(map.SupportsGamemode(gamemodeId) == false) continue;

                candidates.Add(map);
                totalWeight += map.SelectionWeight;
            }

            if(candidates.Count == 0) {
                if(noCandidatesWarningLogged == false) {
                    noCandidatesWarningLogged = true;
                    DevLog.LogWarning(
                        $"[MatchMapService] No enabled maps support gamemode '{gamemodeId}'. Using fallback gameplay scene.");
                }
                ListPool<MapDefinition>.Release(candidates);
                sceneName = DefaultGameplaySceneName;
                mapId = DefaultMapId;
                return false;
            }

            var roll = Random.Range(0, Mathf.Max(1, totalWeight));
            var index = 0;
            foreach(var t in candidates) {
                index += t.SelectionWeight;
                if(roll >= index) continue;
                sceneName = t.SceneName;
                mapId = string.IsNullOrWhiteSpace(t.MapId) ? t.name : t.MapId;
                ListPool<MapDefinition>.Release(candidates);
                return true;
            }

            // Fallback safety.
            sceneName = candidates[0].SceneName;
            mapId = string.IsNullOrWhiteSpace(candidates[0].MapId) ? candidates[0].name : candidates[0].MapId;
            ListPool<MapDefinition>.Release(candidates);
            return true;
        }

        public static bool IsGameplayScene(string sceneName) {
            if(string.IsNullOrWhiteSpace(sceneName)) return false;
            if(string.Equals(sceneName, MainMenuScene, System.StringComparison.OrdinalIgnoreCase)) return false;
            if(string.Equals(sceneName, InitScene, System.StringComparison.OrdinalIgnoreCase)) return false;

            EnsureLoaded();
            if(pool == null || pool.Maps == null || pool.Maps.Count == 0) {
                return string.Equals(sceneName, DefaultGameplaySceneName, System.StringComparison.OrdinalIgnoreCase);
            }

            foreach(var map in pool.Maps) {
                if(map == null) continue;
                if(string.IsNullOrWhiteSpace(map.SceneName)) continue;
                if(string.Equals(map.SceneName, sceneName, System.StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True if the map for the given scene uses Y-level out-of-bounds kill.</summary>
        public static bool IsOobKillEnabled(string sceneName) {
            if(string.IsNullOrWhiteSpace(sceneName)) {
                return true;
            }

            EnsureLoaded();
            if(pool == null || pool.Maps == null || pool.Maps.Count == 0) {
                return true;
            }

            foreach(var map in pool.Maps) {
                if(map == null || string.IsNullOrWhiteSpace(map.SceneName)) continue;
                if(string.Equals(map.SceneName, sceneName, System.StringComparison.OrdinalIgnoreCase) == false) continue;
                return map.UseYLevelOutOfBoundsKill;
            }

            return true;
        }

        /// <summary>True if the map for the given scene uses trigger-based out-of-bounds kill.</summary>
        public static bool IsTriggerOobKillEnabled(string sceneName) {
            if(string.IsNullOrWhiteSpace(sceneName)) {
                return false;
            }

            EnsureLoaded();
            if(pool == null || pool.Maps == null || pool.Maps.Count == 0) {
                return false;
            }

            foreach(var map in pool.Maps) {
                if(map == null || string.IsNullOrWhiteSpace(map.SceneName)) continue;
                if(string.Equals(map.SceneName, sceneName, System.StringComparison.OrdinalIgnoreCase) == false) continue;
                return map.UseTriggerOutOfBoundsKill;
            }

            return false;
        }

        private static void EnsureLoaded() {
            if(loaded) return;
            loaded = true;
            pool = Resources.Load<MapPoolDefinition>(ResourcePath);
        }

        /// <summary>Infers a fallback gameplay scene from build settings when no map pool is set.</summary>
        private static string InferFallbackGameplayScene() {
            var count = SceneManager.sceneCountInBuildSettings;
            if(count <= 0) {
                return LegacyFallbackScene;
            }

            var firstCandidate = string.Empty;
            for(var i = 0; i < count; i++) {
                var scenePath = SceneUtility.GetScenePathByBuildIndex(i);
                if(string.IsNullOrWhiteSpace(scenePath)) continue;

                var sceneName = Path.GetFileNameWithoutExtension(scenePath);
                if(string.IsNullOrWhiteSpace(sceneName)) continue;
                if(string.Equals(sceneName, MainMenuScene, System.StringComparison.OrdinalIgnoreCase)) continue;
                if(string.Equals(sceneName, InitScene, System.StringComparison.OrdinalIgnoreCase)) continue;

                if(string.IsNullOrWhiteSpace(firstCandidate)) {
                    firstCandidate = sceneName;
                }
            }

            return string.IsNullOrWhiteSpace(firstCandidate) ? LegacyFallbackScene : firstCandidate;
        }

        private static class ListPool<T> {
            private static readonly Stack<List<T>> Pool = new();

            public static List<T> Get() {
                if(Pool.Count <= 0) return new List<T>(16);
                var list = Pool.Pop();
                list.Clear();
                return list;

            }

            public static void Release(List<T> list) {
                list.Clear();
                Pool.Push(list);
            }
        }
    }
}
