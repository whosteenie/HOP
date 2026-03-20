using Cysharp.Threading.Tasks;
using Unity.Services.Lobbies.Models;

namespace Network.Contracts {
    public struct StartMatchClientRequest {
        public bool UseFadeOut { get; set; }
        public string ExpectedSessionCode { get; set; }
        public bool? ExpectedIsPrivateMatch { get; set; }
    }

    /// <summary>
    /// Actions used by SessionMatchLobbyService for snapshot/follow/sync/client-start flow.
    /// Implemented by SessionManager.
    /// </summary>
    public interface IMatchSnapshotActions {
        void SyncModeFromMatchLobby(Lobby lobby);
        UniTask StartMatchSyncAsync(bool skipFadeOut);
        UniTask StartMatchClientAsync(in StartMatchClientRequest request);
        UniTask FadeOutWithFallbackAsync(int fallbackDelayMs = 500);
        UniTask LeaveToMainMenuAsync(bool skipFadeOut = false);
        UniTask<DaSessionJoinResult> JoinDaSessionAsync(string sessionCode, bool isPrivateMatch, string contextLabel);
        UniTask<bool> JoinMatchLobbyByIdAsync(string lobbyId);

        bool UgsLocalReadySubmitted { get; set; }
        bool UgsSyncInProgress { get; set; }
        bool UgsClientStartedForMatch { get; set; }
        bool UgsHostPreFadedOut { get; set; }
    }
}
