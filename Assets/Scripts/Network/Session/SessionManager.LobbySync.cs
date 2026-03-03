using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Network.Diagnostics;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace Network.Session {
    public sealed partial class SessionManager {
        private float _nextUgsHeartbeatTime;
        private float _nextUgsPollTime;
        private const float UgsHeartbeatIntervalSeconds = 15f;
        private const float UgsPollIntervalSeconds = 2f;
        private bool _ugsSyncInProgress;
        private bool _ugsLocalReadySubmitted;
        private bool _ugsClientStartedForMatch;
        private bool _ugsHostPreFadedOut;
        private string _lastFailedFollowMatchLobbyId;
        private float _nextUgsMatchPollFailureLogTime;
        private float _nextUgsPartyPollFailureLogTime;
        private float _nextUgsHeartbeatFailureLogTime;
        private float _nextUgsSyncRateLimitLogTime;
        private float _nextUgsPublicReadyWaitFailureLogTime;
        private float _nextUgsClientStartFailureLogTime;
        private bool _isPollingMatchLobby;
        private bool _isPollingPartyLobby;
        private bool _isMatchLobbyPollingLoopRunning;
        private float _matchPollBackoffUntil;
        private float _partyPollBackoffUntil;
        private float _partyHeartbeatBackoffUntil;
        private float _matchHeartbeatBackoffUntil;
        private int _partyHeartbeatRateLimitStreak;
        private int _matchHeartbeatRateLimitStreak;
        private float _nextPartyHeartbeatRateLimitWarnTime;
        private float _nextMatchHeartbeatRateLimitWarnTime;

        private const int HeartbeatRateLimitWarnStreak = 3;
        private const float HeartbeatRateLimitWarnIntervalSeconds = 30f;
        private const float HeartbeatRateLimitBaseBackoffSeconds = 10f;
        private const float HeartbeatRateLimitMaxBackoffSeconds = 90f;

        private void Update() {
            if(_isLeaving || _isShuttingDown) {
                return;
            }

            if(_ugsPartyLobby == null && _ugsMatchLobby == null) return;

            // Global Watchdog: If we are stuck in a black screen phase too long, abort.
            if(Phase == SessionPhase.SynchronizingLoad) {
                if(Time.time - _phaseStartTime > 30f) {
                    Debug.LogError("[SessionManager] Stuck in SynchronizingLoad for >30s. Aborting to menu...");
                    FlowLog.Emit(FlowEventIds.AnomalySessionStuck,
                        ("phase", Phase),
                        ("elapsed", Time.time - _phaseStartTime));
                    LaunchSessionTask(LeaveToMainMenuAsync(),
                        "SynchronizingLoadWatchdog/LeaveToMainMenu");
                    return;
                }
            }

            if(Time.unscaledTime >= _nextUgsHeartbeatTime) {
                _nextUgsHeartbeatTime = Time.unscaledTime + UgsHeartbeatIntervalSeconds;
                LaunchSessionTask(SendPartyHeartbeatsAsync(),
                    "SendPartyHeartbeats");
            }

            if(!(Time.unscaledTime >= _nextUgsPollTime)) return;
            _nextUgsPollTime = Time.unscaledTime + UgsPollIntervalSeconds;
            if(_ugsPartyLobby != null && Time.unscaledTime >= _partyPollBackoffUntil) {
                LaunchSessionTask(PollPartyLobbyAsync(),
                    "PollPartyLobby");
            }

            if(_ugsMatchLobby != null && Time.unscaledTime >= _matchPollBackoffUntil) {
                LaunchSessionTask(PollMatchLobbyAsync(),
                    "PollMatchLobby");
            }
        }

        private async UniTask<bool> WaitForPrivateMatchSyncReadyAsync(List<string> expectedPlayers) {
            var syncStartTime = Time.time;
            const float syncTimeout = 20f;
            while(true) {
                if(_isLeaving || _isShuttingDown || _ugsMatchLobby == null) {
                    return false;
                }

                if(Time.time - syncStartTime > syncTimeout) {
                    Debug.LogWarning("[SessionManager] Private match sync timed out! Aborting to menu...");
                    return false;
                }

                try {
                    var refreshed = await LobbyService.Instance.GetLobbyAsync(_ugsMatchLobby.Id);
                    if(refreshed != null) _ugsMatchLobby = refreshed;

                    if(AreAllExpectedPlayersReady(_ugsMatchLobby, expectedPlayers)) {
                        return true;
                    }
                } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                    if(ShouldEmitThrottledLog(ref _nextUgsSyncRateLimitLogTime, 5f)) {
                        Debug.LogWarning("[SessionManager] Rate limited during sync. Retrying...");
                    }
                } catch(System.Exception ex) {
                    Debug.LogError($"[SessionManager] Error during sync: {ex.Message}. Aborting...");
                    return false;
                }

                try {
                    await UniTask.Delay(1000, cancellationToken: SessionLifetimeToken);
                } catch(System.OperationCanceledException) {
                    return false;
                }
            }
        }

        private async UniTask<bool> WaitForPublicMatchPlayersReadyAsync(List<string> expectedPlayerIds) {
            for(var i = 0; i < 60; i++) { // 60 seconds timeout
                if(_isLeaving || _isShuttingDown || _ugsMatchLobby == null) {
                    return false;
                }

                try {
                    await UniTask.Delay(1000, cancellationToken: SessionLifetimeToken);
                } catch(System.OperationCanceledException) {
                    return false;
                }

                try {
                    _ugsMatchLobby = await LobbyService.Instance.GetLobbyAsync(_ugsMatchLobby.Id);
                } catch(System.Exception ex) {
                    if(ShouldEmitThrottledLog(ref _nextUgsPublicReadyWaitFailureLogTime, 5f)) {
                        Debug.LogWarning(
                            $"[SessionManager] Failed to refresh public match lobby during ready wait: {ex.Message}");
                    }

                    continue;
                }

                if(_ugsMatchLobby == null) return false;

                if(AreAllExpectedPlayersReady(_ugsMatchLobby, expectedPlayerIds)) {
                    if(Debug.isDebugBuild) {
                        Debug.Log("[SessionManager] All expected players ready! Starting match...");
                    }

                    return true;
                }

                if(Debug.isDebugBuild && ((i + 1) % 5 == 0 || i == 0 || i == 59)) {
                    Debug.Log(
                        $"[SessionManager] Waiting for players... lobby has {_ugsMatchLobby.Players?.Count ?? 0} players");
                }
            }

            return false;
        }

        private async UniTask StartMatchLobbyPollingAsync() {
            if(_isMatchLobbyPollingLoopRunning) return;
            _isMatchLobbyPollingLoopRunning = true;

            // Poll until we either connect or timeout
            try {
                for(var i = 0; i < 60; i++) {
                    await UniTask.Delay(1000, cancellationToken: SessionLifetimeToken);
                    if(_ugsMatchLobby == null) break;
                    if(Phase == SessionPhase.InGame) break;
                    if(_ugsClientStartedForMatch) break;

                    LaunchSessionTask(PollMatchLobbyAsync(),
                        "StartMatchLobbyPolling/PollMatchLobby");
                }
            } catch(System.OperationCanceledException) {
                // Session lifetime was canceled (quit/destroy).
            } finally {
                _isMatchLobbyPollingLoopRunning = false;
            }

            if(_ugsMatchLobby != null && Phase != SessionPhase.InGame && !_ugsClientStartedForMatch) {
                Debug.LogWarning("[SessionManager] Match lobby polling timed out before client start.");
            }
        }

        private void SyncModeFromMatchLobby(Lobby lobby) {
            if(_isLeaving || _isShuttingDown) {
                return;
            }

            if(lobby == null) return;
            if(lobby.Data == null) return;
            if(!lobby.Data.TryGetValue(UgsTargetModeKey, out var modeObj)) return;
            if(modeObj == null) return;
            if(string.IsNullOrEmpty(modeObj.Value)) return;
            ApplyRuntimeMode(modeObj.Value, "UgsMatchLobbySync", refreshUi: false);
        }

        private async UniTask PollMatchLobbyAsync() {
            if(_ugsMatchLobby == null) return;
            if(Phase == SessionPhase.InGame) return;
            if(_isLeaving || _isShuttingDown) return;
            if(_isPollingMatchLobby) return;

            _isPollingMatchLobby = true;

            try {
                var refreshed = await LobbyService.Instance.GetLobbyAsync(_ugsMatchLobby.Id);
                if(refreshed != null) _ugsMatchLobby = refreshed;
            } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                _matchPollBackoffUntil = Time.unscaledTime + 4f;
                if(Debug.isDebugBuild && ShouldEmitThrottledLog(ref _nextUgsMatchPollFailureLogTime, 10f)) {
                    Debug.Log("[SessionManager] Match lobby poll rate-limited; backing off for 4s.");
                }
            } catch(System.Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextUgsMatchPollFailureLogTime, 10f)) {
                    Debug.LogWarning($"[SessionManager] Failed to poll UGS match lobby: {ex.Message}");
                }

                return;
            } finally {
                _isPollingMatchLobby = false;
            }

            if(_isLeaving || _isShuttingDown) {
                if(Debug.isDebugBuild) {
                    FlowLog.Emit(FlowEventIds.SessionExit,
                        ("reason", "LeaveToMainMenu"),
                        ("step", "EXIT_POLL_MATCH_SKIPPED_POST_AWAIT"));
                }

                return;
            }

            if(_ugsMatchLobby == null) return;
            SyncModeFromMatchLobby(_ugsMatchLobby);

            if(_ugsMatchLobby.Data == null) return;
            if(!_ugsMatchLobby.Data.TryGetValue(UgsLobbyStateKey, out var stateObj)) return;
            if(stateObj == null) return;

            switch(stateObj.Value) {
                case "SynchronizingLoad": {
                    if(_ugsLocalReadySubmitted == false) {
                        LaunchSessionTask(StartMatchSynchronizationAsync(),
                            "PollMatchLobby/SynchronizingLoad");
                    }

                    return;
                }
                case "LoadingScene": {
                    // The lobby host will start the Netcode host; they should NOT also start as a relay client.
                    var localUgsId = AuthenticationService.Instance.PlayerId;
                    if(!string.IsNullOrEmpty(localUgsId) && _ugsMatchLobby.HostId == localUgsId) {
                        return;
                    }

                    if(_ugsClientStartedForMatch == false) {
                        LaunchSessionTask(StartMatchClientAsync(),
                            "PollMatchLobby/LoadingScene");
                    }

                    break;
                }
                case "InGame": {
                    // Join-in-progress path: treat InGame as client-start signal.
                    var localUgsId = AuthenticationService.Instance.PlayerId;
                    if(!string.IsNullOrEmpty(localUgsId) && _ugsMatchLobby.HostId == localUgsId) {
                        return;
                    }

                    _ugsLocalReadySubmitted = true;
                    if(_ugsClientStartedForMatch == false) {
                        LaunchSessionTask(StartMatchClientAsync(useFadeOut: true),
                            "PollMatchLobby/InGame");
                    }

                    break;
                }
            }
        }

        private async UniTask StartMatchSynchronizationAsync() {
            if(_ugsMatchLobby == null) return;
            if(_ugsLocalReadySubmitted) return;
            if(_ugsSyncInProgress) return;

            _ugsSyncInProgress = true;
            Phase = SessionPhase.SynchronizingLoad;
            SetFrontStatus(SessionPhase.SynchronizingLoad, "Waiting for party...");

            // Fade out via SceneTransitionManager (matches Steam sync UX).
            if(_ugsHostPreFadedOut) {
                _ugsHostPreFadedOut = false;
            } else {
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
            } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                Debug.LogWarning("[SessionManager] Rate limited updating ready state. Polling will retry.");
            } catch(System.Exception ex) {
                Debug.LogError($"[SessionManager] Failed to update ready state: {ex.Message}. Aborting to menu...");
                await LeaveToMainMenuAsync();
            } finally {
                _ugsSyncInProgress = false;
            }
        }

        private static bool AreAllExpectedPlayersReady(Lobby lobby,
            List<string> expectedPlayerIds) {
            if(lobby == null) return false;
            if(expectedPlayerIds == null) return true;
            if(expectedPlayerIds.Count == 0) return true;
            if(lobby.Players == null) return false;

            foreach(var id in expectedPlayerIds) {
                if(string.IsNullOrEmpty(id)) continue;

                Player found = null;
                foreach(var p in lobby.Players) {
                    if(p == null) continue;
                    if(p.Id != id) continue;
                    found = p;
                    break;
                }

                if(found == null) return false;
                if(found.Data == null) return false;
                if(!found.Data.TryGetValue(UgsMemberReadyKey, out var readyObj)) return false;
                if(readyObj == null) return false;
                if(readyObj.Value != "1") return false;
            }

            return true;
        }

        private async UniTask StartMatchClientAsync(bool useFadeOut = false) {
            if(_ugsMatchLobby == null) return;
            if(_ugsClientStartedForMatch) return;
            if(_ugsLocalReadySubmitted == false) return;
            if(_isLeaving || _isShuttingDown) return;

            if(_ugsMatchLobby.Data == null) {
                if(ShouldEmitThrottledLog(ref _nextUgsClientStartFailureLogTime, 10f)) {
                    Debug.LogWarning("[SessionManager] Cannot start match client: match lobby data is unavailable.");
                }

                return;
            }

            if(!_ugsMatchLobby.Data.TryGetValue(UgsRelayJoinCodeKey, out var joinCodeObj) || joinCodeObj == null) {
                if(ShouldEmitThrottledLog(ref _nextUgsClientStartFailureLogTime, 10f)) {
                    Debug.LogWarning("[SessionManager] Cannot start match client: relay join code has not been published.");
                }

                return;
            }

            var joinCode = joinCodeObj.Value;
            if(string.IsNullOrEmpty(joinCode)) {
                if(ShouldEmitThrottledLog(ref _nextUgsClientStartFailureLogTime, 10f)) {
                    Debug.LogWarning("[SessionManager] Cannot start match client: relay join code is empty.");
                }

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

                await CleanupNetworkAsync();

                if(TryGetUnityTransport("StartMatchClientAsync", out var networkManager, out var utp) == false) {
                    await LeaveToMainMenuAsync();
                    return;
                }

                JoinAllocation joinAlloc;
                try {
                    joinAlloc = await RelayService.Instance.JoinAllocationAsync(joinCode);
                } catch(System.Exception ex) {
                    Debug.LogError(
                        $"[SessionManager] Failed to join relay allocation for code '{joinCode}': {ex.Message}");
                    await LeaveToMainMenuAsync();
                    return;
                }

                if(_isLeaving || _isShuttingDown) {
                    if(Debug.isDebugBuild) {
                        FlowLog.Emit(FlowEventIds.SessionExit,
                            ("reason", "LeaveToMainMenu"),
                            ("step", "EXIT_CLIENT_START_SKIPPED_POST_RELAY_JOIN"));
                    }

                    return;
                }

                if(TryApplyRelayToTransport(utp, null, joinAlloc) == false) {
                    Debug.LogError("[SessionManager] Failed to apply relay client allocation to transport.");
                    await LeaveToMainMenuAsync();
                    return;
                }

                networkManager.NetworkConfig.NetworkTransport = utp;

                ApplyLocalConnectionPayload(true);
                if(!networkManager.StartClient()) {
                    Debug.LogError("[SessionManager] Failed to start UGS match client after cleanup.");
                    await LeaveToMainMenuAsync();
                    return;
                }

                shouldResetClientStartFlag = false;
            } finally {
                if(shouldResetClientStartFlag) {
                    _ugsClientStartedForMatch = false;
                }
            }
        }

        private async UniTask SendPartyHeartbeatsAsync() {
            // Heartbeat only required for lobbies we host.
            var localId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localId)) return;

            if(_ugsPartyLobby != null &&
               _ugsPartyLobby.HostId == localId &&
               Time.unscaledTime >= _partyHeartbeatBackoffUntil) {
                try {
                    await LobbyService.Instance.SendHeartbeatPingAsync(_ugsPartyLobby.Id);
                    _partyHeartbeatRateLimitStreak = 0;
                    _partyHeartbeatBackoffUntil = 0f;
                } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                    _partyHeartbeatRateLimitStreak++;
                    var backoff = ComputeHeartbeatRateLimitBackoffSeconds(_partyHeartbeatRateLimitStreak);
                    _partyHeartbeatBackoffUntil = Time.unscaledTime + backoff;

                    if(Debug.isDebugBuild &&
                       _partyHeartbeatRateLimitStreak >= HeartbeatRateLimitWarnStreak &&
                       ShouldEmitThrottledLog(ref _nextPartyHeartbeatRateLimitWarnTime,
                           HeartbeatRateLimitWarnIntervalSeconds)) {
                        Debug.LogWarning(
                            $"[SessionManager] UGS party heartbeat is repeatedly rate-limited ({_partyHeartbeatRateLimitStreak}x). " +
                            $"Backing off for {backoff:0.0}s.");
                    }
                } catch(System.Exception ex) {
                    // Ignore transient heartbeat failures but keep a throttled diagnostic in debug builds.
                    if(Debug.isDebugBuild && ShouldEmitThrottledLog(ref _nextUgsHeartbeatFailureLogTime, 15f)) {
                        Debug.LogWarning($"[SessionManager] UGS party heartbeat ping failed: {ex.Message}");
                    }
                }
            }

            if(_ugsMatchLobby != null &&
               _ugsMatchLobby.HostId == localId &&
               Time.unscaledTime >= _matchHeartbeatBackoffUntil) {
                try {
                    await LobbyService.Instance.SendHeartbeatPingAsync(_ugsMatchLobby.Id);
                    _matchHeartbeatRateLimitStreak = 0;
                    _matchHeartbeatBackoffUntil = 0f;
                } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                    _matchHeartbeatRateLimitStreak++;
                    var backoff = ComputeHeartbeatRateLimitBackoffSeconds(_matchHeartbeatRateLimitStreak);
                    _matchHeartbeatBackoffUntil = Time.unscaledTime + backoff;

                    if(Debug.isDebugBuild &&
                       _matchHeartbeatRateLimitStreak >= HeartbeatRateLimitWarnStreak &&
                       ShouldEmitThrottledLog(ref _nextMatchHeartbeatRateLimitWarnTime,
                           HeartbeatRateLimitWarnIntervalSeconds)) {
                        Debug.LogWarning(
                            $"[SessionManager] UGS match heartbeat is repeatedly rate-limited ({_matchHeartbeatRateLimitStreak}x). " +
                            $"Backing off for {backoff:0.0}s.");
                    }
                } catch(System.Exception ex) {
                    // Ignore transient heartbeat failures but keep a throttled diagnostic in debug builds.
                    if(Debug.isDebugBuild && ShouldEmitThrottledLog(ref _nextUgsHeartbeatFailureLogTime, 15f)) {
                        Debug.LogWarning($"[SessionManager] UGS match heartbeat ping failed: {ex.Message}");
                    }
                }
            }
        }

        private static float ComputeHeartbeatRateLimitBackoffSeconds(int streak) {
            var clampedStreak = Mathf.Clamp(streak, 1, 8);
            var exponent = clampedStreak - 1;
            var rawBackoff = HeartbeatRateLimitBaseBackoffSeconds * Mathf.Pow(2f, exponent);
            var jitter = Random.Range(0f, 2f);
            return Mathf.Min(HeartbeatRateLimitMaxBackoffSeconds, rawBackoff + jitter);
        }

        private async UniTask PollPartyLobbyAsync() {
            if(_ugsPartyLobby == null) return;
            if(Phase == SessionPhase.InGame) return;
            if(_isLeaving || _isShuttingDown) return;
            if(_isPollingPartyLobby) return;

            _isPollingPartyLobby = true;

            try {
                var refreshed = await LobbyService.Instance.GetLobbyAsync(_ugsPartyLobby.Id);
                if(refreshed != null) _ugsPartyLobby = refreshed;
            } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                _partyPollBackoffUntil = Time.unscaledTime + 4f;
                if(Debug.isDebugBuild && ShouldEmitThrottledLog(ref _nextUgsPartyPollFailureLogTime, 10f)) {
                    Debug.Log("[SessionManager] Party lobby poll rate-limited; backing off for 4s.");
                }
            } catch(System.Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextUgsPartyPollFailureLogTime, 10f)) {
                    Debug.LogWarning($"[SessionManager] Failed to poll UGS party lobby: {ex.Message}");
                }

                return;
            } finally {
                _isPollingPartyLobby = false;
            }

            if(_isLeaving || _isShuttingDown) {
                if(Debug.isDebugBuild) {
                    FlowLog.Emit(FlowEventIds.SessionExit,
                        ("reason", "LeaveToMainMenu"),
                        ("step", "EXIT_POLL_PARTY_SKIPPED_POST_AWAIT"));
                }

                return;
            }

            if(_ugsPartyLobby == null) return;
            if(_ugsPartyLobby.Data == null) return;

            if(_ugsPartyLobby.Data.TryGetValue(UgsFollowMatchLobbyIdKey, out var followObj)) {
                if(followObj != null && !string.IsNullOrEmpty(followObj.Value)) {
                    if(_lastFailedFollowMatchLobbyId == followObj.Value) {
                        return;
                    }

                    // Join match lobby if we are not already in it.
                    if(_ugsMatchLobby == null || _ugsMatchLobby.Id != followObj.Value) {
                        try {
                            var joined = await JoinMatchLobbyByIdAsync(followObj.Value);
                            _lastFailedFollowMatchLobbyId = !joined ? followObj.Value : null;
                        } catch(System.Exception ex) {
                            Debug.LogWarning(
                                $"[SessionManager] Failed to follow match lobby '{followObj.Value}': {ex.Message}");
                            _lastFailedFollowMatchLobbyId = followObj.Value;
                        }
                    }
                } else {
                    _lastFailedFollowMatchLobbyId = null;
                }
            }
        }

        private static bool TryPickRelayEndpoint(List<RelayServerEndpoint> endpoints, string connectionType,
            out string host, out ushort port, out bool isSecure) {
            host = "";
            port = 0;
            isSecure = false;

            if(endpoints == null) return false;
            if(endpoints.Count == 0) return false;
            if(string.IsNullOrEmpty(connectionType)) return false;

            foreach(var ep in endpoints) {
                if(ep.ConnectionType != connectionType) continue;
                host = ep.Host;
                port = (ushort)ep.Port;
                isSecure = ep.Secure;
                if(string.IsNullOrEmpty(host)) return false;
                return port != 0;
            }

            return false;
        }

        private static bool TryApplyRelayToTransport(UnityTransport utp, Allocation hostAlloc,
            JoinAllocation clientAlloc) {
            if(utp == null) return false;

            const string connectionType = "dtls";

            if(hostAlloc == null && clientAlloc == null) return false;
            if(hostAlloc != null && clientAlloc != null) return false;

            string host;
            ushort port;
            bool isSecure;

            if(hostAlloc != null) {
                if(TryPickRelayEndpoint(hostAlloc.ServerEndpoints, connectionType, out host, out port, out isSecure) ==
                   false) {
                    Debug.LogError("[SessionManager] Relay allocation missing a DTLS endpoint.");
                    return false;
                }

                utp.UseWebSockets = false;
                utp.SetRelayServerData(host, port, hostAlloc.AllocationIdBytes, hostAlloc.Key, hostAlloc.ConnectionData,
                    null, isSecure);
                return true;
            }

            if(TryPickRelayEndpoint(clientAlloc.ServerEndpoints, connectionType, out host, out port, out isSecure) ==
               false) {
                Debug.LogError("[SessionManager] Relay join allocation missing a DTLS endpoint.");
                return false;
            }

            utp.UseWebSockets = false;
            utp.SetRelayServerData(host, port, clientAlloc.AllocationIdBytes, clientAlloc.Key,
                clientAlloc.ConnectionData, clientAlloc.HostConnectionData, isSecure);
            return true;
        }
    }
}
