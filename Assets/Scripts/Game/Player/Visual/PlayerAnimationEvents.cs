using System;
using Game.Player.Core;
using Game.Weapon.Manager;
using UnityEngine;

namespace Game.Player.Visual {
    public class PlayerAnimationEvents : MonoBehaviour {
        public event Action OnPutAwayComplete;

        [SerializeField] private PlayerController playerController;
        [SerializeField] private bool debugAnimationEvents;
        private WeaponManager _weaponManager;

        private void Awake() {
            // Preserve prefab/inspector wiring. Only attempt to auto-find if not assigned.
            if(playerController == null) {
                playerController = GetComponentInParent<PlayerController>();
                if(playerController == null) {
                    playerController = transform.root.GetComponent<PlayerController>();
                }
                if(playerController == null) {
                    playerController = transform.root.GetComponentInChildren<PlayerController>();
                }
            }

            if(playerController != null) {
                _weaponManager = playerController.WeaponManager;
            }
        }

        /// <summary>
        /// Animation event to play the walk sound.
        /// </summary>
        public void PlayWalkSound() {
            if(debugAnimationEvents && Debug.isDebugBuild) {
                Debug.Log($"[PlayerAnimationEvents] PlayWalkSound event on {name}");
            }
            if(playerController == null) return;
            playerController.PlayWalkSound();
        }

        /// <summary>
        /// Animation event to play the run sound.
        /// </summary>
        public void PlayRunSound() {
            if(debugAnimationEvents && Debug.isDebugBuild) {
                Debug.Log($"[PlayerAnimationEvents] PlayRunSound event on {name}");
            }
            if(playerController == null) return;
            playerController.PlayRunSound();
        }

        /// <summary>
        /// Called when the weapon pull out animation completes.
        /// Allows shooting and reloading again.
        /// </summary>
        public void WeaponPullOutCompleted() {
            if(_weaponManager != null) {
                _weaponManager.HandleThirdPersonPullOutCompleted();
            }
        }

        // Animation Event hook for equip clips that use EquipComplete naming.
        public void EquipComplete() => WeaponPullOutCompleted();

        /// <summary>
        /// Called from TP player animation event to show the weapon during pull out animation.
        /// </summary>
        public void ShowTpWeapon() {
            if(_weaponManager != null) {
                _weaponManager.ShowTpWeapon();
            } else {
                Debug.LogWarning("[PlayerAnimationEvents] WeaponManager is null in ShowTpWeapon.");
            }
        }

        /// <summary>
        /// Called from animation event when PutAway animation completes.
        /// If this is on a hopball arm, destroys the arm GameObject.
        /// Otherwise, invokes the event for other systems (e.g., weapon put away).
        /// </summary>
        public void PutAwayComplete() {
            
            // Check if this is a hopball arm by checking if the GameObject name contains "HopballArm"
            // This is more specific than checking for BobHolder parent (which weapons also have)
            var isHopballArm = gameObject.name.Contains("HopballArm", StringComparison.OrdinalIgnoreCase);
            
            if(isHopballArm) {
                // This is a hopball arm - destroy it directly
                gameObject.SetActive(false);
                GameObject playerArm;
                (playerArm = gameObject).transform.SetParent(null);
                Destroy(playerArm);
            } else {
                // Not a hopball arm - invoke event for other systems
                OnPutAwayComplete?.Invoke();
            }
        }
    }
}
