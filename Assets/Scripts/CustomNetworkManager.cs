using Unity.Netcode;
using UnityEngine;

public class CustomNetworkManager : MonoBehaviour {
    [Header("Manual Player Prefab (do NOT rely on NetworkConfig.PlayerPrefab)")]
    [SerializeField] private NetworkObject playerPrefab;
    [SerializeField] private NetworkObject[] playerPrefabs;

    // When true (after Start Game), new joiners will be spawned automatically on connect.
    private bool _allowPlayerSpawns;

    private void Awake() {
        var nm = NetworkManager.Singleton;
        if(!nm) return;

        // 1) Enable approval BEFORE networking starts.
        nm.NetworkConfig.ConnectionApproval = true;

        // 2) Ensure the built-in auto-spawn path is disabled by leaving PlayerPrefab null.
        //    Use our own serialized playerPrefab for manual spawning.
        nm.NetworkConfig.PlayerPrefab = null;

        // 3) Register approval callback now so the HOST local connection is governed by it.
        nm.ConnectionApprovalCallback = ApprovalCheck;
    }

    private void OnEnable() {
        var nm = NetworkManager.Singleton;
        if(!nm) return;
        nm.OnClientConnectedCallback += OnClientConnected;
    }

    private void OnDisable() {
        var nm = NetworkManager.Singleton;
        if(!nm) return;
        nm.OnClientConnectedCallback -= OnClientConnected;

        // leave approval callback in place; safe across scenes if this persists
        if(nm.ConnectionApprovalCallback == ApprovalCheck)
            nm.ConnectionApprovalCallback = null;
    }

    private void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response) {
        // Approve every connection but NEVER let NGO auto-create the player.
        response.Approved = true;
        response.CreatePlayerObject = false;
        // We’ll spawn manually after the network game scene is loaded
    }

    private void OnClientConnected(ulong clientId) {
        // Late joiners: if game already started, spawn them immediately.
        if(_allowPlayerSpawns && NetworkManager.Singleton.IsServer) {
            SpawnPlayerFor(clientId);
        }
    }

    /// <summary>
    /// Called by SessionManager when the network "Game" scene is loaded on the host.
    /// Spawns every currently connected client and enables auto-spawn for late joiners.
    /// </summary>
    public void EnableGameplaySpawningAndSpawnAll() {
        _allowPlayerSpawns = true;

        if(!NetworkManager.Singleton.IsServer)
            return;

        foreach(var id in NetworkManager.Singleton.ConnectedClientsIds)
            SpawnPlayerFor(id);
    }

    private void SpawnPlayerFor(ulong clientId) {
        if(playerPrefab == null) {
            Debug.LogError("[CustomNetworkManager] Player Prefab not assigned.");
            return;
        }

        var pos = SpawnManager.Instance != null
            ? SpawnManager.Instance.GetNextSpawnPosition()
            : new Vector3(-60, 760, -20);

        var rot = SpawnManager.Instance != null
            ? SpawnManager.Instance.GetNextSpawnRotation()
            : Quaternion.identity;

        var instance = Instantiate(playerPrefab, pos, rot);
        instance.SpawnAsPlayerObject(clientId);
    }
    
    private void SpawnPlayerForChooseModel(ulong clientId) {
        if(playerPrefab == null) {
            Debug.LogError("[CustomNetworkManager] Player Prefab not assigned.");
            return;
        }

        var pos = SpawnManager.Instance != null
            ? SpawnManager.Instance.GetNextSpawnPosition()
            : new Vector3(0, 5, 0);

        var rot = SpawnManager.Instance != null
            ? SpawnManager.Instance.GetNextSpawnRotation()
            : Quaternion.identity;

        var prefab = playerPrefabs[clientId]; // TODO: placeholder, replace with actual selection logic
        var instance = Instantiate(prefab, pos, rot);
        instance.SpawnAsPlayerObject(clientId); // TODO: placeholder
    }
}