using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Match;
using Network.Diagnostics;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Matchmaker;
using Unity.Services.Matchmaker.Models;
using UnityEngine;
using Player = Unity.Services.Matchmaker.Models.Player;

namespace Network {
    public sealed partial class SessionManager {
        // ===== Matchmaker state =====
        private string _matchmakerTicketId;
        private string _matchmakerQueueName;
        private CancellationTokenSource _matchmakerCts;
        private const int MatchmakerPollIntervalMs = 6000;
        private float _nextMatchLobbyQueryFailureLogTime;
        private float _nextMatchmakerPollFailureLogTime;
        private int _consecutiveMatchmakerPollFailures;
        public float MatchmakingStartTime { get; private set; }

        #region UGS Matchmaker (Ticketing)

        /// <summary>
        /// Cancels active UGS matchmaking polling and clears local ticket state.
        /// </summary>
        public void CancelMatchmaking() {
            if(_matchmakerCts != null) {
                _matchmakerCts.Cancel();
                _matchmakerCts.Dispose();
                _matchmakerCts = null;
            }

            if(!string.IsNullOrEmpty(_matchmakerTicketId)) {
                DeleteMatchmakerTicketAsync(_matchmakerTicketId).Forget();
            }

            _matchmakerTicketId = null;
            _matchmakerQueueName = null;
            _consecutiveMatchmakerPollFailures = 0;
            if(Phase != SessionPhase.InGame) {
                SetFrontStatus(SessionPhase.Menu, "");
            }
        }

        private string ResolveRequestedQuickPlayMode(string requestedMode) {
            if(string.IsNullOrEmpty(requestedMode)) {
                return SelectedGameMode;
            }

            ApplyRuntimeMode(requestedMode, "UgsQuickPlayRequest");
            return requestedMode;
        }

        private static int ResolveMaxPlayersForMode(string mode) {
            var def = MatchSettingsManager.Instance != null
                ? MatchSettingsManager.Instance.GetGamemodeDef(mode)
                : default;

            var maxPlayers = 10;
            if(def.maxPlayers > 0) maxPlayers = def.maxPlayers;
            return maxPlayers;
        }

        private static Dictionary<string, object> BuildMatchmakerTicketAttributes(string mode) {
            return new Dictionary<string, object> {
                ["modeId"] = mode,
                ["partySize"] = 1
            };
        }

        private static bool TryBuildMatchmakerPlayers(Dictionary<string, object> attrs, out string localPlayerId,
            out List<Player> players) {
            players = new List<Player>();
            localPlayerId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localPlayerId)) return false;

            players.Add(new Player(localPlayerId, attrs));
            return true;
        }

        private static async UniTask<bool> WaitMatchmakerPollIntervalAsync(CancellationToken ct) {
            try {
                await UniTask.Delay(MatchmakerPollIntervalMs, cancellationToken: ct);
                return true;
            } catch(OperationCanceledException) {
                return false;
            }
        }

        private async UniTask<bool> TryHandleFoundMatchAssignmentAsync(string mode, int maxPlayers,
            MatchIdAssignment assign) {
            if(string.IsNullOrEmpty(assign.MatchId)) {
                Debug.LogError("[SessionManager] Matchmaking found but matchId is empty.");
                CancelMatchmaking();
                return false;
            }

            FlowLog.Emit(FlowEventIds.QueueAssigned,
                ("queue", _matchmakerQueueName),
                ("mode", mode),
                ("matchId", assign.MatchId));

            StoredMatchmakingResults results;
            try {
                results = await MatchmakerService.Instance.GetMatchmakingResultsAsync(assign.MatchId);
            } catch(Exception e) {
                Debug.LogError($"[SessionManager] Failed to fetch matchmaking results. Exception: {e.Message}");
                CancelMatchmaking();
                return false;
            }

            await HandleStoredMatchmakerResultsAsync(mode, maxPlayers, assign.MatchId, results);
            return false;
        }

        private async UniTask<bool> HandleMatchIdAssignmentStatusAsync(MatchIdAssignment assign, string mode,
            int maxPlayers, CancellationToken ct) {
            switch(assign.Status) {
                case MatchIdAssignment.StatusOptions.InProgress: {
                    if(Debug.isDebugBuild) {
                        Debug.Log($"[UGS Matchmaker] Ticket '{_matchmakerTicketId}' in progress...");
                    }

                    return await WaitMatchmakerPollIntervalAsync(ct);
                }
                case MatchIdAssignment.StatusOptions.Timeout:
                    Debug.LogWarning("[SessionManager] Matchmaking timed out.");
                    CancelMatchmaking();
                    return false;
                case MatchIdAssignment.StatusOptions.Failed:
                    Debug.LogWarning($"[SessionManager] Matchmaking failed. Message: {assign.Message}");
                    CancelMatchmaking();
                    return false;
                case MatchIdAssignment.StatusOptions.Found: {
                    if(Debug.isDebugBuild) {
                        Debug.Log($"[UGS Matchmaker] Ticket '{_matchmakerTicketId}' found matchId='{assign.MatchId}'");
                    }

                    return await TryHandleFoundMatchAssignmentAsync(mode, maxPlayers, assign);
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static async UniTaskVoid DeleteMatchmakerTicketAsync(string ticketId) {
            if(string.IsNullOrEmpty(ticketId)) return;
            try {
                await MatchmakerService.Instance.DeleteTicketAsync(ticketId);
            } catch(Exception ex) {
                // Ignore transient failures; ticket will expire server-side.
                if(Debug.isDebugBuild) {
                    Debug.LogWarning($"[SessionManager] Failed to delete matchmaker ticket '{ticketId}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Starts UGS quick-play matchmaking for the provided mode and drives host/client follow-up on assignment.
        /// </summary>
        /// <param name="mode">Requested game mode id; falls back to current selected mode when empty.</param>
        public async UniTask StartMatchmakerQuickPlayAsync(string mode) {
            await EnsureSignedInAsync();
            CancelMatchmaking();

            mode = ResolveRequestedQuickPlayMode(mode);
            var maxPlayers = ResolveMaxPlayersForMode(mode);

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null) {
                matchSettings.preMatchCountdownEnabled = true;
                matchSettings.swapWeaponsOnDeath = true;
            }

            _matchmakerQueueName = GetQueueNameForMode(mode);
            if(string.IsNullOrEmpty(_matchmakerQueueName)) {
                Debug.LogError("[SessionManager] Matchmaker queue name is empty.");
                return;
            }

            FlowLog.Emit(FlowEventIds.QueueStarted,
                ("mode", mode),
                ("queue", _matchmakerQueueName),
                ("maxPlayers", maxPlayers));

            SetFrontStatus(SessionPhase.Searching, $"Searching for {mode}...");
            MatchmakingStartTime = Time.time;

            var attrs = BuildMatchmakerTicketAttributes(mode);
            if(TryBuildMatchmakerPlayers(attrs, out var localPlayerId, out var players) == false) {
                Debug.LogError("[SessionManager] Cannot start matchmaking: local UGS player id is unavailable.");
                CancelMatchmaking();
                return;
            }

            if(Debug.isDebugBuild) {
                Debug.Log(
                    $"[UGS Matchmaker] Creating ticket. mode='{mode}' queue='{_matchmakerQueueName}' playerId='{localPlayerId}'");
            }

            CreateTicketResponse resp;
            try {
                var options = new CreateTicketOptions(_matchmakerQueueName, attrs);
                resp = await MatchmakerService.Instance.CreateTicketAsync(players, options);
            } catch(Exception e) {
                Debug.LogError($"[UGS Matchmaker] CreateTicketAsync failed: {e.Message}");
                CancelMatchmaking();
                return;
            }

            _matchmakerTicketId = resp != null ? resp.Id : null;
            if(string.IsNullOrEmpty(_matchmakerTicketId)) {
                Debug.LogError("[SessionManager] Matchmaker ticket id is empty.");
                CancelMatchmaking();
                return;
            }

            _matchmakerCts = new CancellationTokenSource();
            try {
                if(Debug.isDebugBuild) {
                    Debug.Log($"[UGS Matchmaker] Ticket created: '{_matchmakerTicketId}'");
                }

                await PollMatchmakerTicketAsync(mode, maxPlayers, _matchmakerCts.Token);
            } catch(OperationCanceledException) {
                // Expected when user cancels matchmaking.
            }
        }

        private async UniTask PollMatchmakerTicketAsync(string mode, int maxPlayers, CancellationToken ct) {
            while(ct.IsCancellationRequested == false) {
                TicketStatusResponse status;
                try {
                    status = await MatchmakerService.Instance.GetTicketAsync(_matchmakerTicketId);
                } catch(Exception e) {
                    _consecutiveMatchmakerPollFailures++;
                    if(_consecutiveMatchmakerPollFailures >= 3 &&
                       ShouldEmitThrottledLog(ref _nextMatchmakerPollFailureLogTime, 10f)) {
                        Debug.LogWarning(
                            $"[SessionManager] Matchmaker poll failing repeatedly (count={_consecutiveMatchmakerPollFailures}): {e.Message}");
                    }

                    if(await WaitMatchmakerPollIntervalAsync(ct) == false) return;
                    continue;
                }

                _consecutiveMatchmakerPollFailures = 0;
                if(status == null) {
                    if(await WaitMatchmakerPollIntervalAsync(ct) == false) return;
                    continue;
                }

                if(status.Type == typeof(MatchIdAssignment)) {
                    if(status.Value is not MatchIdAssignment assign) {
                        Debug.LogError("[SessionManager] Matchmaker returned MatchIdAssignment but value was null.");
                        CancelMatchmaking();
                        return;
                    }

                    var continuePolling = await HandleMatchIdAssignmentStatusAsync(assign, mode, maxPlayers, ct);
                    if(continuePolling) continue;
                    return;
                }

                // Unknown/unsupported ticket type. Keep polling.
                if(Debug.isDebugBuild) {
                    var typeName = status.Type != null ? status.Type.Name : "null";
                    Debug.Log($"[UGS Matchmaker] Ticket '{_matchmakerTicketId}' status type='{typeName}'");
                }

                if(await WaitMatchmakerPollIntervalAsync(ct) == false) return;
            }
        }

        private async UniTask HandleStoredMatchmakerResultsAsync(string mode, int maxPlayers, string matchId,
            StoredMatchmakingResults results) {
            if(results == null) {
                Debug.LogError(
                    $"[SessionManager] Matchmaker returned null results for matchId '{matchId}'. Returning to menu.");
                LeaveToMainMenuAsync().Forget();
                return;
            }

            if(results.MatchProperties?.Players == null) {
                Debug.LogError(
                    $"[SessionManager] Matchmaker results missing player data for matchId '{matchId}'. Returning to menu.");
                LeaveToMainMenuAsync().Forget();
                return;
            }

            var localPlayerId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localPlayerId)) {
                Debug.LogError("[SessionManager] Cannot process match assignment: local UGS player id is missing.");
                LeaveToMainMenuAsync().Forget();
                return;
            }

            var hostId = DetermineDeterministicHostId(results.MatchProperties.Players);
            if(string.IsNullOrEmpty(hostId)) {
                Debug.LogError(
                    $"[SessionManager] Could not determine deterministic host for matchId '{matchId}'. Returning to menu.");
                LeaveToMainMenuAsync().Forget();
                return;
            }

            if(localPlayerId == hostId) {
                await StartPublicMatchAsHostAsync(mode, maxPlayers, matchId, results);
            } else {
                await JoinPublicMatchByIdAsync(matchId);
            }
        }

        private static string DetermineDeterministicHostId(List<Player> players) {
            if(players == null) return "";
            if(players.Count == 0) return "";

            var best = "";
            foreach(var t in players) {
                var id = t.Id;
                if(string.IsNullOrEmpty(id)) continue;
                if(string.IsNullOrEmpty(best) || string.CompareOrdinal(id, best) < 0) {
                    best = id;
                }
            }

            return best;
        }

        private static string GetQueueNameForMode(string mode) {
            if(string.IsNullOrEmpty(mode)) return "";

            // Per-mode queues (hyphenated, no whitespace) to match Dashboard configuration.
            return mode switch {
                "Hopball" => "Hopball",
                "Deathmatch" => "Deathmatch",
                "KOTH" => "KOTH",
                "Gun Tag" => "Gun-Tag",
                "Team Deathmatch" => "Team-Deathmatch",
                _ => ""
            };
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

            // Relay allocation for host.
            var (alloc, joinCode) = await CreateRelayAllocationWithJoinCodeAsync(maxPlayers);
            await CreatePublicMatchLobbyAsHostAsync(mode, maxPlayers, matchId, joinCode);
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
                LeaveToMainMenuAsync().Forget();
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

            // Now start the host
            if(await TryStartHostWithRelayAsync(alloc, false, "StartPublicMatchAsHostAsync") == false) {
                LeaveToMainMenuAsync().Forget();
                return;
            }

            if(!TryLoadGameplaySceneAsHost("StartPublicMatchAsHostAsync/LoadScene")) {
                LeaveToMainMenuAsync().Forget();
            }
        }

        private async UniTask JoinPublicMatchByIdAsync(string matchId) {
            await EnsureSignedInAsync();
            if(string.IsNullOrEmpty(matchId)) return;

            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Joining match as non-host. matchId='{matchId}'");
            }

            // Poll lobby query until the host publishes the match lobby.
            for(var i = 0; i < 30; i++) {
                if(Debug.isDebugBuild && (i == 0 || (i + 1) % 5 == 0 || i == 29)) {
                    Debug.Log($"[SessionManager] Polling for lobby... attempt {i + 1}/30");
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

                await UniTask.Delay(1000);
            }

            Debug.LogError("[SessionManager] Timed out or failed waiting for match lobby. Returning to menu...");
            LeaveToMainMenuAsync().Forget();
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
