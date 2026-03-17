using System;
using Cysharp.Threading.Tasks;
using Diagnostics;
using Events;
using Network.Contracts;
using Steamworks;
using UnityEngine;
using Lobby = Steamworks.Data.Lobby;

namespace Network.Session {
    /// <summary>
    /// Subscribes to Steam lobby/friend callbacks and drives session context and join/follow actions.
    /// </summary>
    public sealed class SteamSocialBridge {
        private const string PartyIdKey = "PartyId";
        private const string TargetModeKey = "TargetMode";
        private const string SteamUgsPartyCodeKey = "UgsPartyCode";
        private const string SteamUgsMatchLobbyIdKey = "UgsMatchLobbyId";

        // Shared Steam lobby member-data keys used by game-side adapters.
        public const string DisplayNameKey = "DisplayName";
        public const string AvatarHiddenKey = "AvatarHidden";
        public const string PlayerIconKey = "PlayerIcon";

        private ISessionContext _ctx;
        private ISteamSessionActions _actions;

        // Game-provided hooks
        private static Action<string, bool> lobbyPresenceNotifier;
        private static Action<ISessionContext> updateLocalDisplayMetadata;

        private static void SafeInvokeLobbyPresenceNotifier(string friendName, bool joined) {
            if(lobbyPresenceNotifier == null) return;
            try {
                lobbyPresenceNotifier(friendName, joined);
            } catch(Exception ex) {
                if(Debug.isDebugBuild) {
                    Debug.LogWarning(
                        $"[SessionManager] LobbyPresenceNotifier threw an exception for friend='{friendName}' joined={joined}: {ex.Message}");
                }
            }
        }

        private static void SafeUpdateLocalDisplayMetadata(ISessionContext ctx) {
            if(updateLocalDisplayMetadata == null || ctx == null) return;
            try {
                updateLocalDisplayMetadata(ctx);
            } catch(Exception ex) {
                if(Debug.isDebugBuild) {
                    Debug.LogWarning(
                        $"[SessionManager] UpdateLocalDisplayMetadata threw an exception for lobby='{ctx.CurrentLobby?.Id}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Register game hook to send lobby presence messages (joined/left) to chat or other systems.
        /// Parameters: friendName, joined (true on join, false on leave).
        /// </summary>
        public static void SetLobbyPresenceNotifier(Action<string, bool> notifier) =>
            lobbyPresenceNotifier = notifier;

        /// <summary>
        /// Register game hook to update local display metadata (name, avatar visibility, icon) in the Steam lobby.
        /// </summary>
        public static void SetUpdateLocalDisplayMetadata(Action<ISessionContext> updater) =>
            updateLocalDisplayMetadata = updater;

        public void Register(ISessionContext ctx, ISteamSessionActions actions) {
            _ctx = ctx;
            _actions = actions;

            SteamMatchmaking.OnLobbyDataChanged += OnLobbyDataChanged;
            SteamMatchmaking.OnLobbyMemberDataChanged += OnLobbyMemberDataChanged;
            SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberLeave;
            SteamFriends.OnGameLobbyJoinRequested += OnGameLobbyJoinRequested;
            SteamFriends.OnGameRichPresenceJoinRequested += OnGameRichPresenceJoinRequested;
        }

        public void Unregister() {
            SteamMatchmaking.OnLobbyDataChanged -= OnLobbyDataChanged;
            SteamMatchmaking.OnLobbyMemberDataChanged -= OnLobbyMemberDataChanged;
            SteamMatchmaking.OnLobbyMemberJoined -= OnLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberLeave;
            SteamFriends.OnGameLobbyJoinRequested -= OnGameLobbyJoinRequested;
            SteamFriends.OnGameRichPresenceJoinRequested -= OnGameRichPresenceJoinRequested;

            _ctx = null;
            _actions = null;
        }

        private void OnLobbyDataChanged(Lobby lobby) {
            if(_ctx == null || lobby.Id != _ctx.CurrentLobby?.Id) return;

            var mode = lobby.GetData(TargetModeKey);
            if(!string.IsNullOrEmpty(mode) && mode != _ctx.SelectedGameMode) {
                _ctx.ApplyRuntimeMode(mode, "SteamSocialLobbyDataChanged");
            }

            var partyId = lobby.GetData(PartyIdKey);
            if(!string.IsNullOrEmpty(partyId) && partyId != _ctx.CurrentPartyId) {
                _ctx.SetCurrentPartyId(partyId);
            }

            var amILeader = lobby.Owner.Id == SteamClient.SteamId;
            if(_ctx.IsPartyLeader != amILeader) {
                _ctx.SetIsPartyLeader(amILeader);
                EventBus.Publish(new FrontStatusChangedEvent(null));
            }

            _ctx.NotifyPartyStateChanged();
        }

        private void OnLobbyMemberDataChanged(Lobby lobby, Friend friend) {
            if(_ctx == null || lobby.Id != _ctx.CurrentLobby?.Id) return;
            _ctx.NotifyPartyStateChanged();
        }

        private void OnLobbyMemberJoined(Lobby lobby, Friend friend) {
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Member Joined: {friend.Name}");
            }

            if(_ctx is not { CurrentLobby: not null } || _ctx.CurrentLobby.Value.Id != lobby.Id) {
                return;
            }

            if(friend.Id != SteamClient.SteamId) {
                SafeInvokeLobbyPresenceNotifier(friend.Name, true);
            }

            if(_ctx.CurrentLobby.HasValue && _ctx.CurrentLobby.Value.Id == lobby.Id && lobby.MemberCount > 1) {
                _actions.TryJoinVoiceForSteamSocialLobby(lobby.Id, "OnLobbyMemberJoined");
            }

            _ctx.NotifyPartyStateChanged();
        }

        private void OnLobbyMemberLeave(Lobby lobby, Friend friend) {
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Member Left: {friend.Name}");
            }

            if(_ctx is { CurrentLobby: not null } && _ctx.CurrentLobby.Value.Id == lobby.Id &&
               friend.Id != SteamClient.SteamId) {
                SafeInvokeLobbyPresenceNotifier(friend.Name, false);
            }

            _ctx?.NotifyPartyStateChanged();
        }

        private void OnGameLobbyJoinRequested(Lobby lobby, SteamId id) {
            if(_ctx == null || _actions == null) return;
            _ctx.LaunchSessionTask(HandleGameLobbyJoinRequestedAsync(lobby), "SteamGameLobbyJoinRequested");
        }

        private async UniTask HandleGameLobbyJoinRequestedAsync(Lobby lobby) {
            var ctx = _ctx;
            if(ctx == null) return;

            try {
                if(Debug.isDebugBuild) {
                    Debug.Log($"[SessionManager] Accepted Invite to Lobby {lobby.Id}");
                }

                var joined = await JoinSteamSocialLobbyAsync(lobby);
                if(!joined) {
                    ctx.SetFrontStatus(SessionPhase.Error, "Failed to join invited party.");
                    return;
                }

                await FollowSessionContextFromSteamLobbyAsync(lobby);
            } catch(Exception e) {
                Debug.LogError($"[SessionManager] Failed to join invited lobby '{lobby.Id}': {e.Message}");
                ctx.SetFrontStatus(SessionPhase.Error, "Failed to join invited lobby.");
            }
        }

        private void OnGameRichPresenceJoinRequested(Friend friend, string connect) {
            if(string.IsNullOrEmpty(connect) || _ctx == null || _actions == null) return;
            _ctx.LaunchSessionTask(HandleSteamConnectStringAsync(connect), "SteamRichPresenceJoinRequested");
        }

        /// <summary>Implements Steam connect string handling (UGS_PARTY_CODE: / UGS_MATCH_ID:).</summary>
        public async UniTask HandleSteamConnectStringAsync(string connect) {
            const string partyPrefix = "UGS_PARTY_CODE:";
            const string matchPrefix = "UGS_MATCH_ID:";

            try {
                if(connect.StartsWith(partyPrefix)) {
                    var code = connect[partyPrefix.Length..];
                    if(string.IsNullOrEmpty(code)) return;
                    await _actions.JoinPartyLobbyByCodeAsync(code);
                    return;
                }

                if(connect.StartsWith(matchPrefix)) {
                    var lobbyId = connect[matchPrefix.Length..];
                    if(string.IsNullOrEmpty(lobbyId)) return;
                    await _actions.JoinMatchLobbyByIdAsync(lobbyId);
                }
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to handle Steam connect string '{connect}': {ex.Message}");
            }
        }

        /// <summary>Follows UGS party/match from Steam lobby data. Implemented by bridge.</summary>
        public async UniTask FollowSessionContextFromSteamLobbyAsync(Lobby lobby) {
            if(lobby.Id == 0) return;

            var matchLobbyId = lobby.GetData(SteamUgsMatchLobbyIdKey);
            if(!string.IsNullOrEmpty(matchLobbyId)) {
                await _actions.JoinMatchLobbyByIdAsync(matchLobbyId);
                return;
            }

            var partyCode = lobby.GetData(SteamUgsPartyCodeKey);
            if(!string.IsNullOrEmpty(partyCode))
                await _actions.JoinPartyLobbyByCodeAsync(partyCode);
        }

        /// <summary>Joins Steam social lobby and syncs party context. Implemented by bridge.</summary>
        public async UniTask<bool> JoinSteamSocialLobbyAsync(Lobby lobby) {
            if(lobby.Id == 0) return false;

            var ctx = _ctx;
            if(ctx == null) return false;

            if(ctx.CurrentLobby.HasValue && ctx.CurrentLobby.Value.Id == lobby.Id)
                return true;

            if(ctx.CurrentLobby.HasValue && ctx.CurrentLobby.Value.Id != lobby.Id)
                ctx.LeaveLobby();

            var result = await lobby.Join();
            if(result != RoomEnter.Success) {
                Debug.LogWarning($"[SessionManager] Failed to join Steam social lobby '{lobby.Id}': {result}");
                return false;
            }

            ctx.SetCurrentLobby(lobby);
            ctx.SetIsPartyLeader(lobby.Owner.Id == SteamClient.SteamId);

            var lobbyPartyId = lobby.GetData(PartyIdKey);
            if(!string.IsNullOrEmpty(lobbyPartyId)) {
                ctx.SetCurrentPartyId(lobbyPartyId);
            } else if(string.IsNullOrEmpty(ctx.CurrentPartyId)) {
                ctx.SetCurrentPartyId(Guid.NewGuid().ToString());
                lobby.SetData(PartyIdKey, ctx.CurrentPartyId);
            }

            lobby.SetMemberData(PartyIdKey, ctx.CurrentPartyId);
            UpdateLocalDisplayNameInLobby(ctx);

            _actions.TryJoinVoiceForSteamSocialLobby(lobby.Id, "JoinSteamSocialLobbyAsync");

            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "JoinSteamSocialLobby"),
                ("partyId", ctx.CurrentPartyId),
                ("steamLobbyId", lobby.Id),
                ("owner", lobby.Owner.Id));

            if(ctx.Phase is SessionPhase.Menu or SessionPhase.CreatingLobby or SessionPhase.JoiningLobby)
                ctx.SetFrontStatus(SessionPhase.LobbyReady, "Lobby Ready. Invite Friends!");

            ctx.NotifyPartyStateChanged();
            return true;
        }

        /// <summary>Updates Steam rich presence and lobby data from session context.</summary>
        public static void UpdateSteamRichPresence(ISessionContext ctx) {
            if(ctx == null || !SteamClient.IsValid || !SteamClient.IsLoggedOn) return;

            var partyCode = "";
            var matchLobbyId = "";
            var connect = "";
            var status = "";

            if(ctx.UgsMatchLobby != null && !string.IsNullOrEmpty(ctx.UgsMatchLobby.Id)) {
                matchLobbyId = ctx.UgsMatchLobby.Id;
                connect = "UGS_MATCH_ID:" + matchLobbyId;
                status = "In Match";
            } else if(ctx.UgsPartyLobby != null && !string.IsNullOrEmpty(ctx.UgsPartyLobby.LobbyCode)) {
                partyCode = ctx.UgsPartyLobby.LobbyCode;
                connect = "UGS_PARTY_CODE:" + partyCode;
                status = "In Party";
            }

            if(string.IsNullOrEmpty(connect)) {
                SteamFriends.ClearRichPresence();
            } else {
                SteamFriends.SetRichPresence("connect", connect);
                SteamFriends.SetRichPresence("status", status);
            }

            if(!ctx.CurrentLobby.HasValue || ctx.CurrentLobby.Value.Owner.Id != SteamClient.SteamId) return;
            try {
                ctx.CurrentLobby.Value.SetData(SteamUgsPartyCodeKey, partyCode);
                ctx.CurrentLobby.Value.SetData(SteamUgsMatchLobbyIdKey, matchLobbyId);
                ctx.CurrentLobby.Value.SetData(TargetModeKey, ctx.SelectedGameMode);
            } catch(Exception ex) {
                if(Debug.isDebugBuild)
                    Debug.LogWarning($"[SessionManager] Failed to publish UGS bridge metadata to Steam social lobby: {ex.Message}");
            }
        }

        /// <summary>Pushes party data to Steam lobby if we are the owner.</summary>
        public static void UpdateSteamLobbyWithPartyDataIfOwner(ISessionContext ctx) {
            if(ctx is not { CurrentLobby: not null } || ctx.CurrentLobby.Value.Owner.Id != SteamClient.SteamId) return;
            if(!SteamClient.IsValid || !SteamClient.IsLoggedOn) return;
            try {
                ctx.CurrentLobby.Value.SetData(PartyIdKey, ctx.CurrentPartyId);
                ctx.CurrentLobby.Value.SetData(TargetModeKey, ctx.SelectedGameMode);
                UpdateLocalDisplayNameInLobby(ctx);
            } catch(Exception ex) {
                if(Debug.isDebugBuild)
                    Debug.LogWarning($"[SessionManager] Failed to update Steam lobby party data: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the local player's display name, avatar hidden, and player icon in the Steam lobby.
        /// Actual behavior is provided by the game via UpdateLocalDisplayMetadata.
        /// </summary>
        public static void UpdateLocalDisplayNameInLobby(ISessionContext ctx) {
            SafeUpdateLocalDisplayMetadata(ctx);
        }

        /// <summary>Creates a Steam social lobby and sets context. Called when creating UGS party without an existing Steam lobby.</summary>
        public async UniTask<bool> CreateSteamSocialLobbyAsync(int maxMembers) {
            var ctx = _ctx;
            if(ctx == null) return false;
            try {
                var result = await SteamMatchmaking.CreateLobbyAsync(maxMembers);
                if(!result.HasValue) {
                    ctx.SetFrontStatus(SessionPhase.Error, "Failed to create party lobby.");
                    return false;
                }
                var lobby = result.Value;
                lobby.SetPrivate();
                lobby.SetJoinable(true);
                lobby.SetData(TargetModeKey, ctx.SelectedGameMode);
                if(string.IsNullOrEmpty(ctx.CurrentPartyId)) {
                    ctx.SetCurrentPartyId(Guid.NewGuid().ToString());
                }
                lobby.SetData(PartyIdKey, ctx.CurrentPartyId);
                ctx.SetCurrentLobby(lobby);
                ctx.SetIsPartyLeader(true);
                lobby.SetMemberData(PartyIdKey, ctx.CurrentPartyId);
                UpdateLocalDisplayNameInLobby(ctx);
                if(lobby.MemberCount > 1) {
                    _actions.TryJoinVoiceForSteamSocialLobby(lobby.Id, "CreateSteamSocialLobbyAsync");
                }
                FlowLog.Emit(FlowEventIds.PartyLifecycle,
                    ("action", "CreateSteamSocialLobby"),
                    ("partyId", ctx.CurrentPartyId),
                    ("steamLobbyId", lobby.Id),
                    ("mode", ctx.SelectedGameMode));
                ctx.UpdateSteamRichPresence();
                ctx.SetFrontStatus(SessionPhase.LobbyReady, "Lobby Ready. Invite Friends!");
                ctx.NotifyPartyStateChanged();
                return true;
            } catch(Exception ex) {
                Debug.LogError($"[SessionManager] Failed to create Steam social lobby: {ex.Message}");
                ctx.SetFrontStatus(SessionPhase.Error, "Failed to create party lobby.");
                return false;
            }
        }

        /// <summary>Leaves the current Steam social lobby and resets party state on context.</summary>
        public void LeaveSteamLobby() {
            var ctx = _ctx;
            if(ctx == null) return;
            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "LeaveLobby"),
                ("partyId", ctx.CurrentPartyId),
                ("steamLobbyId", ctx.CurrentLobby.HasValue ? ctx.CurrentLobby.Value.Id.ToString() : "none"));
            ctx.SetIsExpectedDisconnect(true);
            if(ctx.CurrentLobby.HasValue) {
                ctx.CurrentLobby.Value.Leave();
                ctx.SetCurrentLobby(null);
            }
            ctx.SetFrontStatus(SessionPhase.Menu, "");
            ctx.SetIsPartyLeader(false);
            ctx.SetExpectedGamePlayerCount(1, "LeaveLobby");
            ctx.NotifyPartyStateChanged();
        }

        /// <summary>Sets the game mode on the Steam lobby data if we are the owner. Call after ApplyRuntimeMode.</summary>
        public static void SetSteamLobbyGameMode(ISessionContext ctx, string mode) {
            if(ctx == null || string.IsNullOrEmpty(mode)) return;
            if(!ctx.CurrentLobby.HasValue || ctx.CurrentLobby.Value.Owner.Id != SteamClient.SteamId) return;
            ctx.CurrentLobby.Value.SetData(TargetModeKey, mode);
        }
    }
}
