using Cysharp.Threading.Tasks;

namespace Network.Session.Interface {
    /// <summary>
    /// Steps invoked by SessionSceneFlowService during the leave-to-menu flow.
    /// Implemented by SessionManager.
    /// </summary>
    public interface ILeaveToMenuActions {
        UniTask ClearMatchmakingStateAsync();
        UniTask TryLeaveVoiceChannelAsync();
        UniTask ResetPartyFollowStateIfHostAsync();
        void LeaveLobby();
        UniTask ClearMatchStateAsync();
        UniTask CleanupNetworkAsync();
        UniTask EnsureMainMenuLoadedAndReadyAsync(string currentScene);
        string GetActiveSceneName();
    }
}
