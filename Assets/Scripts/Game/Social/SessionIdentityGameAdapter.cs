using Network.Session;
using UnityEngine;

namespace Game.Social {
    /// <summary>
    /// Wires Game.Social.LocalIdentity into Network.Session.SessionNetworkLifecycle via
    /// identity providers so the network stack does not depend directly on Game.Social.
    /// </summary>
    public sealed class SessionIdentityGameAdapter : MonoBehaviour {
        private void Awake() {
            SessionNetworkLifecycle.GetSteamIdProvider = LocalIdentity.GetSteamId;
            SessionNetworkLifecycle.GetUgsPlayerIdProvider = LocalIdentity.GetUgsPlayerId;
            SessionNetworkLifecycle.GetDisplayNameProvider = LocalIdentity.GetDisplayName;
        }
    }
}

