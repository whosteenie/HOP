using System.Collections.Generic;
using System.IO;
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

        private static MapPoolDefinition _pool;
        private static bool _loaded;
        private static bool _missingPoolWarningLogged;
        private static bool _noCandidatesWarningLogged;

        public static string DefaultGameplaySceneName {
            get {
                EnsureLoaded();
                if(_pool != null && string.IsNullOrWhiteSpace(_pool.FallbackGameplaySceneName) == false) {
                    return _pool.FallbackGameplaySceneName;
                }

                var inferred = InferFallbackGameplaySceneFromBuildSettings();
                return string.IsNullOrWhiteSpace(inferred) ? LegacyFallbackScene : inferred;
            }
        }

        public static string DefaultMapId {
            get {
                EnsureLoaded();
                if(_pool != null && string.IsNullOrWhiteSpace(_pool.FallbackMapId) == false) {
                    return _pool.FallbackMapId;
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
            if(_pool == null || _pool.Maps == null || _pool.Maps.Count == 0) return false;
            for(var i = 0; i < _pool.Maps.Count; i++) {
                var map = _pool.Maps[i];
                if(map == null || string.IsNullOrWhiteSpace(map.SceneName)) continue;
                var id = string.IsNullOrWhiteSpace(map.MapId) ? map.name : map.MapId;
                if(!string.Equals(id, mapId, System.StringComparison.OrdinalIgnoreCase)) continue;
                sceneName = map.SceneName;
                return true;
            }
            return false;
        }

        public static bool TrySelectRandomSceneForGamemode(string gamemodeId, out string sceneName, out string mapId) {
            EnsureLoaded();

            if(_pool == null || _pool.Maps == null || _pool.Maps.Count == 0) {
                if(_missingPoolWarningLogged == false) {
                    _missingPoolWarningLogged = true;
                    Debug.LogWarning(
                        $"[MatchMapService] Map pool not found or empty at Resources/{ResourcePath}. Using fallback gameplay scene.");
                }
                sceneName = DefaultGameplaySceneName;
                mapId = DefaultMapId;
                return false;
            }

            var candidates = ListPool<MapDefinition>.Get();
            var totalWeight = 0;
            for(var i = 0; i < _pool.Maps.Count; i++) {
                var map = _pool.Maps[i];
                if(map == null) continue;
                if(map.EnabledInRotation == false) continue;
                if(string.IsNullOrWhiteSpace(map.SceneName)) continue;
                if(map.SupportsGamemode(gamemodeId) == false) continue;

                candidates.Add(map);
                totalWeight += map.SelectionWeight;
            }

            if(candidates.Count == 0) {
                if(_noCandidatesWarningLogged == false) {
                    _noCandidatesWarningLogged = true;
                    Debug.LogWarning(
                        $"[MatchMapService] No enabled maps support gamemode '{gamemodeId}'. Using fallback gameplay scene.");
                }
                ListPool<MapDefinition>.Release(candidates);
                sceneName = DefaultGameplaySceneName;
                mapId = DefaultMapId;
                return false;
            }

            var roll = Random.Range(0, Mathf.Max(1, totalWeight));
            var index = 0;
            for(var i = 0; i < candidates.Count; i++) {
                index += candidates[i].SelectionWeight;
                if(roll < index) {
                    sceneName = candidates[i].SceneName;
                    mapId = string.IsNullOrWhiteSpace(candidates[i].MapId) ? candidates[i].name : candidates[i].MapId;
                    ListPool<MapDefinition>.Release(candidates);
                    return true;
                }
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
            if(_pool == null || _pool.Maps == null || _pool.Maps.Count == 0) {
                return string.Equals(sceneName, DefaultGameplaySceneName, System.StringComparison.OrdinalIgnoreCase);
            }

            for(var i = 0; i < _pool.Maps.Count; i++) {
                var map = _pool.Maps[i];
                if(map == null) continue;
                if(string.IsNullOrWhiteSpace(map.SceneName)) continue;
                if(string.Equals(map.SceneName, sceneName, System.StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }

            return false;
        }

        public static bool IsYLevelOutOfBoundsKillEnabled(string sceneName) {
            if(string.IsNullOrWhiteSpace(sceneName)) {
                return true;
            }

            EnsureLoaded();
            if(_pool == null || _pool.Maps == null || _pool.Maps.Count == 0) {
                return true;
            }

            for(var i = 0; i < _pool.Maps.Count; i++) {
                var map = _pool.Maps[i];
                if(map == null || string.IsNullOrWhiteSpace(map.SceneName)) continue;
                if(string.Equals(map.SceneName, sceneName, System.StringComparison.OrdinalIgnoreCase) == false) continue;
                return map.UseYLevelOutOfBoundsKill;
            }

            return true;
        }

        public static bool IsTriggerOutOfBoundsKillEnabled(string sceneName) {
            if(string.IsNullOrWhiteSpace(sceneName)) {
                return false;
            }

            EnsureLoaded();
            if(_pool == null || _pool.Maps == null || _pool.Maps.Count == 0) {
                return false;
            }

            for(var i = 0; i < _pool.Maps.Count; i++) {
                var map = _pool.Maps[i];
                if(map == null || string.IsNullOrWhiteSpace(map.SceneName)) continue;
                if(string.Equals(map.SceneName, sceneName, System.StringComparison.OrdinalIgnoreCase) == false) continue;
                return map.UseTriggerOutOfBoundsKill;
            }

            return false;
        }

        private static void EnsureLoaded() {
            if(_loaded) return;
            _loaded = true;
            _pool = Resources.Load<MapPoolDefinition>(ResourcePath);
        }

        private static string InferFallbackGameplaySceneFromBuildSettings() {
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
                if(Pool.Count > 0) {
                    var list = Pool.Pop();
                    list.Clear();
                    return list;
                }

                return new List<T>(16);
            }

            public static void Release(List<T> list) {
                list.Clear();
                Pool.Push(list);
            }
        }
    }
}
