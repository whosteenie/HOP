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
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[NetworkFxRelay] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_playerNetworkObject == null) {
                _playerNetworkObject = playerController.NetworkObject;
            }

            if(_playerWeaponManager == null) {
                _playerWeaponManager = playerController.WeaponManager;
            }
        }

        public void RequestShotFx(Vector3 endPoint, Vector3 hitNormal, bool madeImpact,
            bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash = true) {
            if(!playerController.IsOwner || !_playerNetworkObject.IsSpawned) return;

            RequestShotFxServerRpc(_playerNetworkObject, endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef, playMuzzleFlash);
        }

        [Rpc(SendTo.Server)]
        private void RequestShotFxServerRpc(NetworkObjectReference shooterRef, Vector3 endPoint, Vector3 hitNormal,
            bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash) {
            PlayShotFxClientRpc(shooterRef, endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef, playMuzzleFlash);
        }

        [Rpc(SendTo.NotOwner)]
        private void PlayShotFxClientRpc(NetworkObjectReference shooterRef, Vector3 endPoint, Vector3 hitNormal,
            bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash) {
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
            weapon.SpawnTracerLocal(startPoint, endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef);
        }
    }
}