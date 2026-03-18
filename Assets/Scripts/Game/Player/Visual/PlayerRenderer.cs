using System.Collections.Generic;
using Diagnostics;
using Events;
using Game.Player.Contracts;
using Game.Weapon.Manager;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Visual {
    /// <summary>
    /// Centralized renderer management for all player-related renderers.
    /// Handles enabled state, materials, bounds, and caching.
    /// Shadow casting modes are handled by PlayerShadow.cs
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerRenderer : NetworkBehaviour {
        [Header("References")]
        [HideInInspector, SerializeField] private MonoBehaviour playerContextSource;

        private IPlayerVisualContext _playerContext;

        private WeaponManager _weaponManager;
        private Transform _worldWeaponSocket;
        private Transform _fpCameraTransform;
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
            if(!PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext)) {
                DevLog.LogError("[PlayerRenderer] IPlayerVisualContext not found!");
                enabled = false;
                return;
            }

            if(_weaponManager == null) _weaponManager = _playerContext.WeaponManager;
            if(_worldWeaponSocket == null) _worldWeaponSocket = _playerContext.WorldWeaponSocket;
            if(_fpCameraTransform == null && _playerContext.FpCamera != null) {
                _fpCameraTransform = _playerContext.FpCamera.transform;
            }

            _renderersCacheValid = false;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            InvalidateCache();
            EventBus.Subscribe<PlayerWorldWeaponPresentationRefreshRequestedEvent>(OnWorldWeaponRefreshRequested);
        }

        public override void OnNetworkDespawn() {
            EventBus.Unsubscribe<PlayerWorldWeaponPresentationRefreshRequestedEvent>(OnWorldWeaponRefreshRequested);
            base.OnNetworkDespawn();
        }

        private void OnWorldWeaponRefreshRequested(PlayerWorldWeaponPresentationRefreshRequestedEvent evt) {
            if(evt == null || _playerContext?.NetworkObject == null) return;
            if(evt.PlayerNetworkObjectId != _playerContext.NetworkObjectId) return;
            SetWorldWeaponRenderersEnabled(true);
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
        /// Sets enabled state for world weapon renderers.
        /// </summary>
        public void SetWorldWeaponRenderersEnabled(bool isEnabled) {
            var currentWorldWeapon = GetCurrentWorldWeapon();
            if(currentWorldWeapon == null) return;

            // Skip hopball visual (managed separately)
            if(currentWorldWeapon.GetComponent<PlayerManagedVisualMarker>() != null) return;

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

        #endregion

        #region Bounds Management

        /// <summary>
        /// Forces all SkinnedMeshRenderers to update their bounds to prevent culling issues.
        /// </summary>
        public void ForceAllRendererBoundsUpdate() {
            RefreshRendererCacheIfNeeded();
            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr == null) continue;
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
                ForceAllRendererBoundsUpdate();
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

        #endregion
    }
}


