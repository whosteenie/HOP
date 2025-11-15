using Game.Player;
using Game.Weapons;
using Unity.Netcode;
using UnityEngine;

namespace Network.Rpc {
    public class NetworkFxRelay : NetworkBehaviour {
        [SerializeField] private PlayerController playerController;
        [SerializeField] private NetworkObject playerNetworkObject;
        [SerializeField] private WeaponManager playerWeaponManager;

        public void RequestShotFx(Vector3 endPoint) {
            if(!playerController.IsOwner || !playerNetworkObject.IsSpawned) return;

            RequestShotFxServerRpc(playerNetworkObject, endPoint);
        }

        [Rpc(SendTo.Server)]
        private void RequestShotFxServerRpc(NetworkObjectReference shooterRef, Vector3 endPoint) {
            PlayShotFxClientRpc(shooterRef, endPoint);
        }

        [Rpc(SendTo.NotOwner)]
        private void PlayShotFxClientRpc(NetworkObjectReference shooterRef, Vector3 endPoint) {
            if(!shooterRef.TryGet(out var networkObject) || networkObject == null) return;

            var weaponManager = networkObject.GetComponent<WeaponManager>();
            if(weaponManager == null) return;

            var weapon = weaponManager.CurrentWeapon;
            if(weapon == null) return;

            // Get muzzle position for THIS client (each calculates their own)
            var startPoint = weapon.GetMuzzlePosition();

            // Play FX
            weapon.PlayNetworkedMuzzleFlash();
            weapon.SpawnTracerLocal(startPoint, endPoint);
        }
    }
}