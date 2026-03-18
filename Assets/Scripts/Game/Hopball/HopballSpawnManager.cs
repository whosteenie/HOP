using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Diagnostics;
using Events;
using Game.Audio.System;
using Game.Match;
using Game.Player.Core;
using Network.Core;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

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
        private bool _sessionOwnerCallbacksRegistered;
        private readonly Dictionary<ulong, Collider> _registeredPlayerColliders = new();

        public HopballController CurrentHopballController { get; private set; }
        private bool HasHopballAuthority => NetworkAuthority.HasGlobalAuthority(this);

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
            NetworkAuthority.TryConfigureSessionOwnerObject(this);
            RegisterSessionOwnerCallbacks();
            EventBus.Subscribe<HopballPickedUpEvent>(OnHopballPickedUpEvent);
            EventBus.Subscribe<HopballDroppedEvent>(OnHopballDroppedEvent);
            EventBus.Subscribe<HopballEnergyDepletedEvent>(OnHopballEnergyDepletedEvent);
            EventBus.Subscribe<HopballRespawnRequestedEvent>(OnHopballRespawnRequestedEvent);
            EventBus.Subscribe<HopballCollisionIgnoreStateChangedEvent>(OnHopballCollisionIgnoreStateChangedEvent);
            EventBus.Subscribe<ObjectiveTeamScoresRequestedEvent>(OnObjectiveTeamScoresRequested);

            if(HasHopballAuthority) {
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
            EventBus.Unsubscribe<HopballPickedUpEvent>(OnHopballPickedUpEvent);
            EventBus.Unsubscribe<HopballDroppedEvent>(OnHopballDroppedEvent);
            EventBus.Unsubscribe<HopballEnergyDepletedEvent>(OnHopballEnergyDepletedEvent);
            EventBus.Unsubscribe<HopballRespawnRequestedEvent>(OnHopballRespawnRequestedEvent);
            EventBus.Unsubscribe<HopballCollisionIgnoreStateChangedEvent>(OnHopballCollisionIgnoreStateChangedEvent);
            EventBus.Unsubscribe<ObjectiveTeamScoresRequestedEvent>(OnObjectiveTeamScoresRequested);
            _teamAScore.OnValueChanged -= OnTeamAScoreChanged;
            _teamBScore.OnValueChanged -= OnTeamBScoreChanged;
            CleanupActiveHopball();
            UnregisterSessionOwnerCallbacks();
        }

        private void RegisterSessionOwnerCallbacks() {
            if(_sessionOwnerCallbacksRegistered || NetworkManager == null) return;
            NetworkManager.OnSessionOwnerPromoted += OnSessionOwnerPromoted;
            _sessionOwnerCallbacksRegistered = true;
        }

        private void UnregisterSessionOwnerCallbacks() {
            if(!_sessionOwnerCallbacksRegistered || NetworkManager == null) return;
            NetworkManager.OnSessionOwnerPromoted -= OnSessionOwnerPromoted;
            _sessionOwnerCallbacksRegistered = false;
        }

        private void OnSessionOwnerPromoted(ulong _) {
            if(!HasHopballAuthority) {
                if(_respawnCoroutine == null) return;
                StopCoroutine(_respawnCoroutine);
                _respawnCoroutine = null;
                return;
            }

            NetworkAuthority.TryConfigureSessionOwnerObject(this);
            CurrentHopballController = CurrentHopballController ? CurrentHopballController : FindAnyObjectByType<HopballController>();
            if(CurrentHopballController != null) {
                NetworkAuthority.TryConfigureSessionOwnerObject(CurrentHopballController);
            }
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || matchSettings.selectedGameModeId != "Hopball") {
                return;
            }

            if(CurrentHopballController != null || _isSpawning) return;
            if(MatchTimerManager.Instance != null && !MatchTimerManager.Instance.IsPreMatch) {
                SpawnHopball();
            } else {
                StartCoroutine(InitialSpawnCoroutine());
            }
        }

        private static void OnTeamAScoreChanged(int previous, int current) {
            EventBus.Publish(new ScoreboardRefreshRequestedEvent());
        }

        private static void OnTeamBScoreChanged(int previous, int current) {
            EventBus.Publish(new ScoreboardRefreshRequestedEvent());
        }

        public int GetTeamAScore() => _teamAScore.Value;
        public int GetTeamBScore() => _teamBScore.Value;

        private void OnObjectiveTeamScoresRequested(ObjectiveTeamScoresRequestedEvent evt) {
            if(evt == null || evt.HasScores) return;
            if(evt.GameModeId != "Hopball") return;

            evt.TeamAScore = _teamAScore.Value;
            evt.TeamBScore = _teamBScore.Value;
            evt.HasScores = true;
        }

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

            if(!HasHopballAuthority || _hasSpawnedInitial) yield break;
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
            if(!HasHopballAuthority || _isSpawning || hopballPrefab == null) {
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
                DevLog.LogError("[HopballSpawnManager] No hopball spawn points assigned!");
                return;
            }

            _isSpawning = true;

            // Choose random spawn point
            var validPoints = hopballSpawnPoints.Where(p => p != null).ToList();
            if(validPoints.Count == 0) {
                DevLog.LogError("[HopballSpawnManager] No valid spawn points!");
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
                DevLog.LogError("[HopballSpawnManager] Hopball prefab missing NetworkObject component!");
                _isSpawning = false;
                return;
            }

            // Get the Hopball component and assign to _currentHopball
            CurrentHopballController = instance.GetComponent<HopballController>();
            if(CurrentHopballController == null) {
                DevLog.LogError("[HopballSpawnManager] Hopball prefab missing Hopball component!");
                _isSpawning = false;
                return;
            }
            var objectiveSpawnType = _hasSpawnedInitial ? "Respawn" : "Initial";
            NetworkAuthority.TryConfigureSessionOwnerObject(CurrentHopballController);
            FlowLog.Emit(FlowEventIds.ObjectiveSpawned,
                ("mode", "Hopball"),
                ("objectType", "Hopball"),
                ("spawnType", objectiveSpawnType),
                ("spawnPoint", spawnPoint.name));

            _hasSpawnedInitial = true;
            _isSpawning = false;

            PrewarmVisualPoolsClientRpc();

            // Play spawn sound at spawn location (directional, same falloff as gunshots)
            PlaySpawnSoundClientRpc(spawnPoint.transform.position);
        }

        /// <summary>Respawns the hopball at a new spawn point (authority only).</summary>
        private void RespawnAtNewLocation() {
            if(!HasHopballAuthority || CurrentHopballController == null) {
                DevLog.LogWarning(
                    "[HopballSpawnManager] RespawnAtNewLocation: Cannot respawn (not server or no hopball)");
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

            if(!HasHopballAuthority || CurrentHopballController == null) {
                _respawnCoroutine = null;
                yield break;
            }

            // Don't respawn if post-match has started (check again after delay)
            if(PostMatchManager.Instance != null && PostMatchManager.Instance.PostMatchFlowStarted) {
                _respawnCoroutine = null;
                yield break;
            }

            if(hopballSpawnPoints == null || hopballSpawnPoints.Count == 0) {
                DevLog.LogError("[HopballSpawnManager] No hopball spawn points assigned!");
                _respawnCoroutine = null;
                yield break;
            }

            var validPoints = hopballSpawnPoints.Where(p => p != null).ToList();
            if(validPoints.Count == 0) {
                DevLog.LogError("[HopballSpawnManager] No valid spawn points!");
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

            PrewarmVisualPoolsClientRpc();

            PlaySpawnSoundClientRpc(spawnPoint.transform.position);

            _respawnCoroutine = null;
        }

        /// <summary>
        /// Checks if hopball is OOB and teleports it back if needed.
        /// </summary>
        private void Update() {
            if(!HasHopballAuthority) return;

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || matchSettings.selectedGameModeId != "Hopball") {
                CleanupActiveHopball();
                return;
            }

            if(CurrentHopballController == null || !CurrentHopballController.IsSpawned || _mostRecentSpawnPoint == null) return;
            if(!IsOobKillEnabled()) return;
            var outOfBoundsY = GetOobKillY();
            // Check if hopball is dropped (not equipped) and OOB
            if(!CurrentHopballController.IsEquipped && 
               !CurrentHopballController.IsDissolving && 
               !CurrentHopballController.IsAwaitingRespawn && 
               CurrentHopballController.transform.position.y <= outOfBoundsY) {
                TeleportToMostRecentSpawn();
            }
        }

        /// <summary>Y threshold below which the holder is killed (out-of-bounds).</summary>
        private float GetOobKillY() {
            RefreshOobCacheIfNeeded();
            return _cachedOutOfBoundsY;
        }

        /// <summary>True if the current map uses Y-level out-of-bounds kill.</summary>
        private bool IsOobKillEnabled() {
            RefreshOobCacheIfNeeded();
            return _cachedUseYLevelOutOfBoundsKill;
        }

        /// <summary>Refreshes cached OOB Y and enable flag for the current scene.</summary>
        private void RefreshOobCacheIfNeeded() {
            var activeScene = SceneManager.GetActiveScene();
            if(_cachedOobSceneHandle == activeScene.handle) {
                return;
            }

            _cachedOobSceneHandle = activeScene.handle;
            _cachedOutOfBoundsY = oobThreshold;
            _cachedUseYLevelOutOfBoundsKill = MatchMapService.IsOobKillEnabled(activeScene.name);

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

        /// <summary>Moves the hopball to the most recent spawn point (OOB recovery). Retains energy.</summary>
        private void TeleportToMostRecentSpawn() {
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
            PlaySpawnSoundClientRpc(_mostRecentSpawnPoint.transform.position);
        }

        /// <summary>Tracks the active holder for scoring when the hopball is picked up.</summary>
        private void OnPlayerPickedUp(ulong playerId) {
            if(!HasHopballAuthority || CurrentHopballController == null) return;

            // Track who picked it up and at what energy
            _currentHolderId = playerId;
        }

        /// <summary>Clears active holder tracking when the hopball is dropped.</summary>
        private void OnHopballDropped() {
            if(!HasHopballAuthority) return;

            _currentHolderId = 0;
        }

        /// <summary>Awards score for active holder energy drain notifications.</summary>
        private void OnEnergyDepleted(ulong playerId, float energyDepleted) {
            if(!HasHopballAuthority) return;

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

        private void OnHopballPickedUpEvent(HopballPickedUpEvent evt) {
            if(evt == null) return;
            OnPlayerPickedUp(evt.PlayerId);
        }

        private void OnHopballDroppedEvent(HopballDroppedEvent evt) {
            if(evt == null) return;
            OnHopballDropped();
        }

        private void OnHopballEnergyDepletedEvent(HopballEnergyDepletedEvent evt) {
            if(evt == null) return;
            OnEnergyDepleted(evt.PlayerId, evt.EnergyDepleted);
        }

        private void OnHopballRespawnRequestedEvent(HopballRespawnRequestedEvent evt) {
            if(evt == null || !HasHopballAuthority || CurrentHopballController == null) return;
            if(CurrentHopballController.NetworkObjectId != evt.HopballNetworkObjectId) return;
            RespawnAtNewLocation();
        }

        private void OnHopballCollisionIgnoreStateChangedEvent(HopballCollisionIgnoreStateChangedEvent evt) {
            if(evt == null || CurrentHopballController == null) return;
            if(CurrentHopballController.NetworkObjectId != evt.HopballNetworkObjectId) return;
            if(!evt.IgnorePlayerCollisions) return;

            foreach(var playerCollider in _registeredPlayerColliders.Values) {
                if(playerCollider == null) continue;
                CurrentHopballController.OnPlayerColliderRegistered(playerCollider);
            }
        }

        /// <summary>
        /// Awards a point to the specified team and checks for win condition.
        /// </summary>
        private void AwardPointToTeam(SpawnPoint.Team team) {
            if(!HasHopballAuthority) return;

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
            if(!HasHopballAuthority) return;

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

        /// <summary>Plays spawn sound at position on all clients.</summary>
        [Rpc(SendTo.Everyone)]
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void PlaySpawnSoundClientRpc(Vector3 position) {
            if(AudioService.Instance == null) return;

            const string soundId = "gameplay.hopball.spawn";
            AudioService.Instance.Stop(soundId);
            AudioService.Instance.Play(soundId, position);
        }

        /// <summary>Prewarms hopball visual object pools on all clients.</summary>
        [Rpc(SendTo.Everyone)]
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void PrewarmVisualPoolsClientRpc() {
            EventBus.Publish(new HopballVisualPrewarmRequestedEvent());
        }

        /// <summary>Finds and caches all HopballSpawnPoint in the scene.</summary>
        [ContextMenu("Find All Hopball Spawn Points in Scene")]
        private void FindSpawnPointsInScene() {
            hopballSpawnPoints = HopballSpawnPoint.Instances.Where(p => p != null).ToList();
        }

        private void CleanupActiveHopball() {
            if(CurrentHopballController == null) return;

            var hopballNetworkObject = CurrentHopballController.NetworkObject;
            if(HasHopballAuthority && hopballNetworkObject != null && hopballNetworkObject.IsSpawned) {
                hopballNetworkObject.Despawn();
            } else if((hopballNetworkObject == null || hopballNetworkObject.IsSpawned == false) &&
                      CurrentHopballController != null) {
                Destroy(CurrentHopballController.gameObject);
            }

            CurrentHopballController = null;
            _currentHolderId = 0;
            _hasSpawnedInitial = false;
        }

        /// <summary>Requests server to grant equip for the given hopball to the local client.</summary>
        public void RequestEquipAuthority(NetworkObjectReference hopballRef) {
            if(HasHopballAuthority) {
                ProcessEquipRequest(hopballRef, NetworkManager != null ? NetworkManager.LocalClientId : OwnerClientId);
                return;
            }

            RequestEquipAuthorityServerRpc(hopballRef);
        }

        /// <summary>Requests server to drop the hopball at the given position/rotation.</summary>
        public void RequestDropAuthority(NetworkObjectReference hopballRef, Vector3 dropPosition,
            Quaternion dropRotation, Vector3 playerVelocity, string dropReason) {
            if(HasHopballAuthority) {
                ProcessDropRequest(hopballRef, dropPosition, dropRotation, playerVelocity, dropReason,
                    NetworkManager != null ? NetworkManager.LocalClientId : OwnerClientId);
                return;
            }

            RequestDropAuthorityServerRpc(hopballRef, dropPosition, dropRotation, playerVelocity, dropReason);
        }

        public IEnumerable<Collider> RegisteredPlayerColliders => _registeredPlayerColliders.Values;

        public void RegisterPlayerController(ulong ownerClientId, Collider playerCollider) {
            if(playerCollider == null) return;
            _registeredPlayerColliders[ownerClientId] = playerCollider;
            if(CurrentHopballController != null) CurrentHopballController.OnPlayerColliderRegistered(playerCollider);
        }

        public void UnregisterPlayerController(ulong ownerClientId) {
            if(!_registeredPlayerColliders.Remove(ownerClientId, out var playerCollider)) return;
            if(playerCollider == null) return;
            if(CurrentHopballController != null) CurrentHopballController.OnPlayerColliderUnregistered(playerCollider);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestEquipAuthorityServerRpc(NetworkObjectReference hopballRef,
            RpcParams rpcParams = default) {
            ProcessEquipRequest(hopballRef, rpcParams.Receive.SenderClientId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestDropAuthorityServerRpc(NetworkObjectReference hopballRef, Vector3 dropPosition,
            Quaternion dropRotation, Vector3 playerVelocity, string dropReason, RpcParams rpcParams = default) {
            ProcessDropRequest(hopballRef, dropPosition, dropRotation, playerVelocity, dropReason,
                rpcParams.Receive.SenderClientId);
        }

        /// <summary>Handles an equip request (resolve ref, validate, assign holder).</summary>
        private void ProcessEquipRequest(NetworkObjectReference hopballRef, ulong requestingClientId) {
            if(!HasHopballAuthority) return;
            if(!hopballRef.TryGet(out var networkObject) || networkObject == null) return;

            var hopball = networkObject.GetComponent<HopballController>();
            if(hopball == null) return;
            if(hopball.IsEquipped) {
                FlowLog.Emit(FlowEventIds.AnomalyHopballMismatch,
                    ("serverHolder", hopball.HolderController != null ? hopball.HolderController.OwnerClientId.ToString() : "None"),
                    ("localHolder", requestingClientId),
                    ("osiHolder", "Unknown"),
                    ("reason", "PickupRejectedAlreadyEquipped"));
                return;
            }

            if(NetworkManager == null || !NetworkManager.ConnectedClients.TryGetValue(requestingClientId, out var client)) {
                return;
            }

            var requestingPlayer = client.PlayerObject;
            if(requestingPlayer == null) return;

            var requestingController = requestingPlayer.GetComponent<PlayerController>();
            if(requestingController == null) return;
            FlowLog.Emit(FlowEventIds.HopballPickupCommitted,
                ("player", requestingClientId),
                ("hopballNetId", networkObject.NetworkObjectId),
                ("serverHolder", requestingClientId));

            hopball.SetEquipped(true, requestingController);

            PublishHopballEquippedPresentationClientRpc(hopballRef, requestingClientId);
        }

        /// <summary>Handles a drop request (position, rotation, velocity, reason).</summary>
        private void ProcessDropRequest(NetworkObjectReference hopballRef, Vector3 dropPosition,
            Quaternion dropRotation, Vector3 playerVelocity, string dropReason, ulong requestingClientId) {
            if(!HasHopballAuthority) return;
            if(!hopballRef.TryGet(out var networkObject) || networkObject == null) return;

            var hopball = networkObject.GetComponent<HopballController>();
            ExecuteDropAuthorityAsync(hopball, dropPosition, dropRotation, requestingClientId, playerVelocity, dropReason)
                .Forget();
        }

        [Rpc(SendTo.Everyone)]
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void PublishHopballEquippedPresentationClientRpc(NetworkObjectReference hopballRef, ulong holderClientId) {
            EventBus.Publish(new HopballEquippedPresentationEvent(hopballRef, holderClientId));
        }

        [Rpc(SendTo.Everyone)]
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void PublishHopballDropPresentationClientRpc(ulong holderClientId) {
            EventBus.Publish(new HopballDropPresentationEvent(holderClientId));
        }

        private async Cysharp.Threading.Tasks.UniTaskVoid ExecuteDropAuthorityAsync(HopballController hopball,
            Vector3 dropPosition, Quaternion dropRotation, ulong requestingClientId, Vector3 playerVelocity,
            string dropReason) {
            if(hopball == null) return;
            var dropTimer = Stopwatch.StartNew();

            if(!hopball.IsEquipped) {
                FlowLog.Emit(FlowEventIds.AnomalyHopballMismatch,
                    ("serverHolder", hopball.HolderController != null ? hopball.HolderController.OwnerClientId.ToString() : "None"),
                    ("localHolder", requestingClientId),
                    ("osiHolder", "Unknown"),
                    ("reason", "DropRejectedNotEquipped"));
                return;
            }

            hopball.PrepareDropClientRpc();
            LogDropStep("PrepareDropClientRpc");
            await Cysharp.Threading.Tasks.UniTask.WaitForEndOfFrame();
            LogDropStep("WaitForEndOfFrame");

            PlayerController requestingController = null;
            if(NetworkManager.Singleton != null &&
               NetworkManager.Singleton.ConnectedClients.TryGetValue(requestingClientId, out var client)) {
                var requestingPlayer = client.PlayerObject;
                if(requestingPlayer != null) {
                    requestingController = requestingPlayer.GetComponent<PlayerController>();
                }
            }

            var serverHolderId = hopball.HolderController != null ? hopball.HolderController.OwnerClientId : ulong.MaxValue;
            if(serverHolderId != requestingClientId) {
                var serverPos = hopball.transform.position;
                var deltaFromRequest = Vector3.Distance(serverPos, dropPosition);
                FlowLog.Emit(FlowEventIds.AnomalyHopballDivergence,
                    ("serverHolder", serverHolderId == ulong.MaxValue ? "None" : serverHolderId.ToString()),
                    ("serverPos", serverPos),
                    ("clientPos", dropPosition),
                    ("delta", deltaFromRequest));
            }

            var hopballCollider = hopball.GetComponent<Collider>();
            var hopballRadius = 0.5f;
            var sphereCollider = hopballCollider as SphereCollider;
            if(sphereCollider != null) {
                var lossyScale = hopball.transform.lossyScale;
                hopballRadius = sphereCollider.radius * Mathf.Max(lossyScale.x, lossyScale.y, lossyScale.z);
            } else {
                var capsuleCollider = hopballCollider as CapsuleCollider;
                if(capsuleCollider != null) {
                    var lossyScale = hopball.transform.lossyScale;
                    hopballRadius = capsuleCollider.radius * Mathf.Max(lossyScale.x, lossyScale.z);
                }
            }
            LogDropStep("ResolveColliderRadius");

            var finalDropPosition = dropPosition;
            var worldLayer = LayerMask.GetMask("Default", "World");
            if(requestingController != null) {
                worldLayer = requestingController.WorldLayer.value;
                var origin = requestingController.Position + Vector3.up * 1.2f;
                var toDrop = finalDropPosition - origin;
                var distToDrop = toDrop.magnitude;
                if(distToDrop > 0.001f) {
                    var dirToDrop = toDrop / distToDrop;
                    const float wallMargin = 0.03f;
                    var castRadius = hopballRadius + wallMargin;
                    if(Physics.SphereCast(origin, castRadius, dirToDrop, out var wallHit, distToDrop, worldLayer)) {
                        finalDropPosition = wallHit.point - dirToDrop * (castRadius + 0.01f);
                    }
                }
            }
            LogDropStep("WallCastAdjustment");

            const float raycastDistance = 15f;
            const float safetyMargin = 0.2f;
            var sphereCastRadius = hopballRadius + safetyMargin;
            var sphereCastStart = finalDropPosition + Vector3.up * sphereCastRadius;
            var sphereCastDistance = sphereCastRadius * 2f + 5f;
            var sphereHit = Physics.SphereCast(sphereCastStart, sphereCastRadius, Vector3.down, out var hit, sphereCastDistance, worldLayer);

            if(sphereHit) {
                var groundHeight = hit.point.y + sphereCastRadius;
                if(finalDropPosition.y < groundHeight) {
                    finalDropPosition = new Vector3(finalDropPosition.x, groundHeight, finalDropPosition.z);
                }
            } else if(Physics.Raycast(finalDropPosition + Vector3.up * 0.1f, Vector3.down, out var rayHit, raycastDistance, worldLayer)) {
                var groundHeight = rayHit.point.y + sphereCastRadius;
                if(finalDropPosition.y < groundHeight) {
                    finalDropPosition = new Vector3(finalDropPosition.x, groundHeight, finalDropPosition.z);
                }
            } else if(Physics.Raycast(finalDropPosition + Vector3.down * 5f, Vector3.up, out var hitUp, 15f, worldLayer)) {
                var groundHeight = hitUp.point.y + sphereCastRadius;
                if(finalDropPosition.y < groundHeight) {
                    finalDropPosition = new Vector3(finalDropPosition.x, groundHeight, finalDropPosition.z);
                }
            }
            LogDropStep("GroundingAdjustment");

            var currentScale = hopball.transform.localScale;
            var networkTransform = hopball.GetComponent<Unity.Netcode.Components.NetworkTransform>();
            var rb = hopball.Rigidbody;

            rb.isKinematic = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.position = finalDropPosition;
            rb.rotation = dropRotation;

            if(networkTransform != null) {
                networkTransform.Teleport(finalDropPosition, dropRotation, currentScale);
            } else {
                var hopballTransform = hopball.transform;
                hopballTransform.position = finalDropPosition;
                hopballTransform.rotation = dropRotation;
                hopballTransform.localScale = currentScale;
            }
            LogDropStep("TeleportApplied");

            hopball.transform.SetParent(null);
            rb.position = finalDropPosition;
            rb.rotation = dropRotation;
            LogDropStep("ParentAndRigidbodySet");
            Physics.SyncTransforms();
            LogDropStep("PhysicsSyncTransforms");

            await Cysharp.Threading.Tasks.UniTask.WaitForFixedUpdate();
            LogDropStep("WaitForFixedUpdate");

            hopball.SetDropped();
            LogDropStep("SetDropped");
            FlowLog.Emit(FlowEventIds.HopballDropCommitted,
                ("player", requestingClientId),
                ("hopballNetId", hopball.NetworkObjectId),
                ("dropReason", string.IsNullOrEmpty(dropReason) ? "Unknown" : dropReason),
                ("position", finalDropPosition));

            hopball.Rigidbody.isKinematic = false;

            const float velocityTransferFactor = 0.3f;
            var ballVelocity = playerVelocity * velocityTransferFactor;
            if(ballVelocity.y > -1f) {
                ballVelocity.y = -2f;
            }
            hopball.Rigidbody.linearVelocity = ballVelocity;
            LogDropStep("ApplyVelocity");

            PublishHopballDropPresentationClientRpc(requestingClientId);
            LogDropStep("PublishDropPresentation");
            return;

            void LogDropStep(string stepName) {
                if(!Debug.isDebugBuild) return;
                DevLog.Log(
                    $"[HopballDropTiming] client={requestingClientId} reason={dropReason} step={stepName} elapsedMs={dropTimer.Elapsed.TotalMilliseconds:F1}");
            }
        }
    }
}
