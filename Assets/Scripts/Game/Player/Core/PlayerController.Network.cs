using Game.UI;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Network RPC methods for PlayerController.
    /// Separated into partial class for better organization.
    /// </summary>
    public partial class PlayerController {
        #region Network RPCs

        /// <summary>
        /// Sets the visibility of the world model across all clients.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        public void SetWorldModelVisibleRpc(bool visible) {
            if(visualController != null)
                visualController.SetWorldModelVisible(visible);
        }

        /// <summary>
        /// Resets the player's velocity across all clients.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        public void ResetVelocityRpc() {
            if(movementController != null)
                movementController.ResetVelocity();
        }

        /// <summary>
        /// Plays hit effects on the affected client and damage animations on all clients.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        public void PlayHitEffectsClientRpc(Vector3 hitPoint, float amount) {
            if(IsOwner) {
                if(Audio2.AudioService.Instance != null) {
                    Audio2.AudioService.Instance.Play("ui.hit.hurt", Vector3.zero);
                }
                
                impulseSource.GenerateImpulse();

                if(DamageVignetteUIManager.Instance && fpCamera) {
                    var intensity = Mathf.Clamp01(amount / 50f);
                    DamageVignetteUIManager.Instance.ShowHitFromWorldPoint(hitPoint, fpCamera.transform, intensity);
                }
            }

            if(animationController != null)
                animationController.PlayDamageAnimation();
        }

        /// <summary>
        /// Snaps the podium visuals across all clients.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        public void SnapPodiumVisualsClientRpc() {
            if(podiumController != null)
                podiumController.SnapPodiumVisualsClientRpc();
        }

        #endregion
    }
}

