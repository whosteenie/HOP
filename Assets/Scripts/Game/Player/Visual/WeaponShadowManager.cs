using System;
using System.Collections.Generic;
using Diagnostics;
using Game.Player.Contracts;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Game.Player.Visual {
    /// <summary>
    /// Dynamically creates shadow-only duplicates of world geometry that cast shadows on the weapon.
    /// Only checks when the player is moving. Uses sphere cast with player radius for accurate shadow detection.
    /// </summary>
    public class WeaponShadowManager : MonoBehaviour {
        [HideInInspector, SerializeField] private MonoBehaviour playerContextSource;
        
        [Header("Settings")]
        [SerializeField] private float shadowRaycastDistance = 1000f; // Max distance to check for shadows
        [SerializeField] private int weaponShadowLayer = -1; // Will be set to Weapon layer
        [SerializeField] private string blockedRootName = "WorldRoot";
        [SerializeField] private int maxRenderersPerSource = 64;
        [SerializeField] private float maxSourceBoundsDimension = 150f;
        [SerializeField] private int maxCachedSources = 24;
        [SerializeField] private bool logRejectedSources;
        
        private Camera _weaponCamera;
        private CharacterController _characterController;
        private Light _mainLight;
        private LayerMask _worldLayer;
        private IPlayerVisualContext _playerContext;
        private float _playerRadius; // Cached CharacterController radius for sphere cast
        
        // Current shadow state
        private GameObject _currentShadowGeometry;
        private GameObject _currentShadowSource; // The original object we duplicated

        // Cache shadow clones by source so we do not instantiate/destroy repeatedly.
        private readonly Dictionary<GameObject, GameObject> _shadowGeometryCache = new();
        private readonly LinkedList<GameObject> _cacheInsertionOrder = new();
        private readonly Dictionary<GameObject, SourceAssessment> _sourceAssessmentCache = new();

        private struct SourceAssessment {
            internal bool IsUsable;
            internal int RendererCount;
            internal float BoundsDimension;
        }
        
        private void Awake() {
            ValidateComponents();
        }
        
        private void ValidateComponents() {
            if(!PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext) || _playerContext == null) {
                DevLog.LogError("[WeaponShadowManager] IPlayerVisualContext not found!");
                enabled = false;
                return;
            }
            
            _weaponCamera = _playerContext.WeaponCamera;
            _characterController = _playerContext.CharacterController;
            _worldLayer = _playerContext.WorldLayer;
            
            // Cache player radius for sphere cast
            if(_characterController != null) {
                _playerRadius = _characterController.radius;
            } else {
                _playerRadius = 0.5f; // Fallback default
                DevLog.LogWarning("[WeaponShadowManager] CharacterController not found, using default radius of 0.5f");
            }
            
            // Get weapon layer
            weaponShadowLayer = LayerMask.NameToLayer("Weapon");
            if(weaponShadowLayer == -1) {
                DevLog.LogWarning("[WeaponShadowManager] Weapon layer not found! Creating shadow geometry may not work correctly.");
            }
            
            // Find main directional light
            _mainLight = RenderSettings.sun;
            if(_mainLight == null) {
                _mainLight = TryResolveDirectionalLight();
            }
            
            if(_mainLight == null) {
                DevLog.LogWarning("[WeaponShadowManager] No directional light found! Shadow detection will not work.");
            }
        }

        private static Light TryResolveDirectionalLight() {
            var activeScene = SceneManager.GetActiveScene();
            if(!activeScene.IsValid()) return null;

            var roots = activeScene.GetRootGameObjects();
            foreach(var root in roots) {
                if(root == null) continue;
                var lights = root.GetComponentsInChildren<Light>(true);
                foreach(var sceneLight in lights) {
                    if(sceneLight != null && sceneLight.type == LightType.Directional) {
                        return sceneLight;
                    }
                }
            }

            return null;
        }
        
        private void Update() {
            // Check every frame for immediate visual response.
            CheckAndUpdateShadowGeometry();
        }
        
        private void CheckAndUpdateShadowGeometry() {
            if(_weaponCamera == null || _mainLight == null) return;
            
            var isInShadow = IsWeaponInShadow(out var shadowCaster);
            
            if(isInShadow && shadowCaster != null) {
                var source = GetShadowSource(shadowCaster);
                if(source == null) {
                    DeactivateCurrentShadow();
                    return;
                }

                if(_currentShadowSource != source) {
                    ActivateShadowForSource(source);
                } else {
                    SyncShadowTransform(source, _currentShadowGeometry);
                }
            } else {
                // Not in shadow - keep cache but disable current geometry.
                DeactivateCurrentShadow();
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
                   _worldLayer, QueryTriggerInteraction.Ignore)) return false;
            shadowCaster = hit.collider.gameObject;
            return true;

        }

        private GameObject GetShadowSource(GameObject hitObject) {
            if(hitObject == null) return null;

            var cursor = hitObject.transform;
            while(cursor != null) {
                var candidate = cursor.gameObject;
                if(IsBlockedRoot(candidate)) {
                    return null;
                }

                var assessment = AssessSource(candidate);
                if(assessment.IsUsable) {
                    return candidate;
                }

                cursor = cursor.parent;
            }

            return null;
        }

        private bool IsBlockedRoot(GameObject candidate) {
            if(candidate == null) return true;
            return !string.IsNullOrWhiteSpace(blockedRootName) && candidate.name.Equals(blockedRootName, StringComparison.OrdinalIgnoreCase);
        }

        private SourceAssessment AssessSource(GameObject source) {
            if(source == null) {
                return default;
            }

            if(_sourceAssessmentCache.TryGetValue(source, out var cachedAssessment)) {
                return cachedAssessment;
            }

            var assessment = new SourceAssessment {
                IsUsable = false,
                RendererCount = 0,
                BoundsDimension = 0f
            };

            var renderers = source.GetComponentsInChildren<Renderer>(true);
            assessment.RendererCount = renderers.Length;

            if(assessment.RendererCount == 0) {
                _sourceAssessmentCache[source] = assessment;
                return assessment;
            }

            if(maxRenderersPerSource > 0 && assessment.RendererCount > maxRenderersPerSource) {
                if(logRejectedSources) {
                    DevLog.LogWarning($"[WeaponShadowManager] Rejected shadow source '{source.name}' (renderer count {assessment.RendererCount} > {maxRenderersPerSource}).");
                }

                _sourceAssessmentCache[source] = assessment;
                return assessment;
            }

            var combinedBounds = renderers[0].bounds;
            for(var i = 1; i < renderers.Length; i++) {
                combinedBounds.Encapsulate(renderers[i].bounds);
            }

            assessment.BoundsDimension = Mathf.Max(combinedBounds.size.x, Mathf.Max(combinedBounds.size.y, combinedBounds.size.z));
            if(maxSourceBoundsDimension > 0f && assessment.BoundsDimension > maxSourceBoundsDimension) {
                if(logRejectedSources) {
                    DevLog.LogWarning($"[WeaponShadowManager] Rejected shadow source '{source.name}' (bounds {assessment.BoundsDimension:F1} > {maxSourceBoundsDimension:F1}).");
                }

                _sourceAssessmentCache[source] = assessment;
                return assessment;
            }

            assessment.IsUsable = true;
            _sourceAssessmentCache[source] = assessment;
            return assessment;
        }

        private void ActivateShadowForSource(GameObject source) {
            if(source == null) {
                DeactivateCurrentShadow();
                return;
            }

            var geometry = GetOrCreateShadowGeometry(source);
            if(geometry == null) {
                DeactivateCurrentShadow();
                return;
            }

            if(_currentShadowGeometry != null && _currentShadowGeometry != geometry) {
                _currentShadowGeometry.SetActive(false);
            }

            _currentShadowSource = source;
            _currentShadowGeometry = geometry;

            SyncShadowTransform(source, geometry);
            if(!geometry.activeSelf) {
                geometry.SetActive(true);
            }
        }

        private GameObject GetOrCreateShadowGeometry(GameObject source) {
            if(source == null) return null;

            if(_shadowGeometryCache.TryGetValue(source, out var cachedGeometry) && cachedGeometry != null) {
                return cachedGeometry;
            }

            var clone = Instantiate(source);
            clone.name = $"{source.name}_ShadowOnly";
            ConfigureShadowGeometry(clone);
            clone.SetActive(false);

            _shadowGeometryCache[source] = clone;
            _cacheInsertionOrder.AddLast(source);
            TrimCache();
            return clone;
        }

        private void ConfigureShadowGeometry(GameObject geometryRoot) {
            if(geometryRoot == null) return;

            var renderers = geometryRoot.GetComponentsInChildren<Renderer>(true);
            foreach(var geometryRenderer in renderers) {
                geometryRenderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            }

            if(weaponShadowLayer != -1) {
                SetLayerRecursive(geometryRoot, weaponShadowLayer);
            }

            var colliders = geometryRoot.GetComponentsInChildren<Collider>(true);
            foreach(var col in colliders) {
                col.enabled = false;
            }

            var behaviours = geometryRoot.GetComponentsInChildren<Behaviour>(true);
            foreach(var behaviour in behaviours) {
                // Disable behaviour components on the clone so duplicated scene logic does not run.
                behaviour.enabled = false;
            }
        }

        private void TrimCache() {
            if(maxCachedSources <= 0) return;

            while(_shadowGeometryCache.Count > maxCachedSources && _cacheInsertionOrder.First != null) {
                var oldestSource = _cacheInsertionOrder.First.Value;
                _cacheInsertionOrder.RemoveFirst();

                if(oldestSource == null) continue;
                if(oldestSource == _currentShadowSource) {
                    // Keep current source alive and place it at end of LRU order.
                    _cacheInsertionOrder.AddLast(oldestSource);
                    continue;
                }

                if(!_shadowGeometryCache.Remove(oldestSource, out var oldGeometry)) continue;

                if(oldGeometry != null) {
                    Destroy(oldGeometry);
                }
            }
        }

        private static void SyncShadowTransform(GameObject source, GameObject shadowGeometry) {
            if(source == null || shadowGeometry == null) return;

            var sourceTransform = source.transform;
            var geometryTransform = shadowGeometry.transform;

            geometryTransform.position = sourceTransform.position;
            geometryTransform.rotation = sourceTransform.rotation;
            geometryTransform.localScale = sourceTransform.lossyScale;
        }

        private void DeactivateCurrentShadow() {
            if(_currentShadowGeometry != null) {
                _currentShadowGeometry.SetActive(false);
            }

            _currentShadowGeometry = null;
            _currentShadowSource = null;
        }

        private static void SetLayerRecursive(GameObject obj, int layer) {
            if(obj == null) return;
            
            obj.layer = layer;
            foreach(Transform child in obj.transform) {
                SetLayerRecursive(child.gameObject, layer);
            }
        }
        
        private void OnDestroy() {
            DeactivateCurrentShadow();

            foreach(var pair in _shadowGeometryCache) {
                if(pair.Value != null) {
                    Destroy(pair.Value);
                }
            }

            _shadowGeometryCache.Clear();
            _cacheInsertionOrder.Clear();
            _sourceAssessmentCache.Clear();
        }
    }
}
