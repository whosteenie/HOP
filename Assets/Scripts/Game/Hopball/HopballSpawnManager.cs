using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Player;
using Game.Match;
using Game.Player.Hopball;
using Game.Spawning;
using Network.Diagnostics;
using Network.Events;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Hopball {
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
        [SerializeField] private float oobThreshold = 600f; // Fallback Y threshold if no OOB marker is found
        [SerializeField] private int winScore = 60; // Points needed to win
        [SerializeField] private float dissolveRespawnDelay = 5f; // Delay before respawning after dissolve
        [Header("Out Of Bounds")]
        [SerializeField] private string outOfBoundsMarkerName = "OOB";
        [SerializeField] private string outOfBoundsMarkerTag = "OOB";

        // Team scores (server-authoritative)
        private readonly NetworkVariable<int> _teamAScore = new(value: 0);
        private readonly NetworkVariable<int> _teamBScore = new(value: 0);

        private HopballSpawnPoint _mostRecentSpawnPoint; // The most recent spawn point (for OOB respawn)
        private bool _isSpawning;
        private bool _hasSpawnedInitial;
        private ulong _currentHolderId; // Track who is currently holding the ball
        private Coroutine _respawnCoroutine;
        private int _cachedOobSceneHandle = -1;
        private float _cachedOutOfBoundsY;
        private bool _cachedUseYLevelOutOfBoundsKill = true;

        public HopballController CurrentHopballController { get; private set; }

        private int EffectiveWinScore =>
            MatchSettingsManager.Instance != null ? MatchSettingsManager.Instance.GetScoreToWin() : winScore;

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
                } else {
                    if(CurrentHopballController != null) {
                        FlowLog.Emit(FlowEventIds.AnomalyModeMismatch,
                            ("selected", matchSettings != null ? matchSettings.selectedGameModeId : "Unknown"),
                            ("applied", matchSettings != null ? matchSettings.selectedGameModeId : "Unknown"),
                            ("objective", "Hopball"));
                    }
                    CleanupActiveHopball();
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
            CleanupActiveHopball();
        }

        private static void OnTeamAScoreChanged(int previous, int current) {
            EventBus.Publish(new ScoreboardRefreshRequestedEvent());
        }

        private static void OnTeamBScoreChanged(int previous, int current) {
            EventBus.Publish(new ScoreboardRefreshRequestedEvent());
        }

        public int GetTeamAScore() => _teamAScore.Value;
        public int GetTeamBScore() => _teamBScore.Value;

        /// <summary>
        /// Spawns the first hopball after initial delay.
        /// </summary>
        private IEnumerator InitialSpawnCoroutine() {
            var matchSettings = MatchSettingsManager.Instance;
            var preMatchCountdown = 5f;
            if(matchSettings != null) {
                preMatchCountdown = matchSettings.IsPreMatchCountdownEnabled()
                    ? matchSettings.GetPreMatchCountdownSeconds()
                    : 0f;
            }
            yield return new WaitForSeconds(preMatchCountdown + postPrematchSpawnDelay);

            if(!IsServer || _hasSpawnedInitial) yield break;
            matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || matchSettings.selectedGameModeId != "Hopball") {
                yield break;
            }
            SpawnHopball();
        }

        /// <summary>
        /// Spawns a hopball at a random spawn point.
        /// </summary>
        private void SpawnHopball() {
            if(!IsServer || _isSpawning || hopballPrefab == null) {
                return;
            }

            // Don't spawn if post-match has started
            if(PostMatchManager.Instance != null && PostMatchManager.Instance.PostMatchFlowStarted) {
                return;
            }

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || matchSettings.selectedGameModeId != "Hopball") {
                return;
            }

            if(CurrentHopballController != null && CurrentHopballController.IsSpawned) {
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
            var spawnPointTransform = spawnPoint.transform;
            var instance = Instantiate(hopballPrefab, spawnPointTransform.position, spawnPointTransform.rotation);
            var networkObject = instance.GetComponent<NetworkObject>();

            // Ensure the hopball is active and visible
            instance.SetActive(true);

            if(networkObject != null) {
                networkObject.Spawn(true);
            } else {
                Debug.LogError("[HopballSpawnManager] Hopball prefab missing NetworkObject component!");
                _isSpawning = false;
                return;
            }

            // Get the Hopball component and assign to _currentHopball
            CurrentHopballController = instance.GetComponent<HopballController>();
            if(CurrentHopballController == null) {
                Debug.LogError("[HopballSpawnManager] Hopball prefab missing Hopball component!");
                _isSpawning = false;
                return;
            }
            var objectiveSpawnType = _hasSpawnedInitial ? "Respawn" : "Initial";
            FlowLog.Emit(FlowEventIds.ObjectiveSpawned,
                ("mode", "Hopball"),
                ("objectType", "Hopball"),
                ("spawnType", objectiveSpawnType),
                ("spawnPoint", spawnPoint.name));

            _hasSpawnedInitial = true;
            _isSpawning = false;

            PrewarmHopballVisualPoolsClientRpc();

            // Play spawn sound at spawn location (directional, same falloff as gunshots)
            PlayHopballSpawnSoundClientRpc(spawnPoint.transform.position);
        }

        /// <summary>
        /// Respawns the hopball at a new random location with full energy.
        /// Called by Hopball when dissolve completes.
        /// </summary>
        public void RespawnHopballAtNewLocation() {
            if(!IsServer || CurrentHopballController == null) {
                Debug.LogWarning(
                    "[HopballSpawnManager] RespawnHopballAtNewLocation: Cannot respawn (not server or no hopball)");
                return;
            }

            // Don't respawn if post-match has started
            if(PostMatchManager.Instance != null && PostMatchManager.Instance.PostMatchFlowStarted) {
                return;
            }

            if(_respawnCoroutine != null) {
                StopCoroutine(_respawnCoroutine);
            }

            CurrentHopballController.PrepareForRespawnDelay();
            _respawnCoroutine = StartCoroutine(RespawnAfterDelay());
        }

        private IEnumerator RespawnAfterDelay() {
            yield return new WaitForSeconds(Mathf.Max(0f, dissolveRespawnDelay));

            if(!IsServer || CurrentHopballController == null) {
                _respawnCoroutine = null;
                yield break;
            }

            // Don't respawn if post-match has started (check again after delay)
            if(PostMatchManager.Instance != null && PostMatchManager.Instance.PostMatchFlowStarted) {
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

            var spawnPointTransform = spawnPoint.transform;
            CurrentHopballController.RespawnAtLocation(spawnPointTransform.position, spawnPointTransform.rotation);
            FlowLog.Emit(FlowEventIds.HopballRespawnExecuted,
                ("hopballNetId", CurrentHopballController.NetworkObjectId),
                ("spawnPoint", spawnPoint.name),
                ("reason", "DissolveRespawn"));

            _currentHolderId = 0;

            PrewarmHopballVisualPoolsClientRpc();

            PlayHopballSpawnSoundClientRpc(spawnPoint.transform.position);

            _respawnCoroutine = null;
        }

        /// <summary>
        /// Checks if hopball is OOB and teleports it back if needed.
        /// </summary>
        private void Update() {
            if(!IsServer) return;

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || matchSettings.selectedGameModeId != "Hopball") {
                CleanupActiveHopball();
                return;
            }

            if(CurrentHopballController == null || !CurrentHopballController.IsSpawned || _mostRecentSpawnPoint == null) return;
            if(!IsYLevelOutOfBoundsKillEnabled()) return;
            var outOfBoundsY = GetOutOfBoundsKillY();
            // Check if hopball is dropped (not equipped) and OOB
            if(!CurrentHopballController.IsEquipped && 
               !CurrentHopballController.IsDissolving && 
               !CurrentHopballController.IsAwaitingRespawn && 
               CurrentHopballController.transform.position.y <= outOfBoundsY) {
                TeleportHopballToMostRecentSpawn();
            }
        }

        private float GetOutOfBoundsKillY() {
            RefreshOutOfBoundsCacheIfNeeded();
            return _cachedOutOfBoundsY;
        }

        private bool IsYLevelOutOfBoundsKillEnabled() {
            RefreshOutOfBoundsCacheIfNeeded();
            return _cachedUseYLevelOutOfBoundsKill;
        }

        private void RefreshOutOfBoundsCacheIfNeeded() {
            var activeScene = SceneManager.GetActiveScene();
            if(_cachedOobSceneHandle == activeScene.handle) {
                return;
            }

            _cachedOobSceneHandle = activeScene.handle;
            _cachedOutOfBoundsY = oobThreshold;
            _cachedUseYLevelOutOfBoundsKill = MatchMapService.IsYLevelOutOfBoundsKillEnabled(activeScene.name);

            Transform marker = null;
            if(!string.IsNullOrWhiteSpace(outOfBoundsMarkerTag)) {
                try {
                    var taggedObject = GameObject.FindGameObjectWithTag(outOfBoundsMarkerTag);
                    if(taggedObject != null) {
                        marker = taggedObject.transform;
                    }
                } catch(UnityException) {
                    // Tag may be undefined in some scenes/projects; fallback to name lookup.
                }
            }

            if(marker == null && !string.IsNullOrWhiteSpace(outOfBoundsMarkerName)) {
                var namedObject = GameObject.Find(outOfBoundsMarkerName);
                if(namedObject != null) {
                    marker = namedObject.transform;
                }
            }

            if(marker != null) {
                // Use world-space Y in case marker is parented under WorldRoot.
                _cachedOutOfBoundsY = marker.position.y;
            }
        }

        /// <summary>
        /// Teleports hopball back to its most recent spawn point and makes it kinematic.
        /// Retains current energy.
        /// </summary>
        private void TeleportHopballToMostRecentSpawn() {
            if(CurrentHopballController == null || _mostRecentSpawnPoint == null) return;

            // Don't reposition if post-match has started
            if(PostMatchManager.Instance != null && PostMatchManager.Instance.PostMatchFlowStarted) {
                return;
            }

            // Reposition at most recent spawn point (retains energy)
            var recentSpawnTransform = _mostRecentSpawnPoint.transform;
            var prior = CurrentHopballController.transform.position;
            var position = recentSpawnTransform.position;
            CurrentHopballController.RepositionAtLocation(position,
                recentSpawnTransform.rotation);
            FlowLog.Emit(FlowEventIds.HopballOobRecovery,
                ("hopballNetId", CurrentHopballController.NetworkObjectId),
                ("from", prior),
                ("to", position));

            // Play spawn sound at reposition location (directional, same falloff as gunshots)
            PlayHopballSpawnSoundClientRpc(_mostRecentSpawnPoint.transform.position);
        }

        /// <summary>
        /// Called by HopballController when player picks up ball. Tracks energy for scoring.
        /// </summary>
        public void OnPlayerPickedUpHopball(ulong playerId) {
            if(!IsServer || CurrentHopballController == null) return;

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
            if(_currentHolderId != playerId || CurrentHopballController == null || !CurrentHopballController.IsEquipped) {
                return;
            }

            // Get player's team
            var player = GetPlayerById(playerId);
            if(player == null) return;

            var teamManager = player.TeamManager;
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

            // Check win condition (0 means infinite score limit).
            if(EffectiveWinScore <= 0) return;
            if(_teamAScore.Value >= EffectiveWinScore) {
                TriggerWinCondition(SpawnPoint.Team.TeamA);
            } else if(_teamBScore.Value >= EffectiveWinScore) {
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
            if(!NetworkManager.Singleton.ConnectedClients.TryGetValue(playerId, out var client)) {
                return null;
            }
            return client.PlayerObject == null ? null : client.PlayerObject.GetComponent<PlayerController>();
        }

        /// <summary>
        /// Plays hopball spawn sound at the specified position (directional, same falloff as gunshots).
        /// Called via RPC to play on all clients.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        private void PlayHopballSpawnSoundClientRpc(Vector3 position) {
            if(Audio2.AudioService.Instance == null) return;

            const string soundId = "gameplay.hopball.spawn";
            Audio2.AudioService.Instance.Stop(soundId);
            Audio2.AudioService.Instance.Play(soundId, position);
        }

        [Rpc(SendTo.Everyone)]
        private void PrewarmHopballVisualPoolsClientRpc() {
            foreach(var controller in PlayerHopballController.Instances) {
                if(controller == null) continue;
                controller.PrewarmHopballVisualsIfNeeded();
            }
        }

        /// <summary>
        /// Editor helper: Find all HopballSpawnPoints in scene.
        /// </summary>
        [ContextMenu("Find All Hopball Spawn Points in Scene")]
        private void FindAllSpawnPointsInScene() {
            hopballSpawnPoints = HopballSpawnPoint.Instances.Where(p => p != null).ToList();
        }

        private void CleanupActiveHopball() {
            if(CurrentHopballController == null) return;

            var hopballNetworkObject = CurrentHopballController.NetworkObject;
            if(IsServer && hopballNetworkObject != null && hopballNetworkObject.IsSpawned) {
                hopballNetworkObject.Despawn();
            } else if((hopballNetworkObject == null || hopballNetworkObject.IsSpawned == false) &&
                      CurrentHopballController != null) {
                Destroy(CurrentHopballController.gameObject);
            }

            CurrentHopballController = null;
            _currentHolderId = 0;
            _hasSpawnedInitial = false;
        }
    }
}
