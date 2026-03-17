using Cysharp.Threading.Tasks;
using System.Collections.Generic;

namespace Network.SessionContracts {
    /// <summary>
    /// Party lifecycle actions: event subscription, Steam social lobby creation, heartbeat schedule.
    /// Implemented by SessionManager.
    /// </summary>
    public interface IPartySessionActions {
        UniTask UnsubscribeMatchLobbyAsync(string context);
        UniTask UnsubscribePartyLobbyAsync(string context);
        UniTask EnsurePartyLobbySubscriptionAsync(string context);
        UniTask<bool> WaitForPlayersReadyAsync(List<string> expectedPlayerIds, float timeoutSeconds, string contextLabel);
        UniTask<bool> CreateSteamSocialLobbyAsync(int maxPlayers);
        void ResetMatchLobbyFollowState();
        void SetNextUgsHeartbeatTime(float value);
        void UpdateSteamLobbyWithPartyDataIfOwner();
        void TryJoinVoiceForActiveMatch(string context);
    }
}
