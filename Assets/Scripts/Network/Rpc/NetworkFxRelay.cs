using System;
using Game.Player;
using Game.Weapons;
using Unity.Netcode;
using UnityEngine;

namespace Network.Rpc {
    public class NetworkFxRelay : NetworkBehaviour {
        [SerializeField] private PlayerController playerController;
        private NetworkObject _playerNetworkObject;
        private WeaponManager _playerWeaponManager;

        private void Awake() {
            _playerNetworkObject ??= playerController.NetworkObject;
            _playerWeaponManager ??= playerController.WeaponManager;
        }

        public void RequestShotFx(Vector3 endPoint, Vector3 muzzlePosition, bool playMuzzleFlash = true) {
            if(!playerController.IsOwner || !_playerNetworkObject.IsSpawned) return;

            RequestShotFxServerRpc(_playerNetworkObject, endPoint, muzzlePosition, playMuzzleFlash);
        }

        [Rpc(SendTo.Server)]
        private void RequestShotFxServerRpc(NetworkObjectReference shooterRef, Vector3 endPoint,
            Vector3 muzzlePosition, bool playMuzzleFlash) {
            PlayShotFxClientRpc(shooterRef, endPoint, muzzlePosition, playMuzzleFlash);
        }

        [Rpc(SendTo.NotOwner)]
        private void PlayShotFxClientRpc(NetworkObjectReference shooterRef, Vector3 endPoint, Vector3 muzzlePosition,
            bool playMuzzleFlash) {
            if(!shooterRef.TryGet(out var networkObject) || networkObject == null) return;

            var weaponManager = networkObject.GetComponent<WeaponManager>();
            if(weaponManager == null) return;

            var weapon = weaponManager.CurrentWeapon;
            if(weapon == null) return;

            // For non-owners, use world muzzle position (not the owner's FP muzzle position)
            // This ensures trails spawn from the visible world weapon muzzle
            var startPoint = weapon.GetMuzzlePosition();

            // Play FX
            if(playMuzzleFlash) {
                weapon.PlayNetworkedMuzzleFlash();
            }
            weapon.SpawnTracerLocal(startPoint, endPoint);
        }
    }
}