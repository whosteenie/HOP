using System.Collections;
using Network.Singletons;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network.Rpc {
    public class SessionExitRelay : NetworkBehaviour {
        #region Debug Logging

        private static void D(string msg) => Debug.Log($"[EXIT RELAY] {msg}");
        #endregion
        [Rpc(SendTo.Owner)]
        public void ReturnToMenuClientRpc() {
            // Client & host both execute this locally
            HUDManager.Instance.HideHUD();
            if(GameMenuManager.Instance.IsPaused) {
                GameMenuManager.Instance.TogglePause();
            }
            
            SceneManager.LoadScene("MainMenu", LoadSceneMode.Single);
        }

        [Rpc(SendTo.ClientsAndHost)]
        public void ReturnToMenuHostRpc() {
            HUDManager.Instance.HideHUD();
            if(GameMenuManager.Instance.IsPaused) {
                GameMenuManager.Instance.TogglePause();
            }
            
            SceneManager.LoadScene("MainMenu", LoadSceneMode.Single);
        }
    }
}