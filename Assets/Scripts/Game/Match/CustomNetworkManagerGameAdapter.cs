using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Diagnostics;
using Game.Player.Core;
using Game.Spawning;
using Network.Core;
using Network.Session;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Match {
    /// <summary>
    /// Wires game-specific spawn, team assignment, and connection metadata logic into
    /// Network.Core.CustomNetworkManager via hooks so that the network stack stays
    /// free of direct Game.* dependencies.
    /// </summary>
    public sealed class CustomNetworkManagerGameAdapter : MonoBehaviour {
        [Header("Team Settings")]
        [SerializeField] private bool autoBalanceTeams = true;

        // Track pending team assignments during initial batch spawn
        private readonly Dictionary<ulong, SpawnPoint.Team> _pendingTeamAssignments = new();

        // Connection payload-derived metadata (transport agnostic)
        private readonly Dictionary<ulong, string> _clientPartyIds = new();
        private readonly Dictionary<ulong, ulong> _clientSteamIds = new();
        private readonly Dictionary<ulong, string> _clientUgsPlayerIds = new();
        private bool _hasSessionPrivateFlag;
        private bool _sessionIsPrivateMatch;

        // Cached array for spawn point validation (non-allocating overlap check)
        private readonly Collider[] _spawnValidationHits = new Collider[10];

        private void Awake() {
            CustomNetworkManager.SetGameHooks(
                OnClientApproved,
                PrepareBatchSpawns,
                SpawnPlayerForClient);
        }

        // ===== Approval / metadata =====

        private void OnClientApproved(ulong clientId, ConnectionPayload payload) {
            if(payload == null) return;

            if(!string.IsNullOrEmpty(payload.partyId)) {
                _clientPartyIds[clientId] = payload.partyId;
            }

            if(payload.steamId != 0) {
                _clientSteamIds[clientId] = payload.steamId;
            }

            if(!string.IsNullOrWhiteSpace(payload.ugsPlayerId)) {
                _clientUgsPlayerIds[clientId] = payload.ugsPlayerId;
                TryRefreshClientMetadataFromDaSession(clientId);
            }

            if(_hasSessionPrivateFlag) return;
            _sessionIsPrivateMatch = payload.isPrivateMatch;
            _hasSessionPrivateFlag = true;
        }

        // ===== Batch preparation (teams, metadata refresh) =====

        private void PrepareBatchSpawns(List<ulong> clients) {
            _pendingTeamAssignments.Clear();

            foreach(var clientId in clients) {
                TryRefreshClientMetadataFromDaSession(clientId);
            }

            // 1. Group clients by PartyID (from connection payload).
            var partyGroups = new Dictionary<string, List<ulong>>();
            var solos = new List<ulong>();

            foreach(var clientId in clients) {
                var pId = "";
                if(_clientPartyIds.TryGetValue(clientId, out var storedPartyId)) {
                    pId = storedPartyId;
                }

                if(string.IsNullOrEmpty(pId)) {
                    solos.Add(clientId);
                } else {
                    if(!partyGroups.ContainsKey(pId)) partyGroups[pId] = new List<ulong>();
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
                Debug.Log(
                    $"[CustomNetworkManagerGameAdapter] Applied private match draft team assignments for {_pendingTeamAssignments.Count} players.");
                return;
            }

            // Check for single large party (Private Match scenario without draft)
            if(_sessionIsPrivateMatch ||
               (partyGroups.Count == 1 && solos.Count == 0 && partyGroups.First().Value.Count > 1)) {
                // Split logic
                var allClients = new List<ulong>(clients);
                ShuffleList(allClients); // Randomize first

                for(var i = 0; i < allClients.Count; i++) {
                    var team = i % 2 == 0 ? SpawnPoint.Team.TeamA : SpawnPoint.Team.TeamB;
                    _pendingTeamAssignments[allClients[i]] = team;
                }

                Debug.Log(
                    $"[CustomNetworkManagerGameAdapter] Distributed Private Match/Single Party of {clients.Count} players.");
                return;
            }

            // Public / Multiple Parties Logic
            var sortedParties = partyGroups.Values.OrderByDescending(p => p.Count).ToList();

            var teamAMembers = new List<ulong>();
            var teamBMembers = new List<ulong>();

            foreach(var party in sortedParties) {
                // Assign entire party to the smaller team
                if(teamAMembers.Count <= teamBMembers.Count) {
                    teamAMembers.AddRange(party);
                    foreach(var id in party) _pendingTeamAssignments[id] = SpawnPoint.Team.TeamA;
                } else {
                    teamBMembers.AddRange(party);
                    foreach(var id in party) _pendingTeamAssignments[id] = SpawnPoint.Team.TeamB;
                }
            }

            // Distribute solos to balance remaining
            ShuffleList(solos);
            foreach(var soloId in solos) {
                if(teamAMembers.Count <= teamBMembers.Count) {
                    teamAMembers.Add(soloId);
                    _pendingTeamAssignments[soloId] = SpawnPoint.Team.TeamA;
                } else {
                    teamBMembers.Add(soloId);
                    _pendingTeamAssignments[soloId] = SpawnPoint.Team.TeamB;
                }
            }

            Debug.Log(
                $"[CustomNetworkManagerGameAdapter] Distributed Teams (Public): TeamA={teamAMembers.Count}, TeamB={teamBMembers.Count}");
        }

        // ===== Spawn logic (per-client) =====

        private NetworkObject SpawnPlayerForClient(ulong clientId, NetworkObject playerPrefab) {
            const int maxSpawnAttempts = 8;

            var activeScene = SceneManager.GetActiveScene();

            if(playerPrefab == null) {
                Debug.LogError("[CustomNetworkManagerGameAdapter] Player prefab is not assigned. Cannot spawn players.");
                return null;
            }

            var matchSettings = MatchSettingsManager.Instance;
            var isTeamBased = matchSettings != null &&
                              MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId);

            for(var attempt = 0; attempt < maxSpawnAttempts; attempt++) {
                // 1. Assign team first (if team-based) so we can use it for spawn point selection
                var assignedTeam = SpawnPoint.Team.TeamA;
                if(isTeamBased) {
                    assignedTeam = AssignTeam(clientId);
                }

                // 2. Choose spawn point
                var spawnManager = SpawnManager.Instance;
                if(spawnManager == null) {
                    Debug.LogError("[CustomNetworkManagerGameAdapter] SpawnManager unavailable during player spawn.");
                    return null;
                }

                var spawnPoint =
                    isTeamBased ? spawnManager.GetNextSpawnPoint(assignedTeam) :
                    spawnManager.GetNextSpawnPoint();

                if(spawnPoint == null) {
                    Debug.LogError(
                        $"[CustomNetworkManagerGameAdapter] No spawn points available in scene '{activeScene.name}'.");
                    return null;
                }

                var spawnPointTransform = spawnPoint.transform;
                var pos = spawnPointTransform.position;
                var rot = spawnPointTransform.rotation;

                // 3. Validate spawn point (optional safety)
                var layerMask = LayerMask.GetMask("Player", "Enemy");
                var hitCount = Physics.OverlapSphereNonAlloc(pos, 0.5f, _spawnValidationHits, layerMask);
                if(hitCount > 0) {
                    Debug.LogWarning("[CustomNetworkManagerGameAdapter] Spawn point occupied, retrying...");
                    continue;
                }

                // 4. Instantiate player
                var instance = Instantiate(playerPrefab, pos, rot);
                var cc = instance.GetComponent<CharacterController>();
                if(cc) cc.enabled = false;

                instance.name = $"Player_{clientId}_{(isTeamBased ? $"Team{assignedTeam}" : "FFA")}";

                // Disable PlayerInput immediately to prevent control scheme assignment errors
                var playerInput = instance.GetComponent<UnityEngine.InputSystem.PlayerInput>();
                if(playerInput != null) {
                    playerInput.enabled = false;
                }

                // 5. Spawn as player object
                instance.SpawnAsPlayerObject(clientId);

                FlowLog.Emit(FlowEventIds.PlayerSpawned,
                    ("clientId", clientId),
                    ("team", isTeamBased ? assignedTeam.ToString() : "None"),
                    ("mode", matchSettings != null ? matchSettings.selectedGameModeId : "Unknown"),
                    ("spawn", spawnPoint.name));

                // 6. TEAM SETUP (only for team modes)
                if(isTeamBased && NetworkAuthority.HasGlobalAuthority(NetworkManager.Singleton)) {
                    var controller = instance.GetComponent<PlayerController>();
                    PlayerTeamManager teamMgr = null;
                    if(controller != null) {
                        teamMgr = controller.TeamManager;
                    }

                    if(teamMgr != null) {
                        teamMgr.netTeam.Value = assignedTeam;
                        _pendingTeamAssignments[clientId] = assignedTeam;
                    }
                }

                // 7. Re-enable CharacterController next frame
                StartCoroutine(EnableCcNextFrame(cc));

                var spawnedController = instance.GetComponent<PlayerController>();
                if(spawnedController != null) {
                    StartCoroutine(CaptureSpawnedPlayerMetadata(clientId, spawnedController));
                }

                return instance;
            }

            Debug.LogWarning(
                $"[CustomNetworkManagerGameAdapter] Could not find a free spawn point after {maxSpawnAttempts} attempts for client {clientId}.");
            return null;
        }

        // ===== Team helpers =====

        private SpawnPoint.Team AssignTeam(ulong clientId = 0) {
            // If already assigned via pre-calculated batch, return that
            if(_pendingTeamAssignments.TryGetValue(clientId, out var assigned)) {
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
                if(team == SpawnPoint.Team.TeamA) countA++;
                else countB++;
            }

            if(countA == countB) return Random.Range(0, 2) == 0 ? SpawnPoint.Team.TeamA : SpawnPoint.Team.TeamB;
            return countA < countB ? SpawnPoint.Team.TeamA : SpawnPoint.Team.TeamB;
        }

        // ===== DA metadata resolution =====

        private void TryRefreshClientMetadataFromDaSession(ulong clientId) {
            if(!_clientUgsPlayerIds.TryGetValue(clientId, out var ugsPlayerId) || string.IsNullOrWhiteSpace(ugsPlayerId)) {
                return;
            }

            var sessionManager = SessionManager.Instance;
            if(sessionManager == null ||
               !sessionManager.TryResolveDaPlayerMetadata(ugsPlayerId, out var partyId, out var steamId)) {
                return;
            }

            if(!string.IsNullOrWhiteSpace(partyId)) {
                _clientPartyIds[clientId] = partyId;
            }

            if(steamId != 0) {
                _clientSteamIds[clientId] = steamId;
            }
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
                if(steamId != 0 && _clientSteamIds.TryAdd(clientId, steamId)) {
                    updated = true;
                }

                if(updated) {
                    TryRefreshClientMetadataFromDaSession(clientId);
                }

                var hasResolvedUgs = _clientUgsPlayerIds.ContainsKey(clientId);
                var hasResolvedSteam = _clientSteamIds.ContainsKey(clientId);
                if(hasResolvedUgs && hasResolvedSteam) {
                    yield break;
                }

                yield return null;
            }
        }

        // ===== Utilities =====

        private static void ShuffleList<T>(List<T> list) {
            var n = list.Count;
            while(n > 1) {
                n--;
                var k = Random.Range(0, n + 1);
                (list[k], list[n]) = (list[n], list[k]);
            }
        }

        private static IEnumerator EnableCcNextFrame(CharacterController cc) {
            yield return null;
            if(cc) cc.enabled = true;
        }
    }
}

