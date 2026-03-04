using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Menu;
using Game.Player;
using Network.Diagnostics;
using Network.Events;
using Unity.Netcode;
using UnityEngine;
using SessionManager = Network.Session.SessionManager;

namespace Game.Match {
    public class MatchTimerManager : NetworkBehaviour {
        public static MatchTimerManager Instance { get; private set; }
        public static event System.Action<MatchTimerManager> InstanceReady;
        public static event System.Action InstanceCleared;

        [Header("Match Settings")]
        [SerializeField] private int matchDurationSeconds = 600; // 10 minutes by default

        private readonly NetworkVariable<int> _timeRemainingSeconds = new(value: 0);
        private readonly NetworkVariable<int> _preMatchCountdownSeconds = new(value: 0);
        private readonly NetworkVariable<bool> _isWaitingForPlayers = new(value: false);
        private readonly NetworkVariable<bool> _isPreMatch = new(value: true);

        public int TimeRemainingSeconds => _timeRemainingSeconds.Value;
        public int PreMatchCountdownSeconds => _preMatchCountdownSeconds.Value;
        public bool IsWaitingForPlayers => _isWaitingForPlayers.Value;
        public bool IsPreMatch => _isPreMatch.Value;

        private Coroutine _timerRoutine;
        private bool _hasTriggeredPostMatch;
        private bool _hasDesignatedInitialIt;
        private readonly HashSet<ulong> _clientsScenePresented = new();

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            InstanceReady?.Invoke(this);
            EventBus.Publish(new MatchTimerReadyEvent());

            if(MatchSettingsManager.Instance != null) {
                matchDurationSeconds = MatchSettingsManager.Instance.GetMatchDurationSeconds();
            }
        }

        private void ClearInstanceIfCurrent() {
            if(Instance != this) return;
            Instance = null;
            InstanceCleared?.Invoke();
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Subscribe for UI updates on all clients
            _timeRemainingSeconds.OnValueChanged += OnTimeRemainingChanged;
            _preMatchCountdownSeconds.OnValueChanged += OnPreMatchCountdownChanged;
            _isWaitingForPlayers.OnValueChanged += OnPreMatchWaitingForPlayersChanged;
            _isPreMatch.OnValueChanged += OnPreMatchStateChanged;

            if(IsServer) {
                _clientsScenePresented.Clear();
                if(NetworkManager != null) {
                    NetworkManager.OnClientDisconnectCallback -= OnClientDisconnectedDuringPreMatch;
                    NetworkManager.OnClientDisconnectCallback += OnClientDisconnectedDuringPreMatch;
                    // Host is always scene-present when this object is network-spawned.
                    MarkClientScenePresented(NetworkManager.LocalClientId, "HostOnNetworkSpawn");
                }

                // Initialize pre-match countdown on server
                var matchSettings = MatchSettingsManager.Instance;
                var usePreMatchCountdown = matchSettings == null || matchSettings.IsPreMatchCountdownEnabled();

                // Ensure we don't double-start
                if(_timerRoutine != null) {
                    StopCoroutine(_timerRoutine);
                    _timerRoutine = null;
                }

                if(usePreMatchCountdown) {
                    var preMatchSeconds = matchSettings != null ? matchSettings.GetPreMatchCountdownSeconds() : 5;
                    _preMatchCountdownSeconds.Value = Mathf.Max(0, preMatchSeconds);
                    _isWaitingForPlayers.Value = true;
                    _isPreMatch.Value = true;
                    FlowLog.Emit(FlowEventIds.MatchStateTransition,
                        ("from", "None"),
                        ("to", "PreMatch"),
                        ("timeRemaining", _preMatchCountdownSeconds.Value));

                    _timerRoutine = StartCoroutine(PreMatchCountdownCoroutine());
                } else {
                    _preMatchCountdownSeconds.Value = 0;
                    _isWaitingForPlayers.Value = false;
                    StartActiveMatchOnServer("None");
                }
            }

            // Push a sensible initial value to UI immediately when a client joins.
            // Clients can briefly see default NetworkVariable values before sync arrives.
            OnPreMatchCountdownChanged(0, GetInitialPreMatchCountdownForUi());
            OnPreMatchWaitingForPlayersChanged(false, GetInitialWaitingForPlayersForUi());
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            if(NetworkManager != null) {
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnectedDuringPreMatch;
            }
            ClearInstanceIfCurrent();
        }

        public override void OnDestroy() {
            base.OnDestroy();
            _timeRemainingSeconds.OnValueChanged -= OnTimeRemainingChanged;
            _preMatchCountdownSeconds.OnValueChanged -= OnPreMatchCountdownChanged;
            _isWaitingForPlayers.OnValueChanged -= OnPreMatchWaitingForPlayersChanged;
            _isPreMatch.OnValueChanged -= OnPreMatchStateChanged;
            if(NetworkManager != null) {
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnectedDuringPreMatch;
            }

            ClearInstanceIfCurrent();
        }

        private void OnClientDisconnectedDuringPreMatch(ulong clientId) {
            if(!IsServer) return;
            _clientsScenePresented.Remove(clientId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ReportClientScenePresentedServerRpc(RpcParams rpcParams = default) {
            if(!IsServer) return;
            MarkClientScenePresented(rpcParams.Receive.SenderClientId, "ClientServerRpc");
        }

        public void MarkClientScenePresented(ulong clientId, string source = "ServerLocal") {
            if(!IsServer) return;
            if(_clientsScenePresented.Add(clientId) && Debug.isDebugBuild) {
                Debug.Log($"[MatchTimerManager] Client {clientId} marked scene-presented ({source}).");
            }
        }

        /// <summary>
        /// Pre-match countdown coroutine. Waits for all players to load, then counts down from configured seconds, then starts the match timer.
        /// </summary>
        private IEnumerator PreMatchCountdownCoroutine() {
            var wait = new WaitForSeconds(1f);

            // Wait for expected players to connect/load/present their scene before starting countdown.
            // We allow an early-join grace window so stale expected players (alt+F4 during connect)
            // do not block match start forever.
            var expectedPlayers = 1;
            if(SessionManager.Instance != null) {
                expectedPlayers = Mathf.Max(1, SessionManager.Instance.ExpectedGamePlayerCount);
            }

            const float expectedJoinGraceSeconds = 30f;
            var expectedCountLocked = false;
            const float maxWaitSeconds = 60f;
            var waitedSeconds = 0f;

            while (IsServer && waitedSeconds < maxWaitSeconds) {
                var connectedClients = NetworkManager.Singleton.ConnectedClients;
                var connectedCount = connectedClients.Count;

                if(!expectedCountLocked && waitedSeconds >= expectedJoinGraceSeconds && connectedCount < expectedPlayers) {
                    expectedPlayers = Mathf.Max(connectedCount, 1);
                    expectedCountLocked = true;
                    Debug.LogWarning(
                        $"[MatchTimerManager] Expected-player grace expired. Continuing with {expectedPlayers} expected connected players.");
                }

                var haveExpectedConnections = connectedCount >= expectedPlayers;

                // Check if we have player objects spawned for all currently connected clients.
                var allPlayersReady = true;
                foreach (var kvp in connectedClients) {
                    if(kvp.Value.PlayerObject != null && kvp.Value.PlayerObject.IsSpawned) continue;
                    allPlayersReady = false;
                    break;
                }

                var allConnectedPresented = true;
                foreach(var kvp in connectedClients) {
                    if(_clientsScenePresented.Contains(kvp.Key)) continue;
                    allConnectedPresented = false;
                    break;
                }

                if (haveExpectedConnections && allPlayersReady && allConnectedPresented && connectedCount > 0) {
                    Debug.Log(
                        $"[MatchTimerManager] All {connectedCount}/{expectedPlayers} expected players connected, spawned, and scene-presented. Starting countdown.");
                    break;
                }

                Debug.Log(
                    $"[MatchTimerManager] Waiting for players... expected={expectedPlayers} connected={connectedCount} spawnedReady={allPlayersReady} presented={_clientsScenePresented.Count}");
                yield return wait;
                waitedSeconds += 1f;
            }

            if (waitedSeconds >= maxWaitSeconds) {
                Debug.LogWarning("[MatchTimerManager] Timed out waiting for all players. Starting countdown anyway.");
            }

            _isWaitingForPlayers.Value = false;

            // Pre-match countdown
            while(IsServer && _preMatchCountdownSeconds.Value > 0) {
                yield return wait;
                _preMatchCountdownSeconds.Value--;
            }

            // Pre-match countdown finished - start the actual match
            if(!IsServer) yield break;
            StartActiveMatchOnServer("PreMatch");
        }

        private IEnumerator TimerCoroutine() {
            var wait = new WaitForSeconds(1f);

            while(IsServer && !_isPreMatch.Value && _timeRemainingSeconds.Value > 0) {
                yield return wait;
                _timeRemainingSeconds.Value--;
            }

            // Only trigger post-match if we're not in pre-match (safety check)
            if(!IsServer || _isPreMatch.Value || _hasTriggeredPostMatch) yield break;
            _hasTriggeredPostMatch = true;
            FlowLog.Emit(FlowEventIds.MatchStateTransition,
                ("from", "Active"),
                ("to", "PostMatch"),
                ("timeRemaining", _timeRemainingSeconds.Value));
            
            // Publish match ended event
            EventBus.Publish(new MatchEndedEvent());
        }

        private void OnTimeRemainingChanged(int previous, int current) {
            // Only update UI if we're not in pre-match
            if(_isPreMatch.Value || GameMenuManager.Instance == null) return;
            EventBus.Publish(new SetMatchTimeEvent(current));
        }

        private void OnPreMatchCountdownChanged(int previous, int current) {
            // Display pre-match countdown in UI
            if(!_isPreMatch.Value || GameMenuManager.Instance == null) return;
            if(GameMenuManager.Instance.IsPostMatch) {
                GameMenuManager.Instance.RestoreHudForMatchStart();
            }
            EventBus.Publish(new SetMatchTimeEvent(current));
        }

        private static void OnPreMatchWaitingForPlayersChanged(bool previous, bool current) {
            EventBus.Publish(new PreMatchWaitingForPlayersEvent(current));
        }

        private int GetInitialPreMatchCountdownForUi() {
            var current = _preMatchCountdownSeconds.Value;
            if(current > 0 || !_isPreMatch.Value) return current;

            var settings = MatchSettingsManager.Instance;
            var configured = settings != null ? settings.GetPreMatchCountdownSeconds() : 5;
            return Mathf.Max(0, configured);
        }

        private bool GetInitialWaitingForPlayersForUi() {
            if(_isWaitingForPlayers.Value) return true;
            if(!_isPreMatch.Value) return false;

            var settings = MatchSettingsManager.Instance;
            return settings == null || settings.IsPreMatchCountdownEnabled();
        }

        private void OnPreMatchStateChanged(bool previous, bool current) {
            if(current && GameMenuManager.Instance != null) {
                GameMenuManager.Instance.RestoreHudForMatchStart();
            }
            // When pre-match ends, ensure UI shows match timer
            if(current || GameMenuManager.Instance == null) return;
            GameMenuManager.Instance.RestoreHudForMatchStart();
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null && matchSettings.IsInfiniteMatchTimer()) {
                EventBus.Publish(new SetMatchTimeEvent(-1));
            } else {
                EventBus.Publish(new SetMatchTimeEvent(_timeRemainingSeconds.Value));
            }
        }

        private void StartActiveMatchOnServer(string fromState) {
            if(!IsServer) return;

            _isWaitingForPlayers.Value = false;
            _isPreMatch.Value = false;
            var matchSettings = MatchSettingsManager.Instance;
            matchDurationSeconds = 600;
            if(matchSettings != null) {
                matchDurationSeconds = matchSettings.GetMatchDurationSeconds();
            }

            _timeRemainingSeconds.Value = Mathf.Max(0, matchDurationSeconds);
            FlowLog.Emit(FlowEventIds.MatchStateTransition,
                ("from", fromState),
                ("to", "Active"),
                ("timeRemaining", _timeRemainingSeconds.Value));

            // Publish match started event
            EventBus.Publish(new MatchStartedEvent());

            // Check if we're in Tag mode and designate initial "it" after 5 seconds
            if(matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag") {
                StartCoroutine(DesignateInitialItAfterDelay());
            }

            // Check if we're in Hopball mode and spawn hopball after 5 seconds
            if(matchSettings != null && matchSettings.selectedGameModeId == "Hopball") {
                // HopballSpawnManager will handle spawning
            }

            // Start timer only for finite-duration matches. Infinite timer never triggers post-match by time.
            var isInfiniteTimer = matchSettings != null && matchSettings.IsInfiniteMatchTimer();
            if(!isInfiniteTimer) {
                _timerRoutine = StartCoroutine(TimerCoroutine());
            } else {
                EventBus.Publish(new SetMatchTimeEvent(-1));
            }
        }

        /// <summary>
        /// Designates a random player as "it" after 5 seconds if no one is tagged yet (Tag mode only).
        /// </summary>
        private IEnumerator DesignateInitialItAfterDelay() {
            yield return new WaitForSeconds(5f);

            if(_hasDesignatedInitialIt || !IsServer) yield break;

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || matchSettings.selectedGameModeId != "Gun Tag") yield break;

            // Check if anyone is already tagged
            var allPlayers = PlayerController.SpawnedPlayers
                .Where(p => p != null && p.NetworkObject != null && p.NetworkObject.IsSpawned)
                .ToList();

            if(allPlayers.Count == 0) yield break;

            // Check if anyone is already tagged
            var anyoneTagged = allPlayers.Any(p => {
                var tagCtrl = p.GetComponent<PlayerTagController>();
                return tagCtrl != null && tagCtrl.isTagged.Value;
            });

            if(anyoneTagged) yield break;
            {
                // Randomly select a player to be "it"
                var randomPlayer = allPlayers[Random.Range(0, allPlayers.Count)];
                var tagCtrl = randomPlayer.GetComponent<PlayerTagController>();

                if(tagCtrl != null) {
                    // Tag the player
                    tagCtrl.isTagged.Value = true;
                    tagCtrl.tagged.Value++;

                    // Play tagged sound for the player who was designated as "it"
                    tagCtrl.PlayTaggedSoundClientRpc();
                }

                // Broadcast to kill feed with HOP as the tagger (similar to OOB kills)
                if(tagCtrl != null) {
                    tagCtrl.BroadcastTagTransferFromHopClientRpc(randomPlayer.OwnerClientId);
                }

                _hasDesignatedInitialIt = true;
            }
        }
    }
}
