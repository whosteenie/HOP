using Game.Player.Core;
using Game.Settings;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Movement {
    public class WallRunController : NetworkBehaviour {
        #region Fields: References

        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private CharacterController _characterController;
        private CinemachineCamera _fpCamera;
        private PlayerMovementController _movementController;

        #endregion

        #region Fields: Settings

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
        [SerializeField]
        private float wallRunAngleThreshold = 20f; // Limit angle to prevent running straight into wall (curved surfaces)

        [Tooltip("Stricter angle for flat walls only; prevents initiating when running head-on into corners (bounce).")]
        [SerializeField]
        private float flatWallRunAngleThreshold = 40f;

        [SerializeField] private float wallJumpCooldown = 0.35f;
        [SerializeField] private float minWallRunSpeed = 9f; // Slightly below SprintSpeed (10f)

        [Header("Direction Intent")]
        [Tooltip("Minimum movement input magnitude required before keyboard intent is considered.")]
        [SerializeField] private float keyboardIntentInputDeadzone = 0.1f;

        [Tooltip("Minimum absolute alignment to wall tangent before keyboard intent is considered directional.")]
        [SerializeField] private float keyboardIntentDotDeadzone = 0.05f;

        [Tooltip("Minimum absolute camera alignment to wall tangent before camera intent is considered directional.")]
        [SerializeField] private float cameraIntentDotDeadzone = 0.15f;

        [Tooltip("Minimum speed required before velocity direction is considered a valid intent source.")]
        [SerializeField] private float velocityIntentMinSpeed = 0.25f;

        [Tooltip("At/above this speed, prevent opposite-direction flips unless keyboard intent is strongly committed.")]
        [SerializeField] private float highSpeedDirectionLockSpeed = 16f;

        [Tooltip("Minimum keyboard intent strength required to override high-speed velocity direction lock.")]
        [SerializeField] private float highSpeedFlipOverrideStrength = 0.85f;

        [Tooltip("If wall-run starts below this speed, default to forward direction unless a clear backward request exists.")]
        [SerializeField] private float lowSpeedForwardBiasThreshold = 14f;

        [Tooltip("At low-speed starts, backward velocity must reach this signed speed along wall tangent to force backward direction.")]
        [SerializeField] private float lowSpeedBackwardVelocityOverride = 11f;

        [Header("Curved Surface")]
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

        [Tooltip("Blend speed for wall normal updates (used by both curved and flat).")] [SerializeField]
        private float wallNormalBlendSpeed = 16f;

        #endregion

        #region Fields: State

        public bool IsWallRunning { get; private set; }

        public bool IsRightWallRun {
            get {
                if(!IsWallRunning) return false;
                // View-relative side detection for animation: flips when camera yaw crosses the wall side.
                return WallNormal.sqrMagnitude > 0.0001f 
                    ? Vector3.Dot(transform.right, WallNormal.normalized) < 0f
                    : !IsWallLeft;
            }
        }

        private Vector3 WallNormal { get; set; }
        private bool IsWallLeft { get; set; }

        private float _wallRunTimer;
        private RaycastHit _wallHit;
        private float _currentWallRunSpeed;
        private float _wallRunEntrySpeedMagnitude; // Stashed at wall-run start; used on exit to set velocity to exitDirection * this magnitude (cancels stick).
        private float _wallRunTimerRemainingAtStop; // When we stop, stash remaining time and stop time; quick reattach reuses remaining instead of full 3s.
        private float _lastWallRunStopTime;
        private float _jumpCooldownTimer;
        private Vector3 _targetWallNormal;
        private Vector3 _lastWallRunDirection;
        private int _lockedWallRunSign = 1;
        private bool _hasLockedWallRunSign;
        private CurvedWallRunSurface _curvedSurface; // When non-null we use math-based curved path; when null we use single-probe flat path. Set at wall-run start, cleared on stop.
        private string _stopReason;

        #endregion

        #region Initialization

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

        #endregion

        #region Wall Run Detection & Entry

        /// <summary>
        /// Checks for surrounding walls and initiates or stops a wall run.
        /// </summary>
        public void CheckForWall() {
            if(!IsOwner) return;

            if(_characterController.isGrounded) {
                if(!IsWallRunning) return;
                _stopReason = "grounded";
                StopWallRun();
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

            if(!TryFindInitialWallHit(out var initialHit, out var initialIsLeft)) {
                return;
            }

            IsWallLeft = initialIsLeft;
            _wallHit = initialHit;
            _curvedSurface = initialHit.collider != null ? initialHit.collider.GetComponentInParent<CurvedWallRunSurface>() : null;

            if(!CanWallRun()) return;
            
            var canInitiate = GameSettings.Data.controls.autoWallRun 
                || (playerController.PlayerInputController != null && playerController.PlayerInputController.IsJumpHeld);
            
            if(!canInitiate || _movementController.HorizontalVelocity.magnitude < minWallRunSpeed) {
                return;
            }

            StartWallRun();
        }

        private bool CanWallRun() {
            // vertical check - ensure we are off the ground
            if(Physics.Raycast(transform.position, Vector3.down, minWallRunHeight, wallLayer)) {
                return false;
            }

            WallNormal = _wallHit.normal;

            // Angle check: prevent wall running if facing the wall too directly. Use stricter angle on flat walls to avoid corner bounce.
            var angle = Vector3.Angle(transform.forward, -WallNormal);
            var threshold = _curvedSurface != null ? wallRunAngleThreshold : flatWallRunAngleThreshold;
            return !(angle < threshold) && !(angle > 180f - threshold);
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

        private bool TryProbeSide(bool probeLeft, Vector3 origin, out RaycastHit hit) {
            var dir = probeLeft ? -transform.right : transform.right;
            return Physics.Raycast(origin, dir, out hit, wallDistanceCheck, wallLayer);
        }

        #endregion

        #region Wall Run Lifecycle

        private void StartWallRun() {
            IsWallRunning = true;
            var timeSinceStop = Time.time - _lastWallRunStopTime;
            var isQuickReattach = timeSinceStop < quickReattachWindow;

            _wallRunTimer = isQuickReattach switch {
                true when _wallRunTimerRemainingAtStop > 0f => _wallRunTimerRemainingAtStop,
                true => 0f,
                _ => maxWallRunTime
            };

            // Capture entry speed, maintain momentum if faster than base speed
            var entrySpeed = _movementController != null ? _movementController.HorizontalVelocity.magnitude : 0f;
            _currentWallRunSpeed = Mathf.Max(wallRunSpeed, entrySpeed);
            _wallRunEntrySpeedMagnitude = _currentWallRunSpeed;
            WallNormal = _wallHit.normal.normalized;
            _targetWallNormal = WallNormal;
            _lastWallRunDirection = Vector3.zero;
            _lockedWallRunSign = ResolveWallRunDirectionSignForStart(transform.forward, entrySpeed);
            _hasLockedWallRunSign = true;

            UpdateCameraTiltForCurrentSide();
            PublishWallRunNetworkState();
        }

        private void StopWallRun() {
            var stopReasonWas = _stopReason;
            _stopReason = null;

            _wallRunTimerRemainingAtStop = _wallRunTimer;
            _lastWallRunStopTime = Time.time;

            // Cancel stick: set exit velocity to tangent direction × entry magnitude so we don't carry inward momentum.
            // Skip for instant flat_no_hit (timer barely used): logs showed corner grapples starting then stopping in one frame and getting full-speed exit = bounce.
            var veryShortFlatRun = stopReasonWas == "flat_no_hit" && _wallRunTimer > maxWallRunTime - 0.2f;
            if(!veryShortFlatRun && _movementController != null && _lastWallRunDirection.sqrMagnitude > 0.01f && _wallRunEntrySpeedMagnitude > 0f) {
                _movementController.SetVelocity(_lastWallRunDirection.normalized * _wallRunEntrySpeedMagnitude);
            }

            IsWallRunning = false;
            _curvedSurface = null;
            _lockedWallRunSign = 1;
            _hasLockedWallRunSign = false;

            if(playerController.LookController != null) playerController.LookController.SetTargetTilt(0f);
            PublishWallRunNetworkState();
        }

        /// <summary>Called when another system (e.g. grapple) takes over movement so wall run stick doesn't fight it.</summary>
        public void ForceStopWallRun(string reason = "grapple") {
            if(!IsWallRunning) return;
            _stopReason = reason;
            StopWallRun();
        }

        #endregion

        #region Wall Run Maintenance

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
            var position = transform.position;
            var distToSurface = _curvedSurface.GetDistanceToSurface(position);
            var maxDist = Mathf.Max(curvedSurfaceMaxDistance, 1.2f);
            
            if(distToSurface > maxDist) {
                _stopReason = "off_surface";
                StopWallRun();
                return;
            }

            if(_curvedSurface.TryGetNormalAt(position, out var normal)) {
                UpdateWallNormal(normal);
            }
        }

        private void MaintainWallRunFlat() {
            var towardWall = WallNormal.sqrMagnitude > 0.0001f 
                ? -WallNormal.normalized 
                : IsWallLeft ? -transform.right : transform.right;
            
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

            UpdateCameraTiltForCurrentSide();
            PublishWallRunNetworkState();

            // Low-speed stop: try mantle or end run. Skip for curved runs so we only end on timer or off_surface (avoid dampening/velocity quirks kicking us off).
            if(_wallRunTimer >= maxWallRunTime - 0.1f || _curvedSurface != null) return;

            var velocity = _characterController.velocity;
            var actualSpeed = new Vector3(velocity.x, 0, velocity.z).magnitude;
            if(actualSpeed >= 2f) return;
            
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

        private void PublishWallRunNetworkState() {
            if(!IsOwner || playerController == null) return;

            playerController.NetIsWallRunning.Value = IsWallRunning;
            playerController.NetIsRightWallRun.Value = IsWallRunning && IsRightWallRun;
            var forward = transform.forward;
            playerController.NetWallRunDirection.Value = IsWallRunning
                ? Mathf.Sign(Vector3.Dot(GetWallRunVelocity(forward), forward))
                : 1f;
        }

        #endregion

        #region Velocity Calculation

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
            var chosenSign = _hasLockedWallRunSign
                ? _lockedWallRunSign
                : ResolveWallRunDirectionSignFromIntent(wallForward, currentForward);

            if(chosenSign < 0) {
                wallForward = -wallForward;
            }

            _lastWallRunDirection = wallForward;
            var velocity = wallForward * _currentWallRunSpeed;

            return ApplyCurvedSurfaceStick(velocity);
        }

        private int ResolveWallRunDirectionSignForStart(Vector3 currentForward, float entrySpeed) {
            var wallForward = Vector3.Cross(WallNormal, Vector3.up);
            if(wallForward.sqrMagnitude < 0.0001f) {
                return 1;
            }

            wallForward.Normalize();
            return ResolveWallRunDirectionSignFromIntent(wallForward, currentForward, true, entrySpeed);
        }

        private int ResolveWallRunDirectionSignFromIntent(Vector3 wallForward, Vector3 currentForward, bool isWallRunStart = false, float entrySpeed = 0f) {
            var hasKeyboardIntent = TryGetKeyboardIntentSign(wallForward, out var keyboardSign, out var keyboardStrength);
            var hasCameraIntent = TryGetCameraIntentSign(wallForward, currentForward, out var cameraSign);
            var hasVelocityIntent = TryGetVelocityIntentSign(wallForward, out var velocitySign, out var velocitySpeed, out var velocitySpeedAlongWall);

            if(isWallRunStart && entrySpeed < lowSpeedForwardBiasThreshold) {
                var requestedBackwardByInput = hasKeyboardIntent && keyboardSign < 0;
                if(requestedBackwardByInput) {
                    return -1;
                }

                var hasSignificantBackwardVelocity = hasVelocityIntent &&
                                                     velocitySign < 0 &&
                                                     -velocitySpeedAlongWall >= lowSpeedBackwardVelocityOverride;
                return hasSignificantBackwardVelocity ? -1 : 1;
            }

            int chosenSign;
            if(hasKeyboardIntent) {
                chosenSign = keyboardSign;
            } else if(hasCameraIntent) {
                chosenSign = cameraSign;
            } else if(hasVelocityIntent) {
                chosenSign = velocitySign;
            } else if(_lastWallRunDirection.sqrMagnitude > 0.01f) {
                chosenSign = Vector3.Dot(_lastWallRunDirection.normalized, wallForward) >= 0f ? 1 : -1;
            } else {
                chosenSign = 1;
            }

            // At high speeds, avoid large "direction snap" flips unless keyboard intent is strongly committed.
            if(!hasVelocityIntent ||
               !(velocitySpeed >= highSpeedDirectionLockSpeed) ||
               chosenSign == velocitySign) return chosenSign;
            var allowHighSpeedFlip = hasKeyboardIntent && keyboardStrength >= highSpeedFlipOverrideStrength;
            if(!allowHighSpeedFlip) {
                chosenSign = velocitySign;
            }

            return chosenSign;
        }

        /// <summary>
        /// Applies inward stick force for curved surfaces to prevent drift. At high speed, centripetal requirement v²/r dominates.
        /// </summary>
        private Vector3 ApplyCurvedSurfaceStick(Vector3 velocity) {
            if(_curvedSurface == null || !_curvedSurface.TryGetNormalAt(transform.position, out var outwardNormal)) {
                return velocity;
            }

            var distToSurface = _curvedSurface.GetDistanceToSurface(transform.position);
            var over = Mathf.Max(0f, distToSurface - curvedStickTargetDistance);
            var speed = _currentWallRunSpeed;
            var r = Mathf.Max(0.01f, _curvedSurface.WorldRadius);
            var centripetal = speed * speed / r * Mathf.Max(0f, curvedStickCentripetalScale);
            var stickMag = over * curvedStickStrength + centripetal;
            // Logs showed stickMag 160–220 m/s with speed 31–36 → inward dominated and felt like stuck/jitter. Cap to fraction of tangent speed.
            stickMag = Mathf.Min(stickMag, speed * 1.5f);
            
            var inward = -outwardNormal;
            inward.y = 0f;
            if(inward.sqrMagnitude <= 0.0001f) return velocity;
            
            velocity += inward.normalized * stickMag;
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

        private bool TryGetKeyboardIntentSign(Vector3 wallForward, out int sign, out float strength) {
            sign = 0;
            strength = 0f;
            if(playerController == null) return false;

            var moveInput = playerController.moveInput;
            var inputMagnitude = moveInput.magnitude;
            if(inputMagnitude < keyboardIntentInputDeadzone) return false;

            var basis = playerController.PlayerTransform != null ? playerController.PlayerTransform : transform;
            var inputDirection = basis.forward * moveInput.y + basis.right * moveInput.x;
            inputDirection.y = 0f;
            if(inputDirection.sqrMagnitude <= 0.0001f) return false;

            inputDirection.Normalize();
            var alignment = Vector3.Dot(inputDirection, wallForward);
            var absAlignment = Mathf.Abs(alignment);
            if(absAlignment < keyboardIntentDotDeadzone) return false;

            sign = alignment >= 0f ? 1 : -1;
            strength = absAlignment * Mathf.Clamp01(inputMagnitude);
            return true;
        }

        private bool TryGetCameraIntentSign(Vector3 wallForward, Vector3 currentForward, out int sign) {
            sign = 0;

            currentForward.y = 0f;
            if(currentForward.sqrMagnitude <= 0.0001f) {
                currentForward = transform.forward;
                currentForward.y = 0f;
            }

            if(currentForward.sqrMagnitude <= 0.0001f) return false;
            currentForward.Normalize();

            var alignment = Vector3.Dot(currentForward, wallForward);
            if(Mathf.Abs(alignment) < cameraIntentDotDeadzone) return false;

            sign = alignment >= 0f ? 1 : -1;
            return true;
        }

        private bool TryGetVelocityIntentSign(Vector3 wallForward, out int sign, out float speed, out float speedAlongWall) {
            sign = 0;
            speed = 0f;
            speedAlongWall = 0f;

            var planarVelocity = GetPlanarVelocity();
            speed = planarVelocity.magnitude;
            if(speed < velocityIntentMinSpeed) return false;

            var velocityDirection = planarVelocity / speed;
            var alignment = Vector3.Dot(velocityDirection, wallForward);
            if(Mathf.Abs(alignment) <= 0.0001f) return false;

            speedAlongWall = speed * alignment;
            sign = speedAlongWall >= 0f ? 1 : -1;
            return true;
        }

        private Vector3 GetWallJumpForwardVelocityFromActiveRun() {
            if(_lastWallRunDirection.sqrMagnitude > 0.0001f) {
                return _lastWallRunDirection.normalized * _currentWallRunSpeed;
            }

            var wallForward = Vector3.Cross(WallNormal, Vector3.up);
            if(wallForward.sqrMagnitude < 0.0001f) {
                return Vector3.zero;
            }

            wallForward.Normalize();
            if(_hasLockedWallRunSign && _lockedWallRunSign < 0) {
                wallForward = -wallForward;
            }

            return wallForward * _currentWallRunSpeed;
        }

        #endregion

        #region Wall Jump

        /// <summary>
        /// Performs a wall jump, applying forces away from the wall and upward.
        /// </summary>
        public void WallJump() {
            if(!IsWallRunning) return;

            // Snapshot run-tangent momentum before StopWallRun clears locked sign state.
            var forwardVelocity = GetWallJumpForwardVelocityFromActiveRun();

            _stopReason = "wall_jump";
            StopWallRun(); // End state immediately

            // Apply cooldown to prevent immediate re-attachment
            _jumpCooldownTimer = wallJumpCooldown;

            // Combined jump force: add forward momentum from wall run
            var jumpVelocity = WallNormal * wallJumpSideForce + Vector3.up * wallJumpUpForce + forwardVelocity;

            // Apply to movement controller
            if(_movementController == null) return;
            _movementController.SetVelocity(new Vector3(jumpVelocity.x, 0, jumpVelocity.z));
            _movementController.VerticalVelocity = jumpVelocity.y;
        }

        #endregion

        #region Helpers

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
            if(_fpCamera == null) return;
            var tilt = IsRightWallRun ? wallRunCameraTilt : -wallRunCameraTilt;
            if(playerController.LookController != null) playerController.LookController.SetTargetTilt(tilt);
        }

        #endregion
    }
}
