using Cysharp.Threading.Tasks;
using Network.Singletons;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Bridge for sending RPCs from SessionManager (which persists across scenes)
/// </summary>
public class SessionNetworkBridge : NetworkBehaviour {
    public static SessionNetworkBridge Instance { get; private set; }

    private void Awake() {
        if(Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }
    
    public override void OnNetworkSpawn() {
        base.OnNetworkSpawn();
            
        // Ensure instance is set when spawned
        if(Instance == null) {
            Instance = this;
        }
            
        Debug.Log("[SessionNetworkBridge] Network spawned");
    }

    [Rpc(SendTo.Everyone)]
    public void FadeOutAllClientsClientRpc() {
        Debug.Log("[SessionNetworkBridge] FadeOut RPC received");
        if(SceneTransitionManager.Instance != null) {
            _ = SceneTransitionManager.Instance.FadeOut().ToUniTask();
        } else {
            Debug.LogError("[SessionNetworkBridge] SceneTransitionManager.Instance is null!");
        }
    }

    [Rpc(SendTo.Everyone)]
    public void FadeInAllClientsClientRpc() {
        Debug.Log("[SessionNetworkBridge] FadeIn RPC received");
        if(SceneTransitionManager.Instance != null) {
            _ = SceneTransitionManager.Instance.FadeIn().ToUniTask();
        } else {
            Debug.LogError("[SessionNetworkBridge] SceneTransitionManager.Instance is null!");
        }
    }
}