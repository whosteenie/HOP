using UnityEngine;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UnityUtils;
using Exception = System.Exception;

public class SessionManager : Singleton<SessionManager> {
    private ISession _activeSession;
    private const string GameSceneName = "Game";
    private bool _isInitialized;
    private bool _loadingGameScene;

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
        // (If you use Relay, configure UnityTransport here before StartHost)

        if(!NetworkManager.Singleton.IsListening)
            NetworkManager.Singleton.StartHost(); // approval is already set in CustomNetworkManager.Awake

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

            // Optionally reflect in lobby UI
            if(ActiveSession != null && mainMenuManager != null) {
                var props = await GetPlayerProperties();
                if (props.TryGetValue(PlayerNamePropertyKey, out var val))
                    mainMenuManager.AddPlayer(val.Value, false);
            }

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
}
