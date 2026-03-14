using System;
using Cysharp.Threading.Tasks;
using Game.Social;
using Network.Diagnostics;
using Network.Events;
using Steamworks;
using UnityEngine;
using Lobby = Steamworks.Data.Lobby;

namespace Network.Session {
    public sealed partial class SessionManager {
        private const string PartyIdKey = "PartyId";
        private const string DisplayNameKey = "DisplayName";
        private const string AvatarHiddenKey = "AvatarHidden";
        private const string PlayerIconKey = "PlayerIcon";
        private const string TargetModeKey = "TargetMode";
        private const string SteamUgsPartyCodeKey = "UgsPartyCode";
        private const string SteamUgsMatchLobbyIdKey = "UgsMatchLobbyId";

        #region Steam join/follow and presence (used by SteamSocialBridge via ISteamSessionActions)

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
        /// Triggers session property refresh events to notify UI listeners.
        /// </summary>
        private void NotifyPartyStateChanged() {
            EventBus.Publish(new SessionPropertiesRefreshedEvent());
        }

        #endregion
    }
}
