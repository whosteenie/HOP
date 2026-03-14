using Cysharp.Threading.Tasks;

namespace Network.Session {
    /// <summary>
    /// Actions invoked by SessionMatchLobbyService for backfill refresh (evaluate + update).
    /// Scheduling and heartbeats are inside SessionMatchLobbyService. Implemented by SessionManager.
    /// </summary>
    public interface IMatchLobbyTickActions {
        bool IsBackfillEligibilityUpdateInFlight();
        (bool allowed, string reason) EvaluatePublicMatchBackfillEligibility();
        UniTask<bool> TryUpdatePublicMatchBackfillEligibilityAsync(bool allowed, string reason, string context);
    }
}
