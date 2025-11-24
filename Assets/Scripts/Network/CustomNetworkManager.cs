using System.Collections;
using Game.Player;
using Network.Singletons;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network {
    public class CustomNetworkManager : MonoBehaviour {
        [Header("Manual Player Prefab (do NOT rely on NetworkConfig.PlayerPrefab)")]
        [SerializeField] private NetworkObject playerPrefab;

        [SerializeField] private Material[] playerMaterials;

        // When true (after Start Game), new joiners will be spawned automatically on connect.
        private bool _allowPlayerSpawns;
        private NetworkManager _networkManager;

        [Header("Team Settings")]
        [SerializeField] private bool autoBalanceTeams = true;

        // Track pending team assignments during initial batch spawn
        private readonly System.Collections.Generic.Dictionary<ulong, SpawnPoint.Team> _pendingTeamAssignments = new();

        private void Awake() {
            if(NetworkManager.Singleton != null && NetworkManager.Singleton.gameObject != gameObject) {
                Destroy(gameObject);
                return;
            }

            DontDestroyOnLoad(gameObject);

            _networkManager = NetworkManager.Singleton;
            if(!_networkManager) return;

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
        }

        private void OnDisable() {
            if(_networkManager) {
                _networkManager.OnClientConnectedCallback -= OnClientConnected;
                _networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
                _networkManager.OnServerStopped -= OnServerStopped;
                _networkManager.OnClientStopped -= OnClientStopped;
            }
        }

        // --- Public utility: call when leaving to menu/lobby ---
        public void ResetSpawningState() {
            _allowPlayerSpawns = false;
            _pendingTeamAssignments.Clear();
        }

        private void OnServerStopped(bool _) => ResetSpawningState();
        private void OnClientStopped(bool _) => _allowPlayerSpawns = false;

        private void OnClientDisconnected(ulong _) {
        }

        private void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request,
            NetworkManager.ConnectionApprovalResponse response) {
            response.Approved = true;
            response.CreatePlayerObject = false; // We spawn manually
        }

        private void OnClientConnected(ulong clientId) {
            if(_allowPlayerSpawns && NetworkManager.Singleton.IsServer)
                SpawnPlayerFor(clientId);
        }

        /// <summary>
        /// Called by SessionManager when the "Game" scene is loaded on the host.
        /// </summary>
        public void EnableGameplaySpawningAndSpawnAll() {
            _allowPlayerSpawns = true;

            if(!NetworkManager.Singleton.IsServer) {
                Debug.LogWarning("[CustomNetworkManager] Not server, skipping spawn");
                return;
            }

            // Check by scene name instead of build index (build index changes when Init scene is added)
            var activeScene = SceneManager.GetActiveScene();
            // Allow "Game" scene or any scene that contains "Game" in the name
            // This is more flexible than checking build index which changes when Init scene is added
            if(!activeScene.name.Contains("Game")) {
                Debug.LogWarning($"[CustomNetworkManager] Wrong scene: {activeScene.name} (expected Game scene)");
                return;
            }

            var clients = NetworkManager.Singleton.ConnectedClientsIds;
            Debug.Log($"[CustomNetworkManager] Spawning for {clients.Count} clients: {string.Join(", ", clients)}");

            // Clear pending assignments before batch spawn
            _pendingTeamAssignments.Clear();

            foreach(var id in clients)
                SpawnPlayerFor(id);

            // Clear pending assignments after batch spawn
            _pendingTeamAssignments.Clear();
        }

        public void DisableSpawning() => _allowPlayerSpawns = false;

        // ========================================================================
        // MAIN SPAWN LOGIC – Game Mode Aware
        // ========================================================================
        public void SpawnPlayerFor(ulong clientId) {
            while(true) {
                // Prevent double-spawn
                if(NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client) &&
                   client.PlayerObject != null) {
                    Debug.LogWarning($"[CustomNetworkManager] Player already spawned for {clientId}");
                    return;
                }

                // 1. Determine game mode
                var matchSettings = MatchSettingsManager.Instance;
                var isTeamBased = matchSettings != null && IsTeamBasedMode(matchSettings.selectedGameModeId);

                // 2. Assign team first (if team-based) so we can use it for spawn point selection
                var assignedTeam = SpawnPoint.Team.TeamA;
                if(isTeamBased) {
                    assignedTeam = AssignTeam(clientId);
                }

                // 3. Choose spawn point
                SpawnPoint spawnPoint;
                if(isTeamBased) {
                    // ---- TEAM-BASED SPAWN ----
                    spawnPoint = SpawnManager.Instance.GetNextSpawnPoint(assignedTeam);
                } else {
                    // ---- FREE-FOR-ALL SPAWN ----
                    spawnPoint = SpawnManager.Instance.GetNextSpawnPoint();
                }

                if(spawnPoint == null) {
                    // Fallback to default position if no spawn points available
                    Debug.LogWarning("[CustomNetworkManager] No spawn points available, using fallback position");
                    continue;
                }

                Vector3 pos = spawnPoint.transform.position;
                Quaternion rot = spawnPoint.transform.rotation;

                // 4. Validate spawn point (optional safety)
                var layerMask = LayerMask.GetMask("Player", "Enemy");
                if(Physics.OverlapSphere(pos, 0.5f, layerMask).Length > 0) {
                    Debug.LogWarning("Spawn point occupied, retrying...");
                    continue;
                }

                // 5. Instantiate player
                var instance = Instantiate(playerPrefab, pos, rot);
                var cc = instance.GetComponent<CharacterController>();
                if(cc) cc.enabled = false;

                instance.name = $"Player_{clientId}_{(isTeamBased ? $"Team{assignedTeam}" : "FFA")}";

                // 5.5. Disable PlayerInput immediately to prevent control scheme assignment errors
                // The error occurs because PlayerInput tries to assign a control scheme during Instantiate,
                // but in multiplayer only the owner should have input. We disable it here and let
                // OnNetworkSpawn in PlayerInput.cs re-enable it for the owner.
                var playerInput = instance.GetComponent<UnityEngine.InputSystem.PlayerInput>();
                if(playerInput != null) {
                    playerInput.enabled = false;
                }

                // 6. Spawn as player object
                instance.SpawnAsPlayerObject(clientId);

                // 7. TEAM SETUP (only for team modes)
                if(isTeamBased && NetworkManager.Singleton.IsServer) {
                    var teamMgr = instance.GetComponent<PlayerTeamManager>();
                    if(teamMgr != null) {
                        teamMgr.netTeam.Value = assignedTeam;
                        // Track pending assignment during initial spawn
                        _pendingTeamAssignments[clientId] = assignedTeam;
                        Debug.Log(
                            $"[CustomNetworkManager] Assigned team {assignedTeam} to client {clientId} at spawn position {pos}");
                    }
                }

                // 8. Re-enable CharacterController next frame
                StartCoroutine(EnableCcNextFrame(cc));

                break;
            }
        }

        // ========================================================================
        // Helper: Is this a team-based mode?
        // ========================================================================
        private static bool IsTeamBasedMode(string modeId) => modeId switch {
            "Team Deathmatch" => true,
            "Hopball" => true,
            "CTF" => true,
            "Oddball" => true,
            "KOTH" => true,
            // Add more team modes here
            _ => false // Deathmatch, Private Match, etc.
        };

        // ========================================================================
        // Helper: Assign team (auto-balance)
        // ========================================================================
        private SpawnPoint.Team AssignTeam(ulong clientId) {
            if(!autoBalanceTeams)
                return (clientId % 2 == 0) ? SpawnPoint.Team.TeamA : SpawnPoint.Team.TeamB;

            var countA = 0;
            var countB = 0;

            // Count teams from pending assignments (players being spawned right now)
            foreach(var assignment in _pendingTeamAssignments.Values) {
                if(assignment == SpawnPoint.Team.TeamA) countA++;
                else if(assignment == SpawnPoint.Team.TeamB) countB++;
            }

            // Also count teams from already-spawned players (for late joiners)
            var clients = NetworkManager.Singleton.ConnectedClients.Values;
            foreach(var client in clients) {
                if(_pendingTeamAssignments.ContainsKey(client.ClientId)) continue; // Skip if already counted

                var teamMgr = client.PlayerObject?.GetComponent<PlayerTeamManager>();
                if(teamMgr != null) {
                    var team = teamMgr.netTeam.Value;
                    if(team == SpawnPoint.Team.TeamA) countA++;
                    else if(team == SpawnPoint.Team.TeamB) countB++;
                }
            }

            return countA <= countB ? SpawnPoint.Team.TeamA : SpawnPoint.Team.TeamB;
        }

        // ========================================================================
        // Re-enable CharacterController after spawn
        // ========================================================================
        private static IEnumerator EnableCcNextFrame(CharacterController cc) {
            yield return null;
            if(cc) cc.enabled = true;
        }
    }
}