using System;
using Cysharp.Threading.Tasks;
using Network.Contracts;
using UnityEngine;

namespace Game.Social {
    /// <summary>
    /// Voice channel join/leave helpers for Steam social lobby and UGS match. Uses VoiceManager.
    /// </summary>
    public static class SessionVoice {
        /// <summary>Leaves the current voice channel. Call during leave-to-menu.</summary>
        public static async UniTask TryLeaveVoiceChannelAsync() {
            if(VoiceManager.Instance == null) return;
            if(!VoiceManager.Instance.IsLoggedIn) return;
            try {
                await VoiceManager.Instance.LeaveChannelAsync();
            } catch(Exception ex) {
                Debug.LogWarning($"[SessionManager] Voice leave failed during session transition: {ex.Message}");
            }
        }

        /// <summary>Starts joining the voice channel for a Steam social lobby. Launches the task via the provided callback.</summary>
        public static void TryJoinVoiceForSteamSocialLobby(
            ulong lobbyId,
            string context,
            Func<bool> isLeavingOrShuttingDown,
            Action<UniTask, string> launchTask) {
            if(lobbyId == 0) return;
            if(isLeavingOrShuttingDown != null && isLeavingOrShuttingDown()) return;
            if(VoiceManager.Instance == null || !VoiceManager.Instance.IsLoggedIn) return;
            var channelName = "match_" + lobbyId;
            launchTask?.Invoke(
                VoiceManager.Instance.EnsureChannelJoinedAsync(channelName, context: context).AsUniTask(),
                $"VoiceJoinSteamSocialLobby/{context}");
            if(Debug.isDebugBuild)
                Debug.Log($"[SessionManager] Requested voice join for Steam social lobby '{lobbyId}' ({context}).");
        }

        /// <summary>Starts joining the voice channel for the active UGS match or Steam lobby. Launches the task via the provided callback.</summary>
        public static void TryJoinVoiceForActiveMatch(
            ISessionContext ctx,
            Func<bool> isLeavingOrShuttingDown,
            Action<UniTask, string> launchTask,
            string context) {
            if(isLeavingOrShuttingDown != null && isLeavingOrShuttingDown()) return;
            if(VoiceManager.Instance == null || !VoiceManager.Instance.IsLoggedIn) return;
            var channelName = GetMatchVoiceChannelName(ctx);
            if(string.IsNullOrEmpty(channelName)) {
                if(Debug.isDebugBuild)
                    Debug.Log($"[SessionManager] No active match channel available for voice join ({context}).");
                return;
            }
            launchTask?.Invoke(
                VoiceManager.Instance.EnsureChannelJoinedAsync(channelName, context: context).AsUniTask(),
                $"VoiceJoinMatch/{context}");
            if(Debug.isDebugBuild)
                Debug.Log($"[SessionManager] Requested voice join for active match channel '{channelName}' ({context}).");
        }

        /// <summary>Returns the voice channel name for the current match (UGS match lobby id or Steam lobby id), or null.</summary>
        public static string GetMatchVoiceChannelName(ISessionContext ctx) {
            if(ctx == null) return null;
            var ugsMatch = ctx.UgsMatchLobby;
            if(ugsMatch != null && !string.IsNullOrEmpty(ugsMatch.Id))
                return "match_" + ugsMatch.Id;
            var steamLobby = ctx.CurrentLobby;
            if(steamLobby.HasValue && steamLobby.Value.Id != 0)
                return "match_" + steamLobby.Value.Id;
            return null;
        }
    }
}
