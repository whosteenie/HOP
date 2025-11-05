using UnityEngine;
using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;

public class SessionManager : MonoBehaviour
{
    ISession activeSession;

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
        try {
            await UnityServices.InitializeAsync();
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
            Debug.Log($"Sign in anonymous successful. Player ID: {AuthenticationService.Instance.PlayerId}");
        } catch(Exception e) {
            Debug.LogException(e);
        }
    }

    async UniTask<Dictionary<string, PlayerProperty>> GetPlayerProperties() {
        // Custom game-specific properties that apply to an individual player, ie: name, role, skill level, etc.
        var playerName = await AuthenticationService.Instance.GetPlayerNameAsync();
        var playerNameProperty = new PlayerProperty(playerName, VisibilityPropertyOptions.Member);
        return new Dictionary<string, PlayerProperty>() { { playerNamePropertyKey, playerNameProperty } };
    }
    
    async void StartSessionAsHost() {
        var options = new SessionOptions {
            MaxPlayers = 16,
            IsLocked = false,
            IsPrivate = false,
        }.WithRelayNetwork(); // or WithDistributedAuthorityNetwork() to use Distributed Authority instead of Relay
    }
}
