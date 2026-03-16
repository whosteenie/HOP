using Game.Settings;
using Game.Social;
using Network.Session;
using Network.SessionContracts;
using Steamworks;
using UnityEngine;

namespace Game.Social {
    /// <summary>
    /// Wires Game-specific social behavior (chat presence, streamer mode, player icon)
    /// into Network.Session.SteamSocialBridge via hooks so the network layer does
    /// not depend directly on Game.Settings or Game.Social.ChatManager.
    /// </summary>
    public sealed class SteamSocialBridgeGameAdapter : MonoBehaviour {
        private void Awake() {
            SteamSocialBridge.SetLobbyPresenceNotifier(OnLobbyPresenceChanged);
            SteamSocialBridge.SetUpdateLocalDisplayMetadata(UpdateLocalDisplayMetadata);
        }

        private static void OnLobbyPresenceChanged(string friendName, bool joined) {
            if (ChatManager.Instance == null) return;
            ChatManager.SendLobbyPresenceMessage(friendName, joined);
        }

        private static void UpdateLocalDisplayMetadata(ISessionContext ctx) {
            if (ctx is not { CurrentLobby: not null }) return;
            if (!SteamClient.IsValid || !SteamClient.IsLoggedOn) return;

            try {
                var displayName = StreamerMode.GetLocalDisplayName();
                if (string.IsNullOrEmpty(displayName)) return;

                ctx.CurrentLobby.Value.SetMemberData("DisplayName", displayName);

                var hide = StreamerMode.Enabled;
                ctx.CurrentLobby.Value.SetMemberData("AvatarHidden", hide ? "1" : "0");

                var data = GameSettings.Data;
                var baseColor = data.player.customization.baseColor;
                var iconId = PlayerIconPicker.PickIconIdFromBaseColor(baseColor, hide);
                ctx.CurrentLobby.Value.SetMemberData("PlayerIcon", iconId);
            } catch (System.Exception ex) {
                if (Debug.isDebugBuild) {
                    Debug.LogWarning($"[SessionManager] Failed to update local lobby display metadata: {ex.Message}");
                }
            }
        }
    }
}

