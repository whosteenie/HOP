using Cysharp.Threading.Tasks;
using Game.Audio.System;
using Game.Menu;
using Game.Player.Core;
using Game.UI.Misc;
using Network.Session;
using UnityEngine;

namespace Game.Match {
    /// <summary>
    /// Wires game-specific presentation and readiness behavior into Network.Session.SessionSceneFlow
    /// via delegate providers so that the network stack does not depend on Game.* types.
    /// </summary>
    public sealed class SessionSceneFlowGameAdapter : MonoBehaviour {
        private void Awake() {
            // Gameplay readiness: local player, game menu, match timer.
            SessionSceneFlow.SetGameplayReadinessProviders(
                IsLocalPlayerReady,
                IsGameMenuReady,
                IsMatchTimerReady);

            // Scene transitions (fade in/out) and availability.
            SessionSceneFlow.SetSceneTransitionProviders(
                HasSceneTransitionManager,
                FadeOutWithTransitionsAsync,
                FadeInWithTransitionsAsync);

            // Disconnect FP visuals (duplicate or hide).
            SessionSceneFlow.SetDisconnectVisualProviders(
                CaptureDisconnectVisuals,
                CleanupDisconnectVisuals);

            // Audio stop hook used during leave-to-menu flows.
            SessionSceneFlow.SetStopAllAudioProvider(StopAllAudio);

            // Map selection and lookup for current mode.
            SessionSceneFlow.SetMapSelectionProviders(
                GetSceneByMapId,
                SelectRandomSceneForMode,
                GetDefaultMap);

            // Main menu readiness.
            SessionSceneFlow.SetMainMenuReadyProvider(IsMainMenuReady);

            // Match timer / scene-presented notification.
            SessionSceneFlow.SetScenePresentedNotifier(NotifyScenePresented);
        }

        // ===== Gameplay readiness =====

        private static bool IsLocalPlayerReady() {
            return PlayerController.LocalPlayer != null && PlayerController.LocalPlayer.IsSpawned;
        }

        private static bool IsGameMenuReady() {
            return GameMenuManager.Instance != null;
        }

        private static bool IsMatchTimerReady() {
            return MatchTimerManager.Instance != null;
        }

        // ===== Scene transitions =====

        private static bool HasSceneTransitionManager() {
            return SceneTransitionManager.Instance != null;
        }

        private static async UniTask FadeOutWithTransitionsAsync(int fallbackDelayMs) {
            if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeOutAsync();
            } else {
                await UniTask.Delay(fallbackDelayMs);
            }
        }

        private static async UniTask FadeInWithTransitionsAsync(int fallbackDelayMs) {
            if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeInAsync();
            } else {
                await UniTask.Delay(fallbackDelayMs);
            }
        }

        // ===== Disconnect FP visuals =====

        private static void CaptureDisconnectVisuals(GameObject playerObject) {
            if(playerObject == null) return;

            var playerController = playerObject.GetComponent<PlayerController>();
            if(playerController == null) return;

            if(DisconnectTransitionController.Instance == null) {
                playerController.HideFpVisualsForDisconnectTransition();
                return;
            }

            var duplicateShown = DisconnectTransitionController.Instance
                .CaptureDuplicateFpVisuals(playerController);
            if(duplicateShown) return;

            playerController.HideFpVisualsForDisconnectTransition();
        }

        private static void CleanupDisconnectVisuals() {
            if(DisconnectTransitionController.Instance != null) {
                DisconnectTransitionController.Instance.CleanupDuplicate();
            }
        }

        // ===== Audio =====

        private static void StopAllAudio() {
            if(AudioService.Instance != null) {
                AudioService.Instance.StopAll();
            }
        }

        // ===== Map selection =====

        private static (bool ok, string sceneName) GetSceneByMapId(string mapId) {
            if(string.IsNullOrWhiteSpace(mapId)) return (false, string.Empty);
            return MatchMapService.TryGetSceneByMapId(mapId, out var sceneName)
                ? (true, sceneName)
                : (false, string.Empty);
        }

        private static (bool ok, string mapId, string sceneName) SelectRandomSceneForMode(string modeId) {
            if(MatchMapService.TrySelectRandomScene(modeId, out var sceneName, out var mapId)) {
                return (true, mapId, sceneName);
            }

            return (false, string.Empty, string.Empty);
        }

        private static (string mapId, string sceneName) GetDefaultMap() {
            return (MatchMapService.DefaultMapId, MatchMapService.DefaultGameplaySceneName);
        }

        // ===== Main menu readiness =====

        private static bool IsMainMenuReady() {
            return MainMenuManager.Instance != null;
        }

        // ===== Match timer / scene presented =====

        private static void NotifyScenePresented(bool isHost, ulong localClientId, string reason) {
            if(MatchTimerManager.Instance == null) return;
            if(isHost) {
                MatchTimerManager.Instance.MarkClientScenePresented(localClientId, reason);
            } else {
                MatchTimerManager.Instance.ReportScenePresentedServerRpc();
            }
        }
    }
}

