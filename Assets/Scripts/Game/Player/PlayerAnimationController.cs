using Network.Rpc;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Handles all animation state management for the player.
    /// </summary>
    [RequireComponent(typeof(PlayerController))]
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerAnimationController : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private Animator _playerAnimator;
        private NetworkSfxRelay _sfxRelay;
        private Transform _playerTransform;

        // Animation parameter hashes
        private static readonly int MoveXHash = Animator.StringToHash("moveX");
        private static readonly int MoveYHash = Animator.StringToHash("moveY");
        private static readonly int LookXHash = Animator.StringToHash("lookX");
        private static readonly int IsSprintingHash = Animator.StringToHash("IsSprinting");
        private static readonly int IsCrouchingHash = Animator.StringToHash("IsCrouching");
        private static readonly int JumpTriggerHash = Animator.StringToHash("JumpTrigger");
        private static readonly int LandTriggerHash = Animator.StringToHash("LandTrigger");
        private static readonly int DamageTriggerHash = Animator.StringToHash("DamageTrigger");
        private static readonly int IsJumpingHash = Animator.StringToHash("IsJumping");
        private static readonly int IsFallingHash = Animator.StringToHash("IsFalling");
        private static readonly int IsGroundedHash = Animator.StringToHash("IsGrounded");

        // Animation state tracking
        private bool _wasGrounded;
        private float _fallStartHeight;
        private float _lastSpawnTime;
        private const float LandingSoundCooldown = 0.5f; // Block landing sounds for 0.5s after spawn/respawn

        // Constants
        private const float WalkSpeed = 5f;

        private void Awake() {
            playerController ??= GetComponent<PlayerController>();
            _playerAnimator ??= playerController.PlayerAnimator;
            _sfxRelay ??= playerController.SfxRelay;
            _playerTransform ??= playerController.PlayerTransform;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Component reference should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            // Network-dependent initialization
            _lastSpawnTime = Time.time;
        }

        /// <summary>
        /// Updates the animator with current movement and state values.
        /// Should be called every frame from PlayerController.Update().
        /// </summary>
        public void UpdateAnimator(Vector3 horizontalVelocity, float maxSpeed, float cachedHorizontalSpeedSqr) {
            if(_playerAnimator == null || _playerTransform == null) return;

            var localVelocity = _playerTransform.InverseTransformDirection(horizontalVelocity);
            var isSprinting = cachedHorizontalSpeedSqr > (WalkSpeed + 1f) * (WalkSpeed + 1f);

            _playerAnimator.SetFloat(MoveXHash, localVelocity.x / maxSpeed, 0.1f, Time.deltaTime);
            _playerAnimator.SetFloat(MoveYHash, localVelocity.z / maxSpeed, 0.1f, Time.deltaTime);
            _playerAnimator.SetBool(IsSprintingHash, isSprinting);
            _playerAnimator.SetBool(IsFallingHash, IsFalling);

            // Reset jump trigger when grounded and not jumping, or when in air (falling)
            // Since _isFalling is now true during entire jump (up and down), we reset it whenever in air
            // Note: Land trigger is never reset - let the animator consume it naturally
            var isGrounded = playerController != null && playerController.IsGrounded;
            if((isGrounded && !IsJumping) || IsFalling) {
                // Grounded and not jumping, or in air - clear jump trigger
                _playerAnimator.ResetTrigger(JumpTriggerHash);
            }
        }

        /// <summary>
        /// Updates the falling state and handles landing logic.
        /// Should be called every frame from PlayerController.Update().
        /// </summary>
        public void UpdateFallingState(bool isGrounded, float verticalVelocity, Vector3 position) {
            // Track when we leave the ground
            if(_wasGrounded && !isGrounded) {
                _fallStartHeight = position.y;
            }

            // Initialize fall start height if we're in air, and it hasn't been set (edge case)
            if(!isGrounded && _fallStartHeight == 0f) {
                _fallStartHeight = position.y;
            }

            // Set falling to true whenever we're in air (both going up and down)
            // This allows jump->fall transitions to work in the animator
            if(!isGrounded) {
                IsFalling = true;

                // Track peak height while rising (for distance calculations)
                if(verticalVelocity > 0f) {
                    if(position.y > _fallStartHeight) {
                        _fallStartHeight = position.y;
                    }
                }
            } else {
                // Reset when grounded
                _fallStartHeight = 0f;
                IsFalling = false;
            }

            // Landing: always trigger land animation when we hit the ground from air
            if(!_wasGrounded && isGrounded) {
                if(IsOwner) {
                    PlayLandingAnimationServerRpc();
                    // Only play landing sound if enough time has passed since spawn/respawn
                    if(Time.time - _lastSpawnTime >= LandingSoundCooldown) {
                        if(_sfxRelay != null) {
                            _sfxRelay.RequestWorldSfx(SfxKey.Land, attachToSelf: true, allowOverlap: true);
                        }
                    }
                }

                IsJumping = false;
                IsFalling = false;
                _fallStartHeight = 0f;
            }

            _wasGrounded = isGrounded;
        }

        /// <summary>
        /// Updates the turn animation based on yaw delta.
        /// </summary>
        public void UpdateTurnAnimation(float yawDelta) {
            if(_playerAnimator == null) return;

            var turnSpeed = Mathf.Abs(yawDelta) > 0.001f ? Mathf.Clamp(yawDelta * 10f, -1f, 1f) : 0f;
            _playerAnimator.SetFloat(LookXHash, turnSpeed, 0.1f, Time.deltaTime);
        }

        /// <summary>
        /// Plays the jump animation on all clients.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        public void PlayJumpAnimationServerRpc() {
            if(_playerAnimator == null) return;

            _playerAnimator.SetTrigger(JumpTriggerHash);
            _playerAnimator.SetBool(IsJumpingHash, true);
            IsJumping = true;
        }

        /// <summary>
        /// Plays the landing animation on all clients.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        private void PlayLandingAnimationServerRpc() {
            if(_playerAnimator == null) return;

            _playerAnimator.SetTrigger(LandTriggerHash);
            _playerAnimator.SetBool(IsJumpingHash, false);
            IsFalling = false;
            IsJumping = false;
            // Set IsFallingHash based on _isFalling state to ensure consistency
            _playerAnimator.SetBool(IsFallingHash, IsFalling);

            var isGrounded = playerController != null && playerController.IsGrounded;
            _playerAnimator.SetBool(IsGroundedHash, isGrounded);
        }

        /// <summary>
        /// Sets the crouching state in the animator.
        /// </summary>
        public void SetCrouching(bool isCrouching) {
            if(_playerAnimator == null) return;
            _playerAnimator.SetBool(IsCrouchingHash, isCrouching);
        }

        /// <summary>
        /// Triggers the damage animation.
        /// </summary>
        public void PlayDamageAnimation() {
            if(_playerAnimator == null) return;
            _playerAnimator.SetTrigger(DamageTriggerHash);
        }

        /// <summary>
        /// Resets spawn time (called on respawn).
        /// </summary>
        public void ResetSpawnTime() {
            _lastSpawnTime = Time.time;
        }

        // Public getters for state
        private bool IsJumping { get; set; }

        private bool IsFalling { get; set; }
    }
}