using System;
using Cysharp.Threading.Tasks;
using Game.Match;
using Game.Menu;
using Network.Diagnostics;
using Network.Singletons;
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

                if(_networkManager == null) _networkManager = Unity.Netcode.NetworkManager.Singleton;
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

                if(_networkManager == null) _networkManager = Unity.Netcode.NetworkManager.Singleton;
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
            if(clientId == _networkManager.LocalClientId) {
                // We disconnected
                if(!_expectedDisconnect) {
                    Debug.Log("[SessionManager] Unexpected Disconnect (Kick or Error).");
                    HandleUnexpectedDisconnect().Forget();
                } else {
                    // Reset flag
                    _expectedDisconnect = false;
                }
            }

            NotifyPartyStateChanged();
        }

        /// <summary>
        /// Handles cleanup and recovery after an unexpected network disconnect.
        /// </summary>
        private async UniTaskVoid HandleUnexpectedDisconnect() {
            FlowLog.Emit(FlowEventIds.SessionExit,
                ("reason", "UnexpectedDisconnect"),
                ("phase", Phase),
                ("gameplay", IsInGameplay));
            SetFrontStatus(SessionPhase.Error, "Disconnected from party.");

            var currentScene = SceneManager.GetActiveScene().name;
            if(currentScene != "MainMenu") {
                await LeaveToMainMenuAsync();
            } else {
                LeaveLobby();
                await CleanupNetworkAsync();
                SetFrontStatus(SessionPhase.Menu, "");
            }
        }
    }
}
