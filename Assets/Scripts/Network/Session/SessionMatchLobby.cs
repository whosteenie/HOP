using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Diagnostics;
using Network.Contracts;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;
using Player = Unity.Services.Lobbies.Models.Player;

namespace Network.Session {
    /// <summary>
    /// Owns the UGS lobby tick schedule: SynchronizingLoad watchdog, backfill refresh, and party/match heartbeats.
    /// </summary>
    public sealed class SessionMatchLobby {
        private const float UgsHeartbeatIntervalSeconds = 1f;
        private const float SynchronizingLoadWatchdogSeconds = 30f;

        private const float PartyHeartbeatIntervalSeconds = 20f;
        private const float MatchHeartbeatIntervalSeconds = 15f;
        private const float HeartbeatInitialDelaySeconds = 1f;
        private const float HeartbeatStaggerSeconds = 5f;
        private const int HeartbeatRateLimitWarnStreak = 3;
        private const float HeartbeatRateLimitWarnIntervalSeconds = 30f;
        private const float HeartbeatRateLimitBaseBackoffSeconds = 10f;
        private const float HeartbeatRateLimitMaxBackoffSeconds = 90f;
        private const float BackfillEligibilityRefreshIntervalSeconds = 10f;
        private const string UgsMatchTypeKey = "matchType";
        private const string UgsPartyIdKey = "partyId";
        private const string UgsTargetModeKey = "targetMode";
        private const string UgsRelayJoinCodeKey = "relayJoinCode";
        private const string UgsLobbyStateKey = "lobbyState";
        private const string UgsExpectedPlayersKey = "expectedPlayers";
        private const string UgsFollowMatchLobbyIdKey = "followMatchLobbyId";
        private const string UgsMatchIdKey = "matchId";
        private const string UgsBackfillAllowedKey = "backfillAllowed";
        private const string UgsBackfillReasonKey = "backfillReason";
        private const string UgsMemberReadyKey = "readyToLoad";

        private float _nextUgsHeartbeatTime;

        /// <summary>
        /// Game-provided hook that decides whether a public match is still eligible for backfill.
        /// If not set, backfill remains allowed by default.
        /// </summary>
        public static Func<ISessionContext, (bool allowed, string reason)> BackfillEligibilityProvider { get; set; }

        /// <summary>Build options to set readyToLoad=1 on the local player. Used by SessionManager.</summary>
        private static UpdatePlayerOptions BuildReadyToLoadUpdatePlayerOptions() {
            return new UpdatePlayerOptions {
                Data = new Dictionary<string, PlayerDataObject> {
                    [UgsMemberReadyKey] = new(PlayerDataObject.VisibilityOptions.Member, "1")
                }
            };
        }

        /// <summary>Gets the authoritative game mode from match lobby data or selected mode. Used by scene flow and game-load.</summary>
        public static bool TryGetRuntimeMode(ISessionContext ctx, out string mode, out string source) {
            mode = null;
            source = null;
            var lobby = ctx?.UgsMatchLobby;
            if(lobby?.Data != null && lobby.Data.TryGetValue(UgsTargetModeKey, out var ugsModeObj) &&
               ugsModeObj != null && !string.IsNullOrEmpty(ugsModeObj.Value)) {
                mode = ugsModeObj.Value;
                source = "UgsMatchLobby";
                return true;
            }

            if(ctx == null || string.IsNullOrEmpty(ctx.SelectedGameMode)) return false;
            mode = ctx.SelectedGameMode;
            source = "SelectedGameMode";
            return true;
        }

        /// <summary>Syncs SelectedGameMode from match lobby targetMode data. Call when match lobby snapshot is received.</summary>
        public static void SyncModeFromMatchLobby(ISessionContext ctx, Lobby lobby) {
            if(ctx == null || ctx.IsLeaving || ctx.IsShuttingDown || lobby?.Data == null) return;
            if(!lobby.Data.TryGetValue(UgsTargetModeKey, out var modeObj) || modeObj == null ||
               string.IsNullOrEmpty(modeObj.Value)) return;
            ctx.ApplyRuntimeMode(modeObj.Value, "UgsMatchLobbySync", false);
        }

        /// <summary>Marks the local player as ready in the match lobby (readyToLoad=1). Call for public match host after pre-fade.</summary>
        public static async UniTask MarkHostReadyAsync(ISessionContext ctx, IMatchSnapshotActions snapshotActions) {
            if(ctx?.UgsMatchLobby == null) return;
            var localUgsId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localUgsId)) return;
            try {
                var opts = BuildReadyToLoadUpdatePlayerOptions();
                var updated = await LobbyService.Instance.UpdatePlayerAsync(ctx.UgsMatchLobby.Id, localUgsId, opts);
                ctx.SetUgsMatchLobby(updated);
                snapshotActions.UgsLocalReadySubmitted = true;
                if(Debug.isDebugBuild) Debug.Log("[SessionManager] Host marked as ready");
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to mark host ready: {ex.Message}");
            }
        }

        /// <summary>Build options for creating a private match lobby.</summary>
        private static CreateLobbyOptions BuildPrivateMatchCreateOptions(string currentPartyId, string mode,
            string networkJoinCode, string expectedCsv, Player lobbyPlayer) {
            return new CreateLobbyOptions {
                IsPrivate = true,
                Player = lobbyPlayer,
                Data = new Dictionary<string, DataObject> {
                    [UgsPartyIdKey] = new(DataObject.VisibilityOptions.Member, currentPartyId),
                    [UgsMatchTypeKey] = new(DataObject.VisibilityOptions.Member, "Private"),
                    [UgsTargetModeKey] = new(DataObject.VisibilityOptions.Member, mode),
                    [UgsRelayJoinCodeKey] = new(DataObject.VisibilityOptions.Member, networkJoinCode),
                    [UgsLobbyStateKey] = new(DataObject.VisibilityOptions.Member, "SynchronizingLoad"),
                    [UgsExpectedPlayersKey] = new(DataObject.VisibilityOptions.Member, expectedCsv)
                }
            };
        }

        /// <summary>Build options to update party lobby with follow match lobby id.</summary>
        private static UpdateLobbyOptions BuildPartyFollowMatchOptions(string matchLobbyId) {
            return new UpdateLobbyOptions {
                Data = new Dictionary<string, DataObject> {
                    [UgsFollowMatchLobbyIdKey] = new(DataObject.VisibilityOptions.Member, matchLobbyId),
                    [UgsLobbyStateKey] = new(DataObject.VisibilityOptions.Member, "InMatch")
                }
            };
        }

        /// <summary>Build options for creating a public match lobby.</summary>
        private static CreateLobbyOptions BuildPublicMatchCreateOptions(string mode, string networkJoinCode,
            string matchId, Player lobbyPlayer) {
            return new CreateLobbyOptions {
                IsPrivate = false,
                Player = lobbyPlayer,
                Data = new Dictionary<string, DataObject> {
                    [UgsMatchTypeKey] = new(DataObject.VisibilityOptions.Public, "Public", DataObject.IndexOptions.S3),
                    [UgsTargetModeKey] = new(DataObject.VisibilityOptions.Public, mode, DataObject.IndexOptions.S2),
                    [UgsRelayJoinCodeKey] = new(DataObject.VisibilityOptions.Member, networkJoinCode),
                    [UgsMatchIdKey] = new(DataObject.VisibilityOptions.Public, matchId, DataObject.IndexOptions.S1),
                    [UgsLobbyStateKey] =
                        new(DataObject.VisibilityOptions.Public, "SynchronizingLoad", DataObject.IndexOptions.S4),
                    [UgsBackfillAllowedKey] = new(DataObject.VisibilityOptions.Public, "true"),
                    [UgsBackfillReasonKey] = new(DataObject.VisibilityOptions.Public, "Eligible")
                }
            };
        }

        private static Player BuildLobbyPlayer() {
            var pid = AuthenticationService.Instance.PlayerId;
            var displayName = SessionNetworkLifecycle.GetDisplayNameProvider != null
                ? SessionNetworkLifecycle.GetDisplayNameProvider()
                : "Player";
            var steamId = SessionNetworkLifecycle.GetSteamIdProvider != null
                ? SessionNetworkLifecycle.GetSteamIdProvider()
                : 0UL;

            var data = new Dictionary<string, PlayerDataObject> {
                ["displayName"] = new(PlayerDataObject.VisibilityOptions.Member, displayName)
            };
            if(steamId != 0) {
                data["steamId"] = new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, steamId.ToString());
            }
            return new Player(pid, data: data);
        }

        private float _nextMatchLobbyQueryFailureLogTime;

        /// <summary>Creates a private match lobby and updates the party to follow it. Call after DA session is created.</summary>
        public async UniTask CreatePrivateMatchLobbyAsync(ISessionContext ctx, IPartySessionActions partyActions,
            ILobbyEventActions lobbyEventActions, string mode, int maxPlayers, string joinCode, string expectedCsv) {
            var create = BuildPrivateMatchCreateOptions(ctx.CurrentPartyId, mode, joinCode, expectedCsv, BuildLobbyPlayer());
            var lobby = await LobbyService.Instance.CreateLobbyAsync("HOP Match", maxPlayers, create);
            ctx.SetUgsMatchLobby(lobby);
            await EnsureMatchLobbySubscriptionAsync(ctx, lobbyEventActions, "CreatePrivateMatchLobbyAsync");
            partyActions.TryJoinVoiceForActiveMatch("CreatePrivateMatchLobbyAsync");

            var partyLobby = ctx.UgsPartyLobby;
            if(partyLobby != null && !string.IsNullOrEmpty(partyLobby.Id)) {
                var update = BuildPartyFollowMatchOptions(lobby.Id);
                await LobbyService.Instance.UpdateLobbyAsync(partyLobby.Id, update);
                await partyActions.EnsurePartyLobbySubscriptionAsync("CreatePrivateMatchLobbyAsync/PartyUpdate");
            }
            ctx.UpdateSteamRichPresence();
        }

        /// <summary>Creates a public match lobby as host. Call after DA session is created.</summary>
        public async UniTask CreatePublicMatchLobbyAsync(ISessionContext ctx, IPartySessionActions partyActions,
            ILobbyEventActions lobbyEventActions, string mode, int maxPlayers, string matchId, string joinCode) {
            var create = BuildPublicMatchCreateOptions(mode, joinCode, matchId, BuildLobbyPlayer());
            var lobby = await LobbyService.Instance.CreateLobbyAsync("HOP Match", maxPlayers, create);
            ctx.SetUgsMatchLobby(lobby);
            await EnsureMatchLobbySubscriptionAsync(ctx, lobbyEventActions, "CreatePublicMatchLobbyAsync");
            partyActions.TryJoinVoiceForActiveMatch("CreatePublicMatchLobbyAsync");
            ctx.UpdateSteamRichPresence();
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Created UGS lobby in SynchronizingLoad state. lobbyId='{lobby.Id}'");
            }
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "CreateUgsMatchHost"),
                ("matchId", matchId),
                ("lobbyId", lobby.Id),
                ("mode", mode),
                ("maxPlayers", maxPlayers));
        }

        /// <summary>Joins a match lobby by id, subscribes to events, and applies initial snapshot.</summary>
        public async UniTask<bool> JoinMatchLobbyByIdAsync(ISessionContext ctx, IPartySessionActions partyActions,
            ILobbyEventActions lobbyEventActions, IMatchSnapshotActions snapshotActions, string lobbyId) {
            await ctx.EnsureSignedInAsync();
            if(string.IsNullOrEmpty(lobbyId)) {
                if(Debug.isDebugBuild) Debug.LogWarning("[SessionManager] JoinMatchLobbyByIdAsync called with an empty lobby id.");
                return false;
            }
            if(Debug.isDebugBuild) Debug.Log($"[SessionManager] JoinMatchLobbyByIdAsync called with lobbyId='{lobbyId}'");

            var options = new JoinLobbyByIdOptions { Player = BuildLobbyPlayer() };
            Lobby matchLobby;
            try {
                matchLobby = await LobbyService.Instance.JoinLobbyByIdAsync(lobbyId, options);
            } catch(LobbyServiceException ex) when(ex.Reason is LobbyExceptionReason.LobbyNotFound or LobbyExceptionReason.EntityNotFound) {
                if(Debug.isDebugBuild) Debug.LogWarning($"[SessionManager] Match lobby '{lobbyId}' no longer exists.");
                return false;
            } catch(LobbyServiceException ex) {
                if(Debug.isDebugBuild) Debug.LogWarning($"[SessionManager] Failed to join match lobby '{lobbyId}' (reason: {ex.Reason}): {ex.Message}");
                return false;
            } catch(Exception ex) {
                if(Debug.isDebugBuild) Debug.LogWarning($"[SessionManager] Failed to join match lobby '{lobbyId}': {ex.Message}");
                return false;
            }
            if(matchLobby == null) {
                Debug.LogError("[SessionManager] Failed to join lobby - matchLobby is null");
                return false;
            }

            ctx.SetUgsMatchLobby(matchLobby);
            await EnsureMatchLobbySubscriptionAsync(ctx, lobbyEventActions, "JoinMatchLobbyByIdAsync");
            partyActions.TryJoinVoiceForActiveMatch("JoinMatchLobbyByIdAsync");
            ctx.UpdateSteamRichPresence();
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Successfully joined UGS lobby. hostId='{matchLobby.HostId}', playerCount={matchLobby.Players?.Count ?? 0}");
            }
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "JoinUgsMatchLobby"),
                ("lobbyId", matchLobby.Id),
                ("hostId", matchLobby.HostId),
                ("players", matchLobby.Players != null ? matchLobby.Players.Count : 0));
            HandleMatchLobbySnapshot(ctx, snapshotActions, lobbyEventActions, "JoinMatchLobbyByIdAsync/Initial");
            return true;
        }

        /// <summary>Queries for a lobby by matchId (public match index S1). Used by matchmaker client to discover host-created lobby.</summary>
        public async UniTask<Lobby> QueryMatchLobbyByMatchIdAsync(string matchId) {
            if(string.IsNullOrEmpty(matchId)) return null;
            try {
                var opts = new QueryLobbiesOptions {
                    Filters = new List<QueryFilter> {
                        new(QueryFilter.FieldOptions.S1, matchId, QueryFilter.OpOptions.EQ)
                    }
                };
                var resp = await LobbyService.Instance.QueryLobbiesAsync(opts);
                if(resp?.Results == null || resp.Results.Count == 0) return null;
                return resp.Results[0];
            } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                if(ShouldEmitThrottledLog(ref _nextMatchLobbyQueryFailureLogTime, 10f)) {
                    Debug.LogWarning($"[SessionManager] Rate limited querying match lobby for matchId '{matchId}'.");
                }
                return null;
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextMatchLobbyQueryFailureLogTime, 10f)) {
                    Debug.LogWarning($"[SessionManager] Match lobby query failed for matchId '{matchId}': {ex.Message}");
                }
                return null;
            }
        }

        // Backfill refresh state (moved from SessionManager)
        private bool? _lastPublishedBackfillAllowed;
        private string _lastPublishedBackfillReason;
        private float _nextBackfillEligibilityRefreshTime;

        private bool IsBackfillEligibilityUpdateInFlight { get; set; }

        // Heartbeat state (moved from SessionManager LobbySync)
        private float _nextPartyHeartbeatTime;
        private float _nextMatchHeartbeatTime;
        private string _lastPartyHeartbeatLobbyId;
        private string _lastMatchHeartbeatLobbyId;
        private float _partyHeartbeatBackoffUntil;
        private float _matchHeartbeatBackoffUntil;
        private int _partyHeartbeatRateLimitStreak;
        private int _matchHeartbeatRateLimitStreak;
        private float _nextPartyHeartbeatRateLimitWarnTime;
        private float _nextMatchHeartbeatRateLimitWarnTime;
        private float _nextUgsHeartbeatFailureLogTime;
        private bool _isHeartbeatDispatchInFlight;

        public void SetNextHeartbeatTime(float value) {
            _nextUgsHeartbeatTime = value;
        }

        /// <summary>
        /// Call when local player is promoted to match lobby host (e.g. after DA host change). Resets match heartbeat state so keepalive resumes immediately.
        /// </summary>
        public void ResetMatchHeartbeatForNewHost() {
            _nextMatchHeartbeatTime = 0f;
            _matchHeartbeatBackoffUntil = 0f;
            _matchHeartbeatRateLimitStreak = 0;
        }

        /// <summary>
        /// Call from SessionManager.Update(). Runs SynchronizingLoad watchdog, backfill refresh, and party/match heartbeats.
        /// </summary>
        public void Tick(ISessionContext ctx) {
            if(ctx.IsLeaving || ctx.IsShuttingDown) return;
            if(ctx.UgsPartyLobby == null && ctx.UgsMatchLobby == null) return;

            if(ctx.Phase == SessionPhase.SynchronizingLoad &&
               Time.time - ctx.PhaseStartTime > SynchronizingLoadWatchdogSeconds) {
                Debug.LogError("[SessionManager] Stuck in SynchronizingLoad for >30s. Aborting to menu...");
                FlowLog.Emit(FlowEventIds.AnomalySessionStuck,
                    ("phase", ctx.Phase),
                    ("elapsed", Time.time - ctx.PhaseStartTime));
                ctx.LaunchSessionTask(ctx.LeaveToMainMenuAsync(), "SynchronizingLoadWatchdog/LeaveToMainMenu");
                return;
            }

            if(!(Time.unscaledTime >= _nextUgsHeartbeatTime)) return;
            _nextUgsHeartbeatTime = Time.unscaledTime + UgsHeartbeatIntervalSeconds;

            if(!IsBackfillEligibilityUpdateInFlight) {
                ctx.LaunchSessionTask(RefreshBackfillEligibilityAsync(ctx, force: false),
                    "RefreshBackfillEligibility");
            }

            if(!_isHeartbeatDispatchInFlight) {
                ctx.LaunchSessionTask(SendPartyHeartbeatsAsync(ctx), "SendPartyHeartbeats");
            }
        }

        /// <summary>
        /// Run backfill eligibility refresh (evaluate from game rules + update lobby). Call from SessionManager when force refresh is needed (e.g. OnGameSceneLoadedAsync).
        /// </summary>
        public async UniTask RefreshBackfillEligibilityAsync(ISessionContext ctx, bool force = false) {
            if(IsBackfillEligibilityUpdateInFlight || ctx.IsLeaving || ctx.IsShuttingDown) return;

            var matchLobby = ctx.UgsMatchLobby;
            if(ctx.Phase != SessionPhase.InGame || matchLobby?.Data == null) return;

            if(!matchLobby.Data.TryGetValue(UgsMatchTypeKey, out var matchTypeObj) || matchTypeObj == null ||
               !string.Equals(matchTypeObj.Value, "Public", StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            var localId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localId) || !IsLobbyHostForLocalPlayer(matchLobby, localId)) return;

            if(!force && Time.unscaledTime < _nextBackfillEligibilityRefreshTime) return;

            var (allowed, reason) = EvaluateBackfillEligibility(ctx);
            if(!force &&
               _lastPublishedBackfillAllowed == allowed &&
               string.Equals(_lastPublishedBackfillReason, reason, StringComparison.Ordinal)) {
                _nextBackfillEligibilityRefreshTime = Time.unscaledTime + BackfillEligibilityRefreshIntervalSeconds;
                return;
            }

            IsBackfillEligibilityUpdateInFlight = true;
            try {
                if(await TryUpdateBackfillEligibilityAsync(ctx, allowed, reason, "HeartbeatRefresh")) {
                    _lastPublishedBackfillAllowed = allowed;
                    _lastPublishedBackfillReason = reason;
                }
            } finally {
                _nextBackfillEligibilityRefreshTime = Time.unscaledTime + BackfillEligibilityRefreshIntervalSeconds;
                IsBackfillEligibilityUpdateInFlight = false;
            }
        }

        /// <summary>Game rules: whether this public match is still eligible for backfill join-in-progress.</summary>
        private static (bool allowed, string reason) EvaluateBackfillEligibility(ISessionContext ctx) {
            if(BackfillEligibilityProvider == null) return (true, "NoProvider");
            try {
                return BackfillEligibilityProvider(ctx);
            } catch(Exception ex) {
                if(Debug.isDebugBuild) {
                    Debug.LogWarning($"[SessionMatchLobby] BackfillEligibilityProvider threw: {ex.Message}");
                }
                // Fail-open: allow backfill but surface the reason.
                return (true, "ProviderException");
            }
        }

        private static async UniTask<bool> TryUpdateBackfillEligibilityAsync(ISessionContext ctx, bool allowed, string reason, string context) {
            var matchLobby = ctx.UgsMatchLobby;
            if(matchLobby == null || string.IsNullOrEmpty(matchLobby.Id) || matchLobby.Data == null) return false;
            try {
                var update = new UpdateLobbyOptions {
                    Data = new Dictionary<string, DataObject> {
                        [UgsBackfillAllowedKey] = new(DataObject.VisibilityOptions.Public, allowed ? "true" : "false"),
                        [UgsBackfillReasonKey] = new(DataObject.VisibilityOptions.Public, string.IsNullOrWhiteSpace(reason) ? "Eligible" : reason)
                    }
                };
                var updated = await LobbyService.Instance.UpdateLobbyAsync(matchLobby.Id, update);
                ctx.SetUgsMatchLobby(updated);
                if(Debug.isDebugBuild)
                    Debug.Log($"[SessionManager] Updated public match backfill gate ({context}) allowed={allowed} reason='{reason}'.");
                return true;
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to update public match backfill gate during {context}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Sets match lobby state (e.g. LoadingScene, InGame); preserves backfill keys when visibility is Public.</summary>
        public static async UniTask<bool> TrySetMatchLobbyStateAsync(ISessionContext ctx, string lobbyState,
            DataObject.VisibilityOptions visibility, string context) {
            var matchLobby = ctx.UgsMatchLobby;
            if(matchLobby == null || string.IsNullOrEmpty(matchLobby.Id)) {
                Debug.LogWarning(
                    $"[SessionManager] Cannot set match lobby state to '{lobbyState}' during {context}: no active match lobby.");
                return false;
            }
            var matchLobbyId = matchLobby.Id;
            try {
                var stateObject = visibility == DataObject.VisibilityOptions.Public
                    ? new DataObject(visibility, lobbyState, DataObject.IndexOptions.S4)
                    : new DataObject(visibility, lobbyState);
                var data = new Dictionary<string, DataObject> {
                    [UgsLobbyStateKey] = stateObject
                };
                if(visibility == DataObject.VisibilityOptions.Public && matchLobby.Data != null) {
                    if(matchLobby.Data.TryGetValue(UgsBackfillAllowedKey, out var backfillAllowedObj) &&
                       backfillAllowedObj != null) {
                        data[UgsBackfillAllowedKey] =
                            new DataObject(DataObject.VisibilityOptions.Public, backfillAllowedObj.Value);
                    }
                    if(matchLobby.Data.TryGetValue(UgsBackfillReasonKey, out var backfillReasonObj) &&
                       backfillReasonObj != null) {
                        data[UgsBackfillReasonKey] =
                            new DataObject(DataObject.VisibilityOptions.Public, backfillReasonObj.Value);
                    }
                }
                var update = new UpdateLobbyOptions { Data = data };
                var updated = await LobbyService.Instance.UpdateLobbyAsync(matchLobbyId, update);
                ctx.SetUgsMatchLobby(updated);
                if(visibility == DataObject.VisibilityOptions.Public)
                    LogPublicLobbySnapshot(updated, $"StateUpdate/{context}");
                return true;
            } catch(Exception ex) {
                Debug.LogWarning(
                    $"[SessionManager] Failed to set UGS match lobby state to '{lobbyState}' during {context}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Logs a one-line snapshot of public match lobby state (debug).</summary>
        private static void LogPublicLobbySnapshot(Lobby lobby, string context) {
            if(lobby == null) {
                Debug.LogWarning($"[SessionManager] PublicLobbySnapshot({context}): lobby is null.");
                return;
            }
            var data = lobby.Data;
            var matchType = data != null && data.TryGetValue(UgsMatchTypeKey, out var matchTypeObj) &&
                            matchTypeObj != null
                ? matchTypeObj.Value
                : "";
            if(!string.Equals(matchType, "Public", StringComparison.OrdinalIgnoreCase))
                return;
            var mode = data != null && data.TryGetValue(UgsTargetModeKey, out var modeObj) && modeObj != null
                ? modeObj.Value
                : "";
            var state = data != null && data.TryGetValue(UgsLobbyStateKey, out var stateObj) && stateObj != null
                ? stateObj.Value
                : "";
            var backfillAllowed = data != null &&
                                  data.TryGetValue(UgsBackfillAllowedKey, out var backfillAllowedObj) &&
                                  backfillAllowedObj != null
                ? backfillAllowedObj.Value
                : "";
            var backfillReason = data != null &&
                                 data.TryGetValue(UgsBackfillReasonKey, out var backfillReasonObj) &&
                                 backfillReasonObj != null
                ? backfillReasonObj.Value
                : "";
            var matchId = data != null && data.TryGetValue(UgsMatchIdKey, out var matchIdObj) && matchIdObj != null
                ? matchIdObj.Value
                : "";
            var playerCount = lobby.Players != null ? lobby.Players.Count : 0;
            Debug.Log(
                $"[SessionManager] PublicLobbySnapshot({context}): lobbyId='{lobby.Id}' hostId='{lobby.HostId}' players={playerCount}/{lobby.MaxPlayers} mode='{mode}' state='{state}' matchId='{matchId}' backfillAllowed='{backfillAllowed}' backfillReason='{backfillReason}'");
        }

        private static bool IsLobbyHostForLocalPlayer(Lobby lobby, string localId) {
            return lobby != null &&
                   !string.IsNullOrEmpty(localId) &&
                   string.Equals(lobby.HostId, localId, StringComparison.Ordinal);
        }

        public static bool IsLocalPlayerLobbyHost(Lobby lobby) {
            var localId = AuthenticationService.Instance.PlayerId;
            return IsLobbyHostForLocalPlayer(lobby, localId);
        }

        private static bool ShouldHeartbeatPartyLobby(SessionPhase phase) {
            return phase != SessionPhase.InGame;
        }

        private void RefreshHeartbeatSchedules(ISessionContext ctx, float now) {
            var partyLobby = ctx.UgsPartyLobby;
            var matchLobby = ctx.UgsMatchLobby;

            if(partyLobby == null || string.IsNullOrEmpty(partyLobby.Id)) {
                _lastPartyHeartbeatLobbyId = null;
                _nextPartyHeartbeatTime = 0f;
                _partyHeartbeatRateLimitStreak = 0;
                _partyHeartbeatBackoffUntil = 0f;
            } else if(!string.Equals(_lastPartyHeartbeatLobbyId, partyLobby.Id, StringComparison.Ordinal)) {
                _lastPartyHeartbeatLobbyId = partyLobby.Id;
                _nextPartyHeartbeatTime = now + HeartbeatInitialDelaySeconds;
                _partyHeartbeatRateLimitStreak = 0;
                _partyHeartbeatBackoffUntil = 0f;
            } else if(_nextPartyHeartbeatTime <= 0f) {
                _nextPartyHeartbeatTime = now + HeartbeatInitialDelaySeconds;
            }

            if(matchLobby == null || string.IsNullOrEmpty(matchLobby.Id)) {
                _lastMatchHeartbeatLobbyId = null;
                _nextMatchHeartbeatTime = 0f;
                _matchHeartbeatRateLimitStreak = 0;
                _matchHeartbeatBackoffUntil = 0f;
            } else if(!string.Equals(_lastMatchHeartbeatLobbyId, matchLobby.Id, StringComparison.Ordinal)) {
                _lastMatchHeartbeatLobbyId = matchLobby.Id;
                _nextMatchHeartbeatTime = now + HeartbeatInitialDelaySeconds + HeartbeatStaggerSeconds;
                _matchHeartbeatRateLimitStreak = 0;
                _matchHeartbeatBackoffUntil = 0f;
            } else if(_nextMatchHeartbeatTime <= 0f) {
                _nextMatchHeartbeatTime = now + HeartbeatInitialDelaySeconds + HeartbeatStaggerSeconds;
            }
        }

        private async UniTask SendPartyHeartbeatAsync(string lobbyId) {
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

        private async UniTask SendMatchHeartbeatAsync(string lobbyId) {
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

        private async UniTask SendPartyHeartbeatsAsync(ISessionContext ctx) {
            if(_isHeartbeatDispatchInFlight) return;
            _isHeartbeatDispatchInFlight = true;

            try {
                var localId = AuthenticationService.Instance.PlayerId;
                if(string.IsNullOrEmpty(localId)) return;
                var now = Time.unscaledTime;
                RefreshHeartbeatSchedules(ctx, now);

                var partyLobby = ctx.UgsPartyLobby;
                var matchLobby = ctx.UgsMatchLobby;

                if(ShouldHeartbeatPartyLobby(ctx.Phase) &&
                   partyLobby != null &&
                   IsLobbyHostForLocalPlayer(partyLobby, localId) &&
                   now >= _nextPartyHeartbeatTime &&
                   now >= _partyHeartbeatBackoffUntil) {
                    await SendPartyHeartbeatAsync(partyLobby.Id);
                }

                now = Time.unscaledTime;
                if(matchLobby != null &&
                   IsLobbyHostForLocalPlayer(matchLobby, localId) &&
                   now >= _nextMatchHeartbeatTime &&
                   now >= _matchHeartbeatBackoffUntil) {
                    await SendMatchHeartbeatAsync(matchLobby.Id);
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

        private static bool ShouldEmitThrottledLog(ref float nextLogTime, float intervalSeconds) {
            var now = Time.unscaledTime;
            if(now < nextLogTime) return false;
            nextLogTime = now + intervalSeconds;
            return true;
        }

        #region Players-ready waiter (moved from SessionManager)

        private UniTaskCompletionSource<bool> _playersReadyWaiter;
        private List<string> _playersReadyExpectedPlayerIds;
        private string _playersReadyLobbyId;

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

        private UniTaskCompletionSource<bool> ArmPlayersReadyWaiter(string lobbyId, List<string> expectedPlayerIds) {
            CompletePlayersReadyWaiter(false);
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

        public void CompletePlayersReadyWaiter(bool result) {
            var waiter = _playersReadyWaiter;
            _playersReadyWaiter = null;
            _playersReadyExpectedPlayerIds = null;
            _playersReadyLobbyId = null;
            try {
                waiter?.TrySetResult(result);
            } catch {
                // ignore
            }
        }

        /// <summary>
        /// Waits until all expected players are in the match lobby and marked ready (or timeout / cancel).
        /// Call from SessionManager for private or public match sync.
        /// </summary>
        public async UniTask<bool> WaitForPlayersReadyAsync(ISessionContext ctx, List<string> expectedPlayerIds,
            float timeoutSeconds, string contextLabel) {
            if(ctx.IsLeaving || ctx.IsShuttingDown || ctx.UgsMatchLobby == null) return false;
            if(expectedPlayerIds == null || expectedPlayerIds.Count == 0) return true;
            if(AreAllExpectedPlayersReady(ctx.UgsMatchLobby, expectedPlayerIds)) return true;

            var waiter = ArmPlayersReadyWaiter(ctx.UgsMatchLobby.Id, expectedPlayerIds);
            TryCompletePlayersReadyWaiterFromLobby(ctx.UgsMatchLobby);

            var ready = false;
            async UniTask WaitForReadyAsync() {
                ready = await waiter.Task;
            }

            try {
                var winner = await UniTask.WhenAny(
                    WaitForReadyAsync(),
                    UniTask.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken: ctx.SessionLifetimeToken));
                if(winner == 0 && ready) return true;
                if(Debug.isDebugBuild)
                    Debug.LogWarning($"[SessionManager] Timed out waiting for players ready ({contextLabel}).");
                return false;
            } catch(OperationCanceledException) {
                return false;
            } finally {
                ClearPlayersReadyWaiter(waiter);
            }
        }

        /// <summary>Removes local player from the given match lobby. Used when clearing match state.</summary>
        private static async UniTask LeaveMatchLobbyAsync(string lobbyId) {
            if(string.IsNullOrEmpty(lobbyId)) return;
            try {
                var localId = AuthenticationService.Instance.PlayerId;
                if(!string.IsNullOrEmpty(localId)) {
                    await LobbyService.Instance.RemovePlayerAsync(lobbyId, localId);
                    if(Debug.isDebugBuild) Debug.Log($"[SessionManager] Left UGS match lobby '{lobbyId}'");
                }
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to leave UGS match lobby '{lobbyId}': {ex.Message}");
            }
        }

        /// <summary>Clears UGS match lobby state: leave lobby, unsubscribe events, reset snapshot and DA retry state.</summary>
        public async UniTask ClearMatchStateAsync(ISessionContext ctx, ILobbyEventActions lobbyEventActions, IMatchSnapshotActions snapshotActions) {
            if(Debug.isDebugBuild) Debug.Log("[SessionManager] ClearMatchState called");

            var matchLobbyId = ctx.UgsMatchLobby?.Id;
            if(!string.IsNullOrEmpty(matchLobbyId)) await LeaveMatchLobbyAsync(matchLobbyId);

            CompletePlayersReadyWaiter(false);
            await UnsubscribeMatchLobbyAsync(lobbyEventActions, "ClearMatchStateAsync");
            ctx.SetUgsMatchLobby(null);
            snapshotActions.UgsSyncInProgress = false;
            snapshotActions.UgsLocalReadySubmitted = false;
            snapshotActions.UgsClientStartedForMatch = false;
            snapshotActions.UgsHostPreFadedOut = false;
            ResetDistributedAuthorityJoinRetryState();
            ResetFollowState();
        }

        private void TryCompletePlayersReadyWaiterFromLobby(Lobby lobby) {
            if(_playersReadyWaiter == null || lobby == null) return;
            if(!string.Equals(_playersReadyLobbyId, lobby.Id, StringComparison.Ordinal)) return;
            if(!AreAllExpectedPlayersReady(lobby, _playersReadyExpectedPlayerIds)) return;
            _playersReadyWaiter.TrySetResult(true);
        }

        private static bool TryGetDistributedAuthoritySessionCode(Lobby lobby, out string sessionCode) {
            sessionCode = null;
            if(lobby?.Data == null) return false;
            if(lobby.Data.TryGetValue(UgsRelayJoinCodeKey, out var joinCodeObj) == false || joinCodeObj == null) return false;
            sessionCode = joinCodeObj.Value;
            return string.IsNullOrWhiteSpace(sessionCode) == false;
        }

        private static bool IsPrivateMatchLobby(Lobby lobby) {
            if(lobby?.Data == null) return false;
            return lobby.Data.TryGetValue(UgsMatchTypeKey, out var matchTypeObj) &&
                   matchTypeObj != null &&
                   string.Equals(matchTypeObj.Value, "Private", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Match snapshot, follow, sync, client start, DA retry (moved from SessionManager)

        private bool _isRetryingReadySubmission;
        private float _nextUgsSyncRateLimitLogTime;
        private float _nextUgsClientStartFailureLogTime;
        private bool _isFollowingMatchLobby;
        private string _lastFailedFollowMatchLobbyId;
        private bool _isRetryingDistributedAuthorityJoin;
        private int _distributedAuthorityJoinRetryAttempt;
        private string _distributedAuthorityRetrySessionCode;
        private float _nextDistributedAuthorityJoinRetryTime;
        private float _nextDistributedAuthorityJoinRateLimitLogTime;

        private void ResetDistributedAuthorityJoinRetryState() {
            _isRetryingDistributedAuthorityJoin = false;
            _distributedAuthorityJoinRetryAttempt = 0;
            _distributedAuthorityRetrySessionCode = null;
            _nextDistributedAuthorityJoinRetryTime = 0f;
        }

        public void ResetFollowState() {
            _lastFailedFollowMatchLobbyId = null;
        }

        private void HandleMatchLobbySnapshot(ISessionContext ctx, IMatchSnapshotActions actions, ILobbyEventActions lobbyActions, string source) {
            if(ctx.IsLeaving || ctx.IsShuttingDown || ctx.UgsMatchLobby == null || ctx.Phase == SessionPhase.InGame) return;
            actions.SyncModeFromMatchLobby(ctx.UgsMatchLobby);
            TryCompletePlayersReadyWaiterFromLobby(ctx.UgsMatchLobby);
            if(ctx.UgsMatchLobby.Data == null) return;

            TryGetDistributedAuthoritySessionCode(ctx.UgsMatchLobby, out var sessionCode);
            RefreshDistributedAuthorityJoinRetrySessionCode(sessionCode);

            if(!ctx.UgsMatchLobby.Data.TryGetValue(UgsLobbyStateKey, out var stateObj) || stateObj == null ||
               string.IsNullOrEmpty(stateObj.Value)) return;

            switch(stateObj.Value) {
                case "SynchronizingLoad":
                    if(!actions.UgsLocalReadySubmitted) {
                        ctx.LaunchSessionTask(StartMatchSyncAsync(ctx, actions, lobbyActions), $"{source}/SynchronizingLoad");
                    }
                    return;
                case "LoadingScene":
                    if(IsLocalPlayerLobbyHost(ctx.UgsMatchLobby)) return;
                    if(IsDistributedAuthorityJoinRetryBackoffActive(sessionCode)) return;
                    if(!actions.UgsClientStartedForMatch) {
                        ctx.LaunchSessionTask(actions.StartMatchClientAsync(expectedSessionCode: sessionCode), $"{source}/LoadingScene");
                    }
                    return;
                case "InGame":
                    if(IsLocalPlayerLobbyHost(ctx.UgsMatchLobby)) return;
                    actions.UgsLocalReadySubmitted = true;
                    if(IsDistributedAuthorityJoinRetryBackoffActive(sessionCode)) return;
                    if(!actions.UgsClientStartedForMatch) {
                        ctx.LaunchSessionTask(actions.StartMatchClientAsync(useFadeOut: true, expectedSessionCode: sessionCode), $"{source}/InGame");
                    }
                    return;
            }
        }

        private async UniTask HandlePartyLobbyFollowStateAsync(ISessionContext ctx, IMatchSnapshotActions actions, string source) {
            if(ctx.IsLeaving || ctx.IsShuttingDown || ctx.Phase == SessionPhase.InGame || _isFollowingMatchLobby) return;
            if(ctx.UgsPartyLobby?.Data == null) return;
            if(!ctx.UgsPartyLobby.Data.TryGetValue(UgsFollowMatchLobbyIdKey, out var followObj) || followObj == null ||
               string.IsNullOrEmpty(followObj.Value)) {
                _lastFailedFollowMatchLobbyId = null;
                return;
            }

            var followLobbyId = followObj.Value;
            if(_lastFailedFollowMatchLobbyId == followLobbyId) return;
            if(ctx.UgsMatchLobby != null && string.Equals(ctx.UgsMatchLobby.Id, followLobbyId, StringComparison.Ordinal)) return;

            _isFollowingMatchLobby = true;
            try {
                var joined = await actions.JoinMatchLobbyByIdAsync(followLobbyId);
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

        private void RefreshDistributedAuthorityJoinRetrySessionCode(string sessionCode) {
            if(string.IsNullOrWhiteSpace(sessionCode)) return;
            if(string.Equals(_distributedAuthorityRetrySessionCode, sessionCode, StringComparison.Ordinal)) return;
            ResetDistributedAuthorityJoinRetryState();
            _distributedAuthorityRetrySessionCode = sessionCode;
        }

        private bool IsDistributedAuthorityJoinRetryBackoffActive(string sessionCode) {
            if(string.IsNullOrWhiteSpace(sessionCode)) return false;
            RefreshDistributedAuthorityJoinRetrySessionCode(sessionCode);
            return Time.unscaledTime < _nextDistributedAuthorityJoinRetryTime;
        }

        private void ScheduleDistributedAuthorityJoinRetry(ISessionContext ctx, IMatchSnapshotActions actions, string sessionCode, bool isPrivateMatch) {
            if(ctx.IsLeaving || ctx.IsShuttingDown || string.IsNullOrWhiteSpace(sessionCode)) return;
            RefreshDistributedAuthorityJoinRetrySessionCode(sessionCode);

            var delaySeconds = SessionNetworkLifecycle.ComputeDaJoinRetryDelaySeconds(_distributedAuthorityJoinRetryAttempt);
            _distributedAuthorityJoinRetryAttempt++;
            _nextDistributedAuthorityJoinRetryTime = Time.unscaledTime + delaySeconds;

            if(ShouldEmitThrottledLog(ref _nextDistributedAuthorityJoinRateLimitLogTime, 3f)) {
                Debug.LogWarning(
                    $"[SessionManager] Backing off DA join for {delaySeconds:0.0}s before retrying session '{sessionCode}'.");
            }

            if(_isRetryingDistributedAuthorityJoin) return;
            _isRetryingDistributedAuthorityJoin = true;
            ctx.LaunchSessionTask(RetryStartMatchClientAsync(ctx, actions, sessionCode, isPrivateMatch), "DistributedAuthority/RetryJoin");
        }

        private async UniTask RetryStartMatchClientAsync(ISessionContext ctx, IMatchSnapshotActions actions, string sessionCode, bool isPrivateMatch) {
            try {
                while(!ctx.IsLeaving && !ctx.IsShuttingDown) {
                    if(ctx.UgsMatchLobby == null) return;
                    if(!string.Equals(_distributedAuthorityRetrySessionCode, sessionCode, StringComparison.Ordinal)) return;
                    if(!TryGetDistributedAuthoritySessionCode(ctx.UgsMatchLobby, out var currentSessionCode) ||
                       !string.Equals(currentSessionCode, sessionCode, StringComparison.Ordinal)) return;

                    var remainingDelay = _nextDistributedAuthorityJoinRetryTime - Time.unscaledTime;
                    if(remainingDelay > 0f) {
                        try {
                            await UniTask.Delay(TimeSpan.FromSeconds(remainingDelay), cancellationToken: ctx.SessionLifetimeToken);
                        } catch(OperationCanceledException) {
                            return;
                        }
                    }

                    _isRetryingDistributedAuthorityJoin = false;
                    await actions.StartMatchClientAsync(useFadeOut: false, expectedSessionCode: sessionCode, expectedIsPrivateMatch: isPrivateMatch);
                    return;
                }
            } finally {
                _isRetryingDistributedAuthorityJoin = false;
            }
        }

        public async UniTask StartMatchSyncAsync(ISessionContext ctx, IMatchSnapshotActions actions, ILobbyEventActions lobbyActions, bool skipFadeOut = false) {
            if(ctx.UgsMatchLobby == null || actions.UgsLocalReadySubmitted || actions.UgsSyncInProgress) return;

            actions.UgsSyncInProgress = true;
            ctx.SetPhase(SessionPhase.SynchronizingLoad);
            ctx.SetFrontStatus(SessionPhase.SynchronizingLoad, "Waiting for party...");

            if(actions.UgsHostPreFadedOut) {
                actions.UgsHostPreFadedOut = false;
            } else if(!skipFadeOut) {
                await actions.FadeOutWithFallbackAsync();
            }

            var localUgsId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localUgsId)) {
                Debug.LogError("[SessionManager] Cannot submit ready state: local UGS player id is missing.");
                actions.UgsSyncInProgress = false;
                return;
            }

            try {
                var opts = BuildReadyToLoadUpdatePlayerOptions();
                var updatedLobby = await LobbyService.Instance.UpdatePlayerAsync(ctx.UgsMatchLobby.Id, localUgsId, opts);
                ctx.SetUgsMatchLobby(updatedLobby);
                actions.UgsLocalReadySubmitted = true;
                TryCompletePlayersReadyWaiterFromLobby(updatedLobby);
            } catch(LobbyServiceException ex) when(ex.Reason == LobbyExceptionReason.RateLimited) {
                if(ShouldEmitThrottledLog(ref _nextUgsSyncRateLimitLogTime, 5f)) {
                    Debug.LogWarning("[SessionManager] Rate limited updating ready state. Retrying shortly...");
                }
                ctx.LaunchSessionTask(RetrySubmitReadyStateAsync(ctx, actions, lobbyActions), "StartMatchSynchronization/RetryReady");
            } catch(Exception ex) {
                Debug.LogError($"[SessionManager] Failed to update ready state: {ex.Message}. Aborting to menu...");
                await actions.LeaveToMainMenuAsync();
            } finally {
                actions.UgsSyncInProgress = false;
            }
        }

        private async UniTask RetrySubmitReadyStateAsync(ISessionContext ctx, IMatchSnapshotActions actions, ILobbyEventActions lobbyActions) {
            if(_isRetryingReadySubmission) return;
            _isRetryingReadySubmission = true;
            try {
                var retryDelayMs = 1000;
                while(!ctx.IsLeaving && !ctx.IsShuttingDown && ctx.UgsMatchLobby != null && !actions.UgsLocalReadySubmitted) {
                    try {
                        await UniTask.Delay(retryDelayMs, cancellationToken: ctx.SessionLifetimeToken);
                    } catch(OperationCanceledException) {
                        return;
                    }
                    if(actions.UgsSyncInProgress) {
                        retryDelayMs = Mathf.Min(retryDelayMs + 500, 5000);
                        continue;
                    }
                    await StartMatchSyncAsync(ctx, actions, lobbyActions, skipFadeOut: true);
                    retryDelayMs = Mathf.Min(retryDelayMs + 500, 5000);
                }
            } finally {
                _isRetryingReadySubmission = false;
            }
        }

        public async UniTask StartMatchClientAsync(ISessionContext ctx, IMatchSnapshotActions actions, bool useFadeOut = false, string expectedSessionCode = null, bool? expectedIsPrivateMatch = null) {
            if(ctx.UgsMatchLobby == null || actions.UgsClientStartedForMatch || !actions.UgsLocalReadySubmitted) return;
            if(ctx.IsLeaving || ctx.IsShuttingDown) return;

            if(ctx.UgsMatchLobby.Data == null) {
                if(ShouldEmitThrottledLog(ref _nextUgsClientStartFailureLogTime, 10f)) {
                    Debug.LogWarning("[SessionManager] Cannot start match client: match lobby data is unavailable.");
                }
                return;
            }

            if(!TryGetDistributedAuthoritySessionCode(ctx.UgsMatchLobby, out var sessionCode)) {
                if(ShouldEmitThrottledLog(ref _nextUgsClientStartFailureLogTime, 10f)) {
                    Debug.LogWarning("[SessionManager] Cannot start match client: DA session code has not been published.");
                }
                return;
            }

            RefreshDistributedAuthorityJoinRetrySessionCode(sessionCode);
            if(IsDistributedAuthorityJoinRetryBackoffActive(sessionCode)) return;
            if(!string.IsNullOrWhiteSpace(expectedSessionCode) && !string.Equals(expectedSessionCode, sessionCode, StringComparison.Ordinal)) return;

            actions.SyncModeFromMatchLobby(ctx.UgsMatchLobby);

            actions.UgsClientStartedForMatch = true;
            var shouldResetClientStartFlag = true;

            try {
                ctx.SetPhase(SessionPhase.StartingClient);

                if(useFadeOut) {
                    await actions.FadeOutWithFallbackAsync();
                    if(ctx.IsLeaving || ctx.IsShuttingDown) return;
                }

                var isPrivateMatch = expectedIsPrivateMatch ?? IsPrivateMatchLobby(ctx.UgsMatchLobby);
                var joinResult = await actions.JoinDaSessionAsync(sessionCode, isPrivateMatch, "StartMatchClientAsync");
                switch(joinResult) {
                    case DaSessionJoinResult.Success:
                        ResetDistributedAuthorityJoinRetryState();
                        shouldResetClientStartFlag = false;
                        return;
                    case DaSessionJoinResult.RateLimited:
                        ScheduleDistributedAuthorityJoinRetry(ctx, actions, sessionCode, isPrivateMatch);
                        return;
                    default:
                        Debug.LogError("[SessionManager] Failed to start DA match client after cleanup.");
                        ResetDistributedAuthorityJoinRetryState();
                        await actions.LeaveToMainMenuAsync();
                        return;
                }
            } finally {
                if(shouldResetClientStartFlag) actions.UgsClientStartedForMatch = false;
            }
        }

        #endregion

        #region Lobby event subscription (moved from SessionManager)

        private const int LobbyEventSubscriptionRetryBaseDelayMs = 500;
        private const int LobbyEventSubscriptionRetryMaxDelayMs = 10000;
        private const int LobbyEventSubscriptionRetryMaxExponent = 5;
        private const int LobbyEventSubscriptionRetryJitterMs = 250;

        private bool _isSubscribingPartyLobbyEvents;
        private bool _isSubscribingMatchLobbyEvents;
        private bool _isResubscribingPartyLobbyEvents;
        private bool _isResubscribingMatchLobbyEvents;
        private bool _isRetryingPartyLobbyEventsSubscription;
        private bool _isRetryingMatchLobbyEventsSubscription;
        private int _partyLobbyEventsSubscriptionRetryAttempt;
        private int _matchLobbyEventsSubscriptionRetryAttempt;
        private float _nextLobbyEventSubscriptionFailureLogTime;
        private ILobbyEvents _partyLobbyEvents;
        private ILobbyEvents _matchLobbyEvents;

        public async UniTask EnsurePartyLobbySubscriptionAsync(ISessionContext ctx, ILobbyEventActions actions, string context) {
            var partyLobby = ctx.UgsPartyLobby;
            if(partyLobby == null || string.IsNullOrEmpty(partyLobby.Id)) {
                await UnsubscribePartyLobbyAsync(context + "/NoPartyLobby");
                return;
            }

            var targetLobbyId = partyLobby.Id;
            if(_partyLobbyEvents != null && string.Equals(PartyLobbyEventsLobbyId, targetLobbyId, StringComparison.Ordinal))
                return;
            if(_isSubscribingPartyLobbyEvents) return;

            _isSubscribingPartyLobbyEvents = true;
            try {
                await UnsubscribePartyLobbyAsync(context + "/Replace");

                var snapshotActions = actions as IMatchSnapshotActions;
                var callbacks = new LobbyEventCallbacks();
                callbacks.LobbyChanged += changes =>
                    ctx.LaunchSessionTask(HandlePartyLobbyChangedAsync(ctx, snapshotActions, changes), "PartyLobbyEvents/LobbyChanged");
                callbacks.LobbyDeleted += () =>
                    ctx.LaunchSessionTask(HandlePartyLobbyDeletedOrKickedAsync(ctx, "LobbyDeleted"), "PartyLobbyEvents/LobbyDeleted");
                callbacks.KickedFromLobby += () =>
                    ctx.LaunchSessionTask(HandlePartyLobbyDeletedOrKickedAsync(ctx, "KickedFromLobby"), "PartyLobbyEvents/KickedFromLobby");
                callbacks.LobbyEventConnectionStateChanged += state =>
                    ctx.LaunchSessionTask(HandlePartyLobbyEventConnectionStateChangedAsync(ctx, state), "PartyLobbyEvents/ConnectionStateChanged");

                _partyLobbyEvents = await LobbyService.Instance.SubscribeToLobbyEventsAsync(targetLobbyId, callbacks);
                PartyLobbyEventsLobbyId = targetLobbyId;
                _partyLobbyEventsSubscriptionRetryAttempt = 0;
                if(snapshotActions != null)
                    await HandlePartyLobbyFollowStateAsync(ctx, snapshotActions, context + "/Initial");
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f))
                    Debug.LogWarning($"[SessionManager] Failed to subscribe to party lobby events ({context}): {ex.Message}");
                SchedulePartyLobbyEventsSubscriptionRetry(ctx, actions, context);
            } finally {
                _isSubscribingPartyLobbyEvents = false;
            }
        }

        private async UniTask EnsureMatchLobbySubscriptionAsync(ISessionContext ctx, ILobbyEventActions actions, string context) {
            var matchLobby = ctx.UgsMatchLobby;
            if(matchLobby == null || string.IsNullOrEmpty(matchLobby.Id)) {
                await UnsubscribeMatchLobbyAsync(actions, context + "/NoMatchLobby");
                return;
            }

            var targetLobbyId = matchLobby.Id;
            if(_matchLobbyEvents != null && string.Equals(MatchLobbyEventsLobbyId, targetLobbyId, StringComparison.Ordinal))
                return;
            if(_isSubscribingMatchLobbyEvents) return;

            _isSubscribingMatchLobbyEvents = true;
            try {
                await UnsubscribeMatchLobbyAsync(actions, context + "/Replace");

                var snapshotActions = actions as IMatchSnapshotActions;
                var callbacks = new LobbyEventCallbacks();
                callbacks.LobbyChanged += changes =>
                    ctx.LaunchSessionTask(HandleMatchLobbyChangedAsync(ctx, snapshotActions, actions, changes), "MatchLobbyEvents/LobbyChanged");
                callbacks.LobbyDeleted += () =>
                    ctx.LaunchSessionTask(HandleMatchLobbyDeletedOrKickedAsync(ctx, snapshotActions, actions, "LobbyDeleted"), "MatchLobbyEvents/LobbyDeleted");
                callbacks.KickedFromLobby += () =>
                    ctx.LaunchSessionTask(HandleMatchLobbyDeletedOrKickedAsync(ctx, snapshotActions, actions, "KickedFromLobby"), "MatchLobbyEvents/KickedFromLobby");
                callbacks.LobbyEventConnectionStateChanged += state =>
                    ctx.LaunchSessionTask(HandleMatchLobbyEventConnectionStateChangedAsync(ctx, state), "MatchLobbyEvents/ConnectionStateChanged");

                _matchLobbyEvents = await LobbyService.Instance.SubscribeToLobbyEventsAsync(targetLobbyId, callbacks);
                MatchLobbyEventsLobbyId = targetLobbyId;
                _matchLobbyEventsSubscriptionRetryAttempt = 0;

                if(Debug.isDebugBuild)
                    Debug.Log($"[SessionManager] Subscribed to match lobby events ({context}) lobbyId='{targetLobbyId}'.");
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f))
                    Debug.LogWarning($"[SessionManager] Failed to subscribe to match lobby events ({context}): {ex.Message}");
                ScheduleMatchLobbyEventsSubscriptionRetry(ctx, actions, context);
            } finally {
                _isSubscribingMatchLobbyEvents = false;
            }
        }

        public async UniTask UnsubscribePartyLobbyAsync(string context) {
            var eventsHandle = _partyLobbyEvents;

            _partyLobbyEvents = null;
            PartyLobbyEventsLobbyId = null;
            _isResubscribingPartyLobbyEvents = false;

            if(eventsHandle == null) return;

            try {
                await eventsHandle.UnsubscribeAsync();
            } catch(Exception ex) {
                if(Debug.isDebugBuild && ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f))
                    Debug.LogWarning($"[SessionManager] Failed to unsubscribe from party lobby events ({context}): {ex.Message}");
            }
        }

        public async UniTask UnsubscribeMatchLobbyAsync(ILobbyEventActions actions, string context) {
            actions.CompletePlayersReadyWaiter(false);

            var eventsHandle = _matchLobbyEvents;

            _matchLobbyEvents = null;
            MatchLobbyEventsLobbyId = null;
            _isResubscribingMatchLobbyEvents = false;

            if(eventsHandle == null) return;

            try {
                await eventsHandle.UnsubscribeAsync();
            } catch(Exception ex) {
                if(Debug.isDebugBuild && ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f))
                    Debug.LogWarning($"[SessionManager] Failed to unsubscribe from match lobby events ({context}): {ex.Message}");
            }
        }

        private async UniTask ResubscribePartyLobbyEventsAsync(ISessionContext ctx, string context) {
            if(_isResubscribingPartyLobbyEvents || _partyLobbyEvents == null || ctx.UgsPartyLobby == null) return;
            if(!string.Equals(PartyLobbyEventsLobbyId, ctx.UgsPartyLobby.Id, StringComparison.Ordinal)) return;

            _isResubscribingPartyLobbyEvents = true;
            try {
                await _partyLobbyEvents.SubscribeAsync();
                if(Debug.isDebugBuild)
                    Debug.Log($"[SessionManager] Re-subscribed party lobby events ({context}) lobbyId='{PartyLobbyEventsLobbyId}'.");
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f))
                    Debug.LogWarning($"[SessionManager] Failed to re-subscribe party lobby events ({context}): {ex.Message}");
            } finally {
                _isResubscribingPartyLobbyEvents = false;
            }
        }

        private async UniTask ResubscribeMatchLobbyEventsAsync(ISessionContext ctx, string context) {
            if(_isResubscribingMatchLobbyEvents || _matchLobbyEvents == null || ctx.UgsMatchLobby == null) return;
            if(!string.Equals(MatchLobbyEventsLobbyId, ctx.UgsMatchLobby.Id, StringComparison.Ordinal)) return;

            _isResubscribingMatchLobbyEvents = true;
            try {
                await _matchLobbyEvents.SubscribeAsync();
                if(Debug.isDebugBuild)
                    Debug.Log($"[SessionManager] Re-subscribed match lobby events ({context}) lobbyId='{MatchLobbyEventsLobbyId}'.");
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f))
                    Debug.LogWarning($"[SessionManager] Failed to re-subscribe match lobby events ({context}): {ex.Message}");
            } finally {
                _isResubscribingMatchLobbyEvents = false;
            }
        }

        private void SchedulePartyLobbyEventsSubscriptionRetry(ISessionContext ctx, ILobbyEventActions actions, string context) {
            if(ctx.IsLeaving || ctx.IsShuttingDown) return;
            if(_isRetryingPartyLobbyEventsSubscription) return;
            _partyLobbyEventsSubscriptionRetryAttempt = 0;
            _isRetryingPartyLobbyEventsSubscription = true;
            ctx.LaunchSessionTask(RetryEnsurePartyLobbySubscriptionAsync(ctx, actions, context), "PartyLobbyEvents/RetryEnsure");
        }

        private void ScheduleMatchLobbyEventsSubscriptionRetry(ISessionContext ctx, ILobbyEventActions actions, string context) {
            if(ctx.IsLeaving || ctx.IsShuttingDown) return;
            if(_isRetryingMatchLobbyEventsSubscription) return;
            _matchLobbyEventsSubscriptionRetryAttempt = 0;
            _isRetryingMatchLobbyEventsSubscription = true;
            ctx.LaunchSessionTask(RetryEnsureMatchLobbySubscriptionAsync(ctx, actions, context), "MatchLobbyEvents/RetryEnsure");
        }

        private async UniTask RetryEnsurePartyLobbySubscriptionAsync(ISessionContext ctx, ILobbyEventActions actions, string context) {
            try {
                while(!ctx.IsLeaving && !ctx.IsShuttingDown) {
                    if(ctx.UgsPartyLobby == null || string.IsNullOrEmpty(ctx.UgsPartyLobby.Id)) return;
                    if(_partyLobbyEvents != null && string.Equals(PartyLobbyEventsLobbyId, ctx.UgsPartyLobby.Id, StringComparison.Ordinal)) {
                        _partyLobbyEventsSubscriptionRetryAttempt = 0;
                        return;
                    }

                    var retryDelayMs = ComputeLobbyEventSubscriptionRetryDelayMs(_partyLobbyEventsSubscriptionRetryAttempt);
                    _partyLobbyEventsSubscriptionRetryAttempt++;
                    try {
                        await UniTask.Delay(retryDelayMs, cancellationToken: ctx.SessionLifetimeToken);
                    } catch(OperationCanceledException) {
                        return;
                    }
                    await EnsurePartyLobbySubscriptionAsync(ctx, actions, context + "/Retry#" + _partyLobbyEventsSubscriptionRetryAttempt);
                }
            } finally {
                _isRetryingPartyLobbyEventsSubscription = false;
            }
        }

        private async UniTask RetryEnsureMatchLobbySubscriptionAsync(ISessionContext ctx, ILobbyEventActions actions, string context) {
            try {
                while(!ctx.IsLeaving && !ctx.IsShuttingDown) {
                    if(ctx.UgsMatchLobby == null || string.IsNullOrEmpty(ctx.UgsMatchLobby.Id)) return;
                    if(_matchLobbyEvents != null && string.Equals(MatchLobbyEventsLobbyId, ctx.UgsMatchLobby.Id, StringComparison.Ordinal)) {
                        _matchLobbyEventsSubscriptionRetryAttempt = 0;
                        return;
                    }

                    var retryDelayMs = ComputeLobbyEventSubscriptionRetryDelayMs(_matchLobbyEventsSubscriptionRetryAttempt);
                    _matchLobbyEventsSubscriptionRetryAttempt++;
                    try {
                        await UniTask.Delay(retryDelayMs, cancellationToken: ctx.SessionLifetimeToken);
                    } catch(OperationCanceledException) {
                        return;
                    }
                    await EnsureMatchLobbySubscriptionAsync(ctx, actions, context + "/Retry#" + _matchLobbyEventsSubscriptionRetryAttempt);
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

        /// <summary>Expose for handler logging.</summary>
        private string PartyLobbyEventsLobbyId { get; set; }

        private string MatchLobbyEventsLobbyId { get; set; }

        private float NextLobbyEventConnectionLogTime { get; set; }

        /// <summary>Handles party lobby changed event (apply changes, notify, follow state).</summary>
        private async UniTask HandlePartyLobbyChangedAsync(ISessionContext ctx, IMatchSnapshotActions snapshotActions, ILobbyChanges changes) {
            await UniTask.SwitchToMainThread();
            if(ctx.IsLeaving || ctx.IsShuttingDown || ctx.UgsPartyLobby == null || changes == null) return;
            if(!string.IsNullOrEmpty(PartyLobbyEventsLobbyId) &&
               !string.Equals(PartyLobbyEventsLobbyId, ctx.UgsPartyLobby.Id, StringComparison.Ordinal))
                return;
            try {
                changes.ApplyToLobby(ctx.UgsPartyLobby);
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f))
                    Debug.LogWarning($"[SessionManager] Failed applying party lobby changes: {ex.Message}");
                return;
            }
            ctx.NotifyPartyStateChanged();
            if(snapshotActions != null)
                await HandlePartyLobbyFollowStateAsync(ctx, snapshotActions, "PartyLobbyEvents/LobbyChanged");
        }

        /// <summary>Handles party lobby deleted or kicked (clear cache, unsubscribe, notify).</summary>
        private async UniTask HandlePartyLobbyDeletedOrKickedAsync(ISessionContext ctx, string reason) {
            await UniTask.SwitchToMainThread();
            if(Debug.isDebugBuild)
                Debug.LogWarning($"[SessionManager] Party lobby event: {reason}. Clearing local party lobby cache.");
            ctx.SetUgsPartyLobby(null);
            ctx.SetIsPartyLeader(false);
            ResetFollowState();
            await UnsubscribePartyLobbyAsync($"PartyLobbyEvents/{reason}");
            ctx.NotifyPartyStateChanged();
        }

        /// <summary>Handles party lobby connection state (log, resubscribe on error/unsynced).</summary>
        private async UniTask HandlePartyLobbyEventConnectionStateChangedAsync(ISessionContext ctx, LobbyEventConnectionState state) {
            await UniTask.SwitchToMainThread();
            if(Debug.isDebugBuild && ShouldEmitThrottledLogForConnectionState()) {
                Debug.Log(
                    $"[SessionManager] Party lobby events connection state: {state} (lobbyId='{PartyLobbyEventsLobbyId}').");
            }
            if(state is LobbyEventConnectionState.Error or LobbyEventConnectionState.Unsynced)
                await ResubscribePartyLobbyEventsAsync(ctx, $"ConnectionState/{state}");
        }

        /// <summary>Handles match lobby changed event (apply changes, complete waiter, snapshot).</summary>
        private async UniTask HandleMatchLobbyChangedAsync(ISessionContext ctx, IMatchSnapshotActions snapshotActions,
            ILobbyEventActions lobbyActions, ILobbyChanges changes) {
            await UniTask.SwitchToMainThread();
            if(ctx.IsLeaving || ctx.IsShuttingDown || ctx.UgsMatchLobby == null || changes == null) return;
            if(!string.IsNullOrEmpty(MatchLobbyEventsLobbyId) &&
               !string.Equals(MatchLobbyEventsLobbyId, ctx.UgsMatchLobby.Id, StringComparison.Ordinal))
                return;
            try {
                changes.ApplyToLobby(ctx.UgsMatchLobby);
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextLobbyEventSubscriptionFailureLogTime, 10f))
                    Debug.LogWarning($"[SessionManager] Failed applying match lobby changes: {ex.Message}");
                return;
            }
            TryCompletePlayersReadyWaiterFromLobby(ctx.UgsMatchLobby);
            HandleMatchLobbySnapshot(ctx, snapshotActions, lobbyActions, "MatchLobbyEvents/LobbyChanged");
        }

        /// <summary>Handles match lobby deleted or kicked (clear state, unsubscribe, rich presence).</summary>
        private async UniTask HandleMatchLobbyDeletedOrKickedAsync(ISessionContext ctx, IMatchSnapshotActions snapshotActions,
            ILobbyEventActions lobbyActions, string reason) {
            await UniTask.SwitchToMainThread();
            if(Debug.isDebugBuild)
                Debug.LogWarning($"[SessionManager] Match lobby event: {reason}. Clearing local match lobby cache.");
            lobbyActions.CompletePlayersReadyWaiter(false);
            snapshotActions.UgsSyncInProgress = false;
            snapshotActions.UgsClientStartedForMatch = false;
            snapshotActions.UgsHostPreFadedOut = false;
            if(ctx.Phase != SessionPhase.InGame) {
                snapshotActions.UgsLocalReadySubmitted = false;
                ctx.SetUgsMatchLobby(null);
            }
            await UnsubscribeMatchLobbyAsync(lobbyActions, $"MatchLobbyEvents/{reason}");
            ctx.UpdateSteamRichPresence();
        }

        /// <summary>Handles match lobby connection state (log, resubscribe on error/unsynced).</summary>
        private async UniTask HandleMatchLobbyEventConnectionStateChangedAsync(ISessionContext ctx, LobbyEventConnectionState state) {
            await UniTask.SwitchToMainThread();
            if(Debug.isDebugBuild && ShouldEmitThrottledLogForConnectionState()) {
                Debug.Log(
                    $"[SessionManager] Match lobby events connection state: {state} (lobbyId='{MatchLobbyEventsLobbyId}').");
            }
            if(state is LobbyEventConnectionState.Error or LobbyEventConnectionState.Unsynced)
                await ResubscribeMatchLobbyEventsAsync(ctx, $"ConnectionState/{state}");
        }

        private bool ShouldEmitThrottledLogForConnectionState() {
            var now = Time.unscaledTime;
            if(now < NextLobbyEventConnectionLogTime) return false;
            NextLobbyEventConnectionLogTime = now + 2f;
            return true;
        }

        #endregion
    }
}
