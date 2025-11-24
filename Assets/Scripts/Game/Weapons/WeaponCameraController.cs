using UnityEngine;
using Unity.Cinemachine;
using UnityEngine.Rendering.Universal;

namespace Game.Weapons {
    /// <summary>
    /// Manages a separate camera that renders only the weapon layer, ensuring weapons always render above world/enemy geometry.
    /// </summary>
    public class WeaponCameraController : MonoBehaviour {
        [Header("Camera Setup")]
        [SerializeField] private Camera weaponCamera;

        [SerializeField] private CinemachineCamera fpCamera;
        [SerializeField] private int weaponLayerIndex = 6; // Weapon layer from TagManager

        public CinemachineCamera FpCamera {
            get => fpCamera;
            set {
                fpCamera = value;
                if(Application.isPlaying) {
                    SetupWeaponCamera();
                }
            }
        }

        private Camera _mainSceneCamera; // Main scene camera (not player's fpCamera)
        private Camera _fpCameraComponent; // Player's fpCamera component for reference
        private int _weaponLayerMask;

        private void Awake() {
            // Weapon camera should be assigned in the prefab/editor
            if(weaponCamera == null) {
                Debug.LogError("[WeaponCameraController] Weapon camera not assigned! Please assign it in the prefab.");
                return;
            }

            // Get main scene camera (the one in the scene, not the player's fpCamera)
            _mainSceneCamera = Camera.main;
            if(_mainSceneCamera == null) {
                var mainCameraObj = GameObject.FindGameObjectWithTag("MainCamera");
                if(mainCameraObj != null) {
                    _mainSceneCamera = mainCameraObj.GetComponent<Camera>();
                }
            }

            // Get fpCamera component for reference (for FOV sync, etc.)
            if(fpCamera != null) {
                _fpCameraComponent = fpCamera.GetComponent<Camera>();
                if(_fpCameraComponent == null && fpCamera.gameObject != null) {
                    _fpCameraComponent = fpCamera.gameObject.GetComponentInChildren<Camera>();
                }
            }

            SetupWeaponCamera();
        }

        private void SetupWeaponCamera() {
            if(weaponCamera == null) {
                Debug.LogError("[WeaponCameraController] Weapon camera is null!");
                return;
            }

            if(_mainSceneCamera == null) {
                Debug.LogError(
                    "[WeaponCameraController] Main scene camera not found! Make sure there's a camera tagged 'MainCamera' in the scene.");
                return;
            }

            // Calculate weapon layer mask
            _weaponLayerMask = 1 << weaponLayerIndex;

            // Configure weapon camera - simple setup
            weaponCamera.CopyFrom(_mainSceneCamera);
            weaponCamera.cullingMask = _weaponLayerMask; // Only render Weapon layer
            weaponCamera.clearFlags = CameraClearFlags.Depth; // Don't clear color, only depth
            weaponCamera.nearClipPlane = 0.01f;
            weaponCamera.farClipPlane = 100f;

            // Set weapon camera to Overlay type and configure URP settings
            var weaponCameraData = weaponCamera.GetUniversalAdditionalCameraData();
            if(weaponCameraData != null) {
                weaponCameraData.renderType = CameraRenderType.Overlay; // Set to Overlay
                weaponCameraData.renderPostProcessing = false; // Disable post-processing/volume effects
                weaponCameraData.volumeLayerMask = 0; // Exclude all volume layers
            }

            // Sync FOV with fpCamera
            if(fpCamera != null) {
                weaponCamera.fieldOfView = fpCamera.Lens.FieldOfView;
            } else if(_fpCameraComponent != null) {
                weaponCamera.fieldOfView = _fpCameraComponent.fieldOfView;
            }

            // Disable audio listener
            var audioListener = weaponCamera.GetComponent<AudioListener>();
            if(audioListener != null) {
                Destroy(audioListener);
            }

            // Parent to fpCamera transform (for position/rotation sync)
            // Only reparent if not already a child of fpCamera (allows prefab setup)
            if(fpCamera != null) {
                if(weaponCamera.transform.parent != fpCamera.transform) {
                    weaponCamera.transform.SetParent(fpCamera.transform, false);
                }
                weaponCamera.transform.localPosition = Vector3.zero;
                weaponCamera.transform.localRotation = Quaternion.identity;
            }

            // Add weapon camera to main scene camera's camera stack
            var mainCameraData = _mainSceneCamera.GetUniversalAdditionalCameraData();
            if(mainCameraData != null) {
                // Remove from stack if already added (to avoid duplicates)
                if(mainCameraData.cameraStack.Contains(weaponCamera)) {
                    mainCameraData.cameraStack.Remove(weaponCamera);
                }

                // Add to camera stack
                mainCameraData.cameraStack.Add(weaponCamera);
                Debug.Log(
                    $"[WeaponCameraController] Weapon camera added to main scene camera stack. Stack count: {mainCameraData.cameraStack.Count}");
            } else {
                Debug.LogError("[WeaponCameraController] Main scene camera doesn't have URP camera data!");
            }

            Debug.Log(
                $"[WeaponCameraController] Weapon camera configured as Overlay. Weapon layer: {_weaponLayerMask}");
        }

        private void LateUpdate() {
            // Sync FOV with fpCamera
            if(weaponCamera && fpCamera) {
                weaponCamera.fieldOfView = fpCamera.Lens.FieldOfView;
            }
        }

        /// <summary>
        /// Enable or disable the weapon camera (used when player dies/respawns)
        /// </summary>
        public void SetWeaponCameraEnabled(bool enable) {
            if(weaponCamera != null) {
                weaponCamera.enabled = enable;
            }
        }

        /// <summary>
        /// Removes the weapon camera from the main camera's stack before destruction.
        /// This prevents Unity warnings about missing camera overlays.
        /// </summary>
        private void OnDestroy() {
            if(weaponCamera == null) return;

            // Try to get main camera (may be null if scene is unloading)
            Camera mainCam = _mainSceneCamera;
            if(mainCam == null) {
                mainCam = Camera.main;
            }
            if(mainCam == null) {
                var mainCameraObj = GameObject.FindGameObjectWithTag("MainCamera");
                if(mainCameraObj != null) {
                    mainCam = mainCameraObj.GetComponent<Camera>();
                }
            }

            if(mainCam != null) {
                var mainCameraData = mainCam.GetUniversalAdditionalCameraData();
                if(mainCameraData != null && mainCameraData.cameraStack != null) {
                    // Remove this weapon camera from the stack
                    if(mainCameraData.cameraStack.Contains(weaponCamera)) {
                        mainCameraData.cameraStack.Remove(weaponCamera);
                        Debug.Log(
                            $"[WeaponCameraController] Weapon camera removed from main camera stack. Remaining stack count: {mainCameraData.cameraStack.Count}");
                    }
                }
            }
        }
    }
}