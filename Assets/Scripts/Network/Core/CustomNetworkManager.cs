using System.Collections;
using System.Linq;
using Diagnostics;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network.Core {
    public class CustomNetworkManager : MonoBehaviour {
        [Header("Manual Player Prefab (do NOT rely on NetworkConfig.PlayerPrefab)")]
        [SerializeField] private NetworkObject playerPrefab;
        
        [Header("Pre-load Assets")]
        [SerializeField] private GameObject hopballPrefab;

        // When true (after Start Game), new joiners will be spawned automatically on connect.
        private bool _allowPlayerSpawns;
        private NetworkManager _networkManager;

        // Game-provided hooks for approval metadata and player spawning.
        private static System.Action<ulong, ConnectionPayload> onClientApproved;
        private static System.Action<System.Collections.Generic.List<ulong>> prepareBatchSpawns;
        private static System.Func<ulong, NetworkObject, NetworkObject> spawnPlayerForClient;
        public static System.Func<string, bool> IsGameplayScenePredicate { get; set; }

        private static bool IsGameplaySceneName(string sceneName) {
            return IsGameplayScenePredicate != null && IsGameplayScenePredicate(sceneName);
        }

        /// <summary>
        /// Registers game-specific hooks for connection approval metadata and player spawning.
        /// This allows the network stack to remain agnostic of Game.* types.
        /// </summary>
        public static void SetGameHooks(
            System.Action<ulong, ConnectionPayload> onClientApprovedHook,
            System.Action<System.Collections.Generic.List<ulong>> prepareBatchSpawnsHook,
            System.Func<ulong, NetworkObject, NetworkObject> spawnPlayerForClientHook) {
            onClientApproved = onClientApprovedHook;
            prepareBatchSpawns = prepareBatchSpawnsHook;
            spawnPlayerForClient = spawnPlayerForClientHook;
        }

        private void Awake() {
            if(NetworkManager.Singleton != null && NetworkManager.Singleton.gameObject != gameObject) {
                Destroy(gameObject);
                return;
            }

            DontDestroyOnLoad(gameObject);

            _networkManager = NetworkManager.Singleton;
            if(_networkManager == null) return;

            // 1) Enable approval BEFORE networking starts.
            _networkManager.NetworkConfig.ConnectionApproval = true;

            // 2) Ensure the built-in auto-spawn path is disabled.
            _networkManager.NetworkConfig.PlayerPrefab = null;

            // 3) Register approval callback.
            _networkManager.ConnectionApprovalCallback = ApprovalCheck;
        }

        private void OnEnable() {
            if(!_networkManager) _networkManager = NetworkManager.Singleton;
            if(!_networkManager) return;

            _networkManager.OnClientConnectedCallback += OnClientConnected;
            _networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            _networkManager.OnServerStopped += OnServerStopped;
            _networkManager.OnClientStopped += OnClientStopped;
            _networkManager.OnSessionOwnerPromoted += OnSessionOwnerPromoted;
        }

        private void OnDisable() {
            if(!_networkManager) return;
            
            _networkManager.OnClientConnectedCallback -= OnClientConnected;
            _networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            _networkManager.OnServerStopped -= OnServerStopped;
            _networkManager.OnClientStopped -= OnClientStopped;
            _networkManager.OnSessionOwnerPromoted -= OnSessionOwnerPromoted;
        }

        // --- Public utility: call when leaving to menu/lobby ---
        private void ResetSpawningState() {
            _allowPlayerSpawns = false;
            PrivateMatchTeamAssignments.Clear();
        }

        private void OnServerStopped(bool _) => ResetSpawningState();
        private void OnClientStopped(bool _) {
            if(_networkManager == null) {
                _networkManager = NetworkManager.Singleton;
            }

            if(_networkManager != null &&
               _networkManager.DistributedAuthorityMode &&
               IsGameplaySceneName(SceneManager.GetActiveScene().name)) {
                StartCoroutine(HandleDaClientStopped());
                return;
            }

            _allowPlayerSpawns = false;
        }

        private static void OnClientDisconnected(ulong _) {
        }

        private static void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request,
            NetworkManager.ConnectionApprovalResponse response) {
            response.Approved = true;
            response.CreatePlayerObject = false; // We spawn manually

            var payload = ConnectionPayload.Decode(request.Payload);
            if(payload == null) return;
            onClientApproved?.Invoke(request.ClientNetworkId, payload);
        }

        private void OnClientConnected(ulong clientId) {
            if(!_allowPlayerSpawns || !NetworkAuthority.HasGlobalAuthority(NetworkManager.Singleton)) return;
            SpawnPlayerFor(clientId);
            ScheduleVisibilityReconciliation("OnClientConnected");
        }

        private void OnSessionOwnerPromoted(ulong _) {
            if(_networkManager == null) {
                _networkManager = NetworkManager.Singleton;
            }

            if(_networkManager != null &&
               NetworkAuthority.HasGlobalAuthority(_networkManager) &&
               IsGameplaySceneName(SceneManager.GetActiveScene().name)) {
                _allowPlayerSpawns = true;
            }

            if(!_allowPlayerSpawns || !NetworkAuthority.HasGlobalAuthority(_networkManager) || _networkManager == null) {
                return;
            }

            foreach(var clientId in _networkManager.ConnectedClientsIds) {
                SpawnPlayerFor(clientId);
            }

            ScheduleVisibilityReconciliation("OnSessionOwnerPromoted");
        }

        private IEnumerator HandleDaClientStopped() {
            const float graceSeconds = 2f;
            yield return new WaitForSeconds(graceSeconds);

            if(_networkManager == null) {
                _networkManager = NetworkManager.Singleton;
            }

            if(_networkManager != null &&
               _networkManager.IsListening &&
               IsGameplaySceneName(SceneManager.GetActiveScene().name)) {
                if(NetworkAuthority.HasGlobalAuthority(_networkManager)) {
                    _allowPlayerSpawns = true;
                }
                yield break;
            }

            _allowPlayerSpawns = false;
        }

        /// <summary>
        /// Called by SessionManager when the "Game" scene is loaded on the host.
        /// </summary>
        public void EnableGameplaySpawning() {
            _allowPlayerSpawns = true;

            if(!NetworkAuthority.HasGlobalAuthority(NetworkManager.Singleton)) {
                DevLog.LogWarning("[CustomNetworkManager] Not server, skipping spawn");
                return;
            }

            var activeScene = SceneManager.GetActiveScene();
            if(!IsGameplaySceneName(activeScene.name)) {
                DevLog.LogWarning($"[CustomNetworkManager] Wrong scene: {activeScene.name} (expected gameplay scene)");
                return;
            }

            var clients = NetworkManager.Singleton.ConnectedClientsIds.ToList();
            prepareBatchSpawns?.Invoke(clients);

            foreach(var id in clients)
                SpawnPlayerFor(id);
            ScheduleVisibilityReconciliation("EnableGameplaySpawning");
        }

        private void ScheduleVisibilityReconciliation(string context) {
            if(!isActiveAndEnabled) {
                return;
            }

            StartCoroutine(ReconcileVisibilityAfterSpawn(context));
        }

        private IEnumerator ReconcileVisibilityAfterSpawn(string context) {
            const int passes = 8;
            const float retryDelaySeconds = 0.25f;

            yield return null;
            yield return null;

            for(var pass = 0; pass < passes; pass++) {
                if(_networkManager == null) {
                    _networkManager = NetworkManager.Singleton;
                }

                if(_networkManager == null || !_allowPlayerSpawns || !NetworkAuthority.HasGlobalAuthority(_networkManager)) {
                    yield break;
                }

                EnsureAllSpawnedPlayersVisible($"{context}/pass{pass + 1}");
                yield return new WaitForSeconds(retryDelaySeconds);
            }
        }

        private void EnsureAllSpawnedPlayersVisible(string context) {
            if(_networkManager == null || !NetworkAuthority.HasGlobalAuthority(_networkManager)) {
                return;
            }

            foreach(var client in _networkManager.ConnectedClients.Values) {
                if(client?.PlayerObject == null || !client.PlayerObject.IsSpawned) {
                    continue;
                }

                EnsureNetworkObjectVisibleToAll(client.PlayerObject, context);
            }
        }

        private void EnsureNetworkObjectVisibleToAll(NetworkObject networkObject, string context) {
            if(networkObject == null || !networkObject.IsSpawned || _networkManager == null) {
                return;
            }

            var newlyShownCount = 0;
            foreach(var observerClientId in _networkManager.ConnectedClientsIds) {
                if(networkObject.IsNetworkVisibleTo(observerClientId)) {
                    continue;
                }

                networkObject.NetworkShow(observerClientId);
                newlyShownCount++;
            }

            if(Debug.isDebugBuild && newlyShownCount > 0) {
                DevLog.Log(
                    $"[CustomNetworkManager] Reconciled visibility for '{networkObject.name}' to {newlyShownCount} client(s) ({context}).");
            }
        }

        private void SpawnPlayerFor(ulong clientId) {
            if(spawnPlayerForClient == null) {
                DevLog.LogError("[CustomNetworkManager] No game spawn provider registered. Cannot spawn players.");
                return;
            }

            // Prevent double-spawn
            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client) &&
               client.PlayerObject != null) {
                DevLog.LogWarning($"[CustomNetworkManager] Player already spawned for {clientId}");
                return;
            }

            if(playerPrefab == null) {
                DevLog.LogError("[CustomNetworkManager] Player prefab is not assigned. Cannot spawn players.");
                return;
            }

            var instance = spawnPlayerForClient(clientId, playerPrefab);
            if(instance == null) return;

            EnsureNetworkObjectVisibleToAll(instance, $"SpawnPlayerFor/{clientId}");
        }


        // (All game-specific team assignment, spawn point selection, and player metadata
        //  capture logic has been moved to a Game-side adapter via the registered hooks.)
    }
}
