using Game.Audio;
using Game.Menu;
using Game.Weapons;
using Network.Rpc;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Handles all movement-related logic for the player.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public class PlayerMovementController : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private CharacterController _characterController;
        private GrappleController _grappleController;
        private SwingGrapple _swingGrapple;
        private PlayerAnimationController _animationController;
        private NetworkSfxRelay _sfxRelay;
        private Transform _playerTransform;

        // Wall contact dampening
        private float _wallContactTime;
        private const float WallContactThreshold = 0.15f; // Time before dampening kicks in
        private const float WallDampenRate = 8f; // How fast to reduce velocity when stuck
        private const float WallBlockRatio = 0.5f; // Movement ratio that counts as "blocked"
        private const float WallMinSpeedThreshold = 5f; // Minimum speed to trigger wall detection

        // Slide state
        private bool _isSliding;
        private Vector3 _slideDirection;
        private float _slideSpeed;
        private bool _wasStandingBeforeCrouch; // Track if player was standing before crouch input
        private bool _wasAirborne; // Track if player was in air (for landing slide)

        // Slide constants
        private const float SlideMinSpeed = 10f;        // Must be >= sprint speed to initiate
        private const float SlideBaseFriction = 4f;    // Base deceleration during slide
        private const float SlideSpeedFriction = 0.15f; // Additional friction proportional to speed
        private const float SlideExitSpeed = 2.5f;      // Transition to crouch-walk below this speed
        private const float SlideSlopeMultiplier = 5f;  // How much slopes affect slide speed

        [Header("Movement Parameters")]
        private const float Acceleration = 15f;

        private const float AirAcceleration = 50f;
        private const float MaxAirSpeed = 5f;
        private const float Friction = 8f;

        // Movement constants
        private const float WalkSpeed = 5f;
        private const float SprintSpeed = 10f;
        private const float JumpHeight = 2f;
        private const float CrouchSpeed = 2.5f;
        private const float StandHeight = 1.7f;
        private const float CrouchHeight = 1.1f;
        private const float StandCollider = 1.9f;
        private const float CrouchCollider = 1.3f;
        private const float StandCheckHeight = StandCollider - CrouchCollider;
        private const float GravityScale = 3f;
        private const float TerminalVelocity = -50f; // Maximum fall speed (m/s)

        // Movement state
        private Vector3 _horizontalVelocity;
        private float _crouchTransition;
        private Vector3 _moveVelocity;
        private Vector3 _cachedFullVelocity;

        // Physics
        private int _obstacleMask;
        private float _gravityY;
        private bool _isMantling;

        // Wall contact dampening
        private float _wallContactTime;
        private const float WallContactThreshold = 0.15f; // Time before dampening kicks in
        private const float WallDampenRate = 8f; // How fast to reduce velocity when stuck
        private const float WallBlockRatio = 0.5f; // Movement ratio that counts as "blocked"
        private const float WallMinSpeedThreshold = 5f; // Minimum speed to trigger wall detection

        // Input (read from PlayerController)
        private Vector2 MoveInput => playerController == null ? Vector2.zero : playerController.moveInput;

        private bool SprintInput => playerController != null && playerController.sprintInput;

        private bool CrouchInput => playerController != null && playerController.crouchInput;

        // Network state (from PlayerController)
        public NetworkVariable<bool> netIsCrouching;
        public NetworkVariable<bool> netIsSliding;

        // Throttling for crouch updates (at 90Hz: 2 ticks = ~22ms)
        private float _lastCrouchUpdateTime;
        private const float CrouchUpdateInterval = 0.022f; // ~2 ticks at 90Hz

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[PlayerMovementController] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_characterController == null) _characterController = playerController.CharacterController;
            if(_playerTransform == null) _playerTransform = playerController.PlayerTransform;
            if(_grappleController == null) _grappleController = playerController.GrappleController;
            if(_animationController == null) _animationController = playerController.AnimationController;
            if(_sfxRelay == null) _sfxRelay = playerController.SfxRelay;

            // Initialize physics (non-network dependent)
            _obstacleMask = playerController.WorldLayer | playerController.EnemyLayer;
            _gravityY = Physics.gravity.y;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Get network variables from PlayerController (network-dependent)
            if(playerController != null) {
                netIsCrouching = playerController.NetIsCrouching;
                netIsSliding = playerController.netIsSliding;
            }
        }

        public void UpdateMovement(CinemachineCamera fpCamera = null) {
            if(_isMantling || (_swingGrapple != null && _swingGrapple.IsSwinging)) {
                return;
            }

            // Track standing state for slide initiation
            if(!CrouchInput) {
                _wasStandingBeforeCrouch = true;
            }

            // Handle sliding
            if(_isSliding) {
                ProcessSlide();
            } else if(CanInitiateSlide()) {
                BeginSlide();
            } else if(CanLandingSlide()) {
                // Re-initiate slide when landing while crouched at speed
                BeginSlide();
            } else {
                // Normal movement
                UpdateMaxSpeed();
                CalculateHorizontalVelocity();
            }

            // Track airborne state for landing slide detection
            _wasAirborne = !IsGrounded;

            CheckCeilingHit(fpCamera);
            ApplyGravity();
            MoveCharacter();

            // Cache horizontal speed for animation/sound
            CachedHorizontalSpeedSqr = _horizontalVelocity.sqrMagnitude;
        }

        public void UpdateCrouch(CinemachineCamera fpCamera) {
            if(fpCamera == null) return;

            var sphereRadius = _characterController != null ? _characterController.radius : 0.3f;
            var headBlocked = Physics.SphereCast(
                fpCamera.transform.position,
                sphereRadius,
                Vector3.up,
                out _,
                StandCheckHeight,
                _obstacleMask
            );

            var isCurrentlyCrouched = _crouchTransition > 0.5f || (netIsCrouching != null && netIsCrouching.Value);

            bool targetCrouchState;
            if(CrouchInput) {
                targetCrouchState = true;
            } else {
                targetCrouchState = headBlocked && isCurrentlyCrouched;
            }

            if(IsOwner && netIsCrouching != null && netIsCrouching.Value != targetCrouchState) {
                // Throttle network updates - only send if enough time has passed (state change is immediate)
                if(Time.time - _lastCrouchUpdateTime >= CrouchUpdateInterval) {
                    netIsCrouching.Value = targetCrouchState;
                    _lastCrouchUpdateTime = Time.time;
                }
            }

            if(_animationController != null) {
                _animationController.SetCrouching(targetCrouchState);
            }

            var targetTransition = targetCrouchState ? 1f : 0f;
            _crouchTransition = Mathf.Lerp(_crouchTransition, targetTransition, 10f * Time.deltaTime);

            var targetCameraHeight = Mathf.Lerp(StandHeight, CrouchHeight, _crouchTransition);

            if(IsOwner) {
                fpCamera.transform.localPosition = new Vector3(0f, targetCameraHeight, 0f);
            }

            UpdateCharacterControllerCrouch(targetCrouchState);
        }

        private void UpdateMaxSpeed() {
            if(CrouchInput) {
                MaxSpeed = CrouchSpeed;
            } else if(SprintInput) {
                MaxSpeed = SprintSpeed;
            } else {
                MaxSpeed = WalkSpeed;
            }
        }

        private void CalculateHorizontalVelocity() {
            // Block movement during pre-match (but allow input to be set so it feels responsive when match starts)
            if(GameMenuManager.Instance != null &&
               GameMenuManager.IsPreMatch) {
                // Still apply friction to slow down if already moving
                ApplyFriction();
                var targetVelocity = Vector3.zero;
                _horizontalVelocity =
                    Vector3.MoveTowards(_horizontalVelocity, targetVelocity, Acceleration * Time.deltaTime);
                return;
            }

            var motion = (_playerTransform.forward * MoveInput.y + _playerTransform.right * MoveInput.x).normalized;
            motion.y = 0f;

            if(IsGrounded) {
                ApplyFriction();
                ApplyDirectionChange(motion);

                var targetVelocity = motion.sqrMagnitude >= 0.1f ? motion * MaxSpeed : Vector3.zero;
                _horizontalVelocity =
                    Vector3.MoveTowards(_horizontalVelocity, targetVelocity, Acceleration * Time.deltaTime);
            } else {
                AirStrafe(motion);
            }
        }

        private void ApplyFriction() {
            if(MoveInput.sqrMagnitude >= 0.01f) return;

            var speed = _horizontalVelocity.magnitude;
            if(speed < 0.001f) return;

            var drop = speed * Friction * Time.deltaTime;
            var newSpeed = Mathf.Max(speed - drop, 0f);
            _horizontalVelocity *= newSpeed / speed;
        }

        private void ApplyDirectionChange(Vector3 motion) {
            if(!(_horizontalVelocity.magnitude > 0.1f) || !(motion.magnitude > 0.1f)) return;

            var angle = Vector3.Angle(_horizontalVelocity, motion);

            if(!(angle > 90f)) return;

            var normalizedAngle = Mathf.InverseLerp(90f, 180f, angle);
            var reduction = Mathf.Lerp(0.85f, 0.2f, normalizedAngle * normalizedAngle);
            _horizontalVelocity *= reduction;
        }

        private void AirStrafe(Vector3 wishDir) {
            if(MoveInput.sqrMagnitude < 0.01f) return;

            var currentSpeed = Vector3.Dot(_horizontalVelocity, wishDir);
            var addSpeed = MaxAirSpeed - currentSpeed;

            if(addSpeed <= 0) return;

            var accelSpeed = AirAcceleration * Time.deltaTime;
            accelSpeed = Mathf.Min(accelSpeed, addSpeed);

            _horizontalVelocity += wishDir * accelSpeed;
        }

        private void CheckCeilingHit(CinemachineCamera fpCamera) {
            if(fpCamera == null || _grappleController == null) return;

            var rayHit = Physics.Raycast(fpCamera.transform.position, Vector3.up, out _, 0.75f, _obstacleMask);
            if(!rayHit || !(VerticalVelocity > 0f)) return;
            _grappleController.CancelGrapple();

            VerticalVelocity = 0f;
        }

        private void ApplyGravity() {
            if(IsGrounded && VerticalVelocity <= 0.01f) {
                VerticalVelocity = -3f;
            } else {
                VerticalVelocity += _gravityY * GravityScale * Time.deltaTime;
                // Clamp to terminal velocity to prevent infinite acceleration
                VerticalVelocity = Mathf.Max(VerticalVelocity, TerminalVelocity);
            }
        }

        private void MoveCharacter() {
            _moveVelocity.x = _horizontalVelocity.x;
            _moveVelocity.y = VerticalVelocity;
            _moveVelocity.z = _horizontalVelocity.z;
            
            var positionBefore = _playerTransform.position;
            _characterController.Move(_moveVelocity * Time.deltaTime);
            var positionAfter = _playerTransform.position;
            
            HandleWallContactDampening(positionBefore, positionAfter);
        }

        /// <summary>
        /// Detects prolonged wall contact and gradually reduces velocity to match actual movement.
        /// Brief corner clips are preserved, but sustained wall contact dampens stored velocity.
        /// </summary>
        private void HandleWallContactDampening(Vector3 positionBefore, Vector3 positionAfter) {
            // Calculate actual vs intended horizontal movement
            var actualMove = positionAfter - positionBefore;
            var actualHorizontal = new Vector3(actualMove.x, 0f, actualMove.z) / Time.deltaTime;
            var intendedHorizontal = new Vector3(_horizontalVelocity.x, 0f, _horizontalVelocity.z);
            
            var intendedSpeed = intendedHorizontal.magnitude;
            var actualSpeed = actualHorizontal.magnitude;
            
            // Only check for wall contact when moving fast enough
            if (intendedSpeed < WallMinSpeedThreshold) {
                _wallContactTime = 0f;
                return;
            }
            
            // Calculate how much of our intended movement actually happened
            var blockRatio = actualSpeed / intendedSpeed;
            
            if (blockRatio < WallBlockRatio) {
                // We're blocked - accumulate contact time
                _wallContactTime += Time.deltaTime;
                
                if (_wallContactTime > WallContactThreshold) {
                    // Grace period expired - dampen velocity toward actual movement
                    _horizontalVelocity = Vector3.Lerp(
                        _horizontalVelocity,
                        actualHorizontal,
                        WallDampenRate * Time.deltaTime
                    );
                }
            } else {
                // Not blocked - reset timer
                _wallContactTime = 0f;
            }
        }

        private void UpdateCharacterControllerCrouch(bool isCrouching) {
            var targetTransition = isCrouching ? 1f : 0f;
            if(!IsOwner) {
                _crouchTransition = Mathf.Lerp(_crouchTransition, targetTransition, 10f * Time.deltaTime);
            }

            var targetColliderHeight = Mathf.Lerp(StandCollider, CrouchCollider, _crouchTransition);
            var centerY = targetColliderHeight / 2f;
            _characterController.height = targetColliderHeight;
            _characterController.center = new Vector3(0f, centerY, 0f);
        }

        public void TryJump(float height = JumpHeight) {
            if(!IsGrounded) {
                return;
            }

            // Slide-hop: cancel slide but preserve momentum
            if(_isSliding) {
                CancelSlideForJump();
            }

            // Check for jump pads (regular or mega)
            var jumpPadHeight = CheckForJumpPad();
            if(jumpPadHeight > 0f) {
                height = jumpPadHeight;
            }

            if(IsOwner && _sfxRelay != null) {
                var key = Mathf.Approximately(height, 15f) || Mathf.Approximately(height, 30f) ? "jumpPad" : "jump";

                if(key == "jumpPad") {
                    _sfxRelay.RequestWorldSfx(SfxKey.JumpPad, attachToSelf: true, true);
                }

                _sfxRelay.RequestWorldSfx(SfxKey.Jump, attachToSelf: true, true);
            }

            // Calculate and apply vertical velocity for jump
            VerticalVelocity = Mathf.Sqrt(height * -2f * _gravityY * GravityScale);

            // Ensure velocity is positive (upward) before triggering jump animation
            // This guarantees the jump animation only triggers when velocity is actually applied upward
            if(!(VerticalVelocity > 0f)) return;

            // Notify WeaponBob that jump was initiated (owner only, local effect)
            if(IsOwner && playerController != null) {
                WeaponBob weaponBob = null;
                if(playerController.FpCamera != null) {
                    weaponBob = playerController.FpCamera.GetComponentInChildren<WeaponBob>();
                }

                if(weaponBob != null) {
                    weaponBob.OnJumpInitiated();
                } else {
                    Debug.LogWarning(
                        $"[PlayerMovementController] TryJump: WeaponBob not found! FpCamera={playerController.FpCamera != null}");
                }
            }

            if(_animationController != null) {
                _animationController.PlayJumpAnimationServerRpc();
            }
        }

        /// <summary>
        /// Applies a jump pad launch in the direction of the jump pad's surface normal.
        /// For vertical/flat pads: adds vertical boost, preserving horizontal velocity (e.g., from grappling).
        /// For wall/slope pads: adds boost in pad's normal direction (both horizontal and vertical components).
        /// </summary>
        /// <param name="normal">The surface normal of the jump pad (from transform.up)</param>
        /// <param name="force">The force magnitude to apply (defaults to equivalent of 15f jump height)</param>
        public void LaunchFromJumpPad(Vector3 normal, float force = 15f) {
            if(!IsGrounded) {
                return;
            }

            // Cancel slide if active, preserving momentum into the launch
            if(_isSliding) {
                CancelSlideForJump();
            }

            // Normalize the normal to ensure consistent force
            normal = normal.normalized;

            // Calculate the velocity magnitude equivalent to the jump height
            // This matches the calculation in TryJump: sqrt(height * -2 * gravity * gravityScale)
            var velocityMagnitude = Mathf.Sqrt(force * -2f * _gravityY * GravityScale);

            // Apply velocity in the direction of the pad's normal
            // This gives us the full boost vector (horizontal + vertical components)
            var launchVelocity = normal * velocityMagnitude;

            // Always apply the full velocity boost in the pad's normal direction
            // For vertical pads (normal = up): only vertical component, horizontal preserved
            // For angled/wall pads: both horizontal and vertical components added
            VerticalVelocity = launchVelocity.y;
            _horizontalVelocity += new Vector3(launchVelocity.x, 0f, launchVelocity.z);

            // Play jump pad sound
            if(IsOwner && _sfxRelay != null) {
                _sfxRelay.RequestWorldSfx(SfxKey.JumpPad, attachToSelf: true, true);
                _sfxRelay.RequestWorldSfx(SfxKey.Jump, attachToSelf: true, true);
            }

            // Trigger jump animation if moving upward
            if(!(VerticalVelocity > 0f)) return;

            // Notify WeaponBob that jump pad launch was initiated (owner only, local effect)
            if(IsOwner && playerController != null && playerController.FpCamera != null) {
                var weaponBob = playerController.FpCamera.GetComponentInChildren<WeaponBob>();
                if(weaponBob != null) {
                    weaponBob.OnJumpInitiated();
                } else {
                    Debug.LogWarning(
                        $"[PlayerMovementController] LaunchFromJumpPad: WeaponBob not found! FpCamera={playerController.FpCamera != null}");
                }
            }

            if(_animationController)
                _animationController.PlayJumpAnimationServerRpc();
        }

        private float CheckForJumpPad() {
            if(!Physics.Raycast(playerController.Position, Vector3.down, out var hit,
                   _characterController.height * 0.6f))
                return 0f; // No jump pad found
            if(hit.collider.CompareTag("JumpPad")) {
                return 15f; // Regular jump pad height
            }

            return hit.collider.CompareTag("MegaPad")
                ? 30f
                : // Mega jump pad height
                0f; // No jump pad found
        }

        public void ResetVelocity() {
            _horizontalVelocity = Vector3.zero;
            VerticalVelocity = 0f;
        }

        public void SetVelocity(Vector3 horizontalVelocity) {
            _horizontalVelocity = new Vector3(horizontalVelocity.x, 0f, horizontalVelocity.z);
        }

        public void AddVerticalVelocity(float verticalBoost) {
            VerticalVelocity += verticalBoost;
        }

        // Public getters
        public bool IsGrounded => _characterController != null && _characterController.isGrounded;

        public Vector3 FullVelocity {
            get {
                _cachedFullVelocity.x = _horizontalVelocity.x;
                _cachedFullVelocity.y = VerticalVelocity;
                _cachedFullVelocity.z = _horizontalVelocity.z;
                return _cachedFullVelocity;
            }
        }

        public Vector3 HorizontalVelocity => _horizontalVelocity;
        public float VerticalVelocity { get; private set; }

        public float MaxSpeed { get; private set; } = WalkSpeed;

        public float CachedHorizontalSpeedSqr { get; private set; }

        public bool IsSliding => _isSliding;

        #region Slide Methods

        /// <summary>
        /// Check if slide can be initiated.
        /// Requires: grounded, was standing, now crouching, moving fast enough, moving forward-ish.
        /// </summary>
        private bool CanInitiateSlide() {
            if(!IsGrounded) return false;
            if(!CrouchInput) return false;
            if(!_wasStandingBeforeCrouch) return false;

            var speed = _horizontalVelocity.magnitude;
            if(speed < SlideMinSpeed) return false;

            // Don't allow backward slides - check if velocity is roughly forward
            var velocityDir = _horizontalVelocity.normalized;
            var forwardDot = Vector3.Dot(velocityDir, _playerTransform.forward);
            if(forwardDot < -0.3f) return false; // Backward movement

            return true;
        }

        /// <summary>
        /// Check if slide should re-initiate on landing.
        /// Requires: just landed, crouching, moving fast enough.
        /// </summary>
        private bool CanLandingSlide() {
            // Must have been airborne last frame and now grounded
            if(!_wasAirborne || !IsGrounded) return false;
            if(!CrouchInput) return false;

            var speed = _horizontalVelocity.magnitude;
            if(speed < SlideMinSpeed) return false;

            return true;
        }

        /// <summary>
        /// Begin sliding. Lock direction to current velocity, set slide speed.
        /// </summary>
        private void BeginSlide() {
            _isSliding = true;
            _wasStandingBeforeCrouch = false;
            _slideDirection = _horizontalVelocity.normalized;
            _slideSpeed = _horizontalVelocity.magnitude;

            // Sync to network
            if(IsOwner && netIsSliding != null) {
                netIsSliding.Value = true;
            }

            // Play slide sound (skeleton - needs audio asset)
            if(IsOwner && _sfxRelay != null) {
                _sfxRelay.RequestWorldSfx(SfxKey.Slide, attachToSelf: true);
            }
        }

        /// <summary>
        /// Process active slide. Apply friction, slope influence, check exit conditions.
        /// </summary>
        private void ProcessSlide() {
            // Check exit conditions first
            if(!CrouchInput) {
                EndSlide();
                return;
            }

            if(!IsGrounded) {
                // Preserve full momentum when sliding off ledge
                CancelSlideForJump();
                return;
            }

            // Apply proportional friction (faster slides slow down faster)
            var friction = SlideBaseFriction + (_slideSpeed * SlideSpeedFriction);
            _slideSpeed -= friction * Time.deltaTime;

            // Apply slope influence
            ApplySlopeToSlide();

            // Check if slide has ended naturally
            if(_slideSpeed <= SlideExitSpeed) {
                EndSlide();
                return;
            }

            // Update horizontal velocity to match slide
            _horizontalVelocity = _slideDirection * _slideSpeed;

            // MaxSpeed is crouch speed during slide (for animation purposes)
            MaxSpeed = CrouchSpeed;
        }

        /// <summary>
        /// Apply slope influence to slide speed.
        /// Downhill = speed up, uphill = slow down.
        /// </summary>
        private void ApplySlopeToSlide() {
            // Raycast to get ground normal
            if(!Physics.Raycast(_playerTransform.position, Vector3.down, out var hit, 2f, _obstacleMask)) {
                return;
            }

            var groundNormal = hit.normal;
            var slopeAngle = Vector3.Angle(groundNormal, Vector3.up);

            // Only apply slope influence on actual slopes
            if(slopeAngle < 5f) return;

            // Calculate slope direction relative to slide direction
            // Positive dot = sliding downhill, negative = uphill
            var slopeDirection = Vector3.ProjectOnPlane(Vector3.down, groundNormal).normalized;
            var slopeDot = Vector3.Dot(_slideDirection, slopeDirection);

            // Apply speed change based on slope
            _slideSpeed += slopeDot * SlideSlopeMultiplier * Time.deltaTime;

            // Clamp to prevent negative or excessive speed
            _slideSpeed = Mathf.Clamp(_slideSpeed, 0f, 50f);
        }

        /// <summary>
        /// End slide. Transition to crouch-walk with remaining velocity.
        /// </summary>
        private void EndSlide() {
            _isSliding = false;

            // Set remaining velocity (continues in slide direction at current speed)
            if(_slideSpeed > 0f) {
                _horizontalVelocity = _slideDirection * Mathf.Min(_slideSpeed, CrouchSpeed);
            }

            // Sync to network
            if(IsOwner && netIsSliding != null) {
                netIsSliding.Value = false;
            }
        }

        /// <summary>
        /// Called from TryJump when slide-hopping.
        /// Preserves slide momentum into the jump.
        /// </summary>
        public void CancelSlideForJump() {
            if(!_isSliding) return;

            // Preserve full slide velocity for jump
            _horizontalVelocity = _slideDirection * _slideSpeed;
            _isSliding = false;

            // Sync to network
            if(IsOwner && netIsSliding != null) {
                netIsSliding.Value = false;
            }
        }

        #endregion
    }
}