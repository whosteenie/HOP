using Cysharp.Threading.Tasks;

namespace Network.Session {
    /// <summary>
    /// Actions used during network cleanup and DA create/join (leave session, full cleanup).
    /// Implemented by SessionManager.
    /// </summary>
    public interface INetworkLifecycleActions {
        UniTask LeaveActiveSessionAsync(string contextLabel);
        /// <summary>Leaves active DA session and shuts down NetworkManager. Used at start of DA create/join.</summary>
        UniTask CleanupNetworkAsync();
    }
}
