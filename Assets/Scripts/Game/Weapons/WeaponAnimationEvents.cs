using Game.Player;
using UnityEngine;

namespace Game.Weapons {
    /// <summary>
    /// Component attached to FP weapon GameObjects to handle animation events.
    /// Allows animation events to communicate with the weapon system.
    /// </summary>
    public class WeaponAnimationEvents : MonoBehaviour {
        /// <summary>
        /// Called from FP weapon animation event when pull out animation completes.
        /// Finds the WeaponManager and releases control by clearing IsPullingOut flag.
        /// </summary>
        public void OnPullOutCompleted() {
            // Find PlayerController via hierarchy (FP weapon is child of camera, camera is child of player)
            var playerController = GetComponentInParent<PlayerController>();
            if(playerController?.WeaponManager != null) {
                playerController.WeaponManager.HandlePullOutCompleted();
            } else {
                // Fallback: try to find via root
                var root = transform.root;
                playerController = root.GetComponent<PlayerController>();
                if(playerController?.WeaponManager != null) {
                    playerController.WeaponManager.HandlePullOutCompleted();
                } else {
                    Debug.LogWarning("[WeaponAnimationEvents] Could not find PlayerController or WeaponManager to handle pull out completion!");
                }
            }
        }
    }
}

