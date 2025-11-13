using System;
using Game.Player;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Network.Rpc {
    public class NetworkDamageRelay : NetworkBehaviour {
        /// <summary>
        /// Shooter-side callback (client) to play hit/kill UI, etc.
        /// Only invoked on the LOCAL shooter after the server confirms.
        /// </summary>
        public event Action<bool> OnHitConfirm;
        
        
        /// <summary>
        /// Called by the local owner (client) to ask the server to apply damage to a target player.
        /// The target is passed as a NetworkObjectReference to avoid hash/index lookups.
        /// </summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestDamageServerRpc(NetworkObjectReference targetRef, float damage, Vector3 hitPoint, Vector3 hitNormal) {
            if (!targetRef.TryGet(out var networkObject) || networkObject == null || !networkObject.IsSpawned) {
                Debug.LogWarning("[NetworkDamageRelay] Target is invalid/despawned.");
                return;
            }

            var victim = networkObject.GetComponent<PlayerController>();
            if (!victim || victim.netIsDead.Value) return;

            var shooterId = OwnerClientId; // the caller of this RPC

            // Optional: prevent self-damage via this path
            if (victim.OwnerClientId == shooterId) {
                Debug.LogWarning("[NetworkDamageRelay] Prevented self-damage.");
                return;
            }

            // Apply on server (authoritative). This function will update stats (kills/deaths/damageDealt) on server.
            bool wasKill = victim.ApplyDamageServer_Auth(damage, hitPoint, hitNormal, shooterId);

            // Send a confirm to EVERYONE, but only the shooter will act on it (self-filter).
            HitConfirmClientRpc(shooterId, wasKill);
        }
        
        /// <summary>
        /// Server -> Clients: notify a specific shooter they hit/fragged (self-filter on client).
        /// </summary>
        [Rpc(SendTo.Everyone)]
        private void HitConfirmClientRpc(ulong shooterClientId, bool wasKill) {
            if (NetworkManager == null) return;
            if (NetworkManager.LocalClientId != shooterClientId) return; // only the shooter reacts
            OnHitConfirm?.Invoke(wasKill);
        }
    }
}
