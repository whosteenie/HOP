using System.Collections;
using Diagnostics;
using Events;
using Game.Match;
using Game.Player.Combat;
using Game.Player.Contracts;
using Game.Player.Visual;
using Network.Components;
using Network.Core;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Core {
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
        private Coroutine _pendingPodiumVisualRestore;

        [SerializeField] private Animator podiumAnimator;

        private void Awake() {
            ValidateComponents();
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            SubscribeToPostMatchEvents();
        }

        public override void OnNetworkDespawn() {
            UnsubscribePostMatchEvents();
            base.OnNetworkDespawn();
        }

        public override void OnDestroy() {
            UnsubscribePostMatchEvents();
            base.OnDestroy();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                DevLog.LogError("[PlayerPodiumController] PlayerController not found!");
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

        private void SubscribeToPostMatchEvents() {
            EventBus.Unsubscribe<PostMatchPodiumPrepareRequestedEvent>(OnPodiumPrepareRequested);
            EventBus.Unsubscribe<PostMatchResetVelocityRequestedEvent>(OnResetVelocityRequested);
            EventBus.Unsubscribe<PostMatchTeleportRequestedEvent>(OnPostMatchTeleportRequested);
            EventBus.Unsubscribe<PostMatchSnapVisualsRequestedEvent>(OnPostMatchSnapVisualsRequested);
            EventBus.Unsubscribe<PostMatchWorldModelVisibilityEvent>(OnPostMatchWorldModelVisibilityRequested);
            EventBus.Unsubscribe<PostMatchGameplayCameraEvent>(OnPostMatchGameplayCameraStateRequested);
            EventBus.Unsubscribe<PostMatchControlLockRequestedEvent>(OnPostMatchControlLockRequested);
            EventBus.Subscribe<PostMatchPodiumPrepareRequestedEvent>(OnPodiumPrepareRequested);
            EventBus.Subscribe<PostMatchResetVelocityRequestedEvent>(OnResetVelocityRequested);
            EventBus.Subscribe<PostMatchTeleportRequestedEvent>(OnPostMatchTeleportRequested);
            EventBus.Subscribe<PostMatchSnapVisualsRequestedEvent>(OnPostMatchSnapVisualsRequested);
            EventBus.Subscribe<PostMatchWorldModelVisibilityEvent>(OnPostMatchWorldModelVisibilityRequested);
            EventBus.Subscribe<PostMatchGameplayCameraEvent>(OnPostMatchGameplayCameraStateRequested);
            EventBus.Subscribe<PostMatchControlLockRequestedEvent>(OnPostMatchControlLockRequested);
        }

        private void UnsubscribePostMatchEvents() {
            EventBus.Unsubscribe<PostMatchPodiumPrepareRequestedEvent>(OnPodiumPrepareRequested);
            EventBus.Unsubscribe<PostMatchResetVelocityRequestedEvent>(OnResetVelocityRequested);
            EventBus.Unsubscribe<PostMatchTeleportRequestedEvent>(OnPostMatchTeleportRequested);
            EventBus.Unsubscribe<PostMatchSnapVisualsRequestedEvent>(OnPostMatchSnapVisualsRequested);
            EventBus.Unsubscribe<PostMatchWorldModelVisibilityEvent>(OnPostMatchWorldModelVisibilityRequested);
            EventBus.Unsubscribe<PostMatchGameplayCameraEvent>(OnPostMatchGameplayCameraStateRequested);
            EventBus.Unsubscribe<PostMatchControlLockRequestedEvent>(OnPostMatchControlLockRequested);
        }

        private bool IsPostMatchTarget(ulong playerClientId) =>
            playerController != null && playerClientId == playerController.OwnerClientId;

        private void OnPodiumPrepareRequested(PostMatchPodiumPrepareRequestedEvent evt) {
            if(evt == null || !IsPostMatchTarget(evt.PlayerClientId)) return;
            ForceRespawnForPodiumServer();
        }

        private void OnResetVelocityRequested(PostMatchResetVelocityRequestedEvent evt) {
            if(evt == null || !IsPostMatchTarget(evt.PlayerClientId)) return;
            ResetVelocityRpc();
        }

        private void OnPostMatchTeleportRequested(PostMatchTeleportRequestedEvent evt) {
            if(evt == null || !IsPostMatchTarget(evt.PlayerClientId)) return;
            TeleportToPodiumFromServer(evt.Position, evt.Rotation);
        }

        private void OnPostMatchSnapVisualsRequested(PostMatchSnapVisualsRequestedEvent evt) {
            if(evt == null || !IsPostMatchTarget(evt.PlayerClientId)) return;
            SnapPodiumVisualsClientRpc();
        }

        private void OnPostMatchWorldModelVisibilityRequested(PostMatchWorldModelVisibilityEvent evt) {
            if(evt == null || !IsPostMatchTarget(evt.PlayerClientId)) return;
            SetWorldModelVisibleRpc(evt.Visible);
        }

        private void OnPostMatchGameplayCameraStateRequested(PostMatchGameplayCameraEvent evt) {
            if(evt == null || !IsPostMatchTarget(evt.PlayerClientId)) return;
            SetGameplayCameraActive(evt.Active);
        }

        private void OnPostMatchControlLockRequested(PostMatchControlLockRequestedEvent evt) {
            if(evt == null || !IsPostMatchTarget(evt.PlayerClientId)) return;
            SetPostMatchControlLock(evt.Locked, evt.LockLook, evt.ResetVelocity);
        }

        /// <summary>
        /// Resets the player state on the server to prepare for the podium display.
        /// </summary>
        private void ForceRespawnForPodiumServer() {
            if(!NetworkAuthority.HasGlobalAuthority(this)) return;

            // Reset health via PlayerController
            if(playerController != null) {
                playerController.ResetHealthAndRegenerationState();
            }

            ForcePodiumRespawnClientRpc();
        }

        [Rpc(SendTo.Everyone)]
        private void ForcePodiumRespawnClientRpc() {
            if(_playerRagdoll != null) {
                _playerRagdoll.DisableRagdoll();
            }

            ResetAnimatorState(_podiumAnimator);

            // Note: Main player object should be set to Default layer in inspector
            // Ragdoll components are set to Enemy layer in PlayerRagdoll.OnNetworkSpawn()

            if(_pendingPodiumVisualRestore != null) {
                StopCoroutine(_pendingPodiumVisualRestore);
            }
            _pendingPodiumVisualRestore = StartCoroutine(RestorePodiumVisualsWhenBlackoutReady());
            _awaitingPodiumSnap = true;
        }

        private IEnumerator RestorePodiumVisualsWhenBlackoutReady() {
            while(!PostMatchManager.IsPodiumBlackoutActiveLocal) {
                yield return null;
            }

            ApplyPodiumVisualRestore();
            _pendingPodiumVisualRestore = null;
        }

        private void ApplyPodiumVisualRestore() {
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
        }

        private void SetPostMatchControlLock(bool locked, bool lockLook = true, bool resetVelocity = true) {
            if(playerController == null || !playerController.IsOwner) return;

            if(locked) {
                playerController.moveInput = Vector2.zero;
                playerController.lookInput = Vector2.zero;
                playerController.sprintInput = false;
                playerController.crouchInput = false;
                if(resetVelocity && playerController.MovementController != null) {
                    playerController.MovementController.ResetVelocity();
                }
            }

            playerController.LockLook = locked && lockLook;
        }

        private void SetGameplayCameraActive(bool active) {
            if(playerController == null) return;

            if(playerController.FpCamera != null) {
                playerController.FpCamera.enabled = active;
            }

            if(playerController.DeathCamera != null) {
                playerController.DeathCamera.enabled = active;
                if(!active && playerController.DeathCamera.gameObject.activeSelf) {
                    playerController.DeathCamera.gameObject.SetActive(false);
                }
            }

            IPlayerVisualContext visualContext = playerController;
            if(visualContext != null) {
                visualContext.SetWeaponCameraEnabled(active);
            } else if(playerController.WeaponCamera != null) {
                playerController.WeaponCamera.enabled = active;
            }
        }

        /// <summary>
        /// Teleports the player to the podium position from the server.
        /// </summary>
        private void TeleportToPodiumFromServer(Vector3 position, Quaternion rotation) {
            if(!NetworkAuthority.HasGlobalAuthority(this)) return;
            TeleportToPodiumClientRpc(position, rotation);
        }

        [Rpc(SendTo.Owner)]
        private void TeleportToPodiumClientRpc(Vector3 position, Quaternion rotation) {
            StartCoroutine(TeleportAndSnapToPodium(position, rotation));
        }

        [Rpc(SendTo.Everyone)]
        private void ResetVelocityRpc() {
            if(playerController != null && playerController.MovementController != null) {
                playerController.MovementController.ResetVelocity();
            }
        }

        [Rpc(SendTo.Everyone)]
        private void SetWorldModelVisibleRpc(bool visible) {
            if(_visualController != null) {
                _visualController.SetWorldModelVisible(visible);
            }
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
            var podAnimator = _podiumAnimator;
            if(rootBone == null || podAnimator == null) return;

            rootBone.position = playerController.Position;
            rootBone.rotation = playerController.Rotation;

            //noinspection Unity.InefficientPropertyAccess
            podAnimator.enabled = false;
            _podiumAnimator.enabled = true;

            var podiumSkinned = _podiumSkinned;
            if(podiumSkinned == null) return;
            //noinspection Unity.InefficientPropertyAccess
            podiumSkinned.enabled = false;
            _podiumSkinned.enabled = true;

            if(playerController != null && playerController.NetworkObject != null) {
                EventBus.Publish(new PodiumVisualsSnappedEvent(playerController.NetworkObjectId));
            }
        }

        /// <summary>
        /// Snaps the podium visuals on all clients.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        private void SnapPodiumVisualsClientRpc() {
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
