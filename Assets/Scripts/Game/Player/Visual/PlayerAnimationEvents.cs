using System;
using Diagnostics;
using Game.Player.Contracts;
using Game.Weapon.Manager;
using UnityEngine;
// ReSharper disable UnusedMember.Global

namespace Game.Player.Visual {
    public class PlayerAnimationEvents : MonoBehaviour {
        public event Action OnPutAwayComplete;

        [HideInInspector, SerializeField] private MonoBehaviour playerContextSource;
        [SerializeField] private bool debugAnimationEvents;
        private IPlayerVisualContext _playerContext;
        private WeaponManager _weaponManager;

        private void Awake() {
            if(playerContextSource == null) {
                foreach(var behaviour in GetComponentsInParent<MonoBehaviour>(true)) {
                    if(behaviour == null) continue;
                    // ReSharper disable once UseNegatedPatternMatching
                    var candidate = behaviour as IPlayerVisualContext;
                    if(candidate == null) continue;
                    playerContextSource = behaviour;
                    break;
                }

                if(playerContextSource == null) {
                    foreach(var behaviour in transform.root.GetComponentsInChildren<MonoBehaviour>(true)) {
                        if(behaviour == null) continue;
                        // ReSharper disable once UseNegatedPatternMatching
                        var candidate = behaviour as IPlayerVisualContext;
                        if(candidate == null) continue;
                        playerContextSource = behaviour;
                        break;
                    }
                }
            }

            if(PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext) &&
               _playerContext != null) {
                _weaponManager = _playerContext.WeaponManager;
                return;
            }

            _weaponManager = GetComponentInParent<WeaponManager>(true);
            if(debugAnimationEvents && Debug.isDebugBuild) {
                DevLog.Log($"[PlayerAnimationEvents] No IPlayerVisualContext found on {name}; using local fallbacks only.");
            }
        }

        /// <summary>
        /// Animation event to play the walk sound.
        /// </summary>
        public void PlayWalkSound() {
            if(debugAnimationEvents && Debug.isDebugBuild) {
                DevLog.Log($"[PlayerAnimationEvents] PlayWalkSound event on {name}");
            }
            _playerContext?.PlayWalkSound();
        }

        /// <summary>
        /// Animation event to play the run sound.
        /// </summary>
        public void PlayRunSound() {
            if(debugAnimationEvents && Debug.isDebugBuild) {
                DevLog.Log($"[PlayerAnimationEvents] PlayRunSound event on {name}");
            }
            _playerContext?.PlayRunSound();
        }

        /// <summary>
        /// Called when the weapon pull out animation completes.
        /// Allows shooting and reloading again.
        /// </summary>
        private void WeaponPullOutCompleted() {
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
                DevLog.LogWarning("[PlayerAnimationEvents] WeaponManager is null in ShowTpWeapon.");
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

