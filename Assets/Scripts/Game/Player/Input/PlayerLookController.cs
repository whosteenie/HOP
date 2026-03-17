using Game.Player.Contracts;
using Game.Settings;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Input {
    /// <summary>
    /// Handles camera/look logic for the player.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerLookController : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private MonoBehaviour playerContextSource;

        private IPlayerLookContext _playerContext;
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
        private Vector2 LookInput => _playerContext == null ? Vector2.zero : _playerContext.LookInput;

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(!PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext)) {
                Debug.LogError("[PlayerLookController] IPlayerLookContext not found!");
                enabled = false;
                return;
            }

            if(_playerTransform == null) _playerTransform = _playerContext.PlayerTransform;
            if(_upperBodyPitch == null) _upperBodyPitch = GetComponent<UpperBodyPitch>();
            if(_fpCamera == null) _fpCamera = _playerContext.FpCamera;
        }

        /// <summary>
        /// Updates the player's look orientation based on input.
        /// </summary>
        public void UpdateLook() {
            var sensitivity = GetLookSensitivity();
            var lookDelta = new Vector2(LookInput.x * sensitivity.x, LookInput.y * sensitivity.y);

            UpdatePitch(lookDelta.y);
            UpdateYaw(lookDelta.x);

            _playerContext?.UpdateTurnAnimationFromLook(lookDelta.x);

            if(_upperBodyPitch != null) {
                _upperBodyPitch.SetLocalPitchFromCamera(CurrentPitch);
            }
        }

        /// <summary>
        /// Gets the look sensitivity from settings.json.
        /// Handles invert Y setting.
        /// </summary>
        private Vector2 GetLookSensitivity() {
            var s = GameSettings.Data.controls;
            var sensitivityValue = s != null ? s.sensitivity : defaultLookSensitivity.x;

            var invertY = s is { invertY: true };
            var yMultiplier = invertY ? -1f : 1f;

            return new Vector2(sensitivityValue, sensitivityValue * yMultiplier);
        }

        /// <summary>
        /// Updates the field of view based on movement speed.
        /// </summary>
        public void UpdateSpeedFov() {
            if(!IsOwner || _fpCamera == null) return;

            if(_playerContext == null) return;

            var speed = _playerContext.HorizontalVelocity.magnitude;
            var t = Mathf.InverseLerp(sprintStartSpeed, maxSpeedForFov, speed);
            t = Mathf.Pow(t, 0.65f);

            _targetFov = Mathf.Lerp(baseFov, maxFov, t);

            var desiredFov = IsSniperZoomActive
                ? Mathf.Clamp(_sniperZoomFovOverride > 0f ? _sniperZoomFovOverride : baseFov, 5f, maxFov)
                : _targetFov;

            if(IsSniperZoomActive) {
                _fpCamera.Lens.FieldOfView = desiredFov;
                return;
            }

            var currentFov = _fpCamera.Lens.FieldOfView;
            currentFov = Mathf.SmoothDamp(currentFov, desiredFov, ref _fovVel, fovSmoothTime);
            _fpCamera.Lens.FieldOfView = currentFov;
        }

        [Header("Tilt")]
        [SerializeField] private float tiltSmoothTime = 0.1f;
        
        private float _targetTilt;
        private float _currentTilt;
        private float _tiltVel;

        /// <summary>
        /// Sets the target tilt for the camera.
        /// </summary>
        public void SetTargetTilt(float tilt) {
            _targetTilt = tilt;
        }

        /// <summary>
        /// Updates the camera pitch based on Delta input.
        /// </summary>
        private void UpdatePitch(float pitchDelta) {
            CurrentPitch -= pitchDelta;
            
            // Update Tilt
            _currentTilt = Mathf.SmoothDamp(_currentTilt, _targetTilt, ref _tiltVel, tiltSmoothTime);
            
            if(_fpCamera != null) {
                // Apply Pitch (X) and Tilt (Z)
                _fpCamera.transform.localRotation = Quaternion.Euler(CurrentPitch, 0f, _currentTilt);
            }
        }

        /// <summary>
        /// Updates the player yaw based on Delta input.
        /// </summary>
        private void UpdateYaw(float yawDelta) {
            _playerTransform.Rotate(Vector3.up * yawDelta);
        }

        /// <summary>
        /// Resets the camera pitch to zero.
        /// </summary>
        public void ResetPitch() {
            CurrentPitch = 0f;
            if(_fpCamera != null) {
                _fpCamera.transform.localRotation = Quaternion.identity;
            }
        }

        // Public getters
        public float CurrentPitch {
            get => _currentPitch;
            private set => _currentPitch = Mathf.Clamp(value, -PitchLimit, PitchLimit);
        }

        public float BaseFov => baseFov;

        private float _sniperZoomFovOverride;

        /// <summary>
        /// Sets whether sniper zoom is active and optionally overrides the FOV.
        /// </summary>
        public void SetSniperZoomActive(bool active, float zoomFov = 0f) {
            IsSniperZoomActive = active;
            if(active) {
                _sniperZoomFovOverride = zoomFov > 0f ? zoomFov : baseFov;
            } else {
                _sniperZoomFovOverride = 0f;
                if(_fpCamera != null) {
                    _fpCamera.Lens.FieldOfView = baseFov;
                }
            }
        }

        public bool IsSniperZoomActive { get; private set; }
    }
}
