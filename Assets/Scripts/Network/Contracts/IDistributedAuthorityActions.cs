using Unity.Services.Lobbies.Models;
using Unity.Services.Multiplayer;

namespace Network.Contracts {
    /// <summary>
    /// Callbacks used by SessionNetworkLifecycleService during DA create/join and match-lobby refresh.
    /// Implemented by SessionManager.
    /// </summary>
    public interface IDistributedAuthorityActions {
        void BindActiveSession(ISession session);
        void UnbindActiveSession();
        bool IsLocalPlayerMatchLobbyHost(Lobby lobby);
        /// <summary>Called after match lobby is refreshed and local player is the new host (reset heartbeat state).</summary>
        void OnPromotedToMatchHost();
    }
}
