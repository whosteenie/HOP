using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Handles camera/look logic for the player.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerLookController : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private PlayerAnimationController _animationController;
        private PlayerMovementController _movementController;
        private UpperBodyPitch _upperBodyPitch;
        private CinemachineCamera _fpCamera;
        private Transform _playerTransform;

        [Header("Look Parameters")]
        [SerializeField] private Vector2 defaultLookSensitivity = new(0.1f, 0.1f);

        [Header("FOV (speed boost)")]
        [SerializeField] private float baseFov = 80f;

        [SerializeField] private float sprintStartSpeed = 9f;
        [SerializeField] private float maxSpeedForFov = 30f;
        [SerializeField] private float maxFov = 100f;
        [SerializeField] private float fovSmoothTime = 0.12f;

        // Look constants
        private const float PitchLimit = 90f;

        // Look state
        private float _currentPitch;
        private float _fovVel;
        private float _targetFov;

        // Input (read from PlayerController)
        private Vector2 LookInput => playerController != null ? playerController.lookInput : Vector2.zero;

        private void Awake() {
            playerController ??= GetComponent<PlayerController>();
            _playerTransform ??= playerController.PlayerTransform;
            _animationController ??= playerController.AnimationController;
            _upperBodyPitch ??= playerController.UpperBodyPitch;
            _movementController ??= playerController.MovementController;
            _fpCamera ??= playerController.FpCamera;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(_movementController == null) {
                _movementController = playerController.MovementController;
            }
        }

        public void UpdateLook() {
            var sensitivity = GetLookSensitivity();
            var lookDelta = new Vector2(LookInput.x * sensitivity.x, LookInput.y * sensitivity.y);

            UpdatePitch(lookDelta.y);
            UpdateYaw(lookDelta.x);

            if(_animationController != null) {
                _animationController.UpdateTurnAnimation(lookDelta.x);
            }

            if(_upperBodyPitch != null) {
                _upperBodyPitch.SetLocalPitchFromCamera(CurrentPitch);
            }
        }

        /// <summary>
        /// Gets the look sensitivity from PlayerPrefs, with fallback to default value.
        /// Handles invert Y setting.
        /// </summary>
        private Vector2 GetLookSensitivity() {
            float sensitivityValue;

            // Load single sensitivity value, defaulting to 0.1 if not set
            // If old separate X/Y values exist, use X as the new unified value
            if(PlayerPrefs.HasKey("Sensitivity")) {
                sensitivityValue = PlayerPrefs.GetFloat("Sensitivity", defaultLookSensitivity.x);
            } else if(PlayerPrefs.HasKey("SensitivityX")) {
                sensitivityValue = PlayerPrefs.GetFloat("SensitivityX", defaultLookSensitivity.x);
                // Migrate to new unified key
                PlayerPrefs.SetFloat("Sensitivity", sensitivityValue);
            } else {
                sensitivityValue = defaultLookSensitivity.x;
            }

            // Apply invert Y multiplier
            var invertY = PlayerPrefs.GetInt("InvertY", 0) == 1;
            var yMultiplier = invertY ? -1f : 1f;

            return new Vector2(sensitivityValue, sensitivityValue * yMultiplier);
        }

        public void UpdateSpeedFov() {
            if(!IsOwner || _fpCamera == null) return;

            if(_movementController == null) {
                _movementController = GetComponent<PlayerMovementController>();
            }

            if(_movementController == null) return;

            var speed = _movementController.HorizontalVelocity.magnitude;
            var t = Mathf.InverseLerp(sprintStartSpeed, maxSpeedForFov, speed);
            t = Mathf.Pow(t, 0.65f);

            _targetFov = Mathf.Lerp(baseFov, maxFov, t);

            var desiredFov = _sniperZoomActive
                ? Mathf.Clamp(_sniperZoomFovOverride > 0f ? _sniperZoomFovOverride : baseFov, 5f, maxFov)
                : _targetFov;

            if(_sniperZoomActive) {
                _fpCamera.Lens.FieldOfView = desiredFov;
                return;
            }

            var currentFov = _fpCamera.Lens.FieldOfView;
            currentFov = Mathf.SmoothDamp(currentFov, desiredFov, ref _fovVel, fovSmoothTime);
            _fpCamera.Lens.FieldOfView = currentFov;
        }

        private void UpdatePitch(float pitchDelta) {
            CurrentPitch -= pitchDelta;
            if(_fpCamera != null) {
                _fpCamera.transform.localRotation = Quaternion.Euler(CurrentPitch, 0f, 0f);
            }
        }

        private void UpdateYaw(float yawDelta) {
            _playerTransform.Rotate(Vector3.up * yawDelta);
        }

        public void ResetPitch() {
            CurrentPitch = 0f;
            if(_fpCamera != null) {
                _fpCamera.transform.localRotation = Quaternion.identity;
            }
        }

        // Public getters
        private float CurrentPitch {
            get => _currentPitch;
            set => _currentPitch = Mathf.Clamp(value, -PitchLimit, PitchLimit);
        }

        public float BaseFov => baseFov;

        private bool _sniperZoomActive;
        private float _sniperZoomFovOverride;

        public void SetSniperZoomActive(bool active, float zoomFov = 0f) {
            _sniperZoomActive = active;
            if(active) {
                _sniperZoomFovOverride = zoomFov > 0f ? zoomFov : baseFov;
            } else {
                _sniperZoomFovOverride = 0f;
                if(_fpCamera != null) {
                    _fpCamera.Lens.FieldOfView = baseFov;
                }
            }
        }

        public bool IsSniperZoomActive => _sniperZoomActive;
    }
}