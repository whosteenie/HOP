using System;
using Cysharp.Threading.Tasks;
using Game.Social;
using Network.Events;
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

        private ISessionContext _ctx;
        private ISteamSessionActions _actions;

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

            if(_ctx == null || !_ctx.CurrentLobby.HasValue || _ctx.CurrentLobby.Value.Id != lobby.Id) {
                return;
            }

            if(friend.Id != SteamClient.SteamId && ChatManager.Instance != null) {
                ChatManager.SendLobbyPresenceMessage(friend.Name, true);
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

            if(_ctx != null && _ctx.CurrentLobby.HasValue && _ctx.CurrentLobby.Value.Id == lobby.Id &&
               friend.Id != SteamClient.SteamId && ChatManager.Instance != null) {
                ChatManager.SendLobbyPresenceMessage(friend.Name, false);
            }

            _ctx?.NotifyPartyStateChanged();
        }

        private void OnGameLobbyJoinRequested(Lobby lobby, SteamId id) {
            if(_ctx == null || _actions == null) return;
            _ctx.LaunchSessionTask(HandleGameLobbyJoinRequestedAsync(lobby), "SteamGameLobbyJoinRequested");
        }

        private async UniTask HandleGameLobbyJoinRequestedAsync(Lobby lobby) {
            var ctx = _ctx;
            var actions = _actions;
            if(ctx == null || actions == null) return;

            try {
                if(Debug.isDebugBuild) {
                    Debug.Log($"[SessionManager] Accepted Invite to Lobby {lobby.Id}");
                }

                var joined = await actions.JoinSteamSocialLobbyAsync(lobby);
                if(!joined) {
                    ctx.SetFrontStatus(SessionPhase.Error, "Failed to join invited party.");
                    return;
                }

                await actions.FollowSessionContextFromSteamLobbyAsync(lobby);
            } catch(Exception e) {
                Debug.LogError($"[SessionManager] Failed to join invited lobby '{lobby.Id}': {e.Message}");
                ctx.SetFrontStatus(SessionPhase.Error, "Failed to join invited lobby.");
            }
        }

        private void OnGameRichPresenceJoinRequested(Friend friend, string connect) {
            if(string.IsNullOrEmpty(connect) || _ctx == null || _actions == null) return;
            _ctx.LaunchSessionTask(_actions.HandleSteamConnectStringAsync(connect), "SteamRichPresenceJoinRequested");
        }
    }
}
