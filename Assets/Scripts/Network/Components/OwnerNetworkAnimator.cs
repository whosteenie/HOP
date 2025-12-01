using Unity.Netcode.Components;

namespace Network.Components {
    public class OwnerNetworkAnimator : NetworkAnimator {
        protected override bool OnIsServerAuthoritative() {
            return false;
        }
    }
}