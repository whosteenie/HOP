using Diagnostics;
using Events;
using Game.Weapon.Core;
using Network.AntiCheat;
using Network.Core;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapon.Manager {
    internal sealed class WeaponAuthority {
        private readonly WeaponManager _root;

        public WeaponAuthority(WeaponManager root) {
            _root = root;
        }

        private bool HasWeaponAuthority => NetworkAuthority.HasGlobalAuthority(_root);

        /// <summary>Refreshes owner ammo HUD from current weapon.</summary>
        public void RefreshAmmoHud() {
            if(!_root.IsOwner) return;
            if(_root.CurrentWeaponInternal == null) return;

            var currentAmmo = Mathf.Max(0, _root.CurrentWeaponInternal.currentAmmo);
            var magSize = Mathf.Max(1, _root.CurrentWeaponInternal.GetMagSize());
            EventBus.Publish(new UpdateAmmoEvent(currentAmmo, magSize));
        }

        public void ResetAllWeaponAmmo() {
            if(!HasWeaponAuthority) {
                if(WeaponCombatAuthority.Instance != null && _root.NetworkObject != null && _root.NetworkObject.IsSpawned) {
                    WeaponCombatAuthority.Instance.RequestResetWeaponAmmoServerRpc(
                        new NetworkObjectReference(_root.NetworkObject));
                } else {
                    _root.ResetAllWeaponAmmoServerRpc();
                }
                return;
            }

            ResetAllWeaponAmmoOnAuthority();
        }

        public void PrepareForPostMatchPodium() {
            if(_root.CurrentWeaponInternal == null) return;
            if(_root.CurrentWeaponIndexInternal < 0) return;

            _root.CurrentWeaponInternal.PrepareForPostMatchPodium();
            _root.AmmoAuthorityRef.SetLocalAmmo(_root.CurrentWeaponIndexInternal, _root.CurrentWeaponInternal.currentAmmo);
        }

        public void DrainCurrentWeaponAmmoForTag() {
            if(!HasWeaponAuthority) return;
            if(_root.CurrentWeaponIndexInternal < 0 || _root.CurrentWeaponIndexInternal >= _root.WeaponDataListRef.Count) return;

            var data = _root.WeaponDataListRef[_root.CurrentWeaponIndexInternal];
            if(data == null) return;
            var magCapacity = _root.ResolveWeaponCapacity(data);
            if(magCapacity <= 0) {
                DevLog.LogError(
                    $"[WeaponManager][KIN-Strict] Invalid KIN ammo capacity while draining ammo for '{data.weaponName}'.");
                return;
            }

            _root.AmmoAuthorityRef.SetLocalAmmo(_root.CurrentWeaponIndexInternal, 0);
            UpdateServerAmmo(_root.CurrentWeaponIndexInternal, 0);
            _root.ApplyDrainedAmmoOwnerClientRpc(_root.CurrentWeaponIndexInternal, 0, magCapacity);
        }

        public void ApplyDrainedAmmoOwnerClient(int weaponIndex, int ammo, int magSize) {
            _root.AmmoAuthorityRef.SetLocalAmmo(weaponIndex, Mathf.Max(0, ammo));

            if(_root.CurrentWeaponInternal != null && _root.CurrentWeaponIndexInternal == weaponIndex) {
                _root.CurrentWeaponInternal.currentAmmo = Mathf.Max(0, ammo);
            }

            if(_root.IsOwner && _root.CurrentWeaponIndexInternal == weaponIndex) {
                EventBus.Publish(new UpdateAmmoEvent(Mathf.Max(0, ammo), Mathf.Max(0, magSize)));
            }
        }

        public bool RegisterServerShot(int weaponIndex, ulong shotId, float clientShotTime, out string reason) {
            reason = null;
            if(!HasWeaponAuthority) return true;

            if(weaponIndex != GetServerAuthoritativeWeaponIndex()) {
                reason = "weapon index mismatch";
                return false;
            }

            if(IsServerReloadInProgressForWeapon(weaponIndex)) {
                reason = "reloading";
                return false;
            }

            if(Time.time < _root.ServerPullOutBlockedUntilTime) {
                reason = "pulling out";
                return false;
            }

            var config = AntiCheatConfig.Instance;
            return _root.AmmoAuthorityRef.RegisterServerShot(
                weaponIndex,
                shotId,
                Time.time,
                clientShotTime,
                config != null ? config.fireRateGraceSeconds : 0f,
                _root.GetWeaponDataByIndex,
                _root.ResolveWeaponCapacity,
                out reason
            );
        }

        public bool ValidateServerHitClaim(int weaponIndex, ulong shotId, out string reason) {
            reason = null;
            if(!HasWeaponAuthority) return true;

            if(weaponIndex == GetServerAuthoritativeWeaponIndex()) {
                return _root.AmmoAuthorityRef.ValidateServerHitClaim(
                    weaponIndex,
                    shotId,
                    _root.GetWeaponDataByIndex,
                    _root.ResolveWeaponCapacity,
                    out reason
                );
            }

            reason = "weapon index mismatch";
            return false;
        }

        public bool TryComputeServerDamage(int weaponIndex, Vector3 hitPoint, out float damage, out string reason) {
            damage = 0f;
            reason = null;

            var data = _root.GetWeaponDataByIndex(weaponIndex);
            if(data == null) {
                reason = "unknown weapon";
                return false;
            }

            if(weaponIndex != GetServerAuthoritativeWeaponIndex()) {
                reason = "weapon index mismatch";
                return false;
            }

            var shooter = _root.OwnerContext;
            if(shooter == null) {
                reason = "shooter controller missing";
                return false;
            }

            var origin = shooter.FpCameraTransform != null
                ? shooter.FpCameraTransform.position
                : shooter.Transform.position;
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
            if(_root.CurrentWeaponInternal != null && _root.CurrentWeaponIndexInternal == weaponIndex) {
                multiplier = _root.CurrentWeaponInternal.GetDamageMultiplier();
            }

            damage = Mathf.Min(baseDamage * multiplier, data.damageCap);
            return damage > 0f;
        }

        public void ReportWeaponStateSync(int weaponIndex, WeaponManager.AmmoSyncReason reason, int localAmmoAfterEvent) {
            if(!HasWeaponAuthority) {
                if(WeaponCombatAuthority.Instance != null && _root.NetworkObject != null && _root.NetworkObject.IsSpawned) {
                    WeaponCombatAuthority.Instance.RequestWeaponStateSyncServerRpc(
                        new NetworkObjectReference(_root.NetworkObject), weaponIndex, reason, localAmmoAfterEvent);
                } else {
                    _root.ReportWeaponStateSyncServerRpc(weaponIndex, reason, localAmmoAfterEvent);
                }
                return;
            }

            UpdateServerWeaponState(weaponIndex, reason, localAmmoAfterEvent);
        }

        public void ReportShotFired(int weaponIndex, ulong shotId, float clientShotTime) {
            if(!HasWeaponAuthority) {
                if(WeaponCombatAuthority.Instance != null && _root.NetworkObject != null && _root.NetworkObject.IsSpawned) {
                    WeaponCombatAuthority.Instance.RequestShotReportServerRpc(
                        new NetworkObjectReference(_root.NetworkObject), weaponIndex, shotId, clientShotTime);
                } else {
                    _root.ReportShotFiredServerRpc(weaponIndex, shotId, clientShotTime);
                }
                return;
            }

            RegisterServerShotAndLogOnAuthority(weaponIndex, shotId, clientShotTime);
        }

        public void ReportWeaponStateSyncServer(int weaponIndex, WeaponManager.AmmoSyncReason reason, int localAmmoAfterEvent,
            RpcParams rpcParams) {
            if(rpcParams.Receive.SenderClientId != _root.OwnerClientId) {
                AntiCheatLogger.LogAuthorityViolate("WeaponManager.ReportWeaponStateSyncServerRpc",
                    rpcParams.Receive.SenderClientId);
                return;
            }

            UpdateServerWeaponState(weaponIndex, reason, localAmmoAfterEvent);
        }

        public void ResetAllWeaponAmmoServer(RpcParams rpcParams) {
            if(rpcParams.Receive.SenderClientId != _root.OwnerClientId) {
                AntiCheatLogger.LogAuthorityViolate("WeaponManager.ResetAllWeaponAmmoServerRpc",
                    rpcParams.Receive.SenderClientId);
                return;
            }

            ResetAllWeaponAmmoOnAuthority();
        }

        public void ReportShotFiredServer(int weaponIndex, ulong shotId, float clientShotTime, RpcParams rpcParams) {
            if(rpcParams.Receive.SenderClientId != _root.OwnerClientId) {
                AntiCheatLogger.LogAuthorityViolate("WeaponManager.ReportShotFiredServerRpc",
                    rpcParams.Receive.SenderClientId);
                return;
            }

            RegisterServerShotAndLogOnAuthority(weaponIndex, shotId, clientShotTime);
        }

        public void UpdateServerWeaponState(int weaponIndex, WeaponManager.AmmoSyncReason reason, int localAmmoAfterEvent) {
            if(!HasWeaponAuthority) return;
            if(!TryValidateServerWeaponStateRequest(weaponIndex, out var data, out var magCapacity,
                   out var validationReason)) {
                AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, validationReason);
                return;
            }

            var clampedLocalAmmo = Mathf.Clamp(localAmmoAfterEvent, 0, magCapacity);

            switch(reason) {
                case WeaponManager.AmmoSyncReason.ReloadStarted: {
                    var currentAmmo =
                        _root.AmmoAuthorityRef.GetServerAmmo(weaponIndex, _root.GetWeaponDataByIndex, _root.ResolveWeaponCapacity);
                    if(currentAmmo >= magCapacity) {
                        AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, "reload start while full");
                        return;
                    }

                    if(clampedLocalAmmo < currentAmmo) {
                        UpdateServerAmmo(weaponIndex, clampedLocalAmmo);
                    }

                    _root.ServerReloadWeaponIndex = weaponIndex;
                    return;
                }
                case WeaponManager.AmmoSyncReason.ReloadSingleRound: {
                    if(!IsServerReloadInProgressForWeapon(weaponIndex)) {
                        AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, "reload single without reload");
                        return;
                    }

                    if(data.useMagReload) {
                        AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, "reload single on mag weapon");
                        return;
                    }

                    var ammoBeforeIncrement =
                        _root.AmmoAuthorityRef.GetServerAmmo(weaponIndex, _root.GetWeaponDataByIndex, _root.ResolveWeaponCapacity);
                    if(clampedLocalAmmo < ammoBeforeIncrement ||
                       clampedLocalAmmo > Mathf.Min(magCapacity, ammoBeforeIncrement + 1)) {
                        AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, "invalid reload single ammo");
                        return;
                    }

                    if(!_root.AmmoAuthorityRef.TryIncrementServerAmmo(weaponIndex, _root.GetWeaponDataByIndex,
                           _root.ResolveWeaponCapacity, out var currentAmmo, out var incrementReason)) {
                        if(incrementReason != "mag full") {
                            AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, incrementReason ?? "reload increment failed");
                        }

                        if(currentAmmo >= magCapacity) {
                            ClearServerReloadState();
                        }

                        return;
                    }

                    if(currentAmmo != clampedLocalAmmo) {
                        UpdateServerAmmo(weaponIndex, clampedLocalAmmo);
                        currentAmmo = clampedLocalAmmo;
                    }

                    if(currentAmmo >= magCapacity) {
                        ClearServerReloadState();
                    }

                    return;
                }
                case WeaponManager.AmmoSyncReason.ReloadCompleted:
                    if(!IsServerReloadInProgressForWeapon(weaponIndex)) {
                        AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, "reload complete without reload");
                        return;
                    }

                    if(clampedLocalAmmo != magCapacity) {
                        AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, "invalid reload complete ammo");
                        return;
                    }

                    UpdateServerAmmo(weaponIndex, clampedLocalAmmo);

                    ClearServerReloadState();
                    return;
                case WeaponManager.AmmoSyncReason.ReloadCanceled: {
                    if(_root.ServerReloadWeaponIndex >= 0 && !IsServerReloadInProgressForWeapon(weaponIndex)) {
                        AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, "reload cancel weapon mismatch");
                        return;
                    }

                    var currentAmmo =
                        _root.AmmoAuthorityRef.GetServerAmmo(weaponIndex, _root.GetWeaponDataByIndex, _root.ResolveWeaponCapacity);
                    if(data.useMagReload) {
                        if(clampedLocalAmmo > currentAmmo) {
                            AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, "invalid reload cancel ammo");
                            return;
                        }
                    } else if(clampedLocalAmmo < currentAmmo) {
                        AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, "invalid reload cancel ammo");
                        return;
                    }

                    UpdateServerAmmo(weaponIndex, clampedLocalAmmo);
                    ClearServerReloadState();
                    return;
                }
                case WeaponManager.AmmoSyncReason.RefillCurrentWeapon:
                    if(clampedLocalAmmo != magCapacity) {
                        AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, "invalid refill ammo");
                        return;
                    }

                    UpdateServerAmmo(weaponIndex, clampedLocalAmmo);
                    ClearServerReloadState();
                    ResetServerDamageMultiplierForCurrentWeapon();
                    return;
                default:
                    AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, $"invalid ammo sync reason {reason}");
                    return;
            }
        }

        public void ResetAllWeaponAmmoOnAuthority() {
            _root.AmmoAuthorityRef.ResetAllWeaponAmmo(_root.WeaponDataListRef, _root.ResolveWeaponCapacity);
            if(!HasWeaponAuthority) return;
            ClearServerReloadState();
            ResetServerDamageMultiplierForCurrentWeapon();
        }

        public void ApplyServerWeaponSwitch(int weaponIndex) {
            if(!HasWeaponAuthority) return;
            _root.ServerAuthoritativeWeaponIndex = weaponIndex;
            ClearServerReloadState();
            _root.ServerPullOutBlockedUntilTime = Time.time + GetPullOutBlockDuration();
        }

        private void UpdateServerAmmo(int weaponIndex, int ammo) {
            if(!HasWeaponAuthority) return;
            _root.AmmoAuthorityRef.UpdateServerAmmo(
                weaponIndex,
                ammo,
                _root.GetWeaponDataByIndex,
                _root.ResolveWeaponCapacity
            );
        }

        private int GetServerAuthoritativeWeaponIndex() {
            return _root.ServerAuthoritativeWeaponIndex >= 0
                ? _root.ServerAuthoritativeWeaponIndex
                : _root.CurrentWeaponIndexInternal;
        }

        private float GetPullOutBlockDuration() {
            return Mathf.Max(0f, _root.KinemationPullOutCompleteDelay);
        }

        private bool IsServerReloadInProgressForWeapon(int weaponIndex) {
            return _root.ServerReloadWeaponIndex == weaponIndex;
        }

        private void ClearServerReloadState() {
            _root.ServerReloadWeaponIndex = -1;
        }

        private void ResetServerDamageMultiplierForCurrentWeapon() {
            if(!HasWeaponAuthority) return;
            if(_root.CurrentWeaponInternal == null) return;
            _root.CurrentWeaponInternal.ResetDamageMultiplierImmediate();
        }

        private bool TryValidateServerWeaponStateRequest(int weaponIndex, out WeaponData data, out int magCapacity,
            out string reason) {
            data = null;
            magCapacity = 0;
            reason = null;

            if(!HasWeaponAuthority) return true;

            data = _root.GetWeaponDataByIndex(weaponIndex);
            if(data == null) {
                reason = "unknown weapon";
                return false;
            }

            magCapacity = _root.ResolveWeaponCapacity(data);
            if(magCapacity <= 0) {
                reason = "invalid mag capacity";
                return false;
            }

            if(weaponIndex == GetServerAuthoritativeWeaponIndex()) return true;
            reason = "weapon index mismatch";
            return false;
        }

        private void RegisterServerShotAndLog(int weaponIndex, ulong shotId, float clientShotTime) {
            if(RegisterServerShot(weaponIndex, shotId, clientShotTime, out var reason)) return;
            AntiCheatLogger.LogInvalidDamage(_root.OwnerClientId, reason);
        }

        public void RegisterServerShotAndLogOnAuthority(int weaponIndex, ulong shotId, float clientShotTime) {
            if(!HasWeaponAuthority) return;
            RegisterServerShotAndLog(weaponIndex, shotId, clientShotTime);
        }
    }
}
