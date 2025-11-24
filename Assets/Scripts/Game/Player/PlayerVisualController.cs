using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Player {
    /// <summary>
    /// Handles all visual, material, and renderer management for the player.
    /// </summary>
    [RequireComponent(typeof(PlayerController))]
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerVisualController : NetworkBehaviour {
        [Header("References")] [SerializeField]
        private PlayerController playerController;

        [SerializeField] private PlayerShadow playerShadow;
        [SerializeField] private Game.Weapons.WeaponManager weaponManager;
        [SerializeField] private Material[] playerMaterials;

        [SerializeField]
        private GameObject worldModelRoot; // Container GameObject for the player's 3D model (mesh + rig)

        [SerializeField] private Transform worldWeaponSocket; // Socket containing all world weapon GameObjects
        [SerializeField] private GameObject[] worldWeaponPrefabs;

        private SkinnedMeshRenderer _cachedSkinnedMeshRenderer;
        private Renderer[] _cachedRenderers;
        private SkinnedMeshRenderer[] _cachedSkinnedRenderers;
        private bool _renderersCacheValid;
        private Material[] _cachedMaterialsArray;
        private int _lastMaterialIndex = -1;
        private const string GrappleLineName = "GrappleLine";
        
        // Cache Bounds object to prevent allocations
        private static readonly Bounds MaxBounds = new Bounds(Vector3.zero,
            new Vector3(float.MaxValue, float.MaxValue, float.MaxValue));

        private void Awake() {
            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerShadow == null) {
                playerShadow = GetComponent<PlayerShadow>();
            }

            if(weaponManager == null) {
                weaponManager = GetComponent<Game.Weapons.WeaponManager>();
            }

            // Cache renderer references early (GetComponentInChildren is acceptable for child components)
            _cachedSkinnedMeshRenderer = GetComponentInChildren<SkinnedMeshRenderer>();

            if(_cachedSkinnedMeshRenderer != null) {
                _cachedMaterialsArray = _cachedSkinnedMeshRenderer.materials;
            }

            _renderersCacheValid = false;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            // GetComponentInChildren is acceptable for child components (hierarchy-dependent)
            if(_cachedSkinnedMeshRenderer == null) {
                _cachedSkinnedMeshRenderer = GetComponentInChildren<SkinnedMeshRenderer>();
                if(_cachedSkinnedMeshRenderer != null && _cachedMaterialsArray == null) {
                    _cachedMaterialsArray = _cachedSkinnedMeshRenderer.materials;
                }
            }
        }

        /// <summary>
        /// Applies the selected player material color.
        /// </summary>
        public void ApplyPlayerMaterial(int index) {
            if(!_cachedSkinnedMeshRenderer) {
                _cachedSkinnedMeshRenderer = GetComponentInChildren<SkinnedMeshRenderer>();
                if(!_cachedSkinnedMeshRenderer) return;
            }

            if(index == _lastMaterialIndex && _cachedMaterialsArray != null) {
                return;
            }

            if(_cachedMaterialsArray == null || _cachedMaterialsArray.Length < 2) {
                _cachedMaterialsArray = _cachedSkinnedMeshRenderer.materials;
            }

            if(playerMaterials == null || playerMaterials.Length == 0) return;

            var selectedMaterial = playerMaterials[index % playerMaterials.Length];

            // Material array: [0] = outline, [1] = color, [2] = lit
            // Only modify [1] (color) for player color selection
            // Do NOT modify [0] (outline) - outline colors handled by shader/material and PlayerTeamManager

            if(_cachedMaterialsArray[1] != selectedMaterial) {
                _cachedMaterialsArray[1] = selectedMaterial;
                _cachedSkinnedMeshRenderer.materials = _cachedMaterialsArray;
                _lastMaterialIndex = index;
            }
        }

        /// <summary>
        /// Sets the visibility of the world model (for other players to see).
        /// </summary>
        [Rpc(SendTo.Everyone)]
        public void SetWorldModelVisibleRpc(bool visible) {
            if(weaponManager != null) {
                weaponManager.SwitchWeapon(0);
            }

            InvalidateRendererCache();

            if(visible) {
                // Ensure world model root and weapon are active
                if(worldModelRoot != null && !worldModelRoot.activeSelf) {
                    worldModelRoot.SetActive(true);
                }

                // Activate the currently equipped world weapon
                GameObject currentWorldWeapon = GetCurrentWorldWeapon();
                if(currentWorldWeapon != null && !currentWorldWeapon.activeSelf) {
                    currentWorldWeapon.SetActive(true);
                }

                // Enable all renderers and set proper shadow modes
                SetRenderersEnabled(true);
                if(playerShadow != null) {
                    playerShadow.SetSkinnedMeshRenderersShadowMode(ShadowCastingMode.On);
                    playerShadow.SetWorldWeaponRenderersShadowMode(ShadowCastingMode.On);
                }

                // Force bounds update immediately
                ForceRendererBoundsUpdate();

                // Schedule delayed bounds update to ensure Unity has positioned everything
                StartCoroutine(DelayedBoundsUpdate());
            } else {
                if(worldModelRoot != null) {
                    worldModelRoot.SetActive(false);
                }

                // Deactivate the currently equipped world weapon
                GameObject currentWorldWeapon = GetCurrentWorldWeapon();
                if(currentWorldWeapon != null) {
                    currentWorldWeapon.SetActive(false);
                }
            }
        }

        /// <summary>
        /// Sets whether renderers are enabled or disabled.
        /// </summary>
        public void SetRenderersEnabled(bool isEnabled, bool excludeGrappleLine = true,
            ShadowCastingMode? shadowMode = null) {
            RefreshRendererCacheIfNeeded();
            foreach(var r in _cachedRenderers) {
                if(r != null && (!excludeGrappleLine || r.name != GrappleLineName)) {
                    r.enabled = isEnabled;
                    if(shadowMode.HasValue) {
                        r.shadowCastingMode = shadowMode.Value;
                    }

                    // For SkinnedMeshRenderers, ensure bounds are updated
                    if(r is SkinnedMeshRenderer smr) {
                        smr.updateWhenOffscreen = true;
                        
                        // Set bounds to maximum values to prevent culling issues (use cached static bounds)
                        // Note: localBounds is what Unity uses for frustum culling on SkinnedMeshRenderers
                        smr.localBounds = MaxBounds;
                        
                        // Force Unity to recognize the bounds change by accessing the world-space bounds property
                        var _ = smr.bounds;
                    }
                }
            }
        }

        /// <summary>
        /// Sets shadow casting mode for SkinnedMeshRenderers.
        /// Delegates to PlayerShadow to avoid code duplication.
        /// </summary>
        public void SetSkinnedMeshRenderersShadowMode(ShadowCastingMode mode, ShadowCastingMode? ownerMode = null,
            bool? isEnabled = null) {
            if(playerShadow != null) {
                playerShadow.SetSkinnedMeshRenderersShadowMode(mode, ownerMode, isEnabled);
            }
        }

        /// <summary>
        /// Sets shadow casting mode for world weapon renderers.
        /// Delegates to PlayerShadow to avoid code duplication.
        /// </summary>
        public void SetWorldWeaponRenderersShadowMode(ShadowCastingMode mode, bool isEnabled = true) {
            if(playerShadow != null) {
                playerShadow.SetWorldWeaponRenderersShadowMode(mode, isEnabled);
            }
        }

        /// <summary>
        /// Gets the currently equipped world weapon GameObject from the weapon socket.
        /// </summary>
        private GameObject GetCurrentWorldWeapon() {
            // Try to get from WeaponManager first (most reliable)
            if(weaponManager != null) {
                var weaponData = weaponManager.GetWeaponDataByIndex(weaponManager.CurrentWeaponIndex);
                if(weaponData != null && !string.IsNullOrEmpty(weaponData.worldWeaponName) &&
                   worldWeaponSocket != null) {
                    var worldObj = worldWeaponSocket.Find(weaponData.worldWeaponName);
                    if(worldObj != null && worldObj.gameObject.activeSelf) {
                        return worldObj.gameObject;
                    }
                }
            }

            // Fallback: find the first active child in the weapon socket
            if(worldWeaponSocket != null) {
                foreach(Transform child in worldWeaponSocket) {
                    if(child.gameObject.activeSelf) {
                        return child.gameObject;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Sets shadow casting mode for world weapon prefabs.
        /// </summary>
        public void SetWorldWeaponPrefabsShadowMode(ShadowCastingMode mode) {
            if(worldWeaponPrefabs != null) {
                foreach(var w in worldWeaponPrefabs) {
                    var weaponRenderers = w.GetComponentsInChildren<MeshRenderer>();
                    foreach(var mr in weaponRenderers) {
                        if(mr != null) {
                            mr.shadowCastingMode = mode;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Forces all SkinnedMeshRenderers to update their bounds immediately.
        /// This helps prevent frustum culling issues where renderers become invisible
        /// even though they're enabled and should be visible.
        /// Sets bounds to maximum values to prevent culling when players are barely on screen.
        /// </summary>
        public void ForceRendererBoundsUpdate() {
            RefreshRendererCacheIfNeeded();
            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr != null) {
                    // Ensure renderer is enabled and updateWhenOffscreen is true
                    if(!smr.enabled) {
                        smr.enabled = true;
                    }

                    smr.updateWhenOffscreen = true;

                    // Set bounds to maximum values to prevent culling issues (use cached static bounds)
                    // Using localBounds (local space) centered at origin with max size
                    // Note: localBounds is what Unity uses for frustum culling on SkinnedMeshRenderers
                    smr.localBounds = MaxBounds;
                    
                    // Force Unity to recognize the bounds change by accessing the world-space bounds property
                    // This triggers a recalculation using our maxed localBounds
                    var _ = smr.bounds;
                }
            }
        }

        /// <summary>
        /// Verifies that renderers are visible and fixes any issues found.
        /// This is a safety check to catch cases where renderers become invisible.
        /// </summary>
        public void VerifyAndFixVisibility() {
            // Only check if world model should be visible
            if(worldModelRoot == null || !worldModelRoot.activeSelf) return;
            if(playerController == null || playerController.netIsDead.Value) return;

            RefreshRendererCacheIfNeeded();
            bool needsFix = false;

            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr != null && smr.gameObject.activeInHierarchy) {
                    // Check if renderer is enabled but might have bounds issues
                    if(smr.enabled && !smr.updateWhenOffscreen) {
                        smr.updateWhenOffscreen = true;
                        needsFix = true;
                    }
                }
            }

            // If we found issues, force a bounds update
            if(needsFix) {
                ForceRendererBoundsUpdate();
            }
        }

        /// <summary>
        /// Delayed bounds update to ensure Unity has positioned the object before recalculating bounds.
        /// This helps fix visibility issues where renderers are culled incorrectly.
        /// </summary>
        private IEnumerator DelayedBoundsUpdate() {
            // Wait a frame to let Unity position everything
            yield return null;

            // Force bounds update again after positioning
            ForceRendererBoundsUpdate();

            // Wait another frame and update once more to be thorough
            yield return null;
            ForceRendererBoundsUpdate();
        }

        /// <summary>
        /// Refreshes the renderer cache if it's invalid.
        /// </summary>
        private void RefreshRendererCacheIfNeeded() {
            if(!_renderersCacheValid) {
                _cachedRenderers = GetComponentsInChildren<Renderer>(true);
                _cachedSkinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
                _renderersCacheValid = true;
            }
        }

        /// <summary>
        /// Invalidates the renderer cache, forcing it to be refreshed on next access.
        /// </summary>
        public void InvalidateRendererCache() {
            _renderersCacheValid = false;
        }

        // Public getters for PlayerController
        public GameObject GetWorldModelRoot() => worldModelRoot;
        public GameObject GetWorldWeapon() => GetCurrentWorldWeapon();
    }
}