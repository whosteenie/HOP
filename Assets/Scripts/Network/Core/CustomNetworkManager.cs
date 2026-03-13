using System.Collections;
using System.Linq;
using Game.Match;
using Game.Player;
using Game.Spawning;
using Network.Core;
using Network.Diagnostics;
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
        
        // Connection payload-derived metadata (transport agnostic)
        private readonly System.Collections.Generic.Dictionary<ulong, string> _clientPartyIds = new();
        private readonly System.Collections.Generic.Dictionary<ulong, ulong> _clientSteamIds = new();
        private readonly System.Collections.Generic.Dictionary<ulong, string> _clientUgsPlayerIds = new();
        private bool _hasSessionPrivateFlag;
        private bool _sessionIsPrivateMatch;

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
            _pendingTeamAssignments.Clear();
            _clientPartyIds.Clear();
            _clientSteamIds.Clear();
            _clientUgsPlayerIds.Clear();
            _hasSessionPrivateFlag = false;
            _sessionIsPrivateMatch = false;
            PrivateMatchTeamAssignments.Clear();
        }

        private void OnServerStopped(bool _) => ResetSpawningState();
        private void OnClientStopped(bool _) {
            if(_networkManager == null) {
                _networkManager = NetworkManager.Singleton;
            }

            if(_networkManager != null &&
               _networkManager.DistributedAuthorityMode &&
               Session.SessionManager.IsGameplaySceneName(SceneManager.GetActiveScene().name)) {
                StartCoroutine(HandleDistributedAuthorityClientStopped());
                return;
            }

            _allowPlayerSpawns = false;
        }

        private static void OnClientDisconnected(ulong _) {
        }

        private void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request,
            NetworkManager.ConnectionApprovalResponse response) {
            response.Approved = true;
            response.CreatePlayerObject = false; // We spawn manually

            var payload = ConnectionPayload.Decode(request.Payload);
            if(payload == null) return;

            if(!string.IsNullOrEmpty(payload.partyId)) {
                _clientPartyIds[request.ClientNetworkId] = payload.partyId;
            }

            if(payload.steamId != 0) {
                _clientSteamIds[request.ClientNetworkId] = payload.steamId;
            }

            if(!string.IsNullOrWhiteSpace(payload.ugsPlayerId)) {
                _clientUgsPlayerIds[request.ClientNetworkId] = payload.ugsPlayerId;
                TryRefreshClientMetadataFromDistributedAuthoritySession(request.ClientNetworkId);
            }

            if(_hasSessionPrivateFlag) return;
            _sessionIsPrivateMatch = payload.isPrivateMatch;
            _hasSessionPrivateFlag = true;
        }

        private void OnClientConnected(ulong clientId) {
            if(_allowPlayerSpawns && NetworkAuthority.HasGlobalAuthority(NetworkManager.Singleton)) {
                SpawnPlayerFor(clientId);
                SchedulePlayerVisibilityReconciliation("OnClientConnected");
            }
        }

        private void OnSessionOwnerPromoted(ulong _) {
            if(_networkManager == null) {
                _networkManager = NetworkManager.Singleton;
            }

            if(_networkManager != null &&
               NetworkAuthority.HasGlobalAuthority(_networkManager) &&
               Session.SessionManager.IsGameplaySceneName(SceneManager.GetActiveScene().name)) {
                _allowPlayerSpawns = true;
            }

            if(!_allowPlayerSpawns || !NetworkAuthority.HasGlobalAuthority(_networkManager) || _networkManager == null) {
                return;
            }

            foreach(var clientId in _networkManager.ConnectedClientsIds) {
                SpawnPlayerFor(clientId);
            }

            SchedulePlayerVisibilityReconciliation("OnSessionOwnerPromoted");
        }

        private IEnumerator HandleDistributedAuthorityClientStopped() {
            const float graceSeconds = 2f;
            yield return new WaitForSeconds(graceSeconds);

            if(_networkManager == null) {
                _networkManager = NetworkManager.Singleton;
            }

            if(_networkManager != null &&
               _networkManager.IsListening &&
               Session.SessionManager.IsGameplaySceneName(SceneManager.GetActiveScene().name)) {
                if(NetworkAuthority.HasGlobalAuthority(_networkManager)) {
                    _allowPlayerSpawns = true;
                }
                yield break;
            }

            _allowPlayerSpawns = false;
        }

        public void ConfigureSessionMetadata(bool isPrivateMatch) {
            _sessionIsPrivateMatch = isPrivateMatch;
            _hasSessionPrivateFlag = true;
        }

        /// <summary>
        /// Called by SessionManager when the "Game" scene is loaded on the host.
        /// </summary>
        public void EnableGameplaySpawningAndSpawnAll() {
            _allowPlayerSpawns = true;

            if(!NetworkAuthority.HasGlobalAuthority(NetworkManager.Singleton)) {
                Debug.LogWarning("[CustomNetworkManager] Not server, skipping spawn");
                return;
            }

            var activeScene = SceneManager.GetActiveScene();
            if(!Session.SessionManager.IsGameplaySceneName(activeScene.name)) {
                Debug.LogWarning($"[CustomNetworkManager] Wrong scene: {activeScene.name} (expected gameplay scene)");
                return;
            }

            if(playerPrefab == null) {
                Debug.LogError("[CustomNetworkManager] Player prefab is not assigned. Cannot spawn players.");
                return;
            }

            if(SpawnManager.Instance == null) {
                Debug.LogError(
                    $"[CustomNetworkManager] SpawnManager is missing in scene '{activeScene.name}'. Cannot spawn players.");
                return;
            }

            if(MatchSettingsManager.Instance == null) {
                Debug.LogError(
                    $"[CustomNetworkManager] MatchSettingsManager is missing in scene '{activeScene.name}'. Cannot spawn players.");
                return;
            }

            var clients = NetworkManager.Singleton.ConnectedClientsIds.ToList();
            
            // Shuffle client list for random spawn order (initial shuffle)
            ShuffleList(clients);
            
            // Calculate Teams (Batch)
            if (MatchSettingsManager.IsTeamBasedMode(MatchSettingsManager.Instance.selectedGameModeId)) {
                CalculateTeamsForBatch(clients);
            }

            // Clear pending assignments before batch spawn
            _pendingTeamAssignments.Clear();

            foreach(var id in clients)
                SpawnPlayerFor(id);

            // Clear pending assignments after batch spawn
            _pendingTeamAssignments.Clear();
            SchedulePlayerVisibilityReconciliation("EnableGameplaySpawningAndSpawnAll");
        }

        private void SchedulePlayerVisibilityReconciliation(string context) {
            if(!isActiveAndEnabled) {
                return;
            }

            StartCoroutine(ReconcilePlayerVisibilityAfterSpawn(context));
        }

        private IEnumerator ReconcilePlayerVisibilityAfterSpawn(string context) {
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

                EnsureAllSpawnedPlayerObjectsVisible($"{context}/pass{pass + 1}");
                yield return new WaitForSeconds(retryDelaySeconds);
            }
        }

        private void EnsureAllSpawnedPlayerObjectsVisible(string context) {
            if(_networkManager == null || !NetworkAuthority.HasGlobalAuthority(_networkManager)) {
                return;
            }

            foreach(var client in _networkManager.ConnectedClients.Values) {
                if(client?.PlayerObject == null || !client.PlayerObject.IsSpawned) {
                    continue;
                }

                EnsureNetworkObjectVisibleToAllConnectedClients(client.PlayerObject, context);
            }
        }

        private void EnsureNetworkObjectVisibleToAllConnectedClients(NetworkObject networkObject, string context) {
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
                Debug.Log(
                    $"[CustomNetworkManager] Reconciled visibility for '{networkObject.name}' to {newlyShownCount} client(s) ({context}).");
            }
        }

        // ========================================================================
        // MAIN SPAWN LOGIC – Game Mode Aware
        // ========================================================================
        private void SpawnPlayerFor(ulong clientId) {
            const int maxSpawnAttempts = 8;
            for(var attempt = 0; attempt < maxSpawnAttempts; attempt++) {
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
                    assignedTeam = AssignTeam(clientId);
                }

                // 3. Choose spawn point
                var spawnManager = SpawnManager.Instance;
                if(spawnManager == null) {
                    Debug.LogError("[CustomNetworkManager] SpawnManager unavailable during player spawn.");
                    return;
                }

                var spawnPoint =
                    // ---- TEAM-BASED SPAWN ----
                    isTeamBased ? spawnManager.GetNextSpawnPoint(assignedTeam) :
                    // ---- FREE-FOR-ALL SPAWN ----
                    spawnManager.GetNextSpawnPoint();

                if(spawnPoint == null) {
                    Debug.LogError(
                        $"[CustomNetworkManager] No spawn points available in scene '{SceneManager.GetActiveScene().name}'.");
                    return;
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
                EnsureNetworkObjectVisibleToAllConnectedClients(instance, $"SpawnPlayerFor/{clientId}");
                FlowLog.Emit(FlowEventIds.PlayerSpawned,
                    ("clientId", clientId),
                    ("team", isTeamBased ? assignedTeam.ToString() : "None"),
                    ("mode", matchSettings != null ? matchSettings.selectedGameModeId : "Unknown"),
                    ("spawn", spawnPoint.name));

                // 7. TEAM SETUP (only for team modes)
                if(isTeamBased && NetworkAuthority.HasGlobalAuthority(NetworkManager.Singleton)) {
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

                var spawnedController = instance.GetComponent<PlayerController>();
                if(spawnedController != null) {
                    StartCoroutine(CaptureSpawnedPlayerMetadata(clientId, spawnedController));
                }

                return;
            }

            Debug.LogWarning(
                $"[CustomNetworkManager] Could not find a free spawn point after {maxSpawnAttempts} attempts for client {clientId}.");
        }


        // ========================================================================
        // Helper: Assign team (auto-balance with randomness)
        // ========================================================================
        // ========================================================================
        // Helper: Assign team (Party Aware)
        // ========================================================================
        private SpawnPoint.Team AssignTeam(ulong clientId = 0) {
            // If already assigned via pre-calculated batch, return that
            if (_pendingTeamAssignments.TryGetValue(clientId, out var assigned)) {
                return assigned;
            }

            // Fallback for individual joiners (or if logic failed): Auto-Balance by Count
            if(!autoBalanceTeams) {
                return Random.Range(0, 2) == 0 ? SpawnPoint.Team.TeamA : SpawnPoint.Team.TeamB;
            }

            var countA = 0;
            var countB = 0;

            // Count existing players (TeamManager netvars)
            foreach(var controller in PlayerController.SpawnedPlayers) {
                if(controller == null || controller.TeamManager == null) continue;
                if(controller.TeamManager.netTeam.Value == SpawnPoint.Team.TeamA) countA++;
                else countB++;
            }
            
            // Also count pending (if we are in a loop but somehow missed one)
            foreach(var team in _pendingTeamAssignments.Values) {
                if (team == SpawnPoint.Team.TeamA) countA++;
                else countB++;
            }

            if(countA == countB) return Random.Range(0, 2) == 0 ? SpawnPoint.Team.TeamA : SpawnPoint.Team.TeamB;
            return countA < countB ? SpawnPoint.Team.TeamA : SpawnPoint.Team.TeamB;
        }

        private void CalculateTeamsForBatch(System.Collections.Generic.List<ulong> clients) {
            _pendingTeamAssignments.Clear();

            foreach(var clientId in clients) {
                TryRefreshClientMetadataFromDistributedAuthoritySession(clientId);
            }

            // 1. Group clients by PartyID (from connection payload).
            // Map: PartyId -> List<ClientId>
            var partyGroups = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<ulong>>();
            var solos = new System.Collections.Generic.List<ulong>();

            foreach (var clientId in clients) {
                var pId = "";
                if(_clientPartyIds.TryGetValue(clientId, out var storedPartyId)) {
                    pId = storedPartyId;
                }

                if (string.IsNullOrEmpty(pId)) {
                    solos.Add(clientId);
                } else {
                    if (!partyGroups.ContainsKey(pId)) partyGroups[pId] = new System.Collections.Generic.List<ulong>();
                    partyGroups[pId].Add(clientId);
                }
            }

            // 2. Distribute Teams
            // Strategy:
            // Private Match with draft team assignments (from setup panel): use them.
            // Private Match without draft: split evenly (random order).
            // Public Match: Keep parties intact, balance total counts.

            if(_sessionIsPrivateMatch && PrivateMatchTeamAssignments.HasAssignments) {
                foreach(var clientId in clients) {
                    if(!_clientSteamIds.TryGetValue(clientId, out var steamId)) continue;
                    var teamIndex = PrivateMatchTeamAssignments.GetTeamIndexForSteamId(steamId);
                    if(teamIndex < 0) continue;
                    var team = teamIndex == 0 ? SpawnPoint.Team.TeamA : SpawnPoint.Team.TeamB;
                    _pendingTeamAssignments[clientId] = team;
                }
                PrivateMatchTeamAssignments.Clear();
                Debug.Log($"[CustomNetworkManager] Applied private match draft team assignments for {_pendingTeamAssignments.Count} players.");
                return;
            }

            // Check for single large party (Private Match scenario without draft)
            if (_sessionIsPrivateMatch || (partyGroups.Count == 1 && solos.Count == 0 && partyGroups.First().Value.Count > 1)) {
                // Split logic
                var allClients = new System.Collections.Generic.List<ulong>(clients);
                ShuffleList(allClients); // Randomize first
                
                for (var i = 0; i < allClients.Count; i++) {
                    var team = i % 2 == 0 ? SpawnPoint.Team.TeamA : SpawnPoint.Team.TeamB;
                    _pendingTeamAssignments[allClients[i]] = team;
                }
                Debug.Log($"[CustomNetworkManager] Distributed Private Match/Single Party of {clients.Count} players.");
                return;
            }

            // Public / Multiple Parties Logic
            // Sort parties by size (Descending) to place largest chunks first
            var sortedParties = partyGroups.Values.OrderByDescending(p => p.Count).ToList();

            var teamAMembers = new System.Collections.Generic.List<ulong>();
            var teamBMembers = new System.Collections.Generic.List<ulong>();

            foreach (var party in sortedParties) {
                // Assign entire party to the smaller team
                if (teamAMembers.Count <= teamBMembers.Count) {
                    teamAMembers.AddRange(party);
                    foreach(var id in party) _pendingTeamAssignments[id] = SpawnPoint.Team.TeamA;
                } else {
                    teamBMembers.AddRange(party);
                    foreach(var id in party) _pendingTeamAssignments[id] = SpawnPoint.Team.TeamB;
                }
            }

            // Distribute Solos to balance remaining
            ShuffleList(solos);
            foreach (var soloId in solos) {
                 if (teamAMembers.Count <= teamBMembers.Count) {
                    teamAMembers.Add(soloId);
                    _pendingTeamAssignments[soloId] = SpawnPoint.Team.TeamA;
                 } else {
                     teamBMembers.Add(soloId);
                     _pendingTeamAssignments[soloId] = SpawnPoint.Team.TeamB;
                 }
            }
             
            Debug.Log($"[CustomNetworkManager] Distributed Teams (Public): TeamA={teamAMembers.Count}, TeamB={teamBMembers.Count}");
        }

        private bool TryRefreshClientMetadataFromDistributedAuthoritySession(ulong clientId) {
            if(!_clientUgsPlayerIds.TryGetValue(clientId, out var ugsPlayerId) || string.IsNullOrWhiteSpace(ugsPlayerId)) {
                return false;
            }

            var sessionManager = Session.SessionManager.Instance;
            if(sessionManager == null ||
               !sessionManager.TryResolveDistributedAuthorityPlayerMetadata(ugsPlayerId, out var partyId, out var steamId)) {
                return false;
            }

            if(!string.IsNullOrWhiteSpace(partyId)) {
                _clientPartyIds[clientId] = partyId;
            }

            if(steamId != 0) {
                _clientSteamIds[clientId] = steamId;
            }

            return !string.IsNullOrWhiteSpace(partyId) || steamId != 0;
        }

        private IEnumerator CaptureSpawnedPlayerMetadata(ulong clientId, PlayerController controller) {
            const int maxAttempts = 120;

            for(var attempt = 0; attempt < maxAttempts; attempt++) {
                if(controller == null) {
                    yield break;
                }

                var updated = false;
                var ugsPlayerId = controller.UgsId.Value.ToString();
                if(!string.IsNullOrWhiteSpace(ugsPlayerId) &&
                   (!_clientUgsPlayerIds.TryGetValue(clientId, out var cachedUgsId) ||
                    !string.Equals(cachedUgsId, ugsPlayerId, System.StringComparison.Ordinal))) {
                    _clientUgsPlayerIds[clientId] = ugsPlayerId;
                    updated = true;
                }

                var steamId = controller.SteamId.Value;
                if(steamId != 0 && !_clientSteamIds.ContainsKey(clientId)) {
                    _clientSteamIds[clientId] = steamId;
                    updated = true;
                }

                if(updated) {
                    TryRefreshClientMetadataFromDistributedAuthoritySession(clientId);
                }

                var hasResolvedUgs = _clientUgsPlayerIds.ContainsKey(clientId);
                var hasResolvedSteam = _clientSteamIds.ContainsKey(clientId);
                if(hasResolvedUgs && hasResolvedSteam) {
                    yield break;
                }

                yield return null;
            }
        }
        
        /// <summary>
        /// Shuffles a list using Fisher-Yates algorithm for true randomness.
        /// </summary>
        private static void ShuffleList<T>(System.Collections.Generic.List<T> list) {
            var n = list.Count;
            while(n > 1) {
                n--;
                var k = Random.Range(0, n + 1);
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
