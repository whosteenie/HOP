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
        }

        public void PlayWalkSound() => playerController.PlayWalkSound();
        public void PlayRunSound() => playerController.PlayRunSound();

        public void WeaponSheatheCompleted() {
            weaponManager.HandleSheatheCompleted();
        }

        public void WeaponUnsheatheShowFpModel() {
            weaponManager.HandleUnsheatheShowModel();
        }

        public void WeaponUnsheatheCompleted() {
            weaponManager.HandleUnsheatheCompleted();
        }
    }
}