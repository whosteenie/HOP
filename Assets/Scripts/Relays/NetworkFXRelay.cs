using Player;
using Unity.Netcode;
using UnityEngine;
using Weapons;

namespace Relays {
    public class NetworkFXRelay : NetworkBehaviour {
        // TODO: FIX!!!
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

            // CAPTURE EXACT muzzle position at shot time
            var weapon = GetComponent<Weapon>();
            Transform activeMuzzle = weapon?.GetActiveMuzzle();
            Vector3 startPos = activeMuzzle ? activeMuzzle.position : _networkObject.transform.position;
    
            RequestShotFxServerRpc(_networkObject, startPos, endPoint);
        }
        
        [ServerRpc(RequireOwnership = false)]  // Allow server to validate if needed
        private void RequestShotFxServerRpc(NetworkObjectReference shooterRef, Vector3 startPoint, Vector3 endPoint) {
            // Forward identical start/end to EVERY client
            PlayShotFxClientRpc(shooterRef, startPoint, endPoint);
        }

        [ClientRpc]
        private void PlayShotFxClientRpc(NetworkObjectReference shooterRef, Vector3 startPoint, Vector3 endPoint) {
            if(!shooterRef.TryGet(out NetworkObject networkObject) || networkObject == null) return;

            var weapon = networkObject.GetComponent<Weapon>();
            if(weapon == null) return;

            // 1. EVERY client plays their LOCAL muzzle flash (owner=FP, others=world)
            weapon.AssignMuzzlesIfMissing();
            weapon.PlayNetworkedMuzzleFlashLocal();

            // 2. EVERY client spawns tracer from EXACT shared start->end positions
            weapon.SpawnTracerLocal(startPoint, endPoint);
        }
    }
}