using Game.Player;
using UnityEngine;
using Unity.Cinemachine;
using UnityEngine.Rendering.Universal;

namespace Game.Weapons {
    /// <summary>
    /// Manages a separate camera that renders only the weapon layer, ensuring weapons always render above world/enemy geometry.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public class WeaponCameraController : MonoBehaviour {
        [SerializeField] private PlayerController playerController;

        [Header("Camera Setup")]
        private Camera _weaponCamera;
        private CinemachineCamera _fpCamera;
        private Camera _mainSceneCamera; // Main scene camera (not player's fpCamera)

        private void Awake() {
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

            // Get main scene camera (the one in the scene, not the player's fpCamera)
            _mainSceneCamera = Camera.main;

            SetupWeaponCamera();
        }

        private void SetupWeaponCamera() {

            // Sync FOV with fpCamera
            if(_fpCamera != null) {
                _weaponCamera.fieldOfView = _fpCamera.Lens.FieldOfView;
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
            var mainCameraData = _mainSceneCamera.GetUniversalAdditionalCameraData();
            if(mainCameraData == null) return;
            // Remove from stack if already added (to avoid duplicates)
            if(mainCameraData.cameraStack.Contains(_weaponCamera)) {
                mainCameraData.cameraStack.Remove(_weaponCamera);
            }

            // Add to camera stack
            mainCameraData.cameraStack.Add(_weaponCamera);
        }

        private void LateUpdate() {
            // Sync FOV with fpCamera
            if(_weaponCamera == null || _fpCamera == null) return;
            var newFov = _fpCamera.Lens.FieldOfView;
            if(Mathf.Abs(_weaponCamera.fieldOfView - newFov) > 0.01f) {
                _weaponCamera.fieldOfView = newFov;
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

        /// <summary>
        /// Removes the weapon camera from the main camera's stack before destruction.
        /// This prevents Unity warnings about missing camera overlays.
        /// </summary>
        private void OnDestroy() {
            if(_weaponCamera == null) return;

            // Try to get main camera (may be null if scene is unloading)
            var mainCam = _mainSceneCamera;
            if(mainCam == null) {
                mainCam = Camera.main;
            }

            if(mainCam == null) {
                var mainCameraObj = GameObject.FindGameObjectWithTag("MainCamera");
                if(mainCameraObj != null) {
                    mainCam = mainCameraObj.GetComponent<Camera>();
                }
            }

            if(mainCam == null) return;

            var mainCameraData = mainCam.GetUniversalAdditionalCameraData();

            // Remove this weapon camera from the stack
            if(mainCameraData != null && mainCameraData.cameraStack != null && mainCameraData.cameraStack.Contains(_weaponCamera)) {
                mainCameraData.cameraStack.Remove(_weaponCamera);
            }
        }
    }
}