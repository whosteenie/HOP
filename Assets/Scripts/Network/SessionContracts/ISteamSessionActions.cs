using Cysharp.Threading.Tasks;
using Lobby = Steamworks.Data.Lobby;

namespace Network.SessionContracts {
    /// <summary>
    /// Steam join/follow actions used by SteamSocialBridge. Implemented by SessionManager
    /// until party/matchmaker flows are fully extracted.
    /// </summary>
    public interface ISteamSessionActions {
        UniTask<bool> JoinSteamSocialLobbyAsync(Lobby lobby);
        UniTask FollowSessionContextFromSteamLobbyAsync(Lobby lobby);
        UniTask HandleSteamConnectStringAsync(string connect);
        void TryJoinVoiceForSteamSocialLobby(ulong lobbyId, string context);
        /// <summary>Used by SteamSocialBridge for connect string and follow. Implemented by SessionManager.</summary>
        UniTask JoinPartyLobbyByCodeAsync(string code);
        /// <summary>Used by SteamSocialBridge for connect string and follow. Implemented by SessionManager.</summary>
        UniTask<bool> JoinMatchLobbyByIdAsync(string lobbyId);
    }
}
