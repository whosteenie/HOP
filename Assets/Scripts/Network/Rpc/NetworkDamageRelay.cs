using System;
using Game.Player.Core;
using Game.Weapons.Manager;
using Network.AntiCheat;
using Network.Diagnostics;
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
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        public void RequestDamageServerRpc(NetworkObjectReference targetRef, Vector3 hitPoint,
            Vector3 hitDirection, string bodyPartTag = null, bool isHeadshot = false, int weaponIndex = -1,
            float clientShotTime = 0f, ulong shotId = 0, RpcParams rpcParams = default) {
            var senderClientId = rpcParams.Receive.SenderClientId;
            if(senderClientId != OwnerClientId) {
                AntiCheatLogger.LogAuthorityViolate("NetworkDamageRelay.RequestDamageServerRpc", senderClientId);
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

            if(!DebugHelpers.TryGetNetworkObject(targetRef, out var networkObject, senderClientId,
                    "NetworkDamageRelay.RequestDamageServerRpc")) {
                return;
            }

            var victim = networkObject.GetComponent<PlayerController>();
            if(!victim) {
                return;
            }

            if(victim.IsDead) {
                return;
            }

            if(weaponIndex < 0) {
                AntiCheatLogger.LogInvalidDamage(senderClientId, "invalid weapon index");
                return;
            }

            // Optional: prevent self-damage via this path
            if(victim.OwnerClientId == senderClientId) {
                return;
            }

            if(!NetworkManager.Singleton.ConnectedClients.TryGetValue(senderClientId, out var attackerClient)) {
                AntiCheatLogger.LogInvalidDamage(senderClientId, "shooter not found");
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
                AntiCheatLogger.LogInvalidDamage(senderClientId, "weapon manager missing");
                return;
            }

            if(shooterController == null) {
                AntiCheatLogger.LogInvalidDamage(senderClientId, "shooter controller missing");
                return;
            }

            if(shooterWeaponManager.CurrentWeaponIndex != weaponIndex) {
                AntiCheatLogger.LogInvalidDamage(senderClientId, "weapon index mismatch");
                return;
            }

            if(!shooterWeaponManager.ValidateServerHitClaim(weaponIndex, shotId, out var reason)) {
                if(reason == "unregistered shot") {
                    if(!shooterWeaponManager.RegisterServerShot(weaponIndex, shotId, clientShotTime, out var registerReason) &&
                       registerReason != "duplicate shot") {
                        AntiCheatLogger.LogInvalidDamage(senderClientId, registerReason ?? reason);
                        return;
                    }

                    if(!shooterWeaponManager.ValidateServerHitClaim(weaponIndex, shotId, out reason)) {
                        AntiCheatLogger.LogInvalidDamage(senderClientId, reason);
                        return;
                    }
                } else {
                    AntiCheatLogger.LogInvalidDamage(senderClientId, reason);
                    return;
                }
            }

            if(WeaponManager.IsFriendlyFireServer(shooterController, victim)) {
                AntiCheatLogger.LogInvalidDamage(senderClientId, "friendly fire rejected");
                return;
            }

            if(!shooterWeaponManager.TryComputeServerDamage(weaponIndex, hitPoint, out var serverDamage,
                    out reason)) {
                AntiCheatLogger.LogInvalidDamage(senderClientId, reason ?? "server damage computation failed");
                return;
            }

            var weaponId = shooterWeaponManager.GetWeaponIdByIndex(weaponIndex);

            // Apply on server (authoritative). Damage is derived on the host, not trusted from the client claim.
            var wasKill = victim.ApplyDamageServer_Auth(serverDamage, hitPoint, hitDirection, senderClientId, bodyPartTag,
                isHeadshot, weaponId);

            SendHitConfirmToOwner(wasKill);
        }

        /// <summary>
        /// Server -> Clients: notify a specific shooter they hit/fragged (self-filter on client).
        /// </summary>
        public void SendHitConfirmToOwner(bool wasKill) {
            HitConfirmOwnerRpc(wasKill);
        }

        [Rpc(SendTo.Owner)]
        private void HitConfirmOwnerRpc(bool wasKill) {
            if(OnHitConfirm != null) {
                OnHitConfirm.Invoke(wasKill);
            }
        }
    }
}
