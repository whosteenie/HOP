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
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private PlayerShadow _playerShadow;
        private Weapons.WeaponManager _weaponManager;
        private SkinnedMeshRenderer _playerMesh;

        private GameObject _playerModelRoot;
        private Transform _worldWeaponSocket;
        private GameObject[] _worldWeaponPrefabs;

        private Renderer[] _cachedRenderers;
        private SkinnedMeshRenderer[] _cachedSkinnedRenderers;
        private bool _renderersCacheValid;
        private Material[] _cachedMaterialsArray;
        private const string GrappleLineName = "GrappleLine";

        // Cache Bounds object to prevent allocations
        private static readonly Bounds MaxBounds = new(Vector3.zero,
            new Vector3(float.MaxValue, float.MaxValue, float.MaxValue));

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[PlayerVisualController] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_playerShadow == null) {
                _playerShadow = playerController.PlayerShadow;
            }

            if(_weaponManager == null) {
                _weaponManager = playerController.WeaponManager;
            }

            if(_playerMesh == null) {
                _playerMesh = playerController.PlayerMesh;
            }

            if(_playerModelRoot == null) {
                _playerModelRoot = playerController.PlayerModelRoot;
            }

            if(_worldWeaponSocket == null) {
                _worldWeaponSocket = playerController.WorldWeaponSocket;
            }

            if(_worldWeaponPrefabs == null || _worldWeaponPrefabs.Length == 0) {
                _worldWeaponPrefabs = playerController.WorldWeaponPrefabs;
            }

            if(_playerMesh != null) {
                _cachedMaterialsArray = _playerMesh.materials;
            }

            _renderersCacheValid = false;
        }

        /// <summary>
        /// Applies player material customization using the new packet-based system.
        /// Generates a URP/Lit material from the packet and customization values.
        /// Preserves the outline material at index 0.
        /// </summary>
        /// <param name="packetIndex">Index of the material packet (0 = None, 1+ = loaded packets)</param>
        /// <param name="baseColor">Base color tint</param>
        /// <param name="smoothness">Smoothness value (0-1)</param>
        /// <param name="metallic">Metallic value (0-1), only used if packet uses metallic workflow</param>
        /// <param name="specularColor">Specular color, only used if packet uses specular workflow</param>
        /// <param name="heightStrength">Height map strength override, uses packet default if null</param>
        /// <param name="emissionEnabled"></param>
        /// <param name="emissionColor"></param>
        public void ApplyPlayerMaterialCustomization(int packetIndex, Color baseColor, float smoothness, 
            float metallic = 0f, Color? specularColor = null, float? heightStrength = null,
            bool emissionEnabled = false, Color? emissionColor = null) {
            
            // Ensure materials array is initialized (preserves outline at index 0)
            if(_cachedMaterialsArray == null || _cachedMaterialsArray.Length < 2) {
                _cachedMaterialsArray = _playerMesh.materials;
            }

            // Get packet from manager
            var packetManager = Network.Singletons.PlayerMaterialPacketManager.Instance;
            if(packetManager == null) {
                Debug.LogWarning("[PlayerVisualController] PlayerMaterialPacketManager not found. Falling back to legacy system.");
                return;
            }

            var packet = packetManager.GetPacket(packetIndex);
            if(packet == null) {
                Debug.LogWarning($"[PlayerVisualController] Invalid packet index {packetIndex}. Using None packet.");
                packet = packetManager.GetNonePacket();
            }

            // Generate material using the packet system
            var generatedMaterial = PlayerMaterialGenerator.GenerateMaterial(
                packet, baseColor, smoothness, metallic, specularColor, heightStrength, emissionEnabled, emissionColor);

            if(generatedMaterial == null) {
                Debug.LogError("[PlayerVisualController] Failed to generate material from packet.");
                return;
            }

            // Only modify material slot 1 (preserve outline at index 0)
            if(_cachedMaterialsArray[1] == generatedMaterial) return;
            _cachedMaterialsArray[1] = generatedMaterial;
            _playerMesh.materials = _cachedMaterialsArray;
        }

        /// <summary>
        /// Sets the visibility of the world model (for other players to see).
        /// </summary>
        [Rpc(SendTo.Everyone)]
        public void SetWorldModelVisibleRpc(bool visible) {
            _weaponManager?.SwitchWeapon(0);

            InvalidateRendererCache();

            if(visible) {
                // Ensure world model root and weapon are active
                if(_playerModelRoot != null && !_playerModelRoot.activeSelf) {
                    _playerModelRoot.SetActive(true);
                }

                // Activate the currently equipped world weapon
                var currentWorldWeapon = GetCurrentWorldWeapon();
                if(currentWorldWeapon != null && !currentWorldWeapon.activeSelf) {
                    currentWorldWeapon.SetActive(true);
                }

                // Enable all renderers and set proper shadow modes
                SetRenderersEnabled(true);
                if(_playerShadow != null) {
                    _playerShadow.SetSkinnedMeshRenderersShadowMode(ShadowCastingMode.On);
                    _playerShadow.SetWorldWeaponRenderersShadowMode(ShadowCastingMode.On);
                }

                // Force bounds update immediately
                ForceRendererBoundsUpdate();

                // Schedule delayed bounds update to ensure Unity has positioned everything
                StartCoroutine(DelayedBoundsUpdate());
            } else {
                _playerModelRoot?.SetActive(false);

                // Deactivate the currently equipped world weapon
                var currentWorldWeapon = GetCurrentWorldWeapon();
                currentWorldWeapon?.SetActive(false);
            }
        }

        /// <summary>
        /// Sets whether renderers are enabled or disabled.
        /// </summary>
        public void SetRenderersEnabled(bool isEnabled, bool excludeGrappleLine = true, ShadowCastingMode? shadowMode = null) {
            RefreshRendererCacheIfNeeded();
            foreach(var r in _cachedRenderers) {
                if(r == null || (excludeGrappleLine && r.name == GrappleLineName)) continue;

                r.enabled = isEnabled;
                if(shadowMode.HasValue) {
                    r.shadowCastingMode = shadowMode.Value;
                }

                // For SkinnedMeshRenderers, ensure bounds are updated
                if(r is not SkinnedMeshRenderer smr) continue;
                smr.updateWhenOffscreen = true;

                // Set bounds to maximum values to prevent culling issues (use cached static bounds)
                // Note: localBounds is what Unity uses for frustum culling on SkinnedMeshRenderers
                smr.localBounds = MaxBounds;

                // Force Unity to recognize the bounds change by accessing the world-space bounds property
                _ = smr.bounds;
            }
        }

        /// <summary>
        /// Gets the currently equipped world weapon GameObject from the weapon socket.
        /// </summary>
        private GameObject GetCurrentWorldWeapon() => _weaponManager?.CurrentWorldWeaponInstance;

        /// <summary>
        /// Forces all SkinnedMeshRenderers to update their bounds immediately.
        /// This helps prevent frustum culling issues where renderers become invisible
        /// even though they're enabled and should be visible.
        /// Sets bounds to maximum values to prevent culling when players are barely on screen.
        /// </summary>
        public void ForceRendererBoundsUpdate() {
            RefreshRendererCacheIfNeeded();
            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr == null) continue;
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
                _ = smr.bounds;
            }
        }

        /// <summary>
        /// Verifies that renderers are visible and fixes any issues found.
        /// This is a safety check to catch cases where renderers become invisible.
        /// </summary>
        public void VerifyAndFixVisibility() {
            // Only check if world model should be visible
            if(_playerModelRoot == null || !_playerModelRoot.activeSelf) return;
            if(playerController == null || playerController.IsDead) return;

            RefreshRendererCacheIfNeeded();
            var needsFix = false;

            foreach(var smr in _cachedSkinnedRenderers) {
                if(smr == null || !smr.gameObject.activeInHierarchy) continue;
                // Check if renderer is enabled but might have bounds issues
                if(!smr.enabled || smr.updateWhenOffscreen) continue;
                smr.updateWhenOffscreen = true;
                needsFix = true;
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

        // Public getters for PlayerController
        public GameObject GetWorldWeapon() => GetCurrentWorldWeapon();
    }
}