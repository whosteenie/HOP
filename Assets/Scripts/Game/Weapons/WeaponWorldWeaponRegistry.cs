using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Weapons {
    internal sealed class WeaponWorldWeaponRegistry {
        private readonly Dictionary<WeaponData, GameObject> _worldWeaponByData = new();

        public bool Rebuild(Transform worldWeaponSocket, Action<string> logError) {
            _worldWeaponByData.Clear();
            if(worldWeaponSocket == null) return false;

            var isValid = true;
            foreach(Transform child in worldWeaponSocket) {
                if(child == null) continue;

                var binding = child.GetComponentInChildren<WorldWeaponBinding>(true);
                if(binding == null || binding.WeaponData == null) continue;

                if(_worldWeaponByData.ContainsKey(binding.WeaponData)) {
                    logError?.Invoke(
                        $"[WeaponManager] Duplicate WorldWeaponBinding for '{binding.WeaponData.weaponName}' on '{child.name}'.");
                    isValid = false;
                }

                _worldWeaponByData[binding.WeaponData] = child.gameObject;
            }

            return isValid;
        }

        public GameObject Resolve(WeaponData weaponData) {
            if(weaponData == null) return null;
            return _worldWeaponByData.TryGetValue(weaponData, out var worldWeapon) && worldWeapon != null
                ? worldWeapon
                : null;
        }
    }
}
