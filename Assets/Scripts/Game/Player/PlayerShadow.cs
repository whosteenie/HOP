using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Player {
    /// <summary>
    /// Handles shadow casting mode management for player renderers.
    /// Enhanced to centralize all shadow mode logic from PlayerController.
    /// </summary>
    [RequireComponent(typeof(PlayerController))]
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerShadow : NetworkBehaviour {
        [Header("References")] [SerializeField]
        private PlayerController playerController;

        [SerializeField] private Weapons.WeaponManager weaponManager;
        [SerializeField] private Transform worldWeaponSocket; // Socket containing all world weapon GameObjects
        [SerializeField] private GameObject[] worldWeaponPrefabs;

        private SkinnedMeshRenderer[] _cachedSkinnedRenderers;
        private Renderer[] _cachedRenderers;
        private bool _renderersCacheValid;
        
        // Cache world weapon renderers to prevent GetComponentsInChildren allocations
        private GameObject _cachedWorldWeapon;
        private MeshRenderer[] _cachedWorldWeaponRenderers;
        private int _cachedWeaponIndex = -1;
        
        // Cache Bounds object to prevent allocations
        private static readonly Bounds MaxBounds = new Bounds(Vector3.zero,
            new Vector3(float.MaxValue, float.MaxValue, float.MaxValue));

        private void Awake() {
            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(weaponManager == null) {
                weaponManager = GetComponent<Game.Weapons.WeaponManager>();
            }

            _renderersCacheValid = false;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Component reference should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            // Network-dependent initialization
            // Original behavior: set owner's shadows to ShadowsOnly
            if(IsOwner) {
                SetSkinnedMeshRenderersShadowMode(ShadowCastingMode.ShadowsOnly);
                SetMeshRenderersShadowMode(ShadowCastingMode.ShadowsOnly);
            }
        }

        /// <summary>
        /// Sets shadow casting mode for all SkinnedMeshRenderers.
        /// </summary>
        public void SetSkinnedMeshRenderersShadowMode(ShadowCastingMode mode, ShadowCastingMode? ownerMode = null,
            bool? isEnabled = null) {
            RefreshRendererCacheIfNeeded();
            bool isOwner = playerController != null && playerController.IsOwner;

            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr != null) {
                    if(isEnabled.HasValue) {
                        smr.enabled = isEnabled.Value;
                    }

                    if(ownerMode.HasValue) {
                        smr.shadowCastingMode = isOwner ? ownerMode.Value : mode;
                    } else {
                        smr.shadowCastingMode = mode;
                    }

                    // Force bounds update to prevent culling issues
                    smr.updateWhenOffscreen = true;

                    // Set bounds to maximum values to prevent culling issues (use cached static bounds)
                    // Note: localBounds is what Unity uses for frustum culling on SkinnedMeshRenderers
                    smr.localBounds = MaxBounds;
                    
                    // Force Unity to recognize the bounds change by accessing the world-space bounds property
                    var _ = smr.bounds;
                }
            }
        }

        /// <summary>
        /// Sets shadow casting mode for all MeshRenderers.
        /// </summary>
        public void SetMeshRenderersShadowMode(ShadowCastingMode mode) {
            RefreshRendererCacheIfNeeded();

            foreach(var mr in _cachedRenderers) {
                if(mr != null && mr is MeshRenderer) {
                    mr.shadowCastingMode = mode;
                }
            }
        }

        /// <summary>
        /// Sets shadow casting mode for world weapon renderers.
        /// Gets the currently equipped weapon from the weapon socket.
        /// Caches renderers to prevent GetComponentsInChildren allocations.
        /// </summary>
        public void SetWorldWeaponRenderersShadowMode(ShadowCastingMode mode, bool isEnabled = true) {
            // Get the currently equipped world weapon from the socket
            GameObject currentWorldWeapon = GetCurrentWorldWeapon();

            if(currentWorldWeapon != null) {
                // Check if weapon changed - if so, refresh cache
                int currentWeaponIndex = weaponManager != null ? weaponManager.CurrentWeaponIndex : -1;
                if(currentWorldWeapon != _cachedWorldWeapon || currentWeaponIndex != _cachedWeaponIndex) {
                    _cachedWorldWeapon = currentWorldWeapon;
                    _cachedWeaponIndex = currentWeaponIndex;
                    _cachedWorldWeaponRenderers = currentWorldWeapon.GetComponentsInChildren<MeshRenderer>();
                }
                
                // Use cached renderers
                if(_cachedWorldWeaponRenderers != null) {
                    foreach(var mr in _cachedWorldWeaponRenderers) {
                        if(mr != null) {
                            mr.shadowCastingMode = mode;
                            mr.enabled = isEnabled;
                        }
                    }
                }
            } else {
                // Weapon not found, clear cache
                _cachedWorldWeapon = null;
                _cachedWorldWeaponRenderers = null;
                _cachedWeaponIndex = -1;
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
    }
}