using System;
using Cysharp.Threading.Tasks;
using Game.Social;
using Network.Diagnostics;
using Steamworks;
using UnityEngine;
using Lobby = Steamworks.Data.Lobby;

namespace Network {
    public sealed partial class SessionManager {
        private const string PartyIdKey = "PartyId";
        private const string DisplayNameKey = "DisplayName";
        private const string AvatarHiddenKey = "AvatarHidden";
        private const string PlayerIconKey = "PlayerIcon";
        private const string TargetModeKey = "TargetMode";
        private const string SteamUgsPartyCodeKey = "UgsPartyCode";
        private const string SteamUgsMatchLobbyIdKey = "UgsMatchLobbyId";

        #region Steam Callbacks

        private void OnLobbyDataChanged(Lobby lobby) {
            if(lobby.Id != CurrentLobby?.Id) return;

            // Steam lobbies are social-only in the UGS session flow. We still mirror
            // party metadata and mode selection for menu/presence UX.
            var mode = lobby.GetData(TargetModeKey);
            if(!string.IsNullOrEmpty(mode) && mode != SelectedGameMode) {
                ApplyRuntimeMode(mode, "SteamSocialLobbyDataChanged");
            }

            var partyId = lobby.GetData(PartyIdKey);
            if(!string.IsNullOrEmpty(partyId) && partyId != CurrentPartyId) {
                CurrentPartyId = partyId;
            }

            var amILeader = lobby.Owner.Id == SteamClient.SteamId;
            if(IsPartyLeader != amILeader) {
                IsPartyLeader = amILeader;
                if(FrontStatusChanged != null) {
                    FrontStatusChanged.Invoke(null);
                }
            }

            NotifyPartyStateChanged();
        }

        /// <summary>
        /// Steam callback for when a member's social metadata changes.
        /// </summary>
        private void OnLobbyMemberDataChanged(Lobby lobby, Friend friend) {
            if(lobby.Id != CurrentLobby?.Id) return;
            NotifyPartyStateChanged();
        }

        private void OnLobbyMemberJoined(Lobby lobby, Friend friend) {
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Member Joined: {friend.Name}");
            }

            if(!CurrentLobby.HasValue || CurrentLobby.Value.Id != lobby.Id) {
                return;
            }

            if(friend.Id != SteamClient.SteamId && ChatManager.Instance != null) {
                ChatManager.Instance.SendLobbyPresenceMessage(friend.Name, true);
            }

            if(CurrentLobby.HasValue && CurrentLobby.Value.Id == lobby.Id && lobby.MemberCount > 1) {
                TryJoinVoiceForSteamSocialLobby(lobby.Id, "OnLobbyMemberJoined");
            }

            NotifyPartyStateChanged();
        }

        private void OnLobbyMemberLeave(Lobby lobby, Friend friend) {
            if(Debug.isDebugBuild) {
                Debug.Log($"[SessionManager] Member Left: {friend.Name}");
            }

            if(CurrentLobby.HasValue && CurrentLobby.Value.Id == lobby.Id &&
               friend.Id != SteamClient.SteamId && ChatManager.Instance != null) {
                ChatManager.Instance.SendLobbyPresenceMessage(friend.Name, false);
            }

            NotifyPartyStateChanged();
        }

        private void OnGameLobbyJoinRequested(Lobby lobby, SteamId id) {
            LaunchSessionTask(HandleGameLobbyJoinRequestedAsync(lobby, id),
                "SteamGameLobbyJoinRequested");
        }

        private async UniTask HandleGameLobbyJoinRequestedAsync(Lobby lobby, SteamId id) {
            try {
                if(Debug.isDebugBuild) {
                    Debug.Log($"[SessionManager] Accepted Invite to Lobby {lobby.Id}");
                }

                var joined = await JoinSteamSocialLobbyAsync(lobby);
                if(!joined) {
                    SetFrontStatus(SessionPhase.Error, "Failed to join invited party.");
                    return;
                }

                await FollowSessionContextFromSteamLobbyAsync(lobby);
            } catch(Exception e) {
                Debug.LogError($"[SessionManager] Failed to join invited lobby '{lobby.Id}': {e.Message}");
                SetFrontStatus(SessionPhase.Error, "Failed to join invited lobby.");
            }
        }

        private void OnGameRichPresenceJoinRequested(Friend friend, string connect) {
            if(string.IsNullOrEmpty(connect)) return;
            LaunchSessionTask(HandleSteamConnectStringAsync(connect),
                "SteamRichPresenceJoinRequested");
        }

        private async UniTask HandleSteamConnectStringAsync(string connect) {
            // Expected formats:
            // - "UGS_PARTY_CODE:<lobbyCode>"
            // - "UGS_MATCH_ID:<lobbyId>"
            const string partyPrefix = "UGS_PARTY_CODE:";
            const string matchPrefix = "UGS_MATCH_ID:";

            try {
                if(connect.StartsWith(partyPrefix)) {
                    var code = connect[partyPrefix.Length..];
                    if(string.IsNullOrEmpty(code)) return;
                    await JoinPartyLobbyByCodeAsync(code);
                    return;
                }

                if(connect.StartsWith(matchPrefix)) {
                    var lobbyId = connect[matchPrefix.Length..];
                    if(string.IsNullOrEmpty(lobbyId)) return;
                    await JoinMatchLobbyByIdAsync(lobbyId);
                }
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to handle Steam connect string '{connect}': {ex.Message}");
            }
        }

        private async UniTask FollowSessionContextFromSteamLobbyAsync(Lobby lobby) {
            if(lobby.Id == 0) return;

            var matchLobbyId = lobby.GetData(SteamUgsMatchLobbyIdKey);
            if(string.IsNullOrEmpty(matchLobbyId) == false) {
                await JoinMatchLobbyByIdAsync(matchLobbyId);
                return;
            }

            var partyCode = lobby.GetData(SteamUgsPartyCodeKey);
            if(string.IsNullOrEmpty(partyCode) == false) {
                await JoinPartyLobbyByCodeAsync(partyCode);
            }
        }

        private async UniTask<bool> JoinSteamSocialLobbyAsync(Lobby lobby) {
            if(lobby.Id == 0) return false;

            if(CurrentLobby.HasValue && CurrentLobby.Value.Id == lobby.Id) {
                return true;
            }

            if(CurrentLobby.HasValue && CurrentLobby.Value.Id != lobby.Id) {
                LeaveLobby();
            }

            var result = await lobby.Join();
            if(result != RoomEnter.Success) {
                Debug.LogWarning($"[SessionManager] Failed to join Steam social lobby '{lobby.Id}': {result}");
                return false;
            }

            CurrentLobby = lobby;
            IsPartyLeader = lobby.Owner.Id == SteamClient.SteamId;

            var lobbyPartyId = lobby.GetData(PartyIdKey);
            if(!string.IsNullOrEmpty(lobbyPartyId)) {
                CurrentPartyId = lobbyPartyId;
            } else if(string.IsNullOrEmpty(CurrentPartyId)) {
                CurrentPartyId = Guid.NewGuid().ToString();
                lobby.SetData(PartyIdKey, CurrentPartyId);
            }

            lobby.SetMemberData(PartyIdKey, CurrentPartyId);
            UpdateLocalDisplayNameInLobby();

            TryJoinVoiceForSteamSocialLobby(lobby.Id, "JoinSteamSocialLobbyAsync");

            FlowLog.Emit(FlowEventIds.PartyLifecycle,
                ("action", "JoinSteamSocialLobby"),
                ("partyId", CurrentPartyId),
                ("steamLobbyId", lobby.Id),
                ("owner", lobby.Owner.Id));

            if(Phase is SessionPhase.Menu or SessionPhase.CreatingLobby or SessionPhase.JoiningLobby) {
                SetFrontStatus(SessionPhase.LobbyReady, "Lobby Ready. Invite Friends!");
            }

            NotifyPartyStateChanged();
            return true;
        }

        private void UpdateSteamRichPresence() {
            if(!SteamClient.IsValid || !SteamClient.IsLoggedOn) return;

            var partyCode = "";
            var matchLobbyId = "";
            var connect = "";
            var status = "";

            if(_ugsMatchLobby != null && string.IsNullOrEmpty(_ugsMatchLobby.Id) == false) {
                matchLobbyId = _ugsMatchLobby.Id;
                connect = "UGS_MATCH_ID:" + matchLobbyId;
                status = "In Match";
            } else if(_ugsPartyLobby != null && string.IsNullOrEmpty(_ugsPartyLobby.LobbyCode) == false) {
                partyCode = _ugsPartyLobby.LobbyCode;
                connect = "UGS_PARTY_CODE:" + partyCode;
                status = "In Party";
            }

            if(string.IsNullOrEmpty(connect)) {
                SteamFriends.ClearRichPresence();
            } else {
                SteamFriends.SetRichPresence("connect", connect);
                SteamFriends.SetRichPresence("status", status);
            }

            if(!CurrentLobby.HasValue || CurrentLobby.Value.Owner.Id != SteamClient.SteamId) return;
            try {
                CurrentLobby.Value.SetData(SteamUgsPartyCodeKey, partyCode);
                CurrentLobby.Value.SetData(SteamUgsMatchLobbyIdKey, matchLobbyId);
                CurrentLobby.Value.SetData(TargetModeKey, SelectedGameMode);
            } catch(Exception ex) {
                if(Debug.isDebugBuild) {
                    Debug.LogWarning(
                        $"[SessionManager] Failed to publish UGS bridge metadata to Steam social lobby: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Triggers the OnPartyStateChanged event to notify UI listeners.
        /// </summary>
        private static void NotifyPartyStateChanged() {
            if(!HasInstance) return;
            var sessionManager = Instance;
            if(sessionManager.OnPartyStateChanged != null) {
                sessionManager.OnPartyStateChanged.Invoke();
            }
        }

        #endregion
    }
}
