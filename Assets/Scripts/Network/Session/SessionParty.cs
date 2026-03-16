using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Diagnostics;
using Game.Match;
using Game.Social;
using Network.Core;
using Network.SessionContracts;
using Steamworks;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using UnityEngine;
using Lobby = Unity.Services.Lobbies.Models.Lobby;
using Player = Unity.Services.Lobbies.Models.Player;

namespace Network.Session {
    /// <summary>
    /// Creates and joins UGS party lobbies; coordinates with Steam social lobby and event subscription.
    /// </summary>
    public sealed class SessionParty {
        private const string UgsPartyIdKey = "partyId";
        private const string UgsFollowMatchLobbyIdKey = "followMatchLobbyId";
        private const string UgsLobbyStateKey = "lobbyState";

        private bool _isCreatingPartyLobby;

        public async UniTask CreatePartyLobbyAsync(ISessionContext ctx, IPartySessionActions actions, int maxPlayers, bool isPrivate) {
            if(ctx.UgsPartyLobby != null) return;

            if(_isCreatingPartyLobby) {
                var waitStart = Time.realtimeSinceStartup;
                while(_isCreatingPartyLobby && Time.realtimeSinceStartup - waitStart < 5f) {
                    try {
                        await UniTask.DelayFrame(1, cancellationToken: ctx.SessionLifetimeToken);
                    } catch(OperationCanceledException) {
                        return;
                    }
                }

                if(ctx.UgsPartyLobby != null) return;
            }

            _isCreatingPartyLobby = true;
            try {
                await ctx.EnsureSignedInAsync();

                var currentPartyId = ctx.CurrentPartyId;
                if(string.IsNullOrEmpty(currentPartyId)) {
                    currentPartyId = Guid.NewGuid().ToString();
                    ctx.SetCurrentPartyId(currentPartyId);
                }

                var options = new CreateLobbyOptions {
                    IsPrivate = isPrivate,
                    Player = BuildLobbyPlayer(),
                    Data = new Dictionary<string, DataObject> {
                        [UgsPartyIdKey] = new(DataObject.VisibilityOptions.Member, currentPartyId),
                        [UgsFollowMatchLobbyIdKey] = new(DataObject.VisibilityOptions.Member, ""),
                        [UgsLobbyStateKey] = new(DataObject.VisibilityOptions.Member, "Party")
                    }
                };

                var partyLobby = await LobbyService.Instance.CreateLobbyAsync("HOP Party", maxPlayers, options);
                ctx.SetUgsPartyLobby(partyLobby);
                ctx.SetUgsMatchLobby(null);
                var localUgsId = AuthenticationService.Instance.PlayerId;
                ctx.SetIsPartyLeader(partyLobby != null && partyLobby.HostId == localUgsId);

                await actions.UnsubscribeMatchLobbyAsync("CreatePartyLobbyAsync/ResetMatch");
                await actions.EnsurePartyLobbySubscriptionAsync("CreatePartyLobbyAsync");

                if(SteamClient.IsValid && SteamClient.IsLoggedOn) {
                    if(!ctx.CurrentLobby.HasValue) {
                        var socialLobbyCreated = await actions.CreateSteamSocialLobbyAsync(maxPlayers);
                        if(!socialLobbyCreated && Debug.isDebugBuild) {
                            Debug.LogWarning("[SessionManager] UGS party created, but Steam social lobby creation failed.");
                        }
                    } else {
                        actions.UpdateSteamLobbyWithPartyDataIfOwner();
                    }
                }

                actions.SetNextUgsHeartbeatTime(Time.unscaledTime + 1f);
                ctx.UpdateSteamRichPresence();
                FlowLog.Emit(FlowEventIds.PartyLifecycle,
                    ("action", "CreateUgsParty"),
                    ("partyId", currentPartyId),
                    ("lobbyId", partyLobby != null ? partyLobby.Id : "null"),
                    ("private", isPrivate),
                    ("maxPlayers", maxPlayers));
            } finally {
                _isCreatingPartyLobby = false;
            }
        }

        /// <summary>
        /// Computes the current party size, preferring the UGS party lobby when present, otherwise falling back to the Steam lobby.
        /// </summary>
        public static int GetCurrentPartySize(ISessionContext ctx) {
            if(ctx == null) return 1;
            var ugsParty = ctx.UgsPartyLobby;
            if(ugsParty is { Players: { Count: > 0 } }) {
                return ugsParty.Players.Count;
            }

            var steamLobby = ctx.CurrentLobby;
            if(!steamLobby.HasValue) return 1;
            var memberCount = steamLobby.Value.MemberCount;
            return memberCount > 0 ? memberCount : 1;
        }

        /// <summary>True when there is more than one real party member.</summary>
        public static bool HasRealPartyMembers(ISessionContext ctx) => GetCurrentPartySize(ctx) > 1;

        /// <summary>
        /// Determines whether the local player is the resolved party leader, using UGS party lobby when available,
        /// otherwise falling back to Steam lobby ownership or the cached IsPartyLeader flag.
        /// </summary>
        public static bool IsLocalPartyLeaderResolved(ISessionContext ctx) {
            if(ctx == null) return false;

            // Solo users are always considered leaders of their own backend party context.
            if(!HasRealPartyMembers(ctx)) return true;

            var ugsParty = ctx.UgsPartyLobby;
            if(ugsParty != null) {
                var localUgsId = AuthenticationService.Instance.PlayerId;
                if(!string.IsNullOrEmpty(localUgsId)) {
                    return ugsParty.HostId == localUgsId;
                }
            }

            var steamLobby = ctx.CurrentLobby;
            if(!steamLobby.HasValue || !SteamClient.IsValid) return ctx.IsPartyLeader;

            var localSteamId = SteamClient.SteamId;
            if(localSteamId != 0) {
                return steamLobby.Value.Owner.Id == localSteamId;
            }

            return ctx.IsPartyLeader;
        }

        /// <summary>True when we are a resolved party member (i.e., there is a real party, and we are not the leader).</summary>
        public static bool IsPartyMemberResolved(ISessionContext ctx) =>
            HasRealPartyMembers(ctx) && !IsLocalPartyLeaderResolved(ctx);

        public static async UniTask JoinPartyLobbyByCodeAsync(ISessionContext ctx, IPartySessionActions actions, string code) {
            await ctx.EnsureSignedInAsync();
            if(string.IsNullOrEmpty(code)) return;

            var options = new JoinLobbyByCodeOptions {
                Player = BuildLobbyPlayer()
            };

            var partyLobby = await LobbyService.Instance.JoinLobbyByCodeAsync(code, options);
            ctx.SetUgsPartyLobby(partyLobby);
            ctx.SetUgsMatchLobby(null);
            var localUgsId = AuthenticationService.Instance.PlayerId;
            ctx.SetIsPartyLeader(partyLobby != null && partyLobby.HostId == localUgsId);

            await actions.UnsubscribeMatchLobbyAsync("JoinPartyLobbyByCodeAsync/ResetMatch");
            await actions.EnsurePartyLobbySubscriptionAsync("JoinPartyLobbyByCodeAsync");

            if(partyLobby is { Data: not null } && partyLobby.Data.TryGetValue(UgsPartyIdKey, out var partyIdObj) &&
               partyIdObj != null && !string.IsNullOrEmpty(partyIdObj.Value)) {
                ctx.SetCurrentPartyId(partyIdObj.Value);
            }

            actions.SetNextUgsHeartbeatTime(Time.unscaledTime + 1f);
            ctx.UpdateSteamRichPresence();
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "JoinUgsParty"),
                ("code", code),
                ("partyId", ctx.CurrentPartyId),
                ("lobbyId", partyLobby != null ? partyLobby.Id : "null"));
        }

        /// <summary>If local player is party host, clears followMatchLobbyId and lobbyState on the party lobby. Call during leave-to-menu.</summary>
        public static async UniTask ResetPartyFollowStateIfHostAsync(ISessionContext ctx, IPartySessionActions partyActions, SessionMatchLobby matchLobby) {
            var partyLobby = ctx.UgsPartyLobby;
            if(partyLobby == null) return;

            try {
                var localId = AuthenticationService.Instance.PlayerId;
                if(string.IsNullOrEmpty(localId)) return;
                if(partyLobby.HostId != localId) return;

                var followAlreadyCleared = partyLobby.Data != null &&
                    partyLobby.Data.TryGetValue(UgsFollowMatchLobbyIdKey, out var followObj) &&
                    (followObj == null || string.IsNullOrEmpty(followObj.Value));
                if(followAlreadyCleared) return;

                var update = new UpdateLobbyOptions {
                    Data = new Dictionary<string, DataObject> {
                        [UgsFollowMatchLobbyIdKey] = new(DataObject.VisibilityOptions.Member, ""),
                        [UgsLobbyStateKey] = new(DataObject.VisibilityOptions.Member, "Party")
                    }
                };
                var updated = await LobbyService.Instance.UpdateLobbyAsync(partyLobby.Id, update);
                ctx.SetUgsPartyLobby(updated);
                matchLobby.ResetFollowState();
                if(Debug.isDebugBuild) Debug.Log("[SessionManager] Cleared stale followMatchLobbyId on party lobby.");
            } catch(LobbyServiceException ex) when(ex.Reason is LobbyExceptionReason.LobbyNotFound or LobbyExceptionReason.EntityNotFound) {
                ctx.SetUgsPartyLobby(null);
                await partyActions.UnsubscribePartyLobbyAsync("ResetPartyFollowStateIfHostAsync/LobbyMissing");
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to clear followMatchLobbyId on party lobby: {ex.Message}");
            }
        }

        private static Player BuildLobbyPlayer() {
            var pid = AuthenticationService.Instance.PlayerId;
            var data = new Dictionary<string, PlayerDataObject> {
                ["displayName"] = new(PlayerDataObject.VisibilityOptions.Member, LocalIdentity.GetDisplayName())
            };
            var steamId = LocalIdentity.GetSteamId();
            if(steamId != 0) {
                data["steamId"] = new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, steamId.ToString());
            }

            return new Player(pid, data: data);
        }

        /// <summary>Syncs CurrentPartyId from the party lobby data if not already set. Call before creating private match.</summary>
        private static void SyncPartyIdFromPartyLobby(ISessionContext ctx) {
            if(ctx == null) return;
            if(string.IsNullOrEmpty(ctx.CurrentPartyId) == false) return;
            var partyLobby = ctx.UgsPartyLobby;
            if(partyLobby?.Data == null) return;
            if(partyLobby.Data.TryGetValue(UgsPartyIdKey, out var partyIdObj) == false) return;
            if(partyIdObj == null || string.IsNullOrEmpty(partyIdObj.Value)) return;
            ctx.SetCurrentPartyId(partyIdObj.Value);
        }

        /// <summary>Builds the list of expected UGS player IDs from the party lobby for private match sync.</summary>
        private static List<string> BuildExpectedPlayerIdsFromPartyLobby(Lobby partyLobby, string fallbackLocalUgsId) {
            var expected = new List<string>();
            if(partyLobby is { Players: not null }) {
                foreach(var player in partyLobby.Players) {
                    if(player == null || string.IsNullOrEmpty(player.Id)) continue;
                    expected.Add(player.Id);
                }
            }
            if(expected.Count == 0 && string.IsNullOrEmpty(fallbackLocalUgsId) == false)
                expected.Add(fallbackLocalUgsId);
            return expected;
        }

        /// <summary>
        /// Runs the full private match host flow: pre-fade, DA session, match lobby, sync, wait for ready, load scene.
        /// Call from SessionManager.StartPrivateMatchAsync.
        /// </summary>
        public static async UniTask RunStartPrivateMatchAsync(
            string mode,
            int maxPlayers,
            ISessionContext ctx,
            IMatchSnapshotActions snapshotActions,
            IPartySessionActions partyActions,
            ILobbyEventActions lobbyActions,
            SessionMatchLobby matchLobby,
            IPrivateMatchHostActions hostActions) {
            if(!ctx.TryBeginSessionOperation("StartPrivateMatchAsync")) return;
            try {
                await ctx.EnsureSignedInAsync();
                if(ctx.UgsPartyLobby == null) return;

                var localUgsId = AuthenticationService.Instance.PlayerId;
                if(string.IsNullOrEmpty(localUgsId)) return;
                if(string.IsNullOrEmpty(mode)) return;

                ctx.ApplyRuntimeMode(mode, "UgsPrivateMatchHost");
                FlowLog.Emit(FlowEventIds.QueueStarted,
                    ("mode", mode),
                    ("queue", "PrivateParty"),
                    ("maxPlayers", maxPlayers));

                await hostActions.PreFadePrivateHostAsync();
                SyncPartyIdFromPartyLobby(ctx);

                var expectedPlayers = BuildExpectedPlayerIdsFromPartyLobby(ctx.UgsPartyLobby, localUgsId);
                ctx.SetExpectedGamePlayerCount(expectedPlayers.Count, "UgsPrivateMatchHost");
                var expectedCsv = string.Join(",", expectedPlayers);

                var sessionCode = await hostActions.CreateDaSessionAsync(maxPlayers, true, "StartPrivateMatchAsync");
                if(string.IsNullOrEmpty(sessionCode)) {
                    await hostActions.LeaveToMainMenuAsync();
                    return;
                }

                await matchLobby.CreatePrivateMatchLobbyAsync(ctx, partyActions, lobbyActions, mode, maxPlayers, sessionCode, expectedCsv);

                snapshotActions.UgsSyncInProgress = false;
                snapshotActions.UgsLocalReadySubmitted = false;
                snapshotActions.UgsClientStartedForMatch = false;

                await matchLobby.StartMatchSyncAsync(ctx, snapshotActions, lobbyActions, skipFadeOut: false);

                if(await matchLobby.WaitForPlayersReadyAsync(ctx, expectedPlayers, 20f, "PrivateMatch") == false) {
                    await hostActions.LeaveToMainMenuAsync();
                    return;
                }

                var loadingSceneSet = await hostActions.TrySetMatchLobbyStateAsync("LoadingScene",
                    DataObject.VisibilityOptions.Member, "StartPrivateMatchAsync");
                if(!loadingSceneSet) {
                    Debug.LogWarning(
                        "[SessionManager] Failed to set private match lobby state to LoadingScene. Clients may remain in sync state.");
                }

                if(!hostActions.TryLoadGameplaySceneAsHost("StartPrivateMatchAsync/LoadScene")) {
                    await hostActions.LeaveToMainMenuAsync();
                }
            } finally {
                ctx.EndSessionOperation();
            }
        }

        /// <summary>
        /// Applies all private match draft settings before starting the match (gamemode, map, timer, score, tagged, team assignments).
        /// Call from the menu flow before StartPrivateMatchAsync / StartOfflinePrivateMatchAsync.
        /// </summary>
        public static void RunApplyPrivateMatchSettings(
            ISessionContext ctx,
            IHostMapSceneActions hostMapActions,
            string mode,
            string mapId,
            int matchTimerSeconds,
            bool usePreMatchCountdown,
            bool swapWeaponsOnDeath,
            int scoreToWin,
            int kothHillSpeed,
            int taggedPlayers,
            IReadOnlyDictionary<ulong, int> teamAssignments) {
            if(ctx == null) return;
            if(!string.IsNullOrWhiteSpace(mode))
                ctx.ApplyRuntimeMode(mode, "PrivateMatchDraft", refreshUi: false);
            if(hostMapActions != null && !string.IsNullOrWhiteSpace(mapId))
                hostMapActions.SetSelectedMapFromId(mapId);
            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null) {
                matchSettings.matchDurationSeconds = Mathf.Max(0, matchTimerSeconds);
                matchSettings.preMatchCountdownEnabled = usePreMatchCountdown;
                matchSettings.swapWeaponsOnDeath = swapWeaponsOnDeath;
                matchSettings.scoreToWin = Mathf.Max(0, scoreToWin);
                matchSettings.kothHillSpeed = Mathf.Max(1, kothHillSpeed);
                matchSettings.taggedPlayers = Mathf.Max(1, taggedPlayers);
            }
            PrivateMatchTeamAssignments.Set(teamAssignments);
            if(Debug.isDebugBuild) {
                Debug.Log(
                    $"[SessionManager] ApplyPrivateMatchSettings: mode='{mode}' mapId='{mapId}' timer={matchTimerSeconds} preMatchCountdown={usePreMatchCountdown} swapWeaponsOnDeath={swapWeaponsOnDeath} scoreToWin={scoreToWin} kothHillSpeed={kothHillSpeed} tagged={taggedPlayers} teams={teamAssignments?.Count ?? 0}");
            }
        }
    }
}
