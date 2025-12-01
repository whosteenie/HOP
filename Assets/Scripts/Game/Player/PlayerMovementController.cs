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

        // Input (read from PlayerController)
        private Vector2 MoveInput => playerController == null ? Vector2.zero : playerController.moveInput;

        private bool SprintInput => playerController != null && playerController.sprintInput;

        private bool CrouchInput => playerController != null && playerController.crouchInput;

        // Network state (from PlayerController)
        public NetworkVariable<bool> netIsCrouching;

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

            // Get network variable from PlayerController (network-dependent)
            if(playerController != null) {
                netIsCrouching = playerController.NetIsCrouching;
            }
        }

        public void UpdateMovement(CinemachineCamera fpCamera = null) {
            if(_isMantling || (_swingGrapple != null && _swingGrapple.IsSwinging)) {
                return;
            }

            UpdateMaxSpeed();
            CalculateHorizontalVelocity();
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
            _characterController.Move(_moveVelocity * Time.deltaTime);
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
    }
}