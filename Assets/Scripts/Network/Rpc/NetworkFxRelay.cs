using Game.Player;
using Game.Weapons;
using Unity.Netcode;
using UnityEngine;

namespace Network.Rpc {
    public class NetworkFxRelay : NetworkBehaviour {
        private PlayerController _playerController;
        private NetworkObject _networkObject;
        private WeaponManager _weaponManager; // Need this to get current weapon

        private void Awake() {
            _playerController = GetComponent<PlayerController>();
            _networkObject = GetComponent<NetworkObject>();
            _weaponManager = GetComponent<WeaponManager>(); // Get weapon manager
        }

        /// <summary>
        /// Owner calls this right after deciding the shot result.
        /// Pass the weapon that fired so we know which muzzle/tracer to use.
        /// </summary>
        public void RequestShotFx(Weapon firingWeapon, Vector3 endPoint) {
            if(_playerController != null && !_playerController.IsOwner) return;
            if(_networkObject == null || !_networkObject.IsSpawned) return;
            if(firingWeapon == null || !firingWeapon.NetworkObject || !firingWeapon.NetworkObject.IsSpawned) return;

            var activeMuzzle = firingWeapon.GetActiveMuzzle();
            var startPos = activeMuzzle ? activeMuzzle.position : _networkObject.transform.position;

            var shooterRef = (NetworkObjectReference)_networkObject; // shooter (player)
            var weaponRef = (NetworkObjectReference)firingWeapon.NetworkObject; // the actual weapon
            RequestShotFxServerRpc(shooterRef, weaponRef, startPos, endPoint);
        }

        [Rpc(SendTo.Server)]
        private void RequestShotFxServerRpc(NetworkObjectReference shooterRef,
            NetworkObjectReference weaponRef,
            Vector3 startPoint,
            Vector3 endPoint) {
            PlayShotFxClientRpc(shooterRef, weaponRef, startPoint, endPoint);
        }

        [Rpc(SendTo.Everyone)]
        private void PlayShotFxClientRpc(NetworkObjectReference shooterRef,
            NetworkObjectReference weaponRef,
            Vector3 ownerMuzzlePos,
            Vector3 endPoint) {
            if(!weaponRef.TryGet(out var weaponNO) || weaponNO == null) return;

            var weapon = weaponNO.GetComponent<Weapon>();
            if(weapon == null) return;

            // Resolve start position per-client (FP for owner, world muzzle for others)
            weapon.AssignMuzzlesIfMissing();
            var startPos = weapon.GetActiveMuzzle()?.position ?? ownerMuzzlePos;

            weapon.PlayNetworkedMuzzleFlashLocal();
            weapon.SpawnTracerLocal(startPos, endPoint);
        }
    }
}