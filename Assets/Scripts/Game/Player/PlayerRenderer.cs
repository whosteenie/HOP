using System;
using System.Collections.Generic;
using Game.Hopball;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Centralized renderer management for all player-related renderers.
    /// Handles enabled state, materials, bounds, and caching.
    /// Shadow casting modes are handled by PlayerShadow.cs
    /// </summary>
    [RequireComponent(typeof(PlayerController))]
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerRenderer : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private Weapons.WeaponManager _weaponManager;
        private Transform _worldWeaponSocket;
        private Transform _fpCameraTransform;
        private SkinnedMeshRenderer _playerMesh;

        // Renderer caches
        private Renderer[] _cachedAllRenderers;
        private SkinnedMeshRenderer[] _cachedSkinnedRenderers;
        private bool _renderersCacheValid;

        // Category-specific caches
        private readonly Dictionary<GameObject, Renderer[]> _cachedWeaponRenderers = new();
        private readonly Dictionary<GameObject, SkinnedMeshRenderer[]> _cachedFpWeaponSkinnedRenderers = new();
        private MeshRenderer[] _cachedWorldWeaponRenderers;
        private GameObject _cachedWorldWeapon;
        private int _cachedWeaponIndex = -1;

        // Bounds cache
        private static readonly Bounds MaxBounds = new(Vector3.zero, new Vector3(float.MaxValue, float.MaxValue, float.MaxValue));

        // Exclusions
        private const string GrappleLineName = "GrappleLine";

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[PlayerRenderer] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_weaponManager == null) _weaponManager = playerController.WeaponManager;
            if(_worldWeaponSocket == null) _worldWeaponSocket = playerController.WorldWeaponSocket;
            if(_playerMesh == null) _playerMesh = playerController.PlayerMesh;
            if(_fpCameraTransform == null && playerController.FpCamera != null) {
                _fpCameraTransform = playerController.FpCamera.transform;
            }

            _renderersCacheValid = false;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            InvalidateCache();
        }

        #region Cache Management

        /// <summary>
        /// Invalidates all renderer caches, forcing refresh on next access.
        /// </summary>
        public void InvalidateCache() {
            _renderersCacheValid = false;
            _cachedWeaponRenderers.Clear();
            _cachedFpWeaponSkinnedRenderers.Clear();
            _cachedWorldWeaponRenderers = null;
            _cachedWorldWeapon = null;
            _cachedWeaponIndex = -1;
        }

        private void RefreshRendererCacheIfNeeded() {
            if(_renderersCacheValid) return;
            _cachedAllRenderers = GetComponentsInChildren<Renderer>(true);
            _cachedSkinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _renderersCacheValid = true;
        }

        #endregion

        #region Enabled/Disabled State

        /// <summary>
        /// Sets enabled state for all renderers (excluding grapple line and FP camera children).
        /// </summary>
        public void SetAllRenderersEnabled(bool isEnabled, bool excludeGrappleLine = true) {
            RefreshRendererCacheIfNeeded();
            foreach(var r in _cachedAllRenderers) {
                if(r == null) continue;
                if(excludeGrappleLine && r.name == GrappleLineName) continue;
                if(IsFpCameraChild(r.transform)) continue;
                r.enabled = isEnabled;
            }
        }

        /// <summary>
        /// Sets enabled state for player body SkinnedMeshRenderers (excluding FP camera children).
        /// </summary>
        public void SetPlayerBodyRenderersEnabled(bool isEnabled) {
            RefreshRendererCacheIfNeeded();
            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr == null) continue;
                if(IsFpCameraChild(smr.transform)) continue;
                smr.enabled = isEnabled;
            }
        }

        /// <summary>
        /// Sets enabled state for all SkinnedMeshRenderers (including FP weapons).
        /// </summary>
        public void SetAllSkinnedRenderersEnabled(bool isEnabled) {
            RefreshRendererCacheIfNeeded();
            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr == null) continue;
                smr.enabled = isEnabled;
            }
        }

        /// <summary>
        /// Sets enabled state for world weapon renderers.
        /// </summary>
        public void SetWorldWeaponRenderersEnabled(bool isEnabled) {
            var currentWorldWeapon = GetCurrentWorldWeapon();
            if(currentWorldWeapon == null) return;

            // Skip hopball visual (managed separately)
            if(currentWorldWeapon.GetComponent<HopballVisual>() != null) return;

            // Check if weapon changed - refresh cache if needed
            var currentWeaponIndex = _weaponManager != null ? _weaponManager.CurrentWeaponIndex : -1;
            if(currentWorldWeapon != _cachedWorldWeapon || currentWeaponIndex != _cachedWeaponIndex) {
                _cachedWorldWeapon = currentWorldWeapon;
                _cachedWeaponIndex = currentWeaponIndex;
                _cachedWorldWeaponRenderers = currentWorldWeapon.GetComponentsInChildren<MeshRenderer>(true);
            }

            if(_cachedWorldWeaponRenderers == null) return;
            foreach(var mr in _cachedWorldWeaponRenderers) {
                if(mr == null) continue;
                mr.enabled = isEnabled;
            }
        }

        /// <summary>
        /// Sets enabled state for FP weapon renderers.
        /// </summary>
        public void SetFpWeaponRenderersEnabled(bool isEnabled, GameObject fpWeaponInstance = null) {
            if(fpWeaponInstance == null) {
                fpWeaponInstance = _weaponManager != null ? _weaponManager.GetCurrentFpWeapon() : null;
            }
            if(fpWeaponInstance == null) return;

            if(!_cachedWeaponRenderers.TryGetValue(fpWeaponInstance, out var renderers)) {
                renderers = fpWeaponInstance.GetComponentsInChildren<Renderer>(true);
                _cachedWeaponRenderers[fpWeaponInstance] = renderers;
            }

            foreach(var r in renderers) {
                if(r == null) continue;
                r.enabled = isEnabled;
            }
        }

        /// <summary>
        /// Sets enabled state for FP weapon SkinnedMeshRenderers (arms).
        /// </summary>
        public void SetFpWeaponSkinnedRenderersEnabled(bool isEnabled, GameObject fpWeaponInstance = null) {
            if(fpWeaponInstance == null) {
                fpWeaponInstance = _weaponManager != null ? _weaponManager.GetCurrentFpWeapon() : null;
            }
            if(fpWeaponInstance == null) return;

            if(!_cachedFpWeaponSkinnedRenderers.TryGetValue(fpWeaponInstance, out var skinnedRenderers)) {
                skinnedRenderers = fpWeaponInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                _cachedFpWeaponSkinnedRenderers[fpWeaponInstance] = skinnedRenderers;
            }

            foreach(var smr in skinnedRenderers) {
                if(smr == null) continue;
                smr.enabled = isEnabled;
            }
        }

        /// <summary>
        /// Sets enabled state for holster renderers.
        /// </summary>
        public void SetHolsterRenderersEnabled(bool isEnabled, GameObject holster = null) {
            while(true) {
                if(holster == null) {
                    // Set for all holsters
                    if(_weaponManager == null) return;
                    var primary = _weaponManager.PrimaryHolster;
                    var secondary = _weaponManager.SecondaryHolster;
                    SetHolsterRenderersEnabled(isEnabled, primary);
                    holster = secondary;
                    continue;
                }

                var renderers = holster.GetComponentsInChildren<MeshRenderer>(true);
                foreach(var mr in renderers) {
                    if(mr == null) continue;
                    mr.enabled = isEnabled;
                }

                break;
            }
        }

        /// <summary>
        /// Sets enabled state for hopball visual renderers.
        /// </summary>
        public static void SetHopballVisualRenderersEnabled(bool isEnabled, GameObject hopballVisual = null) {
            if(hopballVisual == null) return;
            var renderers = hopballVisual.GetComponentsInChildren<MeshRenderer>(true);
            foreach(var mr in renderers) {
                if(mr == null) continue;
                mr.enabled = isEnabled;
            }
        }

        #endregion

        #region Bounds Management

        /// <summary>
        /// Forces all SkinnedMeshRenderers to update their bounds to prevent culling issues.
        /// </summary>
        public void ForceAllSkinnedRendererBoundsUpdate() {
            RefreshRendererCacheIfNeeded();
            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr == null) continue;
                smr.updateWhenOffscreen = true;
                smr.localBounds = MaxBounds;
                _ = smr.bounds; // Force Unity to recognize bounds change
            }
        }

        /// <summary>
        /// Forces player body SkinnedMeshRenderer bounds update (excluding FP camera children).
        /// </summary>
        public void ForcePlayerBodyBoundsUpdate() {
            RefreshRendererCacheIfNeeded();
            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr == null) continue;
                if(IsFpCameraChild(smr.transform)) continue;
                smr.updateWhenOffscreen = true;
                smr.localBounds = MaxBounds;
                _ = smr.bounds; // Force Unity to recognize bounds change
            }
        }

        /// <summary>
        /// Verifies renderer visibility and fixes bounds issues.
        /// </summary>
        public void VerifyAndFixVisibility() {
            RefreshRendererCacheIfNeeded();
            var needsFix = false;

            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr == null || !smr.gameObject.activeInHierarchy) continue;
                if(!smr.enabled || smr.updateWhenOffscreen) continue;
                smr.updateWhenOffscreen = true;
                needsFix = true;
            }

            if(needsFix) {
                ForceAllSkinnedRendererBoundsUpdate();
            }
        }

        #endregion

        #region Material Management

        /// <summary>
        /// Gets the player body SkinnedMeshRenderer.
        /// </summary>
        public SkinnedMeshRenderer GetPlayerMesh() => _playerMesh;

        /// <summary>
        /// Applies material to player body SkinnedMeshRenderer at specified index.
        /// Preserves other materials in the array.
        /// </summary>
        public void ApplyPlayerBodyMaterial(Material material, int materialIndex = 1) {
            if(_playerMesh == null || material == null) return;
            var materials = _playerMesh.materials;
            if(materialIndex >= 0 && materialIndex < materials.Length) {
                materials[materialIndex] = material;
                _playerMesh.materials = materials;
            }
        }

        /// <summary>
        /// Applies material to FP weapon SkinnedMeshRenderer at specified index.
        /// </summary>
        public void ApplyFpWeaponSkinnedMaterial(Material material, int materialIndex = 1, GameObject fpWeaponInstance = null) {
            if(fpWeaponInstance == null) {
                fpWeaponInstance = _weaponManager != null ? _weaponManager.GetCurrentFpWeapon() : null;
            }
            if(fpWeaponInstance == null || material == null) return;

            if(!_cachedFpWeaponSkinnedRenderers.TryGetValue(fpWeaponInstance, out var skinnedRenderers)) {
                skinnedRenderers = fpWeaponInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                _cachedFpWeaponSkinnedRenderers[fpWeaponInstance] = skinnedRenderers;
            }

            foreach(var smr in skinnedRenderers) {
                if(smr == null) continue;
                var materials = smr.materials;
                if(materialIndex >= 0 && materialIndex < materials.Length) {
                    materials[materialIndex] = material;
                    smr.materials = materials;
                } else if(materials.Length == 0) {
                    smr.material = material;
                }
            }
        }

        /// <summary>
        /// Applies material to all renderers in a GameObject (used for hopball arm materials).
        /// </summary>
        public static void ApplyMaterialToRenderers(GameObject target, Material material, int materialIndex = 0) {
            if(target == null || material == null) return;
            var renderers = target.GetComponentsInChildren<Renderer>(true);
            foreach(var r in renderers) {
                if(r == null) continue;
                var materials = r.materials;
                if(materialIndex >= 0 && materialIndex < materials.Length) {
                    materials[materialIndex] = material;
                    r.materials = materials;
                } else if(materials.Length == 0) {
                    r.material = material;
                }
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets the currently equipped world weapon GameObject.
        /// </summary>
        private GameObject GetCurrentWorldWeapon() {
            if(_weaponManager == null) return null;
            var worldWeapon = _weaponManager.CurrentWorldWeaponInstance;
            if(worldWeapon != null && worldWeapon.activeSelf) {
                return worldWeapon;
            }

            if(_worldWeaponSocket == null) return null;
            foreach(Transform child in _worldWeaponSocket) {
                if(child.gameObject.activeSelf) {
                    return child.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// Checks if a transform is a child of the FP camera.
        /// </summary>
        private bool IsFpCameraChild(Transform tr) {
            return _fpCameraTransform != null && tr.IsChildOf(_fpCameraTransform);
        }

        /// <summary>
        /// Gets all renderers of a specific type from a GameObject.
        /// </summary>
        public T[] GetRenderersFromGameObject<T>(GameObject obj, bool includeInactive = true) where T : Renderer {
            return obj == null ? Array.Empty<T>() : obj.GetComponentsInChildren<T>(includeInactive);
        }

        #endregion
    }
}

