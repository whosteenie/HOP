using Game.Weapons;
using UnityEngine;

namespace Game.Player {
    public class PlayerAnimationEvents : MonoBehaviour {
        public PlayerController playerController;
        public WeaponManager weaponManager;

        private void OnValidate() {
            if(playerController == null) {
                playerController = transform.parent.GetComponent<PlayerController>();
            }
            if(weaponManager == null && playerController != null) {
                weaponManager = playerController.WeaponManager;
            }
        }

        public void PlayWalkSound() => playerController.PlayWalkSound();
        public void PlayRunSound() => playerController.PlayRunSound();

        /// <summary>
        /// Called when the weapon pull out animation completes.
        /// Allows shooting and reloading again.
        /// </summary>
        public void WeaponPullOutCompleted() {
            weaponManager?.HandlePullOutCompleted();
        }

        /// <summary>
        /// Called from TP player animation event to show the weapon during pull out animation.
        /// </summary>
        public void ShowTpWeapon() {
            weaponManager?.ShowTpWeapon();
        }
    }
}