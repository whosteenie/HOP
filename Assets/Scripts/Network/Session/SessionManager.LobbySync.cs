using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Network.Diagnostics;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;

namespace Network.Session {
    public sealed partial class SessionManager {
        private float _nextUgsHeartbeatTime;
        // Scheduler tick (not the per-lobby heartbeat interval).
        private const float UgsHeartbeatIntervalSeconds = 1f;
        private const float PartyHeartbeatIntervalSeconds = 20f;
        private const float MatchHeartbeatIntervalSeconds = 15f;
        private const float HeartbeatInitialDelaySeconds = 1f;
        private const float HeartbeatStaggerSeconds = 5f;
        private bool _ugsSyncInProgress;
        private bool _ugsLocalReadySubmitted;
        private bool _ugsClientStartedForMatch;
        private bool _ugsHostPreFadedOut;
        private bool _isRetryingReadySubmission;
        private bool _isFollowingMatchLobby;
        private string _lastFailedFollowMatchLobbyId;
        private float _nextUgsHeartbeatFailureLogTime;
        private float _nextUgsSyncRateLimitLogTime;
        private float _nextUgsClientStartFailureLogTime;
        private float _nextLobbyEventSubscriptionFailureLogTime;
        private float _nextLobbyEventConnectionLogTime;
        private float _partyHeartbeatBackoffUntil;
        private float _matchHeartbeatBackoffUntil;
        private int _partyHeartbeatRateLimitStreak;
        private int _matchHeartbeatRateLimitStreak;
        private float _nextPartyHeartbeatRateLimitWarnTime;
        private float _nextMatchHeartbeatRateLimitWarnTime;
        private bool _isHeartbeatDispatchInFlight;
        private float _nextPartyHeartbeatTime;
        private float _nextMatchHeartbeatTime;
        private string _lastPartyHeartbeatLobbyId;
        private string _lastMatchHeartbeatLobbyId;
        private bool _isSubscribingPartyLobbyEvents;
        private bool _isSubscribingMatchLobbyEvents;
        private bool _isResubscribingPartyLobbyEvents;
        private bool _isResubscribingMatchLobbyEvents;
        private bool _isRetryingPartyLobbyEventsSubscription;
        private bool _isRetryingMatchLobbyEventsSubscription;
        private bool _isRetryingDistributedAuthorityJoin;
        private int _partyLobbyEventsSubscriptionRetryAttempt;
        private int _matchLobbyEventsSubscriptionRetryAttempt;
        private int _distributedAuthorityJoinRetryAttempt;
        private ILobbyEvents _partyLobbyEvents;
        private ILobbyEvents _matchLobbyEvents;
        private LobbyEventCallbacks _partyLobbyEventCallbacks;
        private LobbyEventCallbacks _matchLobbyEventCallbacks;
        private string _partyLobbyEventsLobbyId;
        private string _matchLobbyEventsLobbyId;
        private string _distributedAuthorityRetrySessionCode;
        private UniTaskCompletionSource<bool> _playersReadyWaiter;
        private List<string> _playersReadyExpectedPlayerIds;
        private string _playersReadyLobbyId;

        private const int HeartbeatRateLimitWarnStreak = 3;
        private const float HeartbeatRateLimitWarnIntervalSeconds = 30f;
        private const float HeartbeatRateLimitBaseBackoffSeconds = 10f;
        private const float HeartbeatRateLimitMaxBackoffSeconds = 90f;
        private const int LobbyEventSubscriptionRetryBaseDelayMs = 500;
        private const int LobbyEventSubscriptionRetryMaxDelayMs = 10000;
        private const int LobbyEventSubscriptionRetryMaxExponent = 5;
        private const int LobbyEventSubscriptionRetryJitterMs = 250;
        private const float DistributedAuthorityJoinRetryBaseDelaySeconds = 2f;
        private const float DistributedAuthorityJoinRetryMaxDelaySeconds = 20f;
        private const int DistributedAuthorityJoinRetryMaxExponent = 4;

        private float _nextDistributedAuthorityJoinRetryTime;
        private float _nextDistributedAuthorityJoinRateLimitLogTime;
        private float _nextBackfillEligibilityRefreshTime;
        private bool? _lastPublishedBackfillAllowed;
        private string _lastPublishedBackfillReason;
        private bool _isBackfillEligibilityUpdateInFlight;
        private const float BackfillEligibilityRefreshIntervalSeconds = 10f;

        private void Update() {
            if(_isLeaving || _isShuttingDown) return;
            if(_ugsPartyLobby == null && _ugsMatchLobby == null) return;

            if(Phase == SessionPhase.SynchronizingLoad && Time.time - _phaseStartTime > 30f) {
                Debug.LogError("[SessionManager] Stuck in SynchronizingLoad for >30s. Aborting to menu...");
                FlowLog.Emit(FlowEventIds.AnomalySessionStuck, ("phase", Phase), ("elapsed", Time.time - _phaseStartTime));
                LaunchSessionTask(LeaveToMainMenuAsync(), "SynchronizingLoadWatchdog/LeaveToMainMenu");
                return;
            }

            if(!(Time.unscaledTime >= _nextUgsHeartbeatTime)) return;
            _nextUgsHeartbeatTime = Time.unscaledTime + UgsHeartbeatIntervalSeconds;
            if(!_isBackfillEligibilityUpdateInFlight) {
                LaunchSessionTask(RefreshPublicMatchBackfillEligibilityAsync(), "RefreshPublicMatchBackfillEligibility");
            }
            if(!_isHeartbeatDispatchInFlight) {
                LaunchSessionTask(SendPartyHeartbeatsAsync(), "SendPartyHeartbeats");
            }
        }

        private async UniTask RefreshPublicMatchBackfillEligibilityAsync(bool force = false) {
            if(_isBackfillEligibilityUpdateInFlight || _isLeaving || _isShuttingDown) {
                return;
            }

            if(Phase != SessionPhase.InGame || _ugsMatchLobby?.Data == null) {
                return;
            }

            if(!_ugsMatchLobby.Data.TryGetValue(UgsMatchTypeKey, out var matchTypeObj) || matchTypeObj == null ||
               !string.Equals(matchTypeObj.Value, "Public", StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            var localId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localId) || !IsLobbyHostForLocalPlayer(_ugsMatchLobby, localId)) {
                return;
            }

            if(!force && Time.unscaledTime < _nextBackfillEligibilityRefreshTime) {
                return;
            }

            var (allowed, reason) = EvaluatePublicMatchBackfillEligibility();
            if(!force &&
               _lastPublishedBackfillAllowed == allowed &&
               string.Equals(_lastPublishedBackfillReason, reason, StringComparison.Ordinal)) {
                _nextBackfillEligibilityRefreshTime = Time.unscaledTime + BackfillEligibilityRefreshIntervalSeconds;
                return;
            }

            _isBackfillEligibilityUpdateInFlight = true;
            try {
                if(await TryUpdatePublicMatchBackfillEligibilityAsync(allowed, reason, "HeartbeatRefresh")) {
                    _lastPublishedBackfillAllowed = allowed;
                    _lastPublishedBackfillReason = reason;
                }
            } finally {
                _nextBackfillEligibilityRefreshTime = Time.unscaledTime + BackfillEligibilityRefreshIntervalSeconds;
                _isBackfillEligibilityUpdateInFlight = false;
            }
        }

        private UniTaskCompletionSource<bool> ArmPlayersReadyWaiter(string lobbyId, List<string> expectedPlayerIds) {
            CompleteAndClearPlayersReadyWaiter(false);
            _playersReadyWaiter = new UniTaskCompletionSource<bool>();
            _playersReadyLobbyId = lobbyId;
            _playersReadyExpectedPlayerIds = expectedPlayerIds != null ? new List<string>(expectedPlayerIds) : null;
            return _playersReadyWaiter;
        }

        private void ClearPlayersReadyWaiter(UniTaskCompletionSource<bool> waiter) {
            if(waiter == null || _playersReadyWaiter != waiter) return;
            _playersReadyWaiter = null;
            _playersReadyExpectedPlayerIds = null;
            _playersReadyLobbyId = null;
        }

        private void CompleteAndClearPlayersReadyWaiter(bool result) {
            var waiter = _playersReadyWaiter;
            if(waiter == null) return;
            _playersReadyWaiter = null;
            _playersReadyExpectedPlayerIds = null;
            _playersReadyLobbyId = null;
            waiter.TrySetResult(result);
        }

        private void TryCompletePlayersReadyWaiterFromLobby(Lobby lobby) {
            if(_playersReadyWaiter == null || lobby == null) return;
            if(string.IsNullOrEmpty(_playersReadyLobbyId) || string.IsNullOrEmpty(lobby.Id)) return;
            if(!string.Equals(lobby.Id, _playersReadyLobbyId, StringComparison.Ordinal)) return;
            if(!AreAllExpectedPlayersReady(lobby, _playersReadyExpectedPlayerIds)) return;
            _playersReadyWaiter.TrySetResult(true);
        }

        private async UniTask<bool> WaitForMatchPlayersReadyAsync(List<string> expectedPlayerIds, float timeoutSeconds,
            string contextLabel) {
            if(_isLeaving || _isShuttingDown || _ugsMatchLobby == null) return false;
            if(expectedPlayerIds == null || expectedPlayerIds.Count == 0) return true;
            if(AreAllExpectedPlayersReady(_ugsMatchLobby, expectedPlayerIds)) return true;

            var waiter = ArmPlayersReadyWaiter(_ugsMatchLobby.Id, expectedPlayerIds);
            TryCompletePlayersReadyWaiterFromLobby(_ugsMatchLobby);

            var ready = false;
            async UniTask WaitForReadyAsync() {
                ready = await waiter.Task;
            }

            try {
                var winner = await UniTask.WhenAny(
                    WaitForReadyAsync(),
                    UniTask.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken: SessionLifetimeToken));

                if(winner == 0 && ready) return true;
                if(Debug.isDebugBuild) {
                    Debug.LogWarning($"[SessionManager] Timed out waiting for players ready ({contextLabel}).");
                }
                return false;
            } catch(OperationCanceledException) {
                return false;
            } finally {
                ClearPlayersReadyWaiter(waiter);
            }
        }

        private async UniTask<bool> WaitForPrivateMatchSyncReadyAsync(List<string> expectedPlayers) {
            return await WaitForMatchPlayersReadyAsync(expectedPlayers, 20f, "PrivateMatch");
        }

        private async UniTask<bool> WaitForPublicMatchPlayersReadyAsync(List<string> expectedPlayerIds) {
            return await WaitForMatchPlayersReadyAsync(expectedPlayerIds, 60f, "PublicMatch");
        }

        private static bool IsLocalPlayerLobbyHost(Lobby lobby) {
            if(lobby == null) return false;
            var localUgsId = AuthenticationService.Instance.PlayerId;
            return !string.IsNullOrEmpty(localUgsId) && string.Equals(lobby.HostId, localUgsId, StringComparison.Ordinal);
        }

        private static bool IsPrivateMatchLobby(Lobby lobby) {
            if(lobby?.Data == null) {
                return false;
            }

            return lobby.Data.TryGetValue(UgsMatchTypeKey, out var matchTypeObj) &&
                   matchTypeObj != null &&
                   string.Equals(matchTypeObj.Value, "Private", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetDistributedAuthoritySessionCode(Lobby lobby, out string sessionCode) {
            sessionCode = null;
            if(lobby?.Data == null) {
                return false;
            }

            if(lobby.Data.TryGetValue(UgsRelayJoinCodeKey, out var joinCodeObj) == false || joinCodeObj == null) {
                return false;
            }

            sessionCode = joinCodeObj.Value;
            return string.IsNullOrWhiteSpace(sessionCode) == false;
        }

        private void ResetDistributedAuthorityJoinRetryState() {
            _isRetryingDistributedAuthorityJoin = false;
            _distributedAuthorityJoinRetryAttempt = 0;
            _distributedAuthorityRetrySessionCode = null;
            _nextDistributedAuthorityJoinRetryTime = 0f;
        }

        private void RefreshDistributedAuthorityJoinRetrySessionCode(string sessionCode) {
            if(string.IsNullOrWhiteSpace(sessionCode)) {
                return;
            }

            if(string.Equals(_distributedAuthorityRetrySessionCode, sessionCode, StringComparison.Ordinal)) {
                return;
            }

            ResetDistributedAuthorityJoinRetryState();
            _distributedAuthorityRetrySessionCode = sessionCode;
        }

        private bool IsDistributedAuthorityJoinRetryBackoffActive(string sessionCode) {
            if(string.IsNullOrWhiteSpace(sessionCode)) {
                return false;
            }

            RefreshDistributedAuthorityJoinRetrySessionCode(sessionCode);
            return Time.unscaledTime < _nextDistributedAuthorityJoinRetryTime;
        }

        private void ScheduleDistributedAuthorityJoinRetry(string sessionCode, bool isPrivateMatch) {
            if(_isLeaving || _isShuttingDown || string.IsNullOrWhiteSpace(sessionCode)) {
                return;
            }

            RefreshDistributedAuthorityJoinRetrySessionCode(sessionCode);

            var delaySeconds = ComputeDistributedAuthorityJoinRetryDelaySeconds(_distributedAuthorityJoinRetryAttempt);
            _distributedAuthorityJoinRetryAttempt++;
            _nextDistributedAuthorityJoinRetryTime = Time.unscaledTime + delaySeconds;

            if(ShouldEmitThrottledLog(ref _nextDistributedAuthorityJoinRateLimitLogTime, 3f)) {
                Debug.LogWarning(
                    $"[SessionManager] Backing off DA join for {delaySeconds:0.0}s before retrying session '{sessionCode}'.");
            }

            if(_isRetryingDistributedAuthorityJoin) {
                return;
            }

            _isRetryingDistributedAuthorityJoin = true;
            LaunchSessionTask(RetryStartMatchClientAsync(sessionCode, isPrivateMatch), "DistributedAuthority/RetryJoin");
        }

        private async UniTask RetryStartMatchClientAsync(string sessionCode, bool isPrivateMatch) {
            try {
                while(!_isLeaving && !_isShuttingDown) {
                    if(_ugsMatchLobby == null) {
                        return;
                    }

                    if(string.Equals(_distributedAuthorityRetrySessionCode, sessionCode, StringComparison.Ordinal) ==
                       false) {
                        return;
                    }

                    if(TryGetDistributedAuthoritySessionCode(_ugsMatchLobby, out var currentSessionCode) == false ||
                       string.Equals(currentSessionCode, sessionCode, StringComparison.Ordinal) == false) {
                        return;
                    }

                    var remainingDelay = _nextDistributedAuthorityJoinRetryTime - Time.unscaledTime;
                    if(remainingDelay > 0f) {
                        try {
                            await UniTask.Delay(TimeSpan.FromSeconds(remainingDelay), cancellationToken: SessionLifetimeToken);
                        } catch(OperationCanceledException) {
                            return;
                        }
                    }

                    _isRetryingDistributedAuthorityJoin = false;
                    await StartMatchClientAsync(useFadeOut: false, expectedSessionCode: sessionCode,
                        expectedIsPrivateMatch: isPrivateMatch);
                    return;
                }
            } finally {
                _isRetryingDistributedAuthorityJoin = false;
            }
        }

        private static float ComputeDistributedAuthorityJoinRetryDelaySeconds(int attempt) {
            var exponent = Mathf.Clamp(attempt, 0, DistributedAuthorityJoinRetryMaxExponent);
            var rawBackoff = DistributedAuthorityJoinRetryBaseDelaySeconds * Mathf.Pow(2f, exponent);
            var jitter = UnityEngine.Random.Range(0f, 1.25f);
            return Mathf.Min(DistributedAuthorityJoinRetryMaxDelaySeconds, rawBackoff + jitter);
        }

        private async UniTask HandlePartyLobbyFollowStateAsync(string source) {
            if(_isLeaving || _isShuttingDown || Phase == SessionPhase.InGame || _isFollowingMatchLobby) return;
            if(_ugsPartyLobby?.Data == null) return;
            if(!_ugsPartyLobby.Data.TryGetValue(UgsFollowMatchLobbyIdKey, out var followObj) || followObj == null ||
               string.IsNullOrEmpty(followObj.Value)) {
                _lastFailedFollowMatchLobbyId = null;
                return;
            }

            var followLobbyId = followObj.Value;
            if(_lastFailedFollowMatchLobbyId == followLobbyId) return;
            if(_ugsMatchLobby != null && string.Equals(_ugsMatchLobby.Id, followLobbyId, StringComparison.Ordinal)) return;

            _isFollowingMatchLobby = true;
            try {
                var joined = await JoinMatchLobbyByIdAsync(followLobbyId);
                _lastFailedFollowMatchLobbyId = joined ? null : followLobbyId;
                if(!joined && Debug.isDebugBuild) {
                    Debug.LogWarning($"[SessionManager] Failed to follow match lobby '{followLobbyId}' ({source}).");
                }
            } catch(Exception ex) {
                _lastFailedFollowMatchLobbyId = followLobbyId;
                Debug.LogWarning($"[SessionManager] Failed to follow match lobby '{followLobbyId}' ({source}): {ex.Message}");
            } finally {
                _isFollowingMatchLobby = false;
            }
        }

        private void HandleMatchLobbySnapshot(string source) {
            if(_isLeaving || _isShuttingDown || _ugsMatchLobby == null || Phase == SessionPhase.InGame) return;
            SyncModeFromMatchLobby(_ugsMatchLobby);
            TryCompletePlayersReadyWaiterFromLobby(_ugsMatchLobby);
            if(_ugsMatchLobby.Data == null) return;

            TryGetDistributedAuthoritySessionCode(_ugsMatchLobby, out var sessionCode);
            RefreshDistributedAuthorityJoinRetrySessionCode(sessionCode);

            if(!_ugsMatchLobby.Data.TryGetValue(UgsLobbyStateKey, out var stateObj) || stateObj == null ||
               string.IsNullOrEmpty(stateObj.Value)) return;

            switch(stateObj.Value) {
                case "SynchronizingLoad":
                    if(!_ugsLocalReadySubmitted) {
                        LaunchSessionTask(StartMatchSynchronizationAsync(), $"{source}/SynchronizingLoad");
                    }
                    return;
                case "LoadingScene":
                    if(IsLocalPlayerLobbyHost(_ugsMatchLobby)) return;
                    if(IsDistributedAuthorityJoinRetryBackoffActive(sessionCode)) return;
                    if(!_ugsClientStartedForMatch) {
                        LaunchSessionTask(StartMatchClientAsync(expectedSessionCode: sessionCode),
                            $"{source}/LoadingScene");
                    }
                    return;
                case "InGame":
                    if(IsLocalPlayerLobbyHost(_ugsMatchLobby)) return;
                    _ugsLocalReadySubmitted = true;
                    if(IsDistributedAuthorityJoinRetryBackoffActive(sessionCode)) return;
                    if(!_ugsClientStartedForMatch) {
                        LaunchSessionTask(StartMatchClientAsync(useFadeOut: true, expectedSessionCode: sessionCode),
                            $"{source}/InGame");
                    }
                    return;
            }
        }

        private void SyncModeFromMatchLobby(Lobby lobby) {
            if(_isLeaving || _isShuttingDown || lobby?.Data == null) return;
            if(!lobby.Data.TryGetValue(UgsTargetModeKey, out var modeObj) || modeObj == null ||
               string.IsNullOrEmpty(modeObj.Value)) return;
            ApplyRuntimeMode(modeObj.Value, "UgsMatchLobbySync", refreshUi: false);
        }

        private async UniTask StartMatchSynchronizationAsync(bool skipFadeOut = false) {
            if(_ugsMatchLobby == null || _ugsLocalReadySubmitted || _ugsSyncInProgress) return;

            _ugsSyncInProgress = true;
            Phase = SessionPhase.SynchronizingLoad;
            SetFrontStatus(SessionPhase.SynchronizingLoad, "Waiting for party...");

            if(_ugsHostPreFadedOut) {
                _ugsHostPreFadedOut = false;
            } else if(!skipFadeOut) {
                await FadeOutWithFallbackAsync();
            }

            var localUgsId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localUgsId)) {
                Debug.LogError("[SessionManager] Cannot submit ready state: local UGS player id is missing.");
                _ugsSyncInProgress = false;
                return;
            }

            try {
                var opts = BuildReadyToLoadUpdatePlayerOptions();
                _ugsMatchLobby = await LobbyService.Instance.UpdatePlayerAsync(_ugsMatchLobby.Id, localUgsId, opts);
                _ugsLocalReadySubmitted = true;
                TryCompletePlayersReadyWaiterFromLobby(_ugsMatchLobby);
            } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                if(ShouldEmitThrottledLog(ref _nextUgsSyncRateLimitLogTime, 5f)) {
                    Debug.LogWarning("[SessionManager] Rate limited updating ready state. Retrying shortly...");
                }
                LaunchSessionTask(RetrySubmitReadyStateAsync(), "StartMatchSynchronization/RetryReady");
            } catch(Exception ex) {
                Debug.LogError($"[SessionManager] Failed to update ready state: {ex.Message}. Aborting to menu...");
                await LeaveToMainMenuAsync();
            } finally {
                _ugsSyncInProgress = false;
            }
        }

        private async UniTask RetrySubmitReadyStateAsync() {
            if(_isRetryingReadySubmission) return;
            _isRetryingReadySubmission = true;
            try {
                var retryDelayMs = 1000;
                while(!_isLeaving && !_isShuttingDown && _ugsMatchLobby != null && !_ugsLocalReadySubmitted) {
                    try {
                        await UniTask.Delay(retryDelayMs, cancellationToken: SessionLifetimeToken);
                    } catch(OperationCanceledException) {
                        return;
                    }

                    if(_ugsSyncInProgress) {
                        retryDelayMs = Mathf.Min(retryDelayMs + 500, 5000);
                        continue;
                    }

                    await StartMatchSynchronizationAsync(skipFadeOut: true);
                    retryDelayMs = Mathf.Min(retryDelayMs + 500, 5000);
                }
            } finally {
                _isRetryingReadySubmission = false;
            }
        }

        private static bool AreAllExpectedPlayersReady(Lobby lobby, List<string> expectedPlayerIds) {
            if(lobby == null) return false;
            if(expectedPlayerIds == null || expectedPlayerIds.Count == 0) return true;
            if(lobby.Players == null) return false;

            foreach(var id in expectedPlayerIds) {
                if(string.IsNullOrEmpty(id)) continue;

                Player found = null;
                foreach(var p in lobby.Players) {
                    if(p == null || p.Id != id) continue;
                    found = p;
                    break;
                }

                if(found?.Data == null) return false;
                if(!found.Data.TryGetValue(UgsMemberReadyKey, out var readyObj) || readyObj == null) return false;
                if(readyObj.Value != "1") return false;
            }

            return true;
        }

        private async UniTask StartMatchClientAsync(bool useFadeOut = false, string expectedSessionCode = null,
            bool? expectedIsPrivateMatch = null) {
            if(_ugsMatchLobby == null || _ugsClientStartedForMatch || !_ugsLocalReadySubmitted) return;
            if(_isLeaving || _isShuttingDown) return;

            if(_ugsMatchLobby.Data == null) {
                if(ShouldEmitThrottledLog(ref _nextUgsClientStartFailureLogTime, 10f)) {
                    Debug.LogWarning("[SessionManager] Cannot start match client: match lobby data is unavailable.");
                }
                return;
            }

            if(TryGetDistributedAuthoritySessionCode(_ugsMatchLobby, out var sessionCode) == false) {
                if(ShouldEmitThrottledLog(ref _nextUgsClientStartFailureLogTime, 10f)) {
                    Debug.LogWarning("[SessionManager] Cannot start match client: DA session code has not been published.");
                }
                return;
            }

            RefreshDistributedAuthorityJoinRetrySessionCode(sessionCode);
            if(IsDistributedAuthorityJoinRetryBackoffActive(sessionCode)) {
                return;
            }
            if(string.IsNullOrWhiteSpace(expectedSessionCode) == false &&
               string.Equals(expectedSessionCode, sessionCode, StringComparison.Ordinal) == false) {
                return;
            }

            SyncModeFromMatchLobby(_ugsMatchLobby);

            _ugsClientStartedForMatch = true;
            var shouldResetClientStartFlag = true;

            try {
                Phase = SessionPhase.StartingClient;

                if(useFadeOut) {
                    await FadeOutWithFallbackAsync();
                    if(_isLeaving || _isShuttingDown) {
                        return;
                    }
                }

                var isPrivateMatch = expectedIsPrivateMatch ?? IsPrivateMatchLobby(_ugsMatchLobby);
                var joinResult =
                    await JoinDistributedAuthoritySessionAsync(sessionCode, isPrivateMatch, "StartMatchClientAsync");
                switch(joinResult) {
                    case DistributedAuthoritySessionJoinResult.Success:
                        ResetDistributedAuthorityJoinRetryState();
                        shouldResetClientStartFlag = false;
                        return;
                    case DistributedAuthoritySessionJoinResult.RateLimited:
                        ScheduleDistributedAuthorityJoinRetry(sessionCode, isPrivateMatch);
                        return;
                    default:
                        Debug.LogError("[SessionManager] Failed to start DA match client after cleanup.");
                        ResetDistributedAuthorityJoinRetryState();
                        await LeaveToMainMenuAsync();
                        return;
                }
            } finally {
                if(shouldResetClientStartFlag) {
                    _ugsClientStartedForMatch = false;
                }
            }
        }

        private async UniTask EnsurePartyLobbyEventsSubscriptionAsync(string context) {
            if(_ugsPartyLobby == null || string.IsNullOrEmpty(_ugsPartyLobby.Id)) {
                await UnsubscribePartyLobbyEventsAsync($"{context}/NoPartyLobby");
                return;
            }

            var targetLobbyId = _ugsPartyLobby.Id;
            if(_partyLobbyEvents != null && string.Equals(_partyLobbyEventsLobbyId, targetLobbyId, StringComparison.Ordinal)) {
                return;
            }
            if(_isSubscribingPartyLobbyEvents) return;

            _isSubscribingPartyLobbyEvents = true;
            try {
                await UnsubscribePartyLobbyEventsAsync($"{context}/Replace");

                var callbacks = new LobbyEventCallbacks();
                callbacks.LobbyChanged += OnPartyLobbyChanged;
                callbacks.LobbyDeleted += OnPartyLobbyDeleted;
                callbacks.KickedFromLobby += OnPartyLobbyKicked;
                callbacks.LobbyEventConnectionStateChanged += OnPartyLobbyEventConnectionStateChanged;

                _partyLobbyEvents = await LobbyService.Instance.SubscribeToLobbyEventsAsync(targetLobbyId, callbacks);
                _partyLobbyEventsLobbyId = targetLobbyId;
                _partyLobbyEventCallbacks = callbacks;
                _partyLobbyEventsSubscriptionRetryAttempt = 0;
                await HandlePartyLobbyFollowStateAsync($"{context}/Initial");

                if(Debug.isDebugBuild) {
                    Debug.Log($"[SessionManager] Subscribed to party lobby events ({context}) lobbyId='{targetLobbyId}'.");
                }
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f)) {
                    Debug.LogWarning($"[SessionManager] Failed to subscribe to party lobby events ({context}): {ex.Message}");
                }
                SchedulePartyLobbyEventsSubscriptionRetry(context);
            } finally {
                _isSubscribingPartyLobbyEvents = false;
            }
        }

        private async UniTask EnsureMatchLobbyEventsSubscriptionAsync(string context) {
            if(_ugsMatchLobby == null || string.IsNullOrEmpty(_ugsMatchLobby.Id)) {
                await UnsubscribeMatchLobbyEventsAsync($"{context}/NoMatchLobby");
                return;
            }

            var targetLobbyId = _ugsMatchLobby.Id;
            if(_matchLobbyEvents != null && string.Equals(_matchLobbyEventsLobbyId, targetLobbyId, StringComparison.Ordinal)) {
                return;
            }
            if(_isSubscribingMatchLobbyEvents) return;

            _isSubscribingMatchLobbyEvents = true;
            try {
                await UnsubscribeMatchLobbyEventsAsync($"{context}/Replace");

                var callbacks = new LobbyEventCallbacks();
                callbacks.LobbyChanged += OnMatchLobbyChanged;
                callbacks.LobbyDeleted += OnMatchLobbyDeleted;
                callbacks.KickedFromLobby += OnMatchLobbyKicked;
                callbacks.LobbyEventConnectionStateChanged += OnMatchLobbyEventConnectionStateChanged;

                _matchLobbyEvents = await LobbyService.Instance.SubscribeToLobbyEventsAsync(targetLobbyId, callbacks);
                _matchLobbyEventsLobbyId = targetLobbyId;
                _matchLobbyEventCallbacks = callbacks;
                _matchLobbyEventsSubscriptionRetryAttempt = 0;

                if(Debug.isDebugBuild) {
                    Debug.Log($"[SessionManager] Subscribed to match lobby events ({context}) lobbyId='{targetLobbyId}'.");
                }
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f)) {
                    Debug.LogWarning($"[SessionManager] Failed to subscribe to match lobby events ({context}): {ex.Message}");
                }
                ScheduleMatchLobbyEventsSubscriptionRetry(context);
            } finally {
                _isSubscribingMatchLobbyEvents = false;
            }
        }

        private async UniTask UnsubscribePartyLobbyEventsAsync(string context) {
            var callbacks = _partyLobbyEventCallbacks;
            var eventsHandle = _partyLobbyEvents;

            if(callbacks != null) {
                callbacks.LobbyChanged -= OnPartyLobbyChanged;
                callbacks.LobbyDeleted -= OnPartyLobbyDeleted;
                callbacks.KickedFromLobby -= OnPartyLobbyKicked;
                callbacks.LobbyEventConnectionStateChanged -= OnPartyLobbyEventConnectionStateChanged;
            }

            _partyLobbyEventCallbacks = null;
            _partyLobbyEvents = null;
            _partyLobbyEventsLobbyId = null;
            _isResubscribingPartyLobbyEvents = false;

            if(eventsHandle == null) return;

            try {
                await eventsHandle.UnsubscribeAsync();
            } catch(Exception ex) {
                if(Debug.isDebugBuild && ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f)) {
                    Debug.LogWarning($"[SessionManager] Failed to unsubscribe from party lobby events ({context}): {ex.Message}");
                }
            }
        }

        private async UniTask UnsubscribeMatchLobbyEventsAsync(string context) {
            var callbacks = _matchLobbyEventCallbacks;
            var eventsHandle = _matchLobbyEvents;

            if(callbacks != null) {
                callbacks.LobbyChanged -= OnMatchLobbyChanged;
                callbacks.LobbyDeleted -= OnMatchLobbyDeleted;
                callbacks.KickedFromLobby -= OnMatchLobbyKicked;
                callbacks.LobbyEventConnectionStateChanged -= OnMatchLobbyEventConnectionStateChanged;
            }

            _matchLobbyEventCallbacks = null;
            _matchLobbyEvents = null;
            _matchLobbyEventsLobbyId = null;
            _isResubscribingMatchLobbyEvents = false;
            CompleteAndClearPlayersReadyWaiter(false);

            if(eventsHandle == null) return;

            try {
                await eventsHandle.UnsubscribeAsync();
            } catch(Exception ex) {
                if(Debug.isDebugBuild && ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f)) {
                    Debug.LogWarning($"[SessionManager] Failed to unsubscribe from match lobby events ({context}): {ex.Message}");
                }
            }
        }

        private async UniTask ResubscribePartyLobbyEventsAsync(string context) {
            if(_isResubscribingPartyLobbyEvents || _partyLobbyEvents == null || _ugsPartyLobby == null) return;
            if(!string.Equals(_partyLobbyEventsLobbyId, _ugsPartyLobby.Id, StringComparison.Ordinal)) return;

            _isResubscribingPartyLobbyEvents = true;
            try {
                await _partyLobbyEvents.SubscribeAsync();
                if(Debug.isDebugBuild) {
                    Debug.Log($"[SessionManager] Re-subscribed party lobby events ({context}) lobbyId='{_partyLobbyEventsLobbyId}'.");
                }
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f)) {
                    Debug.LogWarning($"[SessionManager] Failed to re-subscribe party lobby events ({context}): {ex.Message}");
                }
            } finally {
                _isResubscribingPartyLobbyEvents = false;
            }
        }

        private async UniTask ResubscribeMatchLobbyEventsAsync(string context) {
            if(_isResubscribingMatchLobbyEvents || _matchLobbyEvents == null || _ugsMatchLobby == null) return;
            if(!string.Equals(_matchLobbyEventsLobbyId, _ugsMatchLobby.Id, StringComparison.Ordinal)) return;

            _isResubscribingMatchLobbyEvents = true;
            try {
                await _matchLobbyEvents.SubscribeAsync();
                if(Debug.isDebugBuild) {
                    Debug.Log($"[SessionManager] Re-subscribed match lobby events ({context}) lobbyId='{_matchLobbyEventsLobbyId}'.");
                }
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f)) {
                    Debug.LogWarning($"[SessionManager] Failed to re-subscribe match lobby events ({context}): {ex.Message}");
                }
            } finally {
                _isResubscribingMatchLobbyEvents = false;
            }
        }

        private void SchedulePartyLobbyEventsSubscriptionRetry(string context) {
            if(_isLeaving || _isShuttingDown) return;
            if(_isRetryingPartyLobbyEventsSubscription) return;
            _partyLobbyEventsSubscriptionRetryAttempt = 0;
            _isRetryingPartyLobbyEventsSubscription = true;
            LaunchSessionTask(RetryEnsurePartyLobbyEventsSubscriptionAsync(context), "PartyLobbyEvents/RetryEnsure");
        }

        private void ScheduleMatchLobbyEventsSubscriptionRetry(string context) {
            if(_isLeaving || _isShuttingDown) return;
            if(_isRetryingMatchLobbyEventsSubscription) return;
            _matchLobbyEventsSubscriptionRetryAttempt = 0;
            _isRetryingMatchLobbyEventsSubscription = true;
            LaunchSessionTask(RetryEnsureMatchLobbyEventsSubscriptionAsync(context), "MatchLobbyEvents/RetryEnsure");
        }

        private async UniTask RetryEnsurePartyLobbyEventsSubscriptionAsync(string context) {
            try {
                while(!_isLeaving && !_isShuttingDown) {
                    if(_ugsPartyLobby == null || string.IsNullOrEmpty(_ugsPartyLobby.Id)) {
                        return;
                    }

                    if(_partyLobbyEvents != null &&
                       string.Equals(_partyLobbyEventsLobbyId, _ugsPartyLobby.Id, StringComparison.Ordinal)) {
                        _partyLobbyEventsSubscriptionRetryAttempt = 0;
                        return;
                    }

                    var retryDelayMs = ComputeLobbyEventSubscriptionRetryDelayMs(_partyLobbyEventsSubscriptionRetryAttempt);
                    _partyLobbyEventsSubscriptionRetryAttempt++;
                    try {
                        await UniTask.Delay(retryDelayMs, cancellationToken: SessionLifetimeToken);
                    } catch(OperationCanceledException) {
                        return;
                    }

                    await EnsurePartyLobbyEventsSubscriptionAsync(
                        $"{context}/Retry#{_partyLobbyEventsSubscriptionRetryAttempt}");
                }
            } finally {
                _isRetryingPartyLobbyEventsSubscription = false;
            }
        }

        private async UniTask RetryEnsureMatchLobbyEventsSubscriptionAsync(string context) {
            try {
                while(!_isLeaving && !_isShuttingDown) {
                    if(_ugsMatchLobby == null || string.IsNullOrEmpty(_ugsMatchLobby.Id)) {
                        return;
                    }

                    if(_matchLobbyEvents != null &&
                       string.Equals(_matchLobbyEventsLobbyId, _ugsMatchLobby.Id, StringComparison.Ordinal)) {
                        _matchLobbyEventsSubscriptionRetryAttempt = 0;
                        return;
                    }

                    var retryDelayMs = ComputeLobbyEventSubscriptionRetryDelayMs(_matchLobbyEventsSubscriptionRetryAttempt);
                    _matchLobbyEventsSubscriptionRetryAttempt++;
                    try {
                        await UniTask.Delay(retryDelayMs, cancellationToken: SessionLifetimeToken);
                    } catch(OperationCanceledException) {
                        return;
                    }

                    await EnsureMatchLobbyEventsSubscriptionAsync(
                        $"{context}/Retry#{_matchLobbyEventsSubscriptionRetryAttempt}");
                }
            } finally {
                _isRetryingMatchLobbyEventsSubscription = false;
            }
        }

        private static int ComputeLobbyEventSubscriptionRetryDelayMs(int attempt) {
            var exponent = Mathf.Clamp(attempt, 0, LobbyEventSubscriptionRetryMaxExponent);
            var exponentialMs = LobbyEventSubscriptionRetryBaseDelayMs * (1 << exponent);
            var cappedMs = Mathf.Min(exponentialMs, LobbyEventSubscriptionRetryMaxDelayMs);
            var jitterMs = UnityEngine.Random.Range(0, LobbyEventSubscriptionRetryJitterMs + 1);
            return cappedMs + jitterMs;
        }

        private void OnPartyLobbyChanged(ILobbyChanges changes) {
            LaunchSessionTask(HandlePartyLobbyChangedAsync(changes), "PartyLobbyEvents/LobbyChanged");
        }

        private async UniTask HandlePartyLobbyChangedAsync(ILobbyChanges changes) {
            await UniTask.SwitchToMainThread();

            if(_isLeaving || _isShuttingDown || _ugsPartyLobby == null || changes == null) return;
            if(!string.IsNullOrEmpty(_partyLobbyEventsLobbyId) &&
               !string.Equals(_partyLobbyEventsLobbyId, _ugsPartyLobby.Id, StringComparison.Ordinal)) {
                return;
            }

            try {
                changes.ApplyToLobby(_ugsPartyLobby);
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f)) {
                    Debug.LogWarning($"[SessionManager] Failed applying party lobby changes: {ex.Message}");
                }
                return;
            }

            NotifyPartyStateChanged();
            await HandlePartyLobbyFollowStateAsync("PartyLobbyEvents/LobbyChanged");
        }

        private void OnPartyLobbyDeleted() {
            LaunchSessionTask(HandlePartyLobbyDeletedOrKickedAsync("LobbyDeleted"), "PartyLobbyEvents/LobbyDeleted");
        }

        private void OnPartyLobbyKicked() {
            LaunchSessionTask(HandlePartyLobbyDeletedOrKickedAsync("KickedFromLobby"), "PartyLobbyEvents/KickedFromLobby");
        }

        private async UniTask HandlePartyLobbyDeletedOrKickedAsync(string reason) {
            await UniTask.SwitchToMainThread();

            if(Debug.isDebugBuild) {
                Debug.LogWarning($"[SessionManager] Party lobby event: {reason}. Clearing local party lobby cache.");
            }

            _ugsPartyLobby = null;
            IsPartyLeader = false;
            _lastFailedFollowMatchLobbyId = null;
            await UnsubscribePartyLobbyEventsAsync($"PartyLobbyEvents/{reason}");
            NotifyPartyStateChanged();
        }

        private void OnPartyLobbyEventConnectionStateChanged(LobbyEventConnectionState state) {
            LaunchSessionTask(HandlePartyLobbyEventConnectionStateChangedAsync(state),
                "PartyLobbyEvents/ConnectionStateChanged");
        }

        private async UniTask HandlePartyLobbyEventConnectionStateChangedAsync(LobbyEventConnectionState state) {
            await UniTask.SwitchToMainThread();

            if(Debug.isDebugBuild && ShouldEmitThrottledLog(ref _nextLobbyEventConnectionLogTime, 2f)) {
                Debug.Log($"[SessionManager] Party lobby events connection state: {state} (lobbyId='{_partyLobbyEventsLobbyId}').");
            }

            if(state is LobbyEventConnectionState.Error or LobbyEventConnectionState.Unsynced) {
                await ResubscribePartyLobbyEventsAsync($"ConnectionState/{state}");
            }
        }

        private void OnMatchLobbyChanged(ILobbyChanges changes) {
            LaunchSessionTask(HandleMatchLobbyChangedAsync(changes), "MatchLobbyEvents/LobbyChanged");
        }

        private async UniTask HandleMatchLobbyChangedAsync(ILobbyChanges changes) {
            await UniTask.SwitchToMainThread();

            if(_isLeaving || _isShuttingDown || _ugsMatchLobby == null || changes == null) return;
            if(!string.IsNullOrEmpty(_matchLobbyEventsLobbyId) &&
               !string.Equals(_matchLobbyEventsLobbyId, _ugsMatchLobby.Id, StringComparison.Ordinal)) {
                return;
            }

            try {
                changes.ApplyToLobby(_ugsMatchLobby);
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f)) {
                    Debug.LogWarning($"[SessionManager] Failed applying match lobby changes: {ex.Message}");
                }
                return;
            }

            TryCompletePlayersReadyWaiterFromLobby(_ugsMatchLobby);
            HandleMatchLobbySnapshot("MatchLobbyEvents/LobbyChanged");
        }

        private void OnMatchLobbyDeleted() {
            LaunchSessionTask(HandleMatchLobbyDeletedOrKickedAsync("LobbyDeleted"), "MatchLobbyEvents/LobbyDeleted");
        }

        private void OnMatchLobbyKicked() {
            LaunchSessionTask(HandleMatchLobbyDeletedOrKickedAsync("KickedFromLobby"), "MatchLobbyEvents/KickedFromLobby");
        }

        private async UniTask HandleMatchLobbyDeletedOrKickedAsync(string reason) {
            await UniTask.SwitchToMainThread();

            if(Debug.isDebugBuild) {
                Debug.LogWarning($"[SessionManager] Match lobby event: {reason}. Clearing local match lobby cache.");
            }

            CompleteAndClearPlayersReadyWaiter(false);
            _ugsSyncInProgress = false;
            _ugsClientStartedForMatch = false;
            _ugsHostPreFadedOut = false;
            ResetDistributedAuthorityJoinRetryState();
            if(Phase != SessionPhase.InGame) {
                _ugsLocalReadySubmitted = false;
                _ugsMatchLobby = null;
            }

            await UnsubscribeMatchLobbyEventsAsync($"MatchLobbyEvents/{reason}");
            UpdateSteamRichPresence();
        }

        private void OnMatchLobbyEventConnectionStateChanged(LobbyEventConnectionState state) {
            LaunchSessionTask(HandleMatchLobbyEventConnectionStateChangedAsync(state),
                "MatchLobbyEvents/ConnectionStateChanged");
        }

        private async UniTask HandleMatchLobbyEventConnectionStateChangedAsync(LobbyEventConnectionState state) {
            await UniTask.SwitchToMainThread();

            if(Debug.isDebugBuild && ShouldEmitThrottledLog(ref _nextLobbyEventConnectionLogTime, 2f)) {
                Debug.Log($"[SessionManager] Match lobby events connection state: {state} (lobbyId='{_matchLobbyEventsLobbyId}').");
            }

            if(state is LobbyEventConnectionState.Error or LobbyEventConnectionState.Unsynced) {
                await ResubscribeMatchLobbyEventsAsync($"ConnectionState/{state}");
            }
        }

        private static bool IsLobbyHostForLocalPlayer(Lobby lobby, string localId) {
            return lobby != null &&
                   !string.IsNullOrEmpty(localId) &&
                   string.Equals(lobby.HostId, localId, StringComparison.Ordinal);
        }

        private bool ShouldHeartbeatPartyLobby() {
            // During gameplay we keep match lobby heartbeat alive for backfill/late-join paths,
            // and pause party lobby heartbeat to reduce duplicate UGS pressure.
            return Phase != SessionPhase.InGame;
        }

        private void RefreshHeartbeatSchedulesForCurrentLobbies(float now) {
            if(_ugsPartyLobby == null || string.IsNullOrEmpty(_ugsPartyLobby.Id)) {
                _lastPartyHeartbeatLobbyId = null;
                _nextPartyHeartbeatTime = 0f;
                _partyHeartbeatRateLimitStreak = 0;
                _partyHeartbeatBackoffUntil = 0f;
            } else if(!string.Equals(_lastPartyHeartbeatLobbyId, _ugsPartyLobby.Id, StringComparison.Ordinal)) {
                _lastPartyHeartbeatLobbyId = _ugsPartyLobby.Id;
                _nextPartyHeartbeatTime = now + HeartbeatInitialDelaySeconds;
                _partyHeartbeatRateLimitStreak = 0;
                _partyHeartbeatBackoffUntil = 0f;
            } else if(_nextPartyHeartbeatTime <= 0f) {
                _nextPartyHeartbeatTime = now + HeartbeatInitialDelaySeconds;
            }

            if(_ugsMatchLobby == null || string.IsNullOrEmpty(_ugsMatchLobby.Id)) {
                _lastMatchHeartbeatLobbyId = null;
                _nextMatchHeartbeatTime = 0f;
                _matchHeartbeatRateLimitStreak = 0;
                _matchHeartbeatBackoffUntil = 0f;
            } else if(!string.Equals(_lastMatchHeartbeatLobbyId, _ugsMatchLobby.Id, StringComparison.Ordinal)) {
                _lastMatchHeartbeatLobbyId = _ugsMatchLobby.Id;
                _nextMatchHeartbeatTime = now + HeartbeatInitialDelaySeconds + HeartbeatStaggerSeconds;
                _matchHeartbeatRateLimitStreak = 0;
                _matchHeartbeatBackoffUntil = 0f;
            } else if(_nextMatchHeartbeatTime <= 0f) {
                _nextMatchHeartbeatTime = now + HeartbeatInitialDelaySeconds + HeartbeatStaggerSeconds;
            }
        }

        private async UniTask SendPartyHeartbeatAsync() {
            if(_ugsPartyLobby == null || string.IsNullOrEmpty(_ugsPartyLobby.Id)) return;
            var lobbyId = _ugsPartyLobby.Id;

            try {
                await LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
                _partyHeartbeatRateLimitStreak = 0;
                _partyHeartbeatBackoffUntil = 0f;
            } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                _partyHeartbeatRateLimitStreak++;
                var backoff = ComputeHeartbeatRateLimitBackoffSeconds(_partyHeartbeatRateLimitStreak);
                _partyHeartbeatBackoffUntil = Time.unscaledTime + backoff;
                if(Debug.isDebugBuild &&
                   _partyHeartbeatRateLimitStreak >= HeartbeatRateLimitWarnStreak &&
                   ShouldEmitThrottledLog(ref _nextPartyHeartbeatRateLimitWarnTime, HeartbeatRateLimitWarnIntervalSeconds)) {
                    Debug.LogWarning(
                        $"[SessionManager] UGS party heartbeat is repeatedly rate-limited ({_partyHeartbeatRateLimitStreak}x). Backing off for {backoff:0.0}s.");
                }
            } catch(Exception ex) {
                if(Debug.isDebugBuild && ShouldEmitThrottledLog(ref _nextUgsHeartbeatFailureLogTime, 15f)) {
                    Debug.LogWarning($"[SessionManager] UGS party heartbeat ping failed: {ex.Message}");
                }
            } finally {
                _nextPartyHeartbeatTime = Time.unscaledTime + PartyHeartbeatIntervalSeconds;
            }
        }

        private async UniTask SendMatchHeartbeatAsync() {
            if(_ugsMatchLobby == null || string.IsNullOrEmpty(_ugsMatchLobby.Id)) return;
            var lobbyId = _ugsMatchLobby.Id;

            try {
                await LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
                _matchHeartbeatRateLimitStreak = 0;
                _matchHeartbeatBackoffUntil = 0f;
            } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                _matchHeartbeatRateLimitStreak++;
                var backoff = ComputeHeartbeatRateLimitBackoffSeconds(_matchHeartbeatRateLimitStreak);
                _matchHeartbeatBackoffUntil = Time.unscaledTime + backoff;
                if(Debug.isDebugBuild &&
                   _matchHeartbeatRateLimitStreak >= HeartbeatRateLimitWarnStreak &&
                   ShouldEmitThrottledLog(ref _nextMatchHeartbeatRateLimitWarnTime, HeartbeatRateLimitWarnIntervalSeconds)) {
                    Debug.LogWarning(
                        $"[SessionManager] UGS match heartbeat is repeatedly rate-limited ({_matchHeartbeatRateLimitStreak}x). Backing off for {backoff:0.0}s.");
                }
            } catch(Exception ex) {
                if(Debug.isDebugBuild && ShouldEmitThrottledLog(ref _nextUgsHeartbeatFailureLogTime, 15f)) {
                    Debug.LogWarning($"[SessionManager] UGS match heartbeat ping failed: {ex.Message}");
                }
            } finally {
                _nextMatchHeartbeatTime = Time.unscaledTime + MatchHeartbeatIntervalSeconds;
            }
        }

        private async UniTask SendPartyHeartbeatsAsync() {
            if(_isHeartbeatDispatchInFlight) return;
            _isHeartbeatDispatchInFlight = true;

            try {
                var localId = AuthenticationService.Instance.PlayerId;
                if(string.IsNullOrEmpty(localId)) return;
                var now = Time.unscaledTime;
                RefreshHeartbeatSchedulesForCurrentLobbies(now);

                if(ShouldHeartbeatPartyLobby() &&
                   _ugsPartyLobby != null &&
                   IsLobbyHostForLocalPlayer(_ugsPartyLobby, localId) &&
                   now >= _nextPartyHeartbeatTime &&
                   now >= _partyHeartbeatBackoffUntil) {
                    await SendPartyHeartbeatAsync();
                }

                now = Time.unscaledTime;
                if(_ugsMatchLobby != null &&
                   IsLobbyHostForLocalPlayer(_ugsMatchLobby, localId) &&
                   now >= _nextMatchHeartbeatTime &&
                   now >= _matchHeartbeatBackoffUntil) {
                    await SendMatchHeartbeatAsync();
                }
            } finally {
                _isHeartbeatDispatchInFlight = false;
            }
        }

        private static float ComputeHeartbeatRateLimitBackoffSeconds(int streak) {
            var clampedStreak = Mathf.Clamp(streak, 1, 8);
            var exponent = clampedStreak - 1;
            var rawBackoff = HeartbeatRateLimitBaseBackoffSeconds * Mathf.Pow(2f, exponent);
            var jitter = UnityEngine.Random.Range(0f, 2f);
            return Mathf.Min(HeartbeatRateLimitMaxBackoffSeconds, rawBackoff + jitter);
        }

    }
}
