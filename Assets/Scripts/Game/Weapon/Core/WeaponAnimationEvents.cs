using Diagnostics;
using Game.Weapon.Manager;
using UnityEngine;
// ReSharper disable UnusedMember.Global

namespace Game.Weapon.Core {
    /// <summary>
    /// Component attached to FP weapon GameObjects to handle animation events.
    /// Allows animation events to communicate with the weapon system.
    /// </summary>
    public class WeaponAnimationEvents : MonoBehaviour {
        private WeaponManager ResolveWeaponManager() {
            var weaponManager = GetComponentInParent<WeaponManager>();
            if(weaponManager != null) {
                return weaponManager;
            }

            DevLog.LogWarning(
                "[WeaponAnimationEvents] Could not find WeaponManager to handle equip completion!");
            return null;
        }

        /// <summary>
        /// Called from FP weapon animation event when equip completes.
        /// Releases IsPullingOut so fire/reload can resume.
        /// </summary>
        private void EquipComplete() {
            var weaponManager = ResolveWeaponManager();
            if(weaponManager != null) weaponManager.HandlePullOutCompleted();
        }

        // Backwards-compatible aliases for existing animation events.
        public void OnEquipComplete() => EquipComplete();
        public void OnPullOutCompleted() => EquipComplete();
    }
}
