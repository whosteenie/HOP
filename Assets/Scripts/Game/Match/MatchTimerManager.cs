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

        private readonly NetworkVariable<MatchLifecycleState> _state = new();
        private readonly NetworkVariable<int> _timeRemainingSeconds = new(value: 0);
        private readonly NetworkVariable<int> _preMatchCountdownSeconds = new(value: 0);
        private readonly NetworkVariable<double> _preMatchCountdownEndServerTimeSeconds = new(value: 0d);
        private readonly NetworkVariable<double> _activeMatchEndServerTimeSeconds = new(value: 0d);

        public MatchLifecycleState CurrentState => _state.Value;
        public int TimeRemainingSeconds => GetComputedActiveTimeRemainingSeconds();
        public int PreMatchCountdownSeconds => GetComputedPreMatchCountdownSeconds();

        private Coroutine _stateRoutine;
        private bool _hasDesignatedInitialIt;
        private readonly HashSet<ulong> _clientsScenePresented = new();
        private readonly HashSet<ulong> _spawnedPlayerClientIds = new();
        private readonly HashSet<ulong> _taggedPlayerClientIds = new();
        private bool _sessionOwnerCallbacksRegistered;
        private int _lastPublishedMatchSeconds = int.MinValue;
        private int _lastPublishedPreMatchSeconds = int.MinValue;
        private bool _suppressStateChangedCallback;

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

            _state.OnValueChanged += OnLifecycleStateChanged;

            if(HasMatchAuthority) {
                EnterAuthoritativeMode(resetState: true, "OnNetworkSpawn");
            } else {
                PublishInitialUiFromState(CurrentState);
            }
        }

        private void Update() {
            if(!IsSpawned) return;

            switch(CurrentState) {
                case MatchLifecycleState.WaitingForPlayers:
                case MatchLifecycleState.Countdown: {
                    var currentPreMatchSeconds = PreMatchCountdownSeconds;
                    if(currentPreMatchSeconds == _lastPublishedPreMatchSeconds) return;

                    _lastPublishedPreMatchSeconds = currentPreMatchSeconds;
                    EventBus.Publish(new PreMatchCountdownEvent(currentPreMatchSeconds));
                    EventBus.Publish(new SetMatchTimeEvent(currentPreMatchSeconds));
                    return;
                }
                case MatchLifecycleState.Active: {
                    if(IsInfiniteMatchTimer()) return;

                    var currentSeconds = TimeRemainingSeconds;
                    if(currentSeconds == _lastPublishedMatchSeconds) return;

                    _lastPublishedMatchSeconds = currentSeconds;
                    EventBus.Publish(new SetMatchTimeEvent(currentSeconds));
                    return;
                }
                case MatchLifecycleState.Initializing:
                case MatchLifecycleState.PostMatch:
                default:
                    return;
            }
        }

        public override void OnNetworkDespawn() {
            if(NetworkManager != null && NetworkManager.DistributedAuthorityMode && !NetworkManager.ShutdownInProgress) {
                DevLog.LogWarning("[MatchTimerManager] Unexpected network despawn while DA session is still active.");
            }

            ExitAuthoritativeMode();
            base.OnNetworkDespawn();
            UnsubscribeGameplayEvents();
            _state.OnValueChanged -= OnLifecycleStateChanged;
            if(NetworkManager != null) {
                NetworkManager.OnClientDisconnectCallback -= OnPreMatchClientDisconnected;
            }
            UnregisterSessionOwnerCallbacks();
            ClearInstanceIfCurrent();
        }

        public override void OnDestroy() {
            base.OnDestroy();
            UnsubscribeGameplayEvents();
            _state.OnValueChanged -= OnLifecycleStateChanged;
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

            StopStateRoutine();

            if(resetState) {
                _hasDesignatedInitialIt = false;
                _timeRemainingSeconds.Value = 0;
                _preMatchCountdownEndServerTimeSeconds.Value = 0d;
                _activeMatchEndServerTimeSeconds.Value = 0d;

                if(ShouldUsePreMatchFlow()) {
                    EnterWaitingForPlayers(source);
                    return;
                }

                EnterActiveMatch(source);
                return;
            }

            ResumeCurrentState();
        }

        private void ExitAuthoritativeMode() {
            StopStateRoutine();

            if(NetworkManager != null) {
                NetworkManager.OnClientDisconnectCallback -= OnPreMatchClientDisconnected;
            }
        }

        private void ResumeCurrentState() {
            switch(CurrentState) {
                case MatchLifecycleState.Initializing:
                    if(ShouldUsePreMatchFlow()) {
                        EnterWaitingForPlayers("ResumeInitializing");
                    } else {
                        EnterActiveMatch("ResumeInitializing");
                    }
                    break;
                case MatchLifecycleState.WaitingForPlayers:
                    StartStateRoutineForState(CurrentState);
                    break;
                case MatchLifecycleState.Countdown:
                    if(_preMatchCountdownEndServerTimeSeconds.Value <= 0d && _preMatchCountdownSeconds.Value > 0) {
                        _preMatchCountdownEndServerTimeSeconds.Value =
                            CurrentServerTimeSeconds + _preMatchCountdownSeconds.Value;
                    }
                    StartStateRoutineForState(CurrentState);
                    break;
                case MatchLifecycleState.Active:
                    if(!IsInfiniteMatchTimer() && _activeMatchEndServerTimeSeconds.Value <= 0d) {
                        _activeMatchEndServerTimeSeconds.Value =
                            CurrentServerTimeSeconds + Mathf.Max(0, _timeRemainingSeconds.Value);
                    }
                    StartStateRoutineForState(CurrentState);
                    break;
                case MatchLifecycleState.PostMatch:
                    StopStateRoutine();
                    break;
            }
        }

        private void StopStateRoutine() {
            if(_stateRoutine == null) return;
            StopCoroutine(_stateRoutine);
            _stateRoutine = null;
        }

        private void StartStateRoutineForState(MatchLifecycleState state) {
            if(!HasMatchAuthority) return;

            StopStateRoutine();

            switch(state) {
                case MatchLifecycleState.WaitingForPlayers:
                    _stateRoutine = StartCoroutine(RunWaitingForPlayersRoutine());
                    break;
                case MatchLifecycleState.Countdown:
                    if(PreMatchCountdownSeconds > 0) {
                        _stateRoutine = StartCoroutine(RunCountdownRoutine());
                    } else {
                        EnterActiveMatch("CountdownWithoutSeconds");
                    }
                    break;
                case MatchLifecycleState.Active:
                    if(!IsInfiniteMatchTimer()) {
                        _stateRoutine = StartCoroutine(RunActiveMatchRoutine());
                    }
                    break;
                case MatchLifecycleState.Initializing:
                case MatchLifecycleState.PostMatch:
                default:
                    _stateRoutine = null;
                    break;
            }
        }

        private void EnterWaitingForPlayers(string source) {
            if(!HasMatchAuthority) return;

            _preMatchCountdownSeconds.Value = ResolveConfiguredPreMatchCountdownSeconds();
            _preMatchCountdownEndServerTimeSeconds.Value = 0d;
            _activeMatchEndServerTimeSeconds.Value = 0d;
            _timeRemainingSeconds.Value = 0;
            TransitionToState(MatchLifecycleState.WaitingForPlayers, source);
        }

        private void EnterCountdown(string source) {
            if(!HasMatchAuthority) return;

            var countdownSeconds = Mathf.Max(0, _preMatchCountdownSeconds.Value);
            if(countdownSeconds <= 0) {
                EnterActiveMatch(source);
                return;
            }

            _preMatchCountdownEndServerTimeSeconds.Value = CurrentServerTimeSeconds + countdownSeconds;
            _activeMatchEndServerTimeSeconds.Value = 0d;
            TransitionToState(MatchLifecycleState.Countdown, source);
        }

        private void EnterActiveMatch(string source) {
            if(!HasMatchAuthority) return;

            var matchSettings = MatchSettingsManager.Instance;
            matchDurationSeconds = matchSettings != null ? matchSettings.GetMatchDurationSeconds() : 600;

            _preMatchCountdownEndServerTimeSeconds.Value = 0d;
            _preMatchCountdownSeconds.Value = 0;
            _timeRemainingSeconds.Value = Mathf.Max(0, matchDurationSeconds);
            _activeMatchEndServerTimeSeconds.Value = IsInfiniteMatchTimer(matchSettings)
                ? 0d
                : CurrentServerTimeSeconds + _timeRemainingSeconds.Value;

            TransitionToState(MatchLifecycleState.Active, source);

            if(matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag") {
                StartCoroutine(DesignateInitialItAfterDelay());
            }
        }

        private void EnterPostMatch(string source) {
            if(!HasMatchAuthority) return;

            _timeRemainingSeconds.Value = Mathf.Max(0, TimeRemainingSeconds);
            _preMatchCountdownEndServerTimeSeconds.Value = 0d;
            _activeMatchEndServerTimeSeconds.Value = 0d;
            TransitionToState(MatchLifecycleState.PostMatch, source);
        }

        private void TransitionToState(MatchLifecycleState next, string source) {
            if(!HasMatchAuthority) return;

            var previous = CurrentState;
            if(previous == next) return;
            if(!IsValidTransition(previous, next)) {
                DevLog.LogError($"[MatchTimerManager] Invalid lifecycle transition {previous} -> {next} ({source}).");
                return;
            }

            StopStateRoutine();

            _suppressStateChangedCallback = true;
            _state.Value = next;
            _suppressStateChangedCallback = false;

            FlowLog.Emit(FlowEventIds.MatchStateTransition,
                ("from", previous.ToString()),
                ("to", next.ToString()),
                ("timeRemaining", GetTransitionTimeValue(next)),
                ("source", source));

            PublishStateTransition(previous, next);
            StartStateRoutineForState(next);
        }

        private static bool IsValidTransition(MatchLifecycleState previous, MatchLifecycleState next) {
            return previous switch {
                MatchLifecycleState.Initializing => next is MatchLifecycleState.WaitingForPlayers or
                    MatchLifecycleState.Countdown or MatchLifecycleState.Active,
                MatchLifecycleState.WaitingForPlayers => next is MatchLifecycleState.Countdown or MatchLifecycleState.Active,
                MatchLifecycleState.Countdown => next == MatchLifecycleState.Active,
                MatchLifecycleState.Active => next == MatchLifecycleState.PostMatch,
                MatchLifecycleState.PostMatch => false,
                _ => false
            };
        }

        private int GetTransitionTimeValue(MatchLifecycleState state) {
            return state switch {
                MatchLifecycleState.WaitingForPlayers or MatchLifecycleState.Countdown => PreMatchCountdownSeconds,
                MatchLifecycleState.Active or MatchLifecycleState.PostMatch => TimeRemainingSeconds,
                _ => 0
            };
        }

        private void PublishStateTransition(MatchLifecycleState previous, MatchLifecycleState current) {
            EventBus.Publish(new MatchLifecycleStateChangedEvent(previous, current));
            PublishStateEntryEvents(current);
        }

        private void PublishStateEntryEvents(MatchLifecycleState state) {
            switch(state) {
                case MatchLifecycleState.WaitingForPlayers:
                    PublishPreMatchUi(waitingForPlayers: true, PreMatchCountdownSeconds);
                    break;
                case MatchLifecycleState.Countdown:
                    PublishPreMatchUi(waitingForPlayers: false, PreMatchCountdownSeconds);
                    break;
                case MatchLifecycleState.Active:
                    PublishActiveUi();
                    EventBus.Publish(new MatchStartedEvent());
                    break;
                case MatchLifecycleState.PostMatch:
                    _lastPublishedMatchSeconds = int.MinValue;
                    _lastPublishedPreMatchSeconds = int.MinValue;
                    EventBus.Publish(new PreMatchWaitingForPlayersEvent(false));
                    EventBus.Publish(new MatchEndedEvent());
                    break;
                case MatchLifecycleState.Initializing:
                default:
                    _lastPublishedMatchSeconds = int.MinValue;
                    _lastPublishedPreMatchSeconds = int.MinValue;
                    break;
            }
        }

        private void PublishInitialUiFromState(MatchLifecycleState state) {
            switch(state) {
                case MatchLifecycleState.WaitingForPlayers:
                    PublishPreMatchUi(waitingForPlayers: true, ResolveInitialPreMatchCountdownSeconds());
                    break;
                case MatchLifecycleState.Countdown:
                    PublishPreMatchUi(waitingForPlayers: false, ResolveInitialPreMatchCountdownSeconds());
                    break;
                case MatchLifecycleState.Active:
                    PublishActiveUi();
                    break;
                case MatchLifecycleState.PostMatch:
                    EventBus.Publish(new PreMatchWaitingForPlayersEvent(false));
                    _lastPublishedMatchSeconds = int.MinValue;
                    _lastPublishedPreMatchSeconds = int.MinValue;
                    break;
                case MatchLifecycleState.Initializing:
                default:
                    PublishInitializingUiFallback();
                    break;
            }
        }

        private void PublishInitializingUiFallback() {
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null && matchSettings.IsPreMatchCountdownEnabled()) {
                PublishPreMatchUi(waitingForPlayers: false, Mathf.Max(0, matchSettings.GetPreMatchCountdownSeconds()));
                return;
            }

            if(matchSettings != null && matchSettings.IsInfiniteMatchTimer()) {
                EventBus.Publish(new PreMatchWaitingForPlayersEvent(false));
                EventBus.Publish(new SetMatchTimeEvent(-1));
                _lastPublishedMatchSeconds = -1;
                _lastPublishedPreMatchSeconds = int.MinValue;
                return;
            }

            var fallback = matchSettings != null ? matchSettings.GetMatchDurationSeconds() : Mathf.Max(0, matchDurationSeconds);
            EventBus.Publish(new PreMatchWaitingForPlayersEvent(false));
            EventBus.Publish(new SetMatchTimeEvent(Mathf.Max(0, fallback)));
            _lastPublishedMatchSeconds = Mathf.Max(0, fallback);
            _lastPublishedPreMatchSeconds = int.MinValue;
        }

        private void PublishPreMatchUi(bool waitingForPlayers, int seconds) {
            var clampedSeconds = Mathf.Max(0, seconds);
            EventBus.Publish(new PreMatchWaitingForPlayersEvent(waitingForPlayers));
            EventBus.Publish(new PreMatchCountdownEvent(clampedSeconds));
            EventBus.Publish(new SetMatchTimeEvent(clampedSeconds));
            _lastPublishedPreMatchSeconds = clampedSeconds;
            _lastPublishedMatchSeconds = int.MinValue;
        }

        private void PublishActiveUi() {
            EventBus.Publish(new PreMatchWaitingForPlayersEvent(false));
            var activeSeconds = TimeRemainingSeconds;
            EventBus.Publish(new SetMatchTimeEvent(activeSeconds));
            _lastPublishedMatchSeconds = activeSeconds;
            _lastPublishedPreMatchSeconds = int.MinValue;
        }

        private int ResolveInitialPreMatchCountdownSeconds() {
            var current = PreMatchCountdownSeconds;
            if(current > 0 || CurrentState == MatchLifecycleState.WaitingForPlayers) return Mathf.Max(0, current);
            return ResolveConfiguredPreMatchCountdownSeconds();
        }

        private static int ResolveConfiguredPreMatchCountdownSeconds() {
            var settings = MatchSettingsManager.Instance;
            var configured = settings != null ? settings.GetPreMatchCountdownSeconds() : 5;
            return Mathf.Max(0, configured);
        }

        private static bool ShouldUsePreMatchFlow() {
            var matchSettings = MatchSettingsManager.Instance;
            return matchSettings == null || matchSettings.IsPreMatchCountdownEnabled();
        }

        private static bool IsInfiniteMatchTimer() {
            return IsInfiniteMatchTimer(MatchSettingsManager.Instance);
        }

        private static bool IsInfiniteMatchTimer(MatchSettingsManager matchSettings) {
            return matchSettings != null && matchSettings.IsInfiniteMatchTimer();
        }

        private void OnLifecycleStateChanged(MatchLifecycleState previous, MatchLifecycleState current) {
            if(_suppressStateChangedCallback) return;
            PublishStateTransition(previous, current);
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

        private IEnumerator RunWaitingForPlayersRoutine() {
            var wait = new WaitForSeconds(1f);

            var expectedPlayers = 1;
            var session = SessionManager.Instance;
            if(session != null) {
                ISessionContext sessionCtx = session;
                expectedPlayers = Mathf.Max(1, sessionCtx.ExpectedGamePlayerCount);
            }

            const float expectedJoinGraceSeconds = 30f;
            const float maxWaitSeconds = 60f;
            var expectedCountLocked = false;
            var waitedSeconds = 0f;

            while(HasMatchAuthority && CurrentState == MatchLifecycleState.WaitingForPlayers && waitedSeconds < maxWaitSeconds) {
                var connectedClients = NetworkManager.Singleton.ConnectedClients;
                var connectedCount = connectedClients.Count;

                if(!expectedCountLocked && waitedSeconds >= expectedJoinGraceSeconds && connectedCount < expectedPlayers) {
                    expectedPlayers = Mathf.Max(connectedCount, 1);
                    expectedCountLocked = true;
                    DevLog.LogWarning(
                        $"[MatchTimerManager] Expected-player grace expired. Continuing with {expectedPlayers} expected connected players.");
                }

                var haveExpectedConnections = connectedCount >= expectedPlayers;

                var allPlayersReady = true;
                foreach(var kvp in connectedClients) {
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

                if(haveExpectedConnections && allPlayersReady && allConnectedPresented && connectedCount > 0) {
                    DevLog.Log(
                        $"[MatchTimerManager] All {connectedCount}/{expectedPlayers} expected players connected, spawned, and scene-presented. Starting countdown.");
                    break;
                }

                DevLog.Log(
                    $"[MatchTimerManager] Waiting for players... expected={expectedPlayers} connected={connectedCount} spawnedReady={allPlayersReady} presented={_clientsScenePresented.Count}");
                yield return wait;
                waitedSeconds += 1f;
            }

            if(waitedSeconds >= maxWaitSeconds) {
                DevLog.LogWarning("[MatchTimerManager] Timed out waiting for all players.");
            }

            _stateRoutine = null;
            if(!HasMatchAuthority || CurrentState != MatchLifecycleState.WaitingForPlayers) yield break;

            if(_preMatchCountdownSeconds.Value > 0) {
                EnterCountdown("WaitingForPlayersComplete");
            } else {
                EnterActiveMatch("WaitingForPlayersComplete");
            }
        }

        private IEnumerator RunCountdownRoutine() {
            while(HasMatchAuthority && CurrentState == MatchLifecycleState.Countdown && PreMatchCountdownSeconds > 0) {
                yield return null;
            }

            _stateRoutine = null;
            if(!HasMatchAuthority || CurrentState != MatchLifecycleState.Countdown) yield break;
            EnterActiveMatch("CountdownComplete");
        }

        private IEnumerator RunActiveMatchRoutine() {
            while(HasMatchAuthority && CurrentState == MatchLifecycleState.Active) {
                if(_activeMatchEndServerTimeSeconds.Value <= 0d) {
                    _stateRoutine = null;
                    yield break;
                }

                if(CurrentServerTimeSeconds >= _activeMatchEndServerTimeSeconds.Value) break;
                yield return null;
            }

            _stateRoutine = null;
            if(!HasMatchAuthority || CurrentState != MatchLifecycleState.Active) yield break;
            EnterPostMatch("ActiveTimerExpired");
        }

        private int GetComputedActiveTimeRemainingSeconds() {
            if(IsInfiniteMatchTimer()) {
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

            if(_hasDesignatedInitialIt || !HasMatchAuthority || CurrentState != MatchLifecycleState.Active) yield break;

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
