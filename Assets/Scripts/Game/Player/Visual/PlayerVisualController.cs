using System.Collections;
using System.Collections.Generic;
using Diagnostics;
using Events;
using Game.Player.Contracts;
using Game.Weapon.Manager;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Visual {
    /// <summary>
    /// Handles all visual, material, and renderer management for the player.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerVisualController : NetworkBehaviour {
        private static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");

        [Header("References")]
        [HideInInspector, SerializeField] private MonoBehaviour playerContextSource;

        private IPlayerVisualContext _playerContext;

        private PlayerShadow _playerShadow;
        private PlayerRenderer _playerRenderer;
        private WeaponManager _weaponManager;
        private SkinnedMeshRenderer _playerMesh;

        private GameObject _playerModelRoot;
        private Transform _worldWeaponSocket;
        private MaterialPropertyBlock _tagPropertyBlock;

        private Material[] _cachedMaterialsArray;
        private readonly HashSet<int> _loggedMissingFpArmRoots = new();

        private void Awake() {
            ValidateComponents();
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            EventBus.Subscribe<PlayerFpWeaponRefreshRequestedEvent>(OnFpWeaponRefreshRequested);
        }

        public override void OnNetworkDespawn() {
            EventBus.Unsubscribe<PlayerFpWeaponRefreshRequestedEvent>(OnFpWeaponRefreshRequested);
            base.OnNetworkDespawn();
        }

        private void ValidateComponents() {
            if(!PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext)) {
                DevLog.LogError("[PlayerVisualController] IPlayerVisualContext not found!");
                enabled = false;
                return;
            }

            if(_playerShadow == null) {
                _playerShadow = GetComponent<PlayerShadow>();
            }

            if(_playerRenderer == null) {
                _playerRenderer = GetComponent<PlayerRenderer>();
            }

            if(_weaponManager == null) {
                _weaponManager = _playerContext.WeaponManager;
            }

            if(_playerMesh == null) {
                _playerMesh = _playerContext.PlayerMesh;
            }

            if(_playerModelRoot == null) {
                _playerModelRoot = _playerContext.PlayerModelRoot;
            }

            if(_worldWeaponSocket == null) {
                _worldWeaponSocket = _playerContext.WorldWeaponSocket;
            }
            ApplyMaterialToAllFpArms();
        }

        private void OnFpWeaponRefreshRequested(PlayerFpWeaponRefreshRequestedEvent evt) {
            if(evt == null || _playerContext?.NetworkObject == null) return;
            if(evt.PlayerNetworkObjectId != _playerContext.NetworkObjectId) return;
            if(evt.FpWeaponInstance == null) return;

            if(_playerRenderer != null) _playerRenderer.SetFpWeaponRenderersEnabled(true, evt.FpWeaponInstance);

            if(_playerRenderer != null) _playerRenderer.SetFpWeaponSkinnedRenderersEnabled(true, evt.FpWeaponInstance);

            ApplyMaterialToFpArms(evt.FpWeaponInstance);
            UpdateFpArmTagGlow(_playerContext.IsTagged, evt.FpWeaponInstance);
        }

        private void ApplyMaterialToAllFpArms() {
            if(_weaponManager == null) return;

            foreach(var fpWeapon in _weaponManager.FpWeaponInstancesRef) {
                if(fpWeapon == null) continue;
                ApplyMaterialToFpArms(fpWeapon);
            }
        }

        /// <summary>
        /// Applies the current generated player material (Index 1) to the FP weapon arms.
        /// Index 0 is reserved for Outline.
        /// </summary>
        private void ApplyMaterialToFpArms(GameObject fpWeaponInstance) {
            if(fpWeaponInstance == null || _cachedMaterialsArray == null || _cachedMaterialsArray.Length < 2) {
                return;
            }
            
            var generatedMaterial = _cachedMaterialsArray[1];
            if(generatedMaterial == null) return;

            var armRenderers = ResolveFpArmRenderers(fpWeaponInstance);
            if(armRenderers.Length == 0) {
                var weaponId = fpWeaponInstance.GetInstanceID();
                if(_loggedMissingFpArmRoots.Add(weaponId)) {
                    DevLog.LogWarning(
                        $"[PlayerVisualController] No FP arm renderers resolved for '{fpWeaponInstance.name}' during material apply.",
                        fpWeaponInstance);
                }
                return;
            }

            ApplyMaterialToRenderers(armRenderers, generatedMaterial, 1);
        }

        /// <summary>
        /// Updates the tag glow on the FP weapon arms.
        /// </summary>
        public void UpdateFpArmTagGlow(bool isTagged, GameObject weaponInstance) {
            if(weaponInstance == null || _playerContext == null) return;

            var renderers = ResolveFpArmRenderers(weaponInstance);
            if(renderers.Length == 0) return;

            if(isTagged) {
                if(_tagPropertyBlock == null) _tagPropertyBlock = new MaterialPropertyBlock();

                foreach(var r in renderers) {
                    r.GetPropertyBlock(_tagPropertyBlock, 0); // Get from index 0 (Outline)
                    _tagPropertyBlock.SetColor(OutlineColorId, _playerContext.TaggedGlowColor);
                    r.SetPropertyBlock(_tagPropertyBlock, 0);
                }
            } else {
                // Clear property block to reset to default material properties
                foreach(var r in renderers) {
                    r.SetPropertyBlock(null, 0);
                }
            }
        }

        /// <summary>
        /// Applies player material customization using the packet-based system.
        /// </summary>
        public void ApplyPlayerMaterialCustomization(in PlayerMaterialCustomizationRequest request) {
            
            // Ensure materials array is initialized (preserves outline at index 0)
            if(_cachedMaterialsArray == null || _cachedMaterialsArray.Length < 2) {
                _cachedMaterialsArray = _playerMesh.materials;
            }

            // Get packet from manager
            var packetManager = PlayerMaterialPacketManager.Instance;
            if(packetManager == null) {
                DevLog.LogWarning("[PlayerVisualController] PlayerMaterialPacketManager not found. Falling back to legacy system.");
                return;
            }

            var packet = packetManager.GetPacket(request.PacketIndex);
            if(packet == null) {
                DevLog.LogWarning($"[PlayerVisualController] Invalid packet index {request.PacketIndex}. Using None packet.");
                packet = packetManager.GetNonePacket();
            }

            // Generate material using the packet system
            var generatedMaterial = PlayerMaterialGenerator.GenerateMaterial(packet,
                new PlayerMaterialGenerationRequest {
                    BaseColor = request.BaseColor,
                    Smoothness = request.Smoothness,
                    Metallic = request.Metallic,
                    SpecularColor = request.SpecularColor,
                    HeightStrength = request.HeightStrength,
                    EmissionEnabled = request.EmissionEnabled,
                    EmissionColor = request.EmissionColor
                });

            if(generatedMaterial == null) {
                DevLog.LogError("[PlayerVisualController] Failed to generate material from packet.");
                return;
            }

            // Only modify material slot 1 (preserve outline at index 0)
            if(_cachedMaterialsArray[1] == generatedMaterial) return;
            _cachedMaterialsArray[1] = generatedMaterial;
            _playerMesh.materials = _cachedMaterialsArray;

            if(!IsOwner) return;
            ApplyMaterialToAllFpArms();
        }

        /// <summary>
        /// Sets the visibility of the world model (for other players to see).
        /// </summary>
        public void SetWorldModelVisible(bool visible) {
            if(_playerRenderer != null) {
                _playerRenderer.InvalidateCache();
            }

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

                if(_weaponManager != null) {
                    _weaponManager.RefreshHolsterVisibility();
                }

                // Enable all renderers and set proper shadow modes
                if(_playerRenderer != null) {
                    _playerRenderer.SetAllRenderersEnabled(true);
                }
                if(_playerShadow != null) {
                    _playerShadow.ApplyVisibleShadowState();
                }

                // Force bounds update immediately
                if(_playerRenderer != null) {
                    _playerRenderer.ForceAllRendererBoundsUpdate();
                }

                // Schedule delayed bounds update to ensure Unity has positioned everything
                StartCoroutine(DelayedBoundsUpdate());
            } else {
                if(_playerModelRoot != null) {
                    _playerModelRoot.SetActive(false);
                }

                // Deactivate the currently equipped world weapon
                var currentWorldWeapon = GetCurrentWorldWeapon();
                if(currentWorldWeapon != null) {
                    currentWorldWeapon.SetActive(false);
                }
            }
        }

        /// <summary>
        /// Sets whether renderers are enabled or disabled.
        /// Delegates to PlayerRenderer.
        /// </summary>
        public void SetRenderersEnabled(bool isEnabled, bool excludeGrappleLine = true) {
            if(_playerRenderer == null) return;
            _playerRenderer.SetAllRenderersEnabled(isEnabled, excludeGrappleLine);
            if(isEnabled) {
                _playerRenderer.ForceAllRendererBoundsUpdate();
            }
        }

        /// <summary>
        /// Gets the currently equipped world weapon GameObject from the weapon socket.
        /// </summary>
        private GameObject GetCurrentWorldWeapon() {
            return _weaponManager != null ? _weaponManager.CurrentWorldWeaponInstance : null;
        }

        /// <summary>
        /// Forces all SkinnedMeshRenderers to update their bounds immediately.
        /// Delegates to PlayerRenderer.
        /// </summary>
        public void ForceRendererBoundsUpdate() {
            if(_playerRenderer == null) return;
            _playerRenderer.ForceAllRendererBoundsUpdate();
        }

        /// <summary>
        /// Verifies that renderers are visible and fixes any issues found.
        /// Delegates to PlayerRenderer.
        /// </summary>
        public void VerifyAndFixVisibility() {
            // Only check if world model should be visible
            if(_playerModelRoot == null || !_playerModelRoot.activeSelf) return;
            if(_playerContext == null || _playerContext.IsDead) return;

            if(_playerRenderer != null) {
                _playerRenderer.VerifyAndFixVisibility();
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
            if(_playerRenderer != null) {
                _playerRenderer.ForceAllRendererBoundsUpdate();
            }

            // Wait another frame and update once more to be thorough
            yield return null;
            if(_playerRenderer != null) {
                _playerRenderer.ForceAllRendererBoundsUpdate();
            }
        }

        /// <summary>
        /// Invalidates the renderer cache, forcing it to be refreshed on next access.
        /// Delegates to PlayerRenderer.
        /// </summary>
        public void InvalidateRendererCache() {
            if(_playerRenderer != null) {
                _playerRenderer.InvalidateCache();
            }
        }

        private static Renderer[] ResolveFpArmRenderers(GameObject fpWeaponInstance) {
            if(fpWeaponInstance == null) return System.Array.Empty<Renderer>();

            var armRoot = FindTaggedArmRoot(fpWeaponInstance.transform);
            return armRoot == null ? System.Array.Empty<Renderer>() : armRoot.GetComponentsInChildren<Renderer>(true);
        }

        private static Transform FindTaggedArmRoot(Transform root) {
            foreach(var child in root.GetComponentsInChildren<Transform>(true)) {
                if(IsArmTagged(child)) {
                    return child;
                }
            }

            return null;
        }

        private static bool IsArmTagged(Component component) {
            if(component == null) return false;

            try {
                return component.CompareTag("Arm");
            } catch(UnityException) {
                // If the Arm tag is missing in TagManager, treat as not tagged.
                return false;
            }
        }

        private static void ApplyMaterialToRenderers(Renderer[] renderers, Material material, int materialIndex) {
            if(renderers == null || material == null) return;

            foreach(var renderer in renderers) {
                if(renderer == null) continue;

                var materials = renderer.materials;
                if(materials == null || materials.Length == 0) {
                    renderer.material = material;
                    continue;
                }

                if(materialIndex < 0 || materialIndex >= materials.Length) continue;
                materials[materialIndex] = material;
                renderer.materials = materials;
            }
        }

        // Public getters for PlayerController
        public GameObject GetWorldWeapon() => GetCurrentWorldWeapon();
    }
}

