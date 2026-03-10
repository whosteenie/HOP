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
        private const float SlideBaseFriction = 4f;     // Base deceleration during slide
        private const float SlideSpeedFriction = 0.15f; // Additional friction proportional to speed
        private const float SlideExitSpeed = 2.5f;      // Transition to crouch-walk below this speed
        private const float SlideSlopeMultiplier = 8f;  // How much slopes affect slide speed (higher = more gain downhill)
        private const float SlideDuration = 0.83f;      // ~50 frames at 60fps
        private float _slideTimer;
        [SerializeField] private float maxSlideGroundGap = 0.25f; // Keep slide active over small ground gaps.

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
        private const float GroundStickVelocity = -3f;          // Default grounded downward force
        private const float DownhillStickVelocity = -8f;        // Stronger stick only when moving downhill on a slope
        private const float DownhillStickMinSlopeAngle = 5f;    // Degrees; ignore nearly-flat surfaces
        private const float DownhillStickMinDot = 0.15f;        // How aligned we must be with downhill direction

        // Movement state
        private Vector3 _horizontalVelocity;
        private float _crouchTransition;
        private Vector3 _moveVelocity;
        private Vector3 _cachedFullVelocity;
        private Vector3 _lastIntendedDisplacement;
        private Vector3 _lastActualDisplacement;
        private CollisionFlags _lastMoveCollisionFlags;
        private bool _lastGroundedBeforeMove;
        private bool _lastGroundedAfterMove;
        private float _jumpInputSuppressedUntil;
        private const float JumpPadInputSuppressDuration = 0.12f;

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
        private float DeltaTime => playerController != null ? playerController.MovementSimulationDeltaTime : Time.deltaTime;

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

            if(playerController == null) return;
            netIsCrouching = playerController.NetIsCrouching;
            netIsSliding = playerController.netIsSliding;
        }

        /// <summary>
        /// Main movement update loop called by PlayerController.
        /// Handles wall running, sliding, and normal movement state transitions.
        /// </summary>
        public void UpdateMovement(CinemachineCamera fpCamera = null) {
            SimulateMovementInternal(GetHeadProbeOrigin(fpCamera));
        }

        public void SimulateAuthoritativeMovement() {
            SimulateMovementInternal(GetHeadProbeOrigin(null));
        }

        private void SimulateMovementInternal(Vector3 headProbeOrigin) {
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

            CheckCeilingHit(headProbeOrigin);
            ApplyGravity();
            MoveCharacter();

            CachedHorizontalSpeedSqr = _horizontalVelocity.sqrMagnitude;
        }

        /// <summary>
        /// Updates the player's crouch state, camera height, and collider height.
        /// </summary>
        public void UpdateCrouch(CinemachineCamera fpCamera) {
            SimulateCrouchFromInput(GetHeadProbeOrigin(fpCamera), fpCamera);
        }

        public void SimulateAuthoritativeCrouch(CinemachineCamera fpCamera = null) {
            SimulateCrouchFromInput(GetHeadProbeOrigin(null), fpCamera);
        }

        public void ApplyReplicatedCrouchState(CinemachineCamera fpCamera = null) {
            ApplyCrouchState(netIsCrouching != null && netIsCrouching.Value, fpCamera);
        }

        private void SimulateCrouchFromInput(Vector3 headProbeOrigin, CinemachineCamera fpCamera) {
            var sphereRadius = _characterController != null ? _characterController.radius : 0.3f;
            var headBlocked = Physics.SphereCast(
                headProbeOrigin,
                sphereRadius,
                Vector3.up,
                out _,
                StandCheckHeight,
                _obstacleMask
            );

            var isCurrentlyCrouched = _crouchTransition > 0.5f || netIsCrouching is { Value: true };

            bool targetCrouchState;
            if(CrouchInput) {
                targetCrouchState = true;
            } else {
                targetCrouchState = headBlocked && isCurrentlyCrouched;
            }

            var canWriteCrouchState =
                playerController != null &&
                (playerController.ShouldWriteMovementStateFromOwnerLocalPath() ||
                 playerController.ShouldWriteMovementStateFromServerAuthoritySlice()) &&
                netIsCrouching != null;
            if(canWriteCrouchState && netIsCrouching.Value != targetCrouchState) {
                if(Time.time - _lastCrouchUpdateTime >= CrouchUpdateInterval) {
                    netIsCrouching.Value = targetCrouchState;
                    _lastCrouchUpdateTime = Time.time;
                }
            }

            ApplyCrouchState(targetCrouchState, fpCamera);
        }

        private void ApplyCrouchState(bool isCrouching, CinemachineCamera fpCamera) {
            if(_animationController != null) {
                _animationController.SetCrouching(isCrouching);
            }

            var targetTransition = isCrouching ? 1f : 0f;
            _crouchTransition = Mathf.Lerp(_crouchTransition, targetTransition, 10f * DeltaTime);

            var targetCameraHeight = Mathf.Lerp(StandHeight, CrouchHeight, _crouchTransition);
            if(IsOwner && fpCamera != null) {
                if(playerController != null && playerController.LookController != null) {
                    playerController.LookController.SetCameraBaseLocalPosition(new Vector3(0f, targetCameraHeight, 0f));
                } else {
                    fpCamera.transform.localPosition = new Vector3(0f, targetCameraHeight, 0f);
                }
            }

            UpdateCharacterControllerCrouch(isCrouching);
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
                _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, targetVel, Acceleration * DeltaTime);
                return;
            }

            var motion = (_playerTransform.forward * MoveInput.y + _playerTransform.right * MoveInput.x).normalized;
            motion.y = 0f;

            if(IsGrounded) {
                ApplyFriction();
                ApplyDirectionChange(motion);

                var targetVelocity = motion.sqrMagnitude >= 0.1f ? motion * MaxSpeed : Vector3.zero;
                _horizontalVelocity =
                    Vector3.MoveTowards(_horizontalVelocity, targetVelocity, Acceleration * DeltaTime);
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

            var drop = speed * Friction * DeltaTime;
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

            var accelSpeed = AirAcceleration * DeltaTime;
            accelSpeed = Mathf.Min(accelSpeed, addSpeed);

            _horizontalVelocity += wishDir * accelSpeed;
        }

        /// <summary>
        /// Checks for ceiling collisions and stops vertical velocity.
        /// </summary>
        private void CheckCeilingHit(Vector3 headProbeOrigin) {
            if(_grappleController == null) return;

            var rayHit = Physics.Raycast(headProbeOrigin, Vector3.up, out _, 0.75f, _obstacleMask);
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
                // Keep existing flat-ground feel, but add a simple downhill "stick" to prevent
                // losing contact when sprinting down slopes.
                var stickVelocity = GroundStickVelocity;

                if(_horizontalVelocity.sqrMagnitude > 0.0001f &&
                   Physics.Raycast(_playerTransform.position,
                       Vector3.down,
                       out var hit,
                       _characterController.height * 0.6f + 0.2f,
                       _obstacleMask)) {
                    var slopeAngle = Vector3.Angle(hit.normal, Vector3.up);
                    if(slopeAngle >= DownhillStickMinSlopeAngle) {
                        var downhill = Vector3.ProjectOnPlane(Vector3.down, hit.normal).normalized;
                        var moveDir = _horizontalVelocity.normalized;
                        var downhillDot = Vector3.Dot(moveDir, downhill);
                        if(downhillDot >= DownhillStickMinDot) {
                            stickVelocity = DownhillStickVelocity;
                        }
                    }
                }

                VerticalVelocity = stickVelocity;
            } else {
                VerticalVelocity += _gravityY * GravityScale * DeltaTime;
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

            var tr = _playerTransform;
            var positionBefore = _playerTransform.position;
            _lastGroundedBeforeMove = IsGrounded;
            _lastIntendedDisplacement = _moveVelocity * DeltaTime;
            _lastMoveCollisionFlags = _characterController.Move(_lastIntendedDisplacement);
            var positionAfter = tr.position;
            _lastActualDisplacement = positionAfter - positionBefore;
            _lastGroundedAfterMove = IsGrounded;
            
            if (IsOwner && ProgressionManager.Instance != null) {
                if (IsGrounded) {
                    var dist = Vector3.Distance(new Vector3(positionBefore.x, 0, positionBefore.z), 
                                               new Vector3(positionAfter.x, 0, positionAfter.z));
                    if (dist > 0) {
                         ProgressionManager.Instance.AddDistanceTraveled(dist);
                    }
                } else {
                    ProgressionManager.Instance.RecordAirtime(DeltaTime);
                }
            }

            if(IsInJumpPadLaunch && VerticalVelocity <= 0f) {
                IsInJumpPadLaunch = false;
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
            var actualHorizontal = new Vector3(actualMove.x, 0f, actualMove.z) / DeltaTime;
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
                _wallContactTime += DeltaTime;

                if(!(_wallContactTime > WallContactThreshold)) return;
                // Grace period expired - dampen velocity toward actual movement
                _horizontalVelocity = Vector3.Lerp(
                    _horizontalVelocity,
                    actualHorizontal,
                    WallDampenRate * DeltaTime
                );
                    
                // Also dampen slide speed if currently sliding
                if (IsSliding) {
                    _slideSpeed = Mathf.Lerp(_slideSpeed, actualSpeed, WallDampenRate * DeltaTime);
                }
            } else {
                // Not blocked - reset timer
                _wallContactTime = 0f;
            }
        }

        private void UpdateCharacterControllerCrouch(bool isCrouching) {
            var targetTransition = isCrouching ? 1f : 0f;
            if(!IsOwner) {
                _crouchTransition = Mathf.Lerp(_crouchTransition, targetTransition, 10f * DeltaTime);
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
        public void TryJump(float height = JumpHeight) {
            if(Time.time < _jumpInputSuppressedUntil) {
                return;
            }

            if(!IsGrounded) {
                if(_wallRunController == null || !_wallRunController.IsWallRunning) return;
                _wallRunController.WallJump();
                return;
            }

            if(IsSliding) {
                CancelSlideForJump();
            }

            if(IsOwner && _audioRelay != null) {
                _audioRelay.RequestPlayAttached("foley.tile.jump.start", new NetworkObjectReference(playerController.NetworkObject),
                    allowOverlap: true);
            }

            VerticalVelocity = Mathf.Sqrt(height * -2f * _gravityY * GravityScale);

            if(!(VerticalVelocity > 0f)) return;

            if(IsOwner && playerController != null) {
                var weaponBob = FindActiveWeaponBob();

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
        /// <param name="ignoreGroundedRequirement"></param>
        public void LaunchFromJumpPad(Vector3 normal, float force = 15f, bool ignoreGroundedRequirement = false) {
            if(!ignoreGroundedRequirement && !IsGrounded) {
                return;
            }

            // Prevent held jump input from immediately overwriting jump-pad launch velocity.
            _jumpInputSuppressedUntil = Time.time + JumpPadInputSuppressDuration;

            if(IsSliding) {
                CancelSlideForJump();
            }

            normal = normal.normalized;
            var velocityMagnitude = Mathf.Sqrt(force * -2f * _gravityY * GravityScale);
            var launchVelocity = normal * velocityMagnitude;

            VerticalVelocity = launchVelocity.y;
            _horizontalVelocity += new Vector3(launchVelocity.x, 0f, launchVelocity.z);

            IsInJumpPadLaunch = true;

            if(IsOwner && _audioRelay != null) {
                // Networked jumppad + jump foley, as before.
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
            if(IsOwner && playerController != null) {
                var weaponBob = FindActiveWeaponBob();
                if(weaponBob != null) {
                    weaponBob.OnJumpInitiated();
                } else {
                    Debug.LogWarning(
                        "[PlayerMovementController] LaunchFromJumpPad: WeaponBob not found! " +
                        $"FpCamera={playerController.FpCamera != null} WeaponCamera={playerController.WeaponCamera != null}");
                }
            }

            if(_animationController)
                _animationController.PlayJumpAnimationServerRpc();
        }

        private WeaponBob FindActiveWeaponBob() {
            if(playerController == null) return null;

            var fpCamera = playerController.FpCamera;
            if(fpCamera != null) {
                var fpBob = fpCamera.GetComponentInChildren<WeaponBob>();
                if(fpBob != null) return fpBob;
            }

            var weaponCamera = playerController.WeaponCamera;
            return weaponCamera != null ? weaponCamera.GetComponentInChildren<WeaponBob>() : null;
        }

        /// <summary>
        /// Resets player velocity and vertical speed to zero.
        /// </summary>
        public void ResetVelocity() {
            _horizontalVelocity = Vector3.zero;
            VerticalVelocity = 0f;
        }

        public void SetMantling(bool isMantling) {
            _isMantling = isMantling;
            if(_isMantling) {
                ResetVelocity();
            }
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

        // Used by grapple/jump-pad interactions to identify an active pad-launch phase.
        public bool IsInJumpPadLaunch { get; private set; }
        public Vector3 LastIntendedDisplacement => _lastIntendedDisplacement;
        public Vector3 LastActualDisplacement => _lastActualDisplacement;
        public CollisionFlags LastMoveCollisionFlags => _lastMoveCollisionFlags;
        public bool LastGroundedBeforeMove => _lastGroundedBeforeMove;
        public bool LastGroundedAfterMove => _lastGroundedAfterMove;

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

            if(playerController != null &&
               (playerController.ShouldWriteMovementStateFromOwnerLocalPath() ||
                playerController.ShouldWriteMovementStateFromServerAuthoritySlice()) &&
               netIsSliding != null) {
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

            if(!IsGrounded && !IsGroundWithinSlideGap()) {
                // Preserve full momentum when sliding off ledge
                CancelSlideForJump();
                return;
            }

            // Apply proportional friction (faster slides slow down faster)
            var friction = SlideBaseFriction + _slideSpeed * SlideSpeedFriction;
            _slideSpeed -= friction * DeltaTime;

            // Apply slope influence
            ApplySlopeToSlide();

            // Increment timer and check duration
            _slideTimer += DeltaTime;
            if (_slideTimer >= SlideDuration) {
                EndSlide();
                return;
            }

            // Update horizontal velocity to match slide
            _horizontalVelocity = _slideDirection * _slideSpeed;

            // MaxSpeed is crouch speed during slide (for animation purposes)
            MaxSpeed = CrouchSpeed;
        }

        private bool IsGroundWithinSlideGap() {
            if(_characterController == null || playerController == null) return false;

            var bounds = _characterController.bounds;
            var feet = bounds.center;
            feet.y = bounds.min.y;

            const float probeStartOffset = 0.08f;
            var probeDistance = probeStartOffset + Mathf.Max(0f, maxSlideGroundGap);
            var probeOrigin = feet + Vector3.up * probeStartOffset;

            return Physics.Raycast(
                probeOrigin,
                Vector3.down,
                out _,
                probeDistance,
                playerController.WorldLayer,
                QueryTriggerInteraction.Ignore);
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

            _slideSpeed += slopeDot * SlideSlopeMultiplier * DeltaTime;
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
            if(playerController != null &&
               (playerController.ShouldWriteMovementStateFromOwnerLocalPath() ||
                playerController.ShouldWriteMovementStateFromServerAuthoritySlice()) &&
               netIsSliding != null) {
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
            if(playerController != null &&
               (playerController.ShouldWriteMovementStateFromOwnerLocalPath() ||
                playerController.ShouldWriteMovementStateFromServerAuthoritySlice()) &&
               netIsSliding != null) {
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

        private Vector3 GetHeadProbeOrigin(CinemachineCamera fpCamera) {
            if(fpCamera != null) {
                return fpCamera.transform.position;
            }

            var cameraHeight = Mathf.Lerp(StandHeight, CrouchHeight, _crouchTransition);
            if(_characterController != null) {
                var worldCenter = _characterController.transform.TransformPoint(_characterController.center);
                return new Vector3(worldCenter.x, _characterController.transform.position.y + cameraHeight, worldCenter.z);
            }

            if(_playerTransform != null) {
                return _playerTransform.position + Vector3.up * cameraHeight;
            }

            return transform.position + Vector3.up * cameraHeight;
        }

        #endregion
    }
}
