using Unity.Netcode;
using UnityEngine.SceneManagement;

namespace Network.Core {
    /// <summary>Default scene coordinator: one-liner wrapper around NetworkManager.SceneManager.LoadScene.</summary>
    public sealed class SceneCoordinator : ISceneCoordinator {
        /// <inheritdoc />
        public void LoadGameSceneServer(string gameScene) {
            var sm = NetworkManager.Singleton.SceneManager;
            sm.LoadScene(gameScene, LoadSceneMode.Single);
        }
    }
}