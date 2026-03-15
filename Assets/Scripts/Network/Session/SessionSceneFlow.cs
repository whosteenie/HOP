using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Match;
using Game.Menu;
using Game.Player.Core;
using Network.Core;
using Network.Diagnostics;
using Network.Events;
using Network.Singletons;
using Network.Steam;
using Unity.Services.Lobbies.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network.Session {
    /// <summary>
    /// Handles unexpected disconnect and gameplay readiness (wait for player/menu/timer ready).
    /// </summary>
    public sealed class SessionSceneFlow {
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

        /// <summary>
        /// Waits for local player, game menu, and match timer to be ready (with timeout).
        /// Call from SessionManager after gameplay scene loads.
        /// </summary>
        private static async UniTask<bool> WaitForGameplayReadyAsync(ISessionContext ctx, ISceneFlowActions actions, float timeoutSeconds) {
            if(ctx.IsLeaving || ctx.IsShuttingDown) return false;

            var activeScene = SceneManager.GetActiveScene();
            if(!activeScene.IsValid() || !actions.IsGameplaySceneName(activeScene.name)) {
                if(string.Equals(activeScene.name, "MainMenu", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            var latch = new GameplayReadinessLatch();

            void OnLocalPlayerReady(LocalPlayerReadyEvent evt) {
                var player = evt.Player;
                if(player != null && player.IsOwner && player.IsSpawned)
                    latch.SignalLocalPlayerReady();
            }

            void OnGameMenuReady(GameMenuReadyEvent _) {
                latch.SignalGameMenuReady();
            }

            void OnMatchTimerReady(MatchTimerReadyEvent _) {
                latch.SignalMatchTimerReady();
            }

            void OnActiveSceneChanged(Scene _, Scene nextScene) {
                if(nextScene.IsValid() && actions.IsGameplaySceneName(nextScene.name)) return;
                latch.Cancel();
            }

            EventBus.Unsubscribe<LocalPlayerReadyEvent>(OnLocalPlayerReady);
            EventBus.Unsubscribe<GameMenuReadyEvent>(OnGameMenuReady);
            EventBus.Unsubscribe<MatchTimerReadyEvent>(OnMatchTimerReady);
            EventBus.Subscribe<LocalPlayerReadyEvent>(OnLocalPlayerReady);
            EventBus.Subscribe<GameMenuReadyEvent>(OnGameMenuReady);
            EventBus.Subscribe<MatchTimerReadyEvent>(OnMatchTimerReady);
            SceneManager.activeSceneChanged += OnActiveSceneChanged;

            if(PlayerController.LocalPlayer != null && PlayerController.LocalPlayer.IsSpawned)
                latch.SignalLocalPlayerReady();
            if(GameMenuManager.Instance != null)
                latch.SignalGameMenuReady();
            if(MatchTimerManager.Instance != null)
                latch.SignalMatchTimerReady();

            var ready = false;
            async UniTask WaitForLatchAsync() {
                ready = await latch.Task;
            }

            try {
                var winner = await UniTask.WhenAny(
                    WaitForLatchAsync(),
                    UniTask.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken: ctx.SessionLifetimeToken));
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

        private bool _unexpectedDisconnectInFlight;

        /// <summary>
        /// Call when local client disconnects unexpectedly (e.g. from OnClientDisconnected / OnClientStopped).
        /// Launches HandleUnexpectedDisconnect if not already in progress.
        /// </summary>
        public void TriggerUnexpectedDisconnectFlow(ISessionContext ctx, ISceneFlowActions actions, string source) {
            if(_unexpectedDisconnectInFlight || ctx.IsLeaving || ctx.IsShuttingDown) return;
            if(SessionNetworkLifecycle.IsDistributedAuthorityStartupInFlight) {
                if(Debug.isDebugBuild) {
                    Debug.Log($"[SessionManager] Suppressed unexpected disconnect flow during DA startup ({source}).");
                }
                return;
            }

            _unexpectedDisconnectInFlight = true;
            ctx.LaunchSessionTask(HandleUnexpectedDisconnect(ctx, actions, source),
                $"UnexpectedDisconnect/{source}");
        }

        private async UniTask HandleUnexpectedDisconnect(ISessionContext ctx, ISceneFlowActions actions, string source) {
            var currentScene = actions.GetActiveSceneName();
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] HandleUnexpectedDisconnect source={source} scene={currentScene}");
            }

            FlowLog.Emit(FlowEventIds.SessionExit,
                ("reason", "UnexpectedDisconnect"),
                ("phase", ctx.Phase),
                ("gameplay", ctx.IsInGameplay));

            try {
                actions.SetFrontStatus(SessionPhase.Error, "Disconnected from party.");

                if(currentScene != "MainMenu") {
                    CaptureDuplicateFpVisualsForDisconnect(ctx);
                    await FadeOutWithFallbackAsync();
                    await actions.LeaveToMainMenuAsync(skipFadeOut: true);
                } else {
                    if(Debug.isDebugBuild) {
                        Debug.Log("[SessionManager] HandleUnexpectedDisconnect: already in MainMenu, skipping capture");
                    }
                    await actions.LeaveToMainMenuAsync();
                }
            } finally {
                _unexpectedDisconnectInFlight = false;
            }
        }

        /// <summary>Fade out via SceneTransitionManager or delay. Used by SessionManager and disconnect flow.</summary>
        public static async UniTask FadeOutWithFallbackAsync(int fallbackDelayMs = 500) {
            if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeOutAsync();
                return;
            }
            await UniTask.Delay(fallbackDelayMs);
        }

        /// <summary>
        /// Pre-fade for private match host: set host-pre-faded flag, phase to SynchronizingLoad, then fade out.
        /// Call from SessionManager before starting private match sync.
        /// </summary>
        public static async UniTask RunPreFadePrivateHostAsync(ISessionContext ctx, Action<bool> setHostPreFadedOut) {
            setHostPreFadedOut?.Invoke(false);
            if(SceneTransitionManager.Instance == null) return;
            setHostPreFadedOut?.Invoke(true);
            ctx.SetFrontStatus(SessionPhase.SynchronizingLoad, "Waiting for party...");
            await SceneTransitionManager.Instance.FadeOutAsync();
        }

        /// <summary>
        /// Pre-fade for public match host: set phase to SynchronizingLoad, fade out, then set host-pre-faded flag.
        /// Call from SessionManager before marking host ready.
        /// </summary>
        public static async UniTask RunPreFadePublicHostAsync(ISessionContext ctx, Action<bool> setHostPreFadedOut) {
            ctx.SetFrontStatus(SessionPhase.SynchronizingLoad, "Waiting for players...");
            await FadeOutWithFallbackAsync();
            setHostPreFadedOut?.Invoke(true);
        }

        /// <summary>
        /// Runs the full offline private match flow: leave Steam/voice, cleanup network, set mode/count, fade out, UTP loopback, apply payload, start host, load scene.
        /// Call from SessionManager.StartOfflinePrivateMatchAsync.
        /// </summary>
        public static async UniTask RunStartOfflinePrivateMatchAsync(
            ISessionContext ctx,
            ILeaveToMenuActions leaveActions,
            IMatchmakerSessionActions matchmakerActions,
            string mode) {
            if(ctx == null || leaveActions == null || matchmakerActions == null) return;
            if(!ctx.TryBeginSessionOperation("StartOfflinePrivateMatchAsync")) return;
            try {
                if(string.IsNullOrEmpty(mode)) return;

                leaveActions.LeaveLobby();
                await leaveActions.TryLeaveVoiceChannelAsync();
                await leaveActions.CleanupNetworkAsync();

                ctx.ApplyRuntimeMode(mode, "OfflinePrivateMatch");
                ctx.SetExpectedGamePlayerCount(1, "OfflinePrivateMatch");
                ctx.SetFrontStatus(SessionPhase.StartingHost, "Starting offline match...");

                await FadeOutWithFallbackAsync(300);

                if(!ctx.TryGetUnityTransport("StartOfflinePrivateMatchAsync", out var networkManager, out var utp)) {
                    ctx.SetFrontStatus(SessionPhase.Error, "Offline networking not configured.");
                    return;
                }

                utp.SetConnectionData("127.0.0.1", 7777);
                networkManager.NetworkConfig.NetworkTransport = utp;

                SessionNetworkLifecycle.ApplyLocalConnectionPayload(ctx, true);
                if(!networkManager.StartHost()) {
                    if(Debug.isDebugBuild) Debug.LogError("[SessionManager] Failed to start offline host after cleanup.");
                    ctx.SetFrontStatus(SessionPhase.Error, "Failed to start offline host.");
                    await FadeInWithFallbackAsync(300);
                    return;
                }

                if(!matchmakerActions.TryLoadGameplaySceneAsHost("StartOfflinePrivateMatchAsync/LoadScene")) {
                    ctx.SetFrontStatus(SessionPhase.Error, "Failed to load offline match scene.");
                    await FadeInWithFallbackAsync(300);
                }
            } finally {
                ctx.EndSessionOperation();
            }
        }

        /// <summary>
        /// Captures duplicate FP visuals that survive NGO despawn for disconnect transition, or hides FP visuals as fallback.
        /// Call from unexpected-disconnect flow before fade/leave.
        /// </summary>
        public static void CaptureDuplicateFpVisualsForDisconnect(ISessionContext ctx) {
            if(Debug.isDebugBuild) Debug.Log("[SessionManager] CaptureDuplicateFpVisualsForDisconnect called");
            if(!ctx.TryGetNetworkManager("CaptureFp", out var networkManager) || networkManager.LocalClient == null) {
                if(Debug.isDebugBuild) Debug.Log("[SessionManager] CaptureFp: early out nm or LocalClient null");
                return;
            }
            var playerObject = networkManager.LocalClient.PlayerObject;
            if(playerObject == null) {
                if(Debug.isDebugBuild)
                    Debug.Log("[SessionManager] CaptureFp: early out playerObject null (despawned?)");
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
            if(Debug.isDebugBuild)
                Debug.Log("[SessionManager] Disconnect: duplicate failed, using HideFpVisuals fallback");
            playerController.HideFpVisualsForDisconnectTransition();
        }

        /// <summary>Fade in via SceneTransitionManager or delay.</summary>
        private static async UniTask FadeInWithFallbackAsync(int fallbackDelayMs = 500) {
            if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeInAsync();
                return;
            }
            await UniTask.Delay(fallbackDelayMs);
        }

        /// <summary>
        /// Runs the full leave-to-menu flow: clear matchmaking, audio, fade, voice leave, party reset, Steam leave, clear match state, network cleanup, load main menu, fade in.
        /// Call from SessionManager after setting _isLeaving and leaveId.
        /// </summary>
        public static async UniTask RunLeaveToMenuFlowAsync(ISessionContext ctx, ILeaveToMenuActions actions, long leaveId, bool skipFadeOut) {
            FlowLog.Emit(FlowEventIds.SessionExit,
                ("leaveId", leaveId),
                ("reason", "LeaveToMainMenu"),
                ("step", "EXIT_BEGIN"),
                ("phase", ctx.Phase),
                ("gameplay", ctx.IsInGameplay),
                ("scene", actions.GetActiveSceneName()));

            await actions.ClearMatchmakingStateAsync();
            FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_MATCHMAKING_CLEARED"));

            if(Game.Audio2.AudioService.Instance != null)
                Game.Audio2.AudioService.Instance.StopAll();

            var currentScene = actions.GetActiveSceneName();
            var shouldFade = currentScene != "MainMenu";
            var shouldRevealMenu = shouldFade || string.Equals(currentScene, "MainMenu", StringComparison.OrdinalIgnoreCase);
            FlowLog.Emit(FlowEventIds.SessionExit,
                ("leaveId", leaveId),
                ("step", "EXIT_SCENE_SNAPSHOT"),
                ("currentScene", currentScene),
                ("shouldFade", shouldFade),
                ("shouldRevealMenu", shouldRevealMenu));

            if(skipFadeOut && DisconnectTransitionController.Instance != null)
                DisconnectTransitionController.Instance.CleanupDuplicate();

            if(shouldFade && !skipFadeOut) {
                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_FADE_OUT_BEGIN"));
                await FadeOutWithFallbackAsync();
                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_FADE_OUT_DONE"));
            }

            FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_VOICE_LEAVE_BEGIN"));
            await actions.TryLeaveVoiceChannelAsync();
            FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_VOICE_LEAVE_DONE"));

            FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_PARTY_FOLLOW_RESET_BEGIN"));
            await actions.ResetPartyFollowStateIfHostAsync();
            FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_PARTY_FOLLOW_RESET_DONE"));

            actions.LeaveLobby();
            await actions.ClearMatchStateAsync();
            if(SteamManager.Instance != null)
                SteamManager.Instance.ClearAvatarCache();

            FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_MATCH_STATE_CLEARED"));
            await actions.CleanupNetworkAsync();
            FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_NETWORK_CLEANUP_DONE"));

            FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_SCENE_LOAD_BEGIN"));
            await actions.EnsureMainMenuLoadedAndReadyAsync(currentScene);
            FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_SCENE_LOAD_DONE"));

            if(shouldRevealMenu) {
                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_FADE_IN_BEGIN"));
                await FadeInWithFallbackAsync();
                FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_FADE_IN_DONE"));
            }

            FlowLog.Emit(FlowEventIds.SessionExit, ("leaveId", leaveId), ("step", "EXIT_FINALIZED"));
        }

        /// <summary>
        /// Waits until the active scene name matches the expected one (with timeout and cancellation).
        /// </summary>
        private static async UniTask<bool> WaitForActiveSceneAsync(string expectedSceneName, float timeoutSeconds,
            CancellationToken cancellationToken) {
            var start = Time.realtimeSinceStartup;
            while(Time.realtimeSinceStartup - start < timeoutSeconds) {
                if(cancellationToken.IsCancellationRequested) return false;
                var activeScene = SceneManager.GetActiveScene();
                if(activeScene.IsValid() && activeScene.name == expectedSceneName) return true;
                await UniTask.DelayFrame(1, cancellationToken: cancellationToken);
            }
            return false;
        }

        /// <summary>
        /// Waits until MainMenuManager.Instance is non-null (with timeout and cancellation).
        /// </summary>
        private static async UniTask<bool> WaitForMainMenuReadyAsync(float timeoutSeconds,
            CancellationToken cancellationToken) {
            var start = Time.realtimeSinceStartup;
            while(Time.realtimeSinceStartup - start < timeoutSeconds) {
                if(cancellationToken.IsCancellationRequested) return false;
                if(MainMenuManager.Instance != null) return true;
                await UniTask.DelayFrame(1, cancellationToken: cancellationToken);
            }
            return false;
        }

        /// <summary>
        /// Loads MainMenu scene and waits for it to be active and MainMenuManager ready. Used by leave-to-menu flow.
        /// </summary>
        public static async UniTask EnsureMainMenuLoadedAndReadyAsync(ISessionContext ctx, string currentScene) {
            if(currentScene == "MainMenu") return;
            SceneManager.LoadScene("MainMenu");
            var sceneLoaded = await WaitForActiveSceneAsync("MainMenu", 15f, ctx.SessionLifetimeToken);
            if(!sceneLoaded)
                Debug.LogWarning("[SessionManager] Timed out waiting for MainMenu scene activation during leave flow.");
            var menuReady = await WaitForMainMenuReadyAsync(15f, ctx.SessionLifetimeToken);
            if(!menuReady)
                Debug.LogWarning(
                    "[SessionManager] Timed out waiting for MainMenuManager initialization during leave flow.");
        }

        /// <summary>
        /// Sets the map from a private match draft (map id). Resolves scene name, sets map on host actions, and marks preset. Call before starting private match.
        /// </summary>
        public static void RunSetSelectedMapFromId(ISessionContext ctx, IHostMapSceneActions hostMapActions, string mapId) {
            if(ctx == null || hostMapActions == null || string.IsNullOrWhiteSpace(mapId)) return;
            if(!MatchMapService.TryGetSceneByMapId(mapId, out var sceneName)) return;
            hostMapActions.SetSelectedMap(mapId, sceneName);
            ctx.SetPrivateMatchMapPreset(true);
            if(Debug.isDebugBuild)
                Debug.Log($"[SessionManager] Private match map set: mapId='{mapId}' scene='{sceneName}'.");
        }

        /// <summary>
        /// Selects map for current mode (preset from private draft, random for gamemode, or default) and updates Steam lobby if owner.
        /// </summary>
        private static void SelectMapForHost(ISessionContext ctx, IHostMapSceneActions actions, string context) {
            var usedPreset = actions.ConsumePrivateMatchMapPreset();
            if(usedPreset) {
                if(Debug.isDebugBuild) {
                    Debug.Log(
                        $"[SessionManager] Using preset private match map ({context}) mapId='{ctx.SelectedMapId}' scene='{ctx.SelectedMapSceneName}'.");
                }
            } else if(MatchMapService.TrySelectRandomScene(ctx.SelectedGameMode, out var sceneName, out var mapId)) {
                actions.SetSelectedMap(mapId, sceneName);
            } else {
                actions.SetSelectedMap(MatchMapService.DefaultMapId, MatchMapService.DefaultGameplaySceneName);
            }
            if(Debug.isDebugBuild && !usedPreset) {
                Debug.Log(
                    $"[SessionManager] Map selected ({context}) mode='{ctx.SelectedGameMode}' mapId='{ctx.SelectedMapId}' scene='{ctx.SelectedMapSceneName}'.");
            }
            actions.SetSteamLobbyMapIfOwner(ctx.SelectedMapId ?? string.Empty, ctx.SelectedMapSceneName ?? string.Empty);
        }

        /// <summary>
        /// Loads the gameplay scene as host: selects map for current mode, sets phase to LoadingScene, loads scene via NetworkManager.
        /// </summary>
        public static bool TryLoadGameplaySceneAsHost(ISessionContext ctx, IHostMapSceneActions actions, string contextLabel) {
            if(!actions.TryGetNetworkManager(contextLabel, out _))
                return false;
            SelectMapForHost(ctx, actions, contextLabel);
            ctx.SetPhase(SessionPhase.LoadingScene);
            actions.LoadScene(ctx.SelectedMapSceneName);
            return true;
        }

        /// <summary>
        /// Runs the full on-game-scene-loaded flow: mode sync, voice join, phase InGame, host spawn, public lobby state/backfill, unsubscribe lobby events, wait for readiness, fade-in, match timer presented.
        /// Call from SessionManager when a gameplay scene has finished loading.
        /// </summary>
        public static async UniTask RunOnGameSceneLoadedAsync(ISessionContext ctx, ISceneFlowActions sceneActions, IOnGameSceneLoadedActions actions) {
            try {
                if(ctx.IsLeaving || ctx.IsShuttingDown) {
                    if(Debug.isDebugBuild) {
                        FlowLog.Emit(FlowEventIds.SessionExit,
                            ("reason", "LeaveToMainMenu"),
                            ("step", "EXIT_SCENE_PRESENTATION_SKIPPED"));
                    }
                    return;
                }

                var presentationSerial = actions.StartGameScenePresentation();

                if(actions.TryGetRuntimeMode(out var mode, out var source)) {
                    if(!string.Equals(ctx.SelectedGameMode, mode, StringComparison.OrdinalIgnoreCase)) {
                        FlowLog.Emit(FlowEventIds.AnomalyModeMismatch,
                            ("selected", ctx.SelectedGameMode),
                            ("applied", mode),
                            ("objective", "Unknown"));
                    }
                    ctx.ApplyRuntimeMode(mode, $"SceneLoaded/{source}", refreshUi: false);
                    FlowLog.Emit(FlowEventIds.SceneLoaded, ("mode", mode), ("source", source));
                } else {
                    Debug.LogWarning(
                        "[SessionManager] Game scene loaded without an authoritative mode. Keeping current mode.");
                    FlowLog.Emit(FlowEventIds.SceneLoaded,
                        ("mode", ctx.SelectedGameMode),
                        ("source", "FallbackSelected"));
                }

                actions.TryJoinVoiceForActiveMatch("OnGameSceneLoadedAsync");
                ctx.SetFrontStatus(SessionPhase.InGame, "");

                if(actions.TryGetNetworkManager(out var networkManager) && NetworkAuthority.HasGlobalAuthority(networkManager)) {
                    ctx.SetIsInGameplay(true);
                    actions.EnableGameplaySpawningAndSpawnAllIfHost();

                    if(actions.IsMatchLobbyPublic()) {
                        await actions.TrySetMatchLobbyStateAsync("InGame",
                            DataObject.VisibilityOptions.Public,
                            "OnGameSceneLoadedAsync");
                        await actions.RefreshBackfillEligibilityAsync(force: true);
                    }
                }

                await actions.UnsubscribeMatchLobbyAsync("OnGameSceneLoadedAsync/InGame");

                if(SceneTransitionManager.Instance != null) {
                    var ready = await WaitForGameplayReadyAsync(ctx, sceneActions, 20f);
                    if(!ready) {
                        Debug.LogWarning(
                            "[SessionManager] Gameplay readiness timed out before fade-in. Revealing scene to avoid indefinite black screen.");
                    }

                    if(actions.IsCurrentGameScenePresentation(presentationSerial) && !ctx.IsLeaving && !ctx.IsShuttingDown) {
                        await SceneTransitionManager.Instance.FadeInAsync();
                        if(MatchTimerManager.Instance != null && actions.TryGetNetworkManager(out var nm) && nm.IsClient) {
                            if(NetworkAuthority.HasGlobalAuthority(nm)) {
                                MatchTimerManager.Instance.MarkClientScenePresented(nm.LocalClientId, "HostLocalFadeIn");
                            } else {
                                MatchTimerManager.Instance.ReportScenePresentedServerRpc();
                            }
                        }
                    }
                } else if(MatchTimerManager.Instance != null && actions.TryGetNetworkManager(out var nm) && nm.IsClient) {
                    if(NetworkAuthority.HasGlobalAuthority(nm)) {
                        MatchTimerManager.Instance.MarkClientScenePresented(nm.LocalClientId, "HostNoTransitionManager");
                    } else {
                        MatchTimerManager.Instance.ReportScenePresentedServerRpc();
                    }
                }
            } catch(Exception ex) {
                Debug.LogException(ex);
            }
        }
    }
}
