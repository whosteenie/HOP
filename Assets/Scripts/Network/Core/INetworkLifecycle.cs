using Cysharp.Threading.Tasks;
using Unity.Netcode;

namespace Network.Core {
    /// <summary>
    /// Abstraction over NGO lifecycle operations:
    /// - Coordinated Shutdown (host/client/server)
    /// - Clearing scene-placed object caches (spawn manager internal dict)
    /// - Hook/unhook to NGO SceneManager completion event
    /// Keeps SessionManager focused on orchestration only.
    /// </summary>
    public interface INetworkLifecycle {
        /// <summary>Shuts down NGO if listening; yields until fully down.</summary>
        UniTask ShutdownIfListeningAsync();
        /// <summary>Full cleanup: shutdown NGO, clear cached scene-placed objects, optionally reset transport to defaults.</summary>
        UniTask CleanupNetworkAsync(); // shutdown + clear caches

        /// <summary>Subscribes to NGO scene load completion with the exact delegate signature required by NGO.</summary>
        void HookSceneCallbacks(NetworkSceneManager.OnEventCompletedDelegateHandler onComplete);
        /// <summary>Unsubscribes from NGO scene load completion.</summary>
        void UnhookSceneCallbacks(NetworkSceneManager.OnEventCompletedDelegateHandler onComplete);
        
        /// <summary>Clears the internal SpawnManager m_ScenePlacedObjects dictionary via reflection (prevents stale objects across sessions).</summary>
        void ClearScenePlacedObjectsCache();
    }
}