using Unity.Netcode.Components;

namespace Network.Components {
    public class ClientNetworkTransform : NetworkTransform {
        protected override bool OnIsServerAuthoritative() {
            return false;
        }
    }
}