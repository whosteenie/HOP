using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Network.Core;
using Network.Relay;
using Network.Singletons;
using Network.UGS;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Multiplayer;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
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
    /// <item><see cref="ILocalhostPolicy"/> – editor/localhost detection</item>
    /// </list>
    /// Exposes events for UI updates (PlayersChanged, RelayCodeAvailable).
    /// </summary>
    public sealed class SessionManager : Singleton<SessionManager> {
        public enum SessionPhase {
            None,
            CreatingLobby,
            LobbyReady,
            AllocatingRelay,
            WaitingForRelay,
            ConnectingToRelay,
            StartingHost,
            StartingClient,
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
        private static bool _startingClient;
        private CancellationTokenSource _lobbyWatchCts;
        private static NetworkManager _networkManager;
        private int _expectedLobbyCount;

        // ===== Services =====
        private readonly IUgsSessionService _ugs = new UgsSessionService();
        private readonly INetworkLifecycle _net = new NetworkLifecycle();
        private readonly IPlayerIdentity _ids = new PlayerIdentity();
        private readonly ILocalhostPolicy _localhost = new LocalhostPolicy();
        private readonly IRelayConnector _relay = new RelayConnector();
        private readonly ISceneCoordinator _scenes = new SceneCoordinator();

        public event Action<string> FrontStatusChanged;
        public event Action<string> SessionJoined;
        public SessionPhase Phase { get; private set; }

        private List<ulong> _clientsFinishedLoading = new List<ulong>();
        private CustomNetworkManager _customNetworkManager;

        private bool _isInGameplay = false; // Add this to track if we're actually in-game
        private bool _hasCompletedInitialLoad = false; // Add to track if host has done initial load
        private bool _isReconnecting = false;

        public bool IsInGameplay => _isInGameplay;

        public event Action HostDisconnected; // UI: show message
        public event Action LobbyReset; // UI: clear player list, reset labels

        private void SetFrontStatus(SessionPhase phase, string message) {
            Phase = phase;
            FrontStatusChanged?.Invoke(message);
            Debug.Log($"[SessionManager] {message}");
        }

        private void StopWatchingLobby(string why = null) {
            try {
                _lobbyWatchCts?.Cancel();
            } catch { /* ignore */
            }

            _lobbyWatchCts?.Dispose();
            _lobbyWatchCts = null;
            if(!string.IsNullOrEmpty(why)) Debug.Log($"[SessionManager] Stopped lobby watch: {why}");
        }

        #region Unity Lifecycle

        protected override void Awake() {
            if(HasInstance && Instance != this) {
                Destroy(gameObject);
                return;
            }

            DontDestroyOnLoad(gameObject);

            _networkManager = NetworkManager.Singleton;
            _customNetworkManager = _networkManager.GetComponent<CustomNetworkManager>();
        }

        private void OnEnable() {
            if(_networkManager != null) {
                _networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
                _networkManager.OnClientDisconnectCallback += OnClientDisconnected;

                _networkManager.OnClientConnectedCallback -= OnClientConnected;
                _networkManager.OnClientConnectedCallback += OnClientConnected;
            }
        }

        private void OnDisable() {
            if(_networkManager != null) {
                _networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
                _networkManager.OnClientConnectedCallback -= OnClientConnected;
            }
        }

        private async void Start() {
            try {
                // DontDestroyOnLoad(gameObject);
                await _ugs.InitializeAsync();
            } catch(Exception e) {
                Debug.LogException(e);
            }
        }

        private void OnSceneEvent(SceneEvent sceneEvent) {
            // We only care about SceneEventType.LoadComplete
            if(sceneEvent.SceneEventType == SceneEventType.LoadComplete) {
                OnGameSceneLoaded(sceneEvent.ClientId, sceneEvent.SceneName, LoadSceneMode.Single);
            }
        }

        private void OnClientDisconnected(ulong clientId) {
            // If we're a client, and we got disconnected (not a voluntary leave)
            if(!_networkManager.IsServer && clientId == _networkManager.LocalClientId && !_isLeaving) {
                Debug.Log("[SessionManager] Disconnected from host - returning to menu");
                HandleUnexpectedDisconnect().Forget();
            }
        }

        private async UniTaskVoid HandleUnexpectedDisconnect() {
            SetFrontStatus(SessionPhase.ReturningToMenu, "Lost connection. Returning to main menu...");

            // Fade to black
            if(SceneTransitionManager.Instance != null) {
                await SceneTransitionManager.Instance.FadeOut().ToUniTask();
            }

            await UniTask.Delay(500); // Small delay to let disconnect process

            // Hide game UI if in game
            if(SceneManager.GetActiveScene().name == "Game") {
                HUDManager.Instance?.HideHUD();
                if(GameMenuManager.Instance?.IsPaused == true) {
                    GameMenuManager.Instance.TogglePause();
                }
            }

            ResetSessionState();

            // Load main menu if not already there
            if(SceneManager.GetActiveScene().name != "MainMenu") {
                LoadMainMenu("MainMenu");
                await UniTask.Delay(500); // Wait for scene to load
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

        private void OnDestroy() {
            StopWatchingLobby("destroy");
            UnhookSessionEvents();
        }

        #region Public API

        /// <summary>Creates a UGS session (no network transport started yet). Returns join code displayed to others.</summary>
        public async UniTask<string> StartSessionAsHost() {
            SetFrontStatus(SessionPhase.CreatingLobby, "Creating lobby...");
            var props = _localhost.IsLocalhostTesting()
                ? new Dictionary<string, PlayerProperty>
                    { { PlayerNameKey, new PlayerProperty("Host (Editor)", VisibilityPropertyOptions.Member) } }
                : await _ids.GetPlayerPropertiesAsync(PlayerNameKey);

            var options = new SessionOptions {
                MaxPlayers = 16, IsLocked = false, IsPrivate = false, PlayerProperties = props
            };

            ActiveSession = await _ugs.CreateAsync(options);
            HookSessionEvents();
            PlayersChanged?.Invoke(ActiveSession.Players);

            SetFrontStatus(SessionPhase.LobbyReady, "Lobby ready. Share the join code with friends!");
            return ActiveSession.Code;
        }

        private async UniTask EnsureRelayCodePublishedForLobbyAsync() {
            if(_localhost.IsLocalhostTesting()) {
                var host = ActiveSession?.AsHost();
                if(host != null) {
                    host.SetProperty(RelayCodeKey, new SessionProperty("LOCALHOST", VisibilityPropertyOptions.Member));
                    await host.SavePropertiesAsync();
                }

                _relayJoinCode = "LOCALHOST";
                _hostAllocation = null;
                return;
            }

            if(_hostAllocation == null || string.IsNullOrEmpty(_relayJoinCode)) {
                var (alloc, code) = await _relay.CreateAllocationAsync(16);
                _hostAllocation = alloc;
                _relayJoinCode = code;

                var host = ActiveSession?.AsHost();
                if(host != null) {
                    host.SetProperty(RelayCodeKey, new SessionProperty(code, VisibilityPropertyOptions.Member));
                    await host.SavePropertiesAsync(); // clients see the real Relay code here
                }
            }
        }

        /// <summary>Allocates Relay + configures transport, publishes join code, then starts NGO Host and loads the game.</summary>
        public async UniTask BeginGameplayAsHostAsync() {
            SetFrontStatus(SessionPhase.StartingHost, "Starting game as host...");
            StopWatchingLobby("host starting gameplay");

            // Reset state flags for new game session
            _expectedLobbyCount = ActiveSession?.Players.Count ?? 1;
            _clientsFinishedLoading.Clear();
            _hasCompletedInitialLoad = false; // Reset this flag
            _isInGameplay = false;

            await EnsureRelayCodePublishedForLobbyAsync();
            await SetupHostTransportAsync();
            StartHostIfNeeded();

            SetFrontStatus(SessionPhase.AllocatingRelay, "Allocating relay...");
            if(_networkManager.IsServer)
                await PublishRelayCodeIfAnyAsync();

            SetFrontStatus(SessionPhase.LoadingScene, "Waiting for all players...");
        }

        private void OnClientConnected(ulong clientId) {
            Debug.Log($"[SessionManager] Client {clientId} connected to relay");

            if(!_networkManager.IsServer) {
                SetFrontStatus(SessionPhase.LoadingScene, "Loading...");
                return;
            }

            HandleLateJoiner(clientId);

            // Don't trigger scene transition if we're already in gameplay
            if(_isInGameplay) {
                Debug.Log($"[SessionManager] Late joiner {clientId} connected - game already in progress");
                // Handle late joiner spawning here if needed
                SessionNetworkBridge.Instance?.FadeInSingleClientClientRpc(); // will be executed on the client
                return;
            }

            int expectedPlayerCount = _expectedLobbyCount;
            int connectedCount = _networkManager.ConnectedClientsIds.Count;

            Debug.Log($"[SessionManager] {connectedCount}/{expectedPlayerCount} players connected");

            // Only do initial scene load once
            if(connectedCount >= expectedPlayerCount && !_hasCompletedInitialLoad) {
                Debug.Log("[SessionManager] All lobby players connected! Starting scene transition...");
                _hasCompletedInitialLoad = true;

                // Use bridge to send RPC - but only to connected clients
                SessionNetworkBridge.Instance?.FadeOutNewClientsClientRpc();
                StartCoroutine(LoadSceneAfterFade());
            }
        }

        private IEnumerator LoadSceneAfterFade() {
            // Wait for fade to complete
            yield return new WaitForSeconds(0.5f);

            // Now load the scene
            BeginNetworkSceneLoad();
        }

        private void OnGameSceneLoaded(ulong clientId, string sceneName, LoadSceneMode loadSceneMode) {
            if(sceneName != GameSceneName) return;

            if(!_networkManager.IsServer) {
                _isInGameplay = true;
                if(SceneTransitionManager.Instance != null) {
                    _ = SceneTransitionManager.Instance.FadeIn().ToUniTask(); // <-- ALWAYS
                }

                return;
            }

            if(!_clientsFinishedLoading.Contains(clientId)) {
                _clientsFinishedLoading.Add(clientId);
            }

            if(_clientsFinishedLoading.Count == _networkManager.ConnectedClientsIds.Count) {
                _networkManager.SceneManager.OnSceneEvent -= OnSceneEvent;
                _isInGameplay = true;
                _customNetworkManager?.EnableGameplaySpawningAndSpawnAll();
                SessionNetworkBridge.Instance.FadeInAllClientsClientRpc();
            }
        }

        /// <summary>Joins a session by code; waits for host to publish Relay code (if remote), configures client transport, starts NGO Client.</summary>
        public async UniTask<string> JoinSessionByCodeAsync(string joinCode) {
            string lastSessionId = PlayerPrefs.GetString("LastSessionId", "");
            string lastCode = PlayerPrefs.GetString("LastJoinCode", "");

            // **1. Try silent reconnect if we have a session ID and code matches**
            if(!string.IsNullOrEmpty(lastSessionId) && lastCode == joinCode) {
                _isReconnecting = true;
                try {
                    SetFrontStatus(SessionPhase.ConnectingToRelay, "Reconnecting to session...");
                    ActiveSession = await _ugs.ReconnectToSessionAsync(lastSessionId);
                    HookSessionEvents();
                    await SetupClientTransportFromSessionAsync(); // See below
                    StartClientIfNeeded();
                    SetFrontStatus(SessionPhase.Connected, "Reconnected!");
                    return "Reconnecting to session...";
                } catch(Exception ex) {
                    Debug.Log($"[SessionManager] Reconnect failed (player removed?): {ex.Message}");
                    // TODO: double check this placement
                    _isReconnecting = true;
                    // Continue to normal join — player was kicked
                }
            }

            // **2. Normal join with cache bypass**
            await ForceLeaveAndCleanupAsync();

            try {
                SetFrontStatus(SessionPhase.CreatingLobby, "Joining lobby...");
                await JoinRemoteAsync(joinCode);
                return "Lobby joined. Waiting for host...";
            } catch(Exception ex) when(ex.Message.Contains("already a member") || ex.Message.Contains("409")) {
                // **3. FINAL FALLBACK: Try reconnect again (edge case)**
                if(!string.IsNullOrEmpty(lastSessionId)) {
                    try {
                        ActiveSession = await _ugs.ReconnectToSessionAsync(lastSessionId);
                        await SetupClientTransportFromSessionAsync();
                        StartClientIfNeeded();
                        return "RECONNECTED_AFTER_CACHE";
                    } catch { /* ignore */
                    }
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
                var utp = _networkManager.GetComponent<UnityTransport>();
                utp.SetRelayServerData(join.ToRelayServerData(RelayProtocol.DTLS));
            }
        }

        private async UniTask ForceLeaveAndCleanupAsync() {
            if(ActiveSession != null) {
                await _ugs.LeaveOrDeleteAsync(ActiveSession);
                UnhookSessionEvents();
                ActiveSession = null;
            }

            await _net.CleanupNetworkAsync();
            await UniTask.Delay(500);
        }

        /// <summary>Leaves (or deletes, if host) the UGS session, tears down NGO, returns to Main Menu. Safe to call multiple times.</summary>
        /// <param name="mainMenu">Scene name to load, defaults to "MainMenu".</param>
        public async UniTask LeaveToMainMenuAsync(string mainMenu = "MainMenu") {
            StopWatchingLobby("leave to menu");
            if(_isLeaving) return;
            _isLeaving = true;

            try {
                // Fade to black before leaving, unless in main menu already
                if(SceneTransitionManager.Instance != null && SceneManager.GetActiveScene().name != "MainMenu") {
                    await SceneTransitionManager.Instance.FadeOut().ToUniTask();
                }
                
                GameMenuManager.Instance.ShowInGameHudAfterPostMatch();

                // If we're the host and in gameplay, tell everyone else to fade out
                if(_networkManager != null && _networkManager.IsServer && _isInGameplay &&
                   SessionNetworkBridge.Instance != null) {
                    SessionNetworkBridge.Instance.FadeOutAllClientsClientRpc();
                    await UniTask.Delay(600);
                }

                // Store session reference and clear it immediately to prevent rejoining issues
                var sessionToLeave = ActiveSession;
                UnhookSessionEvents();
                ActiveSession = null;

                // Now cleanup network and leave the stored session
                await _net.CleanupNetworkAsync();
                if(sessionToLeave != null) {
                    await _ugs.LeaveOrDeleteAsync(sessionToLeave);
                }

                ResetSessionState();
                LoadMainMenu(mainMenu);

                await UniTask.Delay(500);
                if(SceneTransitionManager.Instance != null) {
                    await SceneTransitionManager.Instance.FadeIn().ToUniTask();
                }
            } finally {
                _isLeaving = false;
            }
        }

        #endregion

        #region Private - Host path

        /// <summary>Sets UnityTransport for localhost or Relay host and publishes relay code to UGS.</summary>
        private async UniTask SetupHostTransportAsync() {
            var utp = _networkManager.GetComponent<UnityTransport>();
            if(_localhost.IsLocalhostTesting()) {
                utp.SetConnectionData("127.0.0.1", 7777);
                return;
            }

            // Use the cached allocation we created in the lobby step
            if(_hostAllocation == null || string.IsNullOrEmpty(_relayJoinCode)) {
                var (alloc, code) = await _relay.CreateAllocationAsync(16);
                _hostAllocation = alloc;
                _relayJoinCode = code;
            }

            utp.SetRelayServerData(_hostAllocation.ToRelayServerData(RelayProtocol.DTLS));
        }

        private async UniTask PublishRelayCodeIfAnyAsync() {
            if(ActiveSession?.IsHost == true && !string.IsNullOrEmpty(_relayJoinCode) &&
               _relayJoinCode != "LOCALHOST") {
                var host = ActiveSession.AsHost();
                host.SetProperty("relayCode", new SessionProperty(_relayJoinCode, VisibilityPropertyOptions.Member));
                await host.SavePropertiesAsync();
            }
        }

        /// <summary>Starts NGO host if not already listening.</summary>
        private static void StartHostIfNeeded() {
            if(!_networkManager.IsListening)
                _networkManager.StartHost();
        }

        /// <summary>Arms scene-completion callback and asks server to load the game scene via NGO SceneManager.</summary>
        private void BeginNetworkSceneLoad() {
            if(!_networkManager.IsServer) return;
            _clientsFinishedLoading.Clear();
            _networkManager.SceneManager.OnSceneEvent += OnSceneEvent;
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
            PlayerPrefs.SetString("LastJoinCode", joinCode); // Optional: for fallback
            PlayerPrefs.Save();

            _isReconnecting = false;

            HookSessionEvents();
            PlayersChanged?.Invoke(ActiveSession.Players);

            SessionJoined?.Invoke(ActiveSession.Code);

            SetFrontStatus(SessionPhase.WaitingForRelay, "Waiting for host to start game...");
            _ = PollForGameStartAsync();
        }

        private async UniTask PollForGameStartAsync() {
            StopWatchingLobby();
            _lobbyWatchCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var ct = _lobbyWatchCts.Token;

            string relayCode = null;
            bool connected = false;

            void OnRelay(string c) {
                if(!string.IsNullOrWhiteSpace(c) && c.Length >= 6) {
                    relayCode = c;
                    StopWatchingLobby("relay code received");
                }
            }

            RelayCodeAvailable += OnRelay;

            try {
                while(!ct.IsCancellationRequested && !connected && string.IsNullOrEmpty(relayCode)) {
                    if(_networkManager?.IsClient == true || Phase >= SessionPhase.Connected) {
                        connected = true;
                        break;
                    }

                    try {
                        await ActiveSession.RefreshAsync();

                        // ── HOST GONE ──
                        if(ActiveSession == null ||
                           string.IsNullOrEmpty(ActiveSession.Host) ||
                           ActiveSession.Players.Count == 0) {
                            Debug.Log("[SessionManager] Host gone (poll) – reset UI");
                            HostDisconnected?.Invoke();
                            LobbyReset?.Invoke();
                            StopWatchingLobby("host gone (poll)");
                            return;
                        }

                        if(TryGetRelayCode(out var c)) {
                            relayCode = c;
                            RelayCodeAvailable?.Invoke(c);
                            break;
                        }

                        await UniTask.Delay(500, cancellationToken: ct);
                    } catch(Exception e) when(e.Message.Contains("not found") || e.Message.Contains("deleted")) {
                        Debug.Log("[SessionManager] Session deleted – host left");
                        HostDisconnected?.Invoke();
                        LobbyReset?.Invoke();
                        StopWatchingLobby("session deleted");
                        return;
                    } catch(Exception e) when(e.Message.Contains("Too Many Requests")) {
                        await UniTask.Delay(3000, cancellationToken: ct);
                    } catch(Exception e) {
                        Debug.Log($"[SessionManager] Poll error: {e.Message}");
                        break;
                    }
                }

                if(!string.IsNullOrEmpty(relayCode) && !connected)
                    await ConnectToRelayAsync(relayCode);
            } catch(OperationCanceledException) { /* normal */
            } finally {
                RelayCodeAvailable -= OnRelay;
                StopWatchingLobby("poll finished");
            }
        }

        private async UniTask ConnectToRelayAsync(string relayCode) {
            relayCode = relayCode?.Trim();

            // Validate relay code before attempting connection
            if(string.IsNullOrEmpty(relayCode) || relayCode.Length < 6) {
                Debug.LogWarning($"[SessionManager] Invalid relay code received: '{relayCode}' - not connecting");
                SetFrontStatus(SessionPhase.WaitingForRelay, "Waiting for valid connection code...");
                return; // Don't attempt connection with invalid code
            }

            if(string.Equals(relayCode, "LOCALHOST", StringComparison.OrdinalIgnoreCase)) {
                var utp = _networkManager.GetComponent<UnityTransport>();
                utp.SetConnectionData("127.0.0.1", 7777);
                SetFrontStatus(SessionPhase.StartingClient, "Connecting to local host...");
                StartClientIfNeeded();
                SetFrontStatus(SessionPhase.LoadingScene, "Waiting for all players...");
                ArmSceneCompletion();
                return;
            }

            try {
                SetFrontStatus(SessionPhase.ConnectingToRelay, "Connecting to host...");
                var join = await _relay.JoinAllocationAsync(relayCode);
                var utp = _networkManager.GetComponent<UnityTransport>();
                utp.SetRelayServerData(join.ToRelayServerData(RelayProtocol.DTLS));
                StartClientIfNeeded();
                SetFrontStatus(SessionPhase.LoadingScene, "Waiting for all players...");
                ArmSceneCompletion();
            } catch(Exception e) {
                SetFrontStatus(SessionPhase.Error, $"Failed to connect: {e.Message}");
                Debug.LogError($"[SessionManager] Failed to connect to relay: {e}");
            }
        }

        private async void StartClientIfNeeded() {
            if(_networkManager == null)
                _networkManager = NetworkManager.Singleton;

            // Already a client or running as host? Nothing to do.
            if(_networkManager.IsClient || _networkManager.IsHost || _startingClient)
                return;

            _startingClient = true;

            if(SceneTransitionManager.Instance != null)
                SceneTransitionManager.Instance.FadeOutAsync().Forget();

            await UniTask.Delay(500);

            var ok = _networkManager.StartClient();
            if(!ok)
                Debug.LogError("[SessionManager] StartClient failed (transport not configured or already running).");
            _startingClient = false;
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
            if(ActiveSession == null) return false;
            if(ActiveSession.Properties.TryGetValue(RelayCodeKey, out var prop) && !string.IsNullOrEmpty(prop.Value)) {
                code = prop.Value;
                return true;
            }

            return false;
        }

        private void ResetSessionState() {
            StopWatchingLobby("reset");
            _net.UnhookSceneCallbacks(OnNetworkSceneLoadComplete);
            _loadingGameScene = false;
            _relayJoinCode = null;
            _hostAllocation = null; // Also clear the allocation
            _clientsFinishedLoading.Clear();
            _isInGameplay = false; // Reset gameplay flag
            _hasCompletedInitialLoad = false; // Reset initial load flag

            // Clear spawner state
            _networkManager?.GetComponent<CustomNetworkManager>()?.ResetSpawningState();

            // ActiveSession should already be null from LeaveToMainMenuAsync
            if(ActiveSession != null) {
                UnhookSessionEvents();
                ActiveSession = null;
            }

            Debug.Log("[SessionManager] Clean slate – ready for new session");
        }

        public void HandleLateJoiner(ulong clientId) {
            if(!_networkManager.IsServer || !_isInGameplay) return;

            Debug.Log($"[SessionManager] Handling late joiner {clientId}");

            // Spawn the player for the late joiner without affecting others
            if(_customNetworkManager != null) {
                _customNetworkManager.SpawnPlayerFor(clientId);
            }

            // Send them any necessary game state updates
            // This would depend on your game's specific needs
        }

        private static void LoadMainMenu(string scene) {
            if(string.IsNullOrEmpty(scene) || SceneManager.GetActiveScene().name == scene) return;
            SceneManager.LoadScene(scene, LoadSceneMode.Single);
        }

        private void HookSessionEvents() {
            _ugs.HookEvents(ActiveSession,
                onChanged: () => {
                    // 1. Host left → session.Host becomes null or Players empty
                    if(ActiveSession == null ||
                       string.IsNullOrEmpty(ActiveSession.Host) ||
                       ActiveSession.Players.Count == 0) {
                        Debug.Log("[SessionManager] Host left – resetting UI");
                        HostDisconnected?.Invoke();
                        LobbyReset?.Invoke();
                        StopWatchingLobby("host left (onChanged)");
                        return; // ← stop further processing
                    }

                    PlayersChanged?.Invoke(ActiveSession?.Players);
                },
                onJoined: _ => PlayersChanged?.Invoke(ActiveSession?.Players),
                onLeaving: player => {
                    // 2. Any player leaves – we will check on next refresh
                    Debug.Log("[SessionManager] Player leaving – will verify host");
                },
                onPropsChanged: () => {
                    if(ActiveSession == null) return;
                    if(ActiveSession.Properties.TryGetValue(RelayCodeKey, out var p) &&
                       !string.IsNullOrEmpty(p.Value))
                        RelayCodeAvailable?.Invoke(p.Value);
                });
        }

        private void UnhookSessionEvents() {
            _ugs.UnhookEvents(ActiveSession,
                onChanged: () => PlayersChanged?.Invoke(ActiveSession?.Players),
                onJoined: _ => PlayersChanged?.Invoke(ActiveSession?.Players),
                onLeaving: _ => PlayersChanged?.Invoke(ActiveSession?.Players),
                onPropsChanged: () => { });
        }

        #endregion

        #region Scene callback

        /// <summary>Server-side: finishes bootstrapping after game scene loads (spawns, UI reveal).</summary>
        private void OnNetworkSceneLoadComplete(string scene, LoadSceneMode mode,
            List<ulong> completed, List<ulong> timedOut) {
            if(!_loadingGameScene) return;
            DisarmSceneCompletion();
            Debug.Log(
                $"[SessionManager] Scene load complete. Scene: {scene}, IsServer: {NetworkManager.Singleton.IsServer}");
        }

        #endregion
    }
}