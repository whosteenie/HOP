using Events;
using Game.Match;
using Game.Player.Contracts;
using Game.Weapon.Manager;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Player.Visual {
    /// <summary>
    /// Handles shadow casting mode management for player renderers.
    /// Enhanced to centralize all shadow mode logic from PlayerController.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerShadow : NetworkBehaviour {
        [Header("References")]
        [HideInInspector, SerializeField] private MonoBehaviour playerContextSource;

        private IPlayerVisualContext _playerContext;

        private WeaponManager _weaponManager;
        private PlayerRenderer _playerRenderer;
        private Transform _worldWeaponSocket; // Socket containing all world weapon GameObjects

        private SkinnedMeshRenderer[] _cachedSkinnedRenderers;
        private Renderer[] _cachedRenderers;
        private bool _renderersCacheValid;
        private WeaponHolsterData _holsterData;
        private bool _holsterDataInitialized;

        // Cache world weapon renderers to prevent GetComponentsInChildren allocations
        private GameObject _cachedWorldWeapon;
        private MeshRenderer[] _cachedWorldWeaponRenderers;
        private int _cachedWeaponIndex = -1;

        // Cache Bounds object to prevent allocations
        private static readonly Bounds MaxBounds = new(Vector3.zero, new Vector3(float.MaxValue, float.MaxValue, float.MaxValue));

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(!PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext)) {
                Debug.LogError("[PlayerShadow] IPlayerVisualContext not found!");
                enabled = false;
                return;
            }

            if(_weaponManager == null) _weaponManager = _playerContext.WeaponManager;
            if(_playerRenderer == null) _playerRenderer = GetComponent<PlayerRenderer>();
            if(_worldWeaponSocket == null) _worldWeaponSocket = _playerContext.WorldWeaponSocket;

            _renderersCacheValid = false;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            EventBus.Subscribe<PlayerWorldWeaponPresentationRefreshRequestedEvent>(OnPlayerWorldWeaponPresentationRefreshRequested);
            EventBus.Subscribe<PlayerHolsterShadowRefreshRequestedEvent>(OnPlayerHolsterShadowRefreshRequested);

            // Network-dependent initialization
            // Original behavior: set owner's shadows to ShadowsOnly
            if(!IsOwner) return;
            ApplyOwnerDefaultShadowState();
        }

        public override void OnNetworkDespawn() {
            EventBus.Unsubscribe<PlayerWorldWeaponPresentationRefreshRequestedEvent>(OnPlayerWorldWeaponPresentationRefreshRequested);
            EventBus.Unsubscribe<PlayerHolsterShadowRefreshRequestedEvent>(OnPlayerHolsterShadowRefreshRequested);
            base.OnNetworkDespawn();
        }

        private void OnPlayerWorldWeaponPresentationRefreshRequested(PlayerWorldWeaponPresentationRefreshRequestedEvent evt) {
            if(evt == null || _playerContext?.NetworkObject == null) return;
            if(evt.PlayerNetworkObjectId != _playerContext.NetworkObjectId) return;

            if(evt.UsePodiumShadowState) {
                ApplyPodiumShadowState();
            } else {
                SetWorldWeaponShadowMode(ShadowCastingMode.On);
            }
        }

        private void OnPlayerHolsterShadowRefreshRequested(PlayerHolsterShadowRefreshRequestedEvent evt) {
            if(evt == null || _playerContext?.NetworkObject == null) return;
            if(evt.PlayerNetworkObjectId != _playerContext.NetworkObjectId) return;
            UpdateHolsterShadowState();
        }

        /// <summary>Sets shadow casting mode for all SkinnedMeshRenderers.</summary>
        private void SetSkinnedMeshShadowMode(ShadowCastingMode mode, ShadowCastingMode? ownerMode = null, bool? isEnabled = null) {
            RefreshRendererCacheIfNeeded();
            var isOwner = _playerContext is { IsOwner: true };

            Transform fpCameraTransform = null;
            if(_playerContext != null && _playerContext.FpCamera != null) {
                fpCameraTransform = _playerContext.FpCamera.transform;
            }

            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr == null) continue;
                
                // Skip FP weapon renderers (they're parented to fpCamera)
                if(fpCameraTransform != null && smr.transform.IsChildOf(fpCameraTransform)) {
                    continue;
                }
                
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
        private void SetMeshRenderersShadowMode(ShadowCastingMode mode, bool? isEnabled = null) {
            RefreshRendererCacheIfNeeded();

            Transform fpCameraTransform = null;
            if(_playerContext != null && _playerContext.FpCamera != null) {
                fpCameraTransform = _playerContext.FpCamera.transform;
            }

            foreach(var mr in _cachedRenderers) {
                if(mr == null || mr.GetType() != typeof(MeshRenderer)) continue;
                // Skip FP weapon renderers (they're parented to fpCamera)
                if(fpCameraTransform != null && mr.transform.IsChildOf(fpCameraTransform)) {
                    continue;
                }
                
                // Skip subsystem-managed visuals (they're handled separately)
                if(mr.GetComponent<PlayerManagedVisualMarker>() != null) {
                    continue;
                }
            
                if(isEnabled.HasValue) {
                    mr.enabled = isEnabled.Value;
                }
                mr.shadowCastingMode = mode;
            }
        }

        /// <summary>Sets shadow casting mode for world weapon renderers.</summary>
        public void SetWorldWeaponShadowMode(ShadowCastingMode mode, bool isEnabled = true) {
            // Get the currently equipped world weapon from the socket
            var currentWorldWeapon = GetCurrentWorldWeapon();

            if(currentWorldWeapon != null) {
                // Skip subsystem-managed visuals (they're handled separately)
                if(currentWorldWeapon.GetComponent<PlayerManagedVisualMarker>() != null) {
                    return;
                }
                
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
        /// Applies the default shadow state for the owner (shadows only).
        /// </summary>
        public void ApplyOwnerDefaultShadowState() {
            // Ensure renderers are enabled (they may have been disabled during death)
            // Set shadow modes to ShadowsOnly so owner sees their shadow but not the model
            SetSkinnedMeshShadowMode(ShadowCastingMode.ShadowsOnly, null, true);
            SetMeshRenderersShadowMode(ShadowCastingMode.ShadowsOnly, true);
            SetWorldWeaponShadowMode(ShadowCastingMode.ShadowsOnly);
            TrySetHolsterShadowState(true, false, ShadowCastingMode.ShadowsOnly);
        }

        /// <summary>
        /// Applies the shadow state for the death camera.
        /// </summary>
        /// <param name="wasHoldingHopball">Whether the player was holding a hopball at death.</param>
        public void ApplyDeathShadowState(bool wasHoldingHopball = false) {
            // For death camera, all third-person models should be ShadowsOn (visible)
            SetSkinnedMeshShadowMode(ShadowCastingMode.On);
            SetMeshRenderersShadowMode(ShadowCastingMode.On);
            SetWorldWeaponShadowMode(ShadowCastingMode.On);
            
            // Show both holsters only if holding hopball, otherwise show only unequipped holster
            // The equipped weapon's in-hand model is already visible via SetWorldWeaponShadowMode
            TrySetHolsterShadowState(true, wasHoldingHopball, ShadowCastingMode.On);
        }

        /// <summary>
        /// Applies the visible shadow state for non-owners.
        /// </summary>
        public void ApplyVisibleShadowState() {
            SetSkinnedMeshShadowMode(ShadowCastingMode.On, null, true);
            SetWorldWeaponShadowMode(ShadowCastingMode.On);
            TrySetHolsterShadowState(false, false);
        }

        /// <summary>
        /// Applies the shadow state for the podium display.
        /// </summary>
        public void ApplyPodiumShadowState() {
            SetSkinnedMeshShadowMode(ShadowCastingMode.On, null, true);
            SetWorldWeaponShadowMode(ShadowCastingMode.On);
            TrySetHolsterShadowState(true, false, ShadowCastingMode.On);
        }

        /// <summary>
        /// Applies shadow state changes based on hopball holding status.
        /// </summary>
        /// <param name="holdingHopball">Whether the player is holding a hopball.</param>
        /// <param name="isOwner">Whether the player is the owner of this GameObject.</param>
        public void ApplyHopballShadowState(bool holdingHopball, bool isOwner) {
            if(!_holsterDataInitialized) InitializeHolsterData();
            if(_holsterData != null) {
                _holsterData.ApplyHopballShadowState(holdingHopball, isOwner);
            }
            
            // When holding hopball, hide in-hand weapon for both owners and non-owners
            // Owners: ShadowsOff and disable (they see shadows only)
            // Non-owners: ShadowsOff and disable (they see the model, but we hide it when holding hopball)
            if(holdingHopball) {
                SetWorldWeaponShadowMode(ShadowCastingMode.Off, false);
            }
        }

        /// <summary>
        /// Updates holster shadow state for owners after weapon switches.
        /// Only updates holster shadows, not player body or world weapon shadows.
        /// </summary>
        /// <summary>Updates holster shadow state for the owner.</summary>
        private void UpdateHolsterShadowState() {
            if(_playerContext is not { IsOwner: true }) return;
            var isPostMatch = PostMatchManager.Instance != null && PostMatchManager.Instance.PostMatchFlowStarted;
            if(isPostMatch) {
                TrySetHolsterShadowState(true, false, ShadowCastingMode.On);
            } else {
                TrySetHolsterShadowState(true, false);
            }
        }

        private void InitializeHolsterData() {
            if(_holsterDataInitialized) return;
            _holsterDataInitialized = true;
            var weaponManager = _playerContext != null ? _playerContext.WeaponManager : null;
            _holsterData = new WeaponHolsterData(weaponManager);
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

        private void TrySetHolsterShadowState(bool owner, bool showBothHolsters,
            ShadowCastingMode? ownerOverride = null) {
            if(!_holsterDataInitialized) InitializeHolsterData();
            if(_holsterData == null) return;
            _holsterData.SetHolsterShadowState(owner, showBothHolsters, ownerOverride);
        }

        private sealed class WeaponHolsterData {
            private readonly WeaponManager _weaponManager;

            public WeaponHolsterData(WeaponManager weaponManager) {
                _weaponManager = weaponManager;
            }

            private GameObject PrimaryHolster => _weaponManager == null ? null : _weaponManager.PrimaryHolster;
            private GameObject SecondaryHolster => _weaponManager == null ? null : _weaponManager.SecondaryHolster;

            public void SetHolsterShadowState(bool owner, bool showBothHolsters, ShadowCastingMode? ownerOverride = null) {
                if(_weaponManager == null) return;

                var primary = PrimaryHolster;
                var secondary = SecondaryHolster;

                if(showBothHolsters) {
                    if(primary != null) primary.SetActive(true);
                    if(secondary != null) secondary.SetActive(true);

                    var mode = ownerOverride.HasValue ? ownerOverride.Value : owner ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
                    SetShadowMode(primary, mode);
                    SetShadowMode(secondary, mode);
                    return;
                }

                _weaponManager.RefreshHolsterVisibility();

                if(owner) {
                    var currentSlot = _weaponManager.GetCurrentHolsterSlot();
                    var holsterMode = ownerOverride ?? ShadowCastingMode.ShadowsOnly;

                    switch(currentSlot) {
                        // Only set shadow mode for the unequipped weapon's holster
                        // The equipped weapon's holster should be inactive (handled by RefreshHolsterVisibility)
                        case 0:
                            // Primary equipped, set secondary holster to ShadowsOnly
                            SetShadowMode(secondary, holsterMode);
                            break;
                        case 1:
                            // Secondary equipped, set primary holster to ShadowsOnly
                            SetShadowMode(primary, holsterMode);
                            break;
                        default:
                            // Unknown slot, set both to ShadowsOnly
                            SetShadowMode(primary, holsterMode);
                            SetShadowMode(secondary, holsterMode);
                            break;
                    }
                } else {
                    var mode = ownerOverride ?? ShadowCastingMode.On;
                    SetShadowMode(primary, mode);
                    SetShadowMode(secondary, mode);
                }
            }

            public void ApplyHopballShadowState(bool holding, bool owner) {
                if(_weaponManager == null) return;

                var primary = PrimaryHolster;
                var secondary = SecondaryHolster;

                if(holding) {
                    if(primary != null) primary.SetActive(true);
                    if(secondary != null) secondary.SetActive(true);

                    var mode = owner ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
                    SetShadowMode(primary, mode);
                    SetShadowMode(secondary, mode);
                } else {
                    SetHolsterShadowState(owner, false);
                }
            }

            private static void SetShadowMode(GameObject holster, ShadowCastingMode mode) {
                if(holster == null) return;
                var renderers = holster.GetComponentsInChildren<MeshRenderer>(true);
                foreach(var renderer in renderers) {
                    if(renderer == null) continue;
                    renderer.enabled = true; // Ensure renderer is enabled for shadows
                    renderer.shadowCastingMode = mode;
                }
            }
        }
    }
}

