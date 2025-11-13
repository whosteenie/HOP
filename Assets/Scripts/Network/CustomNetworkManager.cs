using Game.Player;
using Network.Singletons;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network {
    public class CustomNetworkManager : MonoBehaviour {
        [Header("Manual Player Prefab (do NOT rely on NetworkConfig.PlayerPrefab)")]
        [SerializeField] private NetworkObject playerPrefab;
        [SerializeField] private NetworkObject[] playerPrefabs;
        [SerializeField] private Material[] playerMaterials;
    
        // When true (after Start Game), new joiners will be spawned automatically on connect.
        private bool _allowPlayerSpawns;
        private NetworkManager _networkManager;
        private int _playerAmount;

        private void Awake() {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.gameObject != gameObject) {
                Destroy(gameObject);
                return;
            }
    
            DontDestroyOnLoad(gameObject);
            
            _networkManager = NetworkManager.Singleton;
            if(!_networkManager) return;

            // 1) Enable approval BEFORE networking starts.
            _networkManager.NetworkConfig.ConnectionApproval = true;

            // 2) Ensure the built-in auto-spawn path is disabled by leaving PlayerPrefab null.
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
    
        // --- Public utility: call this when leaving to menu/lobby ---
        public void ResetSpawningState() {
            _allowPlayerSpawns = false;
            _playerAmount = 0;
        }

        private void OnServerStopped(bool _) {
            // Host stopped -> ensure we never auto-spawn when reconnecting later.
            _allowPlayerSpawns = false;
            _playerAmount = 0;
        }

        private void OnClientStopped(bool _) {
            _allowPlayerSpawns = false;
        }

        private void OnClientDisconnected(ulong _) {
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
            
            Debug.Log($"[CustomNetworkManager] EnableGameplaySpawningAndSpawnAll called. IsServer: {NetworkManager.Singleton.IsServer}, Scene: {SceneManager.GetActiveScene().name}");

            if(!NetworkManager.Singleton.IsServer) {
                Debug.LogWarning("[CustomNetworkManager] Not server, skipping spawn");
                return;
            }

            if(SceneManager.GetActiveScene().name != "Game") {
                Debug.LogWarning($"[CustomNetworkManager] Wrong scene: {SceneManager.GetActiveScene().name}");
                return;
            }
            
            var connectedClients = NetworkManager.Singleton.ConnectedClientsIds;
            Debug.Log($"[CustomNetworkManager] Spawning for {connectedClients.Count} connected clients: {string.Join(", ", connectedClients)}");

            foreach(var id in NetworkManager.Singleton.ConnectedClientsIds)
                SpawnPlayerFor(id);
        }
    
        public void DisableSpawning() {
            _allowPlayerSpawns = false;
        }

        private void SpawnPlayerFor(ulong clientId) {
            foreach(var client in NetworkManager.Singleton.ConnectedClients.Values) {
                if(client.ClientId == clientId && client.PlayerObject != null) {
                    Debug.LogWarning($"[CustomNetworkManager] Player already spawned for {clientId}");
                    return; // Already spawned!
                }
            }
            
            while(true) {
                var pos = SpawnManager.Instance.GetNextSpawnPosition();
                var rot = SpawnManager.Instance.GetNextSpawnRotation();

                var layer = LayerMask.NameToLayer("Player") | LayerMask.NameToLayer("Enemy");
                if(Physics.SphereCast(new Ray(pos, Vector3.forward * 0f), 0.5f, out var hit, 0f, layer)) {
                    Debug.LogWarning("Spawn point occupied, finding next...");
                    continue;
                }

                var instance = Instantiate(playerPrefab, pos, rot);

                var cc = instance.GetComponent<CharacterController>();
                if(cc) cc.enabled = false;
                instance.name = "Player " + (_playerAmount + 1);

                _playerAmount++;

                instance.SpawnAsPlayerObject(clientId);

                break;
            }
        }
    }
}