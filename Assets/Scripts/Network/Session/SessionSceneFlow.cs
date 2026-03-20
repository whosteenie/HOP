using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Diagnostics;
using Events;
using Network.Contracts;
using Network.Core;
using Network.Steam;
using Unity.Netcode;
using Unity.Services.Lobbies.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network.Session {
    /// <summary>
    /// Handles unexpected disconnect and gameplay readiness (wait for player/menu/timer ready).
    /// Presentation and game-specific behavior are provided via delegates so Network.Session
    /// does not depend directly on Game.* types.
    /// </summary>
    public sealed class SessionSceneFlow {
        // Gameplay readiness (player/menu/match timer)
        private static Func<bool> isLocalPlayerReady;
        private static Func<bool> isGameMenuReady;
        private static Func<bool> isMatchTimerReady;

        // Scene transitions (fade in/out) and availability
        private static Func<bool> hasSceneTransitionProvider;
        private static Func<int, UniTask> fadeOutProvider;
        private static Func<int, UniTask> fadeInProvider;

        // Disconnect FP visuals (duplicate / cleanup)
        private static Action<GameObject> captureDisconnectVisuals;
        private static Action cleanupDisconnectVisuals;

        // Audio stop hook used during leave-to-menu flow
        private static Action stopAllAudio;

        // Map selection and lookup
        private static Func<string, (bool ok, string sceneName)> getSceneByMapId;
        private static Func<string, (bool ok, string mapId, string sceneName)> selectRandomSceneForMode;
        private static Func<(string mapId, string sceneName)> getDefaultMap;

        // Main menu readiness
        private static Func<bool> isMainMenuReady;

        // Match timer / scene-presented notification
        // Parameters: isHost, localClientId, reason
        private static Action<bool, ulong, string> notifyScenePresented;

        /// <summary>
        /// Registers providers for gameplay readiness checks.
        /// </summary>
        public static void SetGameplayReadinessProviders(
            Func<bool> localPlayerReadyProvider,
            Func<bool> gameMenuReadyProvider,
            Func<bool> matchTimerReadyProvider) {
            isLocalPlayerReady = localPlayerReadyProvider;
            isGameMenuReady = gameMenuReadyProvider;
            isMatchTimerReady = matchTimerReadyProvider;
        }

        /// <summary>
        /// Registers providers for scene transition availability and fade in/out behavior.
        /// </summary>
        public static void SetSceneTransitionProviders(
            Func<bool> hasSceneTransitionHook,
            Func<int, UniTask> fadeOutHook,
            Func<int, UniTask> fadeInHook) {
            hasSceneTransitionProvider = hasSceneTransitionHook;
            fadeOutProvider = fadeOutHook;
            fadeInProvider = fadeInHook;
        }

        /// <summary>
        /// Registers providers for disconnect FP visuals (duplicate / cleanup).
        /// </summary>
        public static void SetDisconnectVisualProviders(
            Action<GameObject> captureDisconnectVisualsHook,
            Action cleanupDisconnectVisualsHook) {
            captureDisconnectVisuals = captureDisconnectVisualsHook;
            cleanupDisconnectVisuals = cleanupDisconnectVisualsHook;
        }

        /// <summary>
        /// Registers a provider used to stop all game audio during leave-to-menu flows.
        /// </summary>
        public static void SetStopAllAudioProvider(Action stopAllAudioHook) {
            stopAllAudio = stopAllAudioHook;
        }

        /// <summary>
        /// Registers providers for map lookup and selection for the current mode.
        /// </summary>
        public static void SetMapSelectionProviders(
            Func<string, (bool ok, string sceneName)> getSceneByMapIdHook,
            Func<string, (bool ok, string mapId, string sceneName)> selectRandomSceneForModeHook,
            Func<(string mapId, string sceneName)> getDefaultMapHook) {
            getSceneByMapId = getSceneByMapIdHook;
            selectRandomSceneForMode = selectRandomSceneForModeHook;
            getDefaultMap = getDefaultMapHook;
        }

        /// <summary>
        /// Registers a provider that reports whether the main menu is fully initialized.
        /// </summary>
        public static void SetMainMenuReadyProvider(Func<bool> isMainMenuReadyHook) {
            isMainMenuReady = isMainMenuReadyHook;
        }

        /// <summary>
        /// Registers a notifier used to inform the match timer that the game scene has been presented.
        /// </summary>
        public static void SetScenePresentedNotifier(Action<bool, ulong, string> notifyScenePresentedHook) {
            notifyScenePresented = notifyScenePresentedHook;
        }
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
                if(evt == null) return;

                var nm = NetworkManager.Singleton;
                if(nm == null || evt.ClientId != nm.LocalClientId) return;
                if(isLocalPlayerReady == null || isLocalPlayerReady()) {
                    latch.SignalLocalPlayerReady();
                }
            }

            void OnGameMenuReady(GameMenuReadyEvent _) {
                if(isGameMenuReady == null || isGameMenuReady()) {
                    latch.SignalGameMenuReady();
                }
            }

            void OnMatchTimerReady(MatchTimerReadyEvent _) {
                if(isMatchTimerReady == null || isMatchTimerReady()) {
                    latch.SignalMatchTimerReady();
                }
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

            if(isLocalPlayerReady != null && isLocalPlayerReady())
                latch.SignalLocalPlayerReady();
            if(isGameMenuReady != null && isGameMenuReady())
                latch.SignalGameMenuReady();
            if(isMatchTimerReady != null && isMatchTimerReady())
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
        public void TriggerDisconnectFlow(ISessionContext ctx, ISceneFlowActions actions, string source) {
#if UNITY_EDITOR
            if(!Application.isPlaying) return;
#endif
            if(_unexpectedDisconnectInFlight || ctx.IsLeaving || ctx.IsShuttingDown) return;
            if(SessionNetworkLifecycle.IsDaStartupInFlight) {
                if(Debug.isDebugBuild) {
                    DevLog.Log($"[SessionManager] Suppressed unexpected disconnect flow during DA startup ({source}).");
                }
                return;
            }

            _unexpectedDisconnectInFlight = true;
            ctx.LaunchSessionTask(HandleUnexpectedDisconnect(ctx, actions, source),
                $"UnexpectedDisconnect/{source}");
        }

        private async UniTask HandleUnexpectedDisconnect(ISessionContext ctx, ISceneFlowActions actions, string source) {
#if UNITY_EDITOR
            if(!Application.isPlaying) return;
#endif
            var currentScene = actions.GetActiveSceneName();
            if(Debug.isDebugBuild) {
                DevLog.Log($"[SessionManager] HandleUnexpectedDisconnect source={source} scene={currentScene}");
            }

            FlowLog.Emit(FlowEventIds.SessionExit,
                ("reason", "UnexpectedDisconnect"),
                ("phase", ctx.Phase),
                ("gameplay", ctx.IsInGameplay));

            try {
                actions.SetFrontStatus(SessionPhase.Error, "Disconnected from party.");

                if(currentScene != "MainMenu") {
                    actions.CaptureDisconnectFpVisuals();
                    await actions.FadeOutWithFallbackAsync();
                    await actions.LeaveToMainMenuAsync(skipFadeOut: true);
                } else {
                    if(Debug.isDebugBuild) {
                        DevLog.Log("[SessionManager] HandleUnexpectedDisconnect: already in MainMenu, skipping capture");
                    }
                    await actions.LeaveToMainMenuAsync();
                }
            } finally {
                _unexpectedDisconnectInFlight = false;
            }
        }

        /// <summary>Fade out via game-provided transition or delay. Used by SessionManager and disconnect flow.</summary>
        public static async UniTask FadeOutWithFallbackAsync(int fallbackDelayMs = 500) {
            if(fadeOutProvider != null) {
                await fadeOutProvider(fallbackDelayMs);
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
            // Only run pre-fade when the game has an actual scene transition system.
            if(hasSceneTransitionProvider == null || hasSceneTransitionProvider() == false) return;
            setHostPreFadedOut?.Invoke(true);
            ctx.SetFrontStatus(SessionPhase.SynchronizingLoad, "Waiting for party...");
            await FadeOutWithFallbackAsync();
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
                    if(Debug.isDebugBuild) DevLog.LogError("[SessionManager] Failed to start offline host after cleanup.");
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
        public static void CaptureDisconnectFpVisuals(ISessionContext ctx) {
            if(Debug.isDebugBuild) DevLog.Log("[SessionManager] CaptureDisconnectFpVisuals called");
            if(!ctx.TryGetNetworkManager("CaptureFp", out var networkManager) || networkManager.LocalClient == null) {
                if(Debug.isDebugBuild) DevLog.Log("[SessionManager] CaptureFp: early out nm or LocalClient null");
                return;
            }
            if(captureDisconnectVisuals == null) return;
            var playerObject = networkManager.LocalClient.PlayerObject;
            if(playerObject == null) {
                if(Debug.isDebugBuild)
                    DevLog.Log("[SessionManager] CaptureFp: early out playerObject null (despawned?)");
                return;
            }
            captureDisconnectVisuals(playerObject.gameObject);
        }

        /// <summary>Fade in via game-provided transition or delay.</summary>
        private static async UniTask FadeInWithFallbackAsync(int fallbackDelayMs = 500) {
            if(fadeInProvider != null) {
                await fadeInProvider(fallbackDelayMs);
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

            stopAllAudio?.Invoke();

            var currentScene = actions.GetActiveSceneName();
            var shouldFade = currentScene != "MainMenu";
            var shouldRevealMenu = shouldFade || string.Equals(currentScene, "MainMenu", StringComparison.OrdinalIgnoreCase);
            FlowLog.Emit(FlowEventIds.SessionExit,
                ("leaveId", leaveId),
                ("step", "EXIT_SCENE_SNAPSHOT"),
                ("currentScene", currentScene),
                ("shouldFade", shouldFade),
                ("shouldRevealMenu", shouldRevealMenu));

            if(skipFadeOut)
                cleanupDisconnectVisuals?.Invoke();

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
            await actions.EnsureMainMenuReadyAsync(currentScene);
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
        /// Waits until the main menu is ready (as reported by the game-provided provider).
        /// </summary>
        private static async UniTask<bool> WaitForMainMenuReadyAsync(float timeoutSeconds,
            CancellationToken cancellationToken) {
            var start = Time.realtimeSinceStartup;
            while(Time.realtimeSinceStartup - start < timeoutSeconds) {
                if(cancellationToken.IsCancellationRequested) return false;
                if(isMainMenuReady != null && isMainMenuReady()) return true;
                await UniTask.DelayFrame(1, cancellationToken: cancellationToken);
            }
            return false;
        }

        /// <summary>
        /// Loads MainMenu scene and waits for it to be active and MainMenuManager ready. Used by leave-to-menu flow.
        /// </summary>
        public static async UniTask EnsureMainMenuReadyAsync(ISessionContext ctx, string currentScene) {
#if UNITY_EDITOR
            if(!Application.isPlaying) return;
#endif
            if(currentScene == "MainMenu") return;
            SceneManager.LoadScene("MainMenu");
            var sceneLoaded = await WaitForActiveSceneAsync("MainMenu", 15f, ctx.SessionLifetimeToken);
            if(!sceneLoaded)
                DevLog.LogWarning("[SessionManager] Timed out waiting for MainMenu scene activation during leave flow.");
            var menuReady = await WaitForMainMenuReadyAsync(15f, ctx.SessionLifetimeToken);
            if(!menuReady)
                DevLog.LogWarning(
                    "[SessionManager] Timed out waiting for MainMenuManager initialization during leave flow.");
        }

        /// <summary>
        /// Sets the map from a private match draft (map id). Resolves scene name, sets map on host actions, and marks preset. Call before starting private match.
        /// </summary>
        public static void RunSetSelectedMapFromId(ISessionContext ctx, IHostMapSceneActions hostMapActions, string mapId) {
            if(ctx == null || hostMapActions == null || string.IsNullOrWhiteSpace(mapId)) return;
            if(getSceneByMapId == null) return;
            var (ok, sceneName) = getSceneByMapId(mapId);
            if(!ok) return;
            hostMapActions.SetSelectedMap(mapId, sceneName);
            ctx.SetPrivateMatchMapPreset(true);
            if(Debug.isDebugBuild)
                DevLog.Log($"[SessionManager] Private match map set: mapId='{mapId}' scene='{sceneName}'.");
        }

        /// <summary>
        /// Selects map for current mode (preset from private draft, random for gamemode, or default) and updates Steam lobby if owner.
        /// </summary>
        private static void SelectMapForHost(ISessionContext ctx, IHostMapSceneActions actions, string context) {
            var usedPreset = actions.ConsumePrivateMatchMapPreset();
            if(usedPreset) {
                if(Debug.isDebugBuild) {
                    DevLog.Log(
                        $"[SessionManager] Using preset private match map ({context}) mapId='{ctx.SelectedMapId}' scene='{ctx.SelectedMapSceneName}'.");
                }
            } else if(selectRandomSceneForMode != null) {
                var (ok, mapId, sceneName) = selectRandomSceneForMode(ctx.SelectedGameMode);
                if(ok) {
                    actions.SetSelectedMap(mapId, sceneName);
                } else if(getDefaultMap != null) {
                    var (defaultMapId, defaultScene) = getDefaultMap();
                    actions.SetSelectedMap(defaultMapId, defaultScene);
                }
            } else if(getDefaultMap != null) {
                var (defaultMapId, defaultScene) = getDefaultMap();
                actions.SetSelectedMap(defaultMapId, defaultScene);
            }
            if(Debug.isDebugBuild && !usedPreset) {
                DevLog.Log(
                    $"[SessionManager] Map selected ({context}) mode='{ctx.SelectedGameMode}' mapId='{ctx.SelectedMapId}' scene='{ctx.SelectedMapSceneName}'.");
            }
            actions.SetSteamLobbyMapIfOwner(ctx.SelectedMapId ?? string.Empty, ctx.SelectedMapSceneName ?? string.Empty);
        }

        /// <summary>
        /// Loads the gameplay scene as host: selects map for current mode, sets phase to LoadingScene, loads scene via NetworkManager.
        /// </summary>
        public static bool TryLoadGameplaySceneAsHost(ISessionContext ctx, IHostMapSceneActions actions, string contextLabel) {
            if(!actions.TryGetNetworkManager(contextLabel))
                return false;
            SelectMapForHost(ctx, actions, contextLabel);
            if(string.IsNullOrWhiteSpace(ctx.SelectedMapSceneName)) {
                DevLog.LogError(
                    $"[SessionManager] Cannot load gameplay scene: SelectedMapSceneName is empty after map selection (context='{contextLabel}', mode='{ctx.SelectedGameMode ?? "<null>"}').");
                return false;
            }
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
                    DevLog.LogWarning(
                        "[SessionManager] Game scene loaded without an authoritative mode. Keeping current mode.");
                    FlowLog.Emit(FlowEventIds.SceneLoaded,
                        ("mode", ctx.SelectedGameMode),
                        ("source", "FallbackSelected"));
                }

                actions.TryJoinVoiceForActiveMatch("OnGameSceneLoadedAsync");
                ctx.SetFrontStatus(SessionPhase.InGame, "");

                if(actions.TryGetNetworkManager(out var networkManager) && NetworkAuthority.HasGlobalAuthority(networkManager)) {
                    ctx.SetIsInGameplay(true);
                    actions.EnableGameplaySpawningIfHost();

                    if(actions.IsMatchLobbyPublic()) {
                        await actions.TrySetMatchLobbyStateAsync("InGame",
                            DataObject.VisibilityOptions.Public,
                            "OnGameSceneLoadedAsync");
                        await actions.RefreshBackfillEligibilityAsync(force: true);
                    }
                }

                await actions.UnsubscribeMatchLobbyAsync("OnGameSceneLoadedAsync/InGame");

                var hasSceneTransition = hasSceneTransitionProvider != null && hasSceneTransitionProvider();
                if(hasSceneTransition) {
                    var ready = await WaitForGameplayReadyAsync(ctx, sceneActions, 20f);
                    if(!ready) {
                        DevLog.LogWarning(
                            "[SessionManager] Gameplay readiness timed out before fade-in. Revealing scene to avoid indefinite black screen.");
                    }

                    if(actions.IsCurrentGameScenePresentation(presentationSerial) && !ctx.IsLeaving && !ctx.IsShuttingDown) {
                        await FadeInWithFallbackAsync();
                        if(notifyScenePresented != null && actions.TryGetNetworkManager(out var nm) && nm.IsClient) {
                            var isHost = NetworkAuthority.HasGlobalAuthority(nm);
                            notifyScenePresented(isHost, nm.LocalClientId, "HostLocalFadeIn");
                        }
                    }
                } else if(notifyScenePresented != null && actions.TryGetNetworkManager(out var nm) && nm.IsClient) {
                    var isHost = NetworkAuthority.HasGlobalAuthority(nm);
                    notifyScenePresented(isHost, nm.LocalClientId, "HostNoTransitionManager");
                }
            } catch(Exception ex) {
                Debug.LogException(ex);
            }
        }
    }
}
