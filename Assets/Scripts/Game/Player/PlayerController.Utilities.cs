using Game.Weapons;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Public API and utility methods for PlayerController.
    /// Separated into partial class for better organization.
    /// </summary>
    public partial class PlayerController {
        #region Public API

        public void SetGameplayCameraActive(bool active) {
            if(fpCamera != null) {
                fpCamera.gameObject.SetActive(active);
            }

            if(worldCamera != null) {
                worldCamera.gameObject.SetActive(active);
            }
        }

        public void ResetVelocity() {
            if(movementController != null) {
                movementController.ResetVelocity();
            }
        }

        public void TryJump(float height = 2f) {
            if(movementController != null) {
                movementController.TryJump(height);
            }
        }

        public void PlayWalkSound() {
            if(!IsGrounded) return;

            if(movementController == null) return;

            if(movementController.CachedHorizontalSpeedSqr < 0.5f * 0.5f) {
                return;
            }

            if(IsOwner) {
                sfxRelay.RequestWorldSfx(SfxKey.Walk, attachToSelf: true, true);
            }
        }

        public void PlayRunSound() {
            if(!IsGrounded) return;

            if(movementController == null) return;

            if(movementController.CachedHorizontalSpeedSqr < 0.5f * 0.5f) {
                return;
            }

            if(IsOwner) {
                sfxRelay.RequestWorldSfx(SfxKey.Run, attachToSelf: true, true);
            }
        }
        
        public void PickupHopball() {
            if(hopballController != null) {
                hopballController.TryPickupHopball();
            } else {
                Debug.LogWarning("HopballController is null, cannot pick up hopball.");
            }
        }

        public bool IsHoldingHopball => hopballController != null && hopballController.IsHoldingHopball;

        public void DropHopball() {
            if(hopballController != null) {
                hopballController.DropHopball();
            }
        }

        #endregion

        #region Public Accessors

        public GameObject GetWorldModelRoot() => worldModelRoot;
        public MeshRenderer GetWorldWeapon() => worldWeapon;
        public WeaponManager GetWeaponManager() => weaponManager;

        public Vector3 GetHorizontalVelocity() =>
            movementController != null ? movementController.HorizontalVelocity : Vector3.zero;

        public float GetVerticalVelocity() => movementController != null ? movementController.VerticalVelocity : 0f;
        public float GetMaxSpeed() => movementController != null ? movementController.MaxSpeed : 5f;

        public float GetCachedHorizontalSpeedSqr() =>
            movementController != null ? movementController.CachedHorizontalSpeedSqr : 0f;

        public Transform GetTransform() => tr;

        // Convenience properties for accessing network variables from sub-controllers
        public int Tags => tagController != null ? tagController.tags.Value : 0;
        public int Tagged => tagController != null ? tagController.tagged.Value : 0;
        public int TimeTagged => tagController != null ? tagController.timeTagged.Value : 0;
        public bool IsTagged => tagController != null && tagController.isTagged.Value;
        public float AverageVelocity => statsController != null ? statsController.averageVelocity.Value : 0f;
        public int PingMs => statsController != null ? statsController.pingMs.Value : 0;

        #endregion

        #region Grapple Support Methods

        public void SetVelocity(Vector3 horizontalVelocity) {
            if(movementController != null) {
                movementController.SetVelocity(horizontalVelocity);
            }
        }

        public void AddVerticalVelocity(float verticalBoost) {
            if(movementController != null) {
                movementController.AddVerticalVelocity(verticalBoost);
            }
        }

        #endregion

        #region Podium Methods

        public void ForceRespawnForPodiumServer() {
            if(podiumController != null) {
                podiumController.ForceRespawnForPodiumServer();
            }
        }

        public void TeleportToPodiumFromServer(Vector3 position, Quaternion rotation) {
            if(podiumController != null) {
                podiumController.TeleportToPodiumFromServer(position, rotation);
            }
        }

        #endregion
    }
}

