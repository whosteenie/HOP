using System.Collections;
using Game.Match;
using Game.Spawning;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    [RequireComponent(typeof(PlayerController))]
    public class PlayerTeamManager : NetworkBehaviour {
        private static readonly int outlineColor = Shader.PropertyToID("_OutlineColor");
        private static readonly int size = Shader.PropertyToID("_Size");
        [SerializeField] private PlayerController playerController;

        // --------------------------------------------------------------------
        // 1. Networked team (synced once at spawn)
        // --------------------------------------------------------------------
        public NetworkVariable<SpawnPoint.Team> netTeam = new();

        // --------------------------------------------------------------------
        // 2. Outline colours (tweak in inspector)
        // --------------------------------------------------------------------
        [Header("Outline Colours")]
        [ColorUsage(true, true)] // HDR enabled: showAlpha=true, hdr=true
        [SerializeField]
        private Color teammateOutline = new(0f, 1.5f, 2.5f, 1f); // Bright cyan-blue with HDR glow

        [ColorUsage(true, true)] // HDR enabled
        [SerializeField]
        private Color enemyOutline = new(2.5f, 0.5f, 0.5f, 1f); // Bright red with HDR glow

        [ColorUsage(true, true)] // HDR enabled
        [SerializeField]
        private Color taggedGlow = new(8f, 6f, 1f, 1f); // Very bright yellow-orange with HDR glow for tagged players

        [Header("Outline Distance Scaling")]
        [SerializeField] private float minOutlineSize = 0.008f; // Minimum size (close distance)

        [SerializeField]
        private float maxOutlineSize = 0.025f; // Maximum size (far distance) - much thicker at distance

        [SerializeField] private float distanceScaleStart = 10f; // Start scaling at this distance
        [SerializeField] private float distanceScaleEnd = 100f; // Max scaling at this distance

        // --------------------------------------------------------------------
        // 3. Cached components
        // --------------------------------------------------------------------
        private SkinnedMeshRenderer _skinned;
        private MaterialPropertyBlock _propertyBlock;
        private MaterialPropertyBlock _tagPropertyBlock; // Reusable property block for tagged players
        private PlayerTagController _tagController;
        private Camera _mainCamera; // Cached main camera reference

        // Cache MatchSettingsManager and game mode to avoid repeated lookups
        private MatchSettingsManager _cachedMatchSettings;
        private string _cachedGameModeId;
        private bool _cachedIsTeamBased;
        private bool _cachedIsTagMode;
        private bool _gameModeCacheValid;

        // Cache last outline size to avoid GetPropertyBlock every frame
        private float _lastOutlineSize = -1f;

        // --------------------------------------------------------------------
        // Unity / Netcode lifecycle
        // --------------------------------------------------------------------
        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Get the correct SkinnedMeshRenderer from PlayerController (the body mesh, not FP arm)
            if(playerController == null)
                playerController = GetComponent<PlayerController>();
            
            if(playerController == null) {
                Debug.LogError($"[PlayerTeamManager] PlayerController not found! GameObject: {gameObject.name}");
                enabled = false;
                return;
            }

            _skinned = playerController.PlayerMesh;
            if(_skinned == null) {
                Debug.LogError($"[PlayerTeamManager] PlayerController.PlayerMesh is null! GameObject: {gameObject.name}");
                enabled = false;
                return;
            }

            // Find and cache main camera once (for dynamically spawned prefabs)
            _mainCamera = Camera.main;

            _tagController = GetComponent<PlayerTagController>();

            // Cache MatchSettingsManager
            _cachedMatchSettings = MatchSettingsManager.Instance;
            _gameModeCacheValid = false;

            // Initialize MaterialPropertyBlock for per-instance properties
            if(_skinned != null) {
                _propertyBlock = new MaterialPropertyBlock();
                _skinned.GetPropertyBlock(_propertyBlock, 0); // Get existing properties for material index 0

                // Create reusable property block for tagged players
                _tagPropertyBlock = new MaterialPropertyBlock();
            } else {
                Debug.LogError($"[PlayerTeamManager] No SkinnedMeshRenderer found! GameObject: {gameObject.name}");
            }

            // Subscribe to team changes (including late-joiners)
            // Note: netTeam is set by server during spawn, clients should not write to it
            netTeam.OnValueChanged -= OnTeamChanged;
            netTeam.OnValueChanged += OnTeamChanged;

            // Delay outline update to ensure all teams are synced
            StartCoroutine(DelayedOutlineUpdate());
        }

        private IEnumerator DelayedOutlineUpdate() {
            // Wait a frame to ensure network variables are synced
            yield return null;
            UpdateOutlineColour();

            // Also update when local player's team is set (if we're the local player)
            if(!IsOwner) yield break;
            // Update all other players' outlines when our team is set
            UpdateAllPlayerOutlines();
        }

        private void UpdateAllPlayerOutlines() {
            // Find all other players and update their outlines
            var allPlayers = FindObjectsByType<PlayerTeamManager>(FindObjectsSortMode.None);
            foreach(var player in allPlayers) {
                if(player != this) {
                    player.UpdateOutlineColour();
                }
            }
        }

        public override void OnNetworkDespawn() {
            netTeam.OnValueChanged -= OnTeamChanged;
            base.OnNetworkDespawn();
        }

        // --------------------------------------------------------------------
        // Called whenever NetTeam changes (including on spawn)
        // --------------------------------------------------------------------
        private void OnTeamChanged(SpawnPoint.Team previous, SpawnPoint.Team current) {
            UpdateOutlineColour();

            // If this is the local player's team changing, update all other players' outlines
            if(IsOwner) {
                UpdateAllPlayerOutlines();
            }
        }

        // --------------------------------------------------------------------
        // Public method to update outline - can be called by PlayerTagController
        // --------------------------------------------------------------------
        public void UpdateOutlineColour() {
            if(_skinned == null || _propertyBlock == null) {
                Debug.LogWarning($"[PlayerTeamManager] Cannot update outline - skinned: {_skinned != null}, propertyBlock: {_propertyBlock != null}, GameObject: {gameObject.name}");
                return;
            }

            // Refresh MatchSettingsManager cache if needed
            if(_cachedMatchSettings == null) {
                _cachedMatchSettings = MatchSettingsManager.Instance;
                _gameModeCacheValid = false;
            }

            if(_cachedMatchSettings == null) {
                Debug.LogWarning($"[PlayerTeamManager] MatchSettingsManager is null! GameObject: {gameObject.name}");
                return;
            }

            // Always check current game mode and invalidate cache if it changed
            var currentGameModeId = _cachedMatchSettings.selectedGameModeId;
            if(_gameModeCacheValid && _cachedGameModeId != currentGameModeId) {
                // Game mode changed - invalidate cache
                _gameModeCacheValid = false;
                Debug.Log($"[PlayerTeamManager] Game mode changed from '{_cachedGameModeId}' to '{currentGameModeId}', invalidating cache. GameObject: {gameObject.name}");
            }

            // Cache game mode checks
            if(!_gameModeCacheValid) {
                _cachedGameModeId = currentGameModeId;
                _cachedIsTeamBased = MatchSettingsManager.IsTeamBasedMode(_cachedGameModeId);
                _cachedIsTagMode = _cachedGameModeId == "Gun Tag";
                _gameModeCacheValid = true;
                Debug.Log($"[PlayerTeamManager] Cached game mode: '{_cachedGameModeId}', IsTeamBased: {_cachedIsTeamBased}, IsTagMode: {_cachedIsTagMode}, GameObject: {gameObject.name}, IsOwner: {IsOwner}");
            }

            // Gun Tag mode: prioritize tag glow
            if(_cachedIsTagMode && _tagController != null) {
                if(_tagController.isTagged.Value) {
                    // Tagged player: bright yellow-orange glow
                    // Get existing property block first to preserve other properties
                    _skinned.GetPropertyBlock(_tagPropertyBlock, 0);
                    var outlineSize = CalculateOutlineSize();
                    _tagPropertyBlock.SetColor(outlineColor, taggedGlow);
                    _tagPropertyBlock.SetFloat(size, outlineSize);
                    _skinned.SetPropertyBlock(_tagPropertyBlock, 0);
                    _lastOutlineSize = outlineSize; // Update cached size
                    Debug.Log($"[PlayerTeamManager] Set tagged glow color: {taggedGlow}, size: {outlineSize}, GameObject: {gameObject.name}");
                } else {
                    // Not tagged: default black outline (handled by shader/material)
                    // Clear the property block to use default material color
                    _skinned.SetPropertyBlock(null, 0);
                    _lastOutlineSize = -1f; // Reset cached size
                    Debug.Log($"[PlayerTeamManager] Cleared property block for non-tagged player, GameObject: {gameObject.name}");
                }

                return;
            }

            // Team-based mode: update colors
            if(_cachedIsTeamBased) {
                Color target;

                if(IsOwner) {
                    // Yourself: don't change (leave as default/unchanged)
                    Debug.Log($"[PlayerTeamManager] Skipping outline update for owner, GameObject: {gameObject.name}");
                    return;
                }

                // Get the LOCAL player's team (only exists on clients)
                GameObject localPlayer = null;
                var networkManager = NetworkManager.Singleton;
                if(networkManager != null && networkManager.LocalClient != null) {
                    var playerObject = networkManager.LocalClient.PlayerObject;
                    if(playerObject != null) {
                        localPlayer = playerObject.gameObject;
                    }
                }
                if(localPlayer != null) {
                    var localController = localPlayer.GetComponent<PlayerController>();
                    PlayerTeamManager localTeamMgr = null;
                    if(localController != null) {
                        localTeamMgr = localController.TeamManager;
                    }
                    if(localTeamMgr != null && netTeam.Value == localTeamMgr.netTeam.Value) {
                        target = teammateOutline; // Same team → blue
                        Debug.Log($"[PlayerTeamManager] Same team - using teammate color: {target}, GameObject: {gameObject.name}, MyTeam: {netTeam.Value}, LocalTeam: {localTeamMgr.netTeam.Value}");
                    } else {
                        target = enemyOutline; // Different team → red
                        var localTeamValue = localTeamMgr != null ? localTeamMgr.netTeam.Value : SpawnPoint.Team.TeamA;
                        Debug.Log($"[PlayerTeamManager] Different team - using enemy color: {target}, GameObject: {gameObject.name}, MyTeam: {netTeam.Value}, LocalTeam: {localTeamValue}");
                    }
                } else {
                    target = enemyOutline; // Fallback (shouldn't happen)
                    Debug.LogWarning($"[PlayerTeamManager] Local player not found, using enemy color as fallback, GameObject: {gameObject.name}");
                }

                // Calculate distance-based outline size for better visibility at distance
                var outlineSize = CalculateOutlineSize();

                // Check if shared material has the outline color property (don't create instance just to check)
                var sharedMaterial = _skinned.sharedMaterial;
                if(sharedMaterial == null) {
                    Debug.LogWarning($"[PlayerTeamManager] SkinnedMeshRenderer has no shared material at index 0! GameObject: {gameObject.name}");
                    return;
                }
                
                var hasOutlineColor = sharedMaterial.HasProperty(outlineColor);
                var hasSize = sharedMaterial.HasProperty(size);
                Debug.Log($"[PlayerTeamManager] Shared Material: {sharedMaterial.name}, Shader: {sharedMaterial.shader.name}, HasOutlineColor: {hasOutlineColor}, HasSize: {hasSize}, MaterialIndex: 0, GameObject: {gameObject.name}");
                
                if(!hasOutlineColor) {
                    Debug.LogWarning($"[PlayerTeamManager] Shared Material '{sharedMaterial.name}' (Shader: {sharedMaterial.shader.name}) does not have property '_OutlineColor'! GameObject: {gameObject.name}");
                    // Try fallback: set directly on material instance
                    var materialInstance = _skinned.material;
                    if(materialInstance != null && materialInstance.HasProperty(outlineColor)) {
                        materialInstance.SetColor(outlineColor, target);
                        materialInstance.SetFloat(size, outlineSize);
                        Debug.Log($"[PlayerTeamManager] Fallback: Set outline directly on material instance: {target}, size: {outlineSize}");
                        _lastOutlineSize = outlineSize;
                        return;
                    }
                    return; // Can't set outline if property doesn't exist
                }
                
                // Get existing property block first to preserve other properties
                // MaterialPropertyBlock works on shared materials - it's per-instance
                _skinned.GetPropertyBlock(_propertyBlock, 0);
                
                // Use MaterialPropertyBlock to set per-instance property without modifying shared material
                _propertyBlock.SetColor(outlineColor, target);
                _propertyBlock.SetFloat(size, outlineSize);
                _skinned.SetPropertyBlock(_propertyBlock, 0); // Apply to material index 0 (outline material)
                _lastOutlineSize = outlineSize; // Update cached size
                Debug.Log($"[PlayerTeamManager] Applied outline color via PropertyBlock: {target}, size: {outlineSize}, GameObject: {gameObject.name}");
                return;
            }

            // FFA mode: don't update outline color (leave it as default/unchanged)
            // Clear the property block to use default material color
            _skinned.SetPropertyBlock(null, 0);
            _lastOutlineSize = -1f; // Reset cached size
        }

        // --------------------------------------------------------------------
        // Calculate outline size based on distance (larger at distance for visibility)
        // --------------------------------------------------------------------
        private float CalculateOutlineSize() {
            if(_mainCamera == null) {
                // Fallback to Camera.main if cache is lost (shouldn't happen, but safety check)
                _mainCamera = Camera.main;
                if(_mainCamera == null) {
                    Debug.LogWarning($"[PlayerTeamManager] Main camera is null, using min outline size. GameObject: {gameObject.name}");
                    return minOutlineSize;
                }
            }

            // Get distance from camera to player
            var distance = Vector3.Distance(_mainCamera.transform.position, transform.position);

            // Clamp distance to scaling range
            var normalizedDistance = Mathf.InverseLerp(distanceScaleStart, distanceScaleEnd, distance);
            normalizedDistance = Mathf.Clamp01(normalizedDistance);

            // Interpolate between min and max size
            var outlineSize = Mathf.Lerp(minOutlineSize, maxOutlineSize, normalizedDistance);

            return outlineSize;
        }

        // --------------------------------------------------------------------
        // Update outline size every frame for distance-based scaling
        // --------------------------------------------------------------------
        private void Update() {
            if(_skinned == null || _propertyBlock == null) return;
            if(IsOwner) return; // Don't update for self

            // Refresh MatchSettingsManager cache if needed
            if(_cachedMatchSettings == null) {
                _cachedMatchSettings = MatchSettingsManager.Instance;
                _gameModeCacheValid = false;
            }

            if(_cachedMatchSettings == null) return;

            // Always check current game mode and invalidate cache if it changed
            var currentGameModeId = _cachedMatchSettings.selectedGameModeId;
            if(_gameModeCacheValid && _cachedGameModeId != currentGameModeId) {
                // Game mode changed - invalidate cache and update outline
                _gameModeCacheValid = false;
                Debug.Log($"[PlayerTeamManager] Game mode changed from '{_cachedGameModeId}' to '{currentGameModeId}' in Update(), forcing outline update");
                UpdateOutlineColour(); // Force update when game mode changes
                return;
            }

            // Cache game mode checks
            if(!_gameModeCacheValid) {
                _cachedGameModeId = currentGameModeId;
                _cachedIsTeamBased = MatchSettingsManager.IsTeamBasedMode(_cachedGameModeId);
                _cachedIsTagMode = _cachedGameModeId == "Gun Tag";
                _gameModeCacheValid = true;
            }

            // Only update size for team-based or tag modes (where we're using custom colors)
            if(!_cachedIsTeamBased && !_cachedIsTagMode) return;

            // For tag mode, only update if tagged
            if(_cachedIsTagMode && (_tagController == null || !_tagController.isTagged.Value)) {
                return;
            }

            // Update outline size based on distance
            var outlineSize = CalculateOutlineSize();

            // Only update if size actually changed (avoid unnecessary SetPropertyBlock calls)
            // Use cached last size instead of GetPropertyBlock every frame
            if(!(Mathf.Abs(_lastOutlineSize - outlineSize) > 0.001f)) return;
            // For tag mode, update the tag property block
            if(_cachedIsTagMode && _tagController != null && _tagController.isTagged.Value) {
                _tagPropertyBlock.SetFloat(size, outlineSize);
                _skinned.SetPropertyBlock(_tagPropertyBlock, 0);
            } else {
                // For team-based mode, update the regular property block
                _propertyBlock.SetFloat(size, outlineSize);
                _skinned.SetPropertyBlock(_propertyBlock, 0);
            }

            _lastOutlineSize = outlineSize;
        }
    }
}