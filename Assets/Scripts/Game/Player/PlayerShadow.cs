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
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private Weapons.WeaponManager _weaponManager;
        private Transform _worldWeaponSocket; // Socket containing all world weapon GameObjects
        private GameObject[] _worldWeaponPrefabs;

        private SkinnedMeshRenderer[] _cachedSkinnedRenderers;
        private Renderer[] _cachedRenderers;
        private bool _renderersCacheValid;

        // Cache world weapon renderers to prevent GetComponentsInChildren allocations
        private GameObject _cachedWorldWeapon;
        private MeshRenderer[] _cachedWorldWeaponRenderers;
        private int _cachedWeaponIndex = -1;

        // Cache Bounds object to prevent allocations
        private static readonly Bounds MaxBounds = new(Vector3.zero, new Vector3(float.MaxValue, float.MaxValue, float.MaxValue));

        private void Awake() {
            playerController ??= GetComponent<PlayerController>();

            _weaponManager ??= playerController.WeaponManager;

            _worldWeaponSocket ??= playerController.WorldWeaponSocket;

            _worldWeaponPrefabs ??= playerController.WorldWeaponPrefabs;

            _renderersCacheValid = false;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            playerController ??= GetComponent<PlayerController>();

            // Network-dependent initialization
            // Original behavior: set owner's shadows to ShadowsOnly
            if(!IsOwner) return;
            SetSkinnedMeshRenderersShadowMode(ShadowCastingMode.ShadowsOnly);
            SetMeshRenderersShadowMode(ShadowCastingMode.ShadowsOnly);
        }

        /// <summary>
        /// Sets shadow casting mode for all SkinnedMeshRenderers.
        /// </summary>
        public void SetSkinnedMeshRenderersShadowMode(ShadowCastingMode mode, ShadowCastingMode? ownerMode = null,
            bool? isEnabled = null) {
            RefreshRendererCacheIfNeeded();
            var isOwner = playerController != null && playerController.IsOwner;

            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr == null) continue;
                
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
                _ = smr.bounds;
            }
        }

        /// <summary>
        /// Sets shadow casting mode for all MeshRenderers.
        /// </summary>
        private void SetMeshRenderersShadowMode(ShadowCastingMode mode) {
            RefreshRendererCacheIfNeeded();

            var fpCamera = playerController?.FpCamera;
            var fpCameraTransform = fpCamera?.transform;

            foreach(var mr in _cachedRenderers) {
                if(mr == null || mr is not MeshRenderer) continue;
                // Skip FP weapon renderers (they're parented to fpCamera)
                if(fpCameraTransform != null && mr.transform.IsChildOf(fpCameraTransform)) {
                    continue;
                }
            
                mr.shadowCastingMode = mode;
            }
        }

        /// <summary>
        /// Sets shadow casting mode for world weapon renderers.
        /// Gets the currently equipped weapon from the weapon socket.
        /// Caches renderers to prevent GetComponentsInChildren allocations.
        /// </summary>
        public void SetWorldWeaponRenderersShadowMode(ShadowCastingMode mode, bool isEnabled = true) {
            // Get the currently equipped world weapon from the socket
            var currentWorldWeapon = GetCurrentWorldWeapon();

            if(currentWorldWeapon != null) {
                // Check if weapon changed - if so, refresh cache
                var currentWeaponIndex = _weaponManager != null ? _weaponManager.CurrentWeaponIndex : -1;
                if(currentWorldWeapon != _cachedWorldWeapon || currentWeaponIndex != _cachedWeaponIndex) {
                    _cachedWorldWeapon = currentWorldWeapon;
                    _cachedWeaponIndex = currentWeaponIndex;
                    _cachedWorldWeaponRenderers = currentWorldWeapon.GetComponentsInChildren<MeshRenderer>();
                }

                // Use cached renderers
                if(_cachedWorldWeaponRenderers == null) return;
                foreach(var mr in _cachedWorldWeaponRenderers) {
                    if(mr == null) continue;
                    mr.shadowCastingMode = mode;
                    mr.enabled = isEnabled;
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
            if(_weaponManager != null) {
                var weaponData = _weaponManager.GetWeaponDataByIndex(_weaponManager.CurrentWeaponIndex);
                if(weaponData != null && !string.IsNullOrEmpty(weaponData.worldWeaponName) &&
                   _worldWeaponSocket != null) {
                    var worldObj = _worldWeaponSocket.Find(weaponData.worldWeaponName);
                    if(worldObj != null && worldObj.gameObject.activeSelf) {
                        return worldObj.gameObject;
                    }
                }
            }

            // Fallback: find the first active child in the weapon socket
            if(_worldWeaponSocket == null) return null;
            foreach(Transform child in _worldWeaponSocket) {
                if(child.gameObject.activeSelf) {
                    return child.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// Sets shadow casting mode for world weapon prefabs.
        /// </summary>
        public void SetWorldWeaponPrefabsShadowMode(ShadowCastingMode mode) {
            if(_worldWeaponPrefabs == null) return;
            foreach(var w in _worldWeaponPrefabs) {
                var weaponRenderers = w.GetComponentsInChildren<MeshRenderer>();
                foreach(var mr in weaponRenderers) {
                    if(mr != null) {
                        mr.shadowCastingMode = mode;
                    }
                }
            }
        }

        /// <summary>
        /// Refreshes the renderer cache if it's invalid.
        /// </summary>
        private void RefreshRendererCacheIfNeeded() {
            if(_renderersCacheValid) return;
            _cachedRenderers = GetComponentsInChildren<Renderer>(true);
            _cachedSkinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _renderersCacheValid = true;
        }

        /// <summary>
        /// Invalidates the renderer cache, forcing it to be refreshed on next access.
        /// </summary>
        public void InvalidateRendererCache() {
            _renderersCacheValid = false;
        }
    }
}