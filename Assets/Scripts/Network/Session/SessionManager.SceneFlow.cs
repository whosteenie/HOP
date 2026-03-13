using System;
using Cysharp.Threading.Tasks;
using Game.Match;
using Game.Menu;
using Game.Player;
using Game.Player.Core;
using Network.Core;
using Network.Diagnostics;
using Network.Events;
using Network.Singletons;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network.Session {
    public sealed partial class SessionManager {
        private int _gameScenePresentationSerial;

        private sealed class GameplayReadinessLatch {
            private readonly UniTaskCompletionSource<bool> _completion = new();
            private bool _localPlayerReady;
            private bool _gameMenuReady;
            private bool _matchTimerReady;

            public UniTask<bool> Task => _completion.Task;

            public void SignalLocalPlayerReady() {
                _localPlayerReady = true;
                TryComplete();
            }

            public void SignalGameMenuReady() {
                _gameMenuReady = true;
                TryComplete();
            }

            public void SignalMatchTimerReady() {
                _matchTimerReady = true;
                TryComplete();
            }

            public void Cancel() {
                _completion.TrySetResult(false);
            }

            private void TryComplete() {
                if(!_localPlayerReady || !_gameMenuReady || !_matchTimerReady) return;
                _completion.TrySetResult(true);
            }
        }

        private async UniTask<bool> WaitForGameplayReadyAsync(float timeoutSeconds) {
            if(_isLeaving || _isShuttingDown) {
                return false;
            }

            var activeScene = SceneManager.GetActiveScene();
            if(activeScene.IsValid() == false || IsGameplaySceneName(activeScene.name) == false) {
                if(string.Equals(activeScene.name, "MainMenu", StringComparison.OrdinalIgnoreCase)) {
                    return false;
                }
            }

            var latch = new GameplayReadinessLatch();

            void OnLocalPlayerReady(LocalPlayerReadyEvent evt) {
                var player = evt.Player;
                if(player != null && player.IsOwner && player.IsSpawned) {
                    latch.SignalLocalPlayerReady();
                }
            }

            void OnGameMenuReady(GameMenuReadyEvent _) {
                latch.SignalGameMenuReady();
            }

            void OnMatchTimerReady(MatchTimerReadyEvent _) {
                latch.SignalMatchTimerReady();
            }

            void OnActiveSceneChanged(Scene _, Scene nextScene) {
                if(nextScene.IsValid() && IsGameplaySceneName(nextScene.name)) return;
                latch.Cancel();
            }

            EventBus.Unsubscribe<LocalPlayerReadyEvent>(OnLocalPlayerReady);
            EventBus.Unsubscribe<GameMenuReadyEvent>(OnGameMenuReady);
            EventBus.Unsubscribe<MatchTimerReadyEvent>(OnMatchTimerReady);
            EventBus.Subscribe<LocalPlayerReadyEvent>(OnLocalPlayerReady);
            EventBus.Subscribe<GameMenuReadyEvent>(OnGameMenuReady);
            EventBus.Subscribe<MatchTimerReadyEvent>(OnMatchTimerReady);
            SceneManager.activeSceneChanged += OnActiveSceneChanged;

            if(PlayerController.LocalPlayer != null && PlayerController.LocalPlayer.IsSpawned) {
                latch.SignalLocalPlayerReady();
            }

            if(GameMenuManager.Instance != null) {
                latch.SignalGameMenuReady();
            }

            if(MatchTimerManager.Instance != null) {
                latch.SignalMatchTimerReady();
            }

            var ready = false;
            async UniTask WaitForLatchAsync() {
                ready = await latch.Task;
            }

            try {
                var winner = await UniTask.WhenAny(
                    WaitForLatchAsync(),
                    UniTask.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken: SessionLifetimeToken));

                return winner == 0 && ready;
            } catch(OperationCanceledException) {
                return false;
            } finally {
                EventBus.Unsubscribe<LocalPlayerReadyEvent>(OnLocalPlayerReady);
                EventBus.Unsubscribe<GameMenuReadyEvent>(OnGameMenuReady);
                EventBus.Unsubscribe<MatchTimerReadyEvent>(OnMatchTimerReady);
                SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            if(IsGameplaySceneName(scene.name)) {
                LaunchSessionTask(OnGameSceneLoadedAsync(),
                    "OnGameSceneLoadedAsync");
            }
        }

        private async UniTask OnGameSceneLoadedAsync() {
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
                if(NetworkAuthority.HasGlobalAuthority(_networkManager)) {
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
                        await RefreshPublicMatchBackfillEligibilityAsync(force: true);
                    }
                }

                await UnsubscribeMatchLobbyEventsAsync("OnGameSceneLoadedAsync/InGame");

                if(SceneTransitionManager.Instance != null) {
                    var ready = await WaitForGameplayReadyAsync(20f);
                    if(!ready) {
                        Debug.LogWarning(
                            "[SessionManager] Gameplay readiness timed out before fade-in. Revealing scene to avoid indefinite black screen.");
                    }

                    if(presentationSerial == _gameScenePresentationSerial && !_isLeaving && !_isShuttingDown) {
                        await SceneTransitionManager.Instance.FadeInAsync();
                        if(MatchTimerManager.Instance != null && _networkManager != null && _networkManager.IsClient) {
                            if(NetworkAuthority.HasGlobalAuthority(_networkManager)) {
                                MatchTimerManager.Instance.MarkClientScenePresented(_networkManager.LocalClientId,
                                    "HostLocalFadeIn");
                            } else {
                                MatchTimerManager.Instance.ReportClientScenePresentedServerRpc();
                            }
                        }
                    }
                } else if(MatchTimerManager.Instance != null && _networkManager != null && _networkManager.IsClient) {
                    if(NetworkAuthority.HasGlobalAuthority(_networkManager)) {
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
            if(!NetworkAuthority.HasGlobalAuthority(_networkManager)) return;
            NotifyPartyStateChanged();
        }

        private void OnClientDisconnected(ulong clientId) {
            if(_networkManager == null) return;

            if(clientId != _networkManager.LocalClientId) {
                NotifyPartyStateChanged();
                return;
            }

            if(!IsExpectedDisconnect) {
                Debug.Log("[SessionManager] Unexpected local disconnect.");
                TriggerUnexpectedDisconnectFlow("OnClientDisconnected");
            } else {
                IsExpectedDisconnect = false;
            }

            NotifyPartyStateChanged();
        }

        /// <summary>
        /// Backup: when client is fully stopped (e.g. host left, OnClientDisconnectCallback didn't fire).
        /// Only triggers if we didn't expect the disconnect and aren't already leaving.
        /// </summary>
        private void OnClientStopped(bool _) {
            if(IsExpectedDisconnect || _isLeaving) return;
            if(_networkManager != null && _networkManager.IsListening) return;

            if(_networkManager != null && _activeMultiplayerSession != null) {
                LaunchSessionTask(VerifyDistributedAuthorityStopAsync(), "DistributedAuthority/VerifyClientStopped");
                return;
            }

            Debug.Log("[SessionManager] Client stopped unexpectedly. Sending to main menu.");
            TriggerUnexpectedDisconnectFlow("OnClientStopped");
        }

        private async UniTask VerifyDistributedAuthorityStopAsync() {
            await UniTask.Delay(TimeSpan.FromSeconds(3));

            if(IsExpectedDisconnect || _isLeaving || _isShuttingDown) {
                return;
            }

            if(_networkManager != null && _networkManager.IsListening) {
                if(Debug.isDebugBuild) {
                    Debug.Log("[SessionManager] DA client stop recovered during grace period.");
                }
                return;
            }

            Debug.Log("[SessionManager] DA client remained stopped after migration grace period. Sending to main menu.");
            TriggerUnexpectedDisconnectFlow("OnClientStopped/DistributedAuthority");
        }

        private void OnSessionOwnerPromoted(ulong sessionOwnerPromoted) {
            if(_networkManager == null || !_networkManager.DistributedAuthorityMode) {
                return;
            }

            if(Debug.isDebugBuild) {
                Debug.Log(
                    $"[SessionManager] Session owner promoted to client {sessionOwnerPromoted}. LocalSessionOwner={_networkManager.LocalClient != null && _networkManager.LocalClient.IsSessionOwner}");
            }

            if(_networkManager.IsListening && !_isLeaving && !_isShuttingDown && IsGameplaySceneName(SceneManager.GetActiveScene().name)) {
                SetFrontStatus(SessionPhase.InGame, "");
            }

            NotifyPartyStateChanged();
        }

        private void TriggerUnexpectedDisconnectFlow(string source) {
            if(_unexpectedDisconnectInFlight || _isLeaving || _isShuttingDown) {
                return;
            }

            _unexpectedDisconnectInFlight = true;
            LaunchSessionTask(HandleUnexpectedDisconnect(source),
                $"UnexpectedDisconnect/{source}");
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
            if(duplicateShown) return;
            if(Debug.isDebugBuild) Debug.Log("[SessionManager] Disconnect: duplicate failed, using HideFpVisuals fallback");
            playerController.HideFpVisualsForDisconnectTransition();
        }

        /// <summary>
        /// Handles cleanup and recovery after an unexpected network disconnect.
        /// Strict flow: fade to black -> screen black -> teardown/cleanup (hidden) -> main menu -> fade in.
        /// </summary>
        private async UniTask HandleUnexpectedDisconnect(string source) {
            var currentScene = SceneManager.GetActiveScene().name;
            if(Debug.isDebugBuild) {
                Debug.Log(
                    $"[SessionManager] HandleUnexpectedDisconnect source={source} scene={currentScene}");
            }
            FlowLog.Emit(FlowEventIds.SessionExit,
                ("reason", "UnexpectedDisconnect"),
                ("phase", Phase),
                ("gameplay", IsInGameplay));
            try {
                SetFrontStatus(SessionPhase.Error, "Disconnected from party.");

                if(currentScene != "MainMenu") {
                    // 1. Capture duplicate FP visuals (synchronous, before any await) so player sees them during fade
                    CaptureDuplicateFpVisualsForDisconnect();
                    // 2. Client fades to black
                    await FadeOutWithFallbackAsync();
                    // 3. Screen is black -> teardown, cleanup, main menu transition (all while hidden)
                    await LeaveToMainMenuAsync(skipFadeOut: true);
                } else {
                    if(Debug.isDebugBuild) {
                        Debug.Log(
                            "[SessionManager] HandleUnexpectedDisconnect: already in MainMenu, skipping capture");
                    }

                    await LeaveToMainMenuAsync();
                }
            } finally {
                _unexpectedDisconnectInFlight = false;
            }
        }
    }
}
