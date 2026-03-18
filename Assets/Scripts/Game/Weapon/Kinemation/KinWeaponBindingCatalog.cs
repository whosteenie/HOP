using System;
using System.Collections.Generic;
using Game.Weapon.Core;
using UnityEngine;

namespace Game.Weapon.Kinemation {
    internal sealed class KinWeaponBindingCatalog {
        private readonly Dictionary<WeaponData, KinWeaponBinding> _lookup = new();
        private readonly List<WeaponData> _primaryWeaponOptions = new();
        private readonly List<WeaponData> _secondaryWeaponOptions = new();

        public IReadOnlyList<WeaponData> PrimaryWeaponOptions => _primaryWeaponOptions;
        public IReadOnlyList<WeaponData> SecondaryWeaponOptions => _secondaryWeaponOptions;
        public bool IsEmpty => _lookup.Count == 0 && _primaryWeaponOptions.Count == 0 && _secondaryWeaponOptions.Count == 0;

        public void Rebuild(
            IReadOnlyList<KinWeaponBinding> bindings,
            Func<WeaponData, int> resolveWeaponSlot,
            Action<string> logError) {
            _lookup.Clear();
            _primaryWeaponOptions.Clear();
            _secondaryWeaponOptions.Clear();

            if(bindings == null || bindings.Count == 0) return;

            var primarySeen = new HashSet<WeaponData>();
            var secondarySeen = new HashSet<WeaponData>();
            foreach(var binding in bindings) {
                if(binding == null || binding.WeaponData == null || binding.KinWeaponPrefab == null) continue;
                _lookup[binding.WeaponData] = binding;

                var slot = resolveWeaponSlot(binding.WeaponData);
                switch(slot) {
                    case < 0:
                        logError?.Invoke(
                            $"[WeaponManager] Invalid weapon slot on binding weapon '{binding.WeaponData.name}'. " +
                            "Expected Primary/Secondary slot assignment.");
                        continue;
                    case 0: {
                        if(primarySeen.Add(binding.WeaponData)) {
                            _primaryWeaponOptions.Add(binding.WeaponData);
                        }

                        break;
                    }
                    default: {
                        if(secondarySeen.Add(binding.WeaponData)) {
                            _secondaryWeaponOptions.Add(binding.WeaponData);
                        }

                        break;
                    }
                }
            }
        }

        public bool TryGetBinding(
            GameObject kinemationFpsPlayerPrefab,
            WeaponData weaponData,
            out KinWeaponBinding binding) {
            binding = null;
            if(kinemationFpsPlayerPrefab == null || weaponData == null) return false;

            return _lookup.TryGetValue(weaponData, out binding) &&
                   binding != null &&
                   binding.KinWeaponPrefab != null;
        }
    }
}
