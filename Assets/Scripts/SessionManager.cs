using UnityEngine;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UnityUtils;
using Exception = System.Exception;

public class SessionManager : Singleton<SessionManager> {
    private ISession _activeSession;
    private const string GameSceneName = "Game";
    private bool _isInitialized;
    private bool _loadingGameScene;
    private string _relayJoinCode;

    [SerializeField] private MainMenuManager mainMenuManager; // optional

    private ISession ActiveSession {
        get => _activeSession;
        set {
            _activeSession = value;
            Debug.Log($"Active session set: {_activeSession}");
        }
    }

    private const string PlayerNamePropertyKey = "playerName";

    private async void Start() {
        try {
            DontDestroyOnLoad(gameObject);
            await InitializeUnityServices();
        } catch(Exception e) {
            Debug.LogException(e);
        }
    }

    private async UniTask InitializeUnityServices() {
        if(_isInitialized) return;
        try {
            await UnityServices.InitializeAsync();
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
            _isInitialized = true;
            Debug.Log($"Unity Services initialized. Player ID: {AuthenticationService.Instance.PlayerId}");
        } catch(Exception e) { Debug.LogException(e); }
    }

    private async UniTask<Dictionary<string, PlayerProperty>> GetPlayerProperties() {
        var playerName = await AuthenticationService.Instance.GetPlayerNameAsync();
        var playerNameProperty = new PlayerProperty(playerName, VisibilityPropertyOptions.Member);
        return new Dictionary<string, PlayerProperty> { { PlayerNamePropertyKey, playerNameProperty } };
    }

    /// Host clicks "Host Game": create UGS Session only (no StartHost, no LoadScene)
    public async UniTask<string> StartSessionAsHost() {
        try {
            if(!_isInitialized) await InitializeUnityServices();

            var playerProperties = await GetPlayerProperties();

            var options = new SessionOptions {
                MaxPlayers = 16,
                IsLocked = false,
                IsPrivate = false,
                PlayerProperties = playerProperties
            }.WithRelayNetwork();

            ActiveSession = await MultiplayerService.Instance.CreateSessionAsync(options);
            Debug.LogWarning($"Session created: {ActiveSession.Id} Join code: {ActiveSession.Code}");
            return ActiveSession.Code;
        } catch(Exception e) {
            Debug.LogException(e);
            return null;
        }
    }

    /// Host clicks "Start Game": now start host and load the networked scene
    public async UniTask BeginGameplayAsHostAsync() {
        // 1) Allocate Relay & configure transport (NEW API)
        await ConfigureRelayForHostAsync();

        // 2) Optionally publish the relay code into your Session so clients auto-fetch it
        if (ActiveSession?.IsHost == true)
        {
            var hostSession = ActiveSession.AsHost(); // IHostSession
            hostSession.SetProperty(
                "relayCode",
                new SessionProperty(_relayJoinCode, VisibilityPropertyOptions.Member) // or Public
            );
            await hostSession.SavePropertiesAsync();
        }

        if(!NetworkManager.Singleton.IsListening)
            NetworkManager.Singleton.StartHost(); // approval is already set in CustomNetworkManager.Awake
        
        await UniTask.Yield();

        _loadingGameScene = true;
        NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += OnNetworkSceneLoadComplete;
        NetworkManager.Singleton.SceneManager.LoadScene(GameSceneName, LoadSceneMode.Single);
    }

    /// Client enters join code; join UGS and start NGO client. Do NOT load scenes here.
    public async UniTask JoinSessionByCodeAsync(string joinCode) {
        try {
            if(!_isInitialized) await InitializeUnityServices();

            ActiveSession = await MultiplayerService.Instance.JoinSessionByCodeAsync(joinCode);
            Debug.Log($"Joined session by code: {ActiveSession?.Id}");
            
            // 2) Read relay code that host published
            if (ActiveSession == null ||
                !ActiveSession.Properties.TryGetValue("relayCode", out var relayProp) ||
                string.IsNullOrEmpty(relayProp.Value))
            {
                Debug.LogError("Relay code not available on session yet.");
                return;
            }
            var relayJoinCode = relayProp.Value;
            
            // 3) Configure NGO transport with Relay
            var join = await RelayService.Instance.JoinAllocationAsync(relayJoinCode);
            var rsd = AllocationUtils.ToRelayServerData(join, RelayProtocol.DTLS); // or UDP/WSS
            var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
            utp.SetRelayServerData(rsd);

            // (If you use Relay, configure UnityTransport here before StartClient)

            if(!NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsHost)
                NetworkManager.Singleton.StartClient();

            if(!_loadingGameScene) {
                _loadingGameScene = true;
                NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += OnNetworkSceneLoadComplete;
            }
        } catch(Exception e) {
            Debug.LogError($"Failed to join session: {e.Message}");
        }
    }

    private void OnNetworkSceneLoadComplete(string sceneName, LoadSceneMode mode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut) {
        if(!_loadingGameScene) return;

        _loadingGameScene = false;
        NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= OnNetworkSceneLoadComplete;

        // Host: after the "Game" scene loads, spawn everyone manually
        if(NetworkManager.Singleton.IsServer) {
            var spawner = GameObject.FindFirstObjectByType<CustomNetworkManager>();
            if (spawner != null)
                spawner.EnableGameplaySpawningAndSpawnAll();
            else
                Debug.LogWarning("CustomNetworkManager not found; players won’t be spawned.");
        }

        // Optional: show in-game UI
        var pauseMgr = PauseMenuManager.Instance;
        if(pauseMgr != null) {
            var uiDoc = pauseMgr.GetComponent<UIDocument>();
            var root = uiDoc != null ? uiDoc.rootVisualElement : null;
            var rootContainer = root?.Q<VisualElement>("root-container");
            if (rootContainer != null)
                rootContainer.style.visibility = Visibility.Visible;
        }
    }

    private async UniTask ConfigureRelayForHostAsync(int maxPlayers = 16) {
        // Create a Relay allocation; region optional (auto by QoS)
        var alloc = await RelayService.Instance.CreateAllocationAsync(maxPlayers);

        // Produce RelayServerData using the new helper
        var rsd = AllocationUtils.ToRelayServerData(alloc, RelayProtocol.DTLS); // or UDP/WSS

        // Apply to NGO transport
        var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
        utp.SetRelayServerData(rsd);

        // Share this with clients (UI or via Session properties)
        _relayJoinCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
    }

    private async UniTask ConfigureRelayForClientAsync(string relayJoinCode) {
        var join = await RelayService.Instance.JoinAllocationAsync(relayJoinCode);
        var rsd = AllocationUtils.ToRelayServerData(join, RelayProtocol.DTLS); // or UDP/WSS
        var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
        utp.SetRelayServerData(rsd);
    }
}
