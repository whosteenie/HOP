using UnityEngine;
using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine.SceneManagement;
using UnityUtils;

public class SessionManager : Singleton<SessionManager>
{
    ISession activeSession;
    private new const string GameSceneName = "Game";
    private bool _isInitialized;
    private bool _loadingGameScene;

    ISession ActiveSession {
        get => activeSession;
        set {
            activeSession = value;
            Debug.Log($"Active session set: {activeSession}");
        }
    }
    
    const string playerNamePropertyKey = "playerName";
    
    async void Start()
    {
        DontDestroyOnLoad(gameObject);

        await InitializeUnityServices();
    }
    
    private async UniTask InitializeUnityServices()
    {
        if(_isInitialized) return;
        
        try {
            await UnityServices.InitializeAsync();
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
            _isInitialized = true;
            Debug.Log($"Unity Services initialized. Player ID: {AuthenticationService.Instance.PlayerId}");
        } catch(Exception e) {
            Debug.LogException(e);
        }
    }

    // void RegisterSessionEvents() {
    //     ActiveSession.Changed += OnSessionChanged();
    //     
    // }
    //
    // void UnregisterSessionEvents() {
    //     ActiveSession.Changed -= OnSessionChanged();
    // }

    async UniTask<Dictionary<string, PlayerProperty>> GetPlayerProperties() {
        // Custom game-specific properties that apply to an individual player, ie: name, role, skill level, etc.
        var playerName = await AuthenticationService.Instance.GetPlayerNameAsync();
        var playerNameProperty = new PlayerProperty(playerName, VisibilityPropertyOptions.Member);
        return new Dictionary<string, PlayerProperty>() { { playerNamePropertyKey, playerNameProperty } };
    }
    
    public async void StartSessionAsHost() {
        if(!_isInitialized) {
            await InitializeUnityServices();
        }
        
        var playerProperties = await GetPlayerProperties();
        
        var options = new SessionOptions {
            MaxPlayers = 16,
            IsLocked = false,
            IsPrivate = false,
            PlayerProperties = playerProperties
        }.WithRelayNetwork(); // or WithDistributedAuthorityNetwork() to use Distributed Authority instead of Relay
        
        ActiveSession = await MultiplayerService.Instance.CreateSessionAsync(options);
        Debug.Log($"Session created: {ActiveSession.Id} Join code: {ActiveSession.Code}");
        
        await UniTask.Delay(100);
        
        _loadingGameScene = true;
        NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += OnNetworkSceneLoadComplete;
        
        NetworkManager.Singleton.SceneManager.LoadScene(GameSceneName, LoadSceneMode.Single);
    }

    private void OnNetworkSceneLoadComplete(string sceneName, LoadSceneMode loadSceneMode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut) {
        if(!_loadingGameScene) return;

        _loadingGameScene = false;
        NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= OnNetworkSceneLoadComplete;
    }

    async UniTaskVoid JoinSessionById(string sessionId) {
        ActiveSession = await MultiplayerService.Instance.JoinSessionByIdAsync(sessionId);
        Debug.Log($"Joined session by ID: {ActiveSession.Id}");
    }
    
    async UniTaskVoid JoinSessionByCode(string joinCode) {
        ActiveSession = await MultiplayerService.Instance.JoinSessionByCodeAsync(joinCode);
        Debug.Log($"Joined session by code: {ActiveSession.Id}");
    }

    async UniTaskVoid KickPlayer(string playerId) {
        if(!ActiveSession.IsHost) return;
        
        await ActiveSession.AsHost().RemovePlayerAsync(playerId);
    }

    async UniTask<IList<ISessionInfo>> QuerySessions() {
        var sessionQueryOptions = new QuerySessionsOptions();
        var results = await MultiplayerService.Instance.QuerySessionsAsync(sessionQueryOptions);
        return results.Sessions;
    }
    
    async UniTaskVoid LeaveSession() {
        if(ActiveSession != null) {
            // UnregisterSessionEvents();
            try {
                await ActiveSession.LeaveAsync();
            } catch {
                // Ignore
            } finally {
                ActiveSession = null;
            }
        }
    }
}
