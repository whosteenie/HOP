using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class PersistentNetworkManager : MonoBehaviour
{
    private void Awake() {
        // If a NetworkManager already exists (from previous session)
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.gameObject != gameObject)
        {
            // Destroy THIS one (the duplicate from MainMenu)
            Debug.Log("NetworkManager already exists - destroying duplicate");
            Destroy(gameObject);
            return;
        }
        
        // This is the first/only NetworkManager - make it persist
        DontDestroyOnLoad(gameObject);
        Debug.Log("NetworkManager marked as DontDestroyOnLoad");
    }
}
