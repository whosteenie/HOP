using Game.Social;
using Network.Contracts;
using Network.Session;
using UnityEngine;

namespace Game.Adapters {
    /// <summary>
    /// Wires Game.Social.SessionVoice into Network.Session.SessionManager via hooks so that
    /// SessionManager does not need a direct reference to Game.Social.
    /// </summary>
    public sealed class SessionVoiceGameAdapter : MonoBehaviour {
        private void Awake() {
            SessionManager.SetVoiceHooks(
                SessionVoice.TryLeaveVoiceChannelAsync,
                SessionVoice.TryJoinVoiceForSteamSocialLobby,
                (context, isLeavingOrShuttingDown, launchTask) =>
                    SessionVoice.TryJoinVoiceForActiveMatch(SessionManager.Instance, isLeavingOrShuttingDown, launchTask, context),
                GetActiveVoiceChannelName);
        }

        private static string GetActiveVoiceChannelName(ISessionContext ctx) {
            return SessionVoice.GetMatchVoiceChannelName(ctx);
        }
    }
}

