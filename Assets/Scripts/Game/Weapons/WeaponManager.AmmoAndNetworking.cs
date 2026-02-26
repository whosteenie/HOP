using Game.UI;
using Network.AntiCheat;
using Network.Diagnostics;
using Network.Events;
using Unity.Netcode;
using UnityEngine;

namespace Game.Weapons {
    public partial class WeaponManager {
        public void ResetAllWeaponAmmo() {
            _ammoAuthority.ResetAllWeaponAmmo(weaponDataList, ResolveWeaponCapacity);
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

        public void ReportAmmoSync(int weaponIndex, int newAmmo) {
            if(!IsServer) {
                ReportAmmoSyncServerRpc(weaponIndex, newAmmo);
                return;
            }

            UpdateServerAmmo(weaponIndex, newAmmo);
        }

        [Rpc(SendTo.Server)]
        private void ReportAmmoSyncServerRpc(int weaponIndex, int newAmmo) {
            UpdateServerAmmo(weaponIndex, newAmmo);
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
