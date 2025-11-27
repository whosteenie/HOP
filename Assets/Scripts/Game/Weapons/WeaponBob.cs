using UnityEngine;

namespace Game.Weapons {
    [DisallowMultipleComponent]
    public class WeaponBob : MonoBehaviour {
        [Header("References")]
        [SerializeField] private Transform playerTransform; // To read velocity

        [Header("Bob Settings")]
        [SerializeField] private float bobFrequency = 4f; // Base frequency (lowered from 6f)
        [SerializeField] private float bobHorizontalAmount = 0.01f;
        [SerializeField] private float bobVerticalAmount = 0.03f;
        [SerializeField] private float bobRollAmount = 0.3f;

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

        // Internal state
        private Vector3 _baseLocalPos;
        private Quaternion _baseLocalRot;
        private float _bobTimer;
        private float _currentBobIntensity;
        private float _targetBobIntensity;
        private float _landingBobTimer;
        private bool _wasGrounded = true;
        private CharacterController _characterController;
        private bool _initialized;

        private void Awake() {
            _baseLocalPos = transform.localPosition;
            _baseLocalRot = transform.localRotation;
        }

        private void OnEnable() {
            _baseLocalPos = transform.localPosition;
            _baseLocalRot = transform.localRotation;
            _bobTimer = 0f;
            _currentBobIntensity = 0f;
            _targetBobIntensity = 0f;
            _initialized = false;
        }

        private void TryInitialize() {
            if(_initialized) return;
            var current = transform.parent; // FPCamera
            if(current == null) return;
            
            current = current.parent; // Player
            if(current == null) return;
                
            playerTransform = current;
            _characterController = playerTransform.GetComponent<CharacterController>();

            if(_characterController == null) return;
            _initialized = true;
        }

        private void LateUpdate() {
            // Try to initialize if not already done
            if(!_initialized) {
                TryInitialize();
                if(!_initialized) return; // Still not ready, skip this frame
            }

            var deltaTime = Time.deltaTime;
            var isGrounded = _characterController.isGrounded;

            // Detect landing
            if(isGrounded && !_wasGrounded) {
                _landingBobTimer = landingBobDuration;
            }

            _wasGrounded = isGrounded;

            // Update landing bob timer
            if(_landingBobTimer > 0f) {
                _landingBobTimer -= deltaTime;
            }

            // Calculate movement speed
            var velocity = _characterController.velocity;
            velocity.y = 0;
            var speed = velocity.magnitude;

            // Determine target bob intensity based on speed
            if(!isGrounded) {
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

            // Calculate dynamic frequency based on speed (only when grounded and moving)
            var currentFrequency = bobFrequency;
            if(isGrounded && speed > 0.1f) {
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

            // Calculate bob offsets using sine waves
            var xBob = Mathf.Cos(_bobTimer) * bobHorizontalAmount * _currentBobIntensity;
            var yBob = Mathf.Sin(_bobTimer * 2f) * bobVerticalAmount * _currentBobIntensity;
            var rollBob = Mathf.Sin(_bobTimer) * bobRollAmount * _currentBobIntensity;

            // Add landing bob (bouncy effect)
            if(_landingBobTimer > 0f) {
                var landingT = _landingBobTimer / landingBobDuration;
                var landingCurve = Mathf.Sin(landingT * Mathf.PI);
                yBob -= landingCurve * landingBobAmount;
            }

            // Apply ADS multiplier
            var finalMultiplier = adsMultiplier;
            var bobOffset = new Vector3(xBob, yBob, 0f) * finalMultiplier;
            var bobRotation = new Vector3(0f, 0f, rollBob) * finalMultiplier;

            // Apply to transform
            transform.localPosition = _baseLocalPos + bobOffset;
            transform.localRotation = _baseLocalRot * Quaternion.Euler(bobRotation);
        }

        public void SetAdsMultiplier(float multiplier) {
            adsMultiplier = Mathf.Clamp01(multiplier);
        }

        public void TriggerLandingBob() {
            _landingBobTimer = landingBobDuration;
        }

        public void RecalibrateRestPose() {
            _baseLocalPos = transform.localPosition;
            _baseLocalRot = transform.localRotation;
            _bobTimer = 0f;
            _currentBobIntensity = 0f;
        }

        private void OnDrawGizmosSelected() {
            if(!Application.isPlaying || !_initialized) return;

            Gizmos.color = Color.green;
            var pos = transform.position + Vector3.up * 0.5f;
            Gizmos.DrawLine(pos, pos + Vector3.up * (_currentBobIntensity * 0.3f));
        }
    }
}