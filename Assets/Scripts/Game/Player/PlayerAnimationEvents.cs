using Game.Weapons;
using UnityEngine;

namespace Game.Player {
    public class PlayerAnimationEvents : MonoBehaviour {
        [SerializeField] private PlayerController playerController;
        private WeaponManager _weaponManager;

        private void Awake() {
            playerController = GetComponentInParent<PlayerController>();
            _weaponManager = playerController.WeaponManager;
        }

        public void PlayWalkSound() => playerController.PlayWalkSound();
        public void PlayRunSound() => playerController.PlayRunSound();

        /// <summary>
        /// Called when the weapon pull out animation completes.
        /// Allows shooting and reloading again.
        /// </summary>
        public void WeaponPullOutCompleted() {
            // if(_weaponManager == null) {
            //     playerController = GetComponentInParent<PlayerController>();
            //     _weaponManager = playerController.WeaponManager;
            // }
            
            if(_weaponManager != null) {
                _weaponManager.HandlePullOutCompleted();
            }
        }

        /// <summary>
        /// Called from TP player animation event to show the weapon during pull out animation.
        /// </summary>
        public void ShowTpWeapon() {
            // if(_weaponManager == null) {
            //     playerController = GetComponentInParent<PlayerController>();
            //     _weaponManager = playerController.WeaponManager;
            // }
            
            if(_weaponManager != null) {
                _weaponManager.ShowTpWeapon();
            } else {
                Debug.LogWarning("[PlayerAnimationEvents] WeaponManager is null in ShowTpWeapon.");
            }
        }
    }
}