using Unity.Netcode;

namespace Network.Core {
    public static class NetworkAuthority {
        private static bool IsSessionOwner(NetworkManager networkManager) {
            return networkManager != null &&
                   networkManager.IsListening &&
                   networkManager.LocalClient is { IsSessionOwner: true };
        }

        public static bool HasGlobalAuthority(NetworkManager networkManager) {
            if(networkManager == null || !networkManager.IsListening) {
                return false;
            }

            return networkManager.DistributedAuthorityMode ? IsSessionOwner(networkManager) : networkManager.IsServer;
        }

        public static bool HasGlobalAuthority(NetworkBehaviour behaviour) {
            return behaviour != null &&
                   behaviour.IsSpawned &&
                   behaviour.NetworkManager != null &&
                   HasGlobalAuthority(behaviour.NetworkManager);
        }

        public static void TryConfigureSessionOwnerObject(NetworkBehaviour behaviour) {
            if(behaviour == null || !behaviour.IsSpawned) {
                return;
            }

            var networkManager = behaviour.NetworkManager;
            if(networkManager == null || !networkManager.DistributedAuthorityMode) {
                return;
            }

            var networkObject = behaviour.NetworkObject;
            if(networkObject == null || !IsSessionOwner(networkManager)) {
                return;
            }

            networkObject.DontDestroyWithOwner = true;

            if(networkObject.OwnerClientId != networkManager.LocalClientId) {
                networkObject.ChangeOwnership(networkManager.LocalClientId);
            }

            networkObject.SetOwnershipStatus(NetworkObject.OwnershipStatus.SessionOwner, clearAndSet: true);
        }
    }
}
