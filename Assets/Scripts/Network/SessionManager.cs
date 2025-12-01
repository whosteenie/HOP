using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Audio;
using Game.Match;
using Game.Menu;
using Game.UI;
using Network.Core;
using Network.Events;
using Network.Relay;
using Network.Singletons;
using Network.UGS;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Multiplayer;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityUtils;

namespace Network {
    // ─────────────────────────────────────────────────────────────────────────────
    // SessionManager
    // Orchestrates high-level multiplayer flows (host/client) by delegating to
    // small services: UGS sessions, Relay, NGO lifecycle, and scene loading.
    // Public API remains: StartSessionAsHost, BeginGameplayAsHostAsync,
    // JoinSessionByCodeAsync, LeaveToMainMenuAsync.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// High-level coordinator for hosting/joining/tearing-down a session.
    /// Delegates low-level work to injected services:
    /// <list type="bullet">
    /// <item><see cref="IUgsSessionService"/> – create/join/leave/delete sessions</item>
    /// <item><see cref="IRelayConnector"/> – Relay allocate/join</item>
    /// <item><see cref="INetworkLifecycle"/> – NGO shutdown, cache clearing, scene hooks</item>
    /// <item><see cref="ISceneCoordinator"/> – server-driven scene transitions</item>
    /// <item><see cref="IPlayerIdentity"/> – player name/properties</item>
    /// </list>
    /// Exposes events for UI updates (PlayersChanged, RelayCodeAvailable).
    /// </summary>
    public sealed class SessionManager : Singleton<SessionManager> {
        private enum SessionPhase {
            CreatingLobby,
            LobbyReady,
            AllocatingRelay,
            WaitingForRelay,
            ConnectingToRelay,
            StartingHost,
            LoadingScene,
            Connected,
            ReturningToMenu,
            Error
        }

        // ===== Public surface =====
        public ISession ActiveSession { get; private set; }
        public event Action<IReadOnlyList<IReadOnlyPlayer>> PlayersChanged;
        public event Action<string> RelayCodeAvailable;

        // ===== Config/State =====
        private const string GameSceneName = "Game";
        private const string PlayerNameKey = "playerName";
        private const string RelayCodeKey = "relayCode";

        private bool _loadingGameScene;
        private bool _isLeaving;
        private string _relayJoinCode;
        private Allocation _hostAllocation;
        private static bool startingClient;
        private CancellationTokenSource _lobbyWatchCts;
        private static NetworkManager networkManager;
        private int _expectedLobbyCount;

        // ===== Services =====
        private readonly IUgsSessionService _ugs = new UgsSessionService();
        private readonly INetworkLifecycle _net = new NetworkLifecycle();
        private readonly IPlayerIdentity _ids = new PlayerIdentity();
        private readonly IRelayConnector _relay = new RelayConnector();
        private readonly ISceneCoordinator _scenes = new SceneCoordinator();

        public event Action<string> FrontStatusChanged;
        public event Action<string> SessionJoined;
        private SessionPhase Phase { get; set; }

        private readonly List<ulong> _clientsFinishedLoading = new();
        private CustomNetworkManager _customNetworkManager;

        private bool _hasCompletedInitialLoad; // Add to track if host has done initial load

        public bool IsInGameplay { get; private set; }

        public event Action HostDisconnected; // UI: show message
        public event Action LobbyReset; // UI: clear player list, reset labels

        #region State Management & Helpers

        private void SetFrontStatus(SessionPhase phase, string message) {
            Phase = phase;
            if(FrontStatusChanged != null) {
                FrontStatusChanged.Invoke(message);
            }
        }

        private void StopWatchingLobby() {
            try {
                if(_lobbyWatchCts != null) {
                    _lobbyWatchCts.Cancel();
                }
            } catch { /* ignore */
            }

            if(_lobbyWatchCts != null) {
                _lobbyWatchCts.Dispose();
            }
            _lobbyWatchCts = null;
        }

        /// <summary>
        /// Resets all session state to a clean slate. Called after leaving sessions or errors.
        /// Includes: StopWatchingLobby, UnhookSessionEvents, clear ActiveSession, reset flags, etc.
        /// </summary>
        private void ResetSessionState() {
            StopWatchingLobby();
            _net.UnhookSceneCallbacks(OnNetworkSceneLoadComplete);
            _loadingGameScene = false;
            _relayJoinCode = null;
            _hostAllocation = null;
            _clientsFinishedLoading.Clear();
            _expectedLobbyCount = 0;
            IsInGameplay = false;
            _hasCompletedInitialLoad = false;
            startingClient = false;

            // Clear spawner state
            if(networkManager != null) {
                var cnm = networkManager.GetComponent<CustomNetworkManager>();
                if(cnm != null)
                    cnm.ResetSpawningState();
            }

            // Unhook and clear session
            if(ActiveSession != null) {
                UnhookSessionEvents();
                ActiveSession = null;
            }

            // Clear player list in UI
            if(PlayersChanged != null) {
                PlayersChanged.Invoke(new List<IReadOnlyPlayer>());
            }
        }

        /// <summary>
        /// Ensures clean state before starting a new session. Leaves existing session, cleans network, resets state.
        /// </summary>
        private async UniTask EnsureCleanStateForNewSession() {
            // Leave existing session if present
            if(ActiveSession != null) {
                try {
                    await _ugs.LeaveOrDeleteAsync(ActiveSession);
                } catch {
                    // Ignore errors - session might already be gone
                }
            }

            // Ensure network is fully shut down
            if(networkManager != null && networkManager.IsListening) {
                await _net.CleanupNetworkAsync();
            }

            // Reset all state (includes StopWatchingLobby, UnhookSessionEvents, clearing ActiveSession, etc.)
            ResetSessionState();

            // Re-register callbacks after cleanup
            RegisterNetworkCallbacks();
        }

        /// <summary>Gets NetworkManager, trying Singleton if cached reference == null.</summary>
        private static NetworkManager GetNetworkManager() {
            return networkManager = NetworkManager.Singleton;
        }

        /// <summary>
        /// Waits for a scene to finish loading asynchronously.
        /// </summary>
        private static async UniTask WaitForSceneLoadAsync(string sceneName) {
            var scene = SceneManager.GetSceneByName(sceneName);
            if(scene.IsValid() && scene.isLoaded) {
                return; // Already loaded
            }

            // Wait for scene to load (max 10 seconds timeout)
            var elapsed = 0f;
            while(elapsed < 10f) {
                scene = SceneManager.GetSceneByName(sceneName);
                if(scene.IsValid() && scene.isLoaded) {
                    return;
                }

                await UniTask.Yield();
                elapsed += Time.unscaledDeltaTime;
            }

            Debug.LogWarning($"[SessionManager] Timeout waiting for scene {sceneName} to load");
        }

        #endregion

        #region Unity Lifecycle

        protected override void Awake() {
            if(HasInstance && Instance != this) {
                Debug.LogWarning(
                    "[SessionManager] Duplicate SessionManager instance detected - destroying duplicate (keeping existing instance)");
                Destroy(gameObject);
                return;
            }

            // NEVER destroy the SessionManager - it must persist across scenes
            DontDestroyOnLoad(gameObject);

            networkManager = NetworkManager.Singleton;
            if(networkManager != null) {
                _customNetworkManager = networkManager.GetComponent<CustomNetworkManager>();
            } else {
                Debug.LogWarning("[SessionManager] NetworkManager.Singleton == null in Awake");
            }
        }

        // Cache scene name to avoid string allocations
        private string _cachedSceneName;

        private void OnEnable() {
            RegisterNetworkCallbacks();

            // Cache scene name to avoid allocations
            UpdateCachedSceneName();

            // Subscribe to scene changes to update cache
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable() {
            UnregisterNetworkCallbacks();

            // Unsubscribe from scene changes
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void UpdateCachedSceneName() {
            var activeScene = SceneManager.GetActiveScene();
            if(activeScene.IsValid()) {
                _cachedSceneName = activeScene.name;
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            UpdateCachedSceneName();
        }

        private void RegisterNetworkCallbacks() {
            var nm = GetNetworkManager();
            if(nm == null) return;
            networkManager = nm; // Update cached reference

            // Always unregister first to prevent duplicates
            networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            networkManager.OnClientConnectedCallback -= OnClientConnected;

            // Then register
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            networkManager.OnClientConnectedCallback += OnClientConnected;
        }

        private void UnregisterNetworkCallbacks() {
            var nm = GetNetworkManager();
            if(nm == null) return;
            networkManager = nm; // Update cached reference

            networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            networkManager.OnClientConnectedCallback -= OnClientConnected;
        }

        private async void Start() {
            try {
                // DontDestroyOnLoad(gameObject);
                await _ugs.InitializeAsync();
            } catch(Exception e) {
                Debug.LogException(e);
            }
        }

        private void OnDestroy() {
            StopWatchingLobby();
            UnhookSessionEvents();
        }

        private void OnSceneEvent(SceneEvent sceneEvent) {
            // We only care about SceneEventType.LoadComplete
            if(sceneEvent.SceneEventType == SceneEventType.LoadComplete) {
                OnGameSceneLoaded(sceneEvent.ClientId, sceneEvent.SceneName);
            }
        }

        private void OnClientDisconnected(ulong clientId) {
            // If we're a client, and we got disconnected (not a voluntary leave)
            if(!networkManager.IsServer && clientId == networkManager.LocalClientId && !_isLeaving) {
                HandleUnexpectedDisconnect().Forget();
            }
        }

        private async UniTaskVoid HandleUnexpectedDisconnect() {
            SetFrontStatus(SessionPhase.ReturningToMenu, "Lost connection. Returning to main menu...");

            // Stop all sounds before scene transition to prevent accessing destroyed audio clips
            EventBus.Publish(new StopAllSoundsEvent());

            // Fade to black
            if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeOut().ToUniTask();
            }

            // Hide game UI if in game
            if(HUDManager.Instance != null && _cachedSceneName == "Game") {
                EventBus.Publish(new HideHUDEvent());
                if(GameMenuManager.Instance != null && GameMenuManager.Instance.IsPaused) {
                    GameMenuManager.Instance.TogglePause();
                }
            }

            ResetSessionState();

            // Clear camera stacks before leaving scene (prevents warnings about missing overlays)
            ClearCameraStacks();

            // Load main menu if not already there
            if(_cachedSceneName != "MainMenu") {
                LoadMainMenu("MainMenu");
                await WaitForSceneLoadAsync("MainMenu");
            }

            // Show main menu panel (works whether we were in lobby or game)
            var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
            if(mainMenuManager != null) {
                mainMenuManager.ShowPanel(mainMenuManager.MainMenuPanel);
            }

            // Fade back in
            if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeIn().ToUniTask();
            }
        }

        #endregion

        #region Public API

        /// <summary>Creates a UGS session (no network transport started yet). Returns join code displayed to others.</summary>
        public async UniTask<string> StartSessionAsHost() {
            // Ensure clean state before starting new session
            await EnsureCleanStateForNewSession();

            SetFrontStatus(SessionPhase.CreatingLobby, "Creating lobby...");
            var props = await _ids.GetPlayerPropertiesAsync(PlayerNameKey);

            var options = new SessionOptions {
                MaxPlayers = 16, IsLocked = false, IsPrivate = false, PlayerProperties = props
            };

            ActiveSession = await _ugs.CreateAsync(options);
            HookSessionEvents();

            // Ensure callbacks are registered
            RegisterNetworkCallbacks();

            // Notify UI of new session players
            if(PlayersChanged != null) {
                PlayersChanged.Invoke(ActiveSession.Players);
            }

            SetFrontStatus(SessionPhase.LobbyReady, "Lobby ready. Share the join code with friends!");
            return ActiveSession.Code;
        }

        private async UniTask EnsureRelayCodePublishedForLobbyAsync() {
            if(_hostAllocation == null || string.IsNullOrEmpty(_relayJoinCode)) {
                var (alloc, code) = await _relay.CreateAllocationAsync(16);
                _hostAllocation = alloc;
                _relayJoinCode = code;

                var host = ActiveSession != null ? ActiveSession.AsHost() : null;
                if(host != null) {
                    host.SetProperty(RelayCodeKey, new SessionProperty(code, VisibilityPropertyOptions.Member));
                    await host.SavePropertiesAsync(); // clients see the real Relay code here

                    // Immediately notify any clients that are already polling
                    if(RelayCodeAvailable != null) {
                        RelayCodeAvailable.Invoke(code);
                    }
                } else {
                    Debug.LogWarning("[SessionManager] Cannot publish relay code - host == null");
                }
            }
        }

        /// <summary>Allocates Relay + configures transport, publishes join code, then starts NGO Host and loads the game.</summary>
        public async UniTask BeginGameplayAsHostAsync() {
            SetFrontStatus(SessionPhase.StartingHost, "Starting game as host...");
            StopWatchingLobby();

            // Reset state flags for new game session
            _expectedLobbyCount = 1;
            if(ActiveSession != null && ActiveSession.Players != null) {
                _expectedLobbyCount = ActiveSession.Players.Count;
            }
            _clientsFinishedLoading.Clear();
            _hasCompletedInitialLoad = false; // Reset this flag
            IsInGameplay = false;

            await EnsureRelayCodePublishedForLobbyAsync();
            await SetupHostTransportAsync();
            StartHostIfNeeded();

            SetFrontStatus(SessionPhase.AllocatingRelay, "Allocating relay...");
            if(networkManager.IsServer)
                await PublishRelayCodeIfAnyAsync();

            SetFrontStatus(SessionPhase.LoadingScene, "Waiting for all players...");

            // Check if all players are already connected (including host-only scenario)
            // Wait a frame for network manager to update ConnectedClientsIds
            await UniTask.DelayFrame(1);
            CheckAndStartSceneLoadIfReady();

            // If not ready yet, start polling for clients to connect
            if(!_hasCompletedInitialLoad) {
                StartPollingForAllPlayers().Forget();
            }
        }

        /// <summary>
        /// Polls periodically to check if all players have connected.
        /// This handles cases where clients connect after the host has already started.
        /// </summary>
        private async UniTaskVoid StartPollingForAllPlayers() {
            const float pollInterval = 0.5f; // Check every 0.5 seconds
            const float maxWaitTime = 30f; // Maximum 30 seconds wait

            var elapsed = 0f;

            while(!_hasCompletedInitialLoad && elapsed < maxWaitTime && networkManager != null &&
                  networkManager.IsServer) {
                await UniTask.Delay(TimeSpan.FromSeconds(pollInterval));
                elapsed += pollInterval;

                CheckAndStartSceneLoadIfReady();

                // If scene load started, break out of polling
                if(_hasCompletedInitialLoad) {
                    break;
                }
            }

            if(!_hasCompletedInitialLoad && elapsed >= maxWaitTime) {
                Debug.LogWarning("[SessionManager] Timeout waiting for all players to connect. Starting game anyway.");
                // Force start if timeout (in case of network issues)
                if(networkManager != null && networkManager.IsServer && networkManager.ConnectedClientsIds.Count > 0) {
                    _hasCompletedInitialLoad = true;
                    if(SessionNetworkBridge.Instance != null) {
                        SessionNetworkBridge.Instance.FadeOutNewClientsClientRpc();
                    }
                    StartCoroutine(LoadSceneAfterFade());
                }
            }
        }

        /// <summary>
        /// Checks if all expected players are connected and starts scene load if ready.
        /// This handles both host-only and multi-player scenarios.
        /// </summary>
        private void CheckAndStartSceneLoadIfReady() {
            // Safety check: ensure we're the active instance and haven't been destroyed
            // Check if this instance == null/destroyed first (Unity's special null check)
            if(this == null) {
                Debug.LogWarning(
                    "[SessionManager] CheckAndStartSceneLoadIfReady called on destroyed SessionManager instance");
                return;
            }

            // Then check if we're the active singleton instance
            if(!HasInstance || Instance != this) {
                Debug.LogWarning(
                    "[SessionManager] CheckAndStartSceneLoadIfReady called but this is not the active SessionManager instance");
                return;
            }

            // Finally check if GameObject is valid and active
            if(gameObject == null || !gameObject.activeInHierarchy) {
                Debug.LogWarning(
                    "[SessionManager] CheckAndStartSceneLoadIfReady called but SessionManager GameObject == null or inactive");
                return;
            }

            if(networkManager == null || !networkManager.IsServer || IsInGameplay || _hasCompletedInitialLoad) return;

            var expectedPlayerCount = _expectedLobbyCount;
            var connectedCount = networkManager.ConnectedClientsIds.Count;

            // Ensure we have at least 1 connected (the host itself)
            // Host is always client ID 0 and should be in ConnectedClientsIds
            if(connectedCount == 0 && networkManager.IsHost) {
                Debug.LogWarning("[SessionManager] Host started but not in ConnectedClientsIds yet - waiting...");
                return;
            }

            switch(expectedPlayerCount) {
                // Start scene load if we have all expected players OR if we have at least 1 player (host-only scenario)
                case > 0 when connectedCount >= expectedPlayerCount: {
                    _hasCompletedInitialLoad = true;

                    if(SessionNetworkBridge.Instance != null) {
                        SessionNetworkBridge.Instance.FadeOutNewClientsClientRpc();
                    }

                    // Safety check before starting coroutine - ensure we're still the active instance
                    if(HasInstance && Instance == this && this != null && gameObject != null &&
                       gameObject.activeInHierarchy) {
                        StartCoroutine(LoadSceneAfterFade());
                    } else {
                        Debug.LogError(
                            "[SessionManager] Cannot start coroutine - SessionManager is destroyed, inactive, or not the active instance");
                    }

                    break;
                }
                case 0 when connectedCount > 0: {
                    // Fallback: if expected count is 0 (shouldn't happen), but we have players, start anyway
                    Debug.LogWarning(
                        "[SessionManager] Expected count is 0 but players are connected - starting anyway");
                    _hasCompletedInitialLoad = true;
                    if(SessionNetworkBridge.Instance != null) {
                        SessionNetworkBridge.Instance.FadeOutNewClientsClientRpc();
                    }

                    // Safety check before starting coroutine - ensure we're still the active instance
                    if(HasInstance && Instance == this && this != null && gameObject != null &&
                       gameObject.activeInHierarchy) {
                        StartCoroutine(LoadSceneAfterFade());
                    } else {
                        Debug.LogError(
                            "[SessionManager] Cannot start coroutine - SessionManager is destroyed, inactive, or not the active instance");
                    }

                    break;
                }
            }
        }

        private void OnClientConnected(ulong clientId) {
            // Safety check: ensure we're the active instance and haven't been destroyed
            // Check if this instance == null/destroyed first (Unity's special null check)
            if(this == null) {
                Debug.LogWarning(
                    $"[SessionManager] OnClientConnected called on destroyed SessionManager instance (clientId: {clientId})");
                return;
            }

            // Then check if we're the active singleton instance
            if(!HasInstance || Instance != this) {
                Debug.LogWarning(
                    $"[SessionManager] OnClientConnected called but this is not the active SessionManager instance (clientId: {clientId}, HasInstance: {HasInstance})");
                return;
            }

            // Finally check if GameObject is valid and active
            if(gameObject == null || !gameObject.activeInHierarchy) {
                Debug.LogWarning(
                    $"[SessionManager] OnClientConnected called but SessionManager GameObject == null or inactive (clientId: {clientId})");
                return;
            }

            // If we don't have an active session, this is a stale connection - ignore it
            if(ActiveSession == null) {
                Debug.LogWarning($"[SessionManager] Client {clientId} connected but no active session - ignoring");
                return;
            }

            if(networkManager == null || !networkManager.IsServer) {
                SetFrontStatus(SessionPhase.LoadingScene, "Loading...");
                return;
            }

            HandleLateJoiner(clientId);

            // Don't trigger scene transition if we're already in gameplay
            if(IsInGameplay) {
                if(SessionNetworkBridge.Instance != null) {
                    SessionNetworkBridge.Instance.FadeInSingleClientClientRpc();
                }
                return;
            }

            // Update player list when client connects using RPC to synchronize all clients
            // This ensures all players (including the new one) see the update at the same time
            if(ActiveSession != null && SessionNetworkBridge.Instance != null) {
                SessionNetworkBridge.Instance.RefreshPlayerListClientRpc();
            }

            // Use the shared method to check and start scene load
            CheckAndStartSceneLoadIfReady();
        }

        private IEnumerator LoadSceneAfterFade() {
            // Wait for fade to complete using actual fade duration
            if(SceneTransitionManager.Instance != null) {
                yield return SceneTransitionManager.Instance.FadeOut();
            } else {
                // Fallback if SceneTransitionManager is not available
                yield return new WaitForSeconds(0.5f);
            }

            // Now load the scene
            BeginNetworkSceneLoad();
        }

        private void OnGameSceneLoaded(ulong clientId, string sceneName) {
            if(sceneName != GameSceneName) return;

            if(!networkManager.IsServer) {
                IsInGameplay = true;
                if(SceneTransitionManager.Instance != null) {
                    _ = SceneTransitionManager.Instance.FadeIn().ToUniTask(); // <-- ALWAYS
                }

                return;
            }

            if(!_clientsFinishedLoading.Contains(clientId)) {
                _clientsFinishedLoading.Add(clientId);
            }

            if(_clientsFinishedLoading.Count != networkManager.ConnectedClientsIds.Count) return;
            networkManager.SceneManager.OnSceneEvent -= OnSceneEvent;
            IsInGameplay = true;
            if(_customNetworkManager != null) {
                _customNetworkManager.EnableGameplaySpawningAndSpawnAll();
            }
            SessionNetworkBridge.Instance.FadeInAllClientsClientRpc();
        }

        /// <summary>Joins a session by code; waits for host to publish Relay code (if remote), configures client transport, starts NGO Client.</summary>
        public async UniTask<string> JoinSessionByCodeAsync(string joinCode) {
            var lastSessionId = PlayerPrefs.GetString("LastSessionId", "");
            var lastCode = PlayerPrefs.GetString("LastJoinCode", "");

            // **1. Try silent reconnect if we have a session ID and code matches**
            var attemptedReconnect = false;
            if(!string.IsNullOrEmpty(lastSessionId) && lastCode == joinCode) {
                try {
                    SetFrontStatus(SessionPhase.ConnectingToRelay, "Reconnecting to session...");
                    attemptedReconnect = true;
                    ActiveSession = await _ugs.ReconnectToSessionAsync(lastSessionId);
                    HookSessionEvents();
                    await SetupClientTransportFromSessionAsync(); // See below
                    StartClientIfNeeded();
                    if(PlayersChanged != null) {
                        PlayersChanged.Invoke(ActiveSession.Players);
                    }
                    SetFrontStatus(SessionPhase.Connected, "Reconnected!");
                    return "Reconnecting to session...";
                } catch(Exception ex) {
                    Debug.Log($"[SessionManager] Reconnect failed (player removed?): {ex.Message}");
                    // Continue to normal join — player was kicked
                    // Update status for fresh join
                    SetFrontStatus(SessionPhase.CreatingLobby, "Joining session...");
                }
            }

            // **2. Normal join with cache bypass**
            await ForceLeaveAndCleanupAsync();

            try {
                // Only set status if we didn't already set it after failed reconnect
                if(!attemptedReconnect) {
                    SetFrontStatus(SessionPhase.CreatingLobby, "Joining session...");
                }

                await JoinRemoteAsync(joinCode);
                return "Lobby joined. Waiting for host...";
            } catch(Exception ex) when(ex.Message.Contains("already a member") || ex.Message.Contains("409")) {
                // **3. FINAL FALLBACK: Try to reconnect again (edge case)**
                if(string.IsNullOrEmpty(lastSessionId)) throw new Exception("Failed to rejoin. Try again in 30s.");
                try {
                    ActiveSession = await _ugs.ReconnectToSessionAsync(lastSessionId);
                    await SetupClientTransportFromSessionAsync();
                    StartClientIfNeeded();
                    return "RECONNECTED_AFTER_CACHE";
                } catch { /* ignore */
                }

                throw new Exception("Failed to rejoin. Try again in 30s.");
            }
        }

        private async UniTask SetupClientTransportFromSessionAsync() {
            if(ActiveSession == null) return;

            // Extract Relay info from session properties
            if(ActiveSession.Properties.TryGetValue("relayCode", out var prop) && !string.IsNullOrEmpty(prop.Value)) {
                var relayCode = prop.Value;
                var join = await _relay.JoinAllocationAsync(relayCode);
                var utp = networkManager.GetComponent<UnityTransport>();

                // Optimize transport settings for lower latency
                utp.HeartbeatTimeoutMS = 500;
                utp.DisconnectTimeoutMS = 2000;

                utp.SetRelayServerData(join.ToRelayServerData(RelayProtocol.DTLS));
            }
        }

        private async UniTask ForceLeaveAndCleanupAsync() {
            // Leave session if exists (ResetSessionState will unhook events)
            if(ActiveSession != null) {
                try {
                    await _ugs.LeaveOrDeleteAsync(ActiveSession);
                } catch {
                    // Session might already be deleted
                }
            }

            // Cleanup network
            await _net.CleanupNetworkAsync();

            // Reset state (includes StopWatchingLobby, UnhookSessionEvents, etc.)
            ResetSessionState();
        }

        /// <summary>Leaves (or deletes, if host) the UGS session, tears down NGO, returns to Main Menu. Safe to call multiple times.</summary>
        /// <param name="mainMenu">Scene name to load, defaults to "MainMenu".</param>
        /// <param name="skipFade">If true, skips the fade transition (for voluntary lobby leaves). Defaults to false.</param>
        public async UniTask LeaveToMainMenuAsync(string mainMenu = "MainMenu", bool skipFade = false) {
            if(_isLeaving) return;
            _isLeaving = true;

            try {
                // Stop all sounds before scene transition to prevent accessing destroyed audio clips
                EventBus.Publish(new StopAllSoundsEvent());

                // Fade to black before leaving, unless in main menu already or fade is skipped
                if(!skipFade && SceneTransitionManager.Instance != null && _cachedSceneName != "MainMenu") {
                    await SceneTransitionManager.Instance.FadeOut().ToUniTask();
                }

                if(PostMatchManager.Instance != null) {
                    PostMatchManager.Instance.ShowInGameHudAfterPostMatch();
                }

                // If we're the host and in gameplay, tell everyone else to fade out
                // Note: Our own fade already completed above, so no need to wait
                if(networkManager != null && networkManager.IsServer && IsInGameplay &&
                   SessionNetworkBridge.Instance != null) {
                    SessionNetworkBridge.Instance.FadeOutAllClientsClientRpc();
                }

                // Store session reference before cleanup
                var sessionToLeave = ActiveSession;

                // Cleanup network
                await _net.CleanupNetworkAsync();

                // Clear camera stacks before leaving scene (prevents warnings about missing overlays)
                ClearCameraStacks();

                // Leave session
                if(sessionToLeave != null) {
                    try {
                        await _ugs.LeaveOrDeleteAsync(sessionToLeave);
                    } catch {
                        // Session might already be deleted
                    }
                }

                // Reset all state (includes StopWatchingLobby, UnhookSessionEvents, etc.)
                ResetSessionState();

                // Re-register callbacks after cleanup
                RegisterNetworkCallbacks();

                LoadMainMenu(mainMenu);

                await WaitForSceneLoadAsync(mainMenu);
                // Only fade in if we faded out
                if(!skipFade && SceneTransitionManager.Instance != null) {
                    await SceneTransitionManager.Instance.FadeIn().ToUniTask();
                }
            } finally {
                _isLeaving = false;
            }
        }

        #endregion

        #region Private - Host path

        /// <summary>Sets UnityTransport for Relay host and publishes relay code to UGS.</summary>
        private async UniTask SetupHostTransportAsync() {
            var nm = GetNetworkManager();
            if(nm == null) {
                Debug.LogError("[SessionManager] NetworkManager.Singleton == null in SetupHostTransportAsync");
                return;
            }

            networkManager = nm; // Update cached reference

            var utp = nm.GetComponent<UnityTransport>();
            if(utp == null) {
                Debug.LogError("[SessionManager] UnityTransport component not found on NetworkManager");
                return;
            }

            // Optimize transport settings for lower latency
            utp.HeartbeatTimeoutMS = 500; // Default is often 1000ms+ - reduce for faster disconnect detection
            utp.DisconnectTimeoutMS = 2000; // Default is often 10000ms - reduce for faster cleanup

            // Use the cached allocation we created in the lobby step
            if(_hostAllocation == null || string.IsNullOrEmpty(_relayJoinCode)) {
                var (alloc, code) = await _relay.CreateAllocationAsync(16);
                _hostAllocation = alloc;
                _relayJoinCode = code;
            }

            utp.SetRelayServerData(_hostAllocation.ToRelayServerData(RelayProtocol.DTLS));
        }

        private async UniTask PublishRelayCodeIfAnyAsync() {
            if(ActiveSession != null && ActiveSession.IsHost && !string.IsNullOrEmpty(_relayJoinCode)) {
                var host = ActiveSession.AsHost();
                host.SetProperty(RelayCodeKey, new SessionProperty(_relayJoinCode, VisibilityPropertyOptions.Member));
                await host.SavePropertiesAsync();

                // Immediately notify clients via event (in case they're already polling)
                if(RelayCodeAvailable != null) {
                    RelayCodeAvailable.Invoke(_relayJoinCode);
                }
            } else {
                var isHost = ActiveSession != null && ActiveSession.IsHost;
                Debug.LogWarning(
                    $"[SessionManager] Cannot publish relay code - IsHost={isHost}, Code={_relayJoinCode}");
            }
        }

        /// <summary>Starts NGO host if not already listening.</summary>
        private static void StartHostIfNeeded() {
            if(!networkManager.IsListening)
                networkManager.StartHost();
        }

        /// <summary>Arms scene-completion callback and asks server to load the game scene via NGO SceneManager.</summary>
        private void BeginNetworkSceneLoad() {
            if(!networkManager.IsServer) return;
            _clientsFinishedLoading.Clear();
            networkManager.SceneManager.OnSceneEvent += OnSceneEvent;
            _loadingGameScene = true;
            _net.HookSceneCallbacks(OnNetworkSceneLoadComplete);
            _scenes.LoadGameSceneServer(GameSceneName);
        }

        #endregion

        #region Private - Client path

        /// <summary>Joins remote UGS session, waits for Relay code, configures transport, starts NGO client.</summary>
        private async UniTask JoinRemoteAsync(string joinCode) {
            var props = await _ids.GetPlayerPropertiesAsync(PlayerNameKey);
            ActiveSession = await _ugs.JoinByCodeAsync(joinCode, new JoinSessionOptions { PlayerProperties = props });

            PlayerPrefs.SetString("LastSessionId", ActiveSession.Id);
            PlayerPrefs.SetString("LastJoinCode", joinCode);
            PlayerPrefs.Save();

            HookSessionEvents();

            // Ensure callbacks are registered
            RegisterNetworkCallbacks();

            // Notify UI of session players
            if(PlayersChanged != null) {
                PlayersChanged.Invoke(ActiveSession.Players);
            }

            if(SessionJoined != null) {
                SessionJoined.Invoke(ActiveSession.Code);
            }

            SetFrontStatus(SessionPhase.WaitingForRelay, "Waiting for host to start game...");
            _ = PollForGameStartAsync();
        }

        private async UniTask PollForGameStartAsync() {
            StopWatchingLobby();
            _lobbyWatchCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var ct = _lobbyWatchCts.Token;

            string relayCode = null;
            var connected = false;

            RelayCodeAvailable += OnRelay;

            try {
                // Check immediately if relay code is already available
                if(TryGetRelayCode(out var immediateCode)) {
                    relayCode = immediateCode;
                    if(RelayCodeAvailable != null) {
                        RelayCodeAvailable.Invoke(immediateCode);
                    }
                }

                while(!ct.IsCancellationRequested && !connected && string.IsNullOrEmpty(relayCode)) {
                    if((networkManager != null && networkManager.IsClient) || Phase >= SessionPhase.Connected) {
                        connected = true;
                        break;
                    }

                    try {
                        await ActiveSession.RefreshAsync();

                        // ── HOST GONE ──
                        if(ActiveSession == null ||
                           string.IsNullOrEmpty(ActiveSession.Host) ||
                           ActiveSession.Players.Count == 0) {
                            if(HostDisconnected != null) {
                                HostDisconnected.Invoke();
                            }
                            if(LobbyReset != null) {
                                LobbyReset.Invoke();
                            }
                            StopWatchingLobby();
                            return;
                        }

                        if(TryGetRelayCode(out var c)) {
                            relayCode = c;
                            if(RelayCodeAvailable != null) {
                                RelayCodeAvailable.Invoke(c);
                            }
                            break;
                        }

                        await UniTask.Delay(500, cancellationToken: ct);
                    } catch(Exception e) when(e.Message.Contains("not found") || e.Message.Contains("deleted")) {
                        if(HostDisconnected != null) {
                            HostDisconnected.Invoke();
                        }
                        if(LobbyReset != null) {
                            LobbyReset.Invoke();
                        }
                        StopWatchingLobby();
                        return;
                    } catch(Exception e) when(e.Message.Contains("Too Many Requests")) {
                        await UniTask.Delay(3000, cancellationToken: ct);
                    } catch(OperationCanceledException) {
                        // If cancelled, but we have a relay code, break out to connect
                        if(!string.IsNullOrEmpty(relayCode)) {
                            break;
                        }

                        throw; // Re-throw if no relay code
                    } catch(Exception) {
                        break;
                    }
                }

                // Connect to relay if we have a code, even if polling was cancelled
                if(!string.IsNullOrEmpty(relayCode) && !connected) {
                    await ConnectToRelayAsync(relayCode);
                } else if(!connected) {
                    Debug.LogWarning("[SessionManager] Client finished polling but no relay code found");
                }
            } catch(OperationCanceledException) {
                // Check if we have a relay code even though polling was cancelled
                if(!string.IsNullOrEmpty(relayCode) && !connected) {
                    await ConnectToRelayAsync(relayCode);
                }
            } finally {
                RelayCodeAvailable -= OnRelay;
                StopWatchingLobby();
            }

            return;

            void OnRelay(string c) {
                if(!string.IsNullOrWhiteSpace(c) && c.Length >= 6) {
                    relayCode = c;
                    // Don't cancel token here - let the loop break naturally
                    // This prevents OperationCanceledException from preventing ConnectToRelayAsync
                }
            }
        }

        private async UniTask ConnectToRelayAsync(string relayCode) {
            if(relayCode != null) {
                relayCode = relayCode.Trim();
            }

            // Validate relay code before attempting connection
            if(string.IsNullOrEmpty(relayCode) || relayCode.Length < 6) {
                Debug.LogWarning($"[SessionManager] Invalid relay code received: '{relayCode}' - not connecting");
                SetFrontStatus(SessionPhase.WaitingForRelay, "Waiting for valid connection code...");
                return; // Don't attempt connection with invalid code
            }

            try {
                SetFrontStatus(SessionPhase.ConnectingToRelay, "Connecting to host...");
                var join = await _relay.JoinAllocationAsync(relayCode);
                var utp = networkManager.GetComponent<UnityTransport>();

                // Optimize transport settings for lower latency
                utp.HeartbeatTimeoutMS = 500;
                utp.DisconnectTimeoutMS = 2000;

                utp.SetRelayServerData(join.ToRelayServerData(RelayProtocol.DTLS));
                StartClientIfNeeded();
                SetFrontStatus(SessionPhase.LoadingScene, "Waiting for all players...");
                ArmSceneCompletion();
            } catch(Exception e) {
                SetFrontStatus(SessionPhase.Error, $"Failed to connect: {e.Message}");
                Debug.LogError($"[SessionManager] Failed to connect to relay: {e}");
            }
        }

        private static async void StartClientIfNeeded() {
            try {
                if(networkManager == null)
                    networkManager = NetworkManager.Singleton;

                // Already a client or running as host? Nothing to do.
                if(networkManager.IsClient || networkManager.IsHost || startingClient)
                    return;

                startingClient = true;

                // Await fade completion before starting client
                if(SceneTransitionManager.Instance != null) {
                    await SceneTransitionManager.Instance.FadeOutAsync();
                }

                var ok = networkManager.StartClient();
                if(!ok)
                    Debug.LogError(
                        "[SessionManager] StartClient failed (transport not configured or already running).");
                startingClient = false;
            } catch(Exception e) {
                Debug.LogException(e);
            }
        }

        /// <summary>Ensures scene-completion callback is hooked only once during client-side load.</summary>
        private void ArmSceneCompletion() {
            if(_loadingGameScene) return;
            _loadingGameScene = true;
            _net.HookSceneCallbacks(OnNetworkSceneLoadComplete);
        }

        private void DisarmSceneCompletion() {
            _loadingGameScene = false;
            _net.UnhookSceneCallbacks(OnNetworkSceneLoadComplete);
        }

        #endregion

        #region Session events & helpers

        private bool TryGetRelayCode(out string code) {
            code = null;
            if(ActiveSession == null) {
                return false;
            }

            if(!ActiveSession.Properties.TryGetValue(RelayCodeKey, out var prop) ||
               string.IsNullOrEmpty(prop.Value)) return false;
            code = prop.Value;
            return true;
        }

        public async UniTaskVoid RefreshAndUpdatePlayerList() {
            if(ActiveSession == null) return;

            try {
                await ActiveSession.RefreshAsync();
                if(ActiveSession != null) {
                    if(PlayersChanged != null) {
                        PlayersChanged.Invoke(ActiveSession.Players);
                    }
                }
            } catch {
                // Ignore refresh errors - session might be gone
            }
        }

        private void HandleLateJoiner(ulong clientId) {
            if(!networkManager.IsServer || !IsInGameplay) return;

            // Spawn the player for the late joiner without affecting others
            if(_customNetworkManager != null) {
                _customNetworkManager.SpawnPlayerFor(clientId);
            }
        }

        private static void LoadMainMenu(string scene) {
            if(string.IsNullOrEmpty(scene)) return;

            // Check if scene is already loaded
            var existingScene = SceneManager.GetSceneByName(scene);
            if(existingScene.IsValid() && existingScene.isLoaded) {
                // Scene already loaded, just activate it
                SceneManager.SetActiveScene(existingScene);
                return;
            }

            // Load scene additively (init scene should persist)
            // If init scene doesn't exist, fall back to Single mode
            var initScene = SceneManager.GetSceneByName("Init");
            if(initScene.IsValid() && initScene.isLoaded) {
                // Init scene exists, load additively
                var loadOp = SceneManager.LoadSceneAsync(scene, LoadSceneMode.Additive);
                if(loadOp == null) return;
                // Wait for load to complete in a coroutine
                var sessionManagerInstance = Instance;
                if(sessionManagerInstance != null) {
                    sessionManagerInstance.StartCoroutine(SetActiveSceneWhenLoaded(loadOp, scene));
                }
            } else {
                // No init scene, use Single mode (legacy behavior)
                SceneManager.LoadScene(scene, LoadSceneMode.Single);
            }
        }

        private static IEnumerator SetActiveSceneWhenLoaded(AsyncOperation loadOp, string sceneName) {
            while(!loadOp.isDone) {
                yield return null;
            }

            var scene = SceneManager.GetSceneByName(sceneName);
            if(scene.IsValid()) {
                SceneManager.SetActiveScene(scene);
            }
        }

        private void HookSessionEvents() {
            _ugs.HookEvents(ActiveSession,
                onChanged: () => {
                    // 1. Host left → session.Host becomes null or Players empty
                    if(ActiveSession == null ||
                       string.IsNullOrEmpty(ActiveSession.Host) ||
                       ActiveSession.Players.Count == 0) {
                        if(HostDisconnected != null) {
                            HostDisconnected.Invoke();
                        }
                        if(LobbyReset != null) {
                            LobbyReset.Invoke();
                        }
                        StopWatchingLobby();
                        return; // ← stop further processing
                    }

                    if(PlayersChanged != null && ActiveSession != null) {
                        PlayersChanged.Invoke(ActiveSession.Players);
                    }
                },
                onJoined: _ => {
                    if(PlayersChanged != null && ActiveSession != null) {
                        PlayersChanged.Invoke(ActiveSession.Players);
                    }
                },
                onLeaving: _ => {
                    // 2. Any player leaves – we will check on next refresh
                },
                onPropsChanged: () => {
                    if(ActiveSession == null) {
                        return;
                    }

                    if(ActiveSession.Properties.TryGetValue(RelayCodeKey, out var p) &&
                       !string.IsNullOrEmpty(p.Value)) {
                        if(RelayCodeAvailable != null) {
                            RelayCodeAvailable.Invoke(p.Value);
                        }
                    }
                });
        }

        private void UnhookSessionEvents() {
            // Only unhook if we have an active session
            if(ActiveSession != null) {
                _ugs.UnhookEvents(ActiveSession,
                    onChanged: () => { },
                    onJoined: _ => { },
                    onLeaving: _ => { },
                    onPropsChanged: () => { });
            }
        }

        #endregion

        #region Camera Stack Cleanup

        /// <summary>
        /// Clears all camera overlays from the main camera's stack before leaving the scene.
        /// This prevents Unity warnings about missing camera overlays when cameras are destroyed.
        /// </summary>
        private static void ClearCameraStacks() {
            var mainCamera = Camera.main;
            if(mainCamera == null) {
                var mainCameraObj = GameObject.FindGameObjectWithTag("MainCamera");
                if(mainCameraObj != null) {
                    mainCamera = mainCameraObj.GetComponent<Camera>();
                }
            }

            if(mainCamera == null) return;
            var mainCameraData = mainCamera.GetComponent<UniversalAdditionalCameraData>();
            if(mainCameraData == null || mainCameraData.cameraStack == null) return;
            // Remove all null/destroyed cameras from the stack
            for(var i = mainCameraData.cameraStack.Count - 1; i >= 0; i--) {
                var overlayCam = mainCameraData.cameraStack[i];
                if(overlayCam == null) {
                    mainCameraData.cameraStack.RemoveAt(i);
                }
            }

            // Clear the entire stack to ensure clean state
            mainCameraData.cameraStack.Clear();
        }

        #endregion

        #region Scene callback

        /// <summary>Server-side: finishes bootstrapping after game scene loads (spawns, UI reveal).</summary>
        private void OnNetworkSceneLoadComplete(string scene, LoadSceneMode mode,
            List<ulong> completed, List<ulong> timedOut) {
            if(!_loadingGameScene) return;
            DisarmSceneCompletion();
        }

        #endregion
    }
}