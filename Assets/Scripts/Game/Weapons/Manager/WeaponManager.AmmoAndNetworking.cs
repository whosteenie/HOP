using Game.Match;
using Game.Player;
using Game.UI;
using Network.AntiCheat;
using Network.Events;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapons {
    public partial class WeaponManager {
        public enum AmmoSyncReason : byte {
            ReloadStarted = 0,
            ReloadSingleRound = 1,
            ReloadCompleted = 2,
            ReloadCanceled = 3,
            RefillCurrentWeapon = 4
        }

        public void RefreshOwnerAmmoHudFromCurrentWeapon() {
            if(!IsOwner) return;
            if(CurrentWeapon == null) return;

            var currentAmmo = Mathf.Max(0, CurrentWeapon.currentAmmo);
            var magSize = Mathf.Max(1, CurrentWeapon.GetMagSize());
            EventBus.Publish(new UpdateAmmoEvent(currentAmmo, magSize));
        }

        public void ResetAllWeaponAmmo() {
            if(!IsServer) {
                ResetAllWeaponAmmoServerRpc();
            }

            _ammoAuthority.ResetAllWeaponAmmo(weaponDataList, ResolveWeaponCapacity);
            if(IsServer) {
                _serverReloadInProgress = false;
            }
        }

        public void PrepareCurrentWeaponForPostMatchPodium() {
            if(CurrentWeapon == null) return;
            if(CurrentWeaponIndex < 0) return;

            CurrentWeapon.PrepareForPostMatchPodium();
            _ammoAuthority.SetLocalAmmo(CurrentWeaponIndex, CurrentWeapon.currentAmmo);
        }

        /// <summary>
        /// Drains ammo for the currently equipped weapon for this player.
        /// Server-authoritative: updates server validation ammo and syncs owner's FP/HUD state.
        /// </summary>
        public void DrainCurrentWeaponAmmoForTag() {
            if(!IsServer) return;
            if(CurrentWeaponIndex < 0 || CurrentWeaponIndex >= weaponDataList.Count) return;

            var data = weaponDataList[CurrentWeaponIndex];
            if(data == null) return;
            var magCapacity = ResolveWeaponCapacity(data);
            if(magCapacity <= 0) {
                Debug.LogError(
                    $"[WeaponManager][KIN-Strict] Invalid KIN ammo capacity while draining ammo for '{data.weaponName}'.");
                return;
            }

            _ammoAuthority.SetLocalAmmo(CurrentWeaponIndex, 0);
            UpdateServerAmmo(CurrentWeaponIndex, 0);
            ApplyDrainedAmmoOwnerClientRpc(CurrentWeaponIndex, 0, magCapacity);
        }

        [Rpc(SendTo.Owner)]
        private void ApplyDrainedAmmoOwnerClientRpc(int weaponIndex, int ammo, int magSize) {
            _ammoAuthority.SetLocalAmmo(weaponIndex, Mathf.Max(0, ammo));

            if(CurrentWeapon != null && CurrentWeaponIndex == weaponIndex) {
                CurrentWeapon.currentAmmo = Mathf.Max(0, ammo);
            }

            if(IsOwner && HUDManager.Instance != null && CurrentWeaponIndex == weaponIndex) {
                EventBus.Publish(new UpdateAmmoEvent(Mathf.Max(0, ammo), Mathf.Max(0, magSize)));
            }
        }

        private bool TryConsumeWeaponSwitchQuota() {
            var config = AntiCheatConfig.Instance;
            if(config == null) return true;
            if(RpcRateLimiter.TryConsume(OwnerClientId, RpcRateLimiter.Keys.WeaponSwitch, config.weaponSwitchLimit,
                    config.rpcWindowSeconds)) {
                return true;
            }

            AntiCheatLogger.LogRateLimit(OwnerClientId, RpcRateLimiter.Keys.WeaponSwitch);
            return false;
        }

        public bool RegisterServerShot(int weaponIndex, ulong shotId, float clientShotTime, out string reason) {
            reason = null;
            if(!IsServer) return true;

            if(weaponIndex != GetServerAuthoritativeWeaponIndex()) {
                reason = "weapon index mismatch";
                return false;
            }

            if(_serverReloadInProgress) {
                reason = "reloading";
                return false;
            }

            if(Time.time < _serverPullOutBlockedUntilTime) {
                reason = "pulling out";
                return false;
            }

            var config = AntiCheatConfig.Instance;
            return _ammoAuthority.RegisterServerShot(
                weaponIndex,
                shotId,
                Time.time,
                clientShotTime,
                config != null ? config.fireRateGraceSeconds : 0f,
                GetWeaponDataByIndex,
                ResolveWeaponCapacity,
                out reason
            );
        }

        public bool ValidateServerHitClaim(int weaponIndex, ulong shotId, out string reason) {
            reason = null;
            if(!IsServer) return true;

            if(weaponIndex != GetServerAuthoritativeWeaponIndex()) {
                reason = "weapon index mismatch";
                return false;
            }

            return _ammoAuthority.ValidateServerHitClaim(
                weaponIndex,
                shotId,
                GetWeaponDataByIndex,
                ResolveWeaponCapacity,
                out reason
            );
        }

        public bool TryComputeServerDamage(int weaponIndex, Vector3 hitPoint, out float damage, out string reason) {
            damage = 0f;
            reason = null;

            var data = GetWeaponDataByIndex(weaponIndex);
            if(data == null) {
                reason = "unknown weapon";
                return false;
            }

            if(weaponIndex != GetServerAuthoritativeWeaponIndex()) {
                reason = "weapon index mismatch";
                return false;
            }

            var shooter = playerController;
            if(shooter == null) {
                reason = "shooter controller missing";
                return false;
            }

            var origin = shooter.FpCameraTransform != null
                ? shooter.FpCameraTransform.position
                : shooter.transform.position;
            var distance = Vector3.Distance(origin, hitPoint);

            var baseDamage = data.baseDamage;
            if(data.useDamageFalloff) {
                var startRange = Mathf.Max(0f, data.maxDamageRange);
                var endRange = Mathf.Max(startRange, data.minDamageRange);
                var minDamage = Mathf.Clamp(data.minDamage, 0f, baseDamage);

                if(distance >= endRange) {
                    baseDamage = minDamage;
                } else if(distance > startRange) {
                    var t = Mathf.InverseLerp(startRange, endRange, distance);
                    baseDamage = Mathf.Lerp(baseDamage, minDamage, t);
                }
            }

            if(data.usePelletSpread) {
                baseDamage *= Mathf.Max(0f, data.pelletDamageMultiplier);
            }

            var multiplier = 1f;
            if(CurrentWeapon != null && CurrentWeaponIndex == weaponIndex) {
                multiplier = Mathf.Clamp(CurrentWeapon.netCurrentDamageMultiplier.Value, 1f, Weapon.MaxDamageMultiplier);
            }

            damage = Mathf.Min(baseDamage * multiplier, data.damageCap);
            return damage > 0f;
        }

        public bool IsFriendlyFireServer(PlayerController shooter, PlayerController victim) {
            if(shooter == null || victim == null) return false;

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || !MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId)) {
                return false;
            }

            var shooterTeamManager = shooter.TeamManager;
            var victimTeamManager = victim.TeamManager;
            if(shooterTeamManager == null || victimTeamManager == null) {
                return false;
            }

            return shooterTeamManager.netTeam.Value == victimTeamManager.netTeam.Value;
        }

        public void ReportWeaponStateSync(int weaponIndex, AmmoSyncReason reason, int localAmmoAfterEvent) {
            if(!IsServer) {
                ReportWeaponStateSyncServerRpc(weaponIndex, reason, localAmmoAfterEvent);
                return;
            }

            UpdateServerWeaponState(weaponIndex, reason, localAmmoAfterEvent);
        }

        public void ReportShotFired(int weaponIndex, ulong shotId, float clientShotTime) {
            if(!IsServer) {
                ReportShotFiredServerRpc(weaponIndex, shotId, clientShotTime);
                return;
            }

            RegisterServerShotAndLog(weaponIndex, shotId, clientShotTime);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void ReportWeaponStateSyncServerRpc(int weaponIndex, AmmoSyncReason reason, int localAmmoAfterEvent,
            RpcParams rpcParams = default) {
            if(rpcParams.Receive.SenderClientId != OwnerClientId) {
                AntiCheatLogger.LogAuthorityViolation("WeaponManager.ReportWeaponStateSyncServerRpc",
                    rpcParams.Receive.SenderClientId);
                return;
            }

            UpdateServerWeaponState(weaponIndex, reason, localAmmoAfterEvent);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void ResetAllWeaponAmmoServerRpc(RpcParams rpcParams = default) {
            if(rpcParams.Receive.SenderClientId != OwnerClientId) {
                AntiCheatLogger.LogAuthorityViolation("WeaponManager.ResetAllWeaponAmmoServerRpc",
                    rpcParams.Receive.SenderClientId);
                return;
            }

            _ammoAuthority.ResetAllWeaponAmmo(weaponDataList, ResolveWeaponCapacity);
            _serverReloadInProgress = false;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void ReportShotFiredServerRpc(int weaponIndex, ulong shotId, float clientShotTime,
            RpcParams rpcParams = default) {
            if(rpcParams.Receive.SenderClientId != OwnerClientId) {
                AntiCheatLogger.LogAuthorityViolation("WeaponManager.ReportShotFiredServerRpc",
                    rpcParams.Receive.SenderClientId);
                return;
            }

            RegisterServerShotAndLog(weaponIndex, shotId, clientShotTime);
        }

        private void UpdateServerAmmo(int weaponIndex, int ammo) {
            if(!IsServer) return;
            _ammoAuthority.UpdateServerAmmo(
                weaponIndex,
                ammo,
                GetWeaponDataByIndex,
                ResolveWeaponCapacity
            );
        }

        private int GetServerAuthoritativeWeaponIndex() {
            return _serverAuthoritativeWeaponIndex >= 0 ? _serverAuthoritativeWeaponIndex : CurrentWeaponIndex;
        }

        private float GetServerPullOutBlockDurationSeconds() {
            return Mathf.Max(0f, kinemationPullOutCompleteDelay);
        }

        private void ApplyServerAuthoritativeWeaponSwitch(int weaponIndex) {
            if(!IsServer) return;
            _serverAuthoritativeWeaponIndex = weaponIndex;
            _serverReloadInProgress = false;
            _serverPullOutBlockedUntilTime = Time.time + GetServerPullOutBlockDurationSeconds();
        }

        private bool TryValidateServerWeaponStateRequest(int weaponIndex, out WeaponData data, out int magCapacity,
            out string reason) {
            data = null;
            magCapacity = 0;
            reason = null;

            if(!IsServer) return true;

            data = GetWeaponDataByIndex(weaponIndex);
            if(data == null) {
                reason = "unknown weapon";
                return false;
            }

            magCapacity = ResolveWeaponCapacity(data);
            if(magCapacity <= 0) {
                reason = "invalid mag capacity";
                return false;
            }

            if(weaponIndex != GetServerAuthoritativeWeaponIndex()) {
                reason = "weapon index mismatch";
                return false;
            }

            return true;
        }

        private void UpdateServerWeaponState(int weaponIndex, AmmoSyncReason reason, int localAmmoAfterEvent) {
            if(!IsServer) return;

            if(!TryValidateServerWeaponStateRequest(weaponIndex, out var data, out var magCapacity,
                   out var validationReason)) {
                AntiCheatLogger.LogInvalidDamage(OwnerClientId, validationReason);
                return;
            }

            var clampedLocalAmmo = Mathf.Clamp(localAmmoAfterEvent, 0, magCapacity);

            switch(reason) {
                case AmmoSyncReason.ReloadStarted: {
                    var currentAmmo =
                        _ammoAuthority.GetServerAmmo(weaponIndex, GetWeaponDataByIndex, ResolveWeaponCapacity);
                    if(currentAmmo >= magCapacity) {
                        AntiCheatLogger.LogInvalidDamage(OwnerClientId, "reload start while full");
                        return;
                    }

                    if(clampedLocalAmmo < currentAmmo) {
                        UpdateServerAmmo(weaponIndex, clampedLocalAmmo);
                    }

                    _serverReloadInProgress = true;
                    return;
                }
                case AmmoSyncReason.ReloadSingleRound: {
                    if(!_serverReloadInProgress) {
                        AntiCheatLogger.LogInvalidDamage(OwnerClientId, "reload single without reload");
                        return;
                    }

                    if(data.useMagReload) {
                        AntiCheatLogger.LogInvalidDamage(OwnerClientId, "reload single on mag weapon");
                        return;
                    }

                    var ammoBeforeIncrement =
                        _ammoAuthority.GetServerAmmo(weaponIndex, GetWeaponDataByIndex, ResolveWeaponCapacity);
                    if(clampedLocalAmmo < ammoBeforeIncrement ||
                       clampedLocalAmmo > Mathf.Min(magCapacity, ammoBeforeIncrement + 1)) {
                        AntiCheatLogger.LogInvalidDamage(OwnerClientId, "invalid reload single ammo");
                        return;
                    }

                    if(!_ammoAuthority.TryIncrementServerAmmo(weaponIndex, GetWeaponDataByIndex,
                           ResolveWeaponCapacity, out var currentAmmo, out var incrementReason)) {
                        if(incrementReason != "mag full") {
                            AntiCheatLogger.LogInvalidDamage(OwnerClientId, incrementReason ?? "reload increment failed");
                        }

                        if(currentAmmo >= magCapacity) {
                            _serverReloadInProgress = false;
                        }

                        return;
                    }

                    if(currentAmmo != clampedLocalAmmo) {
                        UpdateServerAmmo(weaponIndex, clampedLocalAmmo);
                        currentAmmo = clampedLocalAmmo;
                    }

                    if(currentAmmo >= magCapacity) {
                        _serverReloadInProgress = false;
                    }

                    return;
                }
                case AmmoSyncReason.ReloadCompleted:
                    if(!_serverReloadInProgress) {
                        AntiCheatLogger.LogInvalidDamage(OwnerClientId, "reload complete without reload");
                        return;
                    }

                    if(clampedLocalAmmo != magCapacity) {
                        AntiCheatLogger.LogInvalidDamage(OwnerClientId, "invalid reload complete ammo");
                        return;
                    }

                    UpdateServerAmmo(weaponIndex, clampedLocalAmmo);

                    _serverReloadInProgress = false;
                    return;
                case AmmoSyncReason.ReloadCanceled: {
                    var currentAmmo =
                        _ammoAuthority.GetServerAmmo(weaponIndex, GetWeaponDataByIndex, ResolveWeaponCapacity);
                    if(data.useMagReload) {
                        if(clampedLocalAmmo > currentAmmo) {
                            AntiCheatLogger.LogInvalidDamage(OwnerClientId, "invalid reload cancel ammo");
                            return;
                        }
                    } else if(clampedLocalAmmo < currentAmmo) {
                        AntiCheatLogger.LogInvalidDamage(OwnerClientId, "invalid reload cancel ammo");
                        return;
                    }

                    UpdateServerAmmo(weaponIndex, clampedLocalAmmo);
                    _serverReloadInProgress = false;
                    return;
                }
                case AmmoSyncReason.RefillCurrentWeapon:
                    if(clampedLocalAmmo != magCapacity) {
                        AntiCheatLogger.LogInvalidDamage(OwnerClientId, "invalid refill ammo");
                        return;
                    }

                    UpdateServerAmmo(weaponIndex, clampedLocalAmmo);
                    _serverReloadInProgress = false;
                    return;
                default:
                    AntiCheatLogger.LogInvalidDamage(OwnerClientId, $"invalid ammo sync reason {reason}");
                    return;
            }
        }

        private void RegisterServerShotAndLog(int weaponIndex, ulong shotId, float clientShotTime) {
            if(RegisterServerShot(weaponIndex, shotId, clientShotTime, out var reason)) return;
            AntiCheatLogger.LogInvalidDamage(OwnerClientId, reason);
        }
    }
}
