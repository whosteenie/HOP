using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Handles camera/look logic for the player.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerLookController : NetworkBehaviour {
        [Header("References")] [SerializeField]
        private PlayerController playerController;

        [SerializeField] private PlayerAnimationController animationController;
        [SerializeField] private UpperBodyPitch upperBodyPitch;
        [SerializeField] private Transform tr;

        [Header("Look Parameters")] [SerializeField]
        private Vector2 _defaultLookSensitivity = new Vector2(0.1f, 0.1f); // Fallback if PlayerPrefs not available

        [Header("Components")] [SerializeField]
        private CinemachineCamera fpCamera;

        [Header("FOV (speed boost)")] [SerializeField]
        private float baseFov = 80f;

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
        private Vector2 lookInput => playerController != null ? playerController.lookInput : Vector2.zero;

        // Movement reference (for speed-based FOV)
        [SerializeField] private PlayerMovementController _movementController;

        private void Awake() {
            // Initialize transform reference
            if(tr == null) {
                tr = transform;
            }

            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(animationController == null) {
                animationController = GetComponent<PlayerAnimationController>();
            }

            if(upperBodyPitch == null) {
                upperBodyPitch = GetComponent<UpperBodyPitch>();
            }

            if(_movementController == null) {
                _movementController = GetComponent<PlayerMovementController>();
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(_movementController == null) {
                _movementController = GetComponent<PlayerMovementController>();
            }
        }

        public void UpdateLook() {
            var sensitivity = GetLookSensitivity();
            var lookDelta = new Vector2(lookInput.x * sensitivity.x, lookInput.y * sensitivity.y);

            UpdatePitch(lookDelta.y);
            UpdateYaw(lookDelta.x);

            if(animationController != null) {
                animationController.UpdateTurnAnimation(lookDelta.x);
            }

            if(upperBodyPitch != null) {
                upperBodyPitch.SetLocalPitchFromCamera(CurrentPitch);
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
                sensitivityValue = PlayerPrefs.GetFloat("Sensitivity", _defaultLookSensitivity.x);
            } else if(PlayerPrefs.HasKey("SensitivityX")) {
                sensitivityValue = PlayerPrefs.GetFloat("SensitivityX", _defaultLookSensitivity.x);
                // Migrate to new unified key
                PlayerPrefs.SetFloat("Sensitivity", sensitivityValue);
            } else {
                sensitivityValue = _defaultLookSensitivity.x;
            }

            // Apply invert Y multiplier
            bool invertY = PlayerPrefs.GetInt("InvertY", 0) == 1;
            float yMultiplier = invertY ? -1f : 1f;

            return new Vector2(sensitivityValue, sensitivityValue * yMultiplier);
        }

        public void UpdateSpeedFov() {
            if(!IsOwner || fpCamera == null) return;

            if(_movementController == null) {
                _movementController = GetComponent<PlayerMovementController>();
            }

            if(_movementController == null) return;

            var speed = _movementController.HorizontalVelocity.magnitude;
            var t = Mathf.InverseLerp(sprintStartSpeed, maxSpeedForFov, speed);
            t = Mathf.Pow(t, 0.65f);

            _targetFov = Mathf.Lerp(baseFov, maxFov, t);

            var currentFov = fpCamera.Lens.FieldOfView;
            currentFov = Mathf.SmoothDamp(currentFov, _targetFov, ref _fovVel, fovSmoothTime);
            fpCamera.Lens.FieldOfView = currentFov;
        }

        private void UpdatePitch(float pitchDelta) {
            CurrentPitch -= pitchDelta;
            if(fpCamera != null) {
                fpCamera.transform.localRotation = Quaternion.Euler(CurrentPitch, 0f, 0f);
            }
        }

        private void UpdateYaw(float yawDelta) {
            tr.Rotate(Vector3.up * yawDelta);
        }

        public void ResetPitch() {
            CurrentPitch = 0f;
            if(fpCamera != null) {
                fpCamera.transform.localRotation = Quaternion.identity;
            }
        }

        // Public getters
        public float CurrentPitch {
            get => _currentPitch;
            set => _currentPitch = Mathf.Clamp(value, -PitchLimit, PitchLimit);
        }

        public float BaseFov => baseFov;
    }
}