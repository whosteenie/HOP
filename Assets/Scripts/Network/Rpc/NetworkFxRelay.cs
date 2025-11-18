using Game.Player;
using Game.Weapons;
using Unity.Netcode;
using UnityEngine;

namespace Network.Rpc {
    public class NetworkFxRelay : NetworkBehaviour {
        [SerializeField] private PlayerController playerController;
        [SerializeField] private NetworkObject playerNetworkObject;
        [SerializeField] private WeaponManager playerWeaponManager;

        public void RequestShotFx(Vector3 endPoint, Vector3 muzzlePosition) {
            if(!playerController.IsOwner || !playerNetworkObject.IsSpawned) return;

            RequestShotFxServerRpc(playerNetworkObject, endPoint, muzzlePosition);
        }

        [Rpc(SendTo.Server)]
        private void RequestShotFxServerRpc(NetworkObjectReference shooterRef, Vector3 endPoint, Vector3 muzzlePosition) {
            PlayShotFxClientRpc(shooterRef, endPoint, muzzlePosition);
        }

        [Rpc(SendTo.NotOwner)]
        private void PlayShotFxClientRpc(NetworkObjectReference shooterRef, Vector3 endPoint, Vector3 muzzlePosition) {
            if(!shooterRef.TryGet(out var networkObject) || networkObject == null) return;

            var weaponManager = networkObject.GetComponent<WeaponManager>();
            if(weaponManager == null) return;

            var weapon = weaponManager.CurrentWeapon;
            if(weapon == null) return;

            // Use server-provided muzzle position for accurate FX (captured at shot time)
            // This ensures FX are synced even when moving fast
            var startPoint = muzzlePosition;

            // Play FX
            weapon.PlayNetworkedMuzzleFlash();
            weapon.SpawnTracerLocal(startPoint, endPoint);
        }
    }
}