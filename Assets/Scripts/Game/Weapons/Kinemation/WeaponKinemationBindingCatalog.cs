using System;
using System.Collections.Generic;
using Game.Weapons.Manager;
using UnityEngine;

namespace Game.Weapons {
    internal sealed class WeaponKinemationBindingCatalog {
        private readonly Dictionary<WeaponData, WeaponManager.KinemationWeaponBinding> _lookup = new();
        private readonly List<WeaponData> _primaryWeaponOptions = new();
        private readonly List<WeaponData> _secondaryWeaponOptions = new();

        public IReadOnlyList<WeaponData> PrimaryWeaponOptions => _primaryWeaponOptions;
        public IReadOnlyList<WeaponData> SecondaryWeaponOptions => _secondaryWeaponOptions;
        public bool IsEmpty => _lookup.Count == 0 && _primaryWeaponOptions.Count == 0 && _secondaryWeaponOptions.Count == 0;

        public void Rebuild(
            IReadOnlyList<WeaponManager.KinemationWeaponBinding> bindings,
            Func<WeaponData, int> resolveWeaponSlot,
            Action<string> logError) {
            _lookup.Clear();
            _primaryWeaponOptions.Clear();
            _secondaryWeaponOptions.Clear();

            if(bindings == null || bindings.Count == 0) return;

            var primarySeen = new HashSet<WeaponData>();
            var secondarySeen = new HashSet<WeaponData>();
            foreach(var binding in bindings) {
                if(binding == null || binding.weaponData == null || binding.kinemationWeaponPrefab == null) continue;
                _lookup[binding.weaponData] = binding;

                var slot = resolveWeaponSlot(binding.weaponData);
                switch(slot) {
                    case < 0:
                        logError?.Invoke(
                            $"[WeaponManager] Invalid weapon slot on binding weapon '{binding.weaponData.name}'. " +
                            "Expected Primary/Secondary slot assignment.");
                        continue;
                    case 0: {
                        if(primarySeen.Add(binding.weaponData)) {
                            _primaryWeaponOptions.Add(binding.weaponData);
                        }

                        break;
                    }
                    default: {
                        if(secondarySeen.Add(binding.weaponData)) {
                            _secondaryWeaponOptions.Add(binding.weaponData);
                        }

                        break;
                    }
                }
            }
        }

        public bool TryGetBinding(
            GameObject kinemationFpsPlayerPrefab,
            WeaponData weaponData,
            out WeaponManager.KinemationWeaponBinding binding) {
            binding = null;
            if(kinemationFpsPlayerPrefab == null || weaponData == null) return false;

            return _lookup.TryGetValue(weaponData, out binding) &&
                   binding != null &&
                   binding.kinemationWeaponPrefab != null;
        }
    }
}
