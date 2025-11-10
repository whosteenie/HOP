using Cysharp.Threading.Tasks;

namespace Network.Core {
    /// <summary>
    /// Very small seam around NGO scene loading so SessionManager doesn’t reference SceneManager directly.
    /// Useful if you decide to add pre/post load steps later.
    /// </summary>
    public interface ISceneCoordinator {
        /// <summary>Server-only: loads the gameplay scene using NGO’s scene system.</summary>
        void LoadGameSceneServer(string gameSceneName);
    }
}