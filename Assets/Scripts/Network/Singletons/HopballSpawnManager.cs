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
        [SerializeField] private float postPrematchSpawnDelay = 5f; // Spawn this many seconds after pre-match countdown ends
        [SerializeField] private float oobThreshold = 600f; // Y position threshold for OOB (map is high in the air)
        [SerializeField] private int winScore = 60; // Points needed to win
        [SerializeField] private float dissolveRespawnDelay = 5f; // Delay before respawning after dissolve

        // Team scores (server-authoritative)
        private readonly NetworkVariable<int> _teamAScore = new(value: 0);
        private readonly NetworkVariable<int> _teamBScore = new(value: 0);

        private HopballSpawnPoint _mostRecentSpawnPoint; // The most recent spawn point (for OOB respawn)
        private bool _isSpawning;
        private bool _hasSpawnedInitial;
        private ulong _currentHolderId; // Track who is currently holding the ball
        private Coroutine _respawnCoroutine;

        public Hopball CurrentHopball { get; private set; }

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

        private static void OnTeamAScoreChanged(int previous, int current) {
            // Update UI if needed
            if(ScoreboardManager.Instance != null) {
                ScoreboardManager.Instance.UpdateScoreboard();
            }
        }

        private static void OnTeamBScoreChanged(int previous, int current) {
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
            var matchSettings = MatchSettingsManager.Instance;
            var preMatchCountdown = matchSettings?.GetPreMatchCountdownSeconds() ?? 5f;
            yield return new WaitForSeconds(preMatchCountdown + postPrematchSpawnDelay);

            if(IsServer && !_hasSpawnedInitial) {
                SpawnHopball();
            }
        }

        /// <summary>
        /// Spawns a hopball at a random spawn point.
        /// </summary>
        private void SpawnHopball() {
            if(!IsServer || _isSpawning || hopballPrefab == null) {
                return;
            }

            if(CurrentHopball != null && CurrentHopball.IsSpawned) {
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

            // Spawn hopball
            var instance = Instantiate(hopballPrefab, spawnPoint.transform.position, spawnPoint.transform.rotation);
            var networkObject = instance.GetComponent<NetworkObject>();

            // Ensure the hopball is active and visible
            instance.SetActive(true);

            if(networkObject != null) {
                networkObject.Spawn();
            } else {
                Debug.LogError("[HopballSpawnManager] Hopball prefab missing NetworkObject component!");
                _isSpawning = false;
                return;
            }

            // Get the Hopball component and assign to _currentHopball
            CurrentHopball = instance.GetComponent<Hopball>();
            if(CurrentHopball == null) {
                Debug.LogError("[HopballSpawnManager] Hopball prefab missing Hopball component!");
                _isSpawning = false;
                return;
            }

            _hasSpawnedInitial = true;
            _isSpawning = false;

            // Play spawn sound at spawn location (directional, same falloff as gunshots)
            PlayHopballSpawnSoundClientRpc(spawnPoint.transform.position);
        }

        /// <summary>
        /// Respawns the hopball at a new random location with full energy.
        /// Called by Hopball when dissolve completes.
        /// </summary>
        public void RespawnHopballAtNewLocation() {
            if(!IsServer || CurrentHopball == null) {
                Debug.LogWarning(
                    "[HopballSpawnManager] RespawnHopballAtNewLocation: Cannot respawn (not server or no hopball)");
                return;
            }

            if(_respawnCoroutine != null) {
                StopCoroutine(_respawnCoroutine);
            }

            CurrentHopball.PrepareForRespawnDelay();
            _respawnCoroutine = StartCoroutine(RespawnAfterDelay());
        }

        private IEnumerator RespawnAfterDelay() {
            yield return new WaitForSeconds(Mathf.Max(0f, dissolveRespawnDelay));

            if(!IsServer || CurrentHopball == null) {
                _respawnCoroutine = null;
                yield break;
            }

            if(hopballSpawnPoints == null || hopballSpawnPoints.Count == 0) {
                Debug.LogError("[HopballSpawnManager] No hopball spawn points assigned!");
                _respawnCoroutine = null;
                yield break;
            }

            var validPoints = hopballSpawnPoints.Where(p => p != null).ToList();
            if(validPoints.Count == 0) {
                Debug.LogError("[HopballSpawnManager] No valid spawn points!");
                _respawnCoroutine = null;
                yield break;
            }

            var spawnPoint = validPoints[Random.Range(0, validPoints.Count)];
            _mostRecentSpawnPoint = spawnPoint;

            CurrentHopball.RespawnAtLocation(spawnPoint.transform.position, spawnPoint.transform.rotation);

            _currentHolderId = 0;

            PlayHopballSpawnSoundClientRpc(spawnPoint.transform.position);

            _respawnCoroutine = null;
        }

        /// <summary>
        /// Checks if hopball is OOB and teleports it back if needed.
        /// </summary>
        private void Update() {
            if(!IsServer) return;

            if(CurrentHopball == null || !CurrentHopball.IsSpawned || _mostRecentSpawnPoint == null) return;
            // Check if hopball is dropped (not equipped) and OOB
            if(!CurrentHopball.IsEquipped && CurrentHopball.transform.position.y <= oobThreshold) {
                TeleportHopballToMostRecentSpawn();
            }
        }

        /// <summary>
        /// Teleports hopball back to its most recent spawn point and makes it kinematic.
        /// Retains current energy.
        /// </summary>
        private void TeleportHopballToMostRecentSpawn() {
            if(CurrentHopball == null || _mostRecentSpawnPoint == null) return;

            // Reposition at most recent spawn point (retains energy)
            CurrentHopball.RepositionAtLocation(_mostRecentSpawnPoint.transform.position,
                _mostRecentSpawnPoint.transform.rotation);

            // Play spawn sound at reposition location (directional, same falloff as gunshots)
            PlayHopballSpawnSoundClientRpc(_mostRecentSpawnPoint.transform.position);
        }

        /// <summary>
        /// Called by HopballController when player picks up ball. Tracks energy for scoring.
        /// </summary>
        public void OnPlayerPickedUpHopball(ulong playerId) {
            if(!IsServer || CurrentHopball == null) return;

            // Track who picked it up and at what energy
            _currentHolderId = playerId;
        }

        /// <summary>
        /// Called when hopball is dropped. Clears holder tracking.
        /// </summary>
        public void OnHopballDropped() {
            if(!IsServer) return;

            _currentHolderId = 0;
        }

        /// <summary>
        /// Called by Hopball when energy depletes. Awards 1 point per 1 energy depleted.
        /// </summary>
        public void OnEnergyDepleted(ulong playerId, float energyDepleted) {
            if(!IsServer) return;

            // Only award points if this player is still holding the ball
            if(_currentHolderId != playerId || CurrentHopball == null || !CurrentHopball.IsEquipped) {
                return;
            }

            // Get player's team
            var player = GetPlayerById(playerId);

            var teamManager = player?.TeamManager;
            if(teamManager == null) return;

            var team = teamManager.netTeam.Value;

            // Award points equal to energy depleted (1 point per 1 energy)
            var pointsToAward = Mathf.RoundToInt(energyDepleted);
            for(var i = 0; i < pointsToAward; i++) {
                AwardPointToTeam(team);
            }
        }

        /// <summary>
        /// Awards a point to the specified team and checks for win condition.
        /// </summary>
        private void AwardPointToTeam(SpawnPoint.Team team) {
            if(!IsServer) return;

            // Award point to team
            if(team == SpawnPoint.Team.TeamA) {
                _teamAScore.Value++;
            } else {
                _teamBScore.Value++;
            }

            // Check win condition (60 points)
            if(_teamAScore.Value >= winScore) {
                TriggerWinCondition(SpawnPoint.Team.TeamA);
            } else if(_teamBScore.Value >= winScore) {
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

        private static PlayerController GetPlayerById(ulong playerId) {
            return NetworkManager.Singleton.ConnectedClients.TryGetValue(playerId, out var client) ? client.PlayerObject?.GetComponent<PlayerController>() : null;
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
        }
    }
}