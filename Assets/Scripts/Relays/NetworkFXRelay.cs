using Player;
using Unity.Netcode;
using UnityEngine;

namespace Relays {
    public class NetworkFXRelay : NetworkBehaviour {
        // TODO: singleton pattern

        private PlayerController _playerController;
        private NetworkObject _networkObject;

        private void Awake() {
            _playerController = gameObject.GetComponent<PlayerController>();
            _networkObject = gameObject.GetComponent<NetworkObject>();
        }
    
        /// <summary>
        /// Owner calls this right after deciding the shot result.
        /// </summary>
        public void RequestShotFx(Vector3 endPoint) {
            if(_playerController != null && !_playerController.IsOwner) return;
            if(_networkObject == null || !_networkObject.IsSpawned) return;

            RequestShotFxServerRpc(_networkObject, endPoint);
        }
    
        [Rpc(SendTo.Server)]
        private void RequestShotFxServerRpc(NetworkObjectReference shooterRef, Vector3 endPoint)
        {
            // Forward to everyone (including shooter) so each client can render with its own perspective
            PlayShotFxClientRpc(shooterRef, endPoint);
        }

        [Rpc(SendTo.Everyone)]
        private void PlayShotFxClientRpc(NetworkObjectReference shooterRef, Vector3 endPoint) {
            if(!shooterRef.TryGet(out var networkObject) || networkObject == null) return;

            // Resolve Weapon on that player
            var weapon = networkObject.GetComponent<Weapon.Weapon>();
            if(weapon == null) return;

            // 1) Muzzle flash on this client (owner sees FP flash, others see world flash)
            weapon.AssignMuzzlesIfMissing();
            weapon.PlayNetworkedMuzzleFlashLocal();

            // 2) Tracer from this client's own muzzle to the shared end point
            var muzzle = weapon.GetActiveMuzzle();
            if(muzzle == null) return;
            weapon.SpawnTracerLocal(muzzle.position, endPoint);
        }
    }
}