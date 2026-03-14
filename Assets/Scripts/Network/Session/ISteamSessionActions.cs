using Cysharp.Threading.Tasks;
using Steamworks;
using Lobby = Steamworks.Data.Lobby;

namespace Network.Session {
    /// <summary>
    /// Steam join/follow actions used by SteamSocialBridge. Implemented by SessionManager
    /// until party/matchmaker flows are fully extracted.
    /// </summary>
    public interface ISteamSessionActions {
        UniTask<bool> JoinSteamSocialLobbyAsync(Lobby lobby);
        UniTask FollowSessionContextFromSteamLobbyAsync(Lobby lobby);
        UniTask HandleSteamConnectStringAsync(string connect);
        void TryJoinVoiceForSteamSocialLobby(ulong lobbyId, string context);
    }
}
