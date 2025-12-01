using System.Collections;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

namespace Network.Core {
    /// <summary>
    /// Concrete NGO lifecycle helper. Centralizes shutdown/wait loops and scene callback wiring.
    /// NOTE: Uses reflection to clear SpawnManager's internal scene-placed objects cache.
    /// </summary>
    public sealed class NetworkLifecycle : INetworkLifecycle {
        /// <inheritdoc />
        public async UniTask ShutdownIfListeningAsync() {
            var networkManager = NetworkManager.Singleton;
            if(networkManager != null && networkManager.IsListening) {
                networkManager.Shutdown();
                for(var i = 0;
                    i < 100 && networkManager != null && (networkManager.ShutdownInProgress || networkManager.IsListening);
                    i++)
                    await UniTask.Yield();
            }
        }

        /// <inheritdoc />
        public async UniTask CleanupNetworkAsync() {
            if(NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening) return;
            ClearScenePlacedObjectsCache();
            NetworkManager.Singleton.Shutdown();
            for(var i = 0; i < 100 && NetworkManager.Singleton.ShutdownInProgress; i++)
                await UniTask.Yield();

            // Keep your legacy default reset (as in your code)
            var utp = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if(utp != null) {
                utp.SetConnectionData("127.0.0.1", 7777);
            }
        }

        /// <inheritdoc />
        public void HookSceneCallbacks(NetworkSceneManager.OnEventCompletedDelegateHandler onComplete) {
            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) return;
            var sm = networkManager.SceneManager;
            if(sm == null) return;
            sm.OnLoadEventCompleted -= onComplete;
            sm.OnLoadEventCompleted += onComplete;
        }

        /// <inheritdoc />
        public void UnhookSceneCallbacks(NetworkSceneManager.OnEventCompletedDelegateHandler onComplete) {
            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) return;
            var sm = networkManager.SceneManager;
            if(sm == null) return;
            sm.OnLoadEventCompleted -= onComplete;
        }

        /// <inheritdoc />
        public void ClearScenePlacedObjectsCache() {
            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) return;
            var mgr = networkManager.SpawnManager;
            if(mgr == null) return;

            var field = mgr.GetType().GetField("m_ScenePlacedObjects",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if(field == null) return;
            var dictValue = field.GetValue(mgr);
            if(dictValue is IDictionary dict) {
                dict.Clear();
            }
        }
    }
}