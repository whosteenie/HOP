using Game.Player.Core;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Game.Weapon.Presentation {
    /// <summary>
    /// Manages a separate camera that renders only the weapon layer, ensuring weapons always render above world/enemy geometry.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public class WeaponCameraController : MonoBehaviour {
        [SerializeField] private PlayerController playerController;

        [Header("Camera Setup")]
        private Camera _weaponCamera;
        private Camera _mainSceneCamera;
        private CinemachineCamera _fpCamera;
        [SerializeField] private bool syncWeaponFovWithFpCamera;
        [SerializeField, Range(1f, 179f)] private float fixedWeaponCameraFov = 70f;

        [Header("Dynamic Near Clip (FOV-driven)")]
        [SerializeField] private bool enableDynamicNearClip = true;
        [SerializeField] private float nearClipBaseFov = 80f;
        [SerializeField] private float nearClipMaxFov = 100f;
        [SerializeField] private float nearClipAtBaseFov = 0.03f;
        [SerializeField] private float nearClipAtMaxFov = 0.06f;
        [SerializeField] private AnimationCurve nearClipByFovCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        private void Awake() {
            CacheMainSceneCamera();
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[WeaponCameraController] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_fpCamera == null) _fpCamera = playerController.FpCamera;
            if(_weaponCamera == null) _weaponCamera = playerController.WeaponCamera;

            SetupWeaponCamera();
        }

        private void SetupWeaponCamera() {
            if(_weaponCamera == null) {
                Debug.LogError("[WeaponCameraController] WeaponCamera reference missing on PlayerController.");
                return;
            }

            var weaponCameraData = _weaponCamera.GetUniversalAdditionalCameraData();
            if(weaponCameraData != null) {
                weaponCameraData.renderType = CameraRenderType.Overlay;
            }

            var targetFov = ResolveTargetWeaponFov();
            _weaponCamera.fieldOfView = targetFov;
            nearClipBaseFov = targetFov;

            // Keep existing behavior unchanged at base FOV unless explicitly tuned otherwise.
            if(nearClipAtBaseFov <= 0f && _weaponCamera != null) {
                nearClipAtBaseFov = _weaponCamera.nearClipPlane;
            }

            if(nearClipAtMaxFov < nearClipAtBaseFov) {
                nearClipAtMaxFov = nearClipAtBaseFov;
            }

            // Parent to fpCamera transform (for position/rotation sync)
            // Only reparent if not already a child of fpCamera (allows prefab setup)
            if(_fpCamera != null) {
                if(_weaponCamera.transform.parent != _fpCamera.transform) {
                    _weaponCamera.transform.SetParent(_fpCamera.transform, false);
                }

                var weaponCameraTransform = _weaponCamera.transform;
                weaponCameraTransform.localPosition = Vector3.zero;
                weaponCameraTransform.localRotation = Quaternion.identity;
            }

            // Add weapon camera to main scene camera's camera stack
            var mainSceneCamera = ResolveMainSceneCamera();

            if(mainSceneCamera == null) {
                Debug.LogWarning("[WeaponCameraController] Main scene camera not found; weapon overlay stack setup skipped.");
                return;
            }

            var mainCameraData = mainSceneCamera.GetUniversalAdditionalCameraData();
            if(mainCameraData == null) {
                Debug.LogWarning("[WeaponCameraController] Main scene camera is missing UniversalAdditionalCameraData.");
                return;
            }

            // Remove from stack if already added (to avoid duplicates)
            if(mainCameraData.cameraStack.Contains(_weaponCamera)) {
                mainCameraData.cameraStack.Remove(_weaponCamera);
            }

            // Add to camera stack
            mainCameraData.cameraStack.Add(_weaponCamera);
        }

        private void LateUpdate() {
            if(_weaponCamera == null) return;
            var targetFov = ResolveTargetWeaponFov();
            if(Mathf.Abs(_weaponCamera.fieldOfView - targetFov) > 0.01f) {
                _weaponCamera.fieldOfView = targetFov;
            }

            UpdateDynamicNearClip(targetFov);
        }

        private float ResolveTargetWeaponFov() {
            if(syncWeaponFovWithFpCamera && _fpCamera != null) {
                return _fpCamera.Lens.FieldOfView;
            }

            return Mathf.Clamp(fixedWeaponCameraFov, 1f, 179f);
        }

        private void UpdateDynamicNearClip(float currentFov) {
            if(!enableDynamicNearClip || _weaponCamera == null) return;

            var fovMax = Mathf.Max(nearClipBaseFov + 0.001f, nearClipMaxFov);
            var t = Mathf.InverseLerp(nearClipBaseFov, fovMax, currentFov);
            var curved = nearClipByFovCurve != null ? nearClipByFovCurve.Evaluate(t) : t;
            var targetNear = Mathf.Lerp(nearClipAtBaseFov, nearClipAtMaxFov, curved);
            targetNear = Mathf.Max(0.001f, targetNear);

            if(Mathf.Abs(_weaponCamera.nearClipPlane - targetNear) > 0.0001f) {
                _weaponCamera.nearClipPlane = targetNear;
            }
        }

        /// <summary>
        /// Enable or disable the weapon camera (used when player dies/respawns)
        /// </summary>
        public void SetWeaponCameraEnabled(bool enable) {
            if(_weaponCamera != null) {
                _weaponCamera.enabled = enable;
            }
        }

        public bool TryGetMainSceneCamera(out Camera mainSceneCamera) {
            mainSceneCamera = ResolveMainSceneCamera();
            return mainSceneCamera != null;
        }

        private Camera ResolveMainSceneCamera() {
            if(_mainSceneCamera != null &&
               _mainSceneCamera != _weaponCamera &&
               _mainSceneCamera.isActiveAndEnabled) {
                return _mainSceneCamera;
            }

            CacheMainSceneCamera();
            if(_mainSceneCamera == _weaponCamera) {
                _mainSceneCamera = null;
            }

            return _mainSceneCamera;
        }

        private void CacheMainSceneCamera() {
            _mainSceneCamera = Camera.main;
            if(_mainSceneCamera == _weaponCamera) {
                _mainSceneCamera = null;
            }
        }

        /// <summary>
        /// Removes the weapon camera from the main camera's stack before destruction.
        /// This prevents Unity warnings about missing camera overlays.
        /// </summary>
        private void OnDestroy() {
            if(_weaponCamera == null) return;

            // Try to get main camera (may be null if scene is unloading)
            var mainCam = ResolveMainSceneCamera();
            if(mainCam == null) return;

            var mainCameraData = mainCam.GetUniversalAdditionalCameraData();

            // Remove this weapon camera from the stack
            if(mainCameraData != null && mainCameraData.cameraStack != null && mainCameraData.cameraStack.Contains(_weaponCamera)) {
                mainCameraData.cameraStack.Remove(_weaponCamera);
            }
        }
    }
}
