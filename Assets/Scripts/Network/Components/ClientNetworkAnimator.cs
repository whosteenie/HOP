using Unity.Netcode.Components;

namespace Network.Components {
    public class ClientNetworkAnimator : NetworkAnimator {
        protected override bool OnIsServerAuthoritative() {
            return false;
        }
    }
}