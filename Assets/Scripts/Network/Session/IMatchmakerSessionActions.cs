using Cysharp.Threading.Tasks;
using Unity.Services.Matchmaker.Models;

namespace Network.Session {
    /// <summary>
    /// Matchmaker follow-up actions: join or host a match. Implemented by SessionManager.
    /// </summary>
    public interface IMatchmakerSessionActions {
        UniTask<bool> JoinMatchLobbyByIdAsync(string lobbyId);
        UniTask StartPublicMatchAsHostAsync(string mode, int maxPlayers, string matchId, StoredMatchmakingResults results);
        UniTask JoinPublicMatchByIdAsync(string matchId);
    }
}
