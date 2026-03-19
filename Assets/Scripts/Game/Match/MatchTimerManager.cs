using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Diagnostics;
using Events;
using Network.Contracts;
using Network.Core;
using Unity.Netcode;
using UnityEngine;
using SessionManager = Network.Session.SessionManager;

namespace Game.Match {
    public class MatchTimerManager : NetworkBehaviour {
        public static MatchTimerManager Instance { get; private set; }

        [Header("Match Settings")]
        [SerializeField] private int matchDurationSeconds = 600; // 10 minutes by default

        private readonly NetworkVariable<int> _timeRemainingSeconds = new(value: 0);
        private readonly NetworkVariable<int> _preMatchCountdownSeconds = new(value: 0);
        private readonly NetworkVariable<double> _preMatchCountdownEndServerTimeSeconds = new(value: 0d);
        private readonly NetworkVariable<double> _activeMatchEndServerTimeSeconds = new(value: 0d);
        private readonly NetworkVariable<bool> _isWaitingForPlayers = new(value: false);
        private readonly NetworkVariable<bool> _isPreMatch = new(value: true);

        public int TimeRemainingSeconds => GetComputedActiveTimeRemainingSeconds();
        public int PreMatchCountdownSeconds => GetComputedPreMatchCountdownSeconds();
        public bool IsWaitingForPlayers => _isWaitingForPlayers.Value;
        public bool IsPreMatch => _isPreMatch.Value;

        private Coroutine _timerRoutine;
        private bool _hasTriggeredPostMatch;
        private bool _hasDesignatedInitialIt;
        private readonly HashSet<ulong> _clientsScenePresented = new();
        private readonly HashSet<ulong> _spawnedPlayerClientIds = new();
        private readonly HashSet<ulong> _taggedPlayerClientIds = new();
        private bool _sessionOwnerCallbacksRegistered;
        private int _lastPublishedMatchSeconds = int.MinValue;
        private int _lastPublishedPreMatchSeconds = int.MinValue;

        private bool HasMatchAuthority => NetworkAuthority.HasGlobalAuthority(this);
        private double CurrentServerTimeSeconds => NetworkManager != null ? NetworkManager.ServerTime.Time : Time.unscaledTimeAsDouble;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            EventBus.Publish(new MatchTimerReadyEvent());

            if(MatchSettingsManager.Instance != null) {
                matchDurationSeconds = MatchSettingsManager.Instance.GetMatchDurationSeconds();
            }
        }

        private void ClearInstanceIfCurrent() {
            if(Instance != this) return;
            Instance = null;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            NetworkAuthority.TryConfigureSessionOwnerObject(this);
            RegisterSessionOwnerCallbacks();
            SubscribeGameplayEvents();

            // Subscribe for UI updates on all clients
            _timeRemainingSeconds.OnValueChanged += OnTimeRemainingChanged;
            _preMatchCountdownSeconds.OnValueChanged += OnPreMatchCountdownChanged;
            _isWaitingForPlayers.OnValueChanged += PublishPreMatchWaitingChanged;
            _isPreMatch.OnValueChanged += OnPreMatchStateChanged;

            if(HasMatchAuthority) {
                EnterAuthoritativeMode(resetState: true, "OnNetworkSpawn");
            }

            // Push a sensible initial value to UI immediately when a client joins.
            // Clients can briefly see default NetworkVariable values before sync arrives.
            OnPreMatchCountdownChanged(0, GetInitialPreMatchCountdownForUi());
            PublishPreMatchWaitingChanged(false, GetInitialWaitingForPlayersForUi());
            _lastPublishedMatchSeconds = int.MinValue;
            _lastPublishedPreMatchSeconds = int.MinValue;
        }

        private void Update() {
            if(!IsSpawned) return;

            if(_isPreMatch.Value) {
                var currentPreMatchSeconds = PreMatchCountdownSeconds;
                if(currentPreMatchSeconds == _lastPublishedPreMatchSeconds) return;

                _lastPublishedPreMatchSeconds = currentPreMatchSeconds;
                EventBus.Publish(new PreMatchCountdownEvent(currentPreMatchSeconds));
                EventBus.Publish(new SetMatchTimeEvent(currentPreMatchSeconds));
                return;
            }

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null && matchSettings.IsInfiniteMatchTimer()) return;

            var currentSeconds = TimeRemainingSeconds;
            if(currentSeconds == _lastPublishedMatchSeconds) return;

            _lastPublishedMatchSeconds = currentSeconds;
            EventBus.Publish(new SetMatchTimeEvent(currentSeconds));
        }

        public override void OnNetworkDespawn() {
            if(NetworkManager != null && NetworkManager.DistributedAuthorityMode && !NetworkManager.ShutdownInProgress) {
                DevLog.LogWarning("[MatchTimerManager] Unexpected network despawn while DA session is still active.");
            }

            ExitAuthoritativeMode();
            base.OnNetworkDespawn();
            UnsubscribeGameplayEvents();
            if(NetworkManager != null) {
                NetworkManager.OnClientDisconnectCallback -= OnPreMatchClientDisconnected;
            }
            UnregisterSessionOwnerCallbacks();
            ClearInstanceIfCurrent();
        }

        public override void OnDestroy() {
            base.OnDestroy();
            UnsubscribeGameplayEvents();
            _timeRemainingSeconds.OnValueChanged -= OnTimeRemainingChanged;
            _preMatchCountdownSeconds.OnValueChanged -= OnPreMatchCountdownChanged;
            _isWaitingForPlayers.OnValueChanged -= PublishPreMatchWaitingChanged;
            _isPreMatch.OnValueChanged -= OnPreMatchStateChanged;
            if(NetworkManager != null) {
                NetworkManager.OnClientDisconnectCallback -= OnPreMatchClientDisconnected;
            }
            UnregisterSessionOwnerCallbacks();

            ClearInstanceIfCurrent();
        }

        private void SubscribeGameplayEvents() {
            EventBus.Unsubscribe<PlayerNetworkSpawnedEvent>(OnPlayerNetworkSpawned);
            EventBus.Unsubscribe<PlayerNetworkDespawnedEvent>(OnPlayerNetworkDespawned);
            EventBus.Unsubscribe<PlayerTagStateChangedEvent>(OnPlayerTagStateChanged);
            EventBus.Unsubscribe<PlayerTagBootstrapStateReportedEvent>(OnTagBootstrapStateReported);

            EventBus.Subscribe<PlayerNetworkSpawnedEvent>(OnPlayerNetworkSpawned);
            EventBus.Subscribe<PlayerNetworkDespawnedEvent>(OnPlayerNetworkDespawned);
            EventBus.Subscribe<PlayerTagStateChangedEvent>(OnPlayerTagStateChanged);
            EventBus.Subscribe<PlayerTagBootstrapStateReportedEvent>(OnTagBootstrapStateReported);
        }

        private void UnsubscribeGameplayEvents() {
            EventBus.Unsubscribe<PlayerNetworkSpawnedEvent>(OnPlayerNetworkSpawned);
            EventBus.Unsubscribe<PlayerNetworkDespawnedEvent>(OnPlayerNetworkDespawned);
            EventBus.Unsubscribe<PlayerTagStateChangedEvent>(OnPlayerTagStateChanged);
            EventBus.Unsubscribe<PlayerTagBootstrapStateReportedEvent>(OnTagBootstrapStateReported);
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
            if(HasMatchAuthority) {
                EnterAuthoritativeMode(resetState: false, "SessionOwnerPromoted");
            } else {
                ExitAuthoritativeMode();
            }
        }

        private void EnterAuthoritativeMode(bool resetState, string source) {
            NetworkAuthority.TryConfigureSessionOwnerObject(this);
            _clientsScenePresented.Clear();
            _spawnedPlayerClientIds.Clear();
            _taggedPlayerClientIds.Clear();

            if(NetworkManager != null) {
                NetworkManager.OnClientDisconnectCallback -= OnPreMatchClientDisconnected;
                NetworkManager.OnClientDisconnectCallback += OnPreMatchClientDisconnected;

                if(!resetState) {
                    foreach(var clientId in NetworkManager.ConnectedClientsIds) {
                        _clientsScenePresented.Add(clientId);
                    }
                }

                MarkClientScenePresented(NetworkManager.LocalClientId, source);
            }

            if(_timerRoutine != null) {
                StopCoroutine(_timerRoutine);
                _timerRoutine = null;
            }

            if(resetState) {
                var matchSettings = MatchSettingsManager.Instance;
                var usePreMatchCountdown = matchSettings == null || matchSettings.IsPreMatchCountdownEnabled();
                _hasTriggeredPostMatch = false;
                _hasDesignatedInitialIt = false;
                _preMatchCountdownEndServerTimeSeconds.Value = 0d;
                _activeMatchEndServerTimeSeconds.Value = 0d;
                _lastPublishedPreMatchSeconds = int.MinValue;

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
                    return;
                }

                _preMatchCountdownSeconds.Value = 0;
                _isWaitingForPlayers.Value = false;
                StartActiveMatchOnServer("None");
                return;
            }

            ResumeAuthoritativeMode();
        }

        private void ExitAuthoritativeMode() {
            if(_timerRoutine != null) {
                StopCoroutine(_timerRoutine);
                _timerRoutine = null;
            }

            if(NetworkManager != null) {
                NetworkManager.OnClientDisconnectCallback -= OnPreMatchClientDisconnected;
            }
        }

        private void ResumeAuthoritativeMode() {
            if(_hasTriggeredPostMatch) {
                return;
            }

            if(_isPreMatch.Value) {
                _timerRoutine = StartCoroutine(PreMatchCountdownCoroutine());
                return;
            }

            var matchSettings = MatchSettingsManager.Instance;
            var isInfiniteTimer = matchSettings != null && matchSettings.IsInfiniteMatchTimer();
            if(isInfiniteTimer) return;

            if(_activeMatchEndServerTimeSeconds.Value > 0d) {
                _timerRoutine = StartCoroutine(TimerCoroutine());
            }
        }

        private void OnPreMatchClientDisconnected(ulong clientId) {
            if(!HasMatchAuthority) return;
            _clientsScenePresented.Remove(clientId);
            _spawnedPlayerClientIds.Remove(clientId);
            _taggedPlayerClientIds.Remove(clientId);
        }

        /// <summary>Server RPC: client reports that the gameplay scene is loaded and presented.</summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ReportScenePresentedServerRpc(RpcParams rpcParams = default) {
            if(!HasMatchAuthority) return;
            MarkClientScenePresented(rpcParams.Receive.SenderClientId, "ClientServerRpc");
        }

        public void MarkClientScenePresented(ulong clientId, string source = "ServerLocal") {
            if(!HasMatchAuthority) return;
            if(_clientsScenePresented.Add(clientId) && Debug.isDebugBuild) {
                DevLog.Log($"[MatchTimerManager] Client {clientId} marked scene-presented ({source}).");
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
            var session = SessionManager.Instance;
            if(session != null) {
                ISessionContext sessionCtx = session;
                expectedPlayers = Mathf.Max(1, sessionCtx.ExpectedGamePlayerCount);
            }

            const float expectedJoinGraceSeconds = 30f;
            var expectedCountLocked = false;
            const float maxWaitSeconds = 60f;
            var waitedSeconds = 0f;

            while (HasMatchAuthority && waitedSeconds < maxWaitSeconds) {
                var connectedClients = NetworkManager.Singleton.ConnectedClients;
                var connectedCount = connectedClients.Count;

                if(!expectedCountLocked && waitedSeconds >= expectedJoinGraceSeconds && connectedCount < expectedPlayers) {
                    expectedPlayers = Mathf.Max(connectedCount, 1);
                    expectedCountLocked = true;
                    DevLog.LogWarning(
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
                    DevLog.Log(
                        $"[MatchTimerManager] All {connectedCount}/{expectedPlayers} expected players connected, spawned, and scene-presented. Starting countdown.");
                    break;
                }

                DevLog.Log(
                    $"[MatchTimerManager] Waiting for players... expected={expectedPlayers} connected={connectedCount} spawnedReady={allPlayersReady} presented={_clientsScenePresented.Count}");
                yield return wait;
                waitedSeconds += 1f;
            }

            if (waitedSeconds >= maxWaitSeconds) {
                DevLog.LogWarning("[MatchTimerManager] Timed out waiting for all players. Starting countdown anyway.");
            }

            _isWaitingForPlayers.Value = false;

            if(_preMatchCountdownSeconds.Value > 0 && _preMatchCountdownEndServerTimeSeconds.Value <= 0d) {
                _preMatchCountdownEndServerTimeSeconds.Value = CurrentServerTimeSeconds + _preMatchCountdownSeconds.Value;
                _lastPublishedPreMatchSeconds = int.MinValue;
            }

            // Pre-match countdown
            while(HasMatchAuthority && _isPreMatch.Value && PreMatchCountdownSeconds > 0) {
                yield return null;
                if(!HasMatchAuthority) yield break;
                if(!_isPreMatch.Value) yield break;
            }

            // Pre-match countdown finished - start the actual match
            if(!HasMatchAuthority) yield break;
            StartActiveMatchOnServer("PreMatch");
        }

        private IEnumerator TimerCoroutine() {
            while(HasMatchAuthority && !_isPreMatch.Value) {
                if(_activeMatchEndServerTimeSeconds.Value <= 0d) yield break;
                if(CurrentServerTimeSeconds >= _activeMatchEndServerTimeSeconds.Value) break;

                yield return null;
            }

            // Only trigger post-match if we're not in pre-match (safety check)
            if(!HasMatchAuthority || _isPreMatch.Value || _hasTriggeredPostMatch) yield break;
            _hasTriggeredPostMatch = true;
            FlowLog.Emit(FlowEventIds.MatchStateTransition,
                ("from", "Active"),
                ("to", "PostMatch"),
                ("timeRemaining", TimeRemainingSeconds));
            
            // Publish match ended event
            EventBus.Publish(new MatchEndedEvent());
        }

        private void OnTimeRemainingChanged(int previous, int current) {
            // Only update UI if we're not in pre-match
            if(_isPreMatch.Value) return;
            EventBus.Publish(new SetMatchTimeEvent(TimeRemainingSeconds));
        }

        private void OnPreMatchCountdownChanged(int previous, int current) {
            var computed = PreMatchCountdownSeconds;
            EventBus.Publish(new PreMatchCountdownEvent(computed));

            // Display pre-match countdown in UI
            if(!_isPreMatch.Value) return;
            EventBus.Publish(new SetMatchTimeEvent(computed));
        }

        private static void PublishPreMatchWaitingChanged(bool previous, bool current) {
            EventBus.Publish(new PreMatchWaitingForPlayersEvent(current));
        }

        private int GetInitialPreMatchCountdownForUi() {
            var current = PreMatchCountdownSeconds;
            if(current > 0 || !_isPreMatch.Value) return current;

            var settings = MatchSettingsManager.Instance;
            var configured = settings != null ? settings.GetPreMatchCountdownSeconds() : 5;
            return Mathf.Max(0, configured);
        }

        private bool GetInitialWaitingForPlayersForUi() {
            return _isWaitingForPlayers.Value;
        }

        private void OnPreMatchStateChanged(bool previous, bool current) {
            if(current) {
                _lastPublishedPreMatchSeconds = int.MinValue;
                EventBus.Publish(new PreMatchCountdownEvent(PreMatchCountdownSeconds));
                EventBus.Publish(new SetMatchTimeEvent(PreMatchCountdownSeconds));
                return;
            }

            // When pre-match ends, ensure UI shows match timer
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null && matchSettings.IsInfiniteMatchTimer()) {
                EventBus.Publish(new SetMatchTimeEvent(-1));
            } else {
                _lastPublishedMatchSeconds = int.MinValue;
                EventBus.Publish(new SetMatchTimeEvent(TimeRemainingSeconds));
            }
        }

        private void StartActiveMatchOnServer(string fromState) {
            if(!HasMatchAuthority) return;

            _isWaitingForPlayers.Value = false;
            _isPreMatch.Value = false;
            var matchSettings = MatchSettingsManager.Instance;
            matchDurationSeconds = 600;
            if(matchSettings != null) {
                matchDurationSeconds = matchSettings.GetMatchDurationSeconds();
            }

            _preMatchCountdownEndServerTimeSeconds.Value = 0d;
            _preMatchCountdownSeconds.Value = 0;
            _timeRemainingSeconds.Value = Mathf.Max(0, matchDurationSeconds);
            _activeMatchEndServerTimeSeconds.Value = CurrentServerTimeSeconds + _timeRemainingSeconds.Value;
            _lastPublishedMatchSeconds = int.MinValue;
            FlowLog.Emit(FlowEventIds.MatchStateTransition,
                ("from", fromState),
                ("to", "Active"),
                ("timeRemaining", TimeRemainingSeconds));

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
                _activeMatchEndServerTimeSeconds.Value = 0d;
                EventBus.Publish(new SetMatchTimeEvent(-1));
            }
        }

        private int GetComputedActiveTimeRemainingSeconds() {
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null && matchSettings.IsInfiniteMatchTimer()) {
                return -1;
            }

            if(!(_activeMatchEndServerTimeSeconds.Value > 0d)) return _timeRemainingSeconds.Value;
            var secondsRemaining = Mathf.CeilToInt((float)(_activeMatchEndServerTimeSeconds.Value - CurrentServerTimeSeconds));
            return Mathf.Max(0, secondsRemaining);

        }

        private int GetComputedPreMatchCountdownSeconds() {
            if(!(_preMatchCountdownEndServerTimeSeconds.Value > 0d)) return _preMatchCountdownSeconds.Value;
            var secondsRemaining = Mathf.CeilToInt((float)(_preMatchCountdownEndServerTimeSeconds.Value - CurrentServerTimeSeconds));
            return Mathf.Max(0, secondsRemaining);

        }

        /// <summary>
        /// Designates a random player as "it" after 5 seconds if no one is tagged yet (Tag mode only).
        /// </summary>
        private IEnumerator DesignateInitialItAfterDelay() {
            yield return new WaitForSeconds(5f);

            if(_hasDesignatedInitialIt || !HasMatchAuthority) yield break;

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || matchSettings.selectedGameModeId != "Gun Tag") yield break;

            _spawnedPlayerClientIds.Clear();
            _taggedPlayerClientIds.Clear();
            EventBus.Publish(new PlayerTagBootstrapSnapshotRequestedEvent());

            var allPlayers = _spawnedPlayerClientIds.ToList();
            if(allPlayers.Count == 0) yield break;

            var taggedPlayers = _taggedPlayerClientIds.ToList();

            var maxInitialTaggedPlayers = allPlayers.Count > 1 ? allPlayers.Count - 1 : 1;
            var configuredTaggedPlayers = Mathf.Clamp(matchSettings.taggedPlayers, 1, maxInitialTaggedPlayers);
            var additionalTaggedPlayersNeeded = configuredTaggedPlayers - taggedPlayers.Count;
            if(additionalTaggedPlayersNeeded <= 0) {
                _hasDesignatedInitialIt = true;
                yield break;
            }

            var untaggedPlayers = allPlayers.Where(playerClientId => !_taggedPlayerClientIds.Contains(playerClientId)).ToList();

            for(var i = 0; i < additionalTaggedPlayersNeeded && untaggedPlayers.Count > 0; i++) {
                var selectedIndex = Random.Range(0, untaggedPlayers.Count);
                var selectedPlayerClientId = untaggedPlayers[selectedIndex];
                untaggedPlayers.RemoveAt(selectedIndex);

                EventBus.Publish(new InitialTagDesignationRequestedEvent(selectedPlayerClientId));
            }

            _hasDesignatedInitialIt = true;
        }

        private void OnPlayerNetworkSpawned(PlayerNetworkSpawnedEvent evt) {
            if(evt == null) return;
            _spawnedPlayerClientIds.Add(evt.ClientId);
        }

        private void OnPlayerNetworkDespawned(PlayerNetworkDespawnedEvent evt) {
            if(evt == null) return;
            _spawnedPlayerClientIds.Remove(evt.ClientId);
            _taggedPlayerClientIds.Remove(evt.ClientId);
        }

        private void OnPlayerTagStateChanged(PlayerTagStateChangedEvent evt) {
            if(evt == null) return;
            _spawnedPlayerClientIds.Add(evt.PlayerId);

            if(evt.IsTagged) {
                _taggedPlayerClientIds.Add(evt.PlayerId);
            } else {
                _taggedPlayerClientIds.Remove(evt.PlayerId);
            }
        }

        private void OnTagBootstrapStateReported(PlayerTagBootstrapStateReportedEvent evt) {
            if(evt == null) return;
            _spawnedPlayerClientIds.Add(evt.PlayerClientId);

            if(evt.IsTagged) {
                _taggedPlayerClientIds.Add(evt.PlayerClientId);
            } else {
                _taggedPlayerClientIds.Remove(evt.PlayerClientId);
            }
        }
    }
}
