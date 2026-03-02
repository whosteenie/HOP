using System;
using Cysharp.Threading.Tasks;
using Game.Match;
using Game.Menu;
using Game.Player;
using Network.Diagnostics;
using Network.Singletons;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network {
    public sealed partial class SessionManager {
        private int _gameScenePresentationSerial;

        private async UniTask<bool> WaitForGameplayReadyAsync(float timeoutSeconds) {
            var start = Time.realtimeSinceStartup;
            while(Time.realtimeSinceStartup - start < timeoutSeconds) {
                if(_isLeaving || _isShuttingDown) {
                    return false;
                }

                var activeScene = SceneManager.GetActiveScene();
                if(activeScene.IsValid() == false || IsGameplaySceneName(activeScene.name) == false) {
                    // If we're back in menu, don't keep waiting and spamming a timeout warning.
                    if(string.Equals(activeScene.name, "MainMenu", StringComparison.OrdinalIgnoreCase)) {
                        return false;
                    }

                    await UniTask.Yield();
                    continue;
                }

                if(_networkManager == null) _networkManager = NetworkManager.Singleton;
                if(_networkManager == null || !_networkManager.IsListening || _networkManager.LocalClient == null) {
                    await UniTask.Yield();
                    continue;
                }

                var localPlayer = _networkManager.LocalClient.PlayerObject;
                var localPlayerReady = localPlayer != null && localPlayer.IsSpawned;
                var gameMenuReady = GameMenuManager.Instance != null;
                var timerReady = MatchTimerManager.Instance != null;

                if(localPlayerReady && gameMenuReady && timerReady) {
                    return true;
                }

                await UniTask.Yield();
            }

            return false;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            if(IsGameplaySceneName(scene.name)) {
                OnGameSceneLoadedAsync().Forget();
            }
        }

        private async UniTaskVoid OnGameSceneLoadedAsync() {
            try {
                if(_isLeaving || _isShuttingDown) {
                    if(Debug.isDebugBuild) {
                        FlowLog.Emit(FlowEventIds.SessionExit,
                            ("reason", "LeaveToMainMenu"),
                            ("step", "EXIT_SCENE_PRESENTATION_SKIPPED"));
                    }

                    return;
                }

                var presentationSerial = ++_gameScenePresentationSerial;

                if(TryGetAuthoritativeRuntimeMode(out var mode, out var source)) {
                    if(string.Equals(SelectedGameMode, mode, StringComparison.OrdinalIgnoreCase) == false) {
                        FlowLog.Emit(FlowEventIds.AnomalyModeMismatch,
                            ("selected", SelectedGameMode),
                            ("applied", mode),
                            ("objective", "Unknown"));
                    }

                    ApplyRuntimeMode(mode, $"SceneLoaded/{source}", refreshUi: false);
                    FlowLog.Emit(FlowEventIds.SceneLoaded,
                        ("mode", mode),
                        ("source", source));
                } else {
                    Debug.LogWarning(
                        "[SessionManager] Game scene loaded without an authoritative mode. Keeping current mode.");
                    FlowLog.Emit(FlowEventIds.SceneLoaded,
                        ("mode", SelectedGameMode),
                        ("source", "FallbackSelected"));
                }

                // Always join a match-scoped Vivox channel when gameplay loads so solo private matches
                // retain text chat parity with other match flows.
                TryJoinVoiceForActiveMatch("OnGameSceneLoadedAsync");

                // Mark runtime phase as in-game once gameplay scene is active.
                // Lobby polling paths already respect this phase and will stop polling while playing.
                SetFrontStatus(SessionPhase.InGame, "");

                if(_networkManager == null) _networkManager = NetworkManager.Singleton;
                if(_networkManager != null && _networkManager.IsServer) {
                    IsInGameplay = true;
                    if(_customNetworkManager != null) {
                        _customNetworkManager.EnableGameplaySpawningAndSpawnAll();
                    }

                    // Public lobbies must advertise InGame so queued players can backfill/join in progress.
                    if(_ugsMatchLobby is { Data: not null } &&
                       _ugsMatchLobby.Data.TryGetValue(UgsMatchTypeKey, out var matchTypeObj) &&
                       matchTypeObj != null &&
                       string.Equals(matchTypeObj.Value, "Public", StringComparison.OrdinalIgnoreCase)) {
                        await TrySetMatchLobbyStateAsync("InGame",
                            Unity.Services.Lobbies.Models.DataObject.VisibilityOptions.Public,
                            "OnGameSceneLoadedAsync");
                    }
                }

                if(SceneTransitionManager.Instance != null) {
                    var ready = await WaitForGameplayReadyAsync(20f);
                    if(!ready) {
                        Debug.LogWarning(
                            "[SessionManager] Gameplay readiness timed out before fade-in. Revealing scene to avoid indefinite black screen.");
                    }

                    if(presentationSerial == _gameScenePresentationSerial && !_isLeaving && !_isShuttingDown) {
                        await SceneTransitionManager.Instance.FadeInAsync();
                        if(MatchTimerManager.Instance != null && _networkManager != null && _networkManager.IsClient) {
                            if(_networkManager.IsServer) {
                                MatchTimerManager.Instance.MarkClientScenePresented(_networkManager.LocalClientId,
                                    "HostLocalFadeIn");
                            } else {
                                MatchTimerManager.Instance.ReportClientScenePresentedServerRpc();
                            }
                        }
                    }
                } else if(MatchTimerManager.Instance != null && _networkManager != null && _networkManager.IsClient) {
                    if(_networkManager.IsServer) {
                        MatchTimerManager.Instance.MarkClientScenePresented(_networkManager.LocalClientId,
                            "HostNoTransitionManager");
                    } else {
                        MatchTimerManager.Instance.ReportClientScenePresentedServerRpc();
                    }
                }
            } catch(Exception ex) {
                Debug.LogException(ex);
            }
        }

        private void OnClientConnected(ulong clientId) {
            // Handle connection
            if(!_networkManager.IsServer) return;
            NotifyPartyStateChanged();
        }

        private void OnClientDisconnected(ulong clientId) {
            if(_networkManager == null) return;

            // We disconnected ourselves (LocalClientId), OR server/host disconnected us (ServerClientId)
            var isLocalDisconnect = clientId == _networkManager.LocalClientId;
            var isServerDisconnect = !_networkManager.IsServer && clientId == NetworkManager.ServerClientId;

            if(isLocalDisconnect || isServerDisconnect) {
                if(!_expectedDisconnect) {
                    Debug.Log("[SessionManager] Unexpected Disconnect (Kick or Error).");
                    HandleUnexpectedDisconnect().Forget();
                } else {
                    _expectedDisconnect = false;
                }
            }

            NotifyPartyStateChanged();
        }

        /// <summary>
        /// Backup: when client is fully stopped (e.g. host left, OnClientDisconnectCallback didn't fire).
        /// Only triggers if we didn't expect the disconnect and aren't already leaving.
        /// </summary>
        private void OnClientStopped(bool _) {
            if(_expectedDisconnect || _isLeaving) return;
            if(_networkManager != null && _networkManager.IsServer) return; // Only care when we're a client

            Debug.Log("[SessionManager] Client stopped unexpectedly (e.g. host left). Sending to main menu.");
            HandleUnexpectedDisconnect().Forget();
        }

        /// <summary>
        /// Captures duplicate FP visuals that survive NGO despawn. Player sees duplicate during fade,
        /// then screen is black, then teardown/cleanup (invisible), then main menu.
        /// Falls back to hiding FP visuals if duplicate cannot be created (e.g. holding hopball).
        /// </summary>
        private void CaptureDuplicateFpVisualsForDisconnect() {
            if(Debug.isDebugBuild) Debug.Log("[SessionManager] CaptureDuplicateFpVisualsForDisconnect called");
            if(_networkManager == null || _networkManager.LocalClient == null) {
                if(Debug.isDebugBuild) Debug.Log("[SessionManager] CaptureFp: early out nm or LocalClient null");
                return;
            }
            var playerObject = _networkManager.LocalClient.PlayerObject;
            if(playerObject == null) {
                if(Debug.isDebugBuild) Debug.Log("[SessionManager] CaptureFp: early out playerObject null (despawned?)");
                return;
            }
            var playerController = playerObject.GetComponent<PlayerController>();
            if(playerController == null) {
                if(Debug.isDebugBuild) Debug.Log("[SessionManager] CaptureFp: early out playerController null");
                return;
            }
            if(DisconnectTransitionController.Instance == null && Debug.isDebugBuild)
                Debug.Log("[SessionManager] Disconnect: DisconnectTransitionController.Instance is null");
            var duplicateShown = DisconnectTransitionController.Instance != null &&
                                 DisconnectTransitionController.Instance.CaptureAndShowDuplicateFpVisuals(playerController);
            if(!duplicateShown) {
                if(Debug.isDebugBuild) Debug.Log("[SessionManager] Disconnect: duplicate failed, using HideFpVisuals fallback");
                playerController.HideFpVisualsForDisconnectTransition();
            }
        }

        /// <summary>
        /// Handles cleanup and recovery after an unexpected network disconnect.
        /// Strict flow: fade to black -> screen black -> teardown/cleanup (hidden) -> main menu -> fade in.
        /// </summary>
        private async UniTaskVoid HandleUnexpectedDisconnect() {
            var currentScene = SceneManager.GetActiveScene().name;
            if(Debug.isDebugBuild) Debug.Log($"[SessionManager] HandleUnexpectedDisconnect scene={currentScene}");
            FlowLog.Emit(FlowEventIds.SessionExit,
                ("reason", "UnexpectedDisconnect"),
                ("phase", Phase),
                ("gameplay", IsInGameplay));
            SetFrontStatus(SessionPhase.Error, "Disconnected from party.");

            if(currentScene != "MainMenu") {
                // 1. Capture duplicate FP visuals (synchronous, before any await) so player sees them during fade
                CaptureDuplicateFpVisualsForDisconnect();
                // 2. Client fades to black
                await FadeOutWithFallbackAsync();
                // 3. Screen is black -> teardown, cleanup, main menu transition (all while hidden)
                await LeaveToMainMenuAsync(skipFadeOut: true);
            } else {
                if(Debug.isDebugBuild) Debug.Log("[SessionManager] HandleUnexpectedDisconnect: already in MainMenu, skipping capture");
                LeaveLobby();
                await CleanupNetworkAsync();
                SetFrontStatus(SessionPhase.Menu, "");
            }
        }
    }
}
