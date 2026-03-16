using UnityEngine;

namespace Game.Menu {
    /// <summary>
    /// Simple helper that marks its GameObject as DontDestroyOnLoad.
    /// Attach this to any object that should persist across scene loads.
    /// </summary>
    public sealed class DontDestroyOnLoadBehaviour : MonoBehaviour {
        private void Awake() {
            DontDestroyOnLoad(gameObject);
        }
    }
}

