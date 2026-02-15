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

        [Header("Curved Wall Continuation")]
        [SerializeField] private bool enableCurvedWallContinuation = true;
        [SerializeField] private bool continuationOnlyOnDetach = true;
        [SerializeField] private float continuationProbeForwardOffset = 0.35f;
        [SerializeField] private float continuationProbeRadius = 0.12f;
        [SerializeField] private float continuationMaxNormalDelta = 55f;
        [SerializeField] private float continuationGraceTime = 0.08f;
        [SerializeField] private float wallNormalBlendSpeed = 16f;
        [SerializeField] private float sideSwitchCooldown = 0.08f;
        [SerializeField] private bool wallRunContinuationDebugLogs;

        public bool IsWallRunning { get; private set; }
        public bool IsRightWallRun {
            get {
                if(!IsWallRunning) return false;
                if(WallNormal.sqrMagnitude > 0.0001f) {
                    // View-relative side detection for animation: flips when camera yaw crosses the wall side.
                    return Vector3.Dot(transform.right, WallNormal.normalized) < 0f;
                }

                return !IsWallLeft;
            }
        }
        private Vector3 WallNormal { get; set; }
        private bool IsWallLeft { get; set; }

        private float _wallRunTimer;
        private RaycastHit _wallHit;
        private float _currentWallRunSpeed;
        private float _originalGravity;
        private float _jumpCooldownTimer;
        private float _continuationGraceTimer;
        private float _sideSwitchCooldownTimer;
        private Vector3 _targetWallNormal;
        private Vector3 _lastWallRunDirection;

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

            if(IsWallRunning) {
                MaintainOrTransferWallRun();
                return;
            }

            if(TryFindInitialWallHit(out var initialHit, out var initialIsLeft)) {
                IsWallLeft = initialIsLeft;
                _wallHit = initialHit;
            } else {
                IsWallLeft = false;
                if(IsWallRunning) StopWallRun();
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
            _continuationGraceTimer = continuationGraceTime;
            _sideSwitchCooldownTimer = 0f;

            // Capture entry speed, maintain momentum if faster than base speed
            var entrySpeed = _movementController.HorizontalVelocity.magnitude;
            _currentWallRunSpeed = Mathf.Max(wallRunSpeed, entrySpeed);
            WallNormal = _wallHit.normal.normalized;
            _targetWallNormal = WallNormal;
            _lastWallRunDirection = Vector3.zero;


            // Apply Camera Tilt
            if(_fpCamera == null || playerController.LookController == null) return;
            var tilt = IsWallLeft ? -wallRunCameraTilt : wallRunCameraTilt;
            playerController.LookController.SetTargetTilt(tilt);
        }

        private void StopWallRun() {
            IsWallRunning = false;
            _continuationGraceTimer = 0f;
            _sideSwitchCooldownTimer = 0f;


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

            if(_sideSwitchCooldownTimer > 0f) {
                _sideSwitchCooldownTimer -= Time.deltaTime;
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
            var referenceDirection = planarVelocity.sqrMagnitude > 0.01f ? planarVelocity.normalized : GetPreferredDirection(currentForward);

            if(Vector3.Dot(wallForward, referenceDirection) < 0f) {
                wallForward = -wallForward;
            }

            _lastWallRunDirection = wallForward;
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

        private bool TryFindInitialWallHit(out RaycastHit hit, out bool isLeft) {
            if(TryProbeSide(true, transform.position, out var leftHit)) {
                hit = leftHit;
                isLeft = true;
                return true;
            }

            if(TryProbeSide(false, transform.position, out var rightHit)) {
                hit = rightHit;
                isLeft = false;
                return true;
            }

            hit = default;
            isLeft = false;
            return false;
        }

        private void MaintainOrTransferWallRun() {
            if(!enableCurvedWallContinuation) {
                if(!TryProbeCurrentWall(out var directHit)) {
                    StopWallRun();
                    return;
                }

                _wallHit = directHit;
                WallNormal = directHit.normal.normalized;
                _targetWallNormal = WallNormal;
                return;
            }

            // Step 1: Validate current wall side.
            if(TryProbeCurrentWall(out var currentSideHit)) {
                _wallHit = currentSideHit;
                _continuationGraceTimer = continuationGraceTime;
                if(!continuationOnlyOnDetach) {
                    UpdateWallNormal(currentSideHit.normal);
                }

                return;
            }

            // Step 2: At detach boundary, try to acquire the next compatible segment.
            if(TryAcquireContinuationHit(out var continuationHit, out var continuationIsLeft, out var rejectReason)) {
                if(continuationIsLeft != IsWallLeft && _sideSwitchCooldownTimer > 0f) {
                    LogContinuation($"Rejected side switch due to cooldown ({_sideSwitchCooldownTimer:F3}s).");
                    _continuationGraceTimer -= Time.deltaTime;
                    if(_continuationGraceTimer <= 0f) {
                        StopWallRun();
                    }
                    return;
                }

                _wallHit = continuationHit;
                _continuationGraceTimer = continuationGraceTime;

                if(continuationIsLeft != IsWallLeft) {
                    IsWallLeft = continuationIsLeft;
                    _sideSwitchCooldownTimer = sideSwitchCooldown;
                    UpdateCameraTiltForCurrentSide();
                }

                UpdateWallNormal(continuationHit.normal);
                LogContinuation($"Accepted continuation. side={(IsWallLeft ? "left" : "right")}, normalDelta={Vector3.Angle(WallNormal, continuationHit.normal):F1}");
                return;
            }

            if(!string.IsNullOrWhiteSpace(rejectReason)) {
                LogContinuation($"Continuation rejected: {rejectReason}");
            }

            // Step 3: Allow a short grace period for segment seams.
            _continuationGraceTimer -= Time.deltaTime;
            if(_continuationGraceTimer > 0f) return;

            StopWallRun();
        }

        private bool TryAcquireContinuationHit(out RaycastHit bestHit, out bool bestIsLeft, out string rejectReason) {
            bestHit = default;
            bestIsLeft = IsWallLeft;
            rejectReason = "no candidate hit";

            var found = false;
            var bestScore = float.NegativeInfinity;
            var forward = GetRunDirectionReference();
            var forwardOrigin = transform.position + (forward * continuationProbeForwardOffset);
            var currentWallNormal = WallNormal.sqrMagnitude > 0.0001f ? WallNormal.normalized : (IsWallLeft ? transform.right : -transform.right);
            var expectedWallNormal = GetExpectedWallNormalFromRunDirection(forward);

            EvaluateContinuationProbe(transform.position, -currentWallNormal, DetermineIsLeftFromNormal(currentWallNormal, forward), ref found, ref bestScore, ref bestHit, ref bestIsLeft, ref rejectReason);
            EvaluateContinuationProbe(forwardOrigin, -currentWallNormal, DetermineIsLeftFromNormal(currentWallNormal, forward), ref found, ref bestScore, ref bestHit, ref bestIsLeft, ref rejectReason);
            EvaluateContinuationProbe(transform.position, -expectedWallNormal, DetermineIsLeftFromNormal(expectedWallNormal, forward), ref found, ref bestScore, ref bestHit, ref bestIsLeft, ref rejectReason);
            EvaluateContinuationProbe(forwardOrigin, -expectedWallNormal, DetermineIsLeftFromNormal(expectedWallNormal, forward), ref found, ref bestScore, ref bestHit, ref bestIsLeft, ref rejectReason);
            EvaluateContinuationProbe(transform.position, expectedWallNormal, !DetermineIsLeftFromNormal(expectedWallNormal, forward), ref found, ref bestScore, ref bestHit, ref bestIsLeft, ref rejectReason);
            EvaluateContinuationProbe(forwardOrigin, expectedWallNormal, !DetermineIsLeftFromNormal(expectedWallNormal, forward), ref found, ref bestScore, ref bestHit, ref bestIsLeft, ref rejectReason);

            return found;
        }

        private void EvaluateContinuationProbe(
            Vector3 origin,
            Vector3 probeDirection,
            bool inferredIsLeft,
            ref bool found,
            ref float bestScore,
            ref RaycastHit bestHit,
            ref bool bestIsLeft,
            ref string rejectReason
        ) {
            if(inferredIsLeft != IsWallLeft && _sideSwitchCooldownTimer > 0f) {
                return;
            }

            if(!TryProbeToward(probeDirection, origin, out var hit) &&
               !TrySphereProbeToward(probeDirection, origin, out hit)) {
                return;
            }

            if(!CanContinueOnHit(hit, out var reason)) {
                rejectReason = reason;
                return;
            }

            // Prefer continuity with current normal and previous run direction.
            var normalDelta = Vector3.Angle(WallNormal, hit.normal);
            var normalizedDistance = Mathf.Clamp01(hit.distance / Mathf.Max(0.01f, wallDistanceCheck + continuationProbeForwardOffset + continuationProbeRadius));
            var candidateForward = Vector3.Cross(hit.normal, Vector3.up);
            if(candidateForward.sqrMagnitude > 0.0001f) {
                candidateForward.Normalize();
            }

            var reference = _lastWallRunDirection.sqrMagnitude > 0.0001f ? _lastWallRunDirection : GetPreferredDirection(transform.forward);
            reference.y = 0f;
            if(reference.sqrMagnitude > 0.0001f) reference.Normalize();
            if(Vector3.Dot(candidateForward, reference) < 0f) {
                candidateForward = -candidateForward;
            }

            var tangentScore = Mathf.Clamp01(Vector3.Dot(candidateForward, reference));
            var score = (1f - (normalDelta / Mathf.Max(1f, continuationMaxNormalDelta))) * 0.55f +
                        (1f - normalizedDistance) * 0.30f +
                        tangentScore * 0.15f;

            if(inferredIsLeft == IsWallLeft) {
                score += 0.05f;
            }

            if(score <= bestScore) return;

            found = true;
            bestScore = score;
            bestHit = hit;
            bestIsLeft = inferredIsLeft;
            rejectReason = string.Empty;
        }

        private bool CanContinueOnHit(RaycastHit hit, out string reason) {
            reason = string.Empty;

            if(Physics.Raycast(transform.position, Vector3.down, minWallRunHeight, wallLayer)) {
                reason = "too close to ground";
                return false;
            }

            var normalDelta = Vector3.Angle(WallNormal, hit.normal);
            if(normalDelta > continuationMaxNormalDelta) {
                reason = $"normal delta too high ({normalDelta:F1})";
                return false;
            }

            return true;
        }

        private bool TryProbeSide(bool probeLeft, Vector3 origin, out RaycastHit hit) {
            var dir = probeLeft ? -transform.right : transform.right;
            return Physics.Raycast(origin, dir, out hit, wallDistanceCheck, wallLayer);
        }

        private bool TryProbeToward(Vector3 direction, Vector3 origin, out RaycastHit hit) {
            if(direction.sqrMagnitude < 0.0001f) {
                hit = default;
                return false;
            }

            return Physics.Raycast(origin, direction.normalized, out hit, wallDistanceCheck, wallLayer);
        }

        private bool TrySphereProbeToward(Vector3 direction, Vector3 origin, out RaycastHit hit) {
            if(direction.sqrMagnitude < 0.0001f) {
                hit = default;
                return false;
            }

            return Physics.SphereCast(origin, continuationProbeRadius, direction.normalized, out hit, wallDistanceCheck, wallLayer);
        }

        private bool TryProbeCurrentWall(out RaycastHit hit) {
            var towardWall = WallNormal.sqrMagnitude > 0.0001f
                ? -WallNormal.normalized
                : (IsWallLeft ? -transform.right : transform.right);

            if(TryProbeToward(towardWall, transform.position, out hit)) {
                return true;
            }

            var forward = GetRunDirectionReference();
            var forwardOrigin = transform.position + (forward * continuationProbeForwardOffset);
            if(TryProbeToward(towardWall, forwardOrigin, out hit)) {
                return true;
            }

            if(TrySphereProbeToward(towardWall, transform.position, out hit)) {
                return true;
            }

            return TrySphereProbeToward(towardWall, forwardOrigin, out hit);
        }

        private Vector3 GetRunDirectionReference() {
            var forward = _lastWallRunDirection;
            forward.y = 0f;
            if(forward.sqrMagnitude > 0.0001f) {
                return forward.normalized;
            }

            var planarVelocity = GetPlanarVelocity();
            if(planarVelocity.sqrMagnitude > 0.0001f) {
                return planarVelocity.normalized;
            }

            return GetPreferredDirection(transform.forward);
        }

        private static bool DetermineIsLeftFromNormal(Vector3 wallNormal, Vector3 runDirection) {
            var run = runDirection;
            run.y = 0f;
            if(run.sqrMagnitude < 0.0001f) {
                return false;
            }

            run.Normalize();
            var leftReference = Vector3.Cross(Vector3.up, run);
            if(leftReference.sqrMagnitude < 0.0001f) {
                return false;
            }

            return Vector3.Dot(wallNormal.normalized, leftReference.normalized) >= 0f;
        }

        private Vector3 GetExpectedWallNormalFromRunDirection(Vector3 runDirection) {
            var run = runDirection;
            run.y = 0f;
            if(run.sqrMagnitude < 0.0001f) {
                return WallNormal.sqrMagnitude > 0.0001f ? WallNormal.normalized : (IsWallLeft ? transform.right : -transform.right);
            }

            run.Normalize();
            var expected = IsWallLeft ? Vector3.Cross(Vector3.up, run) : Vector3.Cross(run, Vector3.up);
            if(expected.sqrMagnitude < 0.0001f) {
                return WallNormal.sqrMagnitude > 0.0001f ? WallNormal.normalized : (IsWallLeft ? transform.right : -transform.right);
            }

            return expected.normalized;
        }

        private void UpdateWallNormal(Vector3 newNormal) {
            _targetWallNormal = newNormal.normalized;
            if(WallNormal.sqrMagnitude < 0.0001f) {
                WallNormal = _targetWallNormal;
                return;
            }

            var blendT = 1f - Mathf.Exp(-Mathf.Max(0.01f, wallNormalBlendSpeed) * Time.deltaTime);
            WallNormal = Vector3.Slerp(WallNormal, _targetWallNormal, blendT).normalized;
        }

        private void UpdateCameraTiltForCurrentSide() {
            if(_fpCamera == null || playerController.LookController == null) return;
            var tilt = IsWallLeft ? -wallRunCameraTilt : wallRunCameraTilt;
            playerController.LookController.SetTargetTilt(tilt);
        }

        private void LogContinuation(string message) {
            if(!wallRunContinuationDebugLogs) return;
            Debug.Log($"[WallRun] {message}");
        }
    }
}
