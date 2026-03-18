using System;
using Cysharp.Threading.Tasks;
using Diagnostics;
using Network.Contracts;
using Steamworks;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;

namespace Network.Session {
    /// <summary>Kick and promote party members in the UGS party lobby.</summary>
    public static class SessionPartyModeration {
        public static void KickMember(ISessionContext ctx, SteamId targetId) {
            if(targetId.Value == 0) return;
            if(ctx.UgsPartyLobby == null) {
                DevLog.LogWarning("[SessionManager] KickMember ignored: no active UGS party lobby.");
                return;
            }

            var localId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localId) || ctx.UgsPartyLobby.HostId != localId) {
                DevLog.LogWarning("[SessionManager] KickMember ignored: local player is not the UGS party host.");
                return;
            }

            if(!TryResolvePartyPlayerIdFromSteamId(ctx.UgsPartyLobby, targetId, out var targetUgsId)) {
                DevLog.LogWarning(
                    $"[SessionManager] KickMember failed: could not resolve UGS player for SteamId '{targetId.Value}'.");
                return;
            }

            if(targetUgsId == localId) {
                DevLog.LogWarning("[SessionManager] KickMember ignored: host cannot kick self.");
                return;
            }

            ctx.LaunchSessionTask(KickPartyMemberAsync(ctx, targetUgsId, targetId), "KickPartyMember");
        }

        public static void PromoteMember(ISessionContext ctx, SteamId targetId) {
            if(targetId.Value == 0) return;
            if(ctx.UgsPartyLobby == null) {
                DevLog.LogWarning("[SessionManager] PromoteMember ignored: no active UGS party lobby.");
                return;
            }

            var localId = AuthenticationService.Instance.PlayerId;
            if(string.IsNullOrEmpty(localId) || ctx.UgsPartyLobby.HostId != localId) {
                DevLog.LogWarning("[SessionManager] PromoteMember ignored: local player is not the UGS party host.");
                return;
            }

            if(!TryResolvePartyPlayerIdFromSteamId(ctx.UgsPartyLobby, targetId, out var targetUgsId)) {
                DevLog.LogWarning(
                    $"[SessionManager] PromoteMember failed: could not resolve UGS player for SteamId '{targetId.Value}'.");
                return;
            }

            if(targetUgsId == localId) return;
            ctx.LaunchSessionTask(PromotePartyHostAsync(ctx, targetUgsId, targetId), "PromotePartyHost");
        }

        private static bool TryResolvePartyPlayerIdFromSteamId(Unity.Services.Lobbies.Models.Lobby partyLobby,
            SteamId steamId, out string ugsPlayerId) {
            ugsPlayerId = null;
            if(partyLobby?.Players == null) return false;

            var steamIdValue = steamId.Value.ToString();
            foreach(var player in partyLobby.Players) {
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

        private static async UniTask KickPartyMemberAsync(ISessionContext ctx, string targetUgsId, SteamId targetSteamId) {
            var lobby = ctx.UgsPartyLobby;
            if(lobby == null || string.IsNullOrEmpty(lobby.Id)) return;

            try {
                await LobbyService.Instance.RemovePlayerAsync(lobby.Id, targetUgsId);
                var updated = await LobbyService.Instance.GetLobbyAsync(lobby.Id);
                ctx.SetUgsPartyLobby(updated);
                DevLog.Log($"[SessionManager] Kicked party member SteamId '{targetSteamId.Value}' from UGS party.");
                ctx.NotifyPartyStateChanged();
            } catch(Exception ex) {
                DevLog.LogWarning($"[SessionManager] Failed to kick party member '{targetSteamId.Value}': {ex.Message}");
            }
        }

        private static async UniTask PromotePartyHostAsync(ISessionContext ctx, string targetUgsId, SteamId targetSteamId) {
            var lobby = ctx.UgsPartyLobby;
            if(lobby == null || string.IsNullOrEmpty(lobby.Id)) return;

            try {
                var update = new UpdateLobbyOptions { HostId = targetUgsId };
                var updated = await LobbyService.Instance.UpdateLobbyAsync(lobby.Id, update);
                ctx.SetUgsPartyLobby(updated);

                if(ctx.CurrentLobby.HasValue && ctx.CurrentLobby.Value.Owner.Id == SteamClient.SteamId) {
                    var socialLobby = ctx.CurrentLobby.Value;
                    socialLobby.Owner = new Friend(targetSteamId);
                    ctx.SetCurrentLobby(socialLobby);
                }

                DevLog.Log($"[SessionManager] Promoted party member SteamId '{targetSteamId.Value}' to UGS party host.");
                ctx.NotifyPartyStateChanged();
            } catch(Exception ex) {
                DevLog.LogWarning(
                    $"[SessionManager] Failed to promote party host to '{targetSteamId.Value}': {ex.Message}");
            }
        }
    }
}
