using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Weapons {
    internal sealed class WeaponWorldWeaponRegistry {
        private readonly Dictionary<WeaponData, GameObject> _worldWeaponByData = new();
        private readonly Dictionary<WeaponData, GameObject> _holsterWeaponByData = new();

        public bool Rebuild(Transform worldWeaponSocket, Action<string> logError) {
            _worldWeaponByData.Clear();
            _holsterWeaponByData.Clear();
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

            var allBindings = worldWeaponSocket.root != null
                ? worldWeaponSocket.root.GetComponentsInChildren<WorldWeaponBinding>(true)
                : Array.Empty<WorldWeaponBinding>();

            foreach(var binding in allBindings) {
                if(binding == null || binding.WeaponData == null) continue;
                if(binding.transform.IsChildOf(worldWeaponSocket)) continue;

                var holsterRoot = binding.gameObject;
                if(holsterRoot == null) continue;

                if(_holsterWeaponByData.TryGetValue(binding.WeaponData, out var existingHolster) &&
                   existingHolster != holsterRoot) {
                    logError?.Invoke(
                        $"[WeaponManager] Duplicate holster WorldWeaponBinding for '{binding.WeaponData.weaponName}' on '{holsterRoot.name}'.");
                    isValid = false;
                    continue;
                }

                _holsterWeaponByData[binding.WeaponData] = holsterRoot;
            }

            return isValid;
        }

        public GameObject Resolve(WeaponData weaponData) {
            if(weaponData == null) return null;
            return _worldWeaponByData.TryGetValue(weaponData, out var worldWeapon) && worldWeapon != null
                ? worldWeapon
                : null;
        }

        public GameObject ResolveHolster(WeaponData weaponData) {
            if(weaponData == null) return null;
            return _holsterWeaponByData.TryGetValue(weaponData, out var holsterWeapon) && holsterWeapon != null
                ? holsterWeapon
                : null;
        }
    }
}
