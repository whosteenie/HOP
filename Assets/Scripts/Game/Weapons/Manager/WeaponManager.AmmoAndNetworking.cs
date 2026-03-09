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
            Reload = 0,
            RefillCurrentWeapon = 1
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

        public bool ValidateServerShot(int weaponIndex, ulong shotId, out string reason) {
            reason = null;
            if(!IsServer) return true;

            var config = AntiCheatConfig.Instance;
            return _ammoAuthority.ValidateServerShot(
                weaponIndex,
                shotId,
                Time.time,
                config != null ? config.fireRateGraceSeconds : 0f,
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

            if(CurrentWeaponIndex != weaponIndex) {
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

        public void ReportAmmoSync(int weaponIndex, int newAmmo, AmmoSyncReason reason) {
            if(!IsServer) {
                ReportAmmoSyncServerRpc(weaponIndex, newAmmo, reason);
                return;
            }

            UpdateServerAmmo(weaponIndex, newAmmo, reason);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void ReportAmmoSyncServerRpc(int weaponIndex, int newAmmo, AmmoSyncReason reason,
            RpcParams rpcParams = default) {
            if(rpcParams.Receive.SenderClientId != OwnerClientId) {
                AntiCheatLogger.LogAuthorityViolation("WeaponManager.ReportAmmoSyncServerRpc",
                    rpcParams.Receive.SenderClientId);
                return;
            }

            UpdateServerAmmo(weaponIndex, newAmmo, reason);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void ResetAllWeaponAmmoServerRpc(RpcParams rpcParams = default) {
            if(rpcParams.Receive.SenderClientId != OwnerClientId) {
                AntiCheatLogger.LogAuthorityViolation("WeaponManager.ResetAllWeaponAmmoServerRpc",
                    rpcParams.Receive.SenderClientId);
                return;
            }

            _ammoAuthority.ResetAllWeaponAmmo(weaponDataList, ResolveWeaponCapacity);
        }

        private void UpdateServerAmmo(int weaponIndex, int ammo, AmmoSyncReason reason) {
            if(!IsServer) return;

            switch(reason) {
                case AmmoSyncReason.Reload:
                case AmmoSyncReason.RefillCurrentWeapon:
                    break;
                default:
                    AntiCheatLogger.LogInvalidDamage(OwnerClientId, $"invalid ammo sync reason {reason}");
                    return;
            }

            _ammoAuthority.UpdateServerAmmo(
                weaponIndex,
                ammo,
                GetWeaponDataByIndex,
                ResolveWeaponCapacity
            );
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
    }
}
