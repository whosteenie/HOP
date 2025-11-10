using Player;
using Unity.Netcode;
using UnityEngine;

namespace Relays {
    public class NetworkDamageRelay : NetworkBehaviour {
        /// <summary>
        /// Called by the local owner (client) to ask the server to apply damage to a target player.
        /// The target is passed as a NetworkObjectReference to avoid hash/index lookups.
        /// </summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestDamageServerRpc(NetworkObjectReference targetRef, float damage, Vector3 hitPoint, Vector3 hitNormal) {
            if(!IsServer) return;

            if (!targetRef.TryGet(out var networkObject) || networkObject == null || !networkObject.IsSpawned) {
                // Target is invalid / despawned / not in table
                return;
            }

            var targetPc = networkObject.GetComponent<PlayerController>();
            if (targetPc == null || targetPc.netIsDead.Value) return;

            // Optional: prevent self-damage via relay
            if (networkObject.OwnerClientId == OwnerClientId) return;

            targetPc.ApplyDamageServer(damage, hitPoint, hitNormal);
        }
    }
}
