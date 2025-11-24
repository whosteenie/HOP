using Network;
using Network.Rpc;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Handles all movement-related logic for the player.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerMovementController : NetworkBehaviour {
        [Header("References")] [SerializeField]
        private PlayerController playerController;

        [SerializeField] private CharacterController characterController;
        [SerializeField] private GrappleController grappleController;
        [SerializeField] private SwingGrapple swingGrapple;
        [SerializeField] private PlayerAnimationController animationController;
        [SerializeField] private NetworkSfxRelay sfxRelay;
        [SerializeField] private Transform tr;
        [SerializeField] private LayerMask worldLayer;
        [SerializeField] private LayerMask enemyLayer;

        [Header("Movement Parameters")] [SerializeField]
        private float acceleration = 15f;

        [SerializeField] private float airAcceleration = 50f;
        [SerializeField] private float maxAirSpeed = 5f;
        [SerializeField] private float friction = 8f;

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

        // Movement state
        private float _maxSpeed = WalkSpeed;
        private Vector3 _horizontalVelocity;
        private float _verticalVelocity;
        private float _crouchTransition;
        private Vector3 _moveVelocity;
        private float _cachedHorizontalSpeedSqr;
        private Vector3 _cachedFullVelocity;

        // Physics
        private int _obstacleMask;
        private float _gravityY;
        private bool _isMantling;

        // Input (read from PlayerController)
        private Vector2 moveInput => playerController != null ? playerController.moveInput : Vector2.zero;
        private bool sprintInput => playerController != null && playerController.sprintInput;
        private bool crouchInput => playerController != null && playerController.crouchInput;

        // Network state (from PlayerController)
        public NetworkVariable<bool> netIsCrouching;
        
        // Throttling for crouch updates (at 90Hz: 2 ticks = ~22ms)
        private float _lastCrouchUpdateTime;
        private const float CrouchUpdateInterval = 0.022f; // ~2 ticks at 90Hz

        private void Awake() {
            // Initialize transform reference
            if(tr == null) {
                tr = transform;
            }

            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(characterController == null) {
                characterController = GetComponent<CharacterController>();
            }

            if(grappleController == null) {
                grappleController = GetComponent<GrappleController>();
            }

            if(swingGrapple == null) {
                swingGrapple = GetComponent<SwingGrapple>();
            }

            if(animationController == null) {
                animationController = GetComponent<PlayerAnimationController>();
            }

            if(sfxRelay == null) {
                sfxRelay = GetComponent<NetworkSfxRelay>();
            }

            // Initialize physics (non-network dependent)
            _obstacleMask = worldLayer | enemyLayer;
            _gravityY = Physics.gravity.y;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(characterController == null) {
                characterController = GetComponent<CharacterController>();
            }

            // Get network variable from PlayerController (network-dependent)
            if(playerController != null) {
                netIsCrouching = playerController.netIsCrouching;
            }
        }

        public void UpdateMovement(CinemachineCamera fpCamera = null) {
            if(_isMantling || (swingGrapple != null && swingGrapple.IsSwinging)) {
                return;
            }

            UpdateMaxSpeed();
            CalculateHorizontalVelocity();
            CheckCeilingHit(fpCamera);
            ApplyGravity();
            MoveCharacter();

            // Cache horizontal speed for animation/sound
            _cachedHorizontalSpeedSqr = _horizontalVelocity.sqrMagnitude;
        }

        public void UpdateCrouch(CinemachineCamera fpCamera) {
            if(fpCamera == null) return;

            float sphereRadius = characterController != null ? characterController.radius : 0.3f;
            bool headBlocked = Physics.SphereCast(
                fpCamera.transform.position,
                sphereRadius,
                Vector3.up,
                out _,
                StandCheckHeight,
                _obstacleMask
            );

            bool isCurrentlyCrouched = _crouchTransition > 0.5f || (netIsCrouching != null && netIsCrouching.Value);

            bool targetCrouchState;
            if(crouchInput) {
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

            if(animationController != null) {
                animationController.SetCrouching(targetCrouchState);
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
            if(crouchInput) {
                _maxSpeed = CrouchSpeed;
            } else if(sprintInput) {
                _maxSpeed = SprintSpeed;
            } else {
                _maxSpeed = WalkSpeed;
            }
        }

        private void CalculateHorizontalVelocity() {
            // Block movement during pre-match (but allow input to be set so it feels responsive when match starts)
            if(Network.Singletons.GameMenuManager.Instance != null && 
               Network.Singletons.GameMenuManager.Instance.IsPreMatch) {
                // Still apply friction to slow down if already moving
                ApplyFriction();
                var targetVelocity = Vector3.zero;
                _horizontalVelocity =
                    Vector3.MoveTowards(_horizontalVelocity, targetVelocity, acceleration * Time.deltaTime);
                return;
            }

            var motion = (tr.forward * moveInput.y + tr.right * moveInput.x).normalized;
            motion.y = 0f;

            if(IsGrounded) {
                ApplyFriction();
                ApplyDirectionChange(motion);

                var targetVelocity = motion.sqrMagnitude >= 0.1f ? motion * _maxSpeed : Vector3.zero;
                _horizontalVelocity =
                    Vector3.MoveTowards(_horizontalVelocity, targetVelocity, acceleration * Time.deltaTime);
            } else {
                AirStrafe(motion);
            }
        }

        private void ApplyFriction() {
            if(moveInput.sqrMagnitude >= 0.01f) return;

            var speed = _horizontalVelocity.magnitude;
            if(speed < 0.001f) return;

            var drop = speed * friction * Time.deltaTime;
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
            if(moveInput.sqrMagnitude < 0.01f) return;

            var currentSpeed = Vector3.Dot(_horizontalVelocity, wishDir);
            var addSpeed = maxAirSpeed - currentSpeed;

            if(addSpeed <= 0) return;

            var accelSpeed = airAcceleration * Time.deltaTime;
            accelSpeed = Mathf.Min(accelSpeed, addSpeed);

            _horizontalVelocity += wishDir * accelSpeed;
        }

        private void CheckCeilingHit(CinemachineCamera fpCamera) {
            if(fpCamera == null) return;

            var rayHit = Physics.Raycast(fpCamera.transform.position, Vector3.up, out _, 0.75f, _obstacleMask);
            if(rayHit && _verticalVelocity > 0f) {
                if(grappleController != null) {
                    grappleController.CancelGrapple();
                }

                _verticalVelocity = 0f;
            }
        }

        private void ApplyGravity() {
            if(IsGrounded && _verticalVelocity <= 0.01f) {
                _verticalVelocity = -3f;
            } else {
                _verticalVelocity += _gravityY * GravityScale * Time.deltaTime;
            }
        }

        private void MoveCharacter() {
            _moveVelocity.x = _horizontalVelocity.x;
            _moveVelocity.y = _verticalVelocity;
            _moveVelocity.z = _horizontalVelocity.z;
            characterController.Move(_moveVelocity * Time.deltaTime);
        }

        private void UpdateCharacterControllerCrouch(bool isCrouching) {
            var targetTransition = isCrouching ? 1f : 0f;
            if(!IsOwner) {
                _crouchTransition = Mathf.Lerp(_crouchTransition, targetTransition, 10f * Time.deltaTime);
            }

            var targetColliderHeight = Mathf.Lerp(StandCollider, CrouchCollider, _crouchTransition);
            var centerY = targetColliderHeight / 2f;
            characterController.height = targetColliderHeight;
            characterController.center = new Vector3(0f, centerY, 0f);
        }

        public void TryJump(float height = JumpHeight) {
            if(!IsGrounded) {
                return;
            }

            // Check for jump pads (regular or mega)
            var jumpPadHeight = CheckForJumpPad();
            if(jumpPadHeight > 0f) {
                height = jumpPadHeight;
            }

            if(IsOwner) {
                var key = Mathf.Approximately(height, 15f) || Mathf.Approximately(height, 30f) ? "jumpPad" : "jump";

                if(key == "jumpPad") {
                    sfxRelay?.RequestWorldSfx(SfxKey.JumpPad, attachToSelf: true, true);
                }

                sfxRelay?.RequestWorldSfx(SfxKey.Jump, attachToSelf: true, true);
            }

            // Calculate and apply vertical velocity for jump
            _verticalVelocity = Mathf.Sqrt(height * -2f * _gravityY * GravityScale);

            // Ensure velocity is positive (upward) before triggering jump animation
            // This guarantees the jump animation only triggers when velocity is actually applied upward
            if(_verticalVelocity > 0f) {
                if(animationController != null) {
                    animationController.PlayJumpAnimationServerRpc();
                }
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

            // Normalize the normal to ensure consistent force
            normal = normal.normalized;

            // Calculate the velocity magnitude equivalent to the jump height
            // This matches the calculation in TryJump: sqrt(height * -2 * gravity * gravityScale)
            float velocityMagnitude = Mathf.Sqrt(force * -2f * _gravityY * GravityScale);

            // Apply velocity in the direction of the pad's normal
            // This gives us the full boost vector (horizontal + vertical components)
            Vector3 launchVelocity = normal * velocityMagnitude;
            
            // Always apply the full velocity boost in the pad's normal direction
            // For vertical pads (normal = up): only vertical component, horizontal preserved
            // For angled/wall pads: both horizontal and vertical components added
            _verticalVelocity = launchVelocity.y;
            _horizontalVelocity += new Vector3(launchVelocity.x, 0f, launchVelocity.z);

            // Play jump pad sound
            if(IsOwner) {
                sfxRelay?.RequestWorldSfx(SfxKey.JumpPad, attachToSelf: true, true);
                sfxRelay?.RequestWorldSfx(SfxKey.Jump, attachToSelf: true, true);
            }

            // Trigger jump animation if moving upward
            if(_verticalVelocity > 0f) {
                if(animationController != null) {
                    animationController.PlayJumpAnimationServerRpc();
                }
            }
        }

        private float CheckForJumpPad() {
            if(Physics.Raycast(tr.position, Vector3.down, out var hit, characterController.height * 0.6f)) {
                if(hit.collider.CompareTag("JumpPad")) {
                    return 15f; // Regular jump pad height
                } else if(hit.collider.CompareTag("MegaPad")) {
                    return 30f; // Mega jump pad height
                }
            }

            return 0f; // No jump pad found
        }

        public void ResetVelocity() {
            _horizontalVelocity = Vector3.zero;
            _verticalVelocity = 0f;
        }

        public void SetVelocity(Vector3 horizontalVelocity) {
            _horizontalVelocity = new Vector3(horizontalVelocity.x, 0f, horizontalVelocity.z);
        }

        public void AddVerticalVelocity(float verticalBoost) {
            _verticalVelocity += verticalBoost;
        }

        public void SetMantling(bool mantling) {
            _isMantling = mantling;
        }

        // Public getters
        public bool IsGrounded => characterController != null && characterController.isGrounded;

        public Vector3 CurrentFullVelocity {
            get {
                _cachedFullVelocity.x = _horizontalVelocity.x;
                _cachedFullVelocity.y = _verticalVelocity;
                _cachedFullVelocity.z = _horizontalVelocity.z;
                return _cachedFullVelocity;
            }
        }

        public Vector3 HorizontalVelocity => _horizontalVelocity;
        public float VerticalVelocity => _verticalVelocity;
        public float MaxSpeed => _maxSpeed;
        public float CachedHorizontalSpeedSqr => _cachedHorizontalSpeedSqr;
    }
}