using Game.Player;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Weapons {
    /// <summary>
    /// Dynamically creates shadow-only duplicates of world geometry that cast shadows on the weapon.
    /// Only checks when the player is moving. Uses sphere cast with player radius for accurate shadow detection.
    /// </summary>
    public class WeaponShadowManager : MonoBehaviour {
        [SerializeField] private PlayerController playerController;
        
        [Header("Settings")]
        [SerializeField] private float shadowRaycastDistance = 1000f; // Max distance to check for shadows
        [SerializeField] private int weaponShadowLayer = -1; // Will be set to Weapon layer
        
        private Camera _weaponCamera;
        private CharacterController _characterController;
        private Light _mainLight;
        private LayerMask _worldLayer;
        private float _playerRadius; // Cached CharacterController radius for sphere cast
        
        // Current shadow state
        private GameObject _currentShadowGeometry;
        private GameObject _currentShadowSource; // The original object we duplicated
        
        private void Awake() {
            ValidateComponents();
        }
        
        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }
            
            if(playerController == null) {
                Debug.LogError("[WeaponShadowManager] PlayerController not found!");
                enabled = false;
                return;
            }
            
            _weaponCamera = playerController.WeaponCamera;
            _characterController = playerController.CharacterController;
            _worldLayer = playerController.WorldLayer;
            
            // Cache player radius for sphere cast
            if(_characterController != null) {
                _playerRadius = _characterController.radius;
            } else {
                _playerRadius = 0.5f; // Fallback default
                Debug.LogWarning("[WeaponShadowManager] CharacterController not found, using default radius of 0.5f");
            }
            
            // Get weapon layer
            weaponShadowLayer = LayerMask.NameToLayer("Weapon");
            if(weaponShadowLayer == -1) {
                Debug.LogWarning("[WeaponShadowManager] Weapon layer not found! Creating shadow geometry may not work correctly.");
            }
            
            // Find main directional light
            _mainLight = RenderSettings.sun;
            if(_mainLight == null) {
                // Fallback: find first directional light in scene
                var lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
                foreach(var sceneLight in lights) {
                    if(sceneLight.type != LightType.Directional) continue;
                    _mainLight = sceneLight;
                    break;
                }
            }
            
            if(_mainLight == null) {
                Debug.LogWarning("[WeaponShadowManager] No directional light found! Shadow detection will not work.");
            }
        }
        
        private void Update() {
            // Check every frame when moving (no throttle - raycast is cheap)
            CheckAndUpdateShadowGeometry();
        }
        
        private void CheckAndUpdateShadowGeometry() {
            if(_weaponCamera == null || _mainLight == null) return;
            
            var isInShadow = IsWeaponInShadow(out var shadowCaster);
            
            if(isInShadow && shadowCaster != null) {
                // Get root object of shadow caster
                var rootObject = GetRootObject(shadowCaster);
                
                // Check if we need to update shadow geometry
                if(_currentShadowGeometry != null && _currentShadowSource == rootObject) return;
                // Different shadow caster or no shadow geometry - update
                CleanupCurrentShadow();
                CreateShadowGeometry(rootObject);
                // If same shadow caster, keep existing geometry
            } else {
                // Not in shadow - cleanup (only destroy when actually leaving shadow)
                CleanupCurrentShadow();
            }
        }
        
        private bool IsWeaponInShadow(out GameObject shadowCaster) {
            shadowCaster = null;
            
            if(_weaponCamera == null || _mainLight == null) return false;
            
            // Get weapon position (camera position since weapon camera is parented to FP camera)
            var weaponPos = _weaponCamera.transform.position;
            
            // Get light direction (opposite of light forward)
            var lightDir = -_mainLight.transform.forward;
            
            // SphereCast from weapon position toward light direction using player radius
            // This accounts for the player's volume and detects shadows as soon as they should affect the weapon
            if(!Physics.SphereCast(weaponPos, _playerRadius, lightDir, out var hit, shadowRaycastDistance,
                   _worldLayer)) return false;
            shadowCaster = hit.collider.gameObject;
            return true;

        }
        
        private void CreateShadowGeometry(GameObject source) {
            if(source == null) return;
            
            // Duplicate the source object
            _currentShadowGeometry = Instantiate(source);
            _currentShadowGeometry.name = $"{source.name}_ShadowOnly";
            
            // Configure all renderers to be shadow-only and on Weapon layer
            var renderers = _currentShadowGeometry.GetComponentsInChildren<Renderer>(true);
            foreach(var r in renderers) {
                r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                
                // Set layer to Weapon so weapon camera can see it
                if(weaponShadowLayer != -1) {
                    SetLayerRecursive(r.gameObject, weaponShadowLayer);
                }
            }
            
            // Position at source (match transform)
            _currentShadowGeometry.transform.position = source.transform.position;
            _currentShadowGeometry.transform.rotation = source.transform.rotation;
            _currentShadowGeometry.transform.localScale = source.transform.lossyScale;
            
            // Store reference to source for comparison
            _currentShadowSource = source;
        }
        
        private void CleanupCurrentShadow() {
            if(_currentShadowGeometry == null) return;
            Destroy(_currentShadowGeometry);
            _currentShadowGeometry = null;
            _currentShadowSource = null;
        }
        
        private GameObject GetRootObject(GameObject obj) {
            if(obj == null) return null;
            
            // Traverse up hierarchy to find root
            while(obj.transform.parent != null) {
                obj = obj.transform.parent.gameObject;
            }
            return obj;
        }
        
        private void SetLayerRecursive(GameObject obj, int layer) {
            if(obj == null) return;
            
            obj.layer = layer;
            foreach(Transform child in obj.transform) {
                SetLayerRecursive(child.gameObject, layer);
            }
        }
        
        private void OnDestroy() {
            CleanupCurrentShadow();
        }
    }
}

