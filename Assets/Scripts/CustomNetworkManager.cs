using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class CustomNetworkManager : MonoBehaviour {
    [Header("Manual Player Prefab (do NOT rely on NetworkConfig.PlayerPrefab)")]
    [SerializeField] private NetworkObject playerPrefab;
    [SerializeField] private NetworkObject[] playerPrefabs;

    // When true (after Start Game), new joiners will be spawned automatically on connect.
    private bool _allowPlayerSpawns;
    private NetworkManager _networkManager;

    private void Awake() {
        _networkManager = NetworkManager.Singleton;
        if(!_networkManager) return;

        // 1) Enable approval BEFORE networking starts.
        _networkManager.NetworkConfig.ConnectionApproval = true;

        // 2) Ensure the built-in auto-spawn path is disabled by leaving PlayerPrefab null.
        //    Use our own serialized playerPrefab for manual spawning.
        _networkManager.NetworkConfig.PlayerPrefab = null;

        // 3) Register approval callback now so the HOST local connection is governed by it.
        _networkManager.ConnectionApprovalCallback = ApprovalCheck;
    }

    private void OnEnable() {
        if(!_networkManager) _networkManager = NetworkManager.Singleton; 
        if(!_networkManager) return;
        _networkManager.OnClientConnectedCallback += OnClientConnected;
        _networkManager.OnClientDisconnectCallback += OnClientDisconnected;

        _networkManager.OnServerStopped += OnServerStopped;
        _networkManager.OnClientStopped += OnClientStopped;
    }

    private void OnDisable() {
        if(!_networkManager) return;
        _networkManager.OnClientConnectedCallback -= OnClientConnected;
        _networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        
        _networkManager.OnServerStopped -= OnServerStopped;
        _networkManager.OnClientStopped -= OnClientStopped;

        // leave approval callback in place; safe across scenes if this persists
        if(_networkManager.ConnectionApprovalCallback == ApprovalCheck)
            _networkManager.ConnectionApprovalCallback = null;
    }
    
    // --- Public utility: call this when leaving to menu/lobby ---
    public void ResetSpawningState()
    {
        _allowPlayerSpawns = false;  // late-joiners won't be spawned
    }

    private void OnServerStopped(bool _)
    {
        // Host stopped -> ensure we never auto-spawn when reconnecting later.
        _allowPlayerSpawns = false;
    }

    private void OnClientStopped(bool _)
    {
        _allowPlayerSpawns = false;
    }

    private void OnClientDisconnected(ulong _)
    {
        // nothing special, but handy if you later add per-client tracking
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

        if(!NetworkManager.Singleton.IsServer || SceneManager.GetActiveScene().name != "Game")
            return;

        foreach(var id in NetworkManager.Singleton.ConnectedClientsIds)
            SpawnPlayerFor(id);
    }
    
    public void DisableSpawning() {
        _allowPlayerSpawns = false;
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