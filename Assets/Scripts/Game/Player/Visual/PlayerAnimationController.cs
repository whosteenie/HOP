using Game.Audio.System;
using Game.Player.Contracts;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Visual {
    /// <summary>
    /// Handles all animation state management for the player.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerAnimationController : NetworkBehaviour {
        [Header("References")]
        [HideInInspector, SerializeField] private MonoBehaviour playerContextSource;

        private IPlayerVisualContext _playerContext;

        private Animator _playerAnimator;
        private NetworkAudioRelay _audioRelay;
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
        private static readonly int IsSlidingHash = Animator.StringToHash("IsSliding");
        private static readonly int SlideTriggerHash = Animator.StringToHash("SlideTrigger");
        private static readonly int IsWallRunningHash = Animator.StringToHash("IsWallRunning");
        private static readonly int RightWallRunHash = Animator.StringToHash("RightWallRun");
        private static readonly int WallRunDirectionHash = Animator.StringToHash("WallRunDirection");
        private static readonly int MantleTriggerHash = Animator.StringToHash("MantleTrigger");

        // Animation state tracking
        private bool _wasGrounded;
        private float _fallStartHeight;
        private float _lastSpawnTime;
        private bool _remoteIsWallRunning;
        private bool _remoteIsRightWallRun;
        private float _remoteWallRunDirection = 1f;
        private const float LandingSoundCooldown = 0.5f; // Block landing sounds for 0.5s after spawn/respawn

        // Constants
        private const float WalkSpeed = 5f;

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(!PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext)) {
                Debug.LogError("[PlayerAnimationController] IPlayerVisualContext not found!");
                enabled = false;
                return;
            }

            if(_playerAnimator == null) _playerAnimator = _playerContext.PlayerAnimator;
            if(_audioRelay == null) _audioRelay = _playerContext.AudioRelay;
            if(_playerTransform == null) _playerTransform = _playerContext.PlayerTransform;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

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
            _playerAnimator.SetBool(IsJumpingHash, IsJumping);
            _playerAnimator.SetBool(IsFallingHash, IsFalling);
            _playerAnimator.SetBool(IsGroundedHash, !IsFalling);

            var isWallRunning = IsOwner
                ? _playerContext is { IsWallRunning: true }
                : _remoteIsWallRunning;
            var isRightWallRun = IsOwner
                ? _playerContext is { IsRightWallRunning: true }
                : _remoteIsRightWallRun;
            _playerAnimator.SetBool(IsWallRunningHash, isWallRunning);
            _playerAnimator.SetBool(RightWallRunHash, isRightWallRun);
            _playerAnimator.SetFloat(WallRunDirectionHash,
                IsOwner ? GetWallRunDirection(horizontalVelocity, isWallRunning) : _remoteWallRunDirection);

            var isGrounded = _playerContext is { IsGrounded: true };
            if((isGrounded && !IsJumping) || IsFalling) {
            }
        }

        private float GetWallRunDirection(Vector3 horizontalVelocity, bool isWallRunning) {
            if(!isWallRunning || _playerTransform == null) {
                return 1f;
            }

            var planar = horizontalVelocity;
            planar.y = 0f;
            if(planar.sqrMagnitude < 0.01f) {
                return 1f;
            }

            var forward = _playerTransform.forward;
            forward.y = 0f;
            if(forward.sqrMagnitude < 0.01f) {
                return 1f;
            }

            return Vector3.Dot(planar.normalized, forward.normalized) >= 0f ? 1f : -1f;
        }

        /// <summary>
        /// Updates the falling state and handles landing logic.
        /// Should be called every frame from PlayerController.Update().
        /// </summary>
        public void UpdateFallingState(bool isGrounded, float verticalVelocity, Vector3 position) {
            if(!IsOwner) return;

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
                if(_playerContext is { NetIsFalling: not null }) {
                    _playerContext.NetIsFalling.Value = true;
                }

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
                if(_playerContext is { NetIsFalling: not null }) {
                    _playerContext.NetIsFalling.Value = false;
                }
            }

            // Landing: always trigger land animation when we hit the ground from air
            if(!_wasGrounded && isGrounded) {
                if(IsOwner) {
                    TriggerLandingAnimation();
                    // Only play landing sound if enough time has passed since spawn/respawn
                    if(_audioRelay != null && Time.time - _lastSpawnTime >= LandingSoundCooldown) {
                        _audioRelay.RequestPlayAttached("foley.tile.jump.land", new NetworkObjectReference(_playerContext.NetworkObject),
                            allowOverlap: true);
                    }
                }

                IsJumping = false;
                IsFalling = false;
                _fallStartHeight = 0f;
                if(_playerContext != null) {
                    _playerContext.NetIsJumping.Value = false;
                    if(_playerContext.NetIsFalling != null) _playerContext.NetIsFalling.Value = false;
                }
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
        public void TriggerJumpAnimation() {
            if(_playerAnimator == null) return;
            if(!IsOwner) return;

            _playerAnimator.SetTrigger(JumpTriggerHash);
            _playerAnimator.SetBool(IsJumpingHash, true);
            IsJumping = true;

            if(_playerContext == null) return;
            _playerContext.NetIsJumping.Value = true;
            _playerContext.JumpAnimationSequence.Value++;
        }

        /// <summary>
        /// Plays the landing animation on all clients.
        /// </summary>
        private void TriggerLandingAnimation() {
            if(_playerAnimator == null) return;
            if(!IsOwner) return;

            _playerAnimator.SetTrigger(LandTriggerHash);
            _playerAnimator.SetBool(IsJumpingHash, false);
            IsFalling = false;
            IsJumping = false;
            // Set IsFallingHash based on _isFalling state to ensure consistency
            _playerAnimator.SetBool(IsFallingHash, IsFalling);

            var isGrounded = _playerContext is { IsGrounded: true };
            _playerAnimator.SetBool(IsGroundedHash, isGrounded);

            if(_playerContext == null) return;
            _playerContext.NetIsJumping.Value = false;
            _playerContext.NetIsFalling.Value = false;
            _playerContext.LandAnimationSequence.Value++;
        }

        /// <summary>
        /// Plays the mantle animation trigger on all clients.
        /// </summary>
        public void TriggerMantleAnimation() {
            if(_playerAnimator == null) return;
            if(!IsOwner) return;
            _playerAnimator.SetTrigger(MantleTriggerHash);
            if(_playerContext != null) {
                _playerContext.MantleAnimationSequence.Value++;
            }
        }

        public void SetSlidingState(bool isSliding, bool playTrigger = false) {
            if(_playerAnimator == null) return;
            _playerAnimator.SetBool(IsSlidingHash, isSliding);
            if(playTrigger && isSliding) {
                _playerAnimator.SetTrigger(SlideTriggerHash);
            }
        }

        public void ApplyRemoteJumpingState(bool isJumping) {
            if(IsOwner || _playerAnimator == null) return;
            IsJumping = isJumping;
            _playerAnimator.SetBool(IsJumpingHash, isJumping);
        }

        public void ApplyRemoteFallingState(bool isFalling) {
            if(IsOwner || _playerAnimator == null) return;
            IsFalling = isFalling;
            _playerAnimator.SetBool(IsFallingHash, isFalling);
            _playerAnimator.SetBool(IsGroundedHash, !isFalling);
        }

        public void ApplyRemoteSlidingState(bool isSliding, bool playTrigger) {
            if(IsOwner) return;
            SetSlidingState(isSliding, playTrigger);
        }

        public void PlayRemoteJumpAnimation() {
            if(IsOwner || _playerAnimator == null) return;
            _playerAnimator.SetTrigger(JumpTriggerHash);
        }

        public void PlayRemoteLandingAnimation() {
            if(IsOwner || _playerAnimator == null) return;
            _playerAnimator.SetTrigger(LandTriggerHash);
            IsJumping = false;
            IsFalling = false;
            _playerAnimator.SetBool(IsJumpingHash, false);
            _playerAnimator.SetBool(IsFallingHash, false);
            _playerAnimator.SetBool(IsGroundedHash, true);
        }

        public void PlayRemoteMantleAnimation() {
            if(IsOwner || _playerAnimator == null) return;
            _playerAnimator.SetTrigger(MantleTriggerHash);
        }

        public void ApplyRemoteStateSnapshot(bool isJumping, bool isFalling, bool isSliding) {
            if(IsOwner || _playerAnimator == null) return;
            IsJumping = isJumping;
            IsFalling = isFalling;
            _playerAnimator.SetBool(IsJumpingHash, isJumping);
            _playerAnimator.SetBool(IsFallingHash, isFalling);
            _playerAnimator.SetBool(IsGroundedHash, !isFalling);
            _playerAnimator.SetBool(IsSlidingHash, isSliding);
        }

        public void ApplyRemoteWallRunState(bool isWallRunning, bool isRightWallRun, float wallRunDirection) {
            if(IsOwner || _playerAnimator == null) return;
            _remoteIsWallRunning = isWallRunning;
            _remoteIsRightWallRun = isRightWallRun;
            _remoteWallRunDirection = Mathf.Approximately(wallRunDirection, 0f) ? 1f : Mathf.Sign(wallRunDirection);
            _playerAnimator.SetBool(IsWallRunningHash, isWallRunning);
            _playerAnimator.SetBool(RightWallRunHash, isRightWallRun);
            _playerAnimator.SetFloat(WallRunDirectionHash, _remoteWallRunDirection);
        }

        /// <summary>
        /// Sets the crouching state in the animator.
        /// </summary>
        public void SetCrouching(bool isCrouching) {
            if(_playerAnimator != null) {
                _playerAnimator.SetBool(IsCrouchingHash, isCrouching);
            }
        }

        /// <summary>
        /// Triggers the damage animation.
        /// </summary>
        public void PlayDamageAnimation() {
            if(_playerAnimator != null) {
                _playerAnimator.SetTrigger(DamageTriggerHash);
            }
        }

        /// <summary>
        /// Resets spawn time (called on respawn).
        /// </summary>
        public void ResetSpawnTime() {
            _lastSpawnTime = Time.time;
            _wasGrounded = false;
            IsJumping = false;
            IsFalling = false;

            if(_playerAnimator != null) {
                _playerAnimator.SetBool(IsJumpingHash, false);
                _playerAnimator.SetBool(IsFallingHash, false);
                _playerAnimator.SetBool(IsGroundedHash, true);
                _playerAnimator.SetBool(IsSlidingHash, false);
                _playerAnimator.SetBool(IsWallRunningHash, false);
                _playerAnimator.SetBool(RightWallRunHash, false);
                _playerAnimator.SetFloat(WallRunDirectionHash, 1f);
            }

            if(IsOwner && _playerContext != null) {
                _playerContext.NetIsJumping.Value = false;
                _playerContext.NetIsFalling.Value = false;
                _playerContext.NetIsSliding.Value = false;
                _playerContext.NetIsWallRunning.Value = false;
                _playerContext.NetIsRightWallRun.Value = false;
                _playerContext.NetWallRunDirection.Value = 1f;
            }

            _remoteIsWallRunning = false;
            _remoteIsRightWallRun = false;
            _remoteWallRunDirection = 1f;
        }

        // Public getters for state
        /// <summary>
        /// Gets or sets whether the player is currently in a jumping state.
        /// </summary>
        private bool IsJumping { get; set; }

        /// <summary>
        /// Gets or sets whether the player is currently in a falling state.
        /// </summary>
        private bool IsFalling { get; set; }
    }
}

