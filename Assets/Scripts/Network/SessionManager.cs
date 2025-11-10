using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Network.Core;
using Network.Relay;
using Network.UGS;
using Relays;
using Singletons;
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

        // ===== Services =====
        private readonly IUgsSessionService _ugs = new UgsSessionService();
        private readonly INetworkLifecycle _net = new NetworkLifecycle();
        private readonly IPlayerIdentity _ids = new PlayerIdentity();
        private readonly ILocalhostPolicy _localhost = new LocalhostPolicy();
        private readonly IRelayConnector _relay = new RelayConnector();
        private readonly ISceneCoordinator _scenes = new SceneCoordinator();

        // ===== Unity lifecycle =====
        private async void Start() {
            try {
                DontDestroyOnLoad(gameObject);
                await _ugs.InitializeAsync();
            } catch(Exception e) {
                Debug.LogException(e);
            }
        }

        private void OnDestroy() => UnhookSessionEvents();

        // ===== Public API =====
        /// <summary>Leaves (or deletes, if host) the UGS session, tears down NGO, returns to Main Menu. Safe to call multiple times.</summary>
        /// <param name="mainMenu">Scene name to load, defaults to "MainMenu".</param>
        public async UniTask LeaveToMainMenuAsync(string mainMenu = "MainMenu") {
            if(_isLeaving) return;
            _isLeaving = true;
            try {
                await NotifyClientsToLeaveAsync();
                await _net.CleanupNetworkAsync();
                await _ugs.LeaveOrDeleteAsync(ActiveSession);
                ResetSessionState();
                LoadMainMenu(mainMenu);
            } finally {
                _isLeaving = false;
            }
        }

        /// <summary>Creates a UGS session (no network transport started yet). Returns join code displayed to others.</summary>
        public async UniTask<string> StartSessionAsHost() {
            var props = _localhost.IsLocalhostTesting()
                ? new Dictionary<string, PlayerProperty>
                    { { PlayerNameKey, new PlayerProperty("Host (Editor)", VisibilityPropertyOptions.Member) } }
                : await _ids.GetPlayerPropertiesAsync(PlayerNameKey);

            var options = new SessionOptions {
                MaxPlayers = 16, IsLocked = false, IsPrivate = false, PlayerProperties = props
            }.WithRelayNetwork();

            ActiveSession = await _ugs.CreateAsync(options);
            HookSessionEvents();
            PlayersChanged?.Invoke(ActiveSession.Players);
            return ActiveSession.Code;
        }

        /// <summary>Allocates Relay + configures transport, publishes join code, then starts NGO Host and loads the game.</summary>
        public async UniTask BeginGameplayAsHostAsync() {
            await SetupHostTransportAndPublishRelayAsync();
            StartHostIfNeeded();
            BeginNetworkSceneLoad();
        }

        /// <summary>Joins a session by code; waits for host to publish Relay code (if remote), configures client transport, starts NGO Client.</summary>
        public async UniTask<string> JoinSessionByCodeAsync(string joinCode) {
            if(_localhost.IsLocalhostTesting()) {
                await JoinLocalhostAsync(joinCode);
                return "Connected to Local Host (Editor)";
            }

            await JoinRemoteAsync(joinCode);
            return "Lobby joined";
        }

        // ====== Private: Host path ======
        /// <summary>Sets UnityTransport for localhost or Relay host and publishes relay code to UGS.</summary>
        private async UniTask SetupHostTransportAndPublishRelayAsync() {
            var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();

            if(_localhost.IsLocalhostTesting()) {
                utp.ConnectionData.Address = "127.0.0.1";
                utp.ConnectionData.Port = 7777;
                return;
            }

            var (alloc, code) = await _relay.CreateAllocationAsync(16);
            utp.SetRelayServerData(alloc.ToRelayServerData(RelayProtocol.DTLS));
            _relayJoinCode = code;

            if(ActiveSession?.IsHost == true) {
                var host = ActiveSession.AsHost();
                host.SetProperty(RelayCodeKey, new SessionProperty(_relayJoinCode, VisibilityPropertyOptions.Member));
                await host.SavePropertiesAsync();
            }
        }

        /// <summary>Starts NGO host if not already listening.</summary>
        private static void StartHostIfNeeded() {
            if(!NetworkManager.Singleton.IsListening)
                NetworkManager.Singleton.StartHost();
        }

        /// <summary>Arms scene-completion callback and asks server to load the game scene via NGO SceneManager.</summary>
        private void BeginNetworkSceneLoad() {
            _loadingGameScene = true;
            _net.HookSceneCallbacks(OnNetworkSceneLoadComplete);
            _scenes.LoadGameSceneServer(GameSceneName);
        }

        // ====== Private: Client path ======
        /// <summary>Joins localhost host (no Relay), sets IP/port, starts NGO client.</summary>
        private async UniTask JoinLocalhostAsync(string joinCode) {
            var playerName = _localhost.GetLocalEditorName();
            var props = new Dictionary<string, PlayerProperty> {
                { PlayerNameKey, new PlayerProperty(playerName, VisibilityPropertyOptions.Member) }
            };
            ActiveSession = await _ugs.JoinByCodeAsync(joinCode, new JoinSessionOptions { PlayerProperties = props });
            HookSessionEvents();
            PlayersChanged?.Invoke(ActiveSession.Players);

            var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
            utp.ConnectionData.Address = "127.0.0.1";
            utp.ConnectionData.Port = 7777;
            NetworkManager.Singleton.StartClient();
            ArmSceneCompletionIfNeeded();
        }

        /// <summary>Joins remote UGS session, waits for Relay code, configures transport, starts NGO client.</summary>
        private async UniTask JoinRemoteAsync(string joinCode) {
            var props = await _ids.GetPlayerPropertiesAsync(PlayerNameKey);
            ActiveSession = await _ugs.JoinByCodeAsync(joinCode, new JoinSessionOptions { PlayerProperties = props });
            HookSessionEvents();
            PlayersChanged?.Invoke(ActiveSession.Players);

            var relayCode = await WaitForRelayCodeOrPollAsync();
            var join = await _relay.JoinAllocationAsync(relayCode);
            var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
            utp.SetRelayServerData(join.ToRelayServerData(RelayProtocol.DTLS));
            if(!NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsHost)
                NetworkManager.Singleton.StartClient();
            ArmSceneCompletionIfNeeded();
        }

        /// <summary>Ensures scene-completion callback is hooked only once during client-side load.</summary>
        private void ArmSceneCompletionIfNeeded() {
            if(_loadingGameScene) return;
            _loadingGameScene = true;
            _net.HookSceneCallbacks(OnNetworkSceneLoadComplete);
        }

        // ====== Session events & helpers ======
        /// <summary>Returns the published Relay code immediately if available, otherwise polls session properties until received or timeout.</summary>
        private async UniTask<string> WaitForRelayCodeOrPollAsync() {
            if(TryGetRelayCode(out var code)) return code;

            using var cts = new CancellationTokenSource();
            string captured = null;

            RelayCodeAvailable += OnRelay;
            try {
                await PollRelayCodeAsync(cts.Token);
            } catch(OperationCanceledException) { /* expected when event fires */
            } finally {
                RelayCodeAvailable -= OnRelay;
            }

            return captured;

            void OnRelay(string c) {
                captured = c;
                cts.Cancel();
            }
        }
        
        private async UniTask PollRelayCodeAsync(CancellationToken ct) {
            const int tries = 40;
            for(var i = 0; i < tries; i++) {
                ct.ThrowIfCancellationRequested();
                try {
                    await ActiveSession.RefreshAsync();
                } catch(Exception e) {
                    Debug.LogException(e);
                }

                if(TryGetRelayCode(out var c)) {
                    RelayCodeAvailable?.Invoke(c);
                    return;
                }

                await UniTask.Delay(200, cancellationToken: ct);
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

        private async UniTask NotifyClientsToLeaveAsync() {
            if(!NetworkManager.Singleton?.IsServer ?? true) return;
            var relay = FindFirstObjectByType<SessionExitRelay>();
            if(relay?.IsSpawned == true) {
                relay.ReturnToMenuClientRpc();
                await UniTask.Delay(150);
            }
        }

        private void ResetSessionState() {
            _net.UnhookSceneCallbacks(OnNetworkSceneLoadComplete);
            _loadingGameScene = false;
            _relayJoinCode = null;

            // Clear spawner state
            FindFirstObjectByType<CustomNetworkManager>()?.ResetSpawningState();
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

        // ====== Scene callback ======
        /// <summary>Server-side: finishes bootstrapping after game scene loads (spawns, UI reveal).</summary>
        private void OnNetworkSceneLoadComplete(string scene, LoadSceneMode mode,
            List<ulong> completed, List<ulong> timedOut) {
            if(!_loadingGameScene) return;
            _loadingGameScene = false;
            _net.UnhookSceneCallbacks(OnNetworkSceneLoadComplete);

            if(NetworkManager.Singleton.IsServer) {
                var spawner = FindFirstObjectByType<CustomNetworkManager>();
                StartCoroutine(InitializeAfterSceneLoad(spawner));
            }

            var gameMenu = GameMenuManager.Instance;
            if(gameMenu != null && gameMenu.TryGetComponent(out UIDocument doc)) {
                var root = doc.rootVisualElement;
                var container = root?.Q<VisualElement>("root-container");
                if(container != null) container.style.display = DisplayStyle.Flex;
            }
        }

        private static IEnumerator InitializeAfterSceneLoad(CustomNetworkManager spawner) {
            yield return new WaitForEndOfFrame();
            yield return new WaitForFixedUpdate();
            spawner?.EnableGameplaySpawningAndSpawnAll();
        }
    }
}