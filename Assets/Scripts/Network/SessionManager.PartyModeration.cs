using System;
using Cysharp.Threading.Tasks;
using Steamworks;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using UnityEngine;

namespace Network {
    public sealed partial class SessionManager {
        #region Party Moderation

        /// <summary>
        /// Removes a party member from the UGS party lobby.
        /// </summary>
        /// <param name="targetId">The Steam ID of the member to kick.</param>
        public void KickMember(SteamId targetId) {
            if(targetId.Value == 0) return;
            if(_ugsPartyLobby == null) {
                Debug.LogWarning("[SessionManager] KickMember ignored: no active UGS party lobby.");
                return;
            }

            var localId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localId) || _ugsPartyLobby.HostId != localId) {
                Debug.LogWarning("[SessionManager] KickMember ignored: local player is not the UGS party host.");
                return;
            }

            if(TryResolvePartyPlayerIdFromSteamId(targetId, out var targetUgsId) == false) {
                Debug.LogWarning(
                    $"[SessionManager] KickMember failed: could not resolve UGS player for SteamId '{targetId.Value}'.");
                return;
            }

            if(targetUgsId == localId) {
                Debug.LogWarning("[SessionManager] KickMember ignored: host cannot kick self.");
                return;
            }

            KickPartyMemberAsync(targetUgsId, targetId).Forget();
        }

        /// <summary>
        /// Promotes a party member to be the new UGS party host.
        /// </summary>
        /// <param name="targetId">The Steam ID of the member to promote.</param>
        public void PromoteMember(SteamId targetId) {
            if(targetId.Value == 0) return;
            if(_ugsPartyLobby == null) {
                Debug.LogWarning("[SessionManager] PromoteMember ignored: no active UGS party lobby.");
                return;
            }

            var localId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localId) || _ugsPartyLobby.HostId != localId) {
                Debug.LogWarning("[SessionManager] PromoteMember ignored: local player is not the UGS party host.");
                return;
            }

            if(TryResolvePartyPlayerIdFromSteamId(targetId, out var targetUgsId) == false) {
                Debug.LogWarning(
                    $"[SessionManager] PromoteMember failed: could not resolve UGS player for SteamId '{targetId.Value}'.");
                return;
            }

            if(targetUgsId == localId) return;
            PromotePartyHostAsync(targetUgsId, targetId).Forget();
        }

        private bool TryResolvePartyPlayerIdFromSteamId(SteamId steamId, out string ugsPlayerId) {
            ugsPlayerId = null;
            if(_ugsPartyLobby?.Players == null) return false;

            var steamIdValue = steamId.Value.ToString();
            foreach(var player in _ugsPartyLobby.Players) {
                if(player == null || string.IsNullOrEmpty(player.Id)) continue;
                if(player.Data == null) continue;
                if(!player.Data.TryGetValue("steamId", out var steamObj)) continue;
                if(steamObj == null || string.IsNullOrEmpty(steamObj.Value)) continue;
                if(steamObj.Value != steamIdValue) continue;

                ugsPlayerId = player.Id;
                return true;
            }

            return false;
        }

        private async UniTaskVoid KickPartyMemberAsync(string targetUgsId, SteamId targetSteamId) {
            if(_ugsPartyLobby == null || string.IsNullOrEmpty(_ugsPartyLobby.Id)) return;

            try {
                await LobbyService.Instance.RemovePlayerAsync(_ugsPartyLobby.Id, targetUgsId);
                _ugsPartyLobby = await LobbyService.Instance.GetLobbyAsync(_ugsPartyLobby.Id);
                Debug.Log($"[SessionManager] Kicked party member SteamId '{targetSteamId.Value}' from UGS party.");
                NotifyPartyStateChanged();
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Failed to kick party member '{targetSteamId.Value}': {ex.Message}");
            }
        }

        private async UniTaskVoid PromotePartyHostAsync(string targetUgsId, SteamId targetSteamId) {
            if(_ugsPartyLobby == null || string.IsNullOrEmpty(_ugsPartyLobby.Id)) return;

            try {
                var update = new UpdateLobbyOptions {
                    HostId = targetUgsId
                };
                _ugsPartyLobby = await LobbyService.Instance.UpdateLobbyAsync(_ugsPartyLobby.Id, update);

                if(CurrentLobby.HasValue && CurrentLobby.Value.Owner.Id == SteamClient.SteamId) {
                    var socialLobby = CurrentLobby.Value;
                    socialLobby.Owner = new Friend(targetSteamId);
                    CurrentLobby = socialLobby;
                }

                Debug.Log($"[SessionManager] Promoted party member SteamId '{targetSteamId.Value}' to UGS party host.");
                NotifyPartyStateChanged();
            } catch(Exception ex) {
                Debug.LogWarning(
                    $"[SessionManager] Failed to promote party host to '{targetSteamId.Value}': {ex.Message}");
            }
        }

        #endregion
    }
}
