using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Network.Core;
using Network.Relay;
using Network.Rpc;
using Network.Singletons;
using Network.UGS;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Multiplayer;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
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
            if(_networkManager != null) {
                _networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
                _networkManager.OnClientDisconnectCallback += OnClientDisconnected;
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

        private void OnClientDisconnected(ulong clientId) {
            // If we're a client and we got disconnected (not a voluntary leave)
            if(!_networkManager.IsServer && clientId == _networkManager.LocalClientId && !_isLeaving) {
                Debug.Log("[SessionManager] Disconnected from host - returning to menu");
                HandleUnexpectedDisconnect().Forget();
            }
        }

        private async UniTaskVoid HandleUnexpectedDisconnect() {
            SetFrontStatus(SessionPhase.ReturningToMenu, "Lost connection. Returning to main menu...");
            await UniTask.Delay(100); // Small delay to let disconnect process

            HUDManager.Instance?.HideHUD();
            if(GameMenuManager.Instance?.IsPaused == true) {
                GameMenuManager.Instance.TogglePause();
            }

            ResetSessionState();
            LoadMainMenu("MainMenu");
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
            
            SetFrontStatus(SessionPhase.AllocatingRelay, "Setting up relay connection...");
            await EnsureRelayCodePublishedForLobbyAsync();
            await SetupHostTransportAsync();
            StartHostIfNeeded();

            if(_networkManager.IsServer)
                await PublishRelayCodeIfAnyAsync();

            SetFrontStatus(SessionPhase.LoadingScene, "Loading game scene...");
            BeginNetworkSceneLoad();
        }

        /// <summary>Joins a session by code; waits for host to publish Relay code (if remote), configures client transport, starts NGO Client.</summary>
        public async UniTask<string> JoinSessionByCodeAsync(string joinCode) {
            // Don’t rejoin the same lobby
            if(ActiveSession != null && ActiveSession.Code == joinCode)
                return "Already in lobby";

            if(_localhost.IsLocalhostTesting()) {
                SetFrontStatus(SessionPhase.ConnectingToRelay, "Connecting to local host...");
                await JoinLocalhostAsync(joinCode);
                SetFrontStatus(SessionPhase.Connected, "Connected to local host.");
                return "Connected to Local Host (Editor)";
            }

            SetFrontStatus(SessionPhase.CreatingLobby, "Joining lobby...");
            await JoinRemoteAsync(joinCode);
            return "Lobby joined. Waiting for host to start the game...";
        }

        /// <summary>Leaves (or deletes, if host) the UGS session, tears down NGO, returns to Main Menu. Safe to call multiple times.</summary>
        /// <param name="mainMenu">Scene name to load, defaults to "MainMenu".</param>
        public async UniTask LeaveToMainMenuAsync(string mainMenu = "MainMenu") {
            StopWatchingLobby("leave to menu");
            if(_isLeaving) return;
            _isLeaving = true;
            try {
                await _net.CleanupNetworkAsync();
                await _ugs.LeaveOrDeleteAsync(ActiveSession);
                ResetSessionState();
                LoadMainMenu(mainMenu);
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
            _loadingGameScene = true;
            _net.HookSceneCallbacks(OnNetworkSceneLoadComplete);
            _scenes.LoadGameSceneServer(GameSceneName);
        }

        #endregion

        #region Private - Client path

        /// <summary>Joins localhost host (no Relay), sets IP/port, starts NGO client.</summary>
        private async UniTask JoinLocalhostAsync(string joinCode) {
            var playerName = _localhost.GetLocalEditorName();
            var props = new Dictionary<string, PlayerProperty> {
                { PlayerNameKey, new PlayerProperty(playerName, VisibilityPropertyOptions.Member) }
            };
            ActiveSession = await _ugs.JoinByCodeAsync(joinCode, new JoinSessionOptions { PlayerProperties = props });
            HookSessionEvents();
            PlayersChanged?.Invoke(ActiveSession.Players);

            var utp = _networkManager.GetComponent<UnityTransport>();
            utp.ConnectionData.Address = "127.0.0.1";
            utp.ConnectionData.Port = 7777;
            StartClientIfNeeded();
            ArmSceneCompletion();
        }

        /// <summary>Joins remote UGS session, waits for Relay code, configures transport, starts NGO client.</summary>
        private async UniTask JoinRemoteAsync(string joinCode) {
            var props = await _ids.GetPlayerPropertiesAsync(PlayerNameKey);
            ActiveSession = await _ugs.JoinByCodeAsync(joinCode, new JoinSessionOptions { PlayerProperties = props });
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

            void OnRelay(string c) {
                if(!string.IsNullOrWhiteSpace(c) && c.Length >= 6) {
                    relayCode = c;
                    StopWatchingLobby("relay code received");
                }
            }

            RelayCodeAvailable += OnRelay;

            try {
                // Poll until we get a valid relay code
                await PollRelayCodeAsync(ct);

                // Wait for the code to be set by the event
                await UniTask.WaitUntil(() => relayCode != null || ct.IsCancellationRequested, cancellationToken: ct);

                if(string.IsNullOrEmpty(relayCode)) {
                    Debug.LogWarning("[SessionManager] Timed out waiting for host to start game");
                    SetFrontStatus(SessionPhase.WaitingForRelay, "Host hasn't started yet. Still waiting...");
                    return;
                }

                // Now we have a valid relay code - connect!
                await ConnectToRelayAsync(relayCode);
            } catch(OperationCanceledException) {
                // Expected when relay code received or leaving - check if we got the code
                if(!string.IsNullOrEmpty(relayCode)) {
                    await ConnectToRelayAsync(relayCode);
                } else {
                    Debug.Log("[SessionManager] Polling canceled (normal behavior)");
                }
            } catch(Exception e) {
                Debug.LogWarning($"[SessionManager] Error while waiting for game start: {e.Message}");
            } finally {
                RelayCodeAvailable -= OnRelay;
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
                SetFrontStatus(SessionPhase.LoadingScene, "Loading game scene...");
                ArmSceneCompletion();
                return;
            }

            try {
                SetFrontStatus(SessionPhase.ConnectingToRelay, "Connecting to host...");
                var join = await _relay.JoinAllocationAsync(relayCode);
                var utp = _networkManager.GetComponent<UnityTransport>();
                utp.SetRelayServerData(join.ToRelayServerData(RelayProtocol.DTLS));
                StartClientIfNeeded();
                SetFrontStatus(SessionPhase.LoadingScene, "Loading game scene...");
                ArmSceneCompletion();
            } catch(Exception e) {
                SetFrontStatus(SessionPhase.Error, $"Failed to connect: {e.Message}");
                Debug.LogError($"[SessionManager] Failed to connect to relay: {e}");
            }
        }

        private void StartClientIfNeeded() {
            if(_networkManager == null)
                _networkManager = NetworkManager.Singleton;

            // Already a client or running as host? Nothing to do.
            if(_networkManager.IsClient || _networkManager.IsHost || _startingClient)
                return;

            _startingClient = true;
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

        /// <summary>Returns the published Relay code immediately if available, otherwise waits for callback or polls as fallback.</summary>
        private async UniTask<string> WaitForRelayCodeOrPollAsync() {
            if(TryGetRelayCode(out var code)) return code;

            // keep one CTS we can cancel once we have what we need
            StopWatchingLobby();
            _lobbyWatchCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var ct = _lobbyWatchCts.Token;

            string captured = null;

            void OnRelay(string c) {
                if(!string.IsNullOrWhiteSpace(c)) {
                    captured = c;
                    StopWatchingLobby("relay code received"); // ← cancel the poller immediately
                }
            }

            RelayCodeAvailable += OnRelay;
            try {
                // fire-and-forget poller (will be cancelled by StopWatchingLobby)
                _ = PollRelayCodeAsync(ct);

                // wait until canceled (by either timeout or receiving code)
                await UniTask.WaitUntil(() => captured != null || ct.IsCancellationRequested, cancellationToken: ct);
            } catch { /* ignored */
            } finally {
                RelayCodeAvailable -= OnRelay;
            }

            return captured;
        }

        private async UniTask PollRelayCodeAsync(CancellationToken ct) {
            var delayMs = 500;
            while(!ct.IsCancellationRequested) {
                // bail if we’re already transitioning to gameplay
                if(_loadingGameScene || _networkManager?.IsClient == true ||
                   Phase >= SessionPhase.StartingClient) break;

                try {
                    await ActiveSession.RefreshAsync();
                    if(TryGetRelayCode(out var c)) {
                        RelayCodeAvailable?.Invoke(c);
                        return;
                    }

                    await UniTask.Delay(delayMs, cancellationToken: ct);
                    delayMs = Math.Min(delayMs * 2, 3000);
                } catch(Exception e) {
                    if(e.Message.Contains("Too Many Requests")) {
                        delayMs = Math.Min(delayMs * 2, 5000);
                        await UniTask.Delay(delayMs, cancellationToken: ct);
                    } else {
                        // Normal, e.g., lobby deleted during scene switch
                        Debug.Log($"[SessionManager] PollRelayCodeAsync: {e.Message}");
                        await UniTask.Delay(1500, cancellationToken: ct);
                    }
                }
            }
        }

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

            // Clear spawner state
            _networkManager.GetComponent<CustomNetworkManager>()?.ResetSpawningState();
            ActiveSession = null;
            Debug.Log("[SessionManager] Clean slate – ready for new session");
        }

        private static void LoadMainMenu(string scene) {
            if(string.IsNullOrEmpty(scene) || SceneManager.GetActiveScene().name == scene) return;
            SceneManager.LoadScene(scene, LoadSceneMode.Single);
        }

        private void HookSessionEvents() {
            _ugs.HookEvents(ActiveSession,
                onChanged: () => PlayersChanged?.Invoke(ActiveSession?.Players),
                onJoined: _ => PlayersChanged?.Invoke(ActiveSession?.Players),
                onLeaving: _ => PlayersChanged?.Invoke(ActiveSession?.Players),
                onPropsChanged: () => {
                    if(ActiveSession == null) return;
                    if(ActiveSession.Properties.TryGetValue(RelayCodeKey, out var p) && !string.IsNullOrEmpty(p.Value))
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

            if(_networkManager.IsServer) {
                var spawner = _networkManager.GetComponent<CustomNetworkManager>();
                Debug.Log($"[SessionManager] Found spawner: {spawner != null}");
                spawner.EnableGameplaySpawningAndSpawnAll();
            }
        }

        #endregion
    }
}