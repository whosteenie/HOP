using Network.Singletons;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Network RPC methods for PlayerController.
    /// Separated into partial class for better organization.
    /// </summary>
    public partial class PlayerController {
        #region Network RPCs

        [Rpc(SendTo.Everyone)]
        public void SetWorldModelVisibleRpc(bool visible) {
            visualController?.SetWorldModelVisibleRpc(visible);
        }

        [Rpc(SendTo.Everyone)]
        public void ResetVelocityRpc() {
            movementController?.ResetVelocity();
        }

        [Rpc(SendTo.Everyone)]
        public void PlayHitEffectsClientRpc(Vector3 hitPoint, float amount) {
            if(IsOwner) {
                if(SoundFXManager.Instance != null) {
                    SoundFXManager.Instance.PlayUISound(SfxKey.Hurt);
                }
                impulseSource.GenerateImpulse();

                if(DamageVignetteUIManager.Instance && fpCamera) {
                    var intensity = Mathf.Clamp01(amount / 50f);
                    DamageVignetteUIManager.Instance.ShowHitFromWorldPoint(hitPoint, fpCamera.transform, intensity);
                }
            }

            animationController?.PlayDamageAnimation();
        }

        [Rpc(SendTo.Everyone)]
        public void SnapPodiumVisualsClientRpc() {
            podiumController?.SnapPodiumVisualsClientRpc();
        }

        #endregion
    }
}

