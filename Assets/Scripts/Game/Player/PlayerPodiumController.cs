using System.Collections;
using Network;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Handles podium-specific logic for post-match display.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerPodiumController : NetworkBehaviour {
        [Header("References")] [SerializeField]
        private PlayerController playerController;

        [SerializeField] private PlayerVisualController visualController;
        [SerializeField] private PlayerRagdoll playerRagdoll;
        [SerializeField] private CharacterController characterController;
        [SerializeField] private ClientNetworkTransform clientNetworkTransform;

        [Header("Podium Settings")] [SerializeField]
        private Transform rootBone;

        [SerializeField] private float podiumSnapDelay = 0.05f;

        private Animator _podiumAnimator;
        private SkinnedMeshRenderer _podiumSkinned;
        private bool _awaitingPodiumSnap;

        [SerializeField] private Animator podiumAnimator;

        private void Awake() {
            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(visualController == null) {
                visualController = GetComponent<PlayerVisualController>();
            }

            if(playerRagdoll == null) {
                playerRagdoll = GetComponent<PlayerRagdoll>();
            }

            if(characterController == null) {
                characterController = GetComponent<CharacterController>();
            }

            if(clientNetworkTransform == null) {
                clientNetworkTransform = GetComponent<ClientNetworkTransform>();
            }

            // Cache podium components
            if(podiumAnimator != null) {
                _podiumAnimator = podiumAnimator;
                // GetComponentInChildren is acceptable for child components (hierarchy-dependent)
                _podiumSkinned = GetComponentInChildren<SkinnedMeshRenderer>();
            } else if(playerController != null) {
                // Fallback: try to get from PlayerController
                var animator = playerController.GetComponent<Animator>();
                if(animator != null) {
                    _podiumAnimator = animator;
                    _podiumSkinned = GetComponentInChildren<SkinnedMeshRenderer>();
                }
            }

            if(rootBone == null && _podiumAnimator != null) {
                rootBone = _podiumAnimator.GetBoneTransform(HumanBodyBones.Hips);
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(visualController == null) {
                visualController = GetComponent<PlayerVisualController>();
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
            if(playerRagdoll != null) {
                playerRagdoll.DisableRagdoll();
            }

            ResetAnimatorState(_podiumAnimator);

            // Ensure world model root and weapon are active for podium
            if(playerController != null) {
                var worldModelRoot = playerController.GetWorldModelRoot();
                var worldWeapon = visualController != null ? visualController.GetWorldWeapon() : null;

                if(worldModelRoot != null && !worldModelRoot.activeSelf) {
                    worldModelRoot.SetActive(true);
                }

                if(worldWeapon != null && !worldWeapon.activeSelf) {
                    worldWeapon.SetActive(true);
                }

                // Enable renderers
                if(visualController != null) {
                    visualController.SetRenderersEnabled(true, true, UnityEngine.Rendering.ShadowCastingMode.On);
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
            if(characterController != null) {
                characterController.enabled = false;
            }

            if(clientNetworkTransform != null) {
                clientNetworkTransform.enabled = false;
            }

            transform.SetPositionAndRotation(pos, rot);
            if(clientNetworkTransform != null) {
                clientNetworkTransform.Teleport(pos, rot, Vector3.one);
            }

            yield return new WaitForFixedUpdate();

            if(characterController != null) {
                characterController.enabled = true;
            }

            if(clientNetworkTransform != null) {
                clientNetworkTransform.enabled = true;
            }

            if(_awaitingPodiumSnap) {
                yield return new WaitForSeconds(podiumSnapDelay);
                SnapBonesToRoot();
                _awaitingPodiumSnap = false;
            }
        }

        private void SnapBonesToRoot() {
            if(rootBone == null || _podiumAnimator == null) return;

            rootBone.position = transform.position;
            rootBone.rotation = transform.rotation;

            _podiumAnimator.enabled = false;
            _podiumAnimator.enabled = true;

            if(_podiumSkinned != null) {
                _podiumSkinned.enabled = false;
                _podiumSkinned.enabled = true;
            }
        }

        [Rpc(SendTo.Everyone)]
        public void SnapPodiumVisualsClientRpc() {
            if(_awaitingPodiumSnap) {
                SnapBonesToRoot();
                _awaitingPodiumSnap = false;
            }
        }

        private void ResetAnimatorState(Animator animator) {
            if(animator != null) {
                animator.Rebind();
                animator.Update(0f);
            }
        }
    }
}