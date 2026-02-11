using Game.Settings;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class WallRunController : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private CharacterController _characterController;
        private CinemachineCamera _fpCamera;
        private PlayerMovementController _movementController;

        [Header("Wall Running Settings")]
        [SerializeField] private float wallRunSpeed = 12f;

        [SerializeField] private float maxWallRunTime = 3f;
        [SerializeField] private float wallJumpUpForce = 12f;
        [SerializeField] private float wallJumpSideForce = 10f;
        [SerializeField] private float wallRunCameraTilt = 10f;
        [SerializeField] private float wallDistanceCheck = 1f;
        [SerializeField] private float minWallRunHeight = 1.5f;
        [SerializeField] private LayerMask wallLayer;

        [Header("Detection")]
        [SerializeField] private float wallRunAngleThreshold = 20f; // Limit angle to prevent running straight into wall

        [SerializeField] private float wallJumpCooldown = 0.35f;
        [SerializeField] private float minWallRunSpeed = 9f; // Slightly below SprintSpeed (10f)

        public bool IsWallRunning { get; private set; }
        private Vector3 WallNormal { get; set; }
        private bool IsWallLeft { get; set; }

        private float _wallRunTimer;
        private RaycastHit _wallHit;
        private float _currentWallRunSpeed;
        private float _originalGravity;
        private float _jumpCooldownTimer;

        // Throttling for network


        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) return;
            _characterController = playerController.CharacterController;
            _fpCamera = playerController.FpCamera;
            _movementController = playerController.MovementController;
            // Default to world layer if not set
            if(wallLayer == 0) wallLayer = playerController.WorldLayer;
        }

        /// <summary>
        /// Checks for surrounding walls and initiates or stops a wall run.
        /// </summary>
        public void CheckForWall() {
            if(!IsOwner) return;

            if(_characterController.isGrounded) {
                if(IsWallRunning) StopWallRun();
                return;
            }

            if(_jumpCooldownTimer > 0f) {
                _jumpCooldownTimer -= Time.deltaTime;
                return;
            }

            if(Physics.Raycast(transform.position, -transform.right, out var leftHit, wallDistanceCheck, wallLayer)) {
                IsWallLeft = true;
                _wallHit = leftHit;
            } else if(Physics.Raycast(transform.position, transform.right, out var rightHit, wallDistanceCheck,
                          wallLayer)) {
                IsWallLeft = false;
                _wallHit = rightHit;
            } else {
                IsWallLeft = false;
                if(IsWallRunning) StopWallRun();
                return;
            }

            if(IsWallRunning) {
                return;
            }

            if(!CanWallRun() || IsWallRunning) return;
            var canInitiate = GameSettings.Data.controls.autoWallRun;
            if(!canInitiate && playerController.PlayerInput != null && playerController.PlayerInput.IsJumpHeld) {
                canInitiate = true;
            }

            if(!canInitiate) {
                return;
            }

            if(_movementController.HorizontalVelocity.magnitude < minWallRunSpeed) {
                return;
            }

            StartWallRun();
        }

        private bool CanWallRun() {
            // vertical check - ensure we are off the ground
            if(Physics.Raycast(transform.position, Vector3.down, minWallRunHeight, wallLayer)) {
                // Debug.Log("[WallRun] Too close to ground");
                return false;
            }

            WallNormal = _wallHit.normal;

            // Angle check
            // Prevent wall running if we are facing the wall too directly
            var angle = Vector3.Angle(transform.forward, -WallNormal);
            return !(angle < wallRunAngleThreshold) && !(angle > 180f - wallRunAngleThreshold);
        }


        private void StartWallRun() {
            IsWallRunning = true;
            _wallRunTimer = maxWallRunTime;

            // Capture entry speed, maintain momentum if faster than base speed
            var entrySpeed = _movementController.HorizontalVelocity.magnitude;
            _currentWallRunSpeed = Mathf.Max(wallRunSpeed, entrySpeed);


            // Apply Camera Tilt
            if(_fpCamera == null || playerController.LookController == null) return;
            var tilt = IsWallLeft ? -wallRunCameraTilt : wallRunCameraTilt;
            playerController.LookController.SetTargetTilt(tilt);
        }

        private void StopWallRun() {
            IsWallRunning = false;


            // Reset Camera Tilt
            if(_fpCamera != null && playerController.LookController != null) {
                playerController.LookController.SetTargetTilt(0f);
            }
        }

        /// <summary>
        /// Updates the active wall run state, handling timers and speed checks.
        /// </summary>
        public void UpdateWallRun() {
            if(!IsWallRunning) return;

            _wallRunTimer -= Time.deltaTime;
            if(_wallRunTimer <= 0) {
                StopWallRun();
                return;
            }

            if(!(_wallRunTimer < maxWallRunTime - 0.1f)) return;
            var actualVelocity = _characterController.velocity;
            var actualSpeed = new Vector3(actualVelocity.x, 0, actualVelocity.z).magnitude;

            if(!(actualSpeed < 2f)) return;
            var desiredDir = GetWallRunVelocity(transform.forward).normalized;

            if(playerController.MantleController != null &&
               playerController.MantleController.TryMantle(desiredDir)) {
                StopWallRun();
            } else {
                StopWallRun();
            }
        }

        /// <summary>
        /// Calculates the velocity vector for a wall run based on the wall normal.
        /// </summary>
        public Vector3 GetWallRunVelocity(Vector3 currentForward) {
            // Calculate direction along the wall plane.
            var wallForward = Vector3.Cross(WallNormal, Vector3.up);
            if(wallForward.sqrMagnitude < 0.0001f) {
                return Vector3.zero;
            }
            wallForward.Normalize();

            // Choose wallrun direction from actual movement first, not look direction.
            // This prevents backward wallrun attempts from flipping velocity the wrong way.
            var planarVelocity = GetPlanarVelocity();
            Vector3 referenceDirection;
            if(planarVelocity.sqrMagnitude > 0.01f) {
                referenceDirection = planarVelocity.normalized;
            } else {
                referenceDirection = GetPreferredDirection(currentForward);
            }

            if(Vector3.Dot(wallForward, referenceDirection) < 0f) {
                wallForward = -wallForward;
            }

            return wallForward * _currentWallRunSpeed;
        }

        private Vector3 GetPlanarVelocity() {
            if(_movementController == null) {
                return Vector3.zero;
            }

            var v = _movementController.HorizontalVelocity;
            v.y = 0f;
            return v;
        }

        private Vector3 GetPreferredDirection(Vector3 fallbackForward) {
            if(playerController != null) {
                var moveInput = playerController.moveInput;
                if(moveInput.sqrMagnitude > 0.01f) {
                    var basis = playerController.PlayerTransform != null ? playerController.PlayerTransform : transform;
                    var inputDirection = basis.forward * moveInput.y + basis.right * moveInput.x;
                    inputDirection.y = 0f;
                    if(inputDirection.sqrMagnitude > 0.0001f) {
                        return inputDirection.normalized;
                    }
                }
            }

            fallbackForward.y = 0f;
            if(fallbackForward.sqrMagnitude > 0.0001f) {
                return fallbackForward.normalized;
            }

            var worldForward = transform.forward;
            worldForward.y = 0f;
            return worldForward.sqrMagnitude > 0.0001f ? worldForward.normalized : Vector3.forward;
        }

        /// <summary>
        /// Performs a wall jump, applying forces away from the wall and upward.
        /// </summary>
        public void WallJump() {
            if(!IsWallRunning) return;

            StopWallRun(); // End state immediately

            // Apply cooldown to prevent immediate re-attachment
            _jumpCooldownTimer = wallJumpCooldown;

            // Calculate jump direction
            // Vector3 wallNormal = IsWallLeft ? transform.right : -transform.right; // Approximate normal based on side
            // Or use actual hit normal:
            // Vector3 jumpDir = (Vector3.up * wallJumpUpForce + WallNormal * wallJumpSideForce).normalized;

            // Combined jump force
            // Add forward momentum from wall run
            var forwardVelocity = GetWallRunVelocity(transform.forward);
            var jumpVelocity = (WallNormal * wallJumpSideForce) + (Vector3.up * wallJumpUpForce) + forwardVelocity;

            // Apply to movement controller
            if(_movementController == null) return;
            _movementController.SetVelocity(new Vector3(jumpVelocity.x, 0, jumpVelocity.z));
            _movementController.VerticalVelocity = jumpVelocity.y;
        }
    }
}
