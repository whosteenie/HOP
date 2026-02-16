using System;
using System.IO;
using Game.Settings;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class WallRunController : NetworkBehaviour {
        // #region agent log
        private static void AgentLog(string hypothesisId, string message, string location, string dataJson) {
            try {
                var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".cursor", "debug.log"));
                var dir = Path.GetDirectoryName(path);
                if(string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                var dataStr = string.IsNullOrEmpty(dataJson) ? "{}" : dataJson;
                var escaped = (dataStr ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
                var line = "{\"hypothesisId\":\"" + hypothesisId + "\",\"message\":\"" + (message ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ",\"location\":\"" + (location ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"dataStr\":\"" + escaped + "\"}\n";
                File.AppendAllText(path, line);
            } catch { /* ignore */ }
        }
        // #endregion

        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private CharacterController _characterController;
        private CinemachineCamera _fpCamera;
        private PlayerMovementController _movementController;

        [Header("Wall Running Settings")]
        [SerializeField] private float wallRunSpeed = 12f;

        [SerializeField] private float maxWallRunTime = 3f;

        [Tooltip(
            "If a new wall run starts within this many seconds of the last stop, reuse remaining timer instead of full 3s (avoids instant reattach granting a fresh run).")]
        [SerializeField]
        private float quickReattachWindow = 0.4f;

        [SerializeField] private float wallJumpUpForce = 12f;
        [SerializeField] private float wallJumpSideForce = 10f;
        [SerializeField] private float wallRunCameraTilt = 10f;
        [SerializeField] private float wallDistanceCheck = 1f;
        [SerializeField] private float minWallRunHeight = 1.5f;
        [SerializeField] private LayerMask wallLayer;

        [Header("Detection")]
        [SerializeField] private float wallRunAngleThreshold = 20f; // Limit angle to prevent running straight into wall (curved surfaces)
        [Tooltip("Stricter angle for flat walls only; prevents initiating when running head-on into corners (bounce).")]
        [SerializeField] private float flatWallRunAngleThreshold = 40f;

        [SerializeField] private float wallJumpCooldown = 0.35f;
        [SerializeField] private float minWallRunSpeed = 9f; // Slightly below SprintSpeed (10f)

        #region Stashed: Legacy continuation (not used by current MaintainWallRunNew path)
        [Header("Curved Wall Continuation")]
        [SerializeField] private bool enableCurvedWallContinuation = true;
        [SerializeField] private bool continuationOnlyOnDetach = true;
        [SerializeField] private float continuationProbeForwardOffset = 0.45f;
        [SerializeField] private float continuationProbeRadius = 0.16f;
        [SerializeField] private float continuationProbeForwardMaxDistance = 2.8f;
        [SerializeField] private float continuationMaxNormalDelta = 65f;
        [SerializeField] private float continuationGraceTime = 0.16f;
        [SerializeField] private float sideSwitchCooldown = 0.08f;
        [SerializeField] private bool wallRunContinuationDebugLogs;
        #endregion

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

        /// <summary>Stashed at wall-run start; used on exit to set velocity to exitDirection * this magnitude (cancels stick).</summary>
        private float _wallRunEntrySpeedMagnitude;

        /// <summary>When we stop, stash remaining time and stop time; quick reattach reuses remaining instead of full 3s.</summary>
        private float _wallRunTimerRemainingAtStop;

        private float _lastWallRunStopTime;
        private float _jumpCooldownTimer;
        private Vector3 _targetWallNormal;
        private Vector3 _lastWallRunDirection;

        /// <summary>When non-null we use math-based curved path; when null we use single-probe flat path. Set at wall-run start, cleared on stop.</summary>
        private CurvedWallRunSurface _curvedSurface;

        private string _stopReason;

        #region Stashed: Legacy continuation state (only referenced by stashed methods)
        private float _originalGravity;
        private float _continuationGraceTimer;
        private float _sideSwitchCooldownTimer;
        private int _consecutiveSyntheticContinuationFrames;
        private const int MaxConsecutiveSyntheticContinuationFrames = 8;
        #endregion

        [Header("New wall run (curved / flat)")]
        [SerializeField] private float curvedSurfaceMaxDistance = 1.2f;

        [Tooltip(
            "When on curved surface, pull toward wall so we don't drift off. Strength of inward velocity per unit distance over target.")]
        [SerializeField]
        private float curvedStickStrength = 10f;

        [Tooltip("Target distance from cylinder surface; stick force applies when farther than this.")] [SerializeField]
        private float curvedStickTargetDistance = 0.4f;

        [Tooltip(
            "Scale for centripetal term (v²/radius) so stick scales with speed and keeps high-speed runs on the curve. ~1 = physics-like.")]
        [SerializeField]
        private float curvedStickCentripetalScale = 1f;

        [Tooltip("Blend speed for wall normal updates (used by both curved and flat).")]
        [SerializeField] private float wallNormalBlendSpeed = 16f;

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

        #region Active: Wall run entry, maintain, timer, velocity, jump

        /// <summary>
        /// Checks for surrounding walls and initiates or stops a wall run.
        /// </summary>
        public void CheckForWall() {
            if(!IsOwner) return;

            if(_characterController.isGrounded) {
                if(IsWallRunning) { _stopReason = "grounded"; StopWallRun(); }
                return;
            }

            if(_jumpCooldownTimer > 0f) {
                _jumpCooldownTimer -= Time.deltaTime;
                return;
            }

            if(IsWallRunning) {
                MaintainWallRunNew();
                return;
            }

            if(TryFindInitialWallHit(out var initialHit, out var initialIsLeft)) {
                IsWallLeft = initialIsLeft;
                _wallHit = initialHit;
                _curvedSurface = initialHit.collider != null
                    ? initialHit.collider.GetComponentInParent<CurvedWallRunSurface>()
                    : null;
            } else {
                IsWallLeft = false;
                if(IsWallRunning) { _stopReason = "no_initial_hit"; StopWallRun(); }
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

            _consecutiveSyntheticContinuationFrames = 0;
            StartWallRun();
        }

        private bool CanWallRun() {
            // vertical check - ensure we are off the ground
            if(Physics.Raycast(transform.position, Vector3.down, minWallRunHeight, wallLayer)) {
                // Debug.Log("[WallRun] Too close to ground");
                return false;
            }

            WallNormal = _wallHit.normal;

            // Angle check: prevent wall running if facing the wall too directly. Use stricter angle on flat walls to avoid corner bounce.
            var angle = Vector3.Angle(transform.forward, -WallNormal);
            var threshold = _curvedSurface != null ? wallRunAngleThreshold : flatWallRunAngleThreshold;
            return !(angle < threshold) && !(angle > 180f - threshold);
        }
        
        private void StartWallRun() {
            IsWallRunning = true;
            var timeSinceStop = Time.time - _lastWallRunStopTime;
            if(timeSinceStop < quickReattachWindow && _wallRunTimerRemainingAtStop > 0f) {
                _wallRunTimer = _wallRunTimerRemainingAtStop;
            } else {
                _wallRunTimer = maxWallRunTime;
            }

            _continuationGraceTimer = continuationGraceTime;
            _sideSwitchCooldownTimer = 0f;

            // Capture entry speed, maintain momentum if faster than base speed
            var entrySpeed = _movementController != null ? _movementController.HorizontalVelocity.magnitude : 0f;
            _currentWallRunSpeed = Mathf.Max(wallRunSpeed, entrySpeed);
            _wallRunEntrySpeedMagnitude = _currentWallRunSpeed;
            WallNormal = _wallHit.normal.normalized;
            _targetWallNormal = WallNormal;
            _lastWallRunDirection = Vector3.zero;


            // #region agent log
            AgentLog("H_start", "wall_run_start", "WallRunController.StartWallRun", "{\"curved\":" + (_curvedSurface != null ? "true" : "false") + ",\"entrySpeed\":" + _currentWallRunSpeed.ToString("F2") + ",\"timer\":" + _wallRunTimer.ToString("F2") + ",\"quickReattach\":" + (timeSinceStop < quickReattachWindow && _wallRunTimerRemainingAtStop > 0f ? "true" : "false") + "}");
            // #endregion

            // Apply Camera Tilt
            UpdateCameraTiltForCurrentSide();
        }

        private void StopWallRun() {
            // #region agent log
            AgentLog("H_stop", "wall_run_stop", "WallRunController.StopWallRun", "{\"reason\":\"" + (_stopReason ?? "unknown") + "\",\"timerRemaining\":" + _wallRunTimer.ToString("F2") + ",\"curved\":" + (_curvedSurface != null ? "true" : "false") + "}");
            // #endregion
            _stopReason = null;

            _wallRunTimerRemainingAtStop = _wallRunTimer;
            _lastWallRunStopTime = Time.time;

            // Cancel stick: set exit velocity to tangent direction × entry magnitude so we don't carry inward momentum.
            // Skip for instant flat_no_hit (timer barely used): logs showed corner grapples starting then stopping in one frame and getting full-speed exit = bounce.
            var veryShortFlatRun = _stopReason == "flat_no_hit" && _wallRunTimer > maxWallRunTime - 0.2f;
            if(!veryShortFlatRun && _movementController != null && _lastWallRunDirection.sqrMagnitude > 0.01f &&
               _wallRunEntrySpeedMagnitude > 0f) {
                var exitDir = _lastWallRunDirection.normalized;
                _movementController.SetVelocity(exitDir * _wallRunEntrySpeedMagnitude);
            }

            IsWallRunning = false;
            _curvedSurface = null;
            _continuationGraceTimer = 0f;
            _sideSwitchCooldownTimer = 0f;


            // Reset Camera Tilt
            if(_fpCamera != null && playerController.LookController != null) {
                playerController.LookController.SetTargetTilt(0f);
            }
        }

        /// <summary>
        /// New minimal maintain: curved = math (surface tells us); flat = one probe. No grace, no continuation, no fallbacks.
        /// </summary>
        private void MaintainWallRunNew() {
            if(_curvedSurface != null) {
                MaintainWallRunCurved();
            } else {
                MaintainWallRunFlat();
            }
        }

        private void MaintainWallRunCurved() {
            if(_curvedSurface == null) {
                _stopReason = "curved_null_surface";
                StopWallRun();
                return;
            }

            var position = transform.position;
            var distToSurface = _curvedSurface.GetDistanceToSurface(position);
            var maxDist = Mathf.Max(curvedSurfaceMaxDistance, 1.2f);
            var onSurface = distToSurface <= maxDist;
            var gotNormal = _curvedSurface.TryGetNormalAt(position, out var normal);
            // #region agent log
            if(Time.frameCount % 12 == 0) AgentLog("H_curved", "curved_maintain", "WallRunController.MaintainWallRunCurved", "{\"distToSurface\":" + distToSurface.ToString("F3") + ",\"maxDist\":" + maxDist.ToString("F3") + ",\"onSurface\":" + (onSurface ? "true" : "false") + ",\"timer\":" + _wallRunTimer.ToString("F2") + "}");
            // #endregion
            if(!onSurface) {
                _stopReason = "off_surface";
                StopWallRun();
                return;
            }

            if(gotNormal) {
                UpdateWallNormal(normal);
            }
        }

        private void MaintainWallRunFlat() {
            var towardWall = WallNormal.sqrMagnitude > 0.0001f
                ? -WallNormal.normalized
                : (IsWallLeft ? -transform.right : transform.right);
            if(!Physics.Raycast(transform.position, towardWall, out var hit, wallDistanceCheck, wallLayer)) {
                _stopReason = "flat_no_hit";
                StopWallRun();
                return;
            }

            _wallHit = hit;
            UpdateWallNormal(hit.normal);
        }

        /// <summary>
        /// Updates the active wall run state, handling timers and speed checks.
        /// </summary>
        public void UpdateWallRun() {
            if(!IsWallRunning) return;

            _wallRunTimer -= Time.deltaTime;
            if(_wallRunTimer <= 0) {
                _stopReason = "timer";
                StopWallRun();
                return;
            }

            // Keep FP lean synced with dynamic left/right wall-run side while preserving smooth blending.
            UpdateCameraTiltForCurrentSide();

            if(_sideSwitchCooldownTimer > 0f) {
                _sideSwitchCooldownTimer -= Time.deltaTime;
            }

            // Low-speed stop: try mantle or end run. Skip for curved runs so we only end on timer or off_surface (avoid dampening/velocity quirks kicking us off).
            if(!(_wallRunTimer < maxWallRunTime - 0.1f)) return;
            if(_curvedSurface != null) return;

            var actualVelocity = _characterController.velocity;
            var actualSpeed = new Vector3(actualVelocity.x, 0, actualVelocity.z).magnitude;

            if(!(actualSpeed < 2f)) return;
            var desiredDir = GetWallRunVelocity(transform.forward).normalized;

            if(playerController.MantleController != null &&
               playerController.MantleController.TryMantle(desiredDir)) {
                _stopReason = "low_speed_mantle";
                StopWallRun();
            } else {
                _stopReason = "low_speed";
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

            // Sustain phase: prefer previous wall-run direction to preserve continuity on curves.
            // Entry phase: still allow velocity/input based direction selection.
            Vector3 referenceDirection;
            if(IsWallRunning && _lastWallRunDirection.sqrMagnitude > 0.01f) {
                referenceDirection = _lastWallRunDirection.normalized;
            } else {
                var planarVelocity = GetPlanarVelocity();
                referenceDirection = planarVelocity.sqrMagnitude > 0.01f
                    ? planarVelocity.normalized
                    : GetPreferredDirection(currentForward, allowLookFallback: !IsWallRunning);
            }

            if(referenceDirection.sqrMagnitude < 0.0001f) {
                referenceDirection = wallForward;
            }

            if(Vector3.Dot(wallForward, referenceDirection) < 0f) {
                wallForward = -wallForward;
            }

            _lastWallRunDirection = wallForward;
            var velocity = wallForward * _currentWallRunSpeed;

            // On curved surfaces we only apply tangent velocity above; character would drift off. Add inward stick.
            // At high speed, centripetal requirement v²/r dominates; stick must scale with speed, or we bounce off.
            if(_curvedSurface == null || !_curvedSurface.TryGetNormalAt(transform.position, out var outwardNormal))
                return velocity;
            var distToSurface = _curvedSurface.GetDistanceToSurface(transform.position);
            var over = Mathf.Max(0f, distToSurface - curvedStickTargetDistance);
            var speed = _currentWallRunSpeed;
            var r = Mathf.Max(0.01f, _curvedSurface.WorldRadius);
            var centripetal = (speed * speed / r) * Mathf.Max(0f, curvedStickCentripetalScale);
            var stickMag = over * curvedStickStrength + centripetal;
            // Logs showed stickMag 160–220 m/s with speed 31–36 → inward dominated and felt like stuck/jitter. Cap to fraction of tangent speed.
            stickMag = Mathf.Min(stickMag, speed * 1.5f);
            var inward = -outwardNormal;
            inward.y = 0f;
            if(!(inward.sqrMagnitude > 0.0001f)) return velocity;
            inward.Normalize();
            velocity += inward * stickMag;

            // #region agent log
            if(Time.frameCount % 12 == 0) AgentLog("H_stick", "curved_stick", "WallRunController.GetWallRunVelocity", "{\"distToSurface\":" + distToSurface.ToString("F3") + ",\"stickMag\":" + stickMag.ToString("F2") + ",\"centripetal\":" + centripetal.ToString("F2") + ",\"speed\":" + speed.ToString("F2") + "}");
            // #endregion

            return velocity;
        }

        private Vector3 GetPlanarVelocity() {
            if(_movementController == null) {
                return Vector3.zero;
            }

            var v = _movementController.HorizontalVelocity;
            v.y = 0f;
            return v;
        }

        private Vector3 GetPreferredDirection(Vector3 fallbackForward, bool allowLookFallback = true) {
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

            if(!allowLookFallback) {
                if(_lastWallRunDirection.sqrMagnitude > 0.0001f) {
                    return _lastWallRunDirection.normalized;
                }

                if(!(WallNormal.sqrMagnitude > 0.0001f)) return Vector3.zero;
                var tangent = Vector3.Cross(WallNormal, Vector3.up);
                return tangent.sqrMagnitude > 0.0001f ? tangent.normalized : Vector3.zero;
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

            _stopReason = "wall_jump";
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

        #endregion

        #region Stashed: Legacy continuation (MaintainOrTransferWallRun not called)

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
            var tryProbeOk = TryProbeCurrentWall(out var currentSideHit);
            if(tryProbeOk) {
                _wallHit = currentSideHit;
                _continuationGraceTimer = continuationGraceTime;
                _consecutiveSyntheticContinuationFrames = 0;
                if(!continuationOnlyOnDetach) {
                    UpdateWallNormal(currentSideHit.normal);
                }

                return;
            }

            // Only use continuation/grace on surfaces that explicitly support it (e.g. cylinders with CurvedWallRunSurface).
            var curvedSurface = _wallHit.collider != null
                ? _wallHit.collider.GetComponentInParent<CurvedWallRunSurface>()
                : null;
            if(curvedSurface == null) {
                StopWallRun();
                return;
            }

            // Step 2: At detach boundary, try to acquire the next compatible segment using cylinder geometry when available.
            var tryAcquireOk = TryAcquireContinuationHit(curvedSurface, out var continuationHit,
                out var continuationIsLeft, out var rejectReason);
            if(tryAcquireOk) {
                _consecutiveSyntheticContinuationFrames = 0;
                if(continuationIsLeft != IsWallLeft && _sideSwitchCooldownTimer > 0f) {
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
                return;
            }

            // Step 3: On curved surfaces, allow synthetic continuation when probes miss (e.g. high-speed entry) so we don't kick early. Only end from grace when we can't predict or after too many synthetic frames.
            if(curvedSurface != null &&
               _consecutiveSyntheticContinuationFrames < MaxConsecutiveSyntheticContinuationFrames) {
                var forward = GetRunDirectionReference();
                if(forward.sqrMagnitude > 0.0001f &&
                   curvedSurface.TryGetPredictedNextNormal(WallNormal, forward, out var predictedNormal)) {
                    _continuationGraceTimer = continuationGraceTime;
                    _consecutiveSyntheticContinuationFrames++;
                    UpdateWallNormal(predictedNormal);
                    return;
                }
            }

            // Sharp corner (normal delta reject): end immediately. No grace.
            if(rejectReason != null && rejectReason.IndexOf("normal delta", StringComparison.OrdinalIgnoreCase) >= 0) {
                StopWallRun();
                return;
            }

            _continuationGraceTimer -= Time.deltaTime;
            if(_continuationGraceTimer > 0f) return;

            StopWallRun();
        }

        private bool TryAcquireContinuationHit(CurvedWallRunSurface curvedSurface, out RaycastHit bestHit,
            out bool bestIsLeft, out string rejectReason) {
            bestHit = default;
            bestIsLeft = IsWallLeft;
            rejectReason = "no candidate hit";

            var found = false;
            var bestScore = float.NegativeInfinity;
            var forward = GetRunDirectionReference();
            var forwardOrigin = transform.position + (forward * continuationProbeForwardOffset);
            var currentWallNormal = WallNormal.sqrMagnitude > 0.0001f
                ? WallNormal.normalized
                : (IsWallLeft ? transform.right : -transform.right);

            // Use cylinder geometry for exact expected normal when available.
            var expectedWallNormal = GetExpectedWallNormalFromRunDirection(forward);
            if(curvedSurface != null &&
               curvedSurface.TryGetPredictedNextNormal(WallNormal, forward, out var predictedNext)) {
                expectedWallNormal = predictedNext;
            }

            var position = transform.position;
            EvaluateContinuationProbe(position, -currentWallNormal,
                DetermineIsLeftFromNormal(currentWallNormal, forward), ref found, ref bestScore, ref bestHit,
                ref bestIsLeft, ref rejectReason);
            EvaluateContinuationProbe(forwardOrigin, -currentWallNormal,
                DetermineIsLeftFromNormal(currentWallNormal, forward), ref found, ref bestScore, ref bestHit,
                ref bestIsLeft, ref rejectReason);
            EvaluateContinuationProbe(position, -expectedWallNormal,
                DetermineIsLeftFromNormal(expectedWallNormal, forward), ref found, ref bestScore, ref bestHit,
                ref bestIsLeft, ref rejectReason);
            EvaluateContinuationProbe(forwardOrigin, -expectedWallNormal,
                DetermineIsLeftFromNormal(expectedWallNormal, forward), ref found, ref bestScore, ref bestHit,
                ref bestIsLeft, ref rejectReason);
            EvaluateContinuationProbe(position, expectedWallNormal,
                !DetermineIsLeftFromNormal(expectedWallNormal, forward), ref found, ref bestScore, ref bestHit,
                ref bestIsLeft, ref rejectReason);
            EvaluateContinuationProbe(forwardOrigin, expectedWallNormal,
                !DetermineIsLeftFromNormal(expectedWallNormal, forward), ref found, ref bestScore, ref bestHit,
                ref bestIsLeft, ref rejectReason);

            // Curved/cylindrical walls: next segment is ahead. Use segment-aware distance and speed scaling.
            if(!(forward.sqrMagnitude > 0.0001f)) return found;
            var forwardDir = forward.normalized;
            var speedRatio = Mathf.Max(1f, _currentWallRunSpeed / Mathf.Max(0.01f, wallRunSpeed));
            var baseMaxDist = Mathf.Max(wallDistanceCheck, continuationProbeForwardMaxDistance);
            var segmentDist = curvedSurface != null ? curvedSurface.GetSegmentAwareProbeDistance() : 0f;
            var forwardMaxDist = Mathf.Max(baseMaxDist * speedRatio, segmentDist);
            var ahead = Mathf.Clamp(0.6f + (_currentWallRunSpeed - wallRunSpeed) * 0.06f, 0.6f, 3f);
            if(curvedSurface != null && segmentDist > 0f) {
                ahead = Mathf.Max(ahead, segmentDist * 0.5f);
            }

            EvaluateContinuationProbe(transform.position, forwardDir, IsWallLeft, forwardMaxDist, ref found,
                ref bestScore, ref bestHit, ref bestIsLeft, ref rejectReason, "forward");
            EvaluateContinuationProbe(forwardOrigin, forwardDir, IsWallLeft, forwardMaxDist, ref found, ref bestScore,
                ref bestHit, ref bestIsLeft, ref rejectReason, "forward");
            var forwardInward = (forwardDir - currentWallNormal * 0.4f);
            if(forwardInward.sqrMagnitude > 0.0001f) {
                forwardInward.Normalize();
                EvaluateContinuationProbe(transform.position, forwardInward, IsWallLeft, forwardMaxDist, ref found,
                    ref bestScore, ref bestHit, ref bestIsLeft, ref rejectReason, "forwardInward");
                EvaluateContinuationProbe(forwardOrigin, forwardInward, IsWallLeft, forwardMaxDist, ref found,
                    ref bestScore, ref bestHit, ref bestIsLeft, ref rejectReason, "forwardInward");
            }

            var originAhead = transform.position + forwardDir * ahead;
            var originAheadFromForward = forwardOrigin + forwardDir * ahead;
            var towardNextWall = -expectedWallNormal;
            if(!(towardNextWall.sqrMagnitude > 0.0001f)) return found;
            towardNextWall.Normalize();
            EvaluateContinuationProbe(originAhead, towardNextWall, IsWallLeft, forwardMaxDist, ref found, ref bestScore,
                ref bestHit, ref bestIsLeft, ref rejectReason, "aheadTowardWall");
            EvaluateContinuationProbe(originAheadFromForward, towardNextWall, IsWallLeft, forwardMaxDist, ref found,
                ref bestScore, ref bestHit, ref bestIsLeft, ref rejectReason, "aheadTowardWall");

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
            EvaluateContinuationProbe(origin, probeDirection, inferredIsLeft, wallDistanceCheck, ref found,
                ref bestScore, ref bestHit, ref bestIsLeft, ref rejectReason);
        }

        private void EvaluateContinuationProbe(
            Vector3 origin,
            Vector3 probeDirection,
            bool inferredIsLeft,
            float maxDistance,
            ref bool found,
            ref float bestScore,
            ref RaycastHit bestHit,
            ref bool bestIsLeft,
            ref string rejectReason,
            string probeLabel = null
        ) {
            if(inferredIsLeft != IsWallLeft && _sideSwitchCooldownTimer > 0f) {
                return;
            }

            if(!TryProbeToward(probeDirection, origin, maxDistance, out var hit) &&
               !TrySphereProbeToward(probeDirection, origin, maxDistance, out hit)) {
                return;
            }

            if(!CanContinueOnHit(hit, out var reason)) {
                rejectReason = reason;
                return;
            }

            // Prefer continuity with current normal and previous run direction.
            var normalDelta = Vector3.Angle(WallNormal, hit.normal);
            var normalizedDistance = Mathf.Clamp01(hit.distance / Mathf.Max(0.01f,
                maxDistance + continuationProbeForwardOffset + continuationProbeRadius));
            var candidateForward = Vector3.Cross(hit.normal, Vector3.up);
            if(candidateForward.sqrMagnitude > 0.0001f) {
                candidateForward.Normalize();
            }

            var reference = _lastWallRunDirection.sqrMagnitude > 0.0001f
                ? _lastWallRunDirection
                : GetPreferredDirection(transform.forward, allowLookFallback: false);
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
            if(!(normalDelta > continuationMaxNormalDelta)) return true;
            reason = $"normal delta too high ({normalDelta:F1} > {continuationMaxNormalDelta:F1})";
            return false;
        }

        private bool TryProbeToward(Vector3 direction, Vector3 origin, out RaycastHit hit) {
            return TryProbeToward(direction, origin, wallDistanceCheck, out hit);
        }

        private bool TryProbeToward(Vector3 direction, Vector3 origin, float maxDistance, out RaycastHit hit) {
            if(!(direction.sqrMagnitude < 0.0001f) && !(maxDistance <= 0f))
                return Physics.Raycast(origin, direction.normalized, out hit, maxDistance, wallLayer);
            hit = default;
            return false;
        }

        private bool TrySphereProbeToward(Vector3 direction, Vector3 origin, out RaycastHit hit) {
            return TrySphereProbeToward(direction, origin, wallDistanceCheck, out hit);
        }

        private bool TrySphereProbeToward(Vector3 direction, Vector3 origin, float maxDistance, out RaycastHit hit) {
            if(!(direction.sqrMagnitude < 0.0001f) && !(maxDistance <= 0f))
                return Physics.SphereCast(origin, continuationProbeRadius, direction.normalized, out hit, maxDistance,
                    wallLayer);
            hit = default;
            return false;
        }

        private bool TryProbeCurrentWall(out RaycastHit hit) {
            var towardWall = WallNormal.sqrMagnitude > 0.0001f
                ? -WallNormal.normalized
                : IsWallLeft ? -transform.right : transform.right;

            if(TryProbeToward(towardWall, transform.position, out hit)) {
                return true;
            }

            var forward = GetRunDirectionReference();
            var forwardOrigin = transform.position + (forward * continuationProbeForwardOffset);
            if(TryProbeToward(towardWall, forwardOrigin, out hit)) {
                return true;
            }

            return TrySphereProbeToward(towardWall, transform.position, out hit) ||
                   TrySphereProbeToward(towardWall, forwardOrigin, out hit);
        }

        private Vector3 GetRunDirectionReference() {
            var forward = _lastWallRunDirection;
            forward.y = 0f;
            if(forward.sqrMagnitude > 0.0001f) {
                return forward.normalized;
            }

            var planarVelocity = GetPlanarVelocity();
            return planarVelocity.sqrMagnitude > 0.0001f
                ? planarVelocity.normalized
                : GetPreferredDirection(transform.forward, allowLookFallback: false);
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
                return WallNormal.sqrMagnitude > 0.0001f
                    ? WallNormal.normalized
                    : IsWallLeft ? transform.right : -transform.right;
            }

            run.Normalize();
            var expected = IsWallLeft ? Vector3.Cross(Vector3.up, run) : Vector3.Cross(run, Vector3.up);
            if(expected.sqrMagnitude < 0.0001f) {
                return WallNormal.sqrMagnitude > 0.0001f
                    ? WallNormal.normalized
                    : IsWallLeft ? transform.right : -transform.right;
            }

            return expected.normalized;
        }

        #endregion

        #region Active: Helpers (normal blend, camera tilt, initial probe)

        private bool TryProbeSide(bool probeLeft, Vector3 origin, out RaycastHit hit) {
            var dir = probeLeft ? -transform.right : transform.right;
            return Physics.Raycast(origin, dir, out hit, wallDistanceCheck, wallLayer);
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
            var tilt = IsRightWallRun ? wallRunCameraTilt : -wallRunCameraTilt;
            playerController.LookController.SetTargetTilt(tilt);
        }

        #endregion
    }
}