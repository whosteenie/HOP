using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Player;
using Unity.Netcode;
using UnityEngine;

namespace Network.Singletons {
    /// <summary>
    /// Manages hopball spawning, respawning, OOB handling, and scoring for Hopball gamemode.
    /// </summary>
    public class HopballSpawnManager : NetworkBehaviour {
        public static HopballSpawnManager Instance { get; private set; }

        [Header("Hopball Spawn Points")]
        [SerializeField] private List<HopballSpawnPoint> hopballSpawnPoints = new();

        [Header("Hopball Prefab")]
        [SerializeField] private GameObject hopballPrefab;

        [Header("Settings")]
        [SerializeField] private float initialSpawnDelay = 5f; // Spawn after 5 seconds into match
        [SerializeField] private float respawnDelay = 5f; // Wait 5 seconds after ball is destroyed before respawning
        [SerializeField] private float oobCheckInterval = 0.5f; // How often to check for OOB
        [SerializeField] private float oobThreshold = 600f; // Y position threshold for OOB (map is high in the air)
        [SerializeField] private int winScore = 60; // Points needed to win

        // Team scores (server-authoritative)
        private readonly NetworkVariable<int> _teamAScore = new(value: 0, 
            NetworkVariableReadPermission.Everyone, 
            NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _teamBScore = new(value: 0, 
            NetworkVariableReadPermission.Everyone, 
            NetworkVariableWritePermission.Server);

        private Hopball _currentHopball;
        private HopballSpawnPoint _mostRecentSpawnPoint; // The most recent spawn point (for OOB respawn)
        private bool _isSpawning;
        private bool _hasSpawnedInitial;
        private ulong _currentHolderId; // Track who is currently holding the ball
        private float _energyAtPickup; // Track energy when current holder picked it up

        public Hopball CurrentHopball => _currentHopball;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            if(IsServer) {
                // Reset scores
                _teamAScore.Value = 0;
                _teamBScore.Value = 0;

                // Check if we're in Hopball mode
                var matchSettings = MatchSettingsManager.Instance;
                if(matchSettings != null && matchSettings.selectedGameModeId == "Hopball") {
                    StartCoroutine(InitialSpawnCoroutine());
                }
            }

            // Subscribe to score changes for UI updates
            _teamAScore.OnValueChanged += OnTeamAScoreChanged;
            _teamBScore.OnValueChanged += OnTeamBScoreChanged;
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            _teamAScore.OnValueChanged -= OnTeamAScoreChanged;
            _teamBScore.OnValueChanged -= OnTeamBScoreChanged;
        }

        private void OnTeamAScoreChanged(int previous, int current) {
            // Update UI if needed
            if(ScoreboardManager.Instance != null) {
                ScoreboardManager.Instance.UpdateScoreboard();
            }
        }

        private void OnTeamBScoreChanged(int previous, int current) {
            // Update UI if needed
            if(ScoreboardManager.Instance != null) {
                ScoreboardManager.Instance.UpdateScoreboard();
            }
        }

        public int GetTeamAScore() => _teamAScore.Value;
        public int GetTeamBScore() => _teamBScore.Value;

        /// <summary>
        /// Spawns the first hopball after initial delay.
        /// </summary>
        private IEnumerator InitialSpawnCoroutine() {
            yield return new WaitForSeconds(initialSpawnDelay);

            if(IsServer && !_hasSpawnedInitial) {
                SpawnHopball();
            }
        }

        /// <summary>
        /// Spawns a hopball at a random spawn point.
        /// </summary>
        public void SpawnHopball() {
            Debug.Log($"[HopballSpawnManager] SpawnHopball called. IsServer: {IsServer}, _isSpawning: {_isSpawning}, prefab: {hopballPrefab != null}, currentHopball: {(_currentHopball != null ? "exists" : "null")}");
            
            if(!IsServer || _isSpawning || hopballPrefab == null) {
                Debug.Log($"[HopballSpawnManager] SpawnHopball: Cannot spawn (IsServer: {IsServer}, _isSpawning: {_isSpawning}, prefab: {hopballPrefab != null})");
                return;
            }
            if(_currentHopball != null && _currentHopball.IsSpawned) {
                Debug.Log($"[HopballSpawnManager] SpawnHopball: Hopball already exists and is spawned, skipping");
                return; // Don't spawn if one already exists
            }

            if(hopballSpawnPoints == null || hopballSpawnPoints.Count == 0) {
                Debug.LogError("[HopballSpawnManager] No hopball spawn points assigned!");
                return;
            }

            _isSpawning = true;

            // Choose random spawn point
            var validPoints = hopballSpawnPoints.Where(p => p != null).ToList();
            if(validPoints.Count == 0) {
                Debug.LogError("[HopballSpawnManager] No valid spawn points!");
                _isSpawning = false;
                return;
            }

            var spawnPoint = validPoints[Random.Range(0, validPoints.Count)];
            _mostRecentSpawnPoint = spawnPoint;

            Debug.Log($"[HopballSpawnManager] SpawnHopball: Instantiating hopball at spawn point {spawnPoint.transform.position}");
            
            // Spawn hopball
            var instance = Instantiate(hopballPrefab, spawnPoint.transform.position, spawnPoint.transform.rotation);
            var networkObject = instance.GetComponent<NetworkObject>();
            
            // Ensure the hopball is active and visible
            instance.SetActive(true);
            
            if(networkObject != null) {
                Debug.Log($"[HopballSpawnManager] SpawnHopball: Spawning NetworkObject at {spawnPoint.transform.position}");
                networkObject.Spawn();
            } else {
                Debug.LogError("[HopballSpawnManager] Hopball prefab missing NetworkObject component!");
                _isSpawning = false;
                return;
            }

            // Get the Hopball component and assign to _currentHopball
            _currentHopball = instance.GetComponent<Hopball>();
            if(_currentHopball == null) {
                Debug.LogError("[HopballSpawnManager] Hopball prefab missing Hopball component!");
                _isSpawning = false;
                return;
            }

            _hasSpawnedInitial = true;
            _isSpawning = false;

            // Play spawn sound at spawn location (directional, same falloff as gunshots)
            PlayHopballSpawnSoundClientRpc(spawnPoint.transform.position);

            Debug.Log($"[HopballSpawnManager] SpawnHopball: Successfully spawned hopball at {spawnPoint.transform.position}. Energy: {_currentHopball.Energy}");
        }

        /// <summary>
        /// Respawns the hopball at a new random location with full energy.
        /// Called by Hopball when dissolve completes.
        /// </summary>
        public void RespawnHopballAtNewLocation() {
            if(!IsServer || _currentHopball == null) {
                Debug.LogWarning("[HopballSpawnManager] RespawnHopballAtNewLocation: Cannot respawn (not server or no hopball)");
                return;
            }

            // Choose new random spawn point
            if(hopballSpawnPoints == null || hopballSpawnPoints.Count == 0) {
                Debug.LogError("[HopballSpawnManager] No hopball spawn points assigned!");
                return;
            }

            var validPoints = hopballSpawnPoints.Where(p => p != null).ToList();
            if(validPoints.Count == 0) {
                Debug.LogError("[HopballSpawnManager] No valid spawn points!");
                return;
            }

            var spawnPoint = validPoints[Random.Range(0, validPoints.Count)];
            _mostRecentSpawnPoint = spawnPoint;

            // Respawn the existing ball at new location with full energy
            _currentHopball.RespawnAtLocation(spawnPoint.transform.position, spawnPoint.transform.rotation);
            
            // Clear holder tracking
            _currentHolderId = 0;
            _energyAtPickup = 0f;

            // Play spawn sound at respawn location (directional, same falloff as gunshots)
            PlayHopballSpawnSoundClientRpc(spawnPoint.transform.position);

            Debug.Log($"[HopballSpawnManager] RespawnHopballAtNewLocation: Respawned at {spawnPoint.transform.position}");
        }

        /// <summary>
        /// Checks if hopball is OOB and teleports it back if needed.
        /// </summary>
        private void Update() {
            if(!IsServer) return;

            if(_currentHopball != null && _currentHopball.IsSpawned && _mostRecentSpawnPoint != null) {
                // Check if hopball is dropped (not equipped) and OOB
                if(!_currentHopball.IsEquipped && _currentHopball.transform.position.y <= oobThreshold) {
                    TeleportHopballToMostRecentSpawn();
                }
            }
        }

        /// <summary>
        /// Teleports hopball back to its most recent spawn point and makes it kinematic.
        /// Retains current energy.
        /// </summary>
        private void TeleportHopballToMostRecentSpawn() {
            if(_currentHopball == null || _mostRecentSpawnPoint == null) return;

            // Reposition at most recent spawn point (retains energy)
            _currentHopball.RepositionAtLocation(_mostRecentSpawnPoint.transform.position, _mostRecentSpawnPoint.transform.rotation);

            // Play spawn sound at reposition location (directional, same falloff as gunshots)
            PlayHopballSpawnSoundClientRpc(_mostRecentSpawnPoint.transform.position);

            Debug.Log($"[HopballSpawnManager] Teleported hopball back to most recent spawn point (OOB), energy retained: {_currentHopball.Energy}");
        }

        /// <summary>
        /// Called by HopballController when player picks up ball. Tracks energy for scoring.
        /// </summary>
        public void OnPlayerPickedUpHopball(ulong playerId) {
            if(!IsServer || _currentHopball == null) return;

            // Track who picked it up and at what energy
            _currentHolderId = playerId;
            _energyAtPickup = _currentHopball.Energy;
            
            Debug.Log($"[HopballSpawnManager] OnPlayerPickedUpHopball: Player {playerId} picked up ball at energy {_energyAtPickup}");
        }

        /// <summary>
        /// Called when hopball is dropped. Clears holder tracking.
        /// </summary>
        public void OnHopballDropped() {
            if(!IsServer) return;
            
            _currentHolderId = 0;
            _energyAtPickup = 0f;
        }

        /// <summary>
        /// Called by Hopball when energy depletes. Awards 1 point per 1 energy depleted.
        /// </summary>
        public void OnEnergyDepleted(ulong playerId, float energyDepleted) {
            if(!IsServer) return;
            
            // Only award points if this player is still holding the ball
            if(_currentHolderId != playerId || _currentHopball == null || !_currentHopball.IsEquipped) {
                return;
            }

            // Get player's team
            var player = GetPlayerById(playerId);
            if(player == null) return;

            var teamManager = player.GetComponent<PlayerTeamManager>();
            if(teamManager == null) return;

            var team = teamManager.netTeam.Value;
            
            // Award points equal to energy depleted (1 point per 1 energy)
            var pointsToAward = Mathf.RoundToInt(energyDepleted);
            for(int i = 0; i < pointsToAward; i++) {
                AwardPointToTeam(team);
            }
            
            Debug.Log($"[HopballSpawnManager] OnEnergyDepleted: Awarded {pointsToAward} points to {team} (energy depleted: {energyDepleted})");
        }

        /// <summary>
        /// Awards a point to the specified team and checks for win condition.
        /// </summary>
        private void AwardPointToTeam(SpawnPoint.Team team) {
            if(!IsServer) return;

            // Award point to team
            if(team == SpawnPoint.Team.TeamA) {
                _teamAScore.Value++;
                Debug.Log($"[HopballSpawnManager] Team A scored! Score: {_teamAScore.Value} - {_teamBScore.Value}");
            } else {
                _teamBScore.Value++;
                Debug.Log($"[HopballSpawnManager] Team B scored! Score: {_teamAScore.Value} - {_teamBScore.Value}");
            }

            // Check win condition (60 points)
            if(_teamAScore.Value >= winScore) {
                Debug.Log("[HopballSpawnManager] Team A wins!");
                TriggerWinCondition(SpawnPoint.Team.TeamA);
            } else if(_teamBScore.Value >= winScore) {
                Debug.Log("[HopballSpawnManager] Team B wins!");
                TriggerWinCondition(SpawnPoint.Team.TeamB);
            }
        }

        /// <summary>
        /// Triggers the win condition and ends the match.
        /// </summary>
        private void TriggerWinCondition(SpawnPoint.Team winningTeam) {
            if(!IsServer) return;

            // Trigger post-match flow
            if(PostMatchManager.Instance != null) {
                PostMatchManager.Instance.BeginPostMatchFromScore(winningTeam);
            }
        }

        private PlayerController GetPlayerById(ulong playerId) {
            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(playerId, out var client)) {
                return client.PlayerObject?.GetComponent<PlayerController>();
            }
            return null;
        }

        /// <summary>
        /// Plays hopball spawn sound at the specified position (directional, same falloff as gunshots).
        /// Called via RPC to play on all clients.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        private void PlayHopballSpawnSoundClientRpc(Vector3 position) {
            if(SoundFXManager.Instance != null) {
                // Play sound at position (not attached to any object) with directional falloff
                SoundFXManager.Instance.PlayKey(SfxKey.HopballSpawn, null, position, allowOverlap: false);
            }
        }

        /// <summary>
        /// Editor helper: Find all HopballSpawnPoints in scene.
        /// </summary>
        [ContextMenu("Find All Hopball Spawn Points in Scene")]
        private void FindAllSpawnPointsInScene() {
            hopballSpawnPoints = FindObjectsByType<HopballSpawnPoint>(FindObjectsSortMode.None).ToList();
            Debug.Log($"[HopballSpawnManager] Found {hopballSpawnPoints.Count} hopball spawn points");
        }
    }
}

