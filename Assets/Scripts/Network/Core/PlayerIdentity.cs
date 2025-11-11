using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Multiplayer;

namespace Network.Core {
    /// <summary>
    /// Default player identity implementation using Unity Authentication.
    /// Handles empty/exception cases by falling back to a PlayerId suffix.
    /// </summary>
    public sealed class PlayerIdentity : IPlayerIdentity {
        /// <inheritdoc />
        public async UniTask<Dictionary<string, PlayerProperty>> GetPlayerPropertiesAsync(string key) {
            var playerName = "Player(?)";
            try {
                playerName = await AuthenticationService.Instance.GetPlayerNameAsync();
                if (string.IsNullOrWhiteSpace(playerName))
                    playerName = AuthenticationService.Instance.PlayerName;
            } catch {
                var pid = AuthenticationService.Instance.PlayerId;
                if(pid != null) {
                    var suffix = (pid?.Length ?? 0) >= 4 ? pid[^4..] : pid;
                    playerName = $"Player({suffix})";
                }
            }
            return new Dictionary<string, PlayerProperty> { { key, new PlayerProperty(playerName, VisibilityPropertyOptions.Member) } };
        }
    }
}