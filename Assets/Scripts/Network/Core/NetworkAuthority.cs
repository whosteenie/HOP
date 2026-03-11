using Unity.Netcode;

namespace Network.Core {
    public static class NetworkAuthority {
        public static bool IsDistributedAuthority(NetworkManager networkManager) {
            return networkManager != null && networkManager.DistributedAuthorityMode;
        }

        public static bool IsSessionOwner(NetworkManager networkManager) {
            return networkManager != null &&
                   networkManager.IsListening &&
                   networkManager.LocalClient != null &&
                   networkManager.LocalClient.IsSessionOwner;
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
            if(networkObject == null || !networkObject.HasAuthority || !IsSessionOwner(networkManager)) {
                return;
            }

            if(networkObject.OwnerClientId != networkManager.LocalClientId) {
                networkObject.ChangeOwnership(networkManager.LocalClientId);
            }

            networkObject.SetOwnershipStatus(NetworkObject.OwnershipStatus.SessionOwner, clearAndSet: true);
        }
    }
}
