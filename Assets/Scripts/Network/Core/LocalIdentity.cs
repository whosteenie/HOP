using Game.Social;
using Steamworks;
using Unity.Services.Authentication;

namespace Network.Core {
    /// <summary>
    /// Centralized access to the local user's identities for backend services (UGS) and social display (Steam).
    /// </summary>
    public static class LocalIdentity {
        public static string GetUgsPlayerId() {
            try {
                if(AuthenticationService.Instance.IsSignedIn) {
                    return AuthenticationService.Instance.PlayerId;
                }
            } catch {
                // UGS not initialized yet.
            }
            return "";
        }

        public static ulong GetSteamId() {
            if(SteamClient.IsValid && SteamClient.IsLoggedOn) {
                return SteamClient.SteamId.Value;
            }
            return 0;
        }

        public static string GetDisplayName() {
            return StreamerMode.GetLocalDisplayName();
        }
    }
}

