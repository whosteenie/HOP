using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Services.Lobbies.Models;
using Unity.Services.Matchmaker.Models;

namespace Network.Contracts {
    /// <summary>
    /// Matchmaker follow-up actions: join or host a match. Implemented by SessionManager.
    /// Host flow steps are used by SessionMatchmakerService to run StartPublicMatchAsHostAsync.
    /// </summary>
    public interface IMatchmakerSessionActions {
        UniTask<bool> JoinMatchLobbyByIdAsync(string lobbyId);
        /// <summary>Runs the full public match host flow (orchestrated by SessionMatchmakerService).</summary>
        UniTask StartPublicMatchAsHostAsync(string mode, int maxPlayers, string matchId, StoredMatchmakingResults results);
        UniTask JoinPublicMatchByIdAsync(string matchId);
        UniTask<Lobby> QueryMatchLobbyByMatchIdAsync(string matchId);
        UniTask<bool> WaitForPlayersReadyAsync(List<string> expectedPlayerIds, float timeoutSeconds, string contextLabel);

        UniTask<string> CreateDaSessionAsync(int maxPlayers, bool isPrivateMatch, string contextLabel);
        UniTask CreatePublicMatchLobbyAsync(string mode, int maxPlayers, string matchId, string joinCode);
        UniTask PreFadePublicHostAsync();
        UniTask MarkHostReadyAsync();
        UniTask<bool> TrySetMatchLobbyStateAsync(string lobbyState, DataObject.VisibilityOptions visibility, string context);
        bool TryLoadGameplaySceneAsHost(string contextLabel);
    }
}
