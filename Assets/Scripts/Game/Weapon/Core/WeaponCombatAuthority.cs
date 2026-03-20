using System.Collections.Generic;
using Diagnostics;
using Game.Weapon.Contracts;
using Network.AntiCheat;
using Network.Core;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapon.Core {
    public class WeaponCombatAuthority : NetworkBehaviour {
        public static WeaponCombatAuthority Instance { get; private set; }

        private bool _sessionOwnerCallbacksRegistered;
        private readonly Dictionary<ulong, ulong> _lastDamageQuotaShotIdByShooter = new();

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            _lastDamageQuotaShotIdByShooter.Clear();
            NetworkAuthority.TryConfigureSessionOwnerObject(this);
            RegisterSessionOwnerCallbacks();
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            _lastDamageQuotaShotIdByShooter.Clear();
            UnregisterSessionOwnerCallbacks();
        }

        public override void OnDestroy() {
            base.OnDestroy();
            UnregisterSessionOwnerCallbacks();
            if(Instance == this) {
                Instance = null;
            }
        }

        private void RegisterSessionOwnerCallbacks() {
            if(_sessionOwnerCallbacksRegistered || NetworkManager == null) return;
            NetworkManager.OnSessionOwnerPromoted += OnSessionOwnerPromoted;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
            _sessionOwnerCallbacksRegistered = true;
        }

        private void UnregisterSessionOwnerCallbacks() {
            if(!_sessionOwnerCallbacksRegistered || NetworkManager == null) return;
            NetworkManager.OnSessionOwnerPromoted -= OnSessionOwnerPromoted;
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            _sessionOwnerCallbacksRegistered = false;
        }

        private void OnSessionOwnerPromoted(ulong _) {
            if(NetworkAuthority.HasGlobalAuthority(this)) {
                NetworkAuthority.TryConfigureSessionOwnerObject(this);
            }
        }

        private void OnClientDisconnected(ulong clientId) {
            _lastDamageQuotaShotIdByShooter.Remove(clientId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestDamageServerRpc(NetworkObjectReference targetRef, Vector3 hitPoint,
            Vector3 hitDirection, string bodyPartTag = null, bool isHeadshot = false, int weaponIndex = -1,
            float clientShotTime = 0f, ulong shotId = 0, RpcParams rpcParams = default) {
            var shooterId = rpcParams.Receive.SenderClientId;

            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return;
            }

            var config = AntiCheatConfig.Instance;
            var shouldConsumeDamageQuota = shotId == 0;
            if(shotId != 0) {
                if(!_lastDamageQuotaShotIdByShooter.TryGetValue(shooterId, out var lastQuotaShotId) ||
                   lastQuotaShotId != shotId) {
                    _lastDamageQuotaShotIdByShooter[shooterId] = shotId;
                    shouldConsumeDamageQuota = true;
                }
            }

            if(config != null && shouldConsumeDamageQuota) {
                if(!RpcRateLimiter.TryConsume(shooterId, RpcRateLimiter.Keys.Damage, config.damageRpcLimit,
                        config.rpcWindowSeconds)) {
                    AntiCheatLogger.LogRateLimit(shooterId, RpcRateLimiter.Keys.Damage);
                    return;
                }
            }

            if(!DebugHelpers.TryGetNetworkObject(targetRef, out var targetObject, shooterId,
                    "MatchCombatAuthority.RequestDamageServerRpc")) {
                return;
            }

            var victim = targetObject.GetComponent<IWeaponCombatParticipant>();
            if(victim == null) {
                return;
            }

            if(victim.IsDead) {
                return;
            }

            if(weaponIndex < 0) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "invalid weapon index");
                return;
            }

            if(victim.OwnerClientId == shooterId) {
                return;
            }

            if(NetworkManager == null || !NetworkManager.ConnectedClients.TryGetValue(shooterId, out var attackerClient)) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "shooter not found");
                return;
            }

            var shooterParticipant = attackerClient.PlayerObject != null
                ? attackerClient.PlayerObject.GetComponent<IWeaponCombatParticipant>()
                : null;
            var shooterWeaponManager = shooterParticipant != null ? shooterParticipant.WeaponManager : null;
            if(shooterParticipant == null) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "shooter controller missing");
                return;
            }

            if(shooterWeaponManager == null) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "weapon manager missing");
                return;
            }

            if(shooterWeaponManager.CurrentWeaponIndex != weaponIndex) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "weapon index mismatch");
                return;
            }

            if(!shooterWeaponManager.ValidateServerHitClaim(weaponIndex, shotId, out var reason)) {
                if(reason == "unregistered shot") {
                    if(!shooterWeaponManager.RegisterServerShot(weaponIndex, shotId, clientShotTime, out var registerReason) &&
                       registerReason != "duplicate shot") {
                        AntiCheatLogger.LogInvalidDamage(shooterId, registerReason ?? reason);
                        return;
                    }

                    if(!shooterWeaponManager.ValidateServerHitClaim(weaponIndex, shotId, out reason)) {
                        AntiCheatLogger.LogInvalidDamage(shooterId, reason);
                        return;
                    }
                } else {
                    AntiCheatLogger.LogInvalidDamage(shooterId, reason);
                    return;
                }
            }

            if(shooterWeaponManager.IsFriendlyFireAgainst(victim.OwnerClientId)) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "friendly fire rejected");
                return;
            }

            if(!shooterWeaponManager.TryComputeServerDamage(weaponIndex, hitPoint, out var serverDamage, out reason)) {
                AntiCheatLogger.LogInvalidDamage(shooterId, reason ?? "server damage computation failed");
                return;
            }

            var weaponId = shooterWeaponManager.GetWeaponIdByIndex(weaponIndex);

            var wasKill = victim.ApplyDamageServerAuth(new DamageApplicationRequest {
                Damage = serverDamage,
                HitPoint = hitPoint,
                HitDirection = hitDirection,
                AttackerClientId = shooterId,
                BodyPartTag = bodyPartTag,
                IsHeadshot = isHeadshot,
                WeaponId = weaponId
            });

            var shooterRelay = shooterParticipant.DamageRelay;
            if(shooterRelay != null) {
                shooterRelay.SendHitConfirmToOwner(wasKill);
            }
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestShotReportServerRpc(NetworkObjectReference playerRef, int weaponIndex, ulong shotId,
            float clientShotTime, RpcParams rpcParams = default) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return;
            }

            var senderClientId = rpcParams.Receive.SenderClientId;
            if(!playerRef.TryGet(out var playerObject) || playerObject == null) {
                return;
            }

            var player = playerObject.GetComponent<IWeaponCombatParticipant>();
            var weaponManager = player != null ? player.WeaponManager : null;
            if(player == null || weaponManager == null) {
                return;
            }

            if(player.OwnerClientId != senderClientId) {
                AntiCheatLogger.LogAuthorityViolate("MatchCombatAuthority.RequestShotReportServerRpc",
                    senderClientId);
                return;
            }

            weaponManager.RegisterServerShotAndLogOnAuthority(weaponIndex, shotId, clientShotTime);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestWeaponSwitchServerRpc(NetworkObjectReference playerRef, int newIndex,
            RpcParams rpcParams = default) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return;
            }

            var senderClientId = rpcParams.Receive.SenderClientId;
            if(!playerRef.TryGet(out var playerObject) || playerObject == null) {
                return;
            }

            var player = playerObject.GetComponent<IWeaponCombatParticipant>();
            var weaponManager = player != null ? player.WeaponManager : null;
            if(player == null || weaponManager == null) {
                return;
            }

            if(player.OwnerClientId != senderClientId) {
                AntiCheatLogger.LogAuthorityViolate("MatchCombatAuthority.RequestWeaponSwitchServerRpc",
                    senderClientId);
                return;
            }

            weaponManager.ProcessWeaponSwitchRequest(newIndex);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestWeaponStateSyncServerRpc(NetworkObjectReference playerRef, int weaponIndex,
            WeaponAmmoSyncReason reason, int localAmmoAfterEvent, RpcParams rpcParams = default) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return;
            }

            var senderClientId = rpcParams.Receive.SenderClientId;
            if(!playerRef.TryGet(out var playerObject) || playerObject == null) {
                return;
            }

            var player = playerObject.GetComponent<IWeaponCombatParticipant>();
            var weaponManager = player != null ? player.WeaponManager : null;
            if(player == null || weaponManager == null) {
                return;
            }

            if(player.OwnerClientId != senderClientId) {
                AntiCheatLogger.LogAuthorityViolate("MatchCombatAuthority.RequestWeaponStateSyncServerRpc",
                    senderClientId);
                return;
            }

            weaponManager.UpdateServerWeaponState(weaponIndex, (byte)reason, localAmmoAfterEvent);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestResetWeaponAmmoServerRpc(NetworkObjectReference playerRef,
            RpcParams rpcParams = default) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return;
            }

            var senderClientId = rpcParams.Receive.SenderClientId;
            if(!playerRef.TryGet(out var playerObject) || playerObject == null) {
                return;
            }

            var player = playerObject.GetComponent<IWeaponCombatParticipant>();
            var weaponManager = player != null ? player.WeaponManager : null;
            if(player == null || weaponManager == null) {
                return;
            }

            if(player.OwnerClientId != senderClientId) {
                AntiCheatLogger.LogAuthorityViolate("MatchCombatAuthority.RequestResetWeaponAmmoServerRpc",
                    senderClientId);
                return;
            }

            weaponManager.ResetAllWeaponAmmoOnAuthority();
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestRespawnServerRpc(NetworkObjectReference playerRef, RpcParams rpcParams = default) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return;
            }

            var senderClientId = rpcParams.Receive.SenderClientId;

            if(!playerRef.TryGet(out var playerObject) || playerObject == null) {
                return;
            }

            var player = playerObject.GetComponent<IWeaponCombatParticipant>();
            if(player == null) {
                return;
            }

            if(player.OwnerClientId != senderClientId) {
                AntiCheatLogger.LogAuthorityViolate("MatchCombatAuthority.RequestRespawnServerRpc",
                    senderClientId);
                return;
            }

            player.ProcessRespawnRequest();
        }
    }
}
