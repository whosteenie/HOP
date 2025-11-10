using System.Collections;
using Cysharp.Threading.Tasks;
using Network;
using Singletons;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Relays {
    public class SessionExitRelay : NetworkBehaviour {
        #region Debug Logging

        private static void D(string msg) => Debug.Log($"[EXIT RELAY] {msg}");
        #endregion
        [Rpc(SendTo.ClientsAndHost)]
        public void ReturnToMenuClientRpc() {
            // Client & host both execute this locally
            StartCoroutine(ReturnToMenuCo());
        }

        private IEnumerator ReturnToMenuCo() {
            // tiny delay so UI has a frame to detach from networked state
            yield return null;
            // This is a purely local transition on each process
            HUDManager.Instance.HideHUD();
            if(GameMenuManager.Instance.IsPaused) {
                GameMenuManager.Instance.TogglePause();
            }
            
            SceneManager.LoadScene("MainMenu", LoadSceneMode.Single);
        }
    }
}