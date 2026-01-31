using System;
using System.Collections;
using System.Linq;
using Game.Match;
using Game.Player;
using Game.Spawning;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network {
    public class CustomNetworkManager : MonoBehaviour {
        [Header("Manual Player Prefab (do NOT rely on NetworkConfig.PlayerPrefab)")]
        [SerializeField] private NetworkObject playerPrefab;
        
        [Header("Pre-load Assets")]
        [SerializeField] private GameObject hopballPrefab;

        // When true (after Start Game), new joiners will be spawned automatically on connect.
        private bool _allowPlayerSpawns;
        private NetworkManager _networkManager;

        [Header("Team Settings")]
        [SerializeField] private bool autoBalanceTeams = true;

        // Track pending team assignments during initial batch spawn
        private readonly System.Collections.Generic.Dictionary<ulong, SpawnPoint.Team> _pendingTeamAssignments = new();

        // Cached array for spawn point validation (non-allocating overlap check)
        private readonly Collider[] _spawnValidationHits = new Collider[10];

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

        private void Start() {
            // Silently pre-load heavy assets (Player, Weapons) to warm up shaders/textures
            // Only do this if we are not already in a game scene (e.g. standard boot from Menu)
            var activeScene = SceneManager.GetActiveScene();
            if(!activeScene.name.Contains("Game")) {
                var loader = gameObject.AddComponent<Game.Systems.SilentAssetLoader>();
                var extraAssets = new System.Collections.Generic.List<GameObject>();
                if(hopballPrefab != null) extraAssets.Add(hopballPrefab);
                loader.StartLoading(playerPrefab, extraAssets);
            }
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
        }

        // --- Public utility: call when leaving to menu/lobby ---
        public void ResetSpawningState() {
            _allowPlayerSpawns = false;
            _pendingTeamAssignments.Clear();
        }

        private void OnServerStopped(bool _) => ResetSpawningState();
        private void OnClientStopped(bool _) => _allowPlayerSpawns = false;

        private static void OnClientDisconnected(ulong _) {
        }

        private static void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request,
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

            var clients = NetworkManager.Singleton.ConnectedClientsIds.ToList();
            
            // Shuffle client list for random spawn order
            ShuffleList(clients);

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
                var isTeamBased = matchSettings != null &&
                                  MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId);

                // 2. Assign team first (if team-based) so we can use it for spawn point selection
                var assignedTeam = SpawnPoint.Team.TeamA;
                if(isTeamBased) {
                    assignedTeam = AssignTeam();
                }

                // 3. Choose spawn point
                var spawnPoint =
                    // ---- TEAM-BASED SPAWN ----
                    isTeamBased ? SpawnManager.Instance.GetNextSpawnPoint(assignedTeam) :
                    // ---- FREE-FOR-ALL SPAWN ----
                    SpawnManager.Instance.GetNextSpawnPoint();

                if(spawnPoint == null) {
                    // Fallback to default position if no spawn points available
                    Debug.LogWarning("[CustomNetworkManager] No spawn points available, using fallback position");
                    continue;
                }

                var spawnPointTransform = spawnPoint.transform;
                var pos = spawnPointTransform.position;
                var rot = spawnPointTransform.rotation;

                // 4. Validate spawn point (optional safety)
                var layerMask = LayerMask.GetMask("Player", "Enemy");
                var hitCount = Physics.OverlapSphereNonAlloc(pos, 0.5f, _spawnValidationHits, layerMask);
                if(hitCount > 0) {
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
                    var controller = instance.GetComponent<PlayerController>();
                    PlayerTeamManager teamMgr = null;
                    if(controller != null) {
                        teamMgr = controller.TeamManager;
                    }
                    if(teamMgr != null) {
                        teamMgr.netTeam.Value = assignedTeam;
                        // Track pending assignment during initial spawn
                        _pendingTeamAssignments[clientId] = assignedTeam;
                    }
                }

                // 8. Re-enable CharacterController next frame
                StartCoroutine(EnableCcNextFrame(cc));

                break;
            }
        }


        // ========================================================================
        // Helper: Assign team (auto-balance with randomness)
        // ========================================================================
        private SpawnPoint.Team AssignTeam() {
            if(!autoBalanceTeams) {
                // Random team assignment when auto-balance is off
                return UnityEngine.Random.Range(0, 2) == 0 ? SpawnPoint.Team.TeamA : SpawnPoint.Team.TeamB;
            }

            var countA = 0;
            var countB = 0;

            // Count teams from pending assignments (players being spawned right now)
            foreach(var assignment in _pendingTeamAssignments.Values) {
                switch(assignment) {
                    case SpawnPoint.Team.TeamA:
                        countA++;
                        break;
                    case SpawnPoint.Team.TeamB:
                        countB++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            // Also count teams from already-spawned players (for late joiners)
            var clients = NetworkManager.Singleton.ConnectedClients.Values;
            foreach(var client in clients) {
                if(_pendingTeamAssignments.ContainsKey(client.ClientId)) continue; // Skip if already counted

                PlayerController controller = null;
                if(client.PlayerObject != null) {
                    controller = client.PlayerObject.GetComponent<PlayerController>();
                }
                PlayerTeamManager teamMgr = null;
                if(controller != null) {
                    teamMgr = controller.TeamManager;
                }
                if(teamMgr == null) continue;
                
                var team = teamMgr.netTeam.Value;
                switch(team) {
                    case SpawnPoint.Team.TeamA:
                        countA++;
                        break;
                    case SpawnPoint.Team.TeamB:
                        countB++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            // If teams are balanced, randomly assign
            if(countA == countB) {
                return UnityEngine.Random.Range(0, 2) == 0 ? SpawnPoint.Team.TeamA : SpawnPoint.Team.TeamB;
            }
            
            // Otherwise, assign to smaller team
            return countA < countB ? SpawnPoint.Team.TeamA : SpawnPoint.Team.TeamB;
        }
        
        /// <summary>
        /// Shuffles a list using Fisher-Yates algorithm for true randomness.
        /// </summary>
        private static void ShuffleList<T>(System.Collections.Generic.List<T> list) {
            var n = list.Count;
            while(n > 1) {
                n--;
                var k = UnityEngine.Random.Range(0, n + 1);
                (list[k], list[n]) = (list[n], list[k]);
            }
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