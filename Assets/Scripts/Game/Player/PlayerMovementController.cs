using Audio.Networking;
using Game.Menu;
using Game.Weapons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using Game.Progression;

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
        private WallRunController _wallRunController;
        private PlayerAnimationController _animationController;
        private NetworkAudioRelay _audioRelay;
        private Transform _playerTransform;

        // Wall contact dampening
        private float _wallContactTime;
        private const float WallContactThreshold = 0.15f; // Time before dampening kicks in
        private const float WallDampenRate = 8f; // How fast to reduce velocity when stuck
        private const float WallBlockRatio = 0.5f; // Movement ratio that counts as "blocked"
        private const float WallMinSpeedThreshold = 5f; // Minimum speed to trigger wall detection

        // Slide state
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
        private const float SlideDuration = 0.83f;      // ~50 frames at 60fps
        private float _slideTimer;

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

        // Progression Tracking
        private int _currentWallRunChain;
        private bool _wasWallRunningLastFrame;

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
            if(_wallRunController == null) {
                _wallRunController = playerController.WallRunController;
                if (_wallRunController == null) {
                    _wallRunController = GetComponent<WallRunController>();
                }
            }
            if(_animationController == null) _animationController = playerController.AnimationController;
            if(_audioRelay == null) _audioRelay = playerController.AudioRelay;

            _obstacleMask = playerController.WorldLayer | playerController.EnemyLayer;
            _gravityY = Physics.gravity.y;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            if(playerController != null) {
                netIsCrouching = playerController.NetIsCrouching;
                netIsSliding = playerController.netIsSliding;
            }
        }

        /// <summary>
        /// Main movement update loop called by PlayerController.
        /// Handles wall running, sliding, and normal movement state transitions.
        /// </summary>
        public void UpdateMovement(CinemachineCamera fpCamera = null) {
            if(_isMantling || (_swingGrapple != null && _swingGrapple.IsSwinging)) {
                return;
            }

            // Grapple pull must control movement; stop wall run so stick velocity doesn't fight the pull (especially on curved).
            if(_grappleController != null && _grappleController.IsGrappling) {
                if(_wallRunController != null && _wallRunController.IsWallRunning)
                    _wallRunController.ForceStopWallRun();
            }

            if(!CrouchInput) {
                _wasStandingBeforeCrouch = true;
            }

            if(_wallRunController != null && (_grappleController == null || !_grappleController.IsGrappling)) {
                _wallRunController.CheckForWall();
                if(_wallRunController.IsWallRunning) {
                    if (!_wasWallRunningLastFrame) {
                        _currentWallRunChain++;
                        if (IsOwner && ProgressionManager.Instance != null) {
                            ProgressionManager.Instance.RecordWallRunChain(_currentWallRunChain);
                        }
                    }
                    _wasWallRunningLastFrame = true;

                    _wallRunController.UpdateWallRun();
                    _horizontalVelocity = _wallRunController.GetWallRunVelocity(_playerTransform.forward);
                } else {
                    _wasWallRunningLastFrame = false;
                }
            }

            if (IsGrounded) {
                _currentWallRunChain = 0;
            }

            if(_wallRunController != null && _wallRunController.IsWallRunning && (_grappleController == null || !_grappleController.IsGrappling)) {
            } else if(IsSliding) {
                ProcessSlide();
            } else if(CanInitiateSlide()) {
                BeginSlide();
            } else if(CanLandingSlide()) {
                BeginSlide();
            } else {
                UpdateMaxSpeed();
                CalculateHorizontalVelocity();
            }

            _wasAirborne = !IsGrounded;

            CheckCeilingHit(fpCamera);
            ApplyGravity();
            MoveCharacter();

            CachedHorizontalSpeedSqr = _horizontalVelocity.sqrMagnitude;
        }

        /// <summary>
        /// Updates the player's crouch state, camera height, and collider height.
        /// </summary>
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

        /// <summary>
        /// Updates the max speed allowed based on current player input state.
        /// </summary>
        private void UpdateMaxSpeed() {
            if(CrouchInput) {
                MaxSpeed = CrouchSpeed;
            } else if(SprintInput) {
                MaxSpeed = SprintSpeed;
            } else {
                MaxSpeed = WalkSpeed;
            }
        }

        /// <summary>
        /// Calculates the horizontal velocity vector for the player based on input.
        /// </summary>
        private void CalculateHorizontalVelocity() {
            if(GameMenuManager.Instance != null && GameMenuManager.IsPreMatch) {
                ApplyFriction();
                var targetVel = Vector3.zero;
                _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, targetVel, Acceleration * Time.deltaTime);
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
                if(_wallRunController != null && _wallRunController.IsWallRunning) return;
                AirStrafe(motion);
            }
        }

        /// <summary>
        /// Applies movement friction when no input is provided.
        /// </summary>
        private void ApplyFriction() {
            if(MoveInput.sqrMagnitude >= 0.01f) return;

            var speed = _horizontalVelocity.magnitude;
            if(speed < 0.001f) return;

            var drop = speed * Friction * Time.deltaTime;
            var newSpeed = Mathf.Max(speed - drop, 0f);
            _horizontalVelocity *= newSpeed / speed;
        }

        /// <summary>
        /// Reduces horizontal velocity when changing directions sharply.
        /// </summary>
        private void ApplyDirectionChange(Vector3 motion) {
            if(!(_horizontalVelocity.magnitude > 0.1f) || !(motion.magnitude > 0.1f)) return;

            var angle = Vector3.Angle(_horizontalVelocity, motion);

            if(!(angle > 90f)) return;

            var normalizedAngle = Mathf.InverseLerp(90f, 180f, angle);
            var reduction = Mathf.Lerp(0.85f, 0.2f, normalizedAngle * normalizedAngle);
            _horizontalVelocity *= reduction;
        }

        /// <summary>
        /// Handles airstrafing logic for the player while airborne.
        /// </summary>
        private void AirStrafe(Vector3 wishDir) {
            if(MoveInput.sqrMagnitude < 0.01f) return;

            var currentSpeed = Vector3.Dot(_horizontalVelocity, wishDir);
            var addSpeed = MaxAirSpeed - currentSpeed;

            if(addSpeed <= 0) return;

            var accelSpeed = AirAcceleration * Time.deltaTime;
            accelSpeed = Mathf.Min(accelSpeed, addSpeed);

            _horizontalVelocity += wishDir * accelSpeed;
        }

        /// <summary>
        /// Checks for ceiling collisions and stops vertical velocity.
        /// </summary>
        private void CheckCeilingHit(CinemachineCamera fpCamera) {
            if(fpCamera == null || _grappleController == null) return;

            var rayHit = Physics.Raycast(fpCamera.transform.position, Vector3.up, out _, 0.75f, _obstacleMask);
            if(!rayHit || !(VerticalVelocity > 0f)) return;
            _grappleController.CancelGrapple();

            VerticalVelocity = 0f;
        }

        /// <summary>
        /// Applies gravity to the player's vertical velocity.
        /// </summary>
        private void ApplyGravity() {
            if (_wallRunController != null && _wallRunController.IsWallRunning) {
                VerticalVelocity = 0f;
                return;
            }

            if(IsGrounded && VerticalVelocity <= 0.01f) {
                VerticalVelocity = -3f;
            } else {
                VerticalVelocity += _gravityY * GravityScale * Time.deltaTime;
                VerticalVelocity = Mathf.Max(VerticalVelocity, TerminalVelocity);
            }
        }

        /// <summary>
        /// Applies the calculated velocity vectors to the character controller.
        /// </summary>
        private void MoveCharacter() {
            _moveVelocity.x = _horizontalVelocity.x;
            _moveVelocity.y = VerticalVelocity;
            _moveVelocity.z = _horizontalVelocity.z;
            
            var positionBefore = _playerTransform.position;
            _characterController.Move(_moveVelocity * Time.deltaTime);
            var positionAfter = _playerTransform.position;
            
            if (IsOwner && ProgressionManager.Instance != null) {
                if (IsGrounded) {
                    var dist = Vector3.Distance(new Vector3(positionBefore.x, 0, positionBefore.z), 
                                               new Vector3(positionAfter.x, 0, positionAfter.z));
                    if (dist > 0) {
                         ProgressionManager.Instance.AddDistanceTraveled(dist);
                    }
                } else {
                    ProgressionManager.Instance.RecordAirtime(Time.deltaTime);
                }
            }

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
                    
                    // Also dampen slide speed if currently sliding
                    if (IsSliding) {
                        _slideSpeed = Mathf.Lerp(_slideSpeed, actualSpeed, WallDampenRate * Time.deltaTime);
                    }
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

        /// <summary>
        /// Initiates a jump or wall jump.
        /// </summary>
        /// <param name="height">The desired jump height (m).</param>
        /// <summary>
        /// Attempts to perform a jump or wall jump.
        /// </summary>
        public void TryJump(float height = JumpHeight) {
            if(!IsGrounded) {
                if(_wallRunController != null && _wallRunController.IsWallRunning) {
                    _wallRunController.WallJump();
                    return;
                }
                
                return;
            }

            if(IsSliding) {
                CancelSlideForJump();
            }

            // Check for jump pads (regular or mega)
            var jumpPadHeight = CheckForJumpPad();
            if(jumpPadHeight > 0f) {
                height = jumpPadHeight;
            }

            if(IsOwner && _audioRelay != null) {
                var key = Mathf.Approximately(height, 15f) || Mathf.Approximately(height, 30f) ? "jumpPad" : "jump";

                if(key == "jumpPad") {
                    _audioRelay.RequestPlayAttached("gameplay.jumppad", new NetworkObjectReference(playerController.NetworkObject),
                        allowOverlap: true);
                }

                _audioRelay.RequestPlayAttached("foley.tile.jump.start", new NetworkObjectReference(playerController.NetworkObject),
                    allowOverlap: true);
            }

            VerticalVelocity = Mathf.Sqrt(height * -2f * _gravityY * GravityScale);

            if(!(VerticalVelocity > 0f)) return;

            if(IsOwner && playerController != null) {
                WeaponBob weaponBob = null;
                if(playerController.FpCamera != null) {
                    weaponBob = playerController.FpCamera.GetComponentInChildren<WeaponBob>();
                }

                if(weaponBob != null) {
                    weaponBob.OnJumpInitiated();
                }
            }

            if(_animationController != null) {
                _animationController.PlayJumpAnimationServerRpc();
            }
        }

        /// <summary>
        /// Applies a jump pad launch in the direction of the jump pad's surface normal.
        /// </summary>
        /// <param name="normal">The surface normal of the jump pad.</param>
        /// <param name="force">The force magnitude to apply.</param>
        public void LaunchFromJumpPad(Vector3 normal, float force = 15f) {
            if(!IsGrounded) {
                return;
            }

            if(IsSliding) {
                CancelSlideForJump();
            }

            normal = normal.normalized;
            var velocityMagnitude = Mathf.Sqrt(force * -2f * _gravityY * GravityScale);
            var launchVelocity = normal * velocityMagnitude;

            VerticalVelocity = launchVelocity.y;
            _horizontalVelocity += new Vector3(launchVelocity.x, 0f, launchVelocity.z);

            if(IsOwner && _audioRelay != null) {
                _audioRelay.RequestPlayAttached("gameplay.jumppad", new NetworkObjectReference(playerController.NetworkObject),
                    allowOverlap: true);
                _audioRelay.RequestPlayAttached("foley.tile.jump.start", new NetworkObjectReference(playerController.NetworkObject),
                    allowOverlap: true);
            }

            if (IsOwner && ProgressionManager.Instance != null) {
                ProgressionManager.Instance.RecordJumpPadUsed();
            }

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

        /// <summary>
        /// Resets player velocity and vertical speed to zero.
        /// </summary>
        public void ResetVelocity() {
            _horizontalVelocity = Vector3.zero;
            VerticalVelocity = 0f;
        }

        /// <summary>
        /// Sets a new horizontal velocity for the player.
        /// </summary>
        public void SetVelocity(Vector3 horizontalVelocity) {
            _horizontalVelocity = new Vector3(horizontalVelocity.x, 0f, horizontalVelocity.z);
        }

        /// <summary>
        /// Adds a vertical velocity boost to the player.
        /// </summary>
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
        public float VerticalVelocity { get; set; }

        public float MaxSpeed { get; private set; } = WalkSpeed;

        public float CachedHorizontalSpeedSqr { get; private set; }

        public bool IsSliding { get; private set; }

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
            return !(forwardDot < -0.3f); // Backward movement
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
            return !(speed < SlideMinSpeed);
        }

        /// <summary>
        /// Initiates a slide based on current player movement and direction.
        /// </summary>
        private void BeginSlide() {
            IsSliding = true;
            _wasStandingBeforeCrouch = false;
            _slideDirection = _horizontalVelocity.normalized;
            _slideSpeed = _horizontalVelocity.magnitude;
            _slideTimer = 0f;

            if(IsOwner && netIsSliding != null) {
                netIsSliding.Value = true;
            }
            
            if (IsOwner && _animationController != null) {
                _animationController.TriggerSlideServerRpc();
                _animationController.SetSlidingServerRpc(true);
            }

            if(IsOwner && _audioRelay != null) {
                _audioRelay.RequestPlayAttached("foley.slide", new NetworkObjectReference(playerController.NetworkObject),
                    allowOverlap: false);
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

            // Increment timer and check duration
            _slideTimer += Time.deltaTime;
            if (_slideTimer >= SlideDuration) {
                EndSlide();
                return;
            }

            // Update horizontal velocity to match slide
            _horizontalVelocity = _slideDirection * _slideSpeed;

            // MaxSpeed is crouch speed during slide (for animation purposes)
            MaxSpeed = CrouchSpeed;
        }

        /// <summary>
        /// Applies slope influence to the slide speed.
        /// </summary>
        private void ApplySlopeToSlide() {
            if(!Physics.Raycast(_playerTransform.position, Vector3.down, out var hit, 2f, _obstacleMask)) {
                return;
            }

            var groundNormal = hit.normal;
            var slopeAngle = Vector3.Angle(groundNormal, Vector3.up);

            if(slopeAngle < 5f) return;

            var slopeDirection = Vector3.ProjectOnPlane(Vector3.down, groundNormal).normalized;
            var slopeDot = Vector3.Dot(_slideDirection, slopeDirection);

            _slideSpeed += slopeDot * SlideSlopeMultiplier * Time.deltaTime;
            _slideSpeed = Mathf.Clamp(_slideSpeed, 0f, 50f);
        }

        /// <summary>
        /// End slide. Transition to crouch-walk with remaining velocity.
        /// </summary>
        private void EndSlide() {
            IsSliding = false;

            if(IsOwner && _audioRelay != null) {
                _audioRelay.RequestStop("foley.slide");
            }

            // Set remaining velocity (continues in slide direction at current speed)
            if(_slideSpeed > 0f) {
                _horizontalVelocity = _slideDirection * Mathf.Min(_slideSpeed, CrouchSpeed);
            }

            // Sync to network
            if(IsOwner && netIsSliding != null) {
                netIsSliding.Value = false;
            }

            // Sync animation state
            if (IsOwner && _animationController != null) {
                _animationController.SetSlidingServerRpc(false);
            }
        }

        /// <summary>
        /// Called from TryJump when slide-hopping.
        /// Preserves slide momentum into the jump.
        /// </summary>
        public void CancelSlideForJump() {
            if(!IsSliding) return;
            
            if(IsOwner && _audioRelay != null) {
                _audioRelay.RequestStop("foley.slide");
            }

            // Preserve full slide velocity for jump
            _horizontalVelocity = _slideDirection * _slideSpeed;
            IsSliding = false;

            // Sync to network
            if(IsOwner && netIsSliding != null) {
                netIsSliding.Value = false;
            }

            // Sync animation state
            if (IsOwner && _animationController != null) {
                _animationController.SetSlidingServerRpc(false);
            }
        }

        /// <summary>
        /// Called by GrappleController when grapple ends while grounded.
        /// Checks if slide should initiate based on current state.
        /// </summary>
        public void TryInitiateSlideFromGrapple() {
            if(!IsGrounded) return;
            if(!CrouchInput) return;
            if(IsSliding) return;

            var speed = _horizontalVelocity.magnitude;
            if(speed < SlideMinSpeed) return;

            // Initiate slide in grapple direction
            BeginSlide();
        }

        #endregion
    }
}
