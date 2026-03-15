using Cysharp.Threading.Tasks;

namespace Network.Session {
    /// <summary>
    /// Party lifecycle actions: event subscription, Steam social lobby creation, heartbeat schedule.
    /// Implemented by SessionManager.
    /// </summary>
    public interface IPartySessionActions {
        UniTask UnsubscribeMatchLobbyAsync(string context);
        UniTask UnsubscribePartyLobbyAsync(string context);
        UniTask EnsurePartyLobbySubscriptionAsync(string context);
        UniTask<bool> CreateSteamSocialLobbyAsync(int maxPlayers);
        void SetNextUgsHeartbeatTime(float value);
        void UpdateSteamLobbyWithPartyDataIfOwner();
        void TryJoinVoiceForActiveMatch(string context);
    }
}
