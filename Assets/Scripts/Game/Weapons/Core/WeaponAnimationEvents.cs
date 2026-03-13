using Game.Player.Core;
using UnityEngine;

namespace Game.Weapons {
    /// <summary>
    /// Component attached to FP weapon GameObjects to handle animation events.
    /// Allows animation events to communicate with the weapon system.
    /// </summary>
    public class WeaponAnimationEvents : MonoBehaviour {
        private WeaponManager ResolveWeaponManager() {
            // Find PlayerController via hierarchy (FP weapon is child of camera, camera is child of player)
            var playerController = GetComponentInParent<PlayerController>();
            if(playerController != null && playerController.WeaponManager != null) {
                return playerController.WeaponManager;
            }

            // Fallback: try to find via root
            var root = transform.root;
            playerController = root.GetComponent<PlayerController>();
            if(playerController != null && playerController.WeaponManager != null) {
                return playerController.WeaponManager;
            }

            Debug.LogWarning(
                "[WeaponAnimationEvents] Could not find PlayerController or WeaponManager to handle equip completion!");
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
