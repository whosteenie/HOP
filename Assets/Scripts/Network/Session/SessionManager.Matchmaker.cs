using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Match;
using Network.Diagnostics;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Matchmaker.Models;
using UnityEngine;
using Player = Unity.Services.Matchmaker.Models.Player;

namespace Network.Session {
    public sealed partial class SessionManager {
        // ===== Matchmaker (delegated to SessionMatchmakerService; state for JoinPublicMatchByIdAsync/query) =====
        private float _nextMatchLobbyQueryFailureLogTime;
        public float MatchmakingStartTime { get; private set; }

        private void SetMatchmakingStartTime(float value) => MatchmakingStartTime = value;

        #region UGS Matchmaker (Ticketing)

        /// <summary>
        /// Cancels active UGS matchmaking polling and clears local ticket state.
        /// </summary>
        public void CancelMatchmaking() => _matchmakerService.CancelMatchmaking();

        /// <summary>
        /// Starts UGS quick-play matchmaking for the provided mode and drives host/client follow-up on assignment.
        /// </summary>
        public async UniTask StartMatchmakerQuickPlayAsync(string mode) =>
            await _matchmakerService.StartMatchmakerQuickPlayAsync(mode);

        #endregion

        #region Public match host/client (used by SessionMatchmakerService via IMatchmakerSessionActions)

        private static async UniTask<bool> WaitForPollingDelayAsync(int delayMs, CancellationToken ct) {
            try {
                await UniTask.Delay(delayMs, cancellationToken: ct);
                return true;
            } catch(OperationCanceledException) {
                return false;
            }
        }

        private static UniTask<bool> WaitMatchLobbyDiscoveryPollIntervalAsync(int attemptIndex, CancellationToken ct) {
            var delayMs = MatchmakerPollingPolicy.ResolveMatchLobbyDiscoveryDelayMs(attemptIndex);
            return WaitForPollingDelayAsync(delayMs, ct);
        }

        private async UniTask StartPublicMatchAsHostAsync(string mode, int maxPlayers, string matchId,
            StoredMatchmakingResults results) {
            await EnsureSignedInAsync();
            if(Debug.isDebugBuild) {
                Debug.Log(
                    $"[SessionManager] StartPublicMatchAsHostAsync: mode='{mode}' maxPlayers={maxPlayers} matchId='{matchId}'");
            }

            ApplyRuntimeMode(mode, "UgsPublicMatchHost");

            // Store the expected player IDs from the matchmaker results for sync checking
            var expectedPlayerIds = BuildExpectedPlayerIdsFromMatchResults(results);
            if(expectedPlayerIds != null && Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Expecting {expectedPlayerIds.Count} players for sync");
            }

            SetExpectedGamePlayerCount(
                expectedPlayerIds is { Count: > 0 } ? expectedPlayerIds.Count : 1,
                "UgsPublicMatchHost");

            var sessionCode =
                await CreateDistributedAuthoritySessionAsync(maxPlayers, false, "StartPublicMatchAsHostAsync");
            if(string.IsNullOrEmpty(sessionCode)) {
                await LeaveToMainMenuAsync();
                return;
            }

            await CreatePublicMatchLobbyAsHostAsync(mode, maxPlayers, matchId, sessionCode);
            await PreFadePublicHostAsync();

            // Mark host as ready
            var localUgsId = AuthenticationService.Instance.PlayerId;
            try {
                var opts = BuildReadyToLoadUpdatePlayerOptions();
                _ugsMatchLobby = await LobbyService.Instance.UpdatePlayerAsync(_ugsMatchLobby.Id, localUgsId, opts);
                _ugsLocalReadySubmitted = true;
                if(Debug.isDebugBuild) {
                    Debug.Log("[SessionManager] Host marked as ready");
                }
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to mark host ready: {ex.Message}");
            }

            // Poll until all expected players have joined and are ready
            if(await WaitForPublicMatchPlayersReadyAsync(expectedPlayerIds) == false) {
                Debug.LogError("[SessionManager] Timed out waiting for all players. Aborting to menu...");
                await LeaveToMainMenuAsync();
                return;
            }

            // Update lobby state to LoadingScene
            if(await TrySetMatchLobbyStateAsync("LoadingScene",
                   DataObject.VisibilityOptions.Public,
                   "StartPublicMatchAsHostAsync")) {
                if(Debug.isDebugBuild) {
                    Debug.Log("[SessionManager] Updated lobby state to 'LoadingScene'");
                }
            }

            if(!TryLoadGameplaySceneAsHost("StartPublicMatchAsHostAsync/LoadScene")) {
                await LeaveToMainMenuAsync();
            }
        }

        private async UniTask JoinPublicMatchByIdAsync(string matchId) {
            await EnsureSignedInAsync();
            if(string.IsNullOrEmpty(matchId)) return;

            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Joining match as non-host. matchId='{matchId}'");
            }

            // Poll lobby query until the host publishes the match lobby.
            for(var i = 0; i < MatchmakerPollingPolicy.MatchLobbyDiscoveryMaxAttempts; i++) {
                if(Debug.isDebugBuild &&
                   (i == 0 || (i + 1) % 5 == 0 || i == MatchmakerPollingPolicy.MatchLobbyDiscoveryMaxAttempts - 1)) {
                    Debug.Log(
                        $"[SessionManager] Polling for lobby... attempt {i + 1}/{MatchmakerPollingPolicy.MatchLobbyDiscoveryMaxAttempts}");
                }

                try {
                    var lobby = await QueryMatchLobbyByMatchIdAsync(matchId);
                    if(lobby != null) {
                        if(Debug.isDebugBuild) {
                            Debug.Log($"[SessionManager] Found lobby! lobbyId='{lobby.Id}'. Joining...");
                        }

                        var joined = await JoinMatchLobbyByIdAsync(lobby.Id);
                        if(joined) {
                            return;
                        }
                    }
                } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                    if(ShouldEmitThrottledLog(ref _nextMatchLobbyQueryFailureLogTime, 10f)) {
                        Debug.LogWarning("[SessionManager] Rate limited querying match. Retrying...");
                    }
                } catch(Exception ex) {
                    Debug.LogError($"[SessionManager] Terminal error querying match: {ex.Message}. Aborting...");
                    break;
                }

                if(await WaitMatchLobbyDiscoveryPollIntervalAsync(i, SessionLifetimeToken) == false) {
                    return;
                }
            }

            Debug.LogError("[SessionManager] Timed out or failed waiting for match lobby. Returning to menu...");
            await LeaveToMainMenuAsync();
        }

        private static async UniTask<Lobby>
            QueryMatchLobbyByMatchIdAsync(string matchId) {
            if(string.IsNullOrEmpty(matchId)) return null;

            var opts = new QueryLobbiesOptions {
                Filters = new List<QueryFilter> {
                    new(QueryFilter.FieldOptions.S1, matchId, QueryFilter.OpOptions.EQ)
                }
            };

            QueryResponse resp;
            try {
                resp = await LobbyService.Instance.QueryLobbiesAsync(opts);
            } catch(Exception ex) {
                var sessionManager = Instance;
                if(sessionManager != null &&
                   ShouldEmitThrottledLog(ref sessionManager._nextMatchLobbyQueryFailureLogTime, 10f)) {
                    Debug.LogWarning(
                        $"[SessionManager] Match lobby query failed for matchId '{matchId}': {ex.Message}");
                }

                return null;
            }

            if(resp == null) return null;
            if(resp.Results == null) return null;
            return resp.Results.Count == 0 ? null : resp.Results[0];
        }

        #endregion
    }
}
