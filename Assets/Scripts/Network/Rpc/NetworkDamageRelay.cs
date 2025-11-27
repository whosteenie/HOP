using System;
using Game.Player;
using Network.AntiCheat;
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
        public void RequestDamageServerRpc(NetworkObjectReference targetRef, float damage, Vector3 hitPoint,
            Vector3 hitDirection, string bodyPartTag = null, bool isHeadshot = false, int weaponIndex = -1,
            ulong shotId = 0) {
            var config = AntiCheatConfig.Instance;
            if(config != null) {
                if(!RpcRateLimiter.TryConsume(OwnerClientId, RpcRateLimiter.Keys.Damage, config.damageRpcLimit,
                        config.rpcWindowSeconds)) {
                    AntiCheatLogger.LogRateLimit(OwnerClientId, RpcRateLimiter.Keys.Damage);
                    return;
                }
            }

            if(!targetRef.TryGet(out var networkObject) || networkObject == null || !networkObject.IsSpawned) {
                Debug.LogWarning("[NetworkDamageRelay] Target is invalid/despawned.");
                return;
            }

            var victim = networkObject.GetComponent<PlayerController>();
            if(!victim || victim.IsDead) return;

            var shooterId = OwnerClientId; // the caller of this RPC

            if(weaponIndex < 0) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "invalid weapon index");
                return;
            }

            // Optional: prevent self-damage via this path
            if(victim.OwnerClientId == shooterId) {
                return;
            }

            if(!NetworkManager.Singleton.ConnectedClients.TryGetValue(shooterId, out var attackerClient)) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "shooter not found");
                return;
            }

            var shooterController = attackerClient.PlayerObject?.GetComponent<PlayerController>();
            var shooterWeaponManager = shooterController?.WeaponManager;
            if(shooterWeaponManager == null) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "weapon manager missing");
                return;
            }

            if(!shooterWeaponManager.ValidateServerShot(weaponIndex, shotId, out var reason)) {
                AntiCheatLogger.LogInvalidDamage(shooterId, reason);
                return;
            }

            if(!shooterWeaponManager.ValidateDamageRange(weaponIndex, hitPoint, out var rangeReason)) {
                AntiCheatLogger.LogInvalidDamage(shooterId, rangeReason);
                return;
            }

            // Apply on server (authoritative). This function will update stats (kills/deaths/damageDealt) on server.
            // Body part tag and headshot flag are passed through for future headshot multiplier implementation
            var wasKill = victim.ApplyDamageServer_Auth(damage, hitPoint, hitDirection, shooterId, bodyPartTag, isHeadshot);

            // Send a confirmation to EVERYONE, but only the shooter will act on it (self-filter).
            HitConfirmClientRpc(shooterId, wasKill);
        }

        /// <summary>
        /// Server -> Clients: notify a specific shooter they hit/fragged (self-filter on client).
        /// </summary>
        [Rpc(SendTo.Everyone)]
        private void HitConfirmClientRpc(ulong shooterClientId, bool wasKill) {
            if(NetworkManager == null) return;
            if(NetworkManager.LocalClientId != shooterClientId) return; // only the shooter reacts
            OnHitConfirm?.Invoke(wasKill);
        }
    }
}