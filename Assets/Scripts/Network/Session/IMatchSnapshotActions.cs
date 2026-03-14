using Cysharp.Threading.Tasks;
using Unity.Services.Lobbies.Models;

namespace Network.Session {
    /// <summary>
    /// Actions used by SessionMatchLobbyService for snapshot/follow/sync/client-start flow.
    /// Implemented by SessionManager.
    /// </summary>
    public interface IMatchSnapshotActions {
        void SyncModeFromMatchLobby(Lobby lobby);
        UniTask StartMatchSynchronizationAsync(bool skipFadeOut);
        UniTask StartMatchClientAsync(bool useFadeOut = false, string expectedSessionCode = null, bool? expectedIsPrivateMatch = null);
        UniTask FadeOutWithFallbackAsync(int fallbackDelayMs = 500);
        UniTask LeaveToMainMenuAsync(bool skipFadeOut = false);
        UniTask<SessionNetworkLifecycle.DistributedAuthoritySessionJoinResult> JoinDistributedAuthoritySessionAsync(string sessionCode, bool isPrivateMatch, string contextLabel);
        UniTask<bool> JoinMatchLobbyByIdAsync(string lobbyId);

        bool UgsLocalReadySubmitted { get; set; }
        bool UgsSyncInProgress { get; set; }
        bool UgsClientStartedForMatch { get; set; }
        bool UgsHostPreFadedOut { get; set; }
    }
}
