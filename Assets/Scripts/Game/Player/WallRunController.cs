using Network.Rpc;
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
        public Vector3 WallNormal { get; private set; }
        public bool IsWallLeft { get; private set; }

        private float _wallRunTimer;
        private RaycastHit _wallHit;
        private float _currentWallRunSpeed;
        private float _originalGravity;
        private float _jumpCooldownTimer;

        // Throttling for network
        private readonly NetworkVariable<bool> _netIsWallRunning = new();

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if (playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if (playerController != null) {
                _characterController = playerController.CharacterController;
                _fpCamera = playerController.FpCamera;
                _movementController = playerController.MovementController;
                // Default to world layer if not set
                 if (wallLayer == 0) wallLayer = playerController.WorldLayer;
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            _netIsWallRunning.OnValueChanged += OnWallRunStateChanged;
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            _netIsWallRunning.OnValueChanged -= OnWallRunStateChanged;
        }

        private void OnWallRunStateChanged(bool previousValue, bool newValue) {
             // Can be used for third-person animations later
        }
        
        public void CheckForWall() {
            if (!IsOwner) return;

             // Start conditions: Airborne, moving fast enough (checked by movement controller ideally, or here)
            if (_characterController.isGrounded) {
                if (IsWallRunning) StopWallRun();
                return;
            }

            // Cooldown check
            if (_jumpCooldownTimer > 0f) {
                _jumpCooldownTimer -= Time.deltaTime;
                return;
            }
            
            // Check for wall
            // Debug visualization
            if (IsOwner) {
                Debug.DrawRay(transform.position, -transform.right * wallDistanceCheck, Color.red);
                Debug.DrawRay(transform.position, transform.right * wallDistanceCheck, Color.blue);
            }

            if (Physics.Raycast(transform.position, -transform.right, out var leftHit, wallDistanceCheck, wallLayer)) {
                IsWallLeft = true;
                _wallHit = leftHit;
            } else if (Physics.Raycast(transform.position, transform.right, out var rightHit, wallDistanceCheck, wallLayer)) {
                IsWallLeft = false;
                _wallHit = rightHit;
            } else {
                IsWallLeft = false;
                if (IsWallRunning) StopWallRun();
                return;
            }

            if (IsWallRunning) {
                // If already running, we don't need to re-check the angle or height strictly every frame
                // We just need to make sure we are still close enough to the wall (raycast check above covers this)
                // However, if we want to allow 360 aiming, we shouldn't call StopWallRun just because CanWallRun returns false due to angle.
                
                // We only stop if we lost the wall raycast (already handled above in the raycast block)
                 return;
            }

            if (CanWallRun()) {
                if (!IsWallRunning) {
                     // Input Gate: Require holding jump to attach (allows intentionality without needing movement input)
                     if (playerController.PlayerInput != null && !playerController.PlayerInput.IsJumpHeld) {
                         return;
                     }

                     // Speed Gate: Only start wall run if moving fast enough (near sprint speed)
                     // This prevents accidental wall runs when just jumping near a wall from standstill
                     if (_movementController.HorizontalVelocity.magnitude < minWallRunSpeed) {
                         return;
                     }

                     // Debug.Log($"[WallRun] Starting wall run! Wall: {_wallHit.collider.name}");
                     StartWallRun();
                }
            } else {
                 // For fresh entry, failed checks mean no wall run
                 // But we already handled the "IsWallRunning" case above, so this block is only for !IsWallRunning
                 // No action needed
            }
        }

        private bool CanWallRun() {
            // vertical check - ensure we are off the ground
            if (Physics.Raycast(transform.position, Vector3.down, minWallRunHeight, wallLayer)) {
                // Debug.Log("[WallRun] Too close to ground");
                return false;
            }

            WallNormal = _wallHit.normal;
            
            // Angle check
            // Prevent wall running if we are facing the wall too directly
            float angle = Vector3.Angle(transform.forward, -WallNormal);
            if (angle < wallRunAngleThreshold || angle > 180f - wallRunAngleThreshold) {
                 // Debug.Log($"[WallRun] Bad angle: {angle}");
                 return false;
            }

            return true;
        }


        private void StartWallRun() {
            IsWallRunning = true;
            _wallRunTimer = maxWallRunTime;
            
            // Capture entry speed, maintain momentum if faster than base speed
            float entrySpeed = _movementController.HorizontalVelocity.magnitude;
            _currentWallRunSpeed = Mathf.Max(wallRunSpeed, entrySpeed);

            if (IsOwner) {
                UpdateNetworkStateServerRpc(true);
            }
            
            // Apply Camera Tilt
             if (_fpCamera != null && playerController.LookController != null) {
                  float tilt = IsWallLeft ? -wallRunCameraTilt : wallRunCameraTilt;
                  playerController.LookController.SetTargetTilt(tilt);
             }
        }

        private void StopWallRun() {
            IsWallRunning = false;
            
             if (IsOwner) {
                UpdateNetworkStateServerRpc(false);
            }

            // Reset Camera Tilt
            if (_fpCamera != null && playerController.LookController != null) {
                playerController.LookController.SetTargetTilt(0f);
            }
        }

        public void UpdateWallRun() {
            if (!IsWallRunning) return;



            _wallRunTimer -= Time.deltaTime;
            if (_wallRunTimer <= 0) {
                 StopWallRun();
                 return;
            }

            _wallRunTimer -= Time.deltaTime;
            if (_wallRunTimer <= 0) {
                 StopWallRun();
                 return;
            }

            // Stuck/Bonk Check (Auto-Mantle)
            // If we are moving significantly slower than our target speed, we hit something.
            // Wait a small buffer (0.2s) at start to allow acceleration (though we snap speed instantly usually)
            if (_wallRunTimer < maxWallRunTime - 0.1f) {
                Vector3 actualVelocity = _characterController.velocity;
                float actualSpeed = new Vector3(actualVelocity.x, 0, actualVelocity.z).magnitude;
                
                // If we drop below 2m/s (Arbitrary low value implies stop), we hit a wall/ledge
                if (actualSpeed < 2f) {
                     // Debug.Log($"[WallRun] Bonk detected! Speed: {actualSpeed}");
                     
                     Vector3 desiredDir = GetWallRunVelocity(transform.forward).normalized;
                     
                     if (playerController.MantleController != null && playerController.MantleController.TryMantle(desiredDir)) {
                         StopWallRun();
                         return;
                     } else {
                         StopWallRun();
                         return;
                     }
                }
            }
        }

        public Vector3 GetWallRunVelocity(Vector3 currentForward) {
             // Calculate direction along the wall
             Vector3 wallForward = Vector3.Cross(WallNormal, Vector3.up);
             
             // Determine which way along the wall matches our current forward
             if (Vector3.Dot(wallForward, transform.forward) < 0) {
                 wallForward = -wallForward;
             }
             
             return wallForward * _currentWallRunSpeed;
        }

        public void WallJump() {
             if (!IsWallRunning) return;

             StopWallRun(); // End state immediately
             
             // Apply cooldown to prevent immediate re-attachment
             _jumpCooldownTimer = wallJumpCooldown;

             // Calculate jump direction
             // Vector3 wallNormal = IsWallLeft ? transform.right : -transform.right; // Approximate normal based on side
             // Or use actual hit normal:
             // Vector3 jumpDir = (Vector3.up * wallJumpUpForce + WallNormal * wallJumpSideForce).normalized;
             
             // Combined jump force
             // Add forward momentum from wall run
             Vector3 forwardVelocity = GetWallRunVelocity(transform.forward);
             Vector3 jumpVelocity = (WallNormal * wallJumpSideForce) + (Vector3.up * wallJumpUpForce) + forwardVelocity;
             
             // Apply to movement controller
             if (_movementController != null) {
                 _movementController.SetVelocity(new Vector3(jumpVelocity.x, 0, jumpVelocity.z));
                 _movementController.VerticalVelocity = jumpVelocity.y;
             }
        }

        [Rpc(SendTo.Server)]
        private void UpdateNetworkStateServerRpc(bool isWallRunning) {
            _netIsWallRunning.Value = isWallRunning;
        }
    }
}
