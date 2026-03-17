using Cysharp.Threading.Tasks;

namespace Network.Contracts {
    /// <summary>
    /// Actions used during unexpected disconnect / scene flow (fade, capture FP, leave to menu).
    /// Implemented by SessionManager.
    /// </summary>
    public interface ISceneFlowActions {
        string GetActiveSceneName();
        bool IsGameplaySceneName(string sceneName);
        void SetFrontStatus(SessionPhase phase, string message);
        UniTask FadeOutWithFallbackAsync(int fallbackDelayMs = 500);
        UniTask LeaveToMainMenuAsync(bool skipFadeOut = false);
        void CaptureDuplicateFpVisualsForDisconnect();
    }
}
