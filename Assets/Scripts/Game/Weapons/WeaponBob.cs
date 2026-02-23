using Game.Player;
using UnityEngine;

namespace Game.Weapons {
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
        private CharacterController _characterController;
        private PlayerController _playerController;
        private bool _initialized;
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

        private void Awake() {
            var bobTransform = transform;
            _baseLocalPos = bobTransform.localPosition;
            _baseLocalRot = bobTransform.localRotation;
        }

        private void OnEnable() {
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
        }

        private void TryInitialize() {
            if(_initialized) return;
            
            var current = transform.parent;
            var depth = 0;
            const int maxDepth = 6;

            while(current != null && depth < maxDepth) {
                // Try to find PlayerController first (preferred for jump/fall detection)
                var playerController = current.GetComponent<PlayerController>();
                if(playerController != null) {
                    _playerController = playerController;
                    _characterController = playerController.CharacterController;
                    _initialized = true;
                    return;
                }

                // Fallback to CharacterController if PlayerController not found
                var controller = current.GetComponent<CharacterController>();
                if(controller != null) {
                    _characterController = controller;
                    _initialized = true;
                    return;
                }

                current = current.parent;
                depth++;
            }
        }

        private void LateUpdate() {
            // Try to initialize if not already done
            if(!_initialized) {
                TryInitialize();
                if(!_initialized) return; // Still not ready, skip this frame
            }

            var deltaTime = Time.deltaTime;
            
            // Use PlayerController's IsGrounded if available (accounts for -3f constant), otherwise fallback to CharacterController
            var isGrounded = _playerController != null ? _playerController.IsGrounded : _characterController.isGrounded;
            var isMantling = _playerController != null &&
                             _playerController.MantleController != null &&
                             _playerController.MantleController.IsMantling;

            if(isMantling && !_wasMantling) {
                _pendingMantleLandingBob = false;
                _mantleLandingBobPlayed = false;
            }

            if(!isMantling && _wasMantling) {
                // Mantle settle can briefly bounce grounded state; suppress landing bob during that window.
                _suppressLandingUntil = Time.time + MantleLandingBobSuppressSeconds;
                _landingBobTimer = 0f;
                _targetJumpFallOffset = 0f;
                _jumpFallOffset = 0f;
                _jumpInitiated = false;
                _pendingMantleLandingBob = true;
                _mantleLandingBobPlayed = false;
            }

            if(_pendingMantleLandingBob && !_mantleLandingBobPlayed && isGrounded) {
                _landingBobTimer = landingBobDuration;
                _mantleLandingBobPlayed = true;
                _pendingMantleLandingBob = false;
            }

            var suppressLandingFromMantle = isMantling || Time.time < _suppressLandingUntil;

            if(!suppressLandingFromMantle) {
                _pendingMantleLandingBob = false;
            }

            // Detect landing
            var wasGrounded = _wasGrounded;

            if(isGrounded && !wasGrounded && !suppressLandingFromMantle) {
                // Only start landing bob if jump wasn't initiated (allows normal landing)
                // If jump was initiated, skip landing bob and let velocity system handle it
                if(!_jumpInitiated) {
                    _landingBobTimer = landingBobDuration;
                }
                // Reset jump/fall state when landing
                _targetJumpFallOffset = 0f;
                // Reset jump flag when we actually land (safety reset)
                _jumpInitiated = false;
            }

            // Update landing bob timer
            if(_landingBobTimer > 0f) {
                _landingBobTimer -= deltaTime;
            }

            // Calculate movement speed
            var velocity = _characterController.velocity;
            velocity.y = 0;
            var speed = velocity.magnitude;

            // Use PlayerController's VerticalVelocity if available (accounts for -3f constant), otherwise use CharacterController velocity.y
            var verticalVelocity = _playerController != null ? _playerController.GetVerticalVelocity() : _characterController.velocity.y;
            
            // Reset jump flag when we start falling (at apex) - this allows normal landings to play landing bob
            // If we're falling and the flag is still set, it means we're past the jump phase and can allow landing bob
            if(!isGrounded && verticalVelocity < -0.1f && _jumpInitiated) {
                _jumpInitiated = false;
            }
            
            // Velocity-based jump/fall offset: inversely correlated with vertical velocity
            // Positive velocity (rising) = negative offset (weapon lower)
            // Negative velocity (falling) = positive offset (weapon higher)
            if(isGrounded) {
                // Grounded: reset to idle
                _targetJumpFallOffset = 0f;
            } else {
                // Normalize velocity to 0-1 range based on max velocity
                var normalizedVelocity = Mathf.Clamp01(Mathf.Abs(verticalVelocity) / maxVelocityForNormalization);
                
                // Apply curve to make correlation more natural (more responsive at low velocities, less extreme at high)
                // Using power curve: lower exponent = more sensitive at start, less extreme at end
                var curvedVelocity = Mathf.Pow(normalizedVelocity, velocityCurveExponent);

                _targetJumpFallOffset = verticalVelocity switch {
                    > 0.1f => Mathf.Lerp(0f, maxJumpLowerAmount, curvedVelocity),
                    < -0.1f => Mathf.Lerp(0f, maxFallRaiseAmount, curvedVelocity),
                    _ => 0f
                };
            }

            // Smooth the offset transition
            _jumpFallOffset = Mathf.Lerp(_jumpFallOffset, _targetJumpFallOffset, jumpFallSmoothSpeed * deltaTime);
            // Clamp to prevent extreme values
            _jumpFallOffset = Mathf.Clamp(_jumpFallOffset, maxJumpLowerAmount, maxFallRaiseAmount);
            _wasGrounded = isGrounded;
            _wasMantling = isMantling;

            // Determine target bob intensity based on speed
            // Disable bob when not grounded OR when sliding
            // Allow bob when wall running (treat like grounded for bobbing purposes)
            var isSliding = _playerController != null && _playerController.MovementController != null && _playerController.MovementController.IsSliding;
            var isWallRunning = _playerController != null && _playerController.WallRunController != null && _playerController.WallRunController.IsWallRunning;
            var canBob = isGrounded || isWallRunning;
            
            if(!canBob || isSliding) {
                _targetBobIntensity = 0f;
            } else if(speed < 0.1f) {
                _targetBobIntensity = 0f;
            } else if(speed < walkSpeed) {
                _targetBobIntensity = Mathf.InverseLerp(0.1f, walkSpeed, speed);
            } else {
                var sprintFactor = Mathf.InverseLerp(walkSpeed, sprintSpeed, speed);
                _targetBobIntensity = Mathf.Lerp(1f, sprintBobMultiplier, sprintFactor);
            }

            // Smooth intensity transitions
            _currentBobIntensity = Mathf.Lerp(_currentBobIntensity, _targetBobIntensity, smoothSpeed * deltaTime);

            // Calculate dynamic frequency based on speed (only when grounded/wall running and moving)
            var currentFrequency = bobFrequency;
            if(canBob && speed > 0.1f) {
                // Scale frequency from minFrequency (walking) to maxFrequency (sprinting)
                if(speed < walkSpeed) {
                    // Walking: frequency scales from minFrequency to base frequency
                    currentFrequency =
                        Mathf.Lerp(minFrequency, bobFrequency, Mathf.InverseLerp(0.1f, walkSpeed, speed));
                } else {
                    // Running/sprinting: frequency scales from base frequency to maxFrequency
                    var sprintFactor = Mathf.InverseLerp(walkSpeed, sprintSpeed, speed);
                    currentFrequency = Mathf.Lerp(bobFrequency, maxFrequency, sprintFactor);
                }

                // Clamp frequency to prevent going too crazy
                currentFrequency = Mathf.Clamp(currentFrequency, minFrequency, maxFrequency);
            }

            // Advance bob timer based on dynamic frequency
            if(_currentBobIntensity > 0.01f) {
                _bobTimer += deltaTime * currentFrequency;
            } else {
                _bobTimer = Mathf.Lerp(_bobTimer, 0f, smoothSpeed * deltaTime);
            }

            // Use local movement direction so bob reflects actual motion (e.g. wall slide -> strafe bias)
            var localVelocity = velocity;
            if(_playerController != null && _playerController.PlayerTransform != null) {
                localVelocity = _playerController.PlayerTransform.InverseTransformDirection(velocity);
            }
            _smoothedLocalVelocity = Vector3.Lerp(_smoothedLocalVelocity, localVelocity, directionSmoothing * deltaTime);

            var planarSpeed = Mathf.Max(speed, 0.001f);
            var directionalSpeed = new Vector2(_smoothedLocalVelocity.x, _smoothedLocalVelocity.z).magnitude;
            var useDirectional = directionalSpeed >= directionMinSpeed;
            var strafeFactor = useDirectional ? Mathf.Clamp(_smoothedLocalVelocity.x / planarSpeed, -1f, 1f) : 0f;
            var forwardFactor = useDirectional ? Mathf.Clamp(_smoothedLocalVelocity.z / planarSpeed, -1f, 1f) : 0f;

            var bobScale = isWallRunning ? wallRunBobScale : 1f;
            var cycle = _bobTimer;
            var xWave = Mathf.Sin(cycle + Mathf.PI * 0.5f);
            var yWave = Mathf.Sin(cycle * 2f);
            var zWave = Mathf.Cos(cycle * 2f);
            var rollWave = Mathf.Sin(cycle);
            var forwardLateralWave = Mathf.Sin(cycle * 2f + Mathf.PI * 0.5f);

            // Human-like grounded movement bob: lateral sway + step bounce + subtle depth pulse.
            var forwardLateralBob = forwardLateralWave * forwardLateralSwayAmount * Mathf.Abs(forwardFactor);
            var xBob = (xWave * bobHorizontalAmount +
                        strafeFactor * bobHorizontalAmount * strafeOffsetInfluence +
                        forwardLateralBob) * _currentBobIntensity * bobScale;
            var yBob = yWave * bobVerticalAmount * _currentBobIntensity * bobScale;
            var zBob = zWave * bobForwardAmount * _currentBobIntensity * Mathf.Abs(forwardFactor) * bobScale;
            var rollBob =
                (rollWave * bobRollAmount - strafeFactor * bobRollAmount * strafeRollInfluence) * _currentBobIntensity *
                bobScale;

            // Apply jump/fall offset only when landing bob is not active to prevent jitter
            // When landing, the landing bob handles the animation, so we skip the jump/fall offset
            var finalYBob = yBob;
            if(_landingBobTimer <= 0f) {
                // Landing bob is not active, apply jump/fall offset
                finalYBob += _jumpFallOffset;
            } else {
                // Landing bob is active, smoothly reset jump/fall offset to 0 to prevent sudden jumps
                _jumpFallOffset = Mathf.Lerp(_jumpFallOffset, 0f, jumpFallSmoothSpeed * deltaTime);
            }

            // Add landing bob (bouncy effect) - only if jump wasn't initiated
            if(_landingBobTimer > 0f && !_jumpInitiated) {
                var landingT = _landingBobTimer / landingBobDuration;
                var landingCurve = Mathf.Sin(landingT * Mathf.PI);
                finalYBob -= landingCurve * landingBobAmount;
            }

            // Apply ADS multiplier
            var finalMultiplier = adsMultiplier;
            var bobOffset = new Vector3(xBob, finalYBob, zBob) * finalMultiplier;
            var bobRotation = new Vector3(0f, 0f, rollBob) * finalMultiplier;

            // Subtle idle breathing when grounded and nearly stationary.
            var idleEligible = isGrounded && !isWallRunning && !isSliding && speed <= idleBreathSpeedThreshold;
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

            // Apply to transform
            transform.localPosition = _baseLocalPos + bobOffset + idleOffset;
            transform.localRotation = _baseLocalRot * Quaternion.Euler(bobRotation + idleRotation);
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

        public void TriggerLandingBob() {
            _landingBobTimer = landingBobDuration;
        }

        public void RecalibrateRestPose() {
            var bobTransform = transform;
            _baseLocalPos = bobTransform.localPosition;
            _baseLocalRot = bobTransform.localRotation;
            _bobTimer = 0f;
            _currentBobIntensity = 0f;
            _smoothedLocalVelocity = Vector3.zero;
            _idleBreathTimer = 0f;
            _idleBreathIntensity = 0f;
        }

        private void OnDrawGizmosSelected() {
            if(!Application.isPlaying || !_initialized) return;

            Gizmos.color = Color.green;
            var pos = transform.position + Vector3.up * 0.5f;
            Gizmos.DrawLine(pos, pos + Vector3.up * (_currentBobIntensity * 0.3f));
        }
    }
}
