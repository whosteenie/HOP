using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Social;
using Unity.Services.Authentication;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace Network.Core {
    /// <summary>
    /// Default player identity implementation using Unity Authentication.
    /// Handles empty/exception cases by falling back to a PlayerId suffix.
    /// </summary>
    public sealed class PlayerIdentity : IPlayerIdentity {
        /// <inheritdoc />
        public async UniTask<Dictionary<string, PlayerProperty>> GetPlayerPropertiesAsync(string key) {
            var localName = StreamerMode.GetLocalDisplayName();
            if(!string.IsNullOrWhiteSpace(localName)) {
                return new Dictionary<string, PlayerProperty>
                    { { key, new PlayerProperty(localName, VisibilityPropertyOptions.Member) } };
            }

            var playerName = "Player(?)";
            try {
                playerName = await AuthenticationService.Instance.GetPlayerNameAsync();
                if(string.IsNullOrWhiteSpace(playerName))
                    playerName = AuthenticationService.Instance.PlayerName;
            } catch {
                var pid = AuthenticationService.Instance.PlayerId;
                if(pid == null) return new Dictionary<string, PlayerProperty>
                    { { key, new PlayerProperty(playerName, VisibilityPropertyOptions.Member) } };

                var suffix = pid.Length >= 4 ? pid[^4..] : pid;
                playerName = $"Player({suffix})";
            }

            return new Dictionary<string, PlayerProperty>
                { { key, new PlayerProperty(playerName, VisibilityPropertyOptions.Member) } };
        }
    }
}