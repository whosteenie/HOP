using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Weapons {
    internal sealed class WeaponAmmoAuthority {
        private sealed class ServerWeaponState {
            public float LastShotTime;
            public int ServerAmmo;
            public ulong LastShotId;
            public int AcceptedClaimsForLastShot;
        }

        private readonly Dictionary<int, int> _weaponAmmo = new();
        private readonly Dictionary<int, ServerWeaponState> _serverWeaponStates = new();

        public void ClearAll() {
            _weaponAmmo.Clear();
            _serverWeaponStates.Clear();
        }

        public void CacheCurrentAmmo(int weaponIndex, int currentAmmo) {
            if(weaponIndex < 0) return;
            _weaponAmmo[weaponIndex] = Mathf.Max(0, currentAmmo);
        }

        public int ResolveRestoredAmmo(int weaponIndex, int magCapacity, bool seedWhenMissing) {
            var clampedCapacity = Mathf.Max(1, magCapacity);
            if(_weaponAmmo.TryGetValue(weaponIndex, out var storedAmmo)) {
                var clampedStored = Mathf.Clamp(storedAmmo, 0, clampedCapacity);
                _weaponAmmo[weaponIndex] = clampedStored;
                return clampedStored;
            }

            if(seedWhenMissing) {
                _weaponAmmo[weaponIndex] = clampedCapacity;
            }

            return clampedCapacity;
        }

        public void SeedMagazine(int weaponIndex, int magCapacity) {
            if(weaponIndex < 0) return;
            _weaponAmmo[weaponIndex] = Mathf.Clamp(magCapacity, 0, int.MaxValue);
        }

        public void SetLocalAmmo(int weaponIndex, int ammo) {
            if(weaponIndex < 0) return;
            _weaponAmmo[weaponIndex] = Mathf.Max(0, ammo);
        }

        public void ResetAllWeaponAmmo(IReadOnlyList<WeaponData> weaponDataList, Func<WeaponData, int> resolveWeaponCapacity) {
            _weaponAmmo.Clear();
            _serverWeaponStates.Clear();
            if(weaponDataList == null) return;

            for(var i = 0; i < weaponDataList.Count; i++) {
                var data = weaponDataList[i];
                if(data == null) continue;

                var magCapacity = resolveWeaponCapacity(data);
                var clampedCapacity = Mathf.Clamp(magCapacity, 0, int.MaxValue);
                _weaponAmmo[i] = clampedCapacity;
                _serverWeaponStates[i] = new ServerWeaponState {
                    ServerAmmo = clampedCapacity
                };
            }
        }

        public bool ValidateServerShot(
            int weaponIndex,
            ulong shotId,
            float now,
            float fireRateGraceSeconds,
            Func<int, WeaponData> getWeaponDataByIndex,
            Func<WeaponData, int> resolveWeaponCapacity,
            out string reason) {
            reason = null;
            var data = getWeaponDataByIndex(weaponIndex);
            if(data == null) {
                reason = "unknown weapon";
                return false;
            }

            var state = GetOrCreateServerState(weaponIndex, getWeaponDataByIndex, resolveWeaponCapacity);
            if(shotId == state.LastShotId) {
                var maxClaimsForShot = data.usePelletSpread ? Mathf.Max(1, data.pelletCount) : 1;
                if(state.AcceptedClaimsForLastShot >= maxClaimsForShot) {
                    reason = "too many hit claims for shot";
                    return false;
                }

                state.AcceptedClaimsForLastShot++;
                return true;
            }

            if(shotId < state.LastShotId) {
                reason = "shot id rewind";
                return false;
            }

            if(state.LastShotTime > 0f) {
                var minInterval = Mathf.Max(0.01f, data.fireRate - fireRateGraceSeconds);
                if(now - state.LastShotTime < minInterval) {
                    reason = "firing too fast";
                    return false;
                }
            }

            if(state.ServerAmmo <= 0) {
                reason = "no ammo";
                return false;
            }

            state.ServerAmmo = Mathf.Max(0, state.ServerAmmo - 1);
            state.LastShotTime = now;
            state.LastShotId = shotId;
            state.AcceptedClaimsForLastShot = 1;
            return true;
        }

        public void UpdateServerAmmo(
            int weaponIndex,
            int ammo,
            Func<int, WeaponData> getWeaponDataByIndex,
            Func<WeaponData, int> resolveWeaponCapacity) {
            var data = getWeaponDataByIndex(weaponIndex);
            if(data == null) return;

            var magCapacity = resolveWeaponCapacity(data);
            var clamped = Mathf.Clamp(ammo, 0, magCapacity);
            var state = GetOrCreateServerState(weaponIndex, getWeaponDataByIndex, resolveWeaponCapacity);
            state.ServerAmmo = clamped;
        }

        private ServerWeaponState GetOrCreateServerState(
            int weaponIndex,
            Func<int, WeaponData> getWeaponDataByIndex,
            Func<WeaponData, int> resolveWeaponCapacity) {
            if(_serverWeaponStates.TryGetValue(weaponIndex, out var state)) return state;

            state = new ServerWeaponState();
            var data = getWeaponDataByIndex(weaponIndex);
            state.ServerAmmo = data != null ? resolveWeaponCapacity(data) : 0;
            _serverWeaponStates[weaponIndex] = state;
            return state;
        }
    }
}
