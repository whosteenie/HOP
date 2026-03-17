using System.Collections.Generic;
using Diagnostics;
using Game.Player.Core;
using Game.Weapon.Manager;
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

            var victim = targetObject.GetComponent<PlayerController>();
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

            var shooterController = attackerClient.PlayerObject != null
                ? attackerClient.PlayerObject.GetComponent<PlayerController>()
                : null;
            var shooterWeaponManager = shooterController != null ? shooterController.WeaponManager : null;
            if(shooterController == null) {
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

            if(WeaponManager.IsFriendlyFireServer(shooterController, victim)) {
                AntiCheatLogger.LogInvalidDamage(shooterId, "friendly fire rejected");
                return;
            }

            if(!shooterWeaponManager.TryComputeServerDamage(weaponIndex, hitPoint, out var serverDamage, out reason)) {
                AntiCheatLogger.LogInvalidDamage(shooterId, reason ?? "server damage computation failed");
                return;
            }

            var weaponId = shooterWeaponManager.GetWeaponIdByIndex(weaponIndex);

            var wasKill = victim.ApplyDamageServer_Auth(serverDamage, hitPoint, hitDirection, shooterId, bodyPartTag,
                isHeadshot, weaponId);

            var shooterRelay = shooterController.DamageRelay;
            if(shooterRelay != null) {
                shooterRelay.SendHitConfirmToOwner(wasKill);
            }
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestShotFxServerRpc(NetworkObjectReference shooterRef, Vector3 endPoint,
            Vector3 hitNormal, bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef,
            bool playMuzzleFlash, Vector3 shooterVelocity, RpcParams rpcParams = default) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return;
            }

            var senderClientId = rpcParams.Receive.SenderClientId;
            if(!shooterRef.TryGet(out var shooterObject) || shooterObject == null) {
                return;
            }

            var shooter = shooterObject.GetComponent<PlayerController>();
            if(shooter == null || shooter.OwnerClientId != senderClientId) {
                AntiCheatLogger.LogAuthorityViolate("MatchCombatAuthority.RequestShotFxServerRpc",
                    senderClientId);
                return;
            }

            BroadcastShotFxClientRpc(shooterRef, endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef,
                playMuzzleFlash, shooterVelocity);
        }

        [Rpc(SendTo.Everyone)]
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void BroadcastShotFxClientRpc(NetworkObjectReference shooterRef, Vector3 endPoint, Vector3 hitNormal,
            bool madeImpact, bool hitPlayer, NetworkObjectReference hitPlayerRef, bool playMuzzleFlash,
            Vector3 shooterVelocity) {
            if(!shooterRef.TryGet(out var shooterObject) || shooterObject == null) {
                return;
            }

            var fxRelay = shooterObject.GetComponent<WeaponFxRelay>();
            if(fxRelay == null) {
                return;
            }

            fxRelay.QueueRemoteShotFx(endPoint, hitNormal, madeImpact, hitPlayer, hitPlayerRef, playMuzzleFlash,
                shooterVelocity);
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

            var player = playerObject.GetComponent<PlayerController>();
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

            var player = playerObject.GetComponent<PlayerController>();
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
            WeaponManager.AmmoSyncReason reason, int localAmmoAfterEvent, RpcParams rpcParams = default) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) {
                return;
            }

            var senderClientId = rpcParams.Receive.SenderClientId;
            if(!playerRef.TryGet(out var playerObject) || playerObject == null) {
                return;
            }

            var player = playerObject.GetComponent<PlayerController>();
            var weaponManager = player != null ? player.WeaponManager : null;
            if(player == null || weaponManager == null) {
                return;
            }

            if(player.OwnerClientId != senderClientId) {
                AntiCheatLogger.LogAuthorityViolate("MatchCombatAuthority.RequestWeaponStateSyncServerRpc",
                    senderClientId);
                return;
            }

            weaponManager.UpdateServerWeaponState(weaponIndex, reason, localAmmoAfterEvent);
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

            var player = playerObject.GetComponent<PlayerController>();
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

            var player = playerObject.GetComponent<PlayerController>();
            var healthController = player != null ? player.CombatController : null;
            if(healthController == null) {
                return;
            }

            if(player.OwnerClientId != senderClientId) {
                AntiCheatLogger.LogAuthorityViolate("MatchCombatAuthority.RequestRespawnServerRpc",
                    senderClientId);
                return;
            }

            healthController.ProcessRespawnAuthorityRequest();
        }
    }
}
