using System;
using System.Collections.Generic;
using System.Linq;
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

namespace Network.Session {
    /// <summary>
    /// UGS matchmaker ticketing, backfill join-in-progress, and assignment handling.
    /// </summary>
    public sealed class SessionMatchmakerService {
        private const string UgsMatchTypeKey = "matchType";
        private const string UgsTargetModeKey = "targetMode";
        private const string UgsLobbyStateKey = "lobbyState";
        private const string UgsBackfillAllowedKey = "backfillAllowed";

        private ISessionContext _ctx;
        private IMatchmakerSessionActions _actions;
        private SessionMatchLobbyService _matchLobbyService;

        private string _matchmakerTicketId;
        private string _matchmakerQueueName;
        private CancellationTokenSource _matchmakerCts;
        private float _nextMatchLobbyQueryFailureLogTime;
        private float _nextMatchmakerPollFailureLogTime;
        private int _consecutiveMatchmakerPollFailures;

        public void CancelMatchmaking() {
            if(_matchmakerCts != null) {
                _matchmakerCts.Cancel();
                _matchmakerCts.Dispose();
                _matchmakerCts = null;
            }

            if(!string.IsNullOrEmpty(_matchmakerTicketId)) {
                _ctx.LaunchSessionTask(DeleteMatchmakerTicketAsync(_matchmakerTicketId),
                    "CancelMatchmaking/DeleteTicket");
            }

            _matchmakerTicketId = null;
            _matchmakerQueueName = null;
            _consecutiveMatchmakerPollFailures = 0;
            if(_ctx.Phase != SessionPhase.InGame) {
                _ctx.SetFrontStatus(SessionPhase.Menu, "");
            }
        }

        public async UniTask StartMatchmakerQuickPlayAsync(string mode) {
            if(_ctx == null || _actions == null) return;

            await _ctx.EnsureSignedInAsync();
            CancelMatchmaking();

            mode = ResolveRequestedQuickPlayMode(mode);
            var maxPlayers = ResolveMaxPlayersForMode(mode);

            ResetPublicRuntimeMatchSettings(mode);
            _ctx.SetPrivateMatchMapPreset(false);

            _ctx.SetMatchmakingStartTime(Time.time);
            _ctx.SetFrontStatus(SessionPhase.Searching, $"Searching for {mode}...");

            if(await TryJoinInProgressPublicMatchAsync(mode, maxPlayers)) {
                return;
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
            } finally {
                if(_matchmakerCts != null) {
                    _matchmakerCts.Dispose();
                    _matchmakerCts = null;
                }
            }
        }

        public void SetContext(ISessionContext ctx, IMatchmakerSessionActions actions) {
            _ctx = ctx;
            _actions = actions;
        }

        public void SetMatchLobbyService(SessionMatchLobbyService matchLobbyService) {
            _matchLobbyService = matchLobbyService;
        }

        /// <summary>Polls for the host-created match lobby by matchId then joins via actions. Called when matchmaker assigns this client as non-host.</summary>
        public async UniTask JoinPublicMatchByIdAsync(string matchId) {
            if(_ctx == null || _actions == null || _matchLobbyService == null) return;
            await _ctx.EnsureSignedInAsync();
            if(string.IsNullOrEmpty(matchId)) return;

            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Joining match as non-host. matchId='{matchId}'");
            }

            for(var i = 0; i < MatchmakerPollingPolicy.MatchLobbyDiscoveryMaxAttempts; i++) {
                if(Debug.isDebugBuild &&
                   (i == 0 || (i + 1) % 5 == 0 || i == MatchmakerPollingPolicy.MatchLobbyDiscoveryMaxAttempts - 1)) {
                    Debug.Log(
                        $"[SessionManager] Polling for lobby... attempt {i + 1}/{MatchmakerPollingPolicy.MatchLobbyDiscoveryMaxAttempts}");
                }

                try {
                    var lobby = await _matchLobbyService.QueryMatchLobbyByMatchIdAsync(matchId);
                    if(lobby != null) {
                        if(Debug.isDebugBuild) {
                            Debug.Log($"[SessionManager] Found lobby! lobbyId='{lobby.Id}'. Joining...");
                        }
                        var joined = await _actions.JoinMatchLobbyByIdAsync(lobby.Id);
                        if(joined) return;
                    }
                } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                    // Throttled; continue polling after delay.
                } catch(Exception ex) {
                    Debug.LogError($"[SessionManager] Terminal error querying match: {ex.Message}. Aborting...");
                    break;
                }

                try {
                    var delayMs = MatchmakerPollingPolicy.ResolveMatchLobbyDiscoveryDelayMs(i);
                    await UniTask.Delay(delayMs, cancellationToken: _ctx.SessionLifetimeToken);
                } catch(OperationCanceledException) {
                    return;
                }
            }

            Debug.LogError("[SessionManager] Timed out or failed waiting for match lobby. Returning to menu...");
            await _ctx.LeaveToMainMenuAsync();
        }

        private string ResolveRequestedQuickPlayMode(string requestedMode) {
            if(string.IsNullOrEmpty(requestedMode)) {
                return _ctx.SelectedGameMode;
            }

            _ctx.ApplyRuntimeMode(requestedMode, "UgsQuickPlayRequest");
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

        private static int ResolveDefaultPublicScoreToWin(string mode) {
            if(string.Equals(mode, "Hopball", StringComparison.OrdinalIgnoreCase)) return 60;
            return string.Equals(mode, "KOTH", StringComparison.OrdinalIgnoreCase) ? 200 : 50;
        }

        private static void ResetPublicRuntimeMatchSettings(string mode) {
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null) return;

            var defaultDuration = matchSettings.defaultMatchDurationSeconds > 0
                ? matchSettings.defaultMatchDurationSeconds
                : 600;

            matchSettings.matchDurationSeconds = defaultDuration;
            matchSettings.preMatchCountdownEnabled = true;
            matchSettings.swapWeaponsOnDeath = true;
            matchSettings.scoreToWin = ResolveDefaultPublicScoreToWin(mode);
            matchSettings.kothHillSpeed = 1;
            matchSettings.taggedPlayers = 1;
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

        private static bool IsJoinableInProgressLobbyState(string state) {
            return string.Equals(state, "LoadingScene", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(state, "InGame", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLobbyBackfillAllowed(Lobby lobby) {
            if(lobby?.Data == null) {
                return true;
            }

            if(lobby.Data.TryGetValue(UgsBackfillAllowedKey, out var backfillObj) == false || backfillObj == null) {
                return true;
            }

            return !string.Equals(backfillObj.Value, "false", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPublicLobbyCandidateForJoinInProgress(Lobby lobby, string mode, int queueMaxPlayers) {
            if(lobby == null) return false;
            if(string.IsNullOrWhiteSpace(lobby.Id)) return false;
            if(lobby.Data == null) return false;

            if(!lobby.Data.TryGetValue(UgsMatchTypeKey, out var matchTypeObj) || matchTypeObj == null) {
                return false;
            }

            if(!string.Equals(matchTypeObj.Value, "Public", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            if(!lobby.Data.TryGetValue(UgsTargetModeKey, out var modeObj) || modeObj == null) {
                return false;
            }

            if(!string.Equals(modeObj.Value, mode, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            if(!lobby.Data.TryGetValue(UgsLobbyStateKey, out var stateObj) || stateObj == null) {
                return false;
            }

            if(!IsJoinableInProgressLobbyState(stateObj.Value)) {
                return false;
            }

            if(!IsLobbyBackfillAllowed(lobby)) {
                return false;
            }

            var currentPlayers = lobby.Players != null ? lobby.Players.Count : 0;
            var lobbyMaxPlayers = lobby.MaxPlayers > 0 ? lobby.MaxPlayers : queueMaxPlayers;
            var effectiveMaxPlayers = Mathf.Max(1, Mathf.Min(queueMaxPlayers, lobbyMaxPlayers));
            return currentPlayers < effectiveMaxPlayers;
        }

        private static string GetBackfillRejectReason(Lobby lobby, string mode, int queueMaxPlayers) {
            if(lobby == null) return "NullLobby";
            if(string.IsNullOrWhiteSpace(lobby.Id)) return "MissingLobbyId";
            if(lobby.Data == null) return "MissingData";

            if(!lobby.Data.TryGetValue(UgsMatchTypeKey, out var matchTypeObj) || matchTypeObj == null) {
                return "MissingMatchType";
            }

            if(!string.Equals(matchTypeObj.Value, "Public", StringComparison.OrdinalIgnoreCase)) {
                return "NotPublic";
            }

            if(!lobby.Data.TryGetValue(UgsTargetModeKey, out var modeObj) || modeObj == null) {
                return "MissingMode";
            }

            if(!string.Equals(modeObj.Value, mode, StringComparison.OrdinalIgnoreCase)) {
                return "ModeMismatch";
            }

            if(!lobby.Data.TryGetValue(UgsLobbyStateKey, out var stateObj) || stateObj == null) {
                return "MissingState";
            }

            if(!IsJoinableInProgressLobbyState(stateObj.Value)) {
                return "StateNotJoinable";
            }

            if(!IsLobbyBackfillAllowed(lobby)) {
                return "BackfillDisallowed";
            }

            var currentPlayers = lobby.Players != null ? lobby.Players.Count : 0;
            var lobbyMaxPlayers = lobby.MaxPlayers > 0 ? lobby.MaxPlayers : queueMaxPlayers;
            var effectiveMaxPlayers = Mathf.Max(1, Mathf.Min(queueMaxPlayers, lobbyMaxPlayers));
            return currentPlayers >= effectiveMaxPlayers ? "Full" : "";
        }

        private static int GetLobbyPlayerCount(Lobby lobby) {
            return lobby?.Players == null ? 0 : lobby.Players.Count;
        }

        private static bool ShouldEmitThrottledLog(ref float nextLogTime, float intervalSeconds) {
            var now = Time.unscaledTime;
            if(now < nextLogTime) return false;
            nextLogTime = now + intervalSeconds;
            return true;
        }

        private async UniTask<QueryResponse> QueryLobbiesForBackfillAsync(QueryLobbiesOptions options,
            string mode, string queryLabel) {
            try {
                return await LobbyService.Instance.QueryLobbiesAsync(options);
            } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                if(ShouldEmitThrottledLog(ref _nextMatchLobbyQueryFailureLogTime, 10f)) {
                    Debug.LogWarning(
                        $"[SessionManager] Rate limited while querying in-progress lobbies ({queryLabel}) for mode='{mode}'.");
                }
                return null;
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextMatchLobbyQueryFailureLogTime, 10f)) {
                    Debug.LogWarning(
                        $"[SessionManager] Failed querying in-progress lobbies ({queryLabel}) for mode='{mode}': {ex.Message}");
                }
                return null;
            }
        }

        private async UniTask<bool> TryJoinInProgressPublicMatchAsync(string mode, int maxPlayers) {
            var indexedOptions = new QueryLobbiesOptions {
                Count = 100,
                Filters = new List<QueryFilter> {
                    new(QueryFilter.FieldOptions.S2, mode, QueryFilter.OpOptions.EQ),
                    new(QueryFilter.FieldOptions.S3, "Public", QueryFilter.OpOptions.EQ)
                }
            };

            var response = await QueryLobbiesForBackfillAsync(indexedOptions, mode, "IndexedModePublic");
            if(response == null) return false;

            if(response.Results == null || response.Results.Count == 0) {
                if(Debug.isDebugBuild) {
                    Debug.Log(
                        $"[SessionManager] Backfill indexed query returned 0 lobbies for mode='{mode}'. Falling back to broad query.");
                }

                var fallbackOptions = new QueryLobbiesOptions { Count = 100 };
                response = await QueryLobbiesForBackfillAsync(fallbackOptions, mode, "BroadFallback");
                if(response == null) return false;
            }

            if(response.Results == null || response.Results.Count == 0) {
                if(Debug.isDebugBuild) {
                    Debug.Log($"[SessionManager] Backfill query returned 0 lobbies for mode='{mode}'.");
                }
                return false;
            }

            var candidates = new List<Lobby>();
            var rejectCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach(var lobby in response.Results) {
                if(IsPublicLobbyCandidateForJoinInProgress(lobby, mode, maxPlayers)) {
                    candidates.Add(lobby);
                    continue;
                }

                var reason = GetBackfillRejectReason(lobby, mode, maxPlayers);
                if(string.IsNullOrEmpty(reason)) reason = "Unknown";
                rejectCounts.TryAdd(reason, 0);
                rejectCounts[reason]++;
            }

            if(Debug.isDebugBuild) {
                var rejectSummary = rejectCounts.Count == 0
                    ? "none"
                    : string.Join(", ", rejectCounts);
                Debug.Log(
                    $"[SessionManager] Backfill scan mode='{mode}' total={response.Results.Count} candidates={candidates.Count} rejects=({rejectSummary})");
            }

            if(candidates.Count == 0) {
                Debug.LogWarning($"[SessionManager] Backfill: no joinable in-progress lobbies for mode='{mode}'.");
                return false;
            }

            candidates.Sort((a, b) => GetLobbyPlayerCount(b).CompareTo(GetLobbyPlayerCount(a)));

            _ctx.SetFrontStatus(SessionPhase.JoiningLobby, $"Joining in-progress {mode}...");
            foreach(var lobby in candidates) {
                if(Debug.isDebugBuild) {
                    Debug.Log(
                        $"[SessionManager] Trying in-progress join: lobbyId='{lobby.Id}' players={GetLobbyPlayerCount(lobby)}/{lobby.MaxPlayers} mode='{mode}'.");
                }

                var joined = await _actions.JoinMatchLobbyByIdAsync(lobby.Id);
                if(joined) {
                    FlowLog.Emit(FlowEventIds.QueueAssigned,
                        ("queue", "InProgressBackfill"),
                        ("mode", mode),
                        ("matchId", lobby.Id));
                    return true;
                }

                if(Debug.isDebugBuild) {
                    Debug.LogWarning(
                        $"[SessionManager] Backfill join attempt failed for lobbyId='{lobby.Id}' mode='{mode}'.");
                }
            }

            Debug.LogWarning(
                $"[SessionManager] Backfill: exhausted {candidates.Count} candidate lobbies without joining for mode='{mode}'.");
            return false;
        }

        private static async UniTask<bool> WaitForPollingDelayAsync(int delayMs, CancellationToken ct) {
            try {
                await UniTask.Delay(delayMs, cancellationToken: ct);
                return true;
            } catch(OperationCanceledException) {
                return false;
            }
        }

        private static UniTask<bool> WaitMatchmakerPollIntervalAsync(int consecutiveFailures, CancellationToken ct) {
            var delayMs = MatchmakerPollingPolicy.ResolveTicketPollDelayMs(consecutiveFailures);
            return WaitForPollingDelayAsync(delayMs, ct);
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

            var results = await TryFetchMatchmakingResultsWithRetryAsync(assign.MatchId);
            if(results == null) {
                CancelMatchmaking();
                return false;
            }

            await HandleStoredMatchmakerResultsAsync(mode, maxPlayers, assign.MatchId, results);
            return false;
        }

        private async UniTask<StoredMatchmakingResults> TryFetchMatchmakingResultsWithRetryAsync(string matchId) {
            const int maxAttempts = 4;
            var delayMs = 250;
            var ct = _matchmakerCts != null ? _matchmakerCts.Token : _ctx.SessionLifetimeToken;

            for(var attempt = 1; attempt <= maxAttempts; attempt++) {
                try {
                    return await MatchmakerService.Instance.GetMatchmakingResultsAsync(matchId);
                } catch(Exception e) when(IsTransientMatchmakingResultsNotFound(e) && attempt < maxAttempts) {
                    if(Debug.isDebugBuild) {
                        Debug.LogWarning(
                            $"[SessionManager] Matchmaking results for matchId '{matchId}' were not yet available (attempt {attempt}/{maxAttempts}). Retrying in {delayMs}ms.");
                    }

                    try {
                        await UniTask.Delay(delayMs, cancellationToken: ct);
                    } catch(OperationCanceledException) {
                        return null;
                    }

                    delayMs = Mathf.Min(delayMs * 2, 2000);
                } catch(Exception e) {
                    Debug.LogError($"[SessionManager] Failed to fetch matchmaking results. Exception: {e.Message}");
                    return null;
                }
            }

            Debug.LogError(
                $"[SessionManager] Matchmaking results for matchId '{matchId}' remained unavailable after retrying.");
            return null;
        }

        private static bool IsTransientMatchmakingResultsNotFound(Exception exception) {
            return exception switch {
                null => false,
                MatchmakerServiceException matchmakerServiceException => matchmakerServiceException.Reason.ToString()
                    .IndexOf("NotFound", StringComparison.OrdinalIgnoreCase) >= 0 || matchmakerServiceException.Reason
                    .ToString()
                    .IndexOf("EntityNotFound", StringComparison.OrdinalIgnoreCase) >= 0,
                _ => exception.Message.IndexOf("404", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     exception.Message.IndexOf("Not Found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     exception.Message.IndexOf("EntityNotFound", StringComparison.OrdinalIgnoreCase) >= 0
            };
        }

        private async UniTask<bool> HandleMatchIdAssignmentStatusAsync(MatchIdAssignment assign, string mode,
            int maxPlayers, CancellationToken ct) {
            switch(assign.Status) {
                case MatchIdAssignment.StatusOptions.InProgress: {
                    if(Debug.isDebugBuild) {
                        Debug.Log($"[UGS Matchmaker] Ticket '{_matchmakerTicketId}' in progress...");
                    }
                    return await WaitMatchmakerPollIntervalAsync(0, ct);
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

        private static async UniTask DeleteMatchmakerTicketAsync(string ticketId) {
            if(string.IsNullOrEmpty(ticketId)) return;
            try {
                await MatchmakerService.Instance.DeleteTicketAsync(ticketId);
            } catch(Exception ex) {
                if(Debug.isDebugBuild) {
                    Debug.LogWarning($"[SessionManager] Failed to delete matchmaker ticket '{ticketId}': {ex.Message}");
                }
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

                    if(await WaitMatchmakerPollIntervalAsync(_consecutiveMatchmakerPollFailures, ct) == false) return;
                    continue;
                }

                _consecutiveMatchmakerPollFailures = 0;
                if(status == null) {
                    if(await WaitMatchmakerPollIntervalAsync(0, ct) == false) return;
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

                if(Debug.isDebugBuild) {
                    var typeName = status.Type != null ? status.Type.Name : "null";
                    Debug.Log($"[UGS Matchmaker] Ticket '{_matchmakerTicketId}' status type='{typeName}'");
                }

                if(await WaitMatchmakerPollIntervalAsync(0, ct) == false) return;
            }
        }

        private async UniTask HandleStoredMatchmakerResultsAsync(string mode, int maxPlayers, string matchId,
            StoredMatchmakingResults results) {
            if(results == null) {
                Debug.LogError(
                    $"[SessionManager] Matchmaker returned null results for matchId '{matchId}'. Returning to menu.");
                await _ctx.LeaveToMainMenuAsync();
                return;
            }

            if(results.MatchProperties?.Players == null) {
                Debug.LogError(
                    $"[SessionManager] Matchmaker results missing player data for matchId '{matchId}'. Returning to menu.");
                await _ctx.LeaveToMainMenuAsync();
                return;
            }

            var localPlayerId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localPlayerId)) {
                Debug.LogError("[SessionManager] Cannot process match assignment: local UGS player id is missing.");
                await _ctx.LeaveToMainMenuAsync();
                return;
            }

            var hostId = DetermineDeterministicHostId(results.MatchProperties.Players);
            if(string.IsNullOrEmpty(hostId)) {
                Debug.LogError(
                    $"[SessionManager] Could not determine deterministic host for matchId '{matchId}'. Returning to menu.");
                await _ctx.LeaveToMainMenuAsync();
                return;
            }

            if(localPlayerId == hostId) {
                await RunStartPublicMatchAsHostAsync(mode, maxPlayers, matchId, results);
            } else {
                await JoinPublicMatchByIdAsync(matchId);
            }
        }

        /// <summary>
        /// Runs the full public match host flow: sign-in, mode, expected players, create DA session, create lobby, pre-fade, mark ready, wait for players, set lobby state, load scene.
        /// Called by SessionManager.StartPublicMatchAsHostAsync (IMatchmakerSessionActions) and from assignment handler.
        /// </summary>
        public async UniTask RunStartPublicMatchAsHostAsync(string mode, int maxPlayers, string matchId,
            StoredMatchmakingResults results) {
            await _ctx.EnsureSignedInAsync();
            if(Debug.isDebugBuild) {
                Debug.Log(
                    $"[SessionManager] StartPublicMatchAsHostAsync: mode='{mode}' maxPlayers={maxPlayers} matchId='{matchId}'");
            }

            _ctx.ApplyRuntimeMode(mode, "UgsPublicMatchHost");

            var expectedPlayerIds = BuildExpectedPlayerIdsFromMatchResults(results);
            if(expectedPlayerIds != null && Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Expecting {expectedPlayerIds.Count} players for sync");
            }

            _ctx.SetExpectedGamePlayerCount(
                expectedPlayerIds is { Count: > 0 } ? expectedPlayerIds.Count : 1,
                "UgsPublicMatchHost");

            var sessionCode =
                await _actions.CreateDistributedAuthoritySessionAsync(maxPlayers, false, "StartPublicMatchAsHostAsync");
            if(string.IsNullOrEmpty(sessionCode)) {
                await _ctx.LeaveToMainMenuAsync();
                return;
            }

            await _actions.CreatePublicMatchLobbyAsHostAsync(mode, maxPlayers, matchId, sessionCode);
            await _actions.PreFadePublicHostAsync();
            await _actions.MarkHostReadyInMatchLobbyAsync();

            if(await _matchLobbyService.WaitForMatchPlayersReadyAsync(_ctx, expectedPlayerIds, 60f, "PublicMatch") ==
               false) {
                Debug.LogError("[SessionManager] Timed out waiting for all players. Aborting to menu...");
                await _ctx.LeaveToMainMenuAsync();
                return;
            }

            if(await _actions.TrySetMatchLobbyStateAsync("LoadingScene",
                   DataObject.VisibilityOptions.Public,
                   "StartPublicMatchAsHostAsync")) {
                if(Debug.isDebugBuild) {
                    Debug.Log("[SessionManager] Updated lobby state to 'LoadingScene'");
                }
            }

            if(!_actions.TryLoadGameplaySceneAsHost("StartPublicMatchAsHostAsync/LoadScene")) {
                await _ctx.LeaveToMainMenuAsync();
            }
        }

        private static List<string> BuildExpectedPlayerIdsFromMatchResults(StoredMatchmakingResults results) {
            if(results?.MatchProperties?.Players == null) return null;
            return results.MatchProperties.Players
                .Select(p => p != null ? p.Id : null)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();
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

            return mode switch {
                "Hopball" => "Hopball",
                "Deathmatch" => "Deathmatch",
                "KOTH" => "KOTH",
                "Gun Tag" => "Gun-Tag",
                "Team Deathmatch" => "Team-Deathmatch",
                _ => ""
            };
        }
    }
}
