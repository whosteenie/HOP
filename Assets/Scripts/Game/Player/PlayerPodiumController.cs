using System.Collections;
using Network;
using Network.Components;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Handles podium-specific logic for post-match display.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerPodiumController : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private PlayerVisualController _visualController;
        private PlayerRagdoll _playerRagdoll;
        private CharacterController _characterController;
        private ClientNetworkTransform _clientNetworkTransform;

        [Header("Podium Settings")]
        [SerializeField] private Transform rootBone;

        [SerializeField] private float podiumSnapDelay = 0.05f;

        private Animator _podiumAnimator;
        private SkinnedMeshRenderer _podiumSkinned;
        private bool _awaitingPodiumSnap;

        [SerializeField] private Animator podiumAnimator;

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[PlayerPodiumController] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_visualController == null) _visualController = playerController.VisualController;
            if(_playerRagdoll == null) _playerRagdoll = playerController.PlayerRagdoll;
            if(_characterController == null) _characterController = playerController.CharacterController;
            if(_clientNetworkTransform == null) _clientNetworkTransform = playerController.ClientNetworkTransform;

            // Cache podium components
            if(podiumAnimator != null) {
                _podiumAnimator = podiumAnimator;
                _podiumSkinned = GetComponentInChildren<SkinnedMeshRenderer>();
            } else {
                var animator = playerController.PlayerAnimator;
                if(animator != null) {
                    _podiumAnimator = animator;
                    _podiumSkinned = GetComponentInChildren<SkinnedMeshRenderer>();
                }
            }

            if(rootBone == null && _podiumAnimator != null) {
                rootBone = _podiumAnimator.GetBoneTransform(HumanBodyBones.Hips);
            }
        }

        public void ForceRespawnForPodiumServer() {
            if(!IsServer) return;

            // Reset health via PlayerController
            if(playerController != null) {
                playerController.ResetHealthAndRegenerationState();
            }

            ForceRespawnForPodiumClientRpc();
        }

        [Rpc(SendTo.Everyone)]
        private void ForceRespawnForPodiumClientRpc() {
            if(_playerRagdoll != null) {
                _playerRagdoll.DisableRagdoll();
            }

            ResetAnimatorState(_podiumAnimator);

            // Ensure world model root and weapon are active for podium
            if(playerController != null) {
                var worldModelRoot = playerController.PlayerModelRoot;
                GameObject worldWeapon = null;
                if(_visualController != null) {
                    worldWeapon = _visualController.GetWorldWeapon();
                }

                if(worldModelRoot != null && !worldModelRoot.activeSelf) {
                    worldModelRoot.SetActive(true);
                }

                if(worldWeapon != null && !worldWeapon.activeSelf) {
                    worldWeapon.SetActive(true);
                }

                // Enable renderers
                if(_visualController != null) {
                    _visualController.SetRenderersEnabled(true);
                }
                if(playerController.PlayerShadow != null) {
                    playerController.PlayerShadow.ApplyPodiumShadowState();
                }
            }

            if(_podiumSkinned != null) {
                _podiumSkinned.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            // Note: Main player object should be set to Default layer in inspector
            // Ragdoll components are set to Enemy layer in PlayerRagdoll.OnNetworkSpawn()

            _awaitingPodiumSnap = true;
        }

        public void TeleportToPodiumFromServer(Vector3 position, Quaternion rotation) {
            if(!IsServer) return;
            TeleportToPodiumOwnerClientRpc(position, rotation);
        }

        [Rpc(SendTo.Owner)]
        private void TeleportToPodiumOwnerClientRpc(Vector3 position, Quaternion rotation) {
            StartCoroutine(TeleportAndSnapToPodium(position, rotation));
        }

        private IEnumerator TeleportAndSnapToPodium(Vector3 pos, Quaternion rot) {
            if(_characterController != null) {
                _characterController.enabled = false;
            }

            if(_clientNetworkTransform != null) {
                _clientNetworkTransform.enabled = false;
            }

            transform.SetPositionAndRotation(pos, rot);
            if(_clientNetworkTransform != null) {
                _clientNetworkTransform.Teleport(pos, rot, Vector3.one);
            }

            yield return new WaitForFixedUpdate();

            if(_characterController != null) {
                _characterController.enabled = true;
            }

            if(_clientNetworkTransform != null) {
                _clientNetworkTransform.enabled = true;
            }

            if(!_awaitingPodiumSnap) yield break;
            yield return new WaitForSeconds(podiumSnapDelay);
            SnapBonesToRoot();
            _awaitingPodiumSnap = false;
        }

        private void SnapBonesToRoot() {
            if(rootBone == null || _podiumAnimator == null) return;

            rootBone.position = playerController.Position;
            rootBone.rotation = playerController.Rotation;

            _podiumAnimator.enabled = false;
            _podiumAnimator.enabled = true;

            if(_podiumSkinned == null) return;
            _podiumSkinned.enabled = false;
            _podiumSkinned.enabled = true;
        }

        [Rpc(SendTo.Everyone)]
        public void SnapPodiumVisualsClientRpc() {
            if(!_awaitingPodiumSnap) return;
            SnapBonesToRoot();
            _awaitingPodiumSnap = false;
        }

        private static void ResetAnimatorState(Animator animator) {
            if(animator == null) return;
            animator.Rebind();
            animator.Update(0f);
        }
    }
}