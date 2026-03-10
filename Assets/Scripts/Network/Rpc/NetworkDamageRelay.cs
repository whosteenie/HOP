using System;
using Game.Player;
using Game.Weapons;
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
        /// Called by the local owner (client) to ask the server to verify and apply a shot result.
        /// The client supplies shot timing and shot identity; the host reconstructs the ray and resolves impact data.
        /// </summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        public void RequestDamageServerRpc(float claimedShotServerTime, int weaponIndex = -1, ulong shotId = 0,
            int pelletIndex = 0, bool precisionAim = false, RpcParams rpcParams = default) {
            var senderClientId = rpcParams.Receive.SenderClientId;
            if(senderClientId != OwnerClientId) {
                AntiCheatLogger.LogAuthorityViolation("NetworkDamageRelay.RequestDamageServerRpc", senderClientId);
                return;
            }

            var config = AntiCheatConfig.Instance;
            if(config != null) {
                if(!RpcRateLimiter.TryConsume(senderClientId, RpcRateLimiter.Keys.Damage, config.damageRpcLimit,
                        config.rpcWindowSeconds)) {
                    AntiCheatLogger.LogRateLimit(senderClientId, RpcRateLimiter.Keys.Damage);
                    return;
                }
            }

            var shooterId = senderClientId;

            if(weaponIndex < 0) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "invalid weapon index");
                return;
            }

            if(!NetworkManager.Singleton.ConnectedClients.TryGetValue(shooterId, out var attackerClient)) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "shooter not found");
                return;
            }

            PlayerController shooterController = null;
            if(attackerClient.PlayerObject != null) {
                shooterController = attackerClient.PlayerObject.GetComponent<PlayerController>();
            }
            WeaponManager shooterWeaponManager = null;
            if(shooterController != null) {
                shooterWeaponManager = shooterController.WeaponManager;
            }
            if(shooterWeaponManager == null) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "weapon manager missing");
                return;
            }

            if(shooterController == null) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "shooter controller missing");
                return;
            }

            if(shooterWeaponManager.CurrentWeaponIndex != weaponIndex) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "weapon index mismatch");
                return;
            }

            if(!shooterWeaponManager.ValidateServerShot(weaponIndex, shotId, pelletIndex, out var reason)) {
                AntiCheatLogger.LogInvalidDamage(shooterId, reason);
                return;
            }

            if(!shooterWeaponManager.TryVerifyServerHit(weaponIndex, shotId, pelletIndex, precisionAim,
                   claimedShotServerTime, out var victim, out var verifiedHitPoint, out _, out var bodyPartTag,
                   out var isHeadshot, out var verifiedShotOrigin, out reason)) {
                AntiCheatLogger.LogInvalidDamage(shooterId, reason ?? "server hit verification failed");
                return;
            }

            if(victim == null || victim.IsDead) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "verified victim missing");
                return;
            }

            if(victim.OwnerClientId == shooterId) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "self-hit rejected");
                return;
            }

            if(shooterWeaponManager.IsFriendlyFireServer(shooterController, victim)) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "friendly fire rejected");
                return;
            }

            if(!shooterWeaponManager.TryComputeServerDamage(weaponIndex, verifiedShotOrigin, verifiedHitPoint, out var serverDamage,
                   out reason)) {
                AntiCheatLogger.LogInvalidDamage(shooterId, reason ?? "server damage computation failed");
                return;
            }

            var weaponId = shooterWeaponManager.GetWeaponIdByIndex(weaponIndex);
            var hitDirection = verifiedHitPoint - verifiedShotOrigin;
            if(hitDirection.sqrMagnitude > 0.0001f) {
                hitDirection.Normalize();
            } else {
                hitDirection = shooterController.transform.forward;
            }

            // Apply on server (authoritative). The host verifies the hit and derives damage from host-side state.
            var wasKill = victim.ApplyDamageServer_Auth(serverDamage, verifiedHitPoint, hitDirection, shooterId, bodyPartTag,
                isHeadshot, weaponId);

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
            if(OnHitConfirm != null) {
                OnHitConfirm.Invoke(wasKill);
            }
        }
    }
}
