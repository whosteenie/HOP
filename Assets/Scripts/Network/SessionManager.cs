using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Relays;
using Singletons;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UnityUtils;

namespace Network {
    public class SessionManager : Singleton<SessionManager>
    {
        // ====== Public surface ======

        /// <summary>Live UGS session (null if not in a lobby).</summary>
        public ISession ActiveSession { get; private set; }

        /// <summary>Raised whenever the session's player list should be re-rendered.</summary>
        public event Action<IReadOnlyList<IReadOnlyPlayer>> PlayersChanged;

        /// <summary>Raised on client when the host publishes a Relay join code.</summary>
        public event Action<string> RelayCodeAvailable;

        // ====== Config / state ======

        private const string GameSceneName = "Game";
        private const string PlayerNamePropertyKey = "playerName";
        private const string RelayCodePropertyKey = "relayCode";

        private bool _isInitialized;
        private bool _loadingGameScene;
        private bool _sessionEventsHooked;
        private string _relayJoinCode; // host-generated
        private bool _isLeaving;
    
        // ====== Unity lifecycle ======

        private async void Start() {
            try {
                DontDestroyOnLoad(gameObject);
                await InitializeUnityServices();
            } catch(Exception e) {
                Debug.LogException(e);
            }
        }

        private void OnDestroy() {
            UnhookSessionEvents(ActiveSession);
        }

        // ====== Init / auth ======

        private async UniTask InitializeUnityServices() {
            if(_isInitialized) return;

            try {
                await UnityServices.InitializeAsync();
                if (!AuthenticationService.Instance.IsSignedIn)
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();

                _isInitialized = true;
                Debug.Log($"Unity Services initialized. PlayerID: {AuthenticationService.Instance.PlayerId}");
            }
            catch(Exception e) {
                Debug.LogException(e);
            }
        }

        private async UniTask<Dictionary<string, PlayerProperty>> GetPlayerProperties() {
            // PlayerName API may be unset; fall back to PlayerId
            string playerName;
            try {
                playerName = await AuthenticationService.Instance.GetPlayerNameAsync();
                if(string.IsNullOrWhiteSpace(playerName))
                    playerName = AuthenticationService.Instance.PlayerName;
            } catch(Exception e) {
                Debug.LogException(e);
                playerName = "Player(" + AuthenticationService.Instance.PlayerId[Math.Max(0, AuthenticationService.Instance.PlayerId.Length - 4)] + ")";
            }

            var nameProp = new PlayerProperty(playerName, VisibilityPropertyOptions.Member);
            return new Dictionary<string, PlayerProperty> { { PlayerNamePropertyKey, nameProp } };
        }
        
        private async UniTask NotifyClientsToLeaveAsync() {
            if(!NetworkManager.Singleton?.IsServer ?? true) return;

            var relay = FindFirstObjectByType<SessionExitRelay>();
            if(relay?.IsSpawned == true) {
                relay.ReturnToMenuClientRpc();
                await UniTask.Delay(150);
            }
        }
        
        private async UniTask CleanupNetworkGameObjectsAsync() {
            if(NetworkManager.Singleton == null) {
                return;
            }
    
            if(!NetworkManager.Singleton.IsListening) {
                return;
            }

    
            ClearScenePlacedObjectsCache();
            NetworkManager.Singleton.Shutdown();
    
            int waitFrames = 0;
            while (NetworkManager.Singleton.ShutdownInProgress && waitFrames < 100) {
                await UniTask.Yield();
                waitFrames++;
            }
    
            // CRITICAL: Reset transport to default state
            var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (utp != null) {
                utp.SetConnectionData("127.0.0.1", 7777);
            }
    
            await UniTask.Delay(500);
    
        }
        
        private void ClearScenePlacedObjectsCache() {
            var mgr = NetworkManager.Singleton?.SpawnManager;

            var field = mgr?.GetType().GetField("m_ScenePlacedObjects",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if(field?.GetValue(mgr) is IDictionary dict)
                dict.Clear();
        }
        
        private async UniTask LeaveUgsSessionAsync() {
            if(ActiveSession == null) return;

            try {
                if(ActiveSession.IsHost)
                    await ActiveSession.AsHost().DeleteAsync();
                else
                    await ActiveSession.LeaveAsync();

                await UniTask.Delay(300);
            } catch(Exception e) {
                Debug.LogWarning($"Leave session error: {e.Message}");
            }

            UnhookSessionEvents(ActiveSession);
            ActiveSession = null;
            _relayJoinCode = null;
        }
        
        private void ResetSessionManagerState() {
            UnhookNetworkSceneCallbacks();
            UnhookSessionEvents(ActiveSession);

            _loadingGameScene = false;
            _sessionEventsHooked = false;
            _relayJoinCode = null;
            ActiveSession = null;

            FindFirstObjectByType<CustomNetworkManager>()?.ResetSpawningState();
            Debug.Log("[SessionManager] Clean slate – ready for new session");
        }
        
        private void LoadMainMenuScene(string scene) {
            if(string.IsNullOrEmpty(scene) || SceneManager.GetActiveScene().name == scene) return;
            SceneManager.LoadScene(scene, LoadSceneMode.Single);
        }
        
        private void HookNetworkSceneCallbacks() {
            if(NetworkManager.Singleton?.SceneManager == null) return;
            var sm = NetworkManager.Singleton.SceneManager;

            sm.OnLoadEventCompleted -= OnNetworkSceneLoadComplete;
            sm.OnLoadEventCompleted += OnNetworkSceneLoadComplete;
        }

        private void UnhookNetworkSceneCallbacks() {
            if(NetworkManager.Singleton?.SceneManager == null) return;
            var sm = NetworkManager.Singleton.SceneManager;

            sm.OnLoadEventCompleted -= OnNetworkSceneLoadComplete;
        }
    
        /// <summary>
        /// Cleanly shuts down NGO/Relay and leaves (or deletes, if host) the UGS session,
        /// then optionally loads MainMenu.
        /// Safe to call multiple times.
        /// </summary>
        public async UniTask LeaveToMainMenuAsync(string mainMenuScene = "MainMenu") {
            if(_isLeaving) return;
            _isLeaving = true;

            try {
                await NotifyClientsToLeaveAsync();
                await CleanupNetworkGameObjectsAsync();
                await LeaveUgsSessionAsync();
                ResetSessionManagerState();
                LoadMainMenuScene(mainMenuScene);
            } catch(Exception e) {
                Debug.LogException(e);
            } finally {
                _isLeaving = false;
            }
        }

        // ====== Host flow ======

        /// <summary>Create UGS session (no network start yet). Returns the lobby join code shown to others.</summary>
        public async UniTask<string> StartSessionAsHost() {
            try {
                if (!_isInitialized) await InitializeUnityServices();

                var playerProps = IsLocalhostTesting()
                    ? new Dictionary<string, PlayerProperty> { { PlayerNamePropertyKey, new PlayerProperty("Host (Editor)", VisibilityPropertyOptions.Member) } }
                    : await GetPlayerProperties();

                var options = new SessionOptions {
                    MaxPlayers = 16,
                    IsLocked = false,
                    IsPrivate = false,
                    PlayerProperties = playerProps
                }.WithRelayNetwork();
            
                ActiveSession = await MultiplayerService.Instance.CreateSessionAsync(options);
                HookSessionEvents(ActiveSession);
            
                PlayersChanged?.Invoke(ActiveSession.Players);

                Debug.Log($"Session created: {ActiveSession.Id}  Code: {ActiveSession.Code}");
                return ActiveSession.Code;
            } catch (Exception e) {
                Debug.LogException(e);
                return null;
            }
        }

        /// <summary>Allocate Relay, publish join code to the session, start NGO host, then load the game scene.</summary>
        public async UniTask BeginGameplayAsHostAsync() {
            // 1) Host sets up Relay & transport
            await ConfigureRelayForHostAsync();

            // 2) Publish relay code to session (clients will wait for this)
            if(ActiveSession?.IsHost == true) {
                var hostSession = ActiveSession.AsHost();
                hostSession.SetProperty(RelayCodePropertyKey,
                    new SessionProperty(_relayJoinCode, VisibilityPropertyOptions.Member));
                await hostSession.SavePropertiesAsync();
            }

            // 3) Start NGO host + transition scenes
            if(!NetworkManager.Singleton.IsListening)
                NetworkManager.Singleton.StartHost();

            await UniTask.Yield();

            _loadingGameScene = true;
            NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += OnNetworkSceneLoadComplete;
            NetworkManager.Singleton.SceneManager.LoadScene(GameSceneName, LoadSceneMode.Single);
        }

        // ====== Client flow ======

        private bool IsLocalhostTesting() {
            // 1. ParrelSync: Cloned project path
            if(Application.dataPath.Contains("_clone"))
                return true;

            // 2. Multiple Unity instances on same machine
            var processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            var processes = System.Diagnostics.Process.GetProcessesByName(processName);
            if(processes.Length > 1)
                return true;

            // 3. Local IP (127.0.0.1)
            if(NetworkManager.Singleton.NetworkConfig.NetworkTransport is UnityTransport utp) {
                if(utp.ConnectionData.Address is "127.0.0.1" or "localhost")
                    return true;
            }

            return false;
        }
    
        private string GetLocalEditorName() {
            // Option 1: ParrelSync clone index
            if(Application.dataPath.Contains("_clone")) {
                var parts = Application.dataPath.Split('_');
                if (parts.Length > 1 && int.TryParse(parts[^1], out int index))
                    return $"Editor {index + 1}"; // _clone0 → Editor 1
            }

            // Option 2: Process ID fallback
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            return $"Editor {pid}";
        }
    
        /// <summary>Join session by code, wait until host publishes Relay code, then start NGO client.</summary>
        public async UniTask<string> JoinSessionByCodeAsync(string joinCode) {
            try {
                if(!_isInitialized) await InitializeUnityServices();
            
                if (IsLocalhostTesting()) {
                    string localName = GetLocalEditorName();
                    var playerPropsLocal = new Dictionary<string, PlayerProperty>() {
                        { PlayerNamePropertyKey, new PlayerProperty(localName, VisibilityPropertyOptions.Member) }
                    };
                    var joinOptionsLocal = new JoinSessionOptions {
                        PlayerProperties = playerPropsLocal
                    };
                
                    // Skip Relay, use direct connect
                    var utpLocal = NetworkManager.Singleton.GetComponent<UnityTransport>();
                    utpLocal.ConnectionData.Address = "127.0.0.1";
                    utpLocal.ConnectionData.Port = 7777; // Match host

                    ActiveSession = await MultiplayerService.Instance.JoinSessionByCodeAsync(joinCode, joinOptionsLocal);
                    HookSessionEvents(ActiveSession);
                    PlayersChanged?.Invoke(ActiveSession.Players);

                    NetworkManager.Singleton.StartClient();
                    return "Connected to Local Host (Editor)";
                }

                var playerProps = await GetPlayerProperties();
                var joinOptions = new JoinSessionOptions {
                    PlayerProperties = playerProps
                };
            
                ActiveSession = await MultiplayerService.Instance.JoinSessionByCodeAsync(joinCode, joinOptions);
                HookSessionEvents(ActiveSession);

                // Immediately push the current players list to UI
                PlayersChanged?.Invoke(ActiveSession.Players);

                // Try to obtain relay code immediately; if not there yet, wait briefly
                if(!TryGetRelayCode(out var relayJoinCode)) {
                    using var cts = new CancellationTokenSource();
                    string captured = null;

                    void OnRelay(string code) { captured = code; cts.Cancel(); }
                    RelayCodeAvailable += OnRelay;

                    try { await WaitForRelayCodeAsync(cts.Token); }
                    catch (OperationCanceledException) { /* expected if event fired */ }
                    finally { RelayCodeAvailable -= OnRelay; }

                    relayJoinCode = captured;
                }

                if(string.IsNullOrEmpty(relayJoinCode)) {
                    Debug.LogError("Relay code not available after waiting.");
                    return "Relay code not available after waiting";
                }

                // Configure NGO transport with Relay allocation from code
                var join = await RelayService.Instance.JoinAllocationAsync(relayJoinCode);
                var rsd = AllocationUtils.ToRelayServerData(join, RelayProtocol.DTLS); // or UDP/WSS
                var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
                utp.SetRelayServerData(rsd);

                if(!NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsHost)
                    NetworkManager.Singleton.StartClient();

                if(!_loadingGameScene) {
                    _loadingGameScene = true;
                    NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += OnNetworkSceneLoadComplete;
                }
            } catch (Exception e) {
                Debug.LogError($"Failed to join session: {e.Message}");
                return $"Failed to join session: {e.Message}";
            }

            return "Lobby joined";
        }

        // ====== Scene callback ======

        private void OnNetworkSceneLoadComplete(string sceneName, LoadSceneMode mode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut) {
            if(!_loadingGameScene) return;

            _loadingGameScene = false;
            NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= OnNetworkSceneLoadComplete;

            // Host: kick manual spawning after game scene loads
            if(NetworkManager.Singleton.IsServer) {
                var spawner = FindFirstObjectByType<CustomNetworkManager>();
                StartCoroutine(InitializeAfterSceneLoad(spawner));
            }

            // Reveal in-game UI
            var gameMenu = GameMenuManager.Instance;
            if(gameMenu != null && gameMenu.TryGetComponent(out UIDocument doc)) {
                Debug.Log("Game Start: Revealing in-game UI");
                var root = doc.rootVisualElement;
                var rootContainer = root?.Q<VisualElement>("root-container");
                if(rootContainer != null) rootContainer.style.display = DisplayStyle.Flex;
            }
        }
    
        private IEnumerator InitializeAfterSceneLoad(CustomNetworkManager spawner) {
            yield return new WaitForEndOfFrame();
            yield return new WaitForFixedUpdate();
            if (spawner != null) spawner.EnableGameplaySpawningAndSpawnAll();
        }

        // ====== Relay helpers ======

        private async UniTask ConfigureRelayForHostAsync(int maxPlayers = 16) {
            var alloc = await RelayService.Instance.CreateAllocationAsync(maxPlayers);
            var rsd = AllocationUtils.ToRelayServerData(alloc, RelayProtocol.DTLS); // or UDP/WSS

            var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
            utp.SetRelayServerData(rsd);

            _relayJoinCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
        }

        private bool TryGetRelayCode(out string relayJoinCode) {
            relayJoinCode = null;
            if (ActiveSession == null) return false;

            if(ActiveSession.Properties.TryGetValue(RelayCodePropertyKey, out var prop) && !string.IsNullOrEmpty(prop.Value)) {
                relayJoinCode = prop.Value;
                return true;
            }
            return false;
        }

        private async UniTask WaitForRelayCodeAsync(CancellationToken ct) {
            // Event-first: if SessionPropertiesChanged fires we’ll get RelayCodeAvailable and cancel this.
            // Poll as a fallback so clients don't get stuck if the event arrives before we subscribed.
            const int maxTries = 40; // ~8s @ 200ms
            for(int i = 0; i < maxTries; i++) {
                ct.ThrowIfCancellationRequested();

                try {
                    await ActiveSession.RefreshAsync();
                } catch {
                    /* transient */
                }

                if(TryGetRelayCode(out var code)) {
                    RelayCodeAvailable?.Invoke(code);
                    return;
                }

                await UniTask.Delay(200, cancellationToken: ct);
            }
        }

        // ====== Session event wiring ======

        private void HookSessionEvents(ISession session) {
            if(session == null || _sessionEventsHooked) return;

            session.SessionPropertiesChanged += OnSessionPropertiesChanged;
            session.PlayerJoined += OnPlayerJoined;
            session.PlayerLeaving += OnPlayerLeft;
            session.Changed += OnSessionChanged;

            _sessionEventsHooked = true;
        }

        private void UnhookSessionEvents(ISession session) {
            if(session == null || !_sessionEventsHooked) return;

            session.SessionPropertiesChanged -= OnSessionPropertiesChanged;
            session.PlayerJoined -= OnPlayerJoined;
            session.PlayerLeaving -= OnPlayerLeft;
            session.Changed -= OnSessionChanged;

            _sessionEventsHooked = false;
        }

        private void OnSessionChanged() {
            PlayersChanged?.Invoke(ActiveSession?.Players);
        }

        private void OnPlayerJoined(string playerId) {
            PlayersChanged?.Invoke(ActiveSession?.Players);
        }

        private void OnPlayerLeft(string playerId) {
            PlayersChanged?.Invoke(ActiveSession?.Players);
        }

        private void OnSessionPropertiesChanged() {
            if(ActiveSession == null) return;

            if(ActiveSession.Properties.TryGetValue(RelayCodePropertyKey, out var prop) && !string.IsNullOrEmpty(prop.Value)) {
                RelayCodeAvailable?.Invoke(prop.Value);
            }
        }
    }
}