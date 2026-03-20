using UnityEngine;

namespace Game.Weapon.Presentation {
    public interface IWeaponBobContext {
        Transform PlayerTransform { get; }
        Vector3 FullVelocity { get; }
        float VerticalVelocity { get; }
        bool IsGrounded { get; }
        bool IsMantling { get; }
        bool IsSliding { get; }
        bool IsWallRunning { get; }
        bool IsJumpHeld { get; }
    }

    [DisallowMultipleComponent]
    public class WeaponBob : MonoBehaviour {
        [Header("Bob Settings")]
        [SerializeField] private float bobFrequency = 4f; // Base frequency (lowered from 6f)
        [SerializeField] private float bobHorizontalAmount = 0.01f;
        [SerializeField] private float bobVerticalAmount = 0.03f;
        [SerializeField] private float bobForwardAmount = 0.008f;
        [SerializeField] private float bobRollAmount = 0.3f;
        [SerializeField] private float strafeOffsetInfluence = 0.5f;
        [SerializeField] private float strafeRollInfluence = 0.4f;
        [SerializeField] private float forwardLateralSwayAmount = 0.0035f;
        [SerializeField] private float wallRunBobScale = 0.85f;
        [SerializeField] private float directionSmoothing = 10f;
        [SerializeField] private float directionMinSpeed = 1.5f;

        [Header("Speed Thresholds")]
        [SerializeField] private float walkSpeed = 5f;
        [SerializeField] private float sprintSpeed = 10f;
        [SerializeField] private float sprintBobMultiplier = 1.15f;

        [Header("Frequency Scaling")]
        [SerializeField] private float minFrequency = 3.5f; // Minimum frequency (walking)
        [SerializeField] private float maxFrequency = 7.5f;

        [Header("Dynamics")]
        [SerializeField] private float smoothSpeed = 12f;
        [SerializeField] private float landingBobAmount = 0.04f;
        [SerializeField] private float landingBobDuration = 0.15f;
        [SerializeField, Min(0f)] private float minimumLandingBobInterval = 0.12f;

        [Header("Feature Toggles")]
        [SerializeField] private bool enableMovementBob = true;
        [SerializeField] private bool enableIdleBreath = true;
        [SerializeField] private bool enableJumpFallOffset = true;
        [SerializeField] private bool enableLandingBob = true;

        [Header("ADS")]
        [Range(0f, 1f)]
        [SerializeField] private float adsMultiplier = 0.2f;

        [Header("Idle Breath")]
        [SerializeField] private float idleBreathFrequency = 0.28f;
        [SerializeField] private float idleBreathVerticalAmount = 0.0022f;
        [SerializeField] private float idleBreathPitchAmount = 0.08f;
        [SerializeField] private float idleBreathRollAmount = 0.05f;
        [SerializeField] private float idleBreathBlendSpeed = 6f;
        [SerializeField] private float idleBreathSpeedThreshold = 0.12f;

        [Header("Jump / Fall Animation")]
        [Tooltip("Maximum weapon offset when at max jump velocity (negative = lower on jump)")]
        [SerializeField] private float maxJumpLowerAmount = -0.15f;
        [Tooltip("Maximum weapon offset when at max fall velocity (positive = raise on fall)")]
        [SerializeField] private float maxFallRaiseAmount = 0.08f;
        [Tooltip("Smoothing speed for jump/fall transitions")]
        [SerializeField] private float jumpFallSmoothSpeed = 8f;
        [Tooltip("Maximum vertical velocity to use for normalization (higher = less sensitive). " +
                 "Set to ~50 to allow mega jump pads (30f height, ~42 m/s) and account for high fall velocities")]
        [SerializeField] private float maxVelocityForNormalization = 50f;
        [Tooltip("Curve exponent for velocity correlation (1.0 = linear, 0.5 = square root, 2.0 = squared). " +
                 "Lower values = more sensitive at low velocities, less extreme at high velocities")]
        [SerializeField] private float velocityCurveExponent = 0.5f;

        // Internal state
        private Vector3 _baseLocalPos;
        private Quaternion _baseLocalRot;
        private float _bobTimer;
        private float _currentBobIntensity;
        private float _targetBobIntensity;
        private float _landingBobTimer;
        private bool _wasGrounded = true;
        private IWeaponBobContext _context;
        private bool _initialized;
        private bool _hierarchyReferencesResolved;
        private float _jumpFallOffset;
        private float _targetJumpFallOffset;
        private bool _jumpInitiated;
        private Vector3 _smoothedLocalVelocity;
        private float _idleBreathTimer;
        private float _idleBreathIntensity;
        private bool _wasMantling;
        private float _suppressLandingUntil;
        private const float MantleLandingBobSuppressSeconds = 0.2f;
        private bool _pendingMantleLandingBob;
        private bool _mantleLandingBobPlayed;
        private float _lastLandingBobTime = float.NegativeInfinity;

        private void Awake() {
            ResolveContextFromHierarchy();
            TryInitialize();
            var bobTransform = transform;
            _baseLocalPos = bobTransform.localPosition;
            _baseLocalRot = bobTransform.localRotation;
        }

        private void OnEnable() {
            _hierarchyReferencesResolved = false;
            ResolveContextFromHierarchy();
            var bobTransform = transform;
            _baseLocalPos = bobTransform.localPosition;
            _baseLocalRot = bobTransform.localRotation;
            _bobTimer = 0f;
            _currentBobIntensity = 0f;
            _targetBobIntensity = 0f;
            _initialized = false;
            _smoothedLocalVelocity = Vector3.zero;
            _idleBreathTimer = 0f;
            _idleBreathIntensity = 0f;
            _wasMantling = false;
            _suppressLandingUntil = 0f;
            _pendingMantleLandingBob = false;
            _mantleLandingBobPlayed = false;
            _lastLandingBobTime = float.NegativeInfinity;
        }

        private void OnTransformParentChanged() {
            _initialized = false;
            _context = null;
            _hierarchyReferencesResolved = false;
            ResolveContextFromHierarchy();
            TryInitialize();
        }

        private void ResolveContextFromHierarchy() {
            if(_hierarchyReferencesResolved) return;
            _hierarchyReferencesResolved = true;

            var current = transform.parent;
            var depth = 0;
            const int maxDepth = 6;

            while(current != null && depth < maxDepth) {
                var behaviours = current.GetComponents<MonoBehaviour>();
                foreach(var behaviour in behaviours) {
                    if(behaviour == null) continue;
                    // ReSharper disable once UseNegatedPatternMatching
                    var bobContext = behaviour as IWeaponBobContext;
                    if(bobContext == null) continue;
                    _context = bobContext;
                    return;
                }

                current = current.parent;
                depth++;
            }
        }

        private void TryInitialize() {
            if(_initialized) return;
            _initialized = _context != null;
        }

        private void LateUpdate() {
            if(!TryInitializeForLateUpdate()) return;

            var deltaTime = Time.deltaTime;
            var state = CaptureFrameState();

            HandleMantleState(state.IsGrounded, state.IsMantling);
            var suppressLandingFromMantle = ResolveMantleLandingSuppression(state.IsMantling);
            HandleLandingTransition(state.IsGrounded, suppressLandingFromMantle);
            UpdateLandingBobTimer(deltaTime);
            UpdateJumpFallOffset(state.IsGrounded, state.VerticalVelocity, deltaTime);

            _wasGrounded = state.IsGrounded;
            _wasMantling = state.IsMantling;

            var bobMotion = ComputeBobMotion(state, deltaTime);
            var finalYBob = ComposeFinalYBob(bobMotion.yBob, deltaTime);

            var finalMultiplier = adsMultiplier;
            var bobOffset = new Vector3(bobMotion.xBob, finalYBob, bobMotion.zBob) * finalMultiplier;
            var bobRotation = new Vector3(0f, 0f, bobMotion.rollBob) * finalMultiplier;

            var idleMotion = ComputeIdleMotion(state, deltaTime, finalMultiplier);
            ApplyPose(bobOffset, bobRotation, idleMotion.idleOffset, idleMotion.idleRotation);
        }

        private bool TryInitializeForLateUpdate() {
            if(_initialized) return true;

            TryInitialize();
            return _initialized;
        }

        private FrameState CaptureFrameState() {
            var velocity = _context.FullVelocity;
            velocity.y = 0f;

            return new FrameState(
                _context.IsGrounded,
                _context.IsMantling,
                _context.IsSliding,
                _context.IsWallRunning,
                velocity,
                velocity.magnitude,
                _context.VerticalVelocity);
        }

        private void HandleMantleState(bool isGrounded, bool isMantling) {
            switch(isMantling) {
                case true when !_wasMantling:
                    _pendingMantleLandingBob = false;
                    _mantleLandingBobPlayed = false;
                    break;
                case false when _wasMantling:
                    _suppressLandingUntil = Time.time + MantleLandingBobSuppressSeconds;
                    _landingBobTimer = 0f;
                    _targetJumpFallOffset = 0f;
                    _jumpFallOffset = 0f;
                    _jumpInitiated = false;
                    _pendingMantleLandingBob = true;
                    _mantleLandingBobPlayed = false;
                    break;
            }

            if(!_pendingMantleLandingBob || _mantleLandingBobPlayed || !isGrounded) return;
            if(CanStartLandingBob(ignoreJumpHeld: true)) {
                StartLandingBob();
            }

            _mantleLandingBobPlayed = true;
            _pendingMantleLandingBob = false;
        }

        private bool ResolveMantleLandingSuppression(bool isMantling) {
            var suppressLandingFromMantle = isMantling || Time.time < _suppressLandingUntil;
            if(!suppressLandingFromMantle) {
                _pendingMantleLandingBob = false;
            }

            return suppressLandingFromMantle;
        }

        private void HandleLandingTransition(bool isGrounded, bool suppressLandingFromMantle) {
            var wasGrounded = _wasGrounded;
            if(!isGrounded || wasGrounded || suppressLandingFromMantle) return;

            if(!_jumpInitiated && CanStartLandingBob(ignoreJumpHeld: false)) {
                StartLandingBob();
            }

            _targetJumpFallOffset = 0f;
            _jumpInitiated = false;
        }

        private void UpdateLandingBobTimer(float deltaTime) {
            switch(enableLandingBob) {
                case true when _landingBobTimer > 0f:
                    _landingBobTimer -= deltaTime;
                    break;
                case false:
                    _landingBobTimer = 0f;
                    break;
            }
        }

        private void UpdateJumpFallOffset(bool isGrounded, float verticalVelocity, float deltaTime) {
            if(!isGrounded && verticalVelocity < -0.1f && _jumpInitiated) {
                _jumpInitiated = false;
            }

            if(!enableJumpFallOffset || isGrounded) {
                _targetJumpFallOffset = 0f;
            } else {
                var normalizedVelocity = Mathf.Clamp01(Mathf.Abs(verticalVelocity) / maxVelocityForNormalization);
                var curvedVelocity = Mathf.Pow(normalizedVelocity, velocityCurveExponent);

                _targetJumpFallOffset = verticalVelocity switch {
                    > 0.1f => Mathf.Lerp(0f, maxJumpLowerAmount, curvedVelocity),
                    < -0.1f => Mathf.Lerp(0f, maxFallRaiseAmount, curvedVelocity),
                    _ => 0f
                };
            }

            _jumpFallOffset = Mathf.Lerp(_jumpFallOffset, _targetJumpFallOffset, jumpFallSmoothSpeed * deltaTime);
            _jumpFallOffset = Mathf.Clamp(_jumpFallOffset, maxJumpLowerAmount, maxFallRaiseAmount);
        }

        private (float xBob, float yBob, float zBob, float rollBob) ComputeBobMotion(FrameState state, float deltaTime) {
            var canBob = state.IsGrounded || state.IsWallRunning;

            if(!enableMovementBob || !canBob || state.IsSliding || state.Speed < 0.1f) {
                _targetBobIntensity = 0f;
            } else if(state.Speed < walkSpeed) {
                _targetBobIntensity = Mathf.InverseLerp(0.1f, walkSpeed, state.Speed);
            } else {
                var sprintFactor = Mathf.InverseLerp(walkSpeed, sprintSpeed, state.Speed);
                _targetBobIntensity = Mathf.Lerp(1f, sprintBobMultiplier, sprintFactor);
            }

            _currentBobIntensity = Mathf.Lerp(_currentBobIntensity, _targetBobIntensity, smoothSpeed * deltaTime);

            var currentFrequency = bobFrequency;
            if(enableMovementBob && canBob && state.Speed > 0.1f) {
                if(state.Speed < walkSpeed) {
                    currentFrequency = Mathf.Lerp(minFrequency, bobFrequency, Mathf.InverseLerp(0.1f, walkSpeed, state.Speed));
                } else {
                    var sprintFactor = Mathf.InverseLerp(walkSpeed, sprintSpeed, state.Speed);
                    currentFrequency = Mathf.Lerp(bobFrequency, maxFrequency, sprintFactor);
                }

                currentFrequency = Mathf.Clamp(currentFrequency, minFrequency, maxFrequency);
            }

            if(_currentBobIntensity > 0.01f) {
                _bobTimer += deltaTime * currentFrequency;
            } else {
                _bobTimer = Mathf.Lerp(_bobTimer, 0f, smoothSpeed * deltaTime);
            }

            var localVelocity = state.PlanarVelocity;
            if(_context.PlayerTransform != null) {
                localVelocity = _context.PlayerTransform.InverseTransformDirection(state.PlanarVelocity);
            }

            _smoothedLocalVelocity = Vector3.Lerp(_smoothedLocalVelocity, localVelocity, directionSmoothing * deltaTime);

            var planarSpeed = Mathf.Max(state.Speed, 0.001f);
            var directionalSpeed = new Vector2(_smoothedLocalVelocity.x, _smoothedLocalVelocity.z).magnitude;
            var useDirectional = directionalSpeed >= directionMinSpeed;
            var strafeFactor = useDirectional ? Mathf.Clamp(_smoothedLocalVelocity.x / planarSpeed, -1f, 1f) : 0f;
            var forwardFactor = useDirectional ? Mathf.Clamp(_smoothedLocalVelocity.z / planarSpeed, -1f, 1f) : 0f;

            var bobScale = state.IsWallRunning ? wallRunBobScale : 1f;
            var cycle = _bobTimer;
            var xWave = Mathf.Sin(cycle + Mathf.PI * 0.5f);
            var yWave = Mathf.Sin(cycle * 2f);
            var zWave = Mathf.Cos(cycle * 2f);
            var rollWave = Mathf.Sin(cycle);
            var forwardLateralWave = Mathf.Sin(cycle * 2f + Mathf.PI * 0.5f);

            var forwardLateralBob = forwardLateralWave * forwardLateralSwayAmount * Mathf.Abs(forwardFactor);
            var xBob = (xWave * bobHorizontalAmount +
                        strafeFactor * bobHorizontalAmount * strafeOffsetInfluence +
                        forwardLateralBob) * _currentBobIntensity * bobScale;
            var yBob = yWave * bobVerticalAmount * _currentBobIntensity * bobScale;
            var zBob = zWave * bobForwardAmount * _currentBobIntensity * Mathf.Abs(forwardFactor) * bobScale;
            var rollBob =
                (rollWave * bobRollAmount - strafeFactor * bobRollAmount * strafeRollInfluence) * _currentBobIntensity *
                bobScale;

            return (xBob, yBob, zBob, rollBob);
        }

        private float ComposeFinalYBob(float yBob, float deltaTime) {
            var finalYBob = yBob;
            if(enableJumpFallOffset && _landingBobTimer <= 0f) {
                finalYBob += _jumpFallOffset;
            } else {
                _jumpFallOffset = Mathf.Lerp(_jumpFallOffset, 0f, jumpFallSmoothSpeed * deltaTime);
            }

            if(!enableLandingBob || !(_landingBobTimer > 0f) || _jumpInitiated) return finalYBob;
            var landingT = _landingBobTimer / landingBobDuration;
            var landingCurve = Mathf.Sin(landingT * Mathf.PI);
            finalYBob -= landingCurve * landingBobAmount;

            return finalYBob;
        }

        private (Vector3 idleOffset, Vector3 idleRotation) ComputeIdleMotion(FrameState state, float deltaTime,
            float finalMultiplier) {
            var idleEligible = enableIdleBreath && state is { IsGrounded: true, IsWallRunning: false, IsSliding: false } &&
                               state.Speed <= idleBreathSpeedThreshold;
            var targetIdleIntensity = idleEligible ? 1f : 0f;
            _idleBreathIntensity = Mathf.Lerp(_idleBreathIntensity, targetIdleIntensity, idleBreathBlendSpeed * deltaTime);

            if(_idleBreathIntensity > 0.001f) {
                _idleBreathTimer += deltaTime * idleBreathFrequency * Mathf.PI * 2f;
            } else {
                _idleBreathTimer = Mathf.Lerp(_idleBreathTimer, 0f, idleBreathBlendSpeed * deltaTime);
            }

            var idleWave = Mathf.Sin(_idleBreathTimer);
            var idleOffset = new Vector3(0f, idleWave * idleBreathVerticalAmount * _idleBreathIntensity, 0f) * finalMultiplier;
            var idleRotation = new Vector3(
                idleWave * idleBreathPitchAmount * _idleBreathIntensity,
                0f,
                Mathf.Cos(_idleBreathTimer) * idleBreathRollAmount * _idleBreathIntensity) * finalMultiplier;

            return (idleOffset, idleRotation);
        }

        private void ApplyPose(Vector3 bobOffset, Vector3 bobRotation, Vector3 idleOffset, Vector3 idleRotation) {
            transform.localPosition = _baseLocalPos + bobOffset + idleOffset;
            transform.localRotation = _baseLocalRot * Quaternion.Euler(bobRotation + idleRotation);
        }

        private readonly struct FrameState {
            public FrameState(bool isGrounded, bool isMantling, bool isSliding, bool isWallRunning,
                Vector3 planarVelocity, float speed, float verticalVelocity) {
                IsGrounded = isGrounded;
                IsMantling = isMantling;
                IsSliding = isSliding;
                IsWallRunning = isWallRunning;
                PlanarVelocity = planarVelocity;
                Speed = speed;
                VerticalVelocity = verticalVelocity;
            }

            public bool IsGrounded { get; }
            public bool IsMantling { get; }
            public bool IsSliding { get; }
            public bool IsWallRunning { get; }
            public Vector3 PlanarVelocity { get; }
            public float Speed { get; }
            public float VerticalVelocity { get; }
        }

        public void SetAdsMultiplier(float multiplier) {
            adsMultiplier = Mathf.Clamp01(multiplier);
        }

        /// <summary>
        /// Called when a jump is initiated (from input or jump pad).
        /// Sets flag to prevent landing bob from playing and cancels any active landing animation.
        /// </summary>
        public void OnJumpInitiated() {
            // Set flag to prevent landing bob from playing
            _jumpInitiated = true;
            // Cancel landing bob animation if it's playing to prevent additive jitter
            _landingBobTimer = 0f;
        }

        public void ConfigureFeatures(bool movementBob, bool idleBreath, bool jumpFallOffset, bool landingBob) {
            enableMovementBob = movementBob;
            enableIdleBreath = idleBreath;
            enableJumpFallOffset = jumpFallOffset;
            enableLandingBob = landingBob;

            if(!enableJumpFallOffset) {
                _targetJumpFallOffset = 0f;
                _jumpFallOffset = 0f;
            }

            if(!enableLandingBob) {
                _landingBobTimer = 0f;
            }
        }

        private bool CanStartLandingBob(bool ignoreJumpHeld) {
            if(!enableLandingBob) return false;
            if(Time.time - _lastLandingBobTime < minimumLandingBobInterval) return false;

            if(ignoreJumpHeld) {
                return true;
            }

            return !_context.IsJumpHeld;
        }

        private void StartLandingBob() {
            _landingBobTimer = landingBobDuration;
            _lastLandingBobTime = Time.time;
        }

        private void OnDrawGizmosSelected() {
            if(!Application.isPlaying || !_initialized) return;

            Gizmos.color = Color.green;
            var pos = transform.position + Vector3.up * 0.5f;
            Gizmos.DrawLine(pos, pos + Vector3.up * (_currentBobIntensity * 0.3f));
        }
    }
}
